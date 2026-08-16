using Sol.Environment;
using UnityEngine;

namespace Sol.Water
{
    /// <summary>
    /// Publishes the terrain wetness shader contract consumed by
    /// <c>Shaders/Terrain/SolTerrainWetness.hlsl</c>.
    ///
    /// These globals used to come from the legacy <c>SolWaterManager</c>, which the
    /// one-way Water 2 converter disables. Nothing in Water 2 replaced them, so a
    /// converted scene silently lost terrain wetness entirely: rain stopped darkening
    /// the ground and the shoreline stopped reading as wet. This restores the same
    /// contract from Water 2's own authorities.
    ///
    /// <c>SolEnvironmentCoordinator</c> already captures and restores every global written
    /// here, so this only has to publish.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class SolWaterWetness : MonoBehaviour
    {
        [Tooltip("How strongly rain alone wets the terrain.")]
        [Range(0f, 1f)] public float rainWetness = 1f;

        [Tooltip("How strongly proximity to the water surface wets the terrain.")]
        [Range(0f, 1f)] public float waterWetness = 1f;

        [Tooltip("World-space fade distance above the water level for shoreline wetness.")]
        [Min(0.01f)] public float waterWetnessRange = 0.75f;

        [Tooltip("Terrain whose sand layer receives wet darkening. Null uses the active terrain.")]
        public Terrain wetnessTerrain;

        [Tooltip("Sand TerrainLayer to darken when wet. Null resolves TerrainLayer_Sand by name.")]
        public TerrainLayer sandLayer;

        [Tooltip("Maximum albedo darkening applied to wet sand only.")]
        [Range(0f, 0.4f)] public float sandWetDarkening = 0.14f;

        [Tooltip("Target smoothness for fully wet terrain. Authored values above this are preserved.")]
        [Range(0f, 0.8f)] public float wetSmoothness = 0.48f;

        static readonly int RainIntensityId = Shader.PropertyToID("_Sol_RainIntensity");
        static readonly int GlobalWaterLevelId = Shader.PropertyToID("_Sol_GlobalWaterLevel");
        static readonly int TerrainWetnessId = Shader.PropertyToID("_Sol_TerrainWetness");
        static readonly int TerrainWetSmoothnessId = Shader.PropertyToID("_Sol_TerrainWetSmoothness");
        static readonly int TerrainSandMaskId = Shader.PropertyToID("_Sol_TerrainSandMask");
        static readonly int TerrainSandChannelId = Shader.PropertyToID("_Sol_TerrainSandChannel");
        static readonly int TerrainOriginInvSizeId = Shader.PropertyToID("_Sol_TerrainOriginInvSize");

        // Redundant global writes are not free and this runs every frame, so each value
        // is only pushed when it actually moves. NaN seeds force the first write.
        float _lastRainIntensity = float.NaN;
        float _lastWaterLevel = float.NaN;
        float _lastWetSmoothness = float.NaN;
        Vector4 _lastWetness = new(float.NaN, float.NaN, float.NaN, float.NaN);
        Vector4 _lastSandChannel = new(float.NaN, float.NaN, float.NaN, float.NaN);
        Vector4 _lastOriginInvSize = new(float.NaN, float.NaN, float.NaN, float.NaN);
        Texture _lastSandMask;

        Terrain _resolvedTerrain;
        TerrainData _resolvedData;
        TerrainLayer _resolvedSandLayer;
        // Deliberately not initialised to Texture2D.blackTexture here: field initialisers
        // run in the MonoBehaviour constructor, where Unity forbids that call.
        Texture _sandMask;
        bool _sandMaskDirty = true;
        Vector4 _sandChannel;
        Vector4 _originInvSize;

        void OnEnable() => Invalidate();

        void OnValidate() => Invalidate();

        void LateUpdate() => Publish();

        /// <summary>
        /// Forces every cached global to be rewritten on the next publish. Only flags the
        /// sand mask rather than re-resolving it: this runs from OnValidate, and reaching
        /// into Terrain/TerrainData from there is not reliably safe.
        /// </summary>
        public void Invalidate()
        {
            _lastRainIntensity = float.NaN;
            _lastWaterLevel = float.NaN;
            _lastWetSmoothness = float.NaN;
            _lastWetness = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
            _lastSandChannel = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
            _lastOriginInvSize = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
            _lastSandMask = null;
            _sandMaskDirty = true;
        }

        void Publish()
        {
            float rain = SolEnvironmentWorld.Active != null
                ? Mathf.Clamp01(SolEnvironmentWorld.Active.State.Weather.Rain) : 0f;
            if (!Mathf.Approximately(_lastRainIntensity, rain))
            {
                Shader.SetGlobalFloat(RainIntensityId, rain);
                _lastRainIntensity = rain;
            }

            float level = ResolveWaterLevel();
            if (!Mathf.Approximately(_lastWaterLevel, level))
            {
                Shader.SetGlobalFloat(GlobalWaterLevelId, level);
                _lastWaterLevel = level;
            }

            Vector4 wetness = new(
                Mathf.Clamp01(rainWetness),
                Mathf.Clamp01(waterWetness),
                Mathf.Max(0.01f, waterWetnessRange),
                Mathf.Clamp(sandWetDarkening, 0f, 0.4f));
            if (_lastWetness != wetness)
            {
                Shader.SetGlobalVector(TerrainWetnessId, wetness);
                _lastWetness = wetness;
            }

            float smoothness = Mathf.Clamp(wetSmoothness, 0f, 0.8f);
            if (!Mathf.Approximately(_lastWetSmoothness, smoothness))
            {
                Shader.SetGlobalFloat(TerrainWetSmoothnessId, smoothness);
                _lastWetSmoothness = smoothness;
            }

            ResolveSandMask();
            PushSandMask();
        }

        /// <summary>
        /// The terrain shader compares against a single water height, so one body has to
        /// win. The ocean does when present, because that is what a shoreline is measured
        /// against; otherwise the highest-priority body stands in. Per-body terrain
        /// wetness would need the shader contract to change, and is listed as deferred
        /// work alongside the other last-writer-wins globals.
        /// </summary>
        float ResolveWaterLevel()
        {
            SolWaterWorld world = SolWaterWorld.Active;
            if (world == null)
                return 0f;

            SolWaterBody chosen = null;
            foreach (SolWaterBody body in world.Bodies)
            {
                if (body == null || !body.isActiveAndEnabled)
                    continue;
                if (body.IsInfinite)
                    return body.SurfaceLevel;
                if (chosen == null || body.Priority > chosen.Priority)
                    chosen = body;
            }
            return chosen != null ? chosen.SurfaceLevel : 0f;
        }

        void ResolveSandMask()
        {
            Terrain target = wetnessTerrain != null ? wetnessTerrain : Terrain.activeTerrain;
            TerrainData data = target != null ? target.terrainData : null;

            if (!_sandMaskDirty && target == _resolvedTerrain && data == _resolvedData
                && sandLayer == _resolvedSandLayer)
                return;

            _sandMaskDirty = false;
            _resolvedTerrain = target;
            _resolvedData = data;
            _resolvedSandLayer = sandLayer;
            _sandMask = Texture2D.blackTexture;
            _sandChannel = Vector4.zero;
            _originInvSize = Vector4.zero;
            if (target == null || data == null)
                return;

            TerrainLayer[] layers = data.terrainLayers;
            int sandIndex = -1;
            for (int index = 0; index < layers.Length; index++)
            {
                TerrainLayer layer = layers[index];
                bool matches = sandLayer != null
                    ? layer == sandLayer
                    : layer != null && layer.name == "TerrainLayer_Sand";
                if (matches)
                {
                    sandIndex = index;
                    break;
                }
            }

            int alphamapIndex = sandIndex / 4;
            if (sandIndex < 0 || alphamapIndex >= data.alphamapTextureCount)
                return;

            _sandMask = data.GetAlphamapTexture(alphamapIndex);
            _sandChannel[sandIndex & 3] = 1f;
            Vector3 origin = target.transform.position;
            Vector3 size = data.size;
            _originInvSize = new Vector4(origin.x, origin.z,
                1f / Mathf.Max(size.x, 0.001f), 1f / Mathf.Max(size.z, 0.001f));
        }

        void PushSandMask()
        {
            if (_lastSandMask != _sandMask)
            {
                Shader.SetGlobalTexture(TerrainSandMaskId, _sandMask);
                _lastSandMask = _sandMask;
            }
            if (_lastSandChannel != _sandChannel)
            {
                Shader.SetGlobalVector(TerrainSandChannelId, _sandChannel);
                _lastSandChannel = _sandChannel;
            }
            if (_lastOriginInvSize != _originInvSize)
            {
                Shader.SetGlobalVector(TerrainOriginInvSizeId, _originInvSize);
                _lastOriginInvSize = _originInvSize;
            }
        }
    }
}
