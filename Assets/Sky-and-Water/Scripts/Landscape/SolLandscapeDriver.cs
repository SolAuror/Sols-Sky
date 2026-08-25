using System;
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

        private Texture _lastControl0;
        private Texture _lastControl1;
        private Texture _lastCS;
        private Texture _lastNOH;
        private Vector4 _lastControlTexelSize = NaNVector;
        private Vector4 _lastTerrainOriginSize = NaNVector;
        private Vector4[] _lastLayerST;
        private float[] _lastNormalScale;
        private int _lastLayerCount = int.MinValue;

        private Vector4[] _workingLayerST;
        private float[] _workingNormalScale;
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
            _lastLayerCount = int.MinValue;
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
            PushInteger(LayerCountId, layerCount, ref _lastLayerCount);
            PushVector(TerrainOriginSizeId, terrainOriginSize, ref _lastTerrainOriginSize);

            // Basemap generation runs after the globals above have become valid. Marking it dirty
            // before publication can bake an all-zero control/array contract in edit mode.
            if (LastPublishWriteCount > 0)
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
            }

            cs = config.CSArray;
            noh = config.NOHArray;
            if (cs == null || noh == null)
                return Refusal("The accepted CSNOH arrays are not both assigned.", out refusalReason);
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
