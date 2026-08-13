using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ---------------------------------------------------------------------------
/// WATER TILE GRID
/// ---------------------------------------------------------------------------
///
/// Lays out a grid of water tile GameObjects and optionally follows the
/// camera so the ocean appears infinite. Each tile is a shared-mesh quad
/// (generated at startup) with the same Sol/Water material, so the SRP
/// Batcher draws them all in a single instanced batch.
///
/// MODES
/// Static - fixed NxN grid centred on this GameObject's position.
///                Use for bounded lakes, bays, or pre-placed ocean sections.
/// Follow Camera - grid re-centres on the tracked camera every frame,
///                   snapping to tileSize increments to avoid popping.
///                   Creates the appearance of an infinite ocean at zero
///                   extra art cost.
///
/// LOD RINGS (optional)
///   The grid is divided into concentric rings. Inner rings use the full
///   per-tile vertex count; outer rings use a reduced count to save GPU.
///   Ring 0 is the innermost (highest detail). Any ring beyond ringCount
///   uses the outermost resolution.
///
/// SETUP
///   1. Create an empty GameObject, add WaterTileGrid.
///   2. Assign a Sol/Water material (Ocean type recommended).
///   3. Set Tile Size, Grid Radius, and Mode.
///   4. Press Play - tiles generate automatically.
///   5. In the Inspector click "Rebuild Tiles" to refresh without Play.
///
/// TIPS
/// - Set gridRadius = 1 for a simple 3x3 grid (e.g. a lake).
/// - Set gridRadius = 8 + followCamera = true for an open ocean.
/// - Tile Size should match the wave frequency scale in your profile
///     (larger tiles usually need lower Normal Tiling to keep texel density).
/// - One shared WaterVolume covering the whole grid is enough for
/// gameplay / buoyancy - no need for per-tile volumes.
/// ---------------------------------------------------------------------------
/// </summary>
[ExecuteAlways]
public class WaterTileGrid : MonoBehaviour
{
    // --- Grid Settings ---------------------------------------------------

    [Header("Grid")]
    [Tooltip("World-space size of each square tile (metres).")]
    public float tileSize = 50f;

    [Tooltip("Number of tiles out from centre in each direction. " +
             "Total tile count = (gridRadius*2+1)^2.")]
    [Range(0, 16)]
    public int gridRadius = 4;

    [Tooltip("Vertex resolution per tile side (subdivisions). " +
             "Higher = smoother waves but more GPU cost.")]
    [Range(2, 64)]
    public int tileResolution = 32;

    // --- Camera Following -------------------------------------------------

    [Header("Camera Following")]
    [Tooltip("When enabled the grid recentres on the tracked camera each frame, " +
             "creating an effectively infinite water surface.")]
    public bool followCamera = true;

    [Tooltip("Camera to follow. Leave null to use Camera.main.")]
    public Camera trackedCamera;

    [Tooltip("Y position of the water surface in world space. " +
             "Automatically synced from SolWaterManager.waterLevel if a manager is present.")]
    public float waterY = 0f;

    [Tooltip("Sync waterY from SolWaterManager.waterLevel every frame.")]
    public bool syncWaterLevel = true;

    // --- Material ---------------------------------------------------------

    [Header("Material")]
    [Tooltip("Sol/Water material (Water Type should be Ocean or Lake).")]
    public Material waterMaterial;

    // --- LOD Rings --------------------------------------------------------

    [Header("LOD Rings (optional)")]
    [Tooltip("Enable vertex LOD: tiles further from the viewer use fewer verts.")]
    public bool useLodRings = true;

    [Tooltip("Number of rings before resolution starts dropping. " +
             "Ring 0 = innermost, full tileResolution.")]
    [Range(1, 8)]
    public int fullDetailRings = 2;

    [Tooltip("Minimum resolution for the outermost ring tiles.")]
    [Range(2, 32)]
    public int minTileResolution = 4;

    [Tooltip("Vertical skirt (metres) hung from each tile edge to hide LOD seams " +
             "between rings of different resolution. Must exceed the largest " +
             "wave height difference across one coarse edge segment. " +
             "Skirts are hidden automatically when the camera is underwater.")]
    [Range(0f, 10f)]
    public float skirtDepth = 1.5f;

    // --- Auto Water Volume ------------------------------------------------

    [Header("Auto Water Volume")]
    [Tooltip("Automatically create and resize a WaterVolume to match the grid footprint. " +
             "Covers the full grid XZ extent so buoyancy and swimming work without manual setup.")]
    public bool autoWaterVolume = true;

    [Tooltip("Depth of the water volume below the surface (metres). " +
             "Should be deeper than your riverbed / ocean floor.")]
    [Range(1f, 200f)]
    public float volumeDepth = 30f;

    [Tooltip("Extra margin added around the grid footprint on all sides (metres). " +
             "Useful when followCamera is on - keeps the volume larger than the visible tiles.")]
    [Range(0f, 500f)]
    public float volumeMargin = 50f;

    // --- Private ---------------------------------------------------------

    // Pool of active tile renderers, keyed by integer grid coordinate.
    readonly Dictionary<Vector2Int, GameObject> _activeTiles = new();

    // Pool of inactive tile GameObjects for reuse.
    readonly Queue<GameObject> _tilePool = new();

    // Reused during grid shifts to avoid per-shift GC allocations.
    readonly HashSet<Vector2Int> _desiredTiles = new();
    readonly List<Vector2Int> _tilesToRemove = new();

    // Parent transform to keep hierarchy clean.
    Transform _tileRoot;

    // Cached meshes per resolution to avoid re-generating each frame.
    readonly Dictionary<int, Mesh> _meshCache = new();

    // Last snapped centre position - used to detect when the grid needs to shift.
    Vector2Int _lastCentre = new Vector2Int(int.MaxValue, int.MaxValue);

    // Managed WaterVolume - created/destroyed by this component.
    WaterVolume _managedVolume;
    SolEnvironmentCoordinator _environmentCoordinator;

    // Per-frame state caches. These keep stationary grids from repeatedly
    // touching shader globals, tile transforms, and the managed collider.
    Vector4 _lastWaveFadeCenter = new(float.NaN, float.NaN, float.NaN, float.NaN);
    float _lastAppliedWaterY = float.NaN;
    Vector2Int _lastVolumeCentre = new(int.MaxValue, int.MaxValue);
    float _lastVolumeFootprint = float.NaN;
    float _lastVolumeDepth = float.NaN;

    // Wave-LOD fade centre global (w = 1 while a grid is driving it).
    static readonly int _SID_WaveFadeCenter = Shader.PropertyToID("_Sol_WaveFadeCenter");

    // --- Unity Messages ---------------------------------------------------

    void OnEnable()
    {
        _environmentCoordinator = SolEnvironmentCoordinator.Resolve(this, createIfMissing: true);
        _environmentCoordinator?.Register(this);
        EnsureTileRoot();
        RebuildAll();
    }

    void OnDisable()
    {
        ClearAllTiles();
        ClearMeshCache();
        DestroyManagedVolume();

        // Stop driving the fade centre; the shader falls back to the
        // rendering camera and the C# sampler to full detail.
        Shader.SetGlobalVector(_SID_WaveFadeCenter, Vector4.zero);
        SolWaterSurfaceSampler.ClearWaveFadeCenter();
        _lastWaveFadeCenter = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
        _lastAppliedWaterY = float.NaN;
        _lastVolumeCentre = new Vector2Int(int.MaxValue, int.MaxValue);
        _lastVolumeFootprint = float.NaN;
        _lastVolumeDepth = float.NaN;
        _environmentCoordinator?.Unregister(this);
        _environmentCoordinator = null;
    }

    bool _rebuildPending;

    void OnValidate()
    {
        tileSize = Mathf.Max(0.01f, tileSize);
        volumeDepth = Mathf.Max(1f, volumeDepth);
        volumeMargin = Mathf.Max(0f, volumeMargin);

        // Clamp dependent values so they can't go out of range.
        minTileResolution = Mathf.Min(minTileResolution, tileResolution);
        fullDetailRings    = Mathf.Clamp(fullDetailRings, 1, gridRadius + 1);

        // AddComponent is forbidden during OnValidate - defer to next editor tick.
        _rebuildPending = true;
    }

    void Update()
    {
        // Recover tile root after domain reloads (non-serialized field becomes null).
        EnsureTileRoot();

        // Execute any rebuild that was deferred from OnValidate.
        if (_rebuildPending)
        {
            _rebuildPending = false;
            _lastCentre = new Vector2Int(int.MaxValue, int.MaxValue);
            ClearAllTiles();
            ClearMeshCache();
        }

        // Sync water level from manager.
        if (syncWaterLevel && SolWaterManager.Instance != null)
            waterY = SolWaterManager.Instance.waterLevel;

        // Publish the tracked position as the wave-LOD fade centre so the
        // shader's wave fades and the C# sampler use the same origin as the
        // mesh LOD rings (not the rendering camera, which may be elsewhere -
        // e.g. the Scene view or a minimap).
        Vector3 tracked = GetTrackedPosition();
        Vector4 waveFadeCenter = new(tracked.x, tracked.y, tracked.z, 1f);
        if (_lastWaveFadeCenter != waveFadeCenter)
        {
            Shader.SetGlobalVector(_SID_WaveFadeCenter, waveFadeCenter);
            SolWaterSurfaceSampler.SetWaveFadeCenter(new Vector2(tracked.x, tracked.z));
            _lastWaveFadeCenter = waveFadeCenter;
        }

        // Determine centre of the grid in grid-space.
        Vector2Int centre = WorldToGrid(tracked);

        if (centre != _lastCentre)
        {
            _lastCentre = centre;
            RefreshGrid(centre);
        }

        // Keep every tile at the correct Y only when the level changed.
        if (!Mathf.Approximately(_lastAppliedWaterY, waterY))
        {
            foreach (var tile in _activeTiles.Values)
            {
                Vector3 p = tile.transform.position;
                if (!Mathf.Approximately(p.y, waterY))
                    tile.transform.position = new Vector3(p.x, waterY, p.z);
            }
            _lastAppliedWaterY = waterY;
        }

        SyncVolume();
    }

    // --- Public API -------------------------------------------------------

    /// <summary>Destroy all tiles and rebuild from scratch. Safe to call in editor or at runtime.</summary>
    public void RebuildAll()
    {
        ClearAllTiles();
        _lastCentre = new Vector2Int(int.MaxValue, int.MaxValue);
        ClearMeshCache();
        Update();
    }

    // --- Auto Volume ------------------------------------------------------

    void SyncVolume()
    {
        if (!autoWaterVolume)
        {
            DestroyManagedVolume();
            return;
        }

        // Recover reference after domain reload by searching for the managed child.
        if (_managedVolume == null)
        {
            var existing = transform.Find("_WaterVolume");
            if (existing != null)
                _managedVolume = existing.GetComponent<WaterVolume>();
        }

        // Create if still missing.
        if (_managedVolume == null)
        {
            var volGO = new GameObject("_WaterVolume");
            volGO.SetActive(false);
            volGO.transform.SetParent(transform, false);
            var volumeCollider = volGO.AddComponent<BoxCollider>();
            volumeCollider.isTrigger = true;
            _managedVolume = volGO.AddComponent<WaterVolume>();
            volGO.SetActive(true);
        }

        // Footprint: full grid diameter + margin on all sides.
        float footprint = (gridRadius * 2 + 1) * tileSize + volumeMargin * 2f;

        // Position: horizontally at snapped centre, vertically so the TOP FACE = waterY.
        Vector2Int centre = GetSnappedCentre();
        float centreY     = waterY - volumeDepth * 0.5f;

        bool transformChanged = _managedVolume.transform.position != new Vector3(
            centre.x * tileSize, centreY, centre.y * tileSize);
        bool colliderChanged = centre != _lastVolumeCentre
            || !Mathf.Approximately(_lastVolumeFootprint, footprint)
            || !Mathf.Approximately(_lastVolumeDepth, volumeDepth);
        bool materialChanged = _managedVolume.waterMaterial != waterMaterial;

        if (!transformChanged && !colliderChanged && !materialChanged)
            return;

        _managedVolume.transform.position = new Vector3(
            centre.x * tileSize,
            centreY,
            centre.y * tileSize);

        // Size the BoxCollider directly (WaterVolume uses its bounds for queries).
        if (colliderChanged)
        {
            var col = _managedVolume.GetComponent<BoxCollider>();
            if (col != null)
            {
                col.center = Vector3.zero;
                col.size   = new Vector3(footprint, volumeDepth, footprint);
            }
        }

        // Keep material in sync for wave parameter reading.
        if (materialChanged)
            _managedVolume.SetWaterMaterial(waterMaterial);

        _lastVolumeCentre = centre;
        _lastVolumeFootprint = footprint;
        _lastVolumeDepth = volumeDepth;
    }

    void DestroyManagedVolume()
    {
        if (_managedVolume == null)
        {
            Transform existing = transform.Find("_WaterVolume");
            if (existing != null)
                _managedVolume = existing.GetComponent<WaterVolume>();
        }

        if (_managedVolume == null)
            return;

        DestroyUnityObject(_managedVolume.gameObject);
        _managedVolume = null;
    }

    // --- Grid Logic -------------------------------------------------------

    Vector2Int GetSnappedCentre() => WorldToGrid(GetTrackedPosition());

    /// <summary>
    /// The position the grid centres its LOD rings on. This is also
    /// published as the wave-LOD fade centre so mesh resolution and shader
    /// wave fades always agree, regardless of which camera renders.
    /// </summary>
    Vector3 GetTrackedPosition()
    {
        if (!followCamera)
            return transform.position;

#if UNITY_EDITOR
        // In edit mode the user views through the SceneView camera, not
        // Camera.main - centre the rings on what they actually see.
        if (!Application.isPlaying)
        {
            var sceneView = UnityEditor.SceneView.lastActiveSceneView;
            if (sceneView != null && sceneView.camera != null)
                return sceneView.camera.transform.position;
        }
#endif
        Camera cam = trackedCamera != null ? trackedCamera : Camera.main;
        return cam != null ? cam.transform.position : transform.position;
    }

    Vector2Int WorldToGrid(Vector3 worldPos)
    {
        return new Vector2Int(
            Mathf.RoundToInt(worldPos.x / tileSize),
            Mathf.RoundToInt(worldPos.z / tileSize));
    }

    Vector3 GridToWorld(Vector2Int cell)
    {
        return new Vector3(cell.x * tileSize, waterY, cell.y * tileSize);
    }

    void RefreshGrid(Vector2Int centre)
    {
        // Build set of cells the grid should occupy.
        _desiredTiles.Clear();
        for (int dx = -gridRadius; dx <= gridRadius; dx++)
        for (int dz = -gridRadius; dz <= gridRadius; dz++)
            _desiredTiles.Add(new Vector2Int(centre.x + dx, centre.y + dz));

        // Return tiles that are no longer needed to the pool.
        _tilesToRemove.Clear();
        foreach (var kv in _activeTiles)
        {
            if (!_desiredTiles.Contains(kv.Key))
            {
                ReturnToPool(kv.Value);
                _tilesToRemove.Add(kv.Key);
            }
        }
        foreach (var k in _tilesToRemove) _activeTiles.Remove(k);

        // Spawn tiles for new cells and keep retained tiles' LOD current.
        // A cell's ring changes whenever the grid re-centres, so a retained
        // tile may need a different resolution mesh than it was given when
        // it entered the grid (otherwise low-res tiles drift next to the
        // camera and open huge seams).
        foreach (var cell in _desiredTiles)
        {
            int ring = Mathf.Max(Mathf.Abs(cell.x - centre.x),
                                  Mathf.Abs(cell.y - centre.y));

            if (_activeTiles.TryGetValue(cell, out GameObject existing))
            {
                Mesh desired = GetOrCreateTileMesh(GetResolutionForRing(ring));
                var mf = existing.GetComponent<MeshFilter>();
                if (mf.sharedMesh != desired)
                    mf.sharedMesh = desired;
                continue;
            }

            GameObject tile = GetFromPool();
            SetupTile(tile, cell, ring);
            _activeTiles[cell] = tile;
        }
    }

    // --- Tile Pool --------------------------------------------------------

    void SetupTile(GameObject tile, Vector2Int cell, int ring)
    {
        tile.SetActive(true);
        tile.transform.position   = GridToWorld(cell);
        tile.transform.localScale = new Vector3(tileSize, 1f, tileSize);

        int res = GetResolutionForRing(ring);
        var mf  = tile.GetComponent<MeshFilter>();
        mf.sharedMesh = GetOrCreateTileMesh(res);

        var mr = tile.GetComponent<MeshRenderer>();
        mr.sharedMaterial = waterMaterial;
    }

    GameObject GetFromPool()
    {
        if (_tilePool.Count > 0)
            return _tilePool.Dequeue();

        var go = new GameObject("WaterTile");
        go.transform.SetParent(_tileRoot, false);
        go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        mr.shadowCastingMode    = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows       = false;
        mr.lightProbeUsage      = UnityEngine.Rendering.LightProbeUsage.Off;
        mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.BlendProbesAndSkybox;
        return go;
    }

    void ReturnToPool(GameObject tile)
    {
        tile.SetActive(false);
        _tilePool.Enqueue(tile);
    }

    void ClearAllTiles()
    {
        foreach (var tile in _activeTiles.Values)
            ReturnToPool(tile);
        _activeTiles.Clear();

        // Destroy all pooled tiles to avoid stale objects in edit mode.
        while (_tilePool.Count > 0)
        {
            var t = _tilePool.Dequeue();
            if (t != null)
                DestroyUnityObject(t);
        }
    }

    void ClearMeshCache()
    {
        foreach (Mesh mesh in _meshCache.Values)
            DestroyUnityObject(mesh);
        _meshCache.Clear();
    }

    // --- LOD Resolution --------------------------------------------------

    int GetResolutionForRing(int ring)
    {
        if (!useLodRings || ring < fullDetailRings)
            return tileResolution;

        int rings  = Mathf.Max(gridRadius - fullDetailRings, 1);
        int offset = ring - fullDetailRings;
        float t    = Mathf.Clamp01((float)offset / rings);

        // Step down to the nearest power-of-2 resolution.
        int res = Mathf.RoundToInt(Mathf.Lerp(tileResolution, minTileResolution, t));
        return Mathf.Max(res, 2);
    }

    // --- Mesh Generation -------------------------------------------------

    Mesh GetOrCreateTileMesh(int resolution)
    {
        if (_meshCache.TryGetValue(resolution, out Mesh cached))
            return cached;

        // Skirts are only needed when LOD rings can mismatch edge vertices.
        float skirt = useLodRings ? skirtDepth : 0f;
        Mesh mesh = BuildTileMesh(resolution, skirt);
        _meshCache[resolution] = mesh;
        return mesh;
    }

    /// <summary>
    /// Build a flat NxN subdivided quad that fills exactly one tile.
    /// UV (0,0) = corner, (1,1) = opposite corner.
    /// The mesh is centred at the origin so the tile's world position
    /// can be set to the tile centre directly.
    ///
    /// When skirtDepth > 0, a vertical skirt is hung from the perimeter to
    /// hide cracks between adjacent tiles of different LOD resolution.
    /// Skirt vertices share XZ with the edge vertices, so the wave shader
    /// displaces them identically and the wall always meets the edge.
    /// Skirt UVs are shifted by +2 as a flag; the shader uses this to
    /// discard skirt pixels when the camera is underwater.
    /// </summary>
    static Mesh BuildTileMesh(int resolution, float skirtDepth)
    {
        int vertsPerSide  = resolution + 1;
        int gridVertCount = vertsPerSide * vertsPerSide;
        bool hasSkirt     = skirtDepth > 0f;
        int loopCount     = hasSkirt ? resolution * 4 : 0;

        // Grid + duplicated flagged perimeter ring + lowered ring.
        int totalVerts = gridVertCount + loopCount * 2;

        var vertices  = new Vector3[totalVerts];
        var uvs       = new Vector2[totalVerts];
        var triangles = new int[resolution * resolution * 6 + loopCount * 6];

        for (int z = 0; z <= resolution; z++)
        for (int x = 0; x <= resolution; x++)
        {
            int idx = z * vertsPerSide + x;
            float u = (float)x / resolution;
            float v = (float)z / resolution;
            // Centred on origin, scaled to 1x1. WaterTileGrid scales it via transform.
            vertices[idx] = new Vector3(u - 0.5f, 0f, v - 0.5f);
            uvs[idx]      = new Vector2(u, v);
        }

        int tri = 0;
        for (int z = 0; z < resolution; z++)
        for (int x = 0; x < resolution; x++)
        {
            int bl = z * vertsPerSide + x;
            int br = bl + 1;
            int tl = bl + vertsPerSide;
            int tr = tl + 1;

            triangles[tri++] = bl; triangles[tri++] = tl; triangles[tri++] = tr;
            triangles[tri++] = bl; triangles[tri++] = tr; triangles[tri++] = br;
        }

        if (hasSkirt)
        {
            // Perimeter grid indices, counter-clockwise viewed from above.
            var loop = new int[loopCount];
            int li = 0;
            for (int x = 0; x < resolution; x++) loop[li++] = x;                                    // south row
            for (int z = 0; z < resolution; z++) loop[li++] = z * vertsPerSide + resolution;        // east column
            for (int x = resolution; x > 0; x--) loop[li++] = resolution * vertsPerSide + x;        // north row
            for (int z = resolution; z > 0; z--) loop[li++] = z * vertsPerSide;                     // west column

            // Duplicated top ring (flagged) + lowered bottom ring. The tile
            // transform scales X/Z by tileSize but leaves Y at 1, so the
            // local offset is in world metres.
            int topStart = gridVertCount;
            int botStart = gridVertCount + loopCount;
            for (int i = 0; i < loopCount; i++)
            {
                Vector3 src = vertices[loop[i]];
                Vector2 flaggedUV = uvs[loop[i]] + new Vector2(2f, 2f);
                vertices[topStart + i] = src;
                uvs[topStart + i]      = flaggedUV;
                vertices[botStart + i] = new Vector3(src.x, -skirtDepth, src.z);
                uvs[botStart + i]      = flaggedUV;
            }

            for (int i = 0; i < loopCount; i++)
            {
                int next = (i + 1) % loopCount;
                int a  = topStart + i;
                int b  = topStart + next;
                int a2 = botStart + i;
                int b2 = botStart + next;

                triangles[tri++] = a; triangles[tri++] = a2; triangles[tri++] = b;
                triangles[tri++] = b; triangles[tri++] = a2; triangles[tri++] = b2;
            }
        }

        var mesh = new Mesh
        {
            name   = $"WaterTile_{resolution}x{resolution}",
            indexFormat = totalVerts > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16
        };
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.UploadMeshData(false); // keep readable so WaterVolume can raycast if needed
        return mesh;
    }

    // --- Editor Helpers --------------------------------------------------

    void EnsureTileRoot()
    {
        if (_tileRoot != null) return;

        // After a domain reload, the _Tiles child still exists in the hierarchy
        // but our non-serialized reference is gone. Find and reuse it, clearing
        // any stale tile children we've lost track of.
        Transform existing = transform.Find("_Tiles");
        if (existing != null)
        {
            for (int i = existing.childCount - 1; i >= 0; i--)
                DestroyUnityObject(existing.GetChild(i).gameObject);
            _tileRoot = existing;
            // Force a full rebuild since we cleared the stale tiles.
            _activeTiles.Clear();
            _tilePool.Clear();
            _lastCentre = new Vector2Int(int.MaxValue, int.MaxValue);
            return;
        }

        var rootGO = new GameObject("_Tiles");
        rootGO.transform.SetParent(transform, false);
        _tileRoot = rootGO.transform;
    }

    void OnDrawGizmosSelected()
    {
        if (tileSize <= 0) return;

        Vector2Int centre = GetSnappedCentre();
        Gizmos.color = new Color(0.1f, 0.5f, 1f, 0.25f);

        for (int dx = -gridRadius; dx <= gridRadius; dx++)
        for (int dz = -gridRadius; dz <= gridRadius; dz++)
        {
            Vector3 pos = GridToWorld(new Vector2Int(centre.x + dx, centre.y + dz));
            Gizmos.DrawWireCube(
                pos + Vector3.up * 0.01f,
                new Vector3(tileSize, 0.02f, tileSize));
        }

        // Highlight inner full-detail rings
        Gizmos.color = new Color(0.1f, 1f, 0.5f, 0.15f);
        for (int dx = -fullDetailRings; dx <= fullDetailRings; dx++)
        for (int dz = -fullDetailRings; dz <= fullDetailRings; dz++)
        {
            Vector3 pos = GridToWorld(new Vector2Int(centre.x + dx, centre.y + dz));
            Gizmos.DrawCube(
                pos,
                new Vector3(tileSize, 0.01f, tileSize));
        }
    }

    static void DestroyUnityObject(Object obj)
    {
        if (obj == null)
            return;

        if (Application.isPlaying)
            Destroy(obj);
        else
            DestroyImmediate(obj);
    }
}
