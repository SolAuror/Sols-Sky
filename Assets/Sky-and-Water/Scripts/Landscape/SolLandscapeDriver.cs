using System;
using Sol.Environment;
using UnityEngine;

namespace Sol.Landscape
{
    /// <summary>
    /// Publishes the terrain/control/array contract consumed by Sol/Terrain/Array Lit.
    /// The landscape data is normally static, so every global is dirty-checked and a
    /// stable second LateUpdate performs no shader-global writes.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class SolLandscapeDriver : MonoBehaviour
    {
        private const int MaxShaderLayerCount = 16;

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
        private float[] _workingAutoAltitudeReferences;
        private Vector4[] _workingAutoHeightParams;
        private Vector4[] _workingAutoCavityParams;
        private string _lastLoggedRefusalReason;

        private static Vector4 NaNVector => new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);

        public int LastPublishWriteCount { get; private set; }
        public int TotalGlobalWriteCount { get; private set; }
        public bool LastPublishRefused { get; private set; }
        public string LastRefusalReason { get; private set; }

        private void OnEnable() => Invalidate();

        private void OnValidate() => Invalidate();

        private void LateUpdate() => Publish();

        /// <summary>Forces every cached contract value to be written on the next valid publish.</summary>
        public void Invalidate()
        {
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

        private void Publish()
        {
            LastPublishWriteCount = 0;
            LastPublishRefused = false;
            LastRefusalReason = null;

            Terrain target = landscapeTerrain != null ? landscapeTerrain : Terrain.activeTerrain;
            if (!TryBuildContract(
                    target,
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
            PushFloat(SurfaceSnowCoverId, surface.SnowCover, ref _lastSurfaceSnowCover);
            PushFloat(SurfaceTemperatureId, surface.TemperatureCelsius, ref _lastSurfaceTemperature);

            // Basemap generation runs after the globals above have become valid. Marking it dirty
            // before publication can bake an all-zero control/array contract in edit mode. Dynamic
            // climate writes do not dirty it: otherwise an active simulation would rebuild it every frame.
            if (staticLandscapeContractChanged)
                target.terrainData.SetBaseMapDirty();
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

            if (config.TerrainData != terrainData)
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
                if (entry.heightRange.y <= entry.heightRange.x)
                    return Refusal($"Layer {index} has an invalid world-Y response range.", out refusalReason);
                if (entry.cavityScale < 0f)
                    return Refusal($"Layer {index} has a negative cavity scale.", out refusalReason);
            }

            cs = config.CSArray;
            noh = config.NOHArray;
            if (cs == null || noh == null)
                return Refusal("The accepted CSNOH arrays are not both assigned.", out refusalReason);
            if (config.SnowColorTexture == null || config.SnowNormalTexture == null || config.SnowPackedTexture == null)
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

            if (terrainData.alphamapTextureCount < 2)
                return Refusal("The six-layer contract requires two alphamap textures.", out refusalReason);

            control0 = terrainData.GetAlphamapTexture(0);
            control1 = terrainData.GetAlphamapTexture(1);
            if (control0 == null || control1 == null)
                return Refusal("A required alphamap texture is null.", out refusalReason);
            if (control0.width != control1.width || control0.height != control1.height)
                return Refusal("The two alphamap textures have different dimensions.", out refusalReason);

            controlTexelSize = new Vector4(
                1f / control0.width,
                1f / control0.height,
                control0.width,
                control0.height);

            EnsureWorkingArrays(layerCount);
            Vector3 terrainSize = terrainData.size;
            for (int index = 0; index < layerCount; index++)
            {
                SolLandscapeLayerEntry entry = config.Layers[index];
                TerrainLayer layer = terrainLayers[index];
                Vector2 tileSize = layer.tileSize;
                if (Mathf.Abs(tileSize.x) < 0.0001f || Mathf.Abs(tileSize.y) < 0.0001f)
                    return Refusal($"Layer {index} has a zero tile-size component.", out refusalReason);

                Vector2 tileOffset = layer.tileOffset;
                _workingLayerST[index] = new Vector4(
                    terrainSize.x / tileSize.x,
                    terrainSize.z / tileSize.y,
                    tileOffset.x / tileSize.x,
                    tileOffset.y / tileSize.y);
                _workingNormalScale[index] = layer.normalScale;
                _workingLayerModes[index] = entry.mode == SolLandscapeLayerMode.Auto ? 1f : 0f;
                _workingAutoWeights[index] = Mathf.Clamp01(entry.autoWeight);
                _workingWeatherSnowSusceptibilities[index] = Mathf.Clamp01(entry.weatherSnowSusceptibility);
                _workingPermanentSnowSusceptibilities[index] = Mathf.Clamp01(entry.permanentSnowSusceptibility);
                _workingAutoSlopeParams[index] = new Vector4(
                    Mathf.Clamp(entry.slopeCenter, 0f, 90f),
                    Mathf.Max(entry.slopeContrast, 0.01f),
                    Mathf.Clamp(entry.slopeBias, -1f, 1f),
                    Mathf.Clamp(entry.slopeInfluence, -1f, 1f));
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
            Debug.LogError($"[SolLandscapeDriver] Refusing to publish: {reason}", this);
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
            Shader.SetGlobalTexture(propertyId, value);
            lastValue = value;
            CountWrite();
        }

        private void PushVector(int propertyId, Vector4 value, ref Vector4 lastValue)
        {
            if (lastValue == value)
                return;
            Shader.SetGlobalVector(propertyId, value);
            lastValue = value;
            CountWrite();
        }

        private void PushInteger(int propertyId, int value, ref int lastValue)
        {
            if (lastValue == value)
                return;
            Shader.SetGlobalInteger(propertyId, value);
            lastValue = value;
            CountWrite();
        }

        private void PushFloat(int propertyId, float value, ref float lastValue)
        {
            if (Mathf.Approximately(lastValue, value))
                return;
            Shader.SetGlobalFloat(propertyId, value);
            lastValue = value;
            CountWrite();
        }

        private void PushVectorArray(int propertyId, Vector4[] values, ref Vector4[] lastValues)
        {
            if (ArraysEqual(values, lastValues))
                return;
            Shader.SetGlobalVectorArray(propertyId, values);
            lastValues = (Vector4[])values.Clone();
            CountWrite();
        }

        private void PushFloatArray(int propertyId, float[] values, ref float[] lastValues)
        {
            if (ArraysEqual(values, lastValues))
                return;
            Shader.SetGlobalFloatArray(propertyId, values);
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
