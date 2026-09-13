using System;
using System.Collections.Generic;
using Sol.Environment;
using UnityEngine;

namespace Sol.Landscape
{
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class SolLandscapeDriver : MonoBehaviour
    {
        public Terrain landscapeTerrain;
        public SolLandscapeConfig config;
        static readonly HashSet<SolLandscapeDriver> active = new();
        SolLandscapeBinding binding;
        readonly Dictionary<Terrain, SolLandscapeBinding> legacyRecipients = new();
        readonly HashSet<Terrain> desiredRecipients = new();
        readonly List<Terrain> removedRecipients = new();
        public int LastPublishWriteCount { get; private set; }
        public int TotalGlobalWriteCount { get; private set; }
        public int LegacyRecipientCount => legacyRecipients.Count;
        public bool LastPublishRefused => binding?.LastPublishRefused ?? false;
        public string LastRefusalReason => binding?.LastRefusalReason;
        Terrain Primary => landscapeTerrain != null ? landscapeTerrain : Terrain.activeTerrain;
        SolLandscapeBinding Binding
        {
            get { binding ??= new SolLandscapeBinding(); binding.landscapeTerrain = Primary;
                binding.config = config; return binding; }
        }
        public void Invalidate()
        {
            Binding.Invalidate();
            foreach(var recipient in legacyRecipients.Values)recipient.Invalidate();
        }
        public bool TryValidateContract(out string reason) => Binding.TryValidateContract(out reason);
        void OnEnable() { active.Add(this); Invalidate(); }
        void OnValidate() => Invalidate();
        void LateUpdate() => Publish();
        public void Publish()
        {
            LastPublishWriteCount=0;
            var source=Primary;
            desiredRecipients.Clear();
            if(source!=null && !SolLandscapeGroup.Owns(source))
            {
                Binding.Publish();LastPublishWriteCount+=binding.LastPublishWriteCount;
                var sharedMaterial=source.materialTemplate;
                // The old shader contract was global: every terrain using this material consumed
                // the primary terrain's controls, layer palette and projection origin. Preserve
                // that appearance until the designer explicitly migrates to independent tile data.
                bool ambiguous=false;
                foreach(var other in active)
                    if(other!=this && other!=null && other.Primary!=null && other.Primary.gameObject.scene==source.gameObject.scene
                        && other.Primary.materialTemplate==sharedMaterial) { ambiguous=true;break; }
                if(!binding.LastPublishRefused && sharedMaterial!=null && sharedMaterial.shader!=null
                    && sharedMaterial.shader.name=="Sol/Terrain/Array Lit" && !ambiguous)
                    foreach(var terrain in Terrain.activeTerrains)
                        if(terrain!=source && terrain.gameObject.scene==source.gameObject.scene
                            && terrain.materialTemplate==sharedMaterial && !SolLandscapeGroup.Owns(terrain))desiredRecipients.Add(terrain);
            }
            else { binding?.Release();binding=null; }
            removedRecipients.Clear();
            foreach(var terrain in legacyRecipients.Keys)if(!desiredRecipients.Contains(terrain))removedRecipients.Add(terrain);
            foreach(var terrain in removedRecipients){legacyRecipients[terrain].Release();legacyRecipients.Remove(terrain);}
            foreach(var terrain in desiredRecipients)
            {
                if(!legacyRecipients.TryGetValue(terrain,out var recipient))
                    legacyRecipients.Add(terrain,recipient=new SolLandscapeBinding());
                recipient.landscapeTerrain=terrain;recipient.ContractSource=source;recipient.config=config;
                recipient.Publish();LastPublishWriteCount+=recipient.LastPublishWriteCount;
            }
            TotalGlobalWriteCount+=LastPublishWriteCount;
        }
        // Transfer ownership before a group snapshots the tile's existing property block.
        internal static void ReleaseTerrainBindings(Terrain terrain)
        {
            foreach(var driver in active)
            {
                if(driver==null)continue;
                if(driver.Primary==terrain){driver.binding?.Release();driver.binding=null;}
                if(driver.legacyRecipients.TryGetValue(terrain,out var recipient))
                {recipient.Release();driver.legacyRecipients.Remove(terrain);}
            }
        }
        void OnDisable()
        {
            active.Remove(this);binding?.Release();binding=null;
            foreach(var recipient in legacyRecipients.Values)recipient.Release();legacyRecipients.Clear();
        }
    }

    internal sealed class SolLandscapeBinding
    {
        private const int MaxShaderLayerCount = 8;

        [Tooltip("Terrain whose TerrainData supplies controls, layer transforms, and world bounds. Null uses the active terrain.")]
        public Terrain landscapeTerrain;

        [Tooltip("Accepted baked landscape configuration. Its TerrainData and ordered layers must match the live terrain.")]
        public SolLandscapeConfig config;

        private static readonly int Control0Id = Shader.PropertyToID("_Sol_LandscapeControl0");
        private static readonly int Control1Id = Shader.PropertyToID("_Sol_LandscapeControl1");
        private static readonly int ControlTexelSizeId = Shader.PropertyToID("_Sol_LandscapeControlTexelSize");
        private static readonly int CSId = Shader.PropertyToID("_Sol_LandscapeCS");
        private static readonly int NOHId = Shader.PropertyToID("_Sol_LandscapeNOH");
        private static readonly int LayerSTId = Shader.PropertyToID("_Sol_LandscapeLayerST");
        private static readonly int NormalScaleId = Shader.PropertyToID("_Sol_LandscapeNormalScale");
        private static readonly int LayerCountId = Shader.PropertyToID("_Sol_LandscapeLayerCount");
        private static readonly int TerrainOriginSizeId = Shader.PropertyToID("_Sol_LandscapeTerrainOriginSize");
        private static readonly int HeightTransitionId = Shader.PropertyToID("_Sol_LandscapeHeightTransition");
        private static readonly int LayerModesId = Shader.PropertyToID("_Sol_LandscapeLayerModes");
        private static readonly int AutoWeightsId = Shader.PropertyToID("_Sol_LandscapeAutoWeights");
        private static readonly int WeatherSnowSusceptibilitiesId = Shader.PropertyToID("_Sol_LandscapeWeatherSnowSusceptibilities");
        private static readonly int PermanentSnowSusceptibilitiesId = Shader.PropertyToID("_Sol_LandscapePermanentSnowSusceptibilities");
        private static readonly int AutoSlopeParamsId = Shader.PropertyToID("_Sol_LandscapeAutoSlopeParams");
        private static readonly int AutoSlopeCeilingParamsId = Shader.PropertyToID("_Sol_LandscapeAutoSlopeCeilingParams");
        private static readonly int StochasticId = Shader.PropertyToID("_Sol_LandscapeStochastic");
        private static readonly int TriplanarId = Shader.PropertyToID("_Sol_LandscapeTriplanar");
        private static readonly int TriplanarSharpnessId = Shader.PropertyToID("_Sol_LandscapeTriplanarSharpness");
        private static readonly int AutoAltitudeReferencesId = Shader.PropertyToID("_Sol_LandscapeAutoAltitudeReferences");
        private static readonly int AutoHeightParamsId = Shader.PropertyToID("_Sol_LandscapeAutoHeightParams");
        private static readonly int AutoCavityParamsId = Shader.PropertyToID("_Sol_LandscapeAutoCavityParams");
        private static readonly int SurfaceSnowCoverId = Shader.PropertyToID("_Sol_SurfaceSnowCover");
        private static readonly int SurfaceTemperatureId = Shader.PropertyToID("_Sol_SurfaceTemperature");
        private static readonly int SnowColorId = Shader.PropertyToID("_Sol_LandscapeSnowColor");
        private static readonly int SnowNormalId = Shader.PropertyToID("_Sol_LandscapeSnowNormal");
        private static readonly int SnowPackedId = Shader.PropertyToID("_Sol_LandscapeSnowPacked");
        private static readonly int SnowParamsId = Shader.PropertyToID("_Sol_LandscapeSnowParams");
        private static readonly int PermanentSnowSlopeSheddingRangeId = Shader.PropertyToID("_Sol_LandscapePermanentSnowSlopeSheddingRange");

        private Texture _lastControl0;
        private Texture _lastControl1;
        private Texture _lastCS;
        private Texture _lastNOH;
        private Vector4 _lastControlTexelSize = NaNVector;
        private Vector4 _lastTerrainOriginSize = NaNVector;
        private Vector4[] _lastLayerST;
        private float[] _lastNormalScale;
        private float[] _lastLayerModes;
        private float[] _lastAutoWeights;
        private float[] _lastWeatherSnowSusceptibilities;
        private float[] _lastPermanentSnowSusceptibilities;
        private Vector4[] _lastAutoSlopeParams;
        private Vector4[] _lastAutoSlopeCeilingParams;
        private float[] _lastStochastic;
        private float[] _lastTriplanar;
        private float _lastTriplanarSharpness = float.NaN;
        private float[] _lastAutoAltitudeReferences;
        private Vector4[] _lastAutoHeightParams;
        private Vector4[] _lastAutoCavityParams;
        private int _lastLayerCount = int.MinValue;
        private float _lastHeightTransition = float.NaN;
        private float _lastSurfaceSnowCover = float.NaN;
        private float _lastSurfaceTemperature = float.NaN;
        private Texture _lastSnowColor;
        private Texture _lastSnowNormal;
        private Texture _lastSnowPacked;
        private Vector4 _lastSnowParams = NaNVector;
        private Vector4 _lastPermanentSnowSlopeSheddingRange = NaNVector;

        private Vector4[] _workingLayerST;
        private float[] _workingNormalScale;
        private float[] _workingLayerModes;
        private float[] _workingAutoWeights;
        private float[] _workingWeatherSnowSusceptibilities;
        private float[] _workingPermanentSnowSusceptibilities;
        private Vector4[] _workingAutoSlopeParams;
        private Vector4[] _workingAutoSlopeCeilingParams;
        private float[] _workingStochastic;
        private float[] _workingTriplanar;
        private float[] _workingAutoAltitudeReferences;
        private Vector4[] _workingAutoHeightParams;
        private Vector4[] _workingAutoCavityParams;
        private string _lastLoggedRefusalReason;

        private static Vector4 NaNVector => new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);

        public int LastPublishWriteCount { get; private set; }
        public int TotalGlobalWriteCount { get; private set; }
        public bool LastPublishRefused { get; private set; }
        public string LastRefusalReason { get; private set; }

        public Terrain ContractSource;
        public SolLandscapeGroup Group;
        public SolLandscapePaintData PaintData;
        readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        MaterialPropertyBlock original;
        readonly Dictionary<int, Action<MaterialPropertyBlock>> restoreProperties = new();
        Terrain boundTerrain;
        int? lastExtras;
        public void Release()
        {
            if (boundTerrain != null && original != null)
            {
                boundTerrain.GetSplatMaterialPropertyBlock(block);
                foreach (var restore in restoreProperties.Values) restore(block);
                boundTerrain.SetSplatMaterialPropertyBlock(block);
            }
            restoreProperties.Clear();
            boundTerrain = null; original = null;
        }

        /// <summary>Forces every cached contract value to be written on the next valid publish.</summary>
        public void Invalidate()
        {
            lastExtras = null;
            _lastControl0 = null;
            _lastControl1 = null;
            _lastCS = null;
            _lastNOH = null;
            _lastControlTexelSize = NaNVector;
            _lastTerrainOriginSize = NaNVector;
            _lastLayerST = null;
            _lastNormalScale = null;
            _lastLayerModes = null;
            _lastAutoWeights = null;
            _lastWeatherSnowSusceptibilities = null;
            _lastPermanentSnowSusceptibilities = null;
            _lastAutoSlopeParams = null;
            _lastAutoSlopeCeilingParams = null;
            _lastStochastic = null;
            _lastTriplanar = null;
            _lastTriplanarSharpness = float.NaN;
            _lastAutoAltitudeReferences = null;
            _lastAutoHeightParams = null;
            _lastAutoCavityParams = null;
            _lastLayerCount = int.MinValue;
            _lastHeightTransition = float.NaN;
            _lastSurfaceSnowCover = float.NaN;
            _lastSurfaceTemperature = float.NaN;
            _lastSnowColor = null;
            _lastSnowNormal = null;
            _lastSnowPacked = null;
            _lastSnowParams = NaNVector;
            _lastPermanentSnowSlopeSheddingRange = NaNVector;
            _lastLoggedRefusalReason = null;
            LastPublishWriteCount = 0;
            LastPublishRefused = false;
            LastRefusalReason = null;
        }

        /// <summary>
        /// Validates the currently assigned terrain/config contract without publishing shader globals,
        /// invalidating cached state, or dirtying the TerrainData basemap.
        /// </summary>
        public bool TryValidateContract(out string refusalReason)
        {
            Terrain target = landscapeTerrain != null ? landscapeTerrain : Terrain.activeTerrain;
            return TryBuildContract(
                ContractSource != null ? ContractSource : target,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out refusalReason);
        }

        public void Publish()
        {
            LastPublishWriteCount = 0;
            LastPublishRefused = false;
            LastRefusalReason = null;

            Terrain target = landscapeTerrain != null ? landscapeTerrain : Terrain.activeTerrain;
            if (!TryBuildContract(
                    ContractSource != null ? ContractSource : target,
                    out Texture control0,
                    out Texture control1,
                    out Vector4 controlTexelSize,
                    out Texture2DArray cs,
                    out Texture2DArray noh,
                    out int layerCount,
                    out Vector4 terrainOriginSize,
                    out string refusalReason))
            {
                Refuse(refusalReason);
                return;
            }

            if (boundTerrain != target)
            {
                Release(); boundTerrain = target; original = new MaterialPropertyBlock();
                target.GetSplatMaterialPropertyBlock(original); Invalidate();
            }
            target.GetSplatMaterialPropertyBlock(block);
            _lastLoggedRefusalReason = null;
            PushTexture(Control0Id, control0, ref _lastControl0);
            PushTexture(Control1Id, control1, ref _lastControl1);
            PushVector(ControlTexelSizeId, controlTexelSize, ref _lastControlTexelSize);
            PushTexture(CSId, cs, ref _lastCS);
            PushTexture(NOHId, noh, ref _lastNOH);
            PushVectorArray(LayerSTId, _workingLayerST, ref _lastLayerST);
            PushFloatArray(NormalScaleId, _workingNormalScale, ref _lastNormalScale);
            PushFloatArray(LayerModesId, _workingLayerModes, ref _lastLayerModes);
            PushFloatArray(AutoWeightsId, _workingAutoWeights, ref _lastAutoWeights);
            PushFloatArray(WeatherSnowSusceptibilitiesId, _workingWeatherSnowSusceptibilities, ref _lastWeatherSnowSusceptibilities);
            PushFloatArray(PermanentSnowSusceptibilitiesId, _workingPermanentSnowSusceptibilities, ref _lastPermanentSnowSusceptibilities);
            PushVectorArray(AutoSlopeParamsId, _workingAutoSlopeParams, ref _lastAutoSlopeParams);
            PushVectorArray(AutoSlopeCeilingParamsId, _workingAutoSlopeCeilingParams, ref _lastAutoSlopeCeilingParams);
            PushFloatArray(StochasticId, _workingStochastic, ref _lastStochastic);
            PushFloatArray(TriplanarId, _workingTriplanar, ref _lastTriplanar);
            PushFloat(TriplanarSharpnessId, Mathf.Clamp(config.TriplanarSharpness, 1f, 16f), ref _lastTriplanarSharpness);
            PushFloatArray(AutoAltitudeReferencesId, _workingAutoAltitudeReferences, ref _lastAutoAltitudeReferences);
            PushVectorArray(AutoHeightParamsId, _workingAutoHeightParams, ref _lastAutoHeightParams);
            PushVectorArray(AutoCavityParamsId, _workingAutoCavityParams, ref _lastAutoCavityParams);
            PushInteger(LayerCountId, layerCount, ref _lastLayerCount);
            PushVector(TerrainOriginSizeId, terrainOriginSize, ref _lastTerrainOriginSize);
            PushFloat(HeightTransitionId, config.HeightTransition, ref _lastHeightTransition);
            PushTexture(SnowColorId, config.SnowColorTexture, ref _lastSnowColor);
            PushTexture(SnowNormalId, config.SnowNormalTexture, ref _lastSnowNormal);
            PushTexture(SnowPackedId, config.SnowPackedTexture, ref _lastSnowPacked);
            Vector2 permanentSnowRange = config.PermanentSnowAltitudeRange;
            PushVector(
                SnowParamsId,
                new Vector4(
                    permanentSnowRange.x,
                    permanentSnowRange.y,
                    1f / Mathf.Max(config.SnowTileSize, 0.01f),
                    Mathf.Max(config.SnowNormalScale, 0f)),
                ref _lastSnowParams);
            Vector2 permanentSnowSlopeRange = config.PermanentSnowSlopeSheddingRange;
            PushVector(
                PermanentSnowSlopeSheddingRangeId,
                new Vector4(permanentSnowSlopeRange.x, permanentSnowSlopeRange.y, 0f, 0f),
                ref _lastPermanentSnowSlopeSheddingRange);
            bool staticLandscapeContractChanged = LastPublishWriteCount > 0;

            // Unlike the static landscape data, these integrated climate values can move
            // every simulation frame. Dirty checks still let paused/static scenes settle.
            SolSurfaceConditionState surface = SolEnvironmentWorld.ResolveState().Surface;
            PushFloat(SurfaceSnowCoverId, Group != null && Group.PreviewSnow.HasValue ? Group.PreviewSnow.Value : surface.SnowCover, ref _lastSurfaceSnowCover);
            PushFloat(SurfaceTemperatureId, surface.TemperatureCelsius, ref _lastSurfaceTemperature);

            // Basemap generation runs after the globals above have become valid. Marking it dirty
            // before publication can bake an all-zero control/array contract in edit mode. Dynamic
            // climate writes do not dirty it: otherwise an active simulation would rebuild it every frame.
            PublishExtras();
            if (LastPublishWriteCount > 0) target.SetSplatMaterialPropertyBlock(block);
            if (staticLandscapeContractChanged && Group == null) target.terrainData.SetBaseMapDirty();
        }

        private bool TryBuildContract(
            Terrain target,
            out Texture control0,
            out Texture control1,
            out Vector4 controlTexelSize,
            out Texture2DArray cs,
            out Texture2DArray noh,
            out int layerCount,
            out Vector4 terrainOriginSize,
            out string refusalReason)
        {
            control0 = null;
            control1 = null;
            controlTexelSize = default;
            cs = null;
            noh = null;
            layerCount = 0;
            terrainOriginSize = default;
            refusalReason = null;

            if (target == null)
                return Refusal("No landscape terrain is assigned or active.", out refusalReason);

            TerrainData terrainData = target.terrainData;
            if (terrainData == null)
                return Refusal("The landscape terrain has no TerrainData.", out refusalReason);

            if (config == null)
                return Refusal("No SolLandscapeConfig is assigned.", out refusalReason);

            if (!(config is SolLandscapeProfile) && config.TerrainData != terrainData)
                return Refusal("Config TerrainData differs from the live TerrainData.", out refusalReason);

            TerrainLayer[] terrainLayers = terrainData.terrainLayers;
            if (config.Layers.Count != terrainLayers.Length)
            {
                return Refusal(
                    $"Config layer count {config.Layers.Count} differs from live layer count {terrainLayers.Length}.",
                    out refusalReason);
            }

            layerCount = terrainLayers.Length;
            if (layerCount <= 0 || layerCount > MaxShaderLayerCount)
            {
                return Refusal(
                    $"Live layer count {layerCount} is outside the shader contract range 1-{MaxShaderLayerCount}.",
                    out refusalReason);
            }

            for (int index = 0; index < layerCount; index++)
            {
                SolLandscapeLayerEntry entry = config.Layers[index];
                if (entry == null || entry.terrainLayer != terrainLayers[index])
                    return Refusal($"Config layer order differs at slice {index}.", out refusalReason);
                if (entry.mode != SolLandscapeLayerMode.Manual
                    && entry.mode != SolLandscapeLayerMode.Auto)
                    return Refusal($"Layer {index} has an invalid auto-material mode.", out refusalReason);
                if (entry.altitudeReference != SolLandscapeAltitudeReference.AbsoluteWorldY
                    && entry.altitudeReference != SolLandscapeAltitudeReference.RelativeToWaterLevel)
                    return Refusal($"Layer {index} has an invalid altitude reference.", out refusalReason);
                if (float.IsNaN(entry.autoWeight) || float.IsInfinity(entry.autoWeight))
                    return Refusal($"Layer {index} has a non-finite auto weight.", out refusalReason);
                if (!IsFinite(entry.weatherSnowSusceptibility) || !IsFinite(entry.permanentSnowSusceptibility))
                    return Refusal($"Layer {index} has a non-finite weather or permanent Snow susceptibility.", out refusalReason);
                if (!IsFinite(entry.slopeCenter)
                    || !IsFinite(entry.slopeContrast)
                    || !IsFinite(entry.slopeBias)
                    || !IsFinite(entry.slopeInfluence)
                    || !IsFinite(entry.heightRange.x)
                    || !IsFinite(entry.heightRange.y)
                    || !IsFinite(entry.heightBias)
                    || !IsFinite(entry.heightInfluence)
                    || !IsFinite(entry.cavityScale)
                    || !IsFinite(entry.cavityInfluence))
                    return Refusal($"Layer {index} has a non-finite auto-rule parameter.", out refusalReason);
                if (entry.slopeContrast <= 0f)
                    return Refusal($"Layer {index} has a non-positive slope transition width.", out refusalReason);
                if (!IsFinite(entry.slopeCeiling) || !IsFinite(entry.slopeCeilingFeather))
                    return Refusal($"Layer {index} has a non-finite slope ceiling parameter.", out refusalReason);
                if (entry.heightRange.y <= entry.heightRange.x)
                    return Refusal($"Layer {index} has an invalid world-Y response range.", out refusalReason);
                if (entry.cavityScale < 0f)
                    return Refusal($"Layer {index} has a negative cavity scale.", out refusalReason);
                if (!IsFinite(entry.tint.r) || !IsFinite(entry.tint.g) || !IsFinite(entry.tint.b) || !IsFinite(entry.tintStrength)
                    || !IsFinite(entry.smoothnessRemap.x) || !IsFinite(entry.smoothnessRemap.y) || !IsFinite(entry.occlusionStrength)
                    || !IsFinite(entry.slopeRange.x) || !IsFinite(entry.slopeRange.y) || !IsFinite(entry.slopeFeather) || !IsFinite(entry.altitudeFeather)
                    || !IsFinite(entry.macroVariation) || !IsFinite(entry.mesoVariation) || !IsFinite(entry.normalStrength))
                    return Refusal($"Layer {index} has a non-finite designer setting.", out refusalReason);
                if (config is SolLandscapeProfile palette && palette.SliceFor(index) < 0)
                    return Refusal($"Layer {index} is not in the baked artwork. Rebuild this profile's textures.", out refusalReason);
            }

            cs = config.CSArray;
            noh = config.NOHArray;
            if (cs == null || noh == null)
                return Refusal("The accepted CSNOH arrays are not both assigned.", out refusalReason);
            if (!(config is SolLandscapeProfile) && (config.SnowColorTexture == null || config.SnowNormalTexture == null || config.SnowPackedTexture == null))
                return Refusal("The Snow overlay color, normal, and packed textures are not all assigned.", out refusalReason);
            Vector2 permanentSnowRange = config.PermanentSnowAltitudeRange;
            if (!IsFinite(permanentSnowRange.x) || !IsFinite(permanentSnowRange.y)
                || permanentSnowRange.y <= permanentSnowRange.x)
                return Refusal("The permanent Snow altitude range is invalid.", out refusalReason);
            Vector2 permanentSnowSlopeRange = config.PermanentSnowSlopeSheddingRange;
            if (!IsFinite(permanentSnowSlopeRange.x) || !IsFinite(permanentSnowSlopeRange.y)
                || permanentSnowSlopeRange.x < 0f || permanentSnowSlopeRange.y > 90f
                || permanentSnowSlopeRange.y <= permanentSnowSlopeRange.x)
                return Refusal("The permanent Snow slope-shedding range is invalid.", out refusalReason);
            if (!IsFinite(config.SnowTileSize) || config.SnowTileSize <= 0f
                || !IsFinite(config.SnowNormalScale) || config.SnowNormalScale < 0f)
                return Refusal("The Snow overlay tile size or normal scale is invalid.", out refusalReason);
            if (cs.depth < layerCount || noh.depth < layerCount)
                return Refusal("A CSNOH array has fewer slices than the live layer count.", out refusalReason);

            if (terrainData.alphamapTextureCount < (layerCount + 3) / 4)
                return Refusal("Terrain controls do not contain all palette layers.", out refusalReason);
            control0 = terrainData.GetAlphamapTexture(0);
            control1 = layerCount > 4 ? terrainData.GetAlphamapTexture(1) : Texture2D.blackTexture;
            if (control0 == null || control1 == null)
                return Refusal("A required alphamap texture is null.", out refusalReason);

            controlTexelSize = new Vector4(
                1f / control0.width,
                1f / control0.height,
                control0.width,
                control0.height);

            EnsureWorkingArrays(MaxShaderLayerCount);
            for (int i = 0; i < MaxShaderLayerCount; i++) { _workingLayerModes[i] = 0; _workingAutoWeights[i] = 0; }
            Vector3 terrainSize = terrainData.size;
            for (int index = 0; index < layerCount; index++)
            {
                SolLandscapeLayerEntry entry = config.Layers[index];
                TerrainLayer layer = terrainLayers[index];
                Vector2 tileSize = entry.liveSurfaceSettings ? entry.textureSize : layer.tileSize;
                if (Mathf.Abs(tileSize.x) < 0.0001f || Mathf.Abs(tileSize.y) < 0.0001f)
                    return Refusal($"Layer {index} has a zero tile-size component.", out refusalReason);

                Vector2 tileOffset = entry.liveSurfaceSettings ? entry.textureOffset : layer.tileOffset;
                _workingLayerST[index] = new Vector4(
                    terrainSize.x / tileSize.x,
                    terrainSize.z / tileSize.y,
                    (tileOffset.x + (Group != null ? target.transform.position.x - Group.projectionOrigin.x : 0)) / tileSize.x,
                    (tileOffset.y + (Group != null ? target.transform.position.z - Group.projectionOrigin.y : 0)) / tileSize.y);
                _workingNormalScale[index] = entry.liveSurfaceSettings ? entry.normalStrength : layer.normalScale;
                _workingLayerModes[index] = entry.mode == SolLandscapeLayerMode.Auto ? 1f : 0f;
                _workingAutoWeights[index] = Mathf.Clamp01(entry.autoWeight);
                _workingWeatherSnowSusceptibilities[index] = Mathf.Clamp01(entry.weatherSnowSusceptibility);
                _workingPermanentSnowSusceptibilities[index] = Mathf.Clamp01(entry.permanentSnowSusceptibility);
                _workingAutoSlopeParams[index] = new Vector4(
                    Mathf.Clamp(entry.slopeCenter, 0f, 90f),
                    Mathf.Max(entry.slopeContrast, 0.01f),
                    Mathf.Clamp(entry.slopeBias, -1f, 1f),
                    Mathf.Clamp(entry.slopeInfluence, -1f, 1f));
                // A ceiling of zero means the field was never authored: a config saved before the
                // ceiling existed deserialises its entries with zeroed fields. Read that as disabled,
                // never as "shed at every slope", which would silently erase the layer everywhere.
                _workingAutoSlopeCeilingParams[index] = new Vector4(
                    entry.slopeCeiling > 0f ? Mathf.Min(entry.slopeCeiling, 90f) : 90f,
                    Mathf.Max(entry.slopeCeilingFeather, 0.01f),
                    0f,
                    0f);
                _workingStochastic[index] = entry.stochasticTiling ? 1f : 0f;
                _workingTriplanar[index] = entry.triplanarProjection ? 1f : 0f;
                _workingAutoAltitudeReferences[index] =
                    entry.altitudeReference == SolLandscapeAltitudeReference.RelativeToWaterLevel ? 1f : 0f;
                _workingAutoHeightParams[index] = new Vector4(
                    entry.heightRange.x,
                    entry.heightRange.y,
                    Mathf.Clamp(entry.heightBias, -1f, 1f),
                    Mathf.Clamp(entry.heightInfluence, -1f, 1f));
                _workingAutoCavityParams[index] = new Vector4(
                    entry.cavityScale,
                    Mathf.Clamp(entry.cavityInfluence, -1f, 1f),
                    0f,
                    0f);
            }

            Vector3 origin = target.transform.position;
            terrainOriginSize = new Vector4(
                origin.x,
                origin.z,
                1f / Mathf.Max(terrainSize.x, 0.001f),
                1f / Mathf.Max(terrainSize.z, 0.001f));
            return true;
        }

        void PublishExtras()
        {
            var profile = config as SolLandscapeProfile;
            var hash = new HashCode(); hash.Add(config.GetInstanceID()); hash.Add(config.Layers.Count);
            hash.Add(PaintData != null ? PaintData.GetInstanceID() : 0); hash.Add(PaintData != null ? PaintData.Revision : -1);
            hash.Add(Group != null ? Group.PreviewRevision : 0);
            hash.Add(Group != null ? Group.projectionOrigin : Vector2.zero);
            if (profile != null) { hash.Add(profile.FallbackIndex); hash.Add(profile.preserveLegacyFallback); hash.Add(profile.colourVariation); hash.Add(profile.macroScale); hash.Add(profile.mesoScale); }
            for (int i = 0; i < config.Layers.Count; i++)
            {
                var e = config.Layers[i]; hash.Add(e.tint); hash.Add(e.tintStrength); hash.Add(e.adjustSmoothness); hash.Add(e.smoothnessRemap);
                hash.Add(e.adjustOcclusion); hash.Add(e.occlusionStrength); hash.Add(e.ruleModel); hash.Add(e.slopeRange); hash.Add(e.slopeFeather);
                hash.Add(e.altitudeFeather); hash.Add(e.useAltitudeRange); hash.Add(e.macroVariation); hash.Add(e.mesoVariation); hash.Add(e.sandResponse);
                hash.Add(profile != null ? profile.SliceFor(i) : i);
                hash.Add(e.ProtectAutomaticCoverage);
            }
            int signature = hash.ToHashCode();
            if (signature == lastExtras) return;
            lastExtras = signature;
            foreach (var name in new[] { "_Sol_LandscapeLayerTint", "_Sol_LandscapeLayerSurface", "_Sol_LandscapeRuleRanges", "_Sol_LandscapeRuleSettings", "_Sol_LandscapeLayerVariation" }) Remember(Shader.PropertyToID(name), 4);
            Remember(Shader.PropertyToID("_Sol_LandscapeSandLayers"), 5); Remember(Shader.PropertyToID("_Sol_LandscapeSliceIndices"), 5);
            foreach (var name in new[] { "_Sol_LandscapeVariationScales", "_Sol_LandscapeGroupOrigin", "_Sol_LandscapeWetPreview", "_Sol_LandscapePaintFlags" }) Remember(Shader.PropertyToID(name), 1);
            foreach (var name in new[] { "_Sol_LandscapeGrouped", "_Sol_LandscapeFallback", "_Sol_LandscapeUseFallback", "_Sol_LandscapeDebugMode", "_Sol_LandscapeWeightDebugLayer" }) Remember(Shader.PropertyToID(name), 3);
            for (int i = 0; i < 6; i++) Remember(Shader.PropertyToID("_Sol_LandscapePaint" + i), 0);
            var tint = new Vector4[8]; var surface = new Vector4[8]; var ranges = new Vector4[8];
            var settings = new Vector4[8]; var variation = new Vector4[8]; var sand = new float[8];
            var slices = new float[8];
            for (int i = 0; i < config.Layers.Count; i++)
            {
                var e = config.Layers[i];
                slices[i] = profile != null ? Mathf.Max(0, profile.SliceFor(i)) : i;
                tint[i] = new Vector4(e.tint.r, e.tint.g, e.tint.b, Mathf.Clamp01(e.tintStrength));
                surface[i] = new Vector4(e.adjustSmoothness ? e.smoothnessRemap.x : 0,
                    e.adjustSmoothness ? e.smoothnessRemap.y : 1, e.adjustOcclusion ? Mathf.Clamp(e.occlusionStrength, 0, 2) : 1, 0);
                ranges[i] = new Vector4(e.slopeRange.x, e.slopeRange.y, Mathf.Max(.01f,e.slopeFeather), Mathf.Max(.01f,e.altitudeFeather));
                settings[i] = new Vector4(e.ruleModel == SolLandscapeRuleModel.Ranges ? 1 : 0, e.useAltitudeRange ? 1 : 0, e.ProtectAutomaticCoverage ? 1 : 0, 0);
                variation[i] = new Vector4(e.macroVariation, e.mesoVariation, 0, 0); sand[i] = e.sandResponse ? 1 : 0;
            }
            block.SetVectorArray("_Sol_LandscapeLayerTint", tint); block.SetVectorArray("_Sol_LandscapeLayerSurface", surface);
            block.SetVectorArray("_Sol_LandscapeRuleRanges", ranges); block.SetVectorArray("_Sol_LandscapeRuleSettings", settings);
            block.SetVectorArray("_Sol_LandscapeLayerVariation", variation); block.SetFloatArray("_Sol_LandscapeSandLayers", sand);
            block.SetFloatArray("_Sol_LandscapeSliceIndices", slices);
            block.SetVector("_Sol_LandscapeVariationScales", new Vector4(profile != null ? profile.macroScale : 120, profile != null ? profile.mesoScale : 20, profile != null && profile.colourVariation ? 1 : 0, 0));
            block.SetFloat("_Sol_LandscapeGrouped", Group != null ? 1 : 0);
            block.SetVector("_Sol_LandscapeGroupOrigin",Group!=null?new Vector4(Group.projectionOrigin.x,Group.projectionOrigin.y,0,0):Vector4.zero);
            block.SetFloat("_Sol_LandscapeFallback", profile != null ? profile.FallbackIndex : 0);
            block.SetFloat("_Sol_LandscapeUseFallback", profile != null && !profile.preserveLegacyFallback ? 1 : 0);
            block.SetVector("_Sol_LandscapeWetPreview", new Vector4(Group != null && Group.PreviewWetness.HasValue ? 1 : 0, Group != null ? Group.PreviewWetness ?? 0 : 0, 0, 0));
            if (Group != null)
            {
                block.SetFloat("_Sol_LandscapeDebugMode", Group.DebugMode);
                block.SetFloat("_Sol_LandscapeWeightDebugLayer", Group.DebugLayer);
            }
            for (int i = 0; i < 6; i++) block.SetTexture("_Sol_LandscapePaint" + i, PaintData != null ? PaintData.GetTexture(i) : Texture2D.blackTexture);
            block.SetVector("_Sol_LandscapePaintFlags", new Vector4(PaintData != null && PaintData.HasOverrides ? 1 : 0, PaintData != null && PaintData.HasExclusions ? 1 : 0, PaintData != null ? PaintData.Resolution : 512, PaintData != null && PaintData.HasRemovals ? 1 : 0));
            CountWrite();
        }

        private static bool Refusal(string reason, out string refusalReason)
        {
            refusalReason = reason;
            return false;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private void Refuse(string reason)
        {
            LastPublishRefused = true;
            LastRefusalReason = reason;
            if (string.Equals(_lastLoggedRefusalReason, reason, StringComparison.Ordinal))
                return;

            _lastLoggedRefusalReason = reason;
            Debug.LogError($"[SolLandscapeDriver] Refusing to publish: {reason}");
        }

        private void EnsureWorkingArrays(int layerCount)
        {
            if (_workingLayerST == null || _workingLayerST.Length != layerCount)
                _workingLayerST = new Vector4[layerCount];
            if (_workingNormalScale == null || _workingNormalScale.Length != layerCount)
                _workingNormalScale = new float[layerCount];
            if (_workingLayerModes == null || _workingLayerModes.Length != layerCount)
                _workingLayerModes = new float[layerCount];
            if (_workingAutoWeights == null || _workingAutoWeights.Length != layerCount)
                _workingAutoWeights = new float[layerCount];
            if (_workingWeatherSnowSusceptibilities == null || _workingWeatherSnowSusceptibilities.Length != layerCount)
                _workingWeatherSnowSusceptibilities = new float[layerCount];
            if (_workingPermanentSnowSusceptibilities == null || _workingPermanentSnowSusceptibilities.Length != layerCount)
                _workingPermanentSnowSusceptibilities = new float[layerCount];
            if (_workingAutoSlopeParams == null || _workingAutoSlopeParams.Length != layerCount)
                _workingAutoSlopeParams = new Vector4[layerCount];
            if (_workingAutoSlopeCeilingParams == null || _workingAutoSlopeCeilingParams.Length != layerCount)
                _workingAutoSlopeCeilingParams = new Vector4[layerCount];
            if (_workingStochastic == null || _workingStochastic.Length != layerCount)
                _workingStochastic = new float[layerCount];
            if (_workingTriplanar == null || _workingTriplanar.Length != layerCount)
                _workingTriplanar = new float[layerCount];
            if (_workingAutoAltitudeReferences == null || _workingAutoAltitudeReferences.Length != layerCount)
                _workingAutoAltitudeReferences = new float[layerCount];
            if (_workingAutoHeightParams == null || _workingAutoHeightParams.Length != layerCount)
                _workingAutoHeightParams = new Vector4[layerCount];
            if (_workingAutoCavityParams == null || _workingAutoCavityParams.Length != layerCount)
                _workingAutoCavityParams = new Vector4[layerCount];
        }

        private void PushTexture(int propertyId, Texture value, ref Texture lastValue)
        {
            if (lastValue == value)
                return;
            Remember(propertyId, 0); block.SetTexture(propertyId, value);
            lastValue = value;
            CountWrite();
        }

        private void PushVector(int propertyId, Vector4 value, ref Vector4 lastValue)
        {
            if (lastValue == value)
                return;
            Remember(propertyId, 1); block.SetVector(propertyId, value);
            lastValue = value;
            CountWrite();
        }

        private void PushInteger(int propertyId, int value, ref int lastValue)
        {
            if (lastValue == value)
                return;
            Remember(propertyId, 2); block.SetInt(propertyId, value);
            lastValue = value;
            CountWrite();
        }

        private void PushFloat(int propertyId, float value, ref float lastValue)
        {
            if (Mathf.Approximately(lastValue, value))
                return;
            Remember(propertyId, 3); block.SetFloat(propertyId, value);
            lastValue = value;
            CountWrite();
        }

        private void PushVectorArray(int propertyId, Vector4[] values, ref Vector4[] lastValues)
        {
            if (ArraysEqual(values, lastValues))
                return;
            Remember(propertyId, 4); block.SetVectorArray(propertyId, values);
            lastValues = (Vector4[])values.Clone();
            CountWrite();
        }

        private void PushFloatArray(int propertyId, float[] values, ref float[] lastValues)
        {
            if (ArraysEqual(values, lastValues))
                return;
            Remember(propertyId, 5); block.SetFloatArray(propertyId, values);
            lastValues = (float[])values.Clone();
            CountWrite();
        }

        private static bool ArraysEqual(Vector4[] left, Vector4[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                    return false;
            }
            return true;
        }

        void Remember(int id, int type)
        {
            if (original == null || restoreProperties.ContainsKey(id)) return;
            switch (type)
            {
                case 0: var texture = original.GetTexture(id); restoreProperties[id] = b => b.SetTexture(id, texture != null ? texture : Texture2D.blackTexture); break;
                case 1: var vector = original.GetVector(id); restoreProperties[id] = b => b.SetVector(id, vector); break;
                case 2: var integer = original.GetInt(id); restoreProperties[id] = b => b.SetInt(id, integer); break;
                case 3: var scalar = original.GetFloat(id); restoreProperties[id] = b => b.SetFloat(id, scalar); break;
                case 4: var vectors = original.GetVectorArray(id) ?? new Vector4[8]; restoreProperties[id] = b => b.SetVectorArray(id, vectors); break;
                case 5: var scalars = original.GetFloatArray(id) ?? new float[8]; restoreProperties[id] = b => b.SetFloatArray(id, scalars); break;
            }
        }

        private static bool ArraysEqual(float[] left, float[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (int index = 0; index < left.Length; index++)
            {
                if (!Mathf.Approximately(left[index], right[index]))
                    return false;
            }
            return true;
        }

        private void CountWrite()
        {
            LastPublishWriteCount++;
            TotalGlobalWriteCount++;
        }
    }
}
