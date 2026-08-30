using System;
using Sol.Water;
using UnityEngine;

namespace Sol.Hydrology
{
    /// <summary>
    /// A finite water surface whose extent follows a hydrology node's level over the
    /// terrain beneath it. Where <see cref="SolLakeGeometry"/> takes an authored spline
    /// boundary, this derives the boundary from what the water level actually covers, so
    /// a reservoir filling or a river bursting its banks changes shape rather than just
    /// moving up and down.
    ///
    /// Implements <see cref="ISolWaterGeometry"/>, so flood water shares the same
    /// material, optics, SSR, foam, interaction and query contracts as every other body
    /// rather than being a special case.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [RequireComponent(typeof(SolWaterBody))]
    public sealed class SolFloodGeometry : MonoBehaviour, ISolWaterGeometry
    {
        [Header("Source")]
        [SerializeField] SolHydrologyWorld hydrologyWorld;
        [Tooltip("Hydrology node whose level drives this flood extent.")]
        [SerializeField] string nodeId;
        [Tooltip("Used when no hydrology world is present, so the component is still "
            + "authorable and testable standalone.")]
        [SerializeField] float fallbackLevel;

        [Header("Sampling")]
        [Tooltip("Horizontal extent sampled for terrain, centred on this transform.")]
        [SerializeField] Vector2 extent = new(64f, 64f);
        [Tooltip("Grid spacing in metres. Smaller resolves a tighter shoreline at the "
            + "cost of vertices; the depth fade hides the remaining stair-stepping.")]
        [SerializeField, Range(0.25f, 16f)] float cellSize = 2f;
        [SerializeField] LayerMask terrainMask = ~0;
        [Tooltip("Height above the sampled extent that terrain raycasts start from.")]
        [SerializeField, Min(1f)] float raycastHeight = 500f;

        [Header("Surface")]
        [Tooltip("Level change required before the mesh is rebuilt. Without hysteresis a "
            + "continuously simulating node rebuilds every frame.")]
        [SerializeField, Min(0.001f)] float rebuildThreshold = 0.05f;
        [Tooltip("Depth over which the flooded edge fades in, keeping the shoreline off "
            + "the sample grid.")]
        [SerializeField, Min(0f)] float edgeFadeDepth = 0.5f;
        [SerializeField, Range(0f, 1f)] float edgeFoam = 0.35f;

        MeshFilter _meshFilter;
        MeshRenderer _meshRenderer;
        Mesh _mesh;
        float[] _terrainHeights = Array.Empty<float>();
        int _columns;
        int _rows;
        float _builtLevel = float.NaN;
        bool _terrainSampled;
        int _floodedCellCount;

        public Renderer SurfaceRenderer => ResolveRenderer();
        public Bounds WorldBounds => _meshRenderer != null
            ? _meshRenderer.bounds
            : new Bounds(transform.position, new Vector3(extent.x, 1f, extent.y));
        public Vector3 RepresentativeFlow => Vector3.zero;
        public bool SupportsSurfaceWaves => true;
        public Mesh GeneratedMesh => _mesh;

        /// <summary>Surface area currently under water, in square metres.</summary>
        public double FloodedArea => SolFloodExtentMath.FloodedArea(_floodedCellCount, cellSize);

        /// <summary>Water level this mesh was last built at.</summary>
        public float BuiltLevel => _builtLevel;

        public float CurrentLevel
        {
            get
            {
                if (hydrologyWorld != null && !string.IsNullOrEmpty(nodeId)
                    && hydrologyWorld.TryGetNodeState(nodeId, out SolHydrologyNodeState state))
                    return (float)state.Level;
                return fallbackLevel;
            }
        }

        void Reset()
        {
            ResolveComponents();
            hydrologyWorld = FindAnyObjectByType<SolHydrologyWorld>();
            Rebuild();
        }

        void OnEnable()
        {
            ResolveComponents();
            ConfigureBody();
            Rebuild();
        }

        void OnDisable() => ReleaseMesh();

        void OnValidate()
        {
            extent = new Vector2(Mathf.Max(cellSize, extent.x), Mathf.Max(cellSize, extent.y));
            cellSize = Mathf.Clamp(cellSize, 0.25f, 16f);
            rebuildThreshold = Mathf.Max(0.001f, rebuildThreshold);
            edgeFadeDepth = Mathf.Max(0f, edgeFadeDepth);
            // Terrain sampling is invalidated by any extent change.
            _terrainSampled = false;
            _builtLevel = float.NaN;
        }

        void LateUpdate()
        {
            float level = CurrentLevel;
            if (SolFloodExtentMath.ShouldRebuild(_builtLevel, level, rebuildThreshold))
                Rebuild();
        }

        void ResolveComponents()
        {
            _meshFilter = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();
        }

        Renderer ResolveRenderer()
        {
            if (_meshRenderer == null)
                _meshRenderer = GetComponent<MeshRenderer>();
            return _meshRenderer;
        }

        void ConfigureBody()
        {
            SolWaterBody body = GetComponent<SolWaterBody>();
            if (body != null)
                body.ConfigureGeneratedGeometry(SolWaterBodyType.Lake, ResolveRenderer());
        }

        void ReleaseMesh()
        {
            if (_mesh == null)
                return;
            if (Application.isPlaying)
                Destroy(_mesh);
            else
                DestroyImmediate(_mesh);
            _mesh = null;
        }

        /// <summary>
        /// Samples terrain once per extent change. Raycasts are the expensive part, and
        /// the terrain does not move between level changes, so the height field is cached
        /// and only the flooded subset is re-meshed.
        /// </summary>
        public void SampleTerrain()
        {
            _columns = Mathf.Max(2, Mathf.RoundToInt(extent.x / cellSize) + 1);
            _rows = Mathf.Max(2, Mathf.RoundToInt(extent.y / cellSize) + 1);
            if (_terrainHeights.Length != _columns * _rows)
                _terrainHeights = new float[_columns * _rows];

            Vector3 origin = transform.position;
            float halfX = extent.x * 0.5f;
            float halfZ = extent.y * 0.5f;
            for (int row = 0; row < _rows; row++)
            {
                for (int column = 0; column < _columns; column++)
                {
                    float x = origin.x - halfX + column * cellSize;
                    float z = origin.z - halfZ + row * cellSize;
                    Vector3 from = new(x, origin.y + raycastHeight, z);
                    // No terrain hit means nothing to flood against; treat it as very
                    // high ground so the cell stays dry rather than flooding to infinity.
                    _terrainHeights[row * _columns + column] =
                        Physics.Raycast(from, Vector3.down, out RaycastHit hit,
                            raycastHeight * 2f, terrainMask)
                            ? hit.point.y
                            : float.MaxValue;
                }
            }
            _terrainSampled = true;
        }

        [ContextMenu("Rebuild Flood Extent")]
        public bool Rebuild()
        {
            ResolveComponents();
            if (!_terrainSampled)
                SampleTerrain();
            if (_columns < 2 || _rows < 2)
                return false;

            float level = CurrentLevel;
            if (_mesh == null)
            {
                _mesh = new Mesh { name = "Sol Flood Extent", hideFlags = HideFlags.DontSave };
                _mesh.MarkDynamic();
            }
            _mesh.Clear();

            Vector3 origin = transform.position;
            float halfX = extent.x * 0.5f;
            float halfZ = extent.y * 0.5f;

            // One vertex per grid corner, emitted in local space so the mesh follows the
            // transform without a rebuild.
            var vertices = new Vector3[_columns * _rows];
            var colors = new Color[_columns * _rows];
            for (int row = 0; row < _rows; row++)
            {
                for (int column = 0; column < _columns; column++)
                {
                    int index = row * _columns + column;
                    float x = -halfX + column * cellSize;
                    float z = -halfZ + row * cellSize;
                    vertices[index] = new Vector3(x, level - origin.y, z);
                    float fade = SolFloodExtentMath.EdgeFade(
                        _terrainHeights[index], level, edgeFadeDepth);
                    // Vertex colour carries the edge fade as foam, matching how the
                    // spline geometry components feed authored foam to the shader.
                    colors[index] = new Color(fade * edgeFoam, 0f, 0f, fade);
                }
            }

            var indices = new System.Collections.Generic.List<int>(
                (_columns - 1) * (_rows - 1) * 6);
            _floodedCellCount = 0;
            for (int row = 0; row < _rows - 1; row++)
            {
                for (int column = 0; column < _columns - 1; column++)
                {
                    int a = row * _columns + column;
                    int b = a + 1;
                    int c = a + _columns;
                    int d = c + 1;
                    if (!SolFloodExtentMath.CellIsFlooded(
                        _terrainHeights[a], _terrainHeights[b],
                        _terrainHeights[c], _terrainHeights[d], level))
                        continue;
                    indices.Add(a); indices.Add(c); indices.Add(b);
                    indices.Add(b); indices.Add(c); indices.Add(d);
                    _floodedCellCount++;
                }
            }

            _mesh.SetVertices(vertices);
            _mesh.SetColors(colors);
            _mesh.SetTriangles(indices, 0, true);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            if (_meshFilter != null)
                _meshFilter.sharedMesh = _mesh;
            _builtLevel = level;
            return _floodedCellCount > 0;
        }

        public bool ContainsPoint(Vector3 localWorldPosition)
        {
            if (!_terrainSampled || _columns < 2 || _rows < 2)
                return false;
            if (!TryGetCorner(localWorldPosition, out int index))
                return false;
            return SolFloodExtentMath.Submergence(_terrainHeights[index], CurrentLevel) > 0f;
        }

        public bool TrySample(Vector3 localWorldPosition, out SolWaterGeometrySample sample)
        {
            if (!TryGetCorner(localWorldPosition, out int index))
            {
                sample = default;
                return false;
            }
            float level = CurrentLevel;
            float terrainHeight = _terrainHeights[index];
            float submergence = SolFloodExtentMath.Submergence(terrainHeight, level);
            if (submergence <= 0f)
            {
                sample = default;
                return false;
            }
            float fade = SolFloodExtentMath.EdgeFade(terrainHeight, level, edgeFadeDepth);
            sample = new SolWaterGeometrySample(
                new Vector3(localWorldPosition.x, level, localWorldPosition.z),
                Vector3.up,
                Vector3.zero,
                submergence,
                (1f - fade) * edgeFoam);
            return true;
        }

        bool TryGetCorner(Vector3 localWorldPosition, out int index)
        {
            index = 0;
            if (_columns < 2 || _rows < 2 || _terrainHeights.Length == 0)
                return false;
            Vector3 origin = transform.position;
            float column = (localWorldPosition.x - (origin.x - extent.x * 0.5f)) / cellSize;
            float row = (localWorldPosition.z - (origin.z - extent.y * 0.5f)) / cellSize;
            int columnIndex = Mathf.RoundToInt(column);
            int rowIndex = Mathf.RoundToInt(row);
            if (columnIndex < 0 || columnIndex >= _columns || rowIndex < 0 || rowIndex >= _rows)
                return false;
            index = rowIndex * _columns + columnIndex;
            return true;
        }

        /// <summary>Test seam: injects a height field so extent logic can run without a terrain.</summary>
        internal void SetTerrainHeightsForTesting(float[] heights, int columns, int rows)
        {
            _terrainHeights = heights;
            _columns = columns;
            _rows = rows;
            _terrainSampled = true;
            _builtLevel = float.NaN;
        }
    }
}
