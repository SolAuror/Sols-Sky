using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;

namespace Sol.Water
{
    /// <summary>
    /// Bounded, spline-authored river surface. The generated mesh is consumed by Water 2's
    /// finite draw path; cached centerline data supplies matching flow and query results.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SplineContainer), typeof(MeshFilter), typeof(MeshRenderer))]
    [RequireComponent(typeof(SolWaterBody))]
    public sealed class SolRiverGeometry : MonoBehaviour, ISolWaterGeometry
    {
        const int MaximumLongitudinalSegments = 4096;
        const int MaximumLateralSegments = 16;

        [Header("Spline Sampling")]
        [Tooltip("Centreline spline. Resolved from this object when empty; it needs at "
            + "least two knots.")]
        [SerializeField] SplineContainer splineContainer;
        [Tooltip("Longest step along the centreline, in metres. Smaller values follow "
            + "tight bends more closely at the cost of triangles.")]
        [SerializeField, Min(0.25f)] float maximumSegmentLength = 2f;
        [Tooltip("Quads across the channel. 1 is a flat ribbon; raise it only if the "
            + "banks need to curve across the width.")]
        [SerializeField, Range(1, MaximumLateralSegments)] int lateralSegments = 2;

        [Header("Channel")]
        [Tooltip("Channel width in metres at the widest point.")]
        [SerializeField, Min(0.1f)] float width = 8f;
        [Tooltip("Water depth in metres. Reported to buoyancy and used to size the "
            + "culling bounds; it does not generate a bed.")]
        [SerializeField, Min(0.05f)] float depth = 2f;
        [Tooltip("Width multiplier along the river, 0 at the source to 1 at the mouth.")]
        [SerializeField] AnimationCurve widthAlongRiver = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        [Tooltip("Depth multiplier along the river, 0 at the source to 1 at the mouth.")]
        [SerializeField] AnimationCurve depthAlongRiver = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        [Tooltip("Width of the soft margin at each bank, in metres, where the surface "
            + "fades and drops toward the ground.")]
        [SerializeField, Min(0f)] float bankBlendWidth = 0.5f;
        [Tooltip("How far the bank edge is pulled down, in metres. Sinking the rim "
            + "slightly is what hides the seam against uneven ground.")]
        [SerializeField, Min(0f)] float bankDrop = 0.15f;
        [Tooltip("Metres of river covered by one UV tile. Sets the scale of every "
            + "surface texture on this body.")]
        [SerializeField, Min(0.01f)] float uvMetersPerTile = 4f;

        [Header("Flow")]
        [Tooltip("Current speed in metres per second, along the spline. Drives foam "
            + "advection, wave direction and buoyancy drift.")]
        [SerializeField, Min(0f)] float flowSpeed = 2f;
        [Tooltip("Foam generated along the banks, where the water drags against land.")]
        [SerializeField, Range(0f, 1f)] float bankFoam = 0.35f;

        [Header("Terrain Following")]
        [Tooltip("Raycast the surface down onto terrain so the river bed follows the "
            + "ground rather than the spline alone. Costs one raycast per sample on "
            + "every rebuild.")]
        [SerializeField] bool followTerrain;
        [Tooltip("Layers treated as ground by the terrain raycast.")]
        [SerializeField] LayerMask terrainLayers = ~0;
        [Tooltip("Height above the spline the terrain ray starts from, in metres. It "
            + "must clear the tallest ground the river crosses.")]
        [SerializeField, Min(0.1f)] float terrainRayHeight = 100f;
        [Tooltip("Metres the surface is lifted above the terrain hit, to stop the two "
            + "z-fighting.")]
        [SerializeField, Min(0f)] float terrainClearance = 0.025f;

        [Header("Lifecycle")]
        [Tooltip("Regenerate the mesh whenever the spline is edited. Turn off on very "
            + "long rivers and rebuild manually from the component context menu.")]
        [SerializeField] bool rebuildOnSplineChange = true;

        MeshFilter _meshFilter;
        MeshRenderer _meshRenderer;
        Mesh _mesh;
        bool _dirty = true;
        Vector3[] _centers = Array.Empty<Vector3>();
        Vector3[] _tangents = Array.Empty<Vector3>();
        Vector3[] _rights = Array.Empty<Vector3>();
        Vector3[] _normals = Array.Empty<Vector3>();
        float[] _widths = Array.Empty<float>();
        float[] _depths = Array.Empty<float>();
        float[] _distances = Array.Empty<float>();
        Vector3 _representativeFlow;

        public Renderer SurfaceRenderer => ResolveRenderer();
        public Bounds WorldBounds => _meshRenderer != null ? _meshRenderer.bounds
            : new Bounds(transform.position, Vector3.zero);
        public Vector3 RepresentativeFlow => _representativeFlow;
        public bool SupportsSurfaceWaves => true;
        public Mesh GeneratedMesh => _mesh;
        public int SampleCount => _centers.Length;

        void Reset()
        {
            ResolveComponents();
            ConfigureBody();
            Rebuild();
        }

        void OnEnable()
        {
            ResolveComponents();
            Spline.Changed += OnSplineChanged;
            ConfigureBody();
            _dirty = true;
        }

        void OnDisable()
        {
            Spline.Changed -= OnSplineChanged;
        }

        void OnDestroy()
        {
            if (_mesh == null)
                return;
            if (Application.isPlaying)
                Destroy(_mesh);
            else
                DestroyImmediate(_mesh);
        }

        void OnValidate()
        {
            maximumSegmentLength = Mathf.Max(0.25f, maximumSegmentLength);
            lateralSegments = Mathf.Clamp(lateralSegments, 1, MaximumLateralSegments);
            width = Mathf.Max(0.1f, width);
            depth = Mathf.Max(0.05f, depth);
            bankBlendWidth = Mathf.Max(0f, bankBlendWidth);
            bankDrop = Mathf.Max(0f, bankDrop);
            uvMetersPerTile = Mathf.Max(0.01f, uvMetersPerTile);
            flowSpeed = Mathf.Max(0f, flowSpeed);
            terrainRayHeight = Mathf.Max(0.1f, terrainRayHeight);
            terrainClearance = Mathf.Max(0f, terrainClearance);
            ResolveComponents();
            ConfigureBody();
            _dirty = true;
        }

        void LateUpdate()
        {
            if (_dirty)
                Rebuild();
        }

        public bool ValidateConfiguration(out string message)
        {
            ResolveComponents();
            if (splineContainer == null || splineContainer.Splines.Count == 0
                || splineContainer.Splines[0].Count < 2)
            {
                message = "A river requires a spline with at least two knots.";
                return false;
            }
            if (width <= 0f || depth <= 0f)
            {
                message = "River width and depth must be positive.";
                return false;
            }
            message = string.Empty;
            return true;
        }

        [ContextMenu("Rebuild River Mesh")]
        public bool Rebuild()
        {
            _dirty = false;
            ResolveComponents();
            ConfigureBody();
            if (!ValidateConfiguration(out _))
            {
                ClearMeshAndCache();
                return false;
            }

            float length = Mathf.Max(0.01f, splineContainer.CalculateLength());
            int longitudinalSegments = Mathf.Clamp(
                Mathf.CeilToInt(length / maximumSegmentLength), 1, MaximumLongitudinalSegments);
            int ringCount = longitudinalSegments + 1;
            int crossCount = lateralSegments + 3;
            int vertexCount = ringCount * crossCount;
            int indexCount = longitudinalSegments * (crossCount - 1) * 6;

            Vector3[] vertices = new Vector3[vertexCount];
            Vector3[] normals = new Vector3[vertexCount];
            Vector4[] tangents = new Vector4[vertexCount];
            Vector2[] uv = new Vector2[vertexCount];
            Color[] colors = new Color[vertexCount];
            int[] indices = new int[indexCount];
            EnsureSampleCache(ringCount);

            Vector3 previousCenter = Vector3.zero;
            float distance = 0f;
            Vector3 flowAccumulator = Vector3.zero;
            for (int ring = 0; ring < ringCount; ring++)
            {
                float t = ring / (float)longitudinalSegments;
                splineContainer.Evaluate(t, out float3 splinePosition,
                    out float3 splineTangent, out float3 splineUp);
                Vector3 centerWorld = FollowTerrain(splinePosition);
                Vector3 tangentWorld = ((Vector3)splineTangent).normalized;
                if (tangentWorld.sqrMagnitude < 0.0001f)
                    tangentWorld = ring > 0 ? _tangents[ring - 1] : transform.forward;
                Vector3 upWorld = ((Vector3)splineUp).normalized;
                if (upWorld.sqrMagnitude < 0.0001f)
                    upWorld = Vector3.up;
                Vector3 rightWorld = Vector3.Cross(upWorld, tangentWorld).normalized;
                if (rightWorld.sqrMagnitude < 0.0001f)
                    rightWorld = Vector3.Cross(Vector3.up, tangentWorld).normalized;
                if (rightWorld.sqrMagnitude < 0.0001f)
                    rightWorld = transform.right;
                Vector3 normalWorld = Vector3.Cross(tangentWorld, rightWorld).normalized;
                if (normalWorld.y < 0f)
                    normalWorld = -normalWorld;

                float sampleWidth = width * Mathf.Max(0.01f, EvaluateCurve(widthAlongRiver, t));
                float sampleDepth = depth * Mathf.Max(0.01f, EvaluateCurve(depthAlongRiver, t));
                if (ring > 0)
                    distance += Vector3.Distance(previousCenter, centerWorld);
                previousCenter = centerWorld;
                _centers[ring] = transform.InverseTransformPoint(centerWorld);
                _tangents[ring] = transform.InverseTransformDirection(tangentWorld).normalized;
                _rights[ring] = transform.InverseTransformDirection(rightWorld).normalized;
                _normals[ring] = transform.InverseTransformDirection(normalWorld).normalized;
                _widths[ring] = sampleWidth;
                _depths[ring] = sampleDepth;
                _distances[ring] = distance;
                flowAccumulator += tangentWorld;

                for (int cross = 0; cross < crossCount; cross++)
                {
                    bool leftSkirt = cross == 0;
                    bool rightSkirt = cross == crossCount - 1;
                    float across01 = Mathf.Clamp01((cross - 1f) / lateralSegments);
                    float crossOffset = Mathf.Lerp(-sampleWidth * 0.5f, sampleWidth * 0.5f, across01);
                    if (leftSkirt)
                        crossOffset -= bankBlendWidth;
                    else if (rightSkirt)
                        crossOffset += bankBlendWidth;
                    Vector3 vertexWorld = centerWorld + rightWorld * crossOffset;
                    vertexWorld = FollowTerrain(vertexWorld);
                    if (leftSkirt || rightSkirt)
                        vertexWorld -= normalWorld * bankDrop;

                    int index = ring * crossCount + cross;
                    vertices[index] = transform.InverseTransformPoint(vertexWorld);
                    normals[index] = transform.InverseTransformDirection(normalWorld).normalized;
                    Vector3 tangentLocal = transform.InverseTransformDirection(tangentWorld).normalized;
                    tangents[index] = new Vector4(tangentLocal.x, tangentLocal.y, tangentLocal.z, 1f);
                    uv[index] = new Vector2(across01, distance / uvMetersPerTile);
                    float edge = 1f - Mathf.Clamp01(Mathf.Min(across01, 1f - across01) * 8f);
                    colors[index] = new Color(edge * bankFoam, 0f, 0f, 1f);
                }
            }

            int write = 0;
            for (int ring = 0; ring < longitudinalSegments; ring++)
            {
                int row = ring * crossCount;
                int nextRow = row + crossCount;
                for (int cross = 0; cross < crossCount - 1; cross++)
                {
                    int a = row + cross;
                    int b = nextRow + cross;
                    int c = nextRow + cross + 1;
                    int d = row + cross + 1;
                    indices[write++] = a;
                    indices[write++] = b;
                    indices[write++] = c;
                    indices[write++] = a;
                    indices[write++] = c;
                    indices[write++] = d;
                }
            }

            Mesh mesh = EnsureMesh();
            mesh.Clear();
            mesh.indexFormat = vertexCount > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.tangents = tangents;
            mesh.uv = uv;
            mesh.colors = colors;
            mesh.triangles = indices;
            mesh.RecalculateBounds();
            _meshFilter.sharedMesh = mesh;
            _representativeFlow = flowAccumulator.sqrMagnitude > 0.0001f
                ? flowAccumulator.normalized * flowSpeed : Vector3.zero;
            return true;
        }

        public bool ContainsPoint(Vector3 localWorldPosition)
        {
            return TryFindNearest(localWorldPosition, out _, out float crossOffset,
                out float sampleWidth, out _, out _, out _)
                && Mathf.Abs(crossOffset) <= sampleWidth * 0.5f + bankBlendWidth;
        }

        public bool TrySample(Vector3 localWorldPosition, out SolWaterGeometrySample sample)
        {
            if (!TryFindNearest(localWorldPosition, out Vector3 center, out float crossOffset,
                out float sampleWidth, out float sampleDepth, out Vector3 tangent, out Vector3 normal)
                || Mathf.Abs(crossOffset) > sampleWidth * 0.5f + bankBlendWidth)
            {
                sample = default;
                return false;
            }
            Vector3 right = Vector3.Cross(normal, tangent).normalized;
            Vector3 surfaceLocal = center + right * Mathf.Clamp(crossOffset,
                -sampleWidth * 0.5f, sampleWidth * 0.5f);
            float edge = Mathf.InverseLerp(sampleWidth * 0.25f, sampleWidth * 0.5f,
                Mathf.Abs(crossOffset));
            sample = new SolWaterGeometrySample(
                transform.TransformPoint(surfaceLocal),
                transform.TransformDirection(normal),
                transform.TransformDirection(tangent).normalized * flowSpeed,
                sampleDepth,
                edge * bankFoam);
            return true;
        }

        bool TryFindNearest(Vector3 worldPosition, out Vector3 center, out float crossOffset,
            out float sampleWidth, out float sampleDepth, out Vector3 tangent, out Vector3 normal)
        {
            center = default;
            crossOffset = 0f;
            sampleWidth = 0f;
            sampleDepth = 0f;
            tangent = Vector3.forward;
            normal = Vector3.up;
            if (_centers.Length < 2)
                return false;
            Vector3 point = transform.InverseTransformPoint(worldPosition);
            Vector2 pointXZ = new(point.x, point.z);
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < _centers.Length - 1; i++)
            {
                Vector2 a = new(_centers[i].x, _centers[i].z);
                Vector2 b = new(_centers[i + 1].x, _centers[i + 1].z);
                Vector2 segment = b - a;
                float segmentLengthSq = segment.sqrMagnitude;
                float u = segmentLengthSq > 0.000001f
                    ? Mathf.Clamp01(Vector2.Dot(pointXZ - a, segment) / segmentLengthSq) : 0f;
                Vector2 closestXZ = a + segment * u;
                float distanceSq = (pointXZ - closestXZ).sqrMagnitude;
                if (distanceSq >= bestDistance)
                    continue;
                bestDistance = distanceSq;
                center = Vector3.Lerp(_centers[i], _centers[i + 1], u);
                tangent = Vector3.Slerp(_tangents[i], _tangents[i + 1], u).normalized;
                normal = Vector3.Slerp(_normals[i], _normals[i + 1], u).normalized;
                Vector3 right = Vector3.Slerp(_rights[i], _rights[i + 1], u).normalized;
                crossOffset = Vector3.Dot(point - center, right);
                sampleWidth = Mathf.Lerp(_widths[i], _widths[i + 1], u);
                sampleDepth = Mathf.Lerp(_depths[i], _depths[i + 1], u);
            }
            return !float.IsPositiveInfinity(bestDistance);
        }

        Vector3 FollowTerrain(Vector3 worldPoint)
        {
            if (!followTerrain)
                return worldPoint;
            Vector3 origin = worldPoint + Vector3.up * terrainRayHeight;
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit,
                terrainRayHeight * 2f, terrainLayers, QueryTriggerInteraction.Ignore))
                worldPoint.y = hit.point.y + terrainClearance;
            return worldPoint;
        }

        void OnSplineChanged(Spline spline, int knotIndex, SplineModification modification)
        {
            if (!rebuildOnSplineChange || splineContainer == null)
                return;
            for (int i = 0; i < splineContainer.Splines.Count; i++)
            {
                if (ReferenceEquals(splineContainer.Splines[i], spline))
                {
                    _dirty = true;
                    return;
                }
            }
        }

        static float EvaluateCurve(AnimationCurve curve, float t)
            => curve == null || curve.length == 0 ? 1f : curve.Evaluate(t);

        void ResolveComponents()
        {
            if (splineContainer == null)
                splineContainer = GetComponent<SplineContainer>();
            if (_meshFilter == null)
                _meshFilter = GetComponent<MeshFilter>();
            if (_meshRenderer == null)
                _meshRenderer = GetComponent<MeshRenderer>();
        }

        Renderer ResolveRenderer()
        {
            ResolveComponents();
            return _meshRenderer;
        }

        void ConfigureBody()
        {
            SolWaterBody body = GetComponent<SolWaterBody>();
            if (body != null)
                body.ConfigureGeneratedGeometry(SolWaterBodyType.River, ResolveRenderer());
        }

        Mesh EnsureMesh()
        {
            if (_mesh != null)
                return _mesh;
            _mesh = new Mesh { name = $"{name} River Water Mesh", hideFlags = HideFlags.DontSave };
            _mesh.MarkDynamic();
            return _mesh;
        }

        void EnsureSampleCache(int count)
        {
            if (_centers.Length == count)
                return;
            _centers = new Vector3[count];
            _tangents = new Vector3[count];
            _rights = new Vector3[count];
            _normals = new Vector3[count];
            _widths = new float[count];
            _depths = new float[count];
            _distances = new float[count];
        }

        void ClearMeshAndCache()
        {
            if (_mesh != null)
                _mesh.Clear();
            _centers = Array.Empty<Vector3>();
            _tangents = Array.Empty<Vector3>();
            _rights = Array.Empty<Vector3>();
            _normals = Array.Empty<Vector3>();
            _widths = Array.Empty<float>();
            _depths = Array.Empty<float>();
            _distances = Array.Empty<float>();
            _representativeFlow = Vector3.zero;
        }
    }
}
