using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;

namespace Sol.Water
{
    /// <summary>
    /// Builds deterministic spline-following waterfall ribbons plus an optional plunge-pool
    /// impact zone. All breakup is geometry-local, so camera and floating-origin motion cannot
    /// change ribbon orientation or bounds.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SplineContainer), typeof(MeshFilter), typeof(MeshRenderer))]
    [RequireComponent(typeof(SolWaterBody))]
    public sealed class SolWaterfallGeometry : MonoBehaviour, ISolWaterGeometry
    {
        const int MaximumSegments = 2048;
        const int MaximumRibbons = 16;

        [Header("Sheet")]
        [SerializeField] SplineContainer splineContainer;
        [SerializeField, Min(0.1f)] float maximumSegmentLength = 0.75f;
        [SerializeField, Min(0.1f)] float width = 6f;
        [SerializeField] AnimationCurve widthAlongFall = AnimationCurve.Linear(0f, 1f, 1f, 0.8f);
        [SerializeField, Range(1, MaximumRibbons)] int ribbonCount = 5;
        [SerializeField, Range(0f, 1f)] float ribbonGap = 0.08f;
        [SerializeField, Min(0f)] float ribbonBreakup = 0.12f;
        [SerializeField, Min(0.01f)] float sheetThickness = 0.2f;
        [SerializeField, Min(0.01f)] float uvMetersPerTile = 2f;

        [Header("Flow and impact")]
        [SerializeField, Min(0f)] float flowSpeed = 8f;
        [SerializeField, Range(0f, 1f)] float lipFoam = 0.2f;
        [SerializeField, Range(0f, 1f)] float impactFoam = 1f;
        [SerializeField] bool generatePlungePool = true;
        [SerializeField, Min(0.1f)] float plungePoolRadius = 4f;
        [SerializeField, Min(0.05f)] float plungePoolDepth = 2f;
        [SerializeField, Range(8, 128)] int plungePoolSegments = 32;
        [SerializeField, Range(0.05f, 1f)] float impactZoneRadius = 0.35f;
        [SerializeField] bool rebuildOnSplineChange = true;

        MeshFilter _meshFilter;
        MeshRenderer _meshRenderer;
        Mesh _mesh;
        Vector3[] _centers = Array.Empty<Vector3>();
        Vector3[] _tangents = Array.Empty<Vector3>();
        Vector3[] _rights = Array.Empty<Vector3>();
        Vector3[] _normals = Array.Empty<Vector3>();
        float[] _widths = Array.Empty<float>();
        float[] _distances = Array.Empty<float>();
        Vector3 _representativeFlow;
        bool _dirty = true;

        public Renderer SurfaceRenderer => ResolveRenderer();
        public Bounds WorldBounds => _meshRenderer != null ? _meshRenderer.bounds
            : new Bounds(transform.position, Vector3.zero);
        public Vector3 RepresentativeFlow => _representativeFlow;
        public bool SupportsSurfaceWaves => false;
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

        void OnDisable() => Spline.Changed -= OnSplineChanged;

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
            maximumSegmentLength = Mathf.Max(0.1f, maximumSegmentLength);
            width = Mathf.Max(0.1f, width);
            ribbonCount = Mathf.Clamp(ribbonCount, 1, MaximumRibbons);
            ribbonBreakup = Mathf.Max(0f, ribbonBreakup);
            sheetThickness = Mathf.Max(0.01f, sheetThickness);
            uvMetersPerTile = Mathf.Max(0.01f, uvMetersPerTile);
            flowSpeed = Mathf.Max(0f, flowSpeed);
            plungePoolRadius = Mathf.Max(0.1f, plungePoolRadius);
            plungePoolDepth = Mathf.Max(0.05f, plungePoolDepth);
            plungePoolSegments = Mathf.Clamp(plungePoolSegments, 8, 128);
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
                message = "A waterfall requires a spline with at least two knots.";
                return false;
            }
            if (width <= 0f || sheetThickness <= 0f)
            {
                message = "Waterfall width and sheet thickness must be positive.";
                return false;
            }
            message = string.Empty;
            return true;
        }

        [ContextMenu("Rebuild Waterfall Mesh")]
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
            int segmentCount = Mathf.Clamp(Mathf.CeilToInt(length / maximumSegmentLength),
                1, MaximumSegments);
            int sampleCount = segmentCount + 1;
            EnsureSampleCache(sampleCount);
            Vector3 previous = Vector3.zero;
            float distance = 0f;
            Vector3 tangentAccumulator = Vector3.zero;
            for (int i = 0; i < sampleCount; i++)
            {
                float t = i / (float)segmentCount;
                splineContainer.Evaluate(t, out float3 splinePosition,
                    out float3 splineTangent, out float3 splineUp);
                Vector3 centerWorld = splinePosition;
                Vector3 tangentWorld = ((Vector3)splineTangent).normalized;
                if (tangentWorld.sqrMagnitude < 0.0001f)
                    tangentWorld = i > 0 ? transform.TransformDirection(_tangents[i - 1]) : -transform.up;
                Vector3 upWorld = ((Vector3)splineUp).normalized;
                Vector3 rightWorld = Vector3.Cross(upWorld, tangentWorld).normalized;
                if (rightWorld.sqrMagnitude < 0.0001f)
                    rightWorld = Vector3.Cross(Vector3.forward, tangentWorld).normalized;
                if (rightWorld.sqrMagnitude < 0.0001f)
                    rightWorld = transform.right;
                Vector3 normalWorld = Vector3.Cross(tangentWorld, rightWorld).normalized;
                if (i > 0 && Vector3.Dot(normalWorld,
                    transform.TransformDirection(_normals[i - 1])) < 0f)
                {
                    rightWorld = -rightWorld;
                    normalWorld = -normalWorld;
                }
                if (i > 0)
                    distance += Vector3.Distance(previous, centerWorld);
                previous = centerWorld;
                _centers[i] = transform.InverseTransformPoint(centerWorld);
                _tangents[i] = transform.InverseTransformDirection(tangentWorld).normalized;
                _rights[i] = transform.InverseTransformDirection(rightWorld).normalized;
                _normals[i] = transform.InverseTransformDirection(normalWorld).normalized;
                _widths[i] = width * Mathf.Max(0.01f, EvaluateCurve(widthAlongFall, t));
                _distances[i] = distance;
                tangentAccumulator += tangentWorld;
            }

            List<Vector3> vertices = new(sampleCount * ribbonCount * 2 + plungePoolSegments + 1);
            List<Vector3> normals = new(vertices.Capacity);
            List<Vector4> tangents = new(vertices.Capacity);
            List<Vector2> uv = new(vertices.Capacity);
            List<Color> colors = new(vertices.Capacity);
            List<int> triangles = new(segmentCount * ribbonCount * 6 + plungePoolSegments * 3);

            for (int ribbon = 0; ribbon < ribbonCount; ribbon++)
            {
                int baseVertex = vertices.Count;
                float ribbonMin = -0.5f + ribbon / (float)ribbonCount;
                float ribbonMax = -0.5f + (ribbon + 1f) / ribbonCount;
                float inset = ribbonGap / ribbonCount * 0.5f;
                for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
                {
                    float t = sampleIndex / (float)segmentCount;
                    Vector3 center = _centers[sampleIndex];
                    Vector3 right = _rights[sampleIndex];
                    Vector3 normal = _normals[sampleIndex];
                    Vector3 tangent = _tangents[sampleIndex];
                    float sampleWidth = _widths[sampleIndex];
                    float breakup = RibbonNoise(ribbon, t) * ribbonBreakup;
                    center += normal * breakup;
                    float leftOffset = (ribbonMin + inset) * sampleWidth;
                    float rightOffset = (ribbonMax - inset) * sampleWidth;
                    float foam = Mathf.Clamp01(Mathf.Lerp(lipFoam, impactFoam,
                        Mathf.SmoothStep(0.55f, 1f, t)) + Mathf.Abs(breakup) * 0.5f);
                    AddVertex(center + right * leftOffset, normal, tangent,
                        new Vector2(0f, _distances[sampleIndex] / uvMetersPerTile), foam,
                        vertices, normals, tangents, uv, colors);
                    AddVertex(center + right * rightOffset, normal, tangent,
                        new Vector2(1f, _distances[sampleIndex] / uvMetersPerTile), foam,
                        vertices, normals, tangents, uv, colors);
                }
                for (int segment = 0; segment < segmentCount; segment++)
                {
                    int a = baseVertex + segment * 2;
                    int b = a + 2;
                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(b + 1);
                    triangles.Add(a);
                    triangles.Add(b + 1);
                    triangles.Add(a + 1);
                }
            }

            if (generatePlungePool)
                AppendPlungePool(vertices, normals, tangents, uv, colors, triangles);

            Mesh mesh = EnsureMesh();
            mesh.Clear();
            mesh.indexFormat = vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTangents(tangents);
            mesh.SetUVs(0, uv);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0, true);
            mesh.RecalculateBounds();
            Bounds expanded = mesh.bounds;
            expanded.Expand(sheetThickness * 2f);
            mesh.bounds = expanded;
            _meshFilter.sharedMesh = mesh;
            _representativeFlow = tangentAccumulator.sqrMagnitude > 0.0001f
                ? tangentAccumulator.normalized * flowSpeed : Vector3.zero;
            return true;
        }

        public bool ContainsPoint(Vector3 localWorldPosition)
        {
            Vector3 point = transform.InverseTransformPoint(localWorldPosition);
            if (generatePlungePool && _centers.Length > 0)
            {
                Vector3 impact = _centers[^1];
                if (new Vector2(point.x - impact.x, point.z - impact.z).sqrMagnitude
                    <= plungePoolRadius * plungePoolRadius)
                    return true;
            }
            return TryFindNearestSheet(point, out _, out float across, out float normalDistance,
                out float sampleWidth, out _, out _, out _)
                && Mathf.Abs(across) <= sampleWidth * 0.5f
                && Mathf.Abs(normalDistance) <= sheetThickness;
        }

        public bool TrySample(Vector3 localWorldPosition, out SolWaterGeometrySample sample)
        {
            Vector3 point = transform.InverseTransformPoint(localWorldPosition);
            if (generatePlungePool && _centers.Length > 0)
            {
                Vector3 impact = _centers[^1];
                Vector2 radial = new(point.x - impact.x, point.z - impact.z);
                if (radial.sqrMagnitude <= plungePoolRadius * plungePoolRadius)
                {
                    float radius01 = Mathf.Clamp01(radial.magnitude / plungePoolRadius);
                    sample = new SolWaterGeometrySample(
                        transform.TransformPoint(new Vector3(point.x, impact.y, point.z)),
                        transform.up, Vector3.zero, plungePoolDepth,
                        (1f - Mathf.SmoothStep(impactZoneRadius, 1f, radius01)) * impactFoam);
                    return true;
                }
            }

            if (!TryFindNearestSheet(point, out Vector3 center, out float across,
                out float normalDistance, out float sampleWidth, out Vector3 tangent,
                out Vector3 right, out Vector3 normal)
                || Mathf.Abs(across) > sampleWidth * 0.5f
                || Mathf.Abs(normalDistance) > sheetThickness)
            {
                sample = default;
                return false;
            }
            Vector3 surface = center + right * across;
            float downwardProgress = Mathf.InverseLerp(_centers[0].y, _centers[^1].y, center.y);
            sample = new SolWaterGeometrySample(transform.TransformPoint(surface),
                transform.TransformDirection(normal),
                transform.TransformDirection(tangent).normalized * flowSpeed,
                sheetThickness,
                Mathf.Lerp(lipFoam, impactFoam, Mathf.SmoothStep(0.55f, 1f, downwardProgress)));
            return true;
        }

        bool TryFindNearestSheet(Vector3 localPoint, out Vector3 center, out float across,
            out float normalDistance, out float sampleWidth, out Vector3 tangent,
            out Vector3 right, out Vector3 normal)
        {
            center = default;
            across = 0f;
            normalDistance = 0f;
            sampleWidth = 0f;
            tangent = Vector3.down;
            right = Vector3.right;
            normal = Vector3.forward;
            if (_centers.Length < 2)
                return false;
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < _centers.Length - 1; i++)
            {
                Vector3 segment = _centers[i + 1] - _centers[i];
                float u = segment.sqrMagnitude > 0.000001f
                    ? Mathf.Clamp01(Vector3.Dot(localPoint - _centers[i], segment) / segment.sqrMagnitude) : 0f;
                Vector3 candidate = _centers[i] + segment * u;
                float distanceSq = (localPoint - candidate).sqrMagnitude;
                if (distanceSq >= nearest)
                    continue;
                nearest = distanceSq;
                center = candidate;
                tangent = Vector3.Slerp(_tangents[i], _tangents[i + 1], u).normalized;
                right = Vector3.Slerp(_rights[i], _rights[i + 1], u).normalized;
                normal = Vector3.Slerp(_normals[i], _normals[i + 1], u).normalized;
                sampleWidth = Mathf.Lerp(_widths[i], _widths[i + 1], u);
                Vector3 delta = localPoint - center;
                across = Vector3.Dot(delta, right);
                normalDistance = Vector3.Dot(delta, normal);
            }
            return !float.IsPositiveInfinity(nearest);
        }

        void AppendPlungePool(List<Vector3> vertices, List<Vector3> normals,
            List<Vector4> tangents, List<Vector2> uv, List<Color> colors, List<int> triangles)
        {
            Vector3 impact = _centers[^1];
            int centerIndex = vertices.Count;
            AddVertex(impact, Vector3.up, Vector3.right, new Vector2(0.5f, 0.5f),
                impactFoam, vertices, normals, tangents, uv, colors);
            for (int i = 0; i < plungePoolSegments; i++)
            {
                float angle = i / (float)plungePoolSegments * Mathf.PI * 2f;
                Vector3 radial = new(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                AddVertex(impact + radial * plungePoolRadius, Vector3.up, Vector3.right,
                    new Vector2(radial.x, radial.z) * 0.5f + Vector2.one * 0.5f,
                    0f, vertices, normals, tangents, uv, colors);
            }
            for (int i = 0; i < plungePoolSegments; i++)
            {
                triangles.Add(centerIndex);
                triangles.Add(centerIndex + 1 + i);
                triangles.Add(centerIndex + 1 + (i + 1) % plungePoolSegments);
            }
        }

        static void AddVertex(Vector3 position, Vector3 normal, Vector3 tangent, Vector2 textureUv,
            float foam, List<Vector3> vertices, List<Vector3> normals,
            List<Vector4> tangents, List<Vector2> uv, List<Color> colors)
        {
            vertices.Add(position);
            normals.Add(normal.normalized);
            tangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, 1f));
            uv.Add(textureUv);
            colors.Add(new Color(Mathf.Clamp01(foam), 0f, 0f, 1f));
        }

        static float RibbonNoise(int ribbon, float t)
        {
            float phase = ribbon * 2.417f;
            return Mathf.Sin(t * 17.3f + phase) * 0.55f
                + Mathf.Sin(t * 43.7f + phase * 1.91f) * 0.3f
                + Mathf.Sin(t * 91.1f + phase * 0.73f) * 0.15f;
        }

        static float EvaluateCurve(AnimationCurve curve, float t)
            => curve == null || curve.length == 0 ? 1f : curve.Evaluate(t);

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
                body.ConfigureGeneratedGeometry(SolWaterBodyType.Waterfall, ResolveRenderer());
        }

        Mesh EnsureMesh()
        {
            if (_mesh != null)
                return _mesh;
            _mesh = new Mesh { name = $"{name} Waterfall Mesh", hideFlags = HideFlags.DontSave };
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
            _distances = Array.Empty<float>();
            _representativeFlow = Vector3.zero;
        }
    }
}
