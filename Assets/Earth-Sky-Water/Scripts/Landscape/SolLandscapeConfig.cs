using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Sol.Landscape
{
    public enum SolLandscapeLayerMode : byte
    {
        Manual,
        Auto,
    }

    public enum SolLandscapeAltitudeReference : byte
    {
        AbsoluteWorldY,
        RelativeToWaterLevel,
    }

    [Serializable]
    public sealed class SolLandscapeLayerEntry
    {
        [Tooltip("The array slice index is this entry's position in the list.")]
        public TerrainLayer terrainLayer;

        [Tooltip("Break up visible repetition on this layer by sampling it through a randomised per-cell rotation. Costs three texture samples per array instead of one, so enable it only on layers whose tiling actually reads. Requires Stochastic Tiling on the landscape material.")]
        public bool stochasticTiling;

        [Tooltip("Project this layer from all three world axes instead of straight down, so it stops smearing on cliffs. Flat ground still resolves to the single top-down projection and costs nothing extra; only steep faces pay for a second or third sample. Requires Triplanar Projection on the landscape material.")]
        public bool triplanarProjection;

        [Header("Auto-material contract")]
        [Tooltip("Manual keeps this layer's painted alphamap channel authoritative. Auto drives it from the rules below, live in the shader, sharing whatever weight the Manual layers do not claim.")]
        public SolLandscapeLayerMode mode = SolLandscapeLayerMode.Auto;

        [Tooltip("Base multiplier for this layer's evaluated procedural claim.")]
        [Range(0f, 1f)] public float autoWeight = 1f;

        [Tooltip("How readily fresh weather Snow adheres to this layer. This never changes terrain weights or top-K selection.")]
        [FormerlySerializedAs("snowSusceptibility")]
        [Range(0f, 1f)] public float weatherSnowSusceptibility = 1f;

        [Tooltip("How readily accumulated permanent Snow pack covers this layer. This is separate from fresh-weather adhesion.")]
        [Range(0f, 1f)] public float permanentSnowSusceptibility = 1f;

        [Header("Rule authoring")]
        [Tooltip("Slope response midpoint in degrees, evaluated from the geometric terrain normal.")]
        [Range(0f, 90f)] public float slopeCenter = 35f;
        [Tooltip("Full width in degrees of the polynomial transition around Slope Center.")]
        [Min(0.01f)] public float slopeContrast = 8f;
        [Tooltip("Slope in degrees above which this layer sheds, the way snow does. 90 disables it. Use this to turn a steep-favouring layer into a mid-slope band so it does not run all the way up the cliffs.")]
        [Range(0f, 90f)] public float slopeCeiling = 90f;
        [Tooltip("Degrees over which the layer fades out above Slope Ceiling.")]
        [Min(0.01f)] public float slopeCeilingFeather = 10f;
        [Tooltip("Quadratic response bias. Positive ramps early then plateaus; negative delays the ramp.")]
        [Range(-1f, 1f)] public float slopeBias;
        [Tooltip("Positive favours steep ground, negative favours flat ground, and zero leaves the base claim unchanged.")]
        [Range(-1f, 1f)] public float slopeInfluence = 1f;

        [Tooltip("Selects whether Height Range is evaluated in absolute world Y or relative to the live global water level.")]
        public SolLandscapeAltitudeReference altitudeReference = SolLandscapeAltitudeReference.AbsoluteWorldY;
        [Tooltip("Altitude values mapping the height response from zero to one, in the selected reference space.")]
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

    /// <summary>
    /// Records what a baked array slice was produced from, so the inspector can tell the artist when
    /// the arrays no longer match their sources. The dependency hash covers content and importer
    /// settings, which a GUID comparison alone would miss when a texture is edited in place.
    /// </summary>
    [Serializable]
    public sealed class SolLandscapeBakeFingerprint
    {
        [SerializeField] private string terrainLayerGuid;
        [SerializeField] private string diffuseTextureGuid;
        [SerializeField] private string normalTextureGuid;
        [SerializeField] private string maskTextureGuid;
        [SerializeField] private string dependencyHash;

        public string TerrainLayerGuid => terrainLayerGuid;
        public string DiffuseTextureGuid => diffuseTextureGuid;
        public string NormalTextureGuid => normalTextureGuid;
        public string MaskTextureGuid => maskTextureGuid;
        public string DependencyHash => dependencyHash;

        public SolLandscapeBakeFingerprint(
            string terrainLayerGuid,
            string diffuseTextureGuid,
            string normalTextureGuid,
            string maskTextureGuid,
            string dependencyHash)
        {
            this.terrainLayerGuid = terrainLayerGuid ?? string.Empty;
            this.diffuseTextureGuid = diffuseTextureGuid ?? string.Empty;
            this.normalTextureGuid = normalTextureGuid ?? string.Empty;
            this.maskTextureGuid = maskTextureGuid ?? string.Empty;
            this.dependencyHash = dependencyHash ?? string.Empty;
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

        [Header("Triplanar projection")]
        [Tooltip("How sharply the three projections separate. Higher keeps the top-down projection dominant further up a slope, so fewer pixels pay for a second sample, at the cost of a tighter transition. 4 to 8 is the useful range.")]
        [Range(1f, 16f)] [SerializeField] private float triplanarSharpness = 6f;

        [Header("Height blend")]
        [Tooltip("Transition width for height-based layer blending. The 0.56 default preserves the authored legacy-material intent.")]
        [Min(0f)] [SerializeField] private float heightTransition = 0.56f;

        [Header("Snow overlay")]
        [SerializeField] private Texture2D snowColorTexture;
        [SerializeField] private Texture2D snowNormalTexture;
        [SerializeField] private Texture2D snowPackedTexture;
        [Tooltip("Absolute world-Y range over which permanent mountaintop Snow rises from zero to full coverage.")]
        [SerializeField] private Vector2 permanentSnowAltitudeRange = new Vector2(60f, 105f);
        [Tooltip("Slope range in degrees over which accumulated permanent Snow sheds from full retention to zero. Weather Snow keeps its separate, more aggressive shedding range.")]
        [SerializeField] private Vector2 permanentSnowSlopeSheddingRange = new Vector2(50f, 80f);
        [Tooltip("World-space Snow texture repeat size in metres.")]
        [Min(0.01f)] [SerializeField] private float snowTileSize = 4f;
        [Tooltip("Tangent-space strength of the sampled Snow normal.")]
        [Min(0f)] [SerializeField] private float snowNormalScale = 1f;

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
        public float TriplanarSharpness => triplanarSharpness;
        public Texture2D SnowColorTexture => snowColorTexture;
        public Texture2D SnowNormalTexture => snowNormalTexture;
        public Texture2D SnowPackedTexture => snowPackedTexture;
        public Vector2 PermanentSnowAltitudeRange => permanentSnowAltitudeRange;
        public Vector2 PermanentSnowSlopeSheddingRange => permanentSnowSlopeSheddingRange;
        public float SnowTileSize => snowTileSize;
        public float SnowNormalScale => snowNormalScale;
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
