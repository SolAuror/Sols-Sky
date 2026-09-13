using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sol.Landscape
{
    [Serializable]
    public sealed class SolLandscapeTile
    {
        public Terrain terrain;
        public SolLandscapePaintData paint;
        [NonSerialized] internal SolLandscapeBinding binding;
        [NonSerialized] internal Material previousMaterial;
        [NonSerialized] internal float previousBasemapDistance;
    }

    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class SolLandscapeGroup : MonoBehaviour
    {
        static readonly HashSet<SolLandscapeGroup> active = new();
        public SolLandscapeProfile profile;
        public SolLandscapeAsset assetOwner;
        public List<SolLandscapeTile> tiles = new();
        public Vector2 projectionOrigin;
        public Material materialTemplate;
        [Range(64, 2048)] public int paintResolution = 512;
        Material material;
        readonly Dictionary<Terrain, SolLandscapeTile> boundTiles = new();
        readonly List<Terrain> removedTiles = new();
        string keywords;
        object previewOwner;
        public float? PreviewSnow { get; private set; }
        public float? PreviewWetness { get; private set; }
        public int DebugMode { get; private set; }
        public int DebugLayer { get; private set; }
        public int PreviewRevision { get; private set; }
        public int LastPublishWriteCount { get; private set; }
        public static bool Owns(Terrain terrain)
        {
            foreach (var group in active) if (group != null && group.tiles.Exists(t => t.terrain == terrain)) return true;
            return false;
        }
        public bool SetPreview(object owner, float? snow, float? wetness, int debugMode, int debugLayer)
        {
            if (Application.isPlaying || owner == null || (previewOwner != null && !ReferenceEquals(owner, previewOwner))) return false;
            previewOwner = owner; PreviewSnow = snow.HasValue ? Mathf.Clamp01(snow.Value) : null;
            PreviewWetness = wetness.HasValue ? Mathf.Clamp01(wetness.Value) : null;
            DebugMode = debugMode; DebugLayer = debugLayer; PreviewRevision++; return true;
        }
        public void ClearPreview(object owner = null)
        {
            if (owner != null && !ReferenceEquals(owner, previewOwner)) return;
            previewOwner = null; PreviewSnow = PreviewWetness = null; DebugMode = DebugLayer = 0; PreviewRevision++;
        }
        void OnEnable() { active.Add(this); Invalidate(); }
        void OnValidate() => Invalidate();
        void OnDisable()
        {
            active.Remove(this); ClearPreview();
            SuspendBindings();
            if (material != null) { if (Application.isPlaying) Destroy(material); else DestroyImmediate(material); }
            material = null;
        }
        public void Invalidate() { keywords = null; foreach (var tile in tiles) tile.binding?.Invalidate(); }
        public void SuspendBindings()
        {
            foreach (var tile in boundTiles.Values) ReleaseTile(tile);
            boundTiles.Clear(); foreach (var tile in tiles) tile.binding = null;
        }
        void ReleaseTile(SolLandscapeTile tile)
        {
            tile.binding?.Release();
            if (tile.terrain != null && tile.terrain.materialTemplate == material)
            { tile.terrain.materialTemplate = tile.previousMaterial; tile.terrain.basemapDistance = tile.previousBasemapDistance; }
        }
        void LateUpdate() => Publish();
        public void Publish()
        {
            LastPublishWriteCount = 0;
            if (profile == null) { SuspendBindings(); return; }
            removedTiles.Clear();
            foreach (var terrain in boundTiles.Keys) if (!tiles.Exists(t => t.terrain == terrain)) removedTiles.Add(terrain);
            foreach (var terrain in removedTiles) { ReleaseTile(boundTiles[terrain]); boundTiles.Remove(terrain); }
            if (Application.isPlaying && previewOwner != null) ClearPreview();
            if (material == null)
            {
                var shader = profile.terrainShader != null ? profile.terrainShader : Shader.Find("Sol/Terrain/Array Lit");
                if (shader == null) return;
                material = materialTemplate != null ? new Material(materialTemplate) : new Material(shader);
                material.hideFlags = HideFlags.HideAndDontSave;
            }
            string next = $"{profile.stochasticTiling}:{profile.triplanarProjection}:{profile.heightBlend}:{DebugMode}";
            if (keywords != next)
            {
                SetKeyword("_SOL_LANDSCAPE_STOCHASTIC", profile.stochasticTiling);
                SetKeyword("_SOL_LANDSCAPE_TRIPLANAR", profile.triplanarProjection);
                SetKeyword("_SOL_LANDSCAPE_BLEND_HEIGHT", profile.heightBlend);
                SetKeyword("_SOL_LANDSCAPE_DEBUG", DebugMode != 0); keywords = next;
            }
            foreach (var tile in tiles)
            {
                if (tile.terrain == null) continue;
                if (!boundTiles.TryGetValue(tile.terrain, out var bound))
                {
                    SolLandscapeDriver.ReleaseTerrainBindings(tile.terrain);
                    bound = new SolLandscapeTile { terrain = tile.terrain, previousMaterial = tile.terrain.materialTemplate,
                        previousBasemapDistance = tile.terrain.basemapDistance, binding = new SolLandscapeBinding() };
                    boundTiles.Add(tile.terrain, bound);
                }
                tile.binding = bound.binding;
                tile.terrain.materialTemplate = material; tile.terrain.basemapDistance = float.MaxValue;
                tile.binding.landscapeTerrain = tile.terrain; tile.binding.config = profile;
                tile.binding.Group = this; tile.binding.PaintData = tile.paint;
                tile.binding.Publish(); LastPublishWriteCount += tile.binding.LastPublishWriteCount;
            }
        }
        void SetKeyword(string name, bool enabled) { if (enabled) material.EnableKeyword(name); else material.DisableKeyword(name); }
        public bool Validate(out string reason, bool artwork = true)
        {
            reason = null;
            if (profile == null) { reason = "Choose a landscape profile."; return false; }
            if (profile.Layers.Count < 1 || profile.Layers.Count > 8) { reason = "A landscape palette requires 1–8 materials."; return false; }
            if (tiles.Count == 0 || tiles[0].terrain == null) { reason = "Assign the landscape's terrain tiles."; return false; }
            var ids = new HashSet<string>();
            foreach (var layer in profile.Layers)
                if (layer == null || layer.terrainLayer == null || string.IsNullOrEmpty(layer.materialId) || !ids.Add(layer.materialId))
                { reason = "Palette materials need artwork and unique stable IDs. Recreate or migrate this profile."; return false; }
            bool hasAutomatic = false; foreach (var entry in profile.Layers) hasAutomatic |= entry.mode == SolLandscapeLayerMode.Auto;
            if (hasAutomatic && !profile.preserveLegacyFallback && profile.Layers[profile.FallbackIndex].mode != SolLandscapeLayerMode.Auto)
            { reason = "The fallback material must use automatic distribution."; return false; }
            var first = tiles[0].terrain.terrainData;
            if (first == null) { reason = "The first tile has no TerrainData."; return false; }
            var occupied = new HashSet<Vector2Int>();
            var paintOwners = new HashSet<SolLandscapePaintData>();
            var origin = tiles[0].terrain.transform.position;
            foreach (var tile in tiles)
            {
                var terrain = tile.terrain;
                if (terrain == null || terrain.terrainData == null) { reason = "Replace the missing terrain tile."; return false; }
                var data = terrain.terrainData;
                if (Quaternion.Angle(terrain.transform.rotation, Quaternion.identity) > .001f || (terrain.transform.lossyScale - Vector3.one).sqrMagnitude > .000001f)
                { reason = terrain.name + ": reset rotation and scale before painting."; return false; }
                if (data.size != first.size || data.heightmapResolution != first.heightmapResolution || data.alphamapResolution != first.alphamapResolution)
                { reason = terrain.name + ": tile dimensions and height/control resolutions must match the group."; return false; }
                var offset = terrain.transform.position - origin;
                var cell = new Vector2Int(Mathf.RoundToInt(offset.x / data.size.x), Mathf.RoundToInt(offset.z / data.size.z));
                if (Mathf.Abs(offset.x - cell.x * data.size.x) > .01f || Mathf.Abs(offset.z - cell.y * data.size.z) > .01f || Mathf.Abs(offset.y) > .01f || !occupied.Add(cell))
                { reason = terrain.name + ": align this tile to an unused group grid cell at the same base height."; return false; }
                foreach (var other in active) if (other != this && other.tiles.Exists(t => t.terrain == terrain))
                { reason = terrain.name + ": belongs to another landscape group."; return false; }
                if (tile.paint != null && tile.paint.Resolution != paintResolution)
                { reason = terrain.name + ": resize painted data to the group's paint resolution."; return false; }
                if (tile.paint != null && !paintOwners.Add(tile.paint))
                { reason = terrain.name + ": shares a paint asset with another tile. Duplicate its paint asset before editing."; return false; }
                if (tile.paint != null)
                    foreach (var other in active) if (other != this && other.tiles.Exists(t => t.paint == tile.paint))
                    { reason = terrain.name + ": shares a paint asset with another landscape. Duplicate its paint asset before editing."; return false; }
                var layers = data.terrainLayers;
                if (layers.Length != profile.Layers.Count) { reason = terrain.name + ": apply the group's palette to this tile."; return false; }
                for (int i = 0; i < layers.Length; i++) if (layers[i] != profile.Layers[i].terrainLayer)
                { reason = terrain.name + ": material order differs from the group palette."; return false; }
            }
            var reached = new HashSet<Vector2Int>(); var queue = new Queue<Vector2Int>(); queue.Enqueue(Vector2Int.zero);
            while (queue.Count > 0)
            {
                var p = queue.Dequeue(); if (!occupied.Contains(p) || !reached.Add(p)) continue;
                queue.Enqueue(p + Vector2Int.right); queue.Enqueue(p + Vector2Int.left); queue.Enqueue(p + Vector2Int.up); queue.Enqueue(p + Vector2Int.down);
            }
            if (reached.Count != occupied.Count) { reason = "Tiles must form one edge-connected group."; return false; }
            if (artwork && (profile.CSArray == null || profile.NOHArray == null || profile.CSArray.depth < profile.Layers.Count || profile.NOHArray.depth < profile.Layers.Count))
            { reason = "Rebuild this profile's terrain textures."; return false; }
            return true;
        }
    }
}

