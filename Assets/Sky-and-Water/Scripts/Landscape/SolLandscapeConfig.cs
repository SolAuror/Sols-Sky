using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sol.Landscape
{
    public enum SolLandscapeLayerMode : byte
    {
        Manual,
        Auto,
    }

    [Serializable]
    public sealed class SolLandscapeLayerEntry
    {
        [Tooltip("The array slice index is this entry's position in the list.")]
        public TerrainLayer terrainLayer;

        [Header("Phase 4 auto-material contract")]
        [Tooltip("Manual preserves painted weight. Auto redistributes only the budget painted into Auto-tagged layers.")]
        public SolLandscapeLayerMode mode = SolLandscapeLayerMode.Manual;

        [Tooltip("Base multiplier for the evaluated Phase 4B procedural claim.")]
        [Range(0f, 1f)] public float autoWeight = 1f;

        [Header("Phase 4B rule authoring")]
        [Tooltip("Slope response midpoint in degrees, evaluated from the geometric terrain normal.")]
        [Range(0f, 90f)] public float slopeCenter = 35f;
        [Tooltip("Full width in degrees of the polynomial transition around Slope Center.")]
        [Min(0.01f)] public float slopeContrast = 8f;
        [Tooltip("Quadratic response bias. Positive ramps early then plateaus; negative delays the ramp.")]
        [Range(-1f, 1f)] public float slopeBias;
        [Tooltip("Positive favours steep ground, negative favours flat ground, and zero leaves the base claim unchanged.")]
        [Range(-1f, 1f)] public float slopeInfluence = 1f;

        [Tooltip("World-Y values mapping the altitude response from zero to one.")]
        public Vector2 heightRange = new Vector2(0f, 1000f);
        [Tooltip("Quadratic altitude-response bias, with the same endpoint-preserving shape as slope.")]
        [Range(-1f, 1f)] public float heightBias;
        [Tooltip("Positive favours high ground, negative favours low ground, and zero disables altitude modulation.")]
        [Range(-1f, 1f)] public float heightInfluence;

        [Tooltip("World-space scale applied to signed ddx/ddy mean curvature before clamping.")]
        [Min(0f)] public float cavityScale = 12f;
        [Tooltip("Positive favours concavity, negative favours convexity, and zero disables cavity modulation.")]
        [Range(-1f, 1f)] public float cavityInfluence;

        public SolLandscapeLayerEntry(TerrainLayer terrainLayer)
        {
            this.terrainLayer = terrainLayer;
        }
    }

    [Serializable]
    public sealed class SolLandscapeBakeFingerprint
    {
        [SerializeField] private string terrainLayerGuid;
        [SerializeField] private string diffuseTextureGuid;
        [SerializeField] private string normalTextureGuid;
        [SerializeField] private string maskTextureGuid;

        public string TerrainLayerGuid => terrainLayerGuid;
        public string DiffuseTextureGuid => diffuseTextureGuid;
        public string NormalTextureGuid => normalTextureGuid;
        public string MaskTextureGuid => maskTextureGuid;

        public SolLandscapeBakeFingerprint(
            string terrainLayerGuid,
            string diffuseTextureGuid,
            string normalTextureGuid,
            string maskTextureGuid)
        {
            this.terrainLayerGuid = terrainLayerGuid ?? string.Empty;
            this.diffuseTextureGuid = diffuseTextureGuid ?? string.Empty;
            this.normalTextureGuid = normalTextureGuid ?? string.Empty;
            this.maskTextureGuid = maskTextureGuid ?? string.Empty;
        }
    }

    [CreateAssetMenu(menuName = "Sol/Landscape/Config", fileName = "SolLandscapeConfig")]
    public sealed class SolLandscapeConfig : ScriptableObject
    {
        [SerializeField] private TerrainData terrainData;

        [Tooltip("Order is authoritative: entry index equals the texture-array slice index.")]
        [SerializeField] private List<SolLandscapeLayerEntry> layers = new List<SolLandscapeLayerEntry>();

        [Header("Baked CSNOH arrays")]
        [Tooltip("sRGB: RGB diffuse colour, A remapped smoothness.")]
        [SerializeField] private Texture2DArray csArray;
        [Tooltip("Linear: RG normal XY, B ambient occlusion, A height.")]
        [SerializeField] private Texture2DArray nohArray;

        [Header("Height blend")]
        [Tooltip("Transition width for height-based layer blending. The 0.56 default preserves the authored legacy-material intent.")]
        [Min(0f)] [SerializeField] private float heightTransition = 0.56f;

        [Header("Last bake")]
        [SerializeField] private string bakedUtc;
        [SerializeField] private int bakedWidth;
        [SerializeField] private int bakedHeight;
        [SerializeField] private int bakedMipCount;
        [SerializeField] private long csStorageBytes;
        [SerializeField] private long nohStorageBytes;
        [TextArea(3, 12)] [SerializeField] private string lastBakeSummary;

        [HideInInspector] [SerializeField] private string terrainDataGuid;
        [HideInInspector] [SerializeField]
        private List<SolLandscapeBakeFingerprint> bakeFingerprints = new List<SolLandscapeBakeFingerprint>();

        public TerrainData TerrainData => terrainData;
        public IReadOnlyList<SolLandscapeLayerEntry> Layers => layers;
        public Texture2DArray CSArray => csArray;
        public Texture2DArray NOHArray => nohArray;
        public float HeightTransition => heightTransition;
        public string BakedUtc => bakedUtc;
        public int BakedWidth => bakedWidth;
        public int BakedHeight => bakedHeight;
        public int BakedMipCount => bakedMipCount;
        public long CSStorageBytes => csStorageBytes;
        public long NOHStorageBytes => nohStorageBytes;
        public string LastBakeSummary => lastBakeSummary;
        public string TerrainDataGuid => terrainDataGuid;
        public IReadOnlyList<SolLandscapeBakeFingerprint> BakeFingerprints => bakeFingerprints;

        public void RecordBake(
            TerrainData sourceTerrainData,
            IReadOnlyList<TerrainLayer> orderedLayers,
            Texture2DArray bakedCSArray,
            Texture2DArray bakedNOHArray,
            string sourceTerrainDataGuid,
            IReadOnlyList<SolLandscapeBakeFingerprint> fingerprints,
            string utcTimestamp,
            long csBytes,
            long nohBytes,
            string summary)
        {
            terrainData = sourceTerrainData;
            SynchronizeLayerEntries(orderedLayers);
            csArray = bakedCSArray;
            nohArray = bakedNOHArray;
            terrainDataGuid = sourceTerrainDataGuid ?? string.Empty;
            bakeFingerprints = new List<SolLandscapeBakeFingerprint>(fingerprints);
            bakedUtc = utcTimestamp ?? string.Empty;
            bakedWidth = bakedCSArray != null ? bakedCSArray.width : 0;
            bakedHeight = bakedCSArray != null ? bakedCSArray.height : 0;
            bakedMipCount = bakedCSArray != null ? bakedCSArray.mipmapCount : 0;
            csStorageBytes = csBytes;
            nohStorageBytes = nohBytes;
            lastBakeSummary = summary ?? string.Empty;
        }

        private void SynchronizeLayerEntries(IReadOnlyList<TerrainLayer> orderedLayers)
        {
            var priorEntries = new List<SolLandscapeLayerEntry>(layers);
            layers.Clear();

            for (int i = 0; i < orderedLayers.Count; i++)
            {
                TerrainLayer terrainLayer = orderedLayers[i];
                SolLandscapeLayerEntry existing = priorEntries.Find(entry => entry.terrainLayer == terrainLayer);
                layers.Add(existing ?? new SolLandscapeLayerEntry(terrainLayer));
            }
        }
    }
}
