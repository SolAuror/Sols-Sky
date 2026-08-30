using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;

namespace Sol.Water
{
    /// <summary>Triangulates a closed spline into a bounded lake surface for the finite-water path.</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SplineContainer), typeof(MeshFilter), typeof(MeshRenderer))]
    [RequireComponent(typeof(SolWaterBody))]
    public sealed class SolLakeGeometry : MonoBehaviour, ISolWaterGeometry
    {
        const int MaximumBoundarySamples = 4096;

        [Header("Spline Sampling")]
        [Tooltip("Closed boundary spline. Resolved from this object when empty; it needs "
            + "at least three knots and must be marked closed.")]
        [SerializeField] SplineContainer splineContainer;
        [Tooltip("Longest edge of the generated boundary, in metres. Smaller values "
            + "follow tight curves more closely at the cost of triangles.")]
        [SerializeField, Min(0.25f)] float maximumBoundarySegmentLength = 3f;

        [Header("Surface")]
        [Tooltip("Take the surface height from the spline knots, averaged. Off uses the "
            + "flat offset below, which is what a still lake usually wants.")]
        [SerializeField] bool useSplineHeight;
        [Tooltip("Surface height in local space, used when spline height is off.")]
        [SerializeField] float surfaceOffset;
        [Tooltip("Water depth in metres. Reported to buoyancy and used to size the "
            + "culling bounds; it does not generate a bed.")]
        [SerializeField, Min(0.05f)] float depth = 6f;

        [Header("Edge Foam")]
        [Tooltip("Distance inward from the boundary over which edge foam is generated, "
            + "in metres. Visual shoreline foam comes from scene-depth contact instead; "
            + "this is the value gameplay queries read.")]
        [SerializeField, Min(0f)] float edgeFoamWidth = 0.75f;
        [Tooltip("Foam amount reported right at the boundary.")]
        [SerializeField, Range(0f, 1f)] float edgeFoam = 0.3f;

        [Header("Lifecycle")]
        [Tooltip("Regenerate the mesh whenever the spline is edited. Turn off on very "
            + "large lakes and rebuild manually from the component context menu.")]
        [SerializeField] bool rebuildOnSplineChange = true;

        MeshFilter _meshFilter;
        MeshRenderer _meshRenderer;
        Mesh _mesh;
        Vector3[] _boundary = Array.Empty<Vector3>();
        bool _dirty = true;
        float _surfaceLocalY;

        public Renderer SurfaceRenderer => ResolveRenderer();
        public Bounds WorldBounds => _meshRenderer != null ? _meshRenderer.bounds
            : new Bounds(transform.position, Vector3.zero);
        public Vector3 RepresentativeFlow => Vector3.zero;
        public bool SupportsSurfaceWaves => true;
        public Mesh GeneratedMesh => _mesh;

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
            maximumBoundarySegmentLength = Mathf.Max(0.25f, maximumBoundarySegmentLength);
            depth = Mathf.Max(0.05f, depth);
            edgeFoamWidth = Mathf.Max(0f, edgeFoamWidth);
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
                || splineContainer.Splines[0].Count < 3)
            {
                message = "A lake requires a boundary spline with at least three knots.";
                return false;
            }
            if (!splineContainer.Splines[0].Closed)
            {
                message = "The lake boundary spline must be closed.";
                return false;
            }
            if (depth <= 0f)
            {
                message = "Lake depth must be positive.";
                return false;
            }
            message = string.Empty;
            return true;
        }

        [ContextMenu("Rebuild Lake Mesh")]
        public bool Rebuild()
        {
            _dirty = false;
            ResolveComponents();
            ConfigureBody();
            if (!ValidateConfiguration(out _))
            {
                ClearMesh();
                return false;
            }

            float length = Mathf.Max(0.01f, splineContainer.CalculateLength());
            int count = Mathf.Clamp(Mathf.CeilToInt(length / maximumBoundarySegmentLength),
                3, MaximumBoundarySamples);
            _boundary = new Vector3[count];
            Vector3[] vertices = new Vector3[count];
            Vector3[] normals = new Vector3[count];
            Vector4[] tangents = new Vector4[count];
            Vector2[] uv = new Vector2[count];
            Color[] colors = new Color[count];

            Bounds planarBounds = default;
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)count;
                splineContainer.Evaluate(t, out float3 worldPosition, out _, out _);
                Vector3 local = transform.InverseTransformPoint((Vector3)worldPosition);
                if (!useSplineHeight)
                    local.y = surfaceOffset;
                _boundary[i] = local;
                vertices[i] = local;
                if (i == 0)
                    planarBounds = new Bounds(local, Vector3.zero);
                else
                    planarBounds.Encapsulate(local);
            }
            _surfaceLocalY = useSplineHeight ? AverageHeight(_boundary) : surfaceOffset;

            float sizeX = Mathf.Max(0.001f, planarBounds.size.x);
            float sizeZ = Mathf.Max(0.001f, planarBounds.size.z);
            for (int i = 0; i < count; i++)
            {
                vertices[i].y = _surfaceLocalY;
                _boundary[i].y = _surfaceLocalY;
                normals[i] = Vector3.up;
                tangents[i] = new Vector4(1f, 0f, 0f, 1f);
                uv[i] = new Vector2(
                    (vertices[i].x - planarBounds.min.x) / sizeX,
                    (vertices[i].z - planarBounds.min.z) / sizeZ);
                // Scene-depth contact supplies visual edge foam. Boundary-only
                // triangles cannot interpolate an edge mask without whitening the lake.
                colors[i] = Color.clear;
            }

            if (!TryTriangulate(_boundary, out int[] triangles))
            {
                Debug.LogWarning("[SolLakeGeometry] Boundary is self-intersecting or degenerate; lake mesh was not generated.", this);
                ClearMesh();
                return false;
            }

            Mesh mesh = EnsureMesh();
            mesh.Clear();
            mesh.indexFormat = count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.tangents = tangents;
            mesh.uv = uv;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            Bounds bounds = mesh.bounds;
            bounds.Expand(new Vector3(0f, depth * 2f, 0f));
            mesh.bounds = bounds;
            _meshFilter.sharedMesh = mesh;
            return true;
        }

        public bool ContainsPoint(Vector3 localWorldPosition)
        {
            if (_boundary.Length < 3)
                return false;
            Vector3 local = transform.InverseTransformPoint(localWorldPosition);
            return PointInPolygon(new Vector2(local.x, local.z), _boundary);
        }

        public bool TrySample(Vector3 localWorldPosition, out SolWaterGeometrySample sample)
        {
            if (!ContainsPoint(localWorldPosition))
            {
                sample = default;
                return false;
            }
            Vector3 local = transform.InverseTransformPoint(localWorldPosition);
            float nearestEdge = DistanceToBoundary(new Vector2(local.x, local.z), _boundary);
            float foam = edgeFoamWidth > 0f
                ? (1f - Mathf.Clamp01(nearestEdge / edgeFoamWidth)) * edgeFoam : 0f;
            Vector3 surfaceLocal = new(local.x, _surfaceLocalY, local.z);
            sample = new SolWaterGeometrySample(transform.TransformPoint(surfaceLocal),
                transform.up, Vector3.zero, depth, foam);
            return true;
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

        static bool TryTriangulate(Vector3[] points, out int[] triangles)
        {
            int count = points.Length;
            List<int> remaining = new(count);
            bool ccw = SignedArea(points) > 0f;
            for (int i = 0; i < count; i++)
                remaining.Add(ccw ? i : count - 1 - i);
            List<int> result = new((count - 2) * 3);
            int guard = count * count;
            while (remaining.Count > 2 && guard-- > 0)
            {
                bool clipped = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    int previous = remaining[(i - 1 + remaining.Count) % remaining.Count];
                    int current = remaining[i];
                    int next = remaining[(i + 1) % remaining.Count];
                    Vector2 a = new(points[previous].x, points[previous].z);
                    Vector2 b = new(points[current].x, points[current].z);
                    Vector2 c = new(points[next].x, points[next].z);
                    if (Cross(b - a, c - b) <= 0.000001f)
                        continue;
                    bool contains = false;
                    for (int j = 0; j < remaining.Count; j++)
                    {
                        int candidate = remaining[j];
                        if (candidate == previous || candidate == current || candidate == next)
                            continue;
                        Vector2 point = new(points[candidate].x, points[candidate].z);
                        if (PointInTriangle(point, a, b, c))
                        {
                            contains = true;
                            break;
                        }
                    }
                    if (contains)
                        continue;
                    result.Add(previous);
                    result.Add(next);
                    result.Add(current);
                    remaining.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped)
                    break;
            }
            triangles = result.ToArray();
            return triangles.Length == (count - 2) * 3;
        }

        static bool PointInPolygon(Vector2 point, Vector3[] polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                Vector2 a = new(polygon[i].x, polygon[i].z);
                Vector2 b = new(polygon[j].x, polygon[j].z);
                if ((a.y > point.y) != (b.y > point.y)
                    && point.x < (b.x - a.x) * (point.y - a.y)
                    / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        static float DistanceToBoundary(Vector2 point, Vector3[] polygon)
        {
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < polygon.Length; i++)
            {
                Vector2 a = new(polygon[i].x, polygon[i].z);
                Vector2 b = new(polygon[(i + 1) % polygon.Length].x,
                    polygon[(i + 1) % polygon.Length].z);
                Vector2 segment = b - a;
                float t = segment.sqrMagnitude > 0.000001f
                    ? Mathf.Clamp01(Vector2.Dot(point - a, segment) / segment.sqrMagnitude) : 0f;
                nearest = Mathf.Min(nearest, Vector2.Distance(point, a + segment * t));
            }
            return nearest;
        }

        static float SignedArea(Vector3[] points)
        {
            float area = 0f;
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 a = points[i];
                Vector3 b = points[(i + 1) % points.Length];
                area += a.x * b.z - b.x * a.z;
            }
            return area * 0.5f;
        }

        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float ab = Cross(b - a, p - a);
            float bc = Cross(c - b, p - b);
            float ca = Cross(a - c, p - c);
            return ab >= -0.000001f && bc >= -0.000001f && ca >= -0.000001f;
        }

        static float AverageHeight(Vector3[] points)
        {
            float total = 0f;
            for (int i = 0; i < points.Length; i++)
                total += points[i].y;
            return points.Length > 0 ? total / points.Length : 0f;
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
                body.ConfigureGeneratedGeometry(SolWaterBodyType.Lake, ResolveRenderer());
        }

        Mesh EnsureMesh()
        {
            if (_mesh != null)
                return _mesh;
            _mesh = new Mesh { name = $"{name} Lake Water Mesh", hideFlags = HideFlags.DontSave };
            _mesh.MarkDynamic();
            return _mesh;
        }

        void ClearMesh()
        {
            if (_mesh != null)
                _mesh.Clear();
            _boundary = Array.Empty<Vector3>();
        }
    }
}
