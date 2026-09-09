using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Scene-owned lifetime coordinator for Sol environment authorities.
/// Captures the scene's authored RenderSettings and Sol shader globals before
/// the first authority writes them, then restores that state after the final
/// authority releases ownership.
/// </summary>
[ExecuteAlways]
public sealed class SolEnvironmentCoordinator : MonoBehaviour
{
    public static SolEnvironmentCoordinator Instance { get; private set; }

    readonly HashSet<int> _owners = new();
    readonly Dictionary<int, float> _floats = new();
    readonly Dictionary<int, int> _ints = new();
    readonly Dictionary<int, Vector4> _vectors = new();
    readonly Dictionary<int, Color> _colors = new();
    readonly Dictionary<int, Texture> _textures = new();
    readonly Dictionary<int, Vector4[]> _vectorArrays = new();

    RenderSettingsSnapshot _renderSettings;
    bool _captured;
    Material _runtimeSkybox;
    int _skyboxOwnerId;

    static readonly int[] FloatIds =
    {
        Shader.PropertyToID("_Sol_DayFactor"),
        Shader.PropertyToID("_Sol_EclipseFactor"),
        Shader.PropertyToID("_Sol_WindStrength"),
        Shader.PropertyToID("_Sol_GlobalWaveSpeedMul"),
        Shader.PropertyToID("_Sol_WaveTime"),
        Shader.PropertyToID("_Sol_RainIntensity"),
        Shader.PropertyToID("_Sol_SurfaceWetness"),
        Shader.PropertyToID("_Sol_GlobalWaterLevel"),
        Shader.PropertyToID("_Sol_RainRoughnessBoost"),
        Shader.PropertyToID("_Sol_RainNormalBoost"),
        Shader.PropertyToID("_Sol_RainReflectionDampen"),
        Shader.PropertyToID("_Sol_TerrainWetSmoothness"),
        Shader.PropertyToID("_Sol_LightningFlash"),
        Shader.PropertyToID("_SolAtmosphereActive"),
        Shader.PropertyToID("_SolAtmosphereLightning"),
        Shader.PropertyToID("_SolAtmosphereLightningScattering"),
        Shader.PropertyToID("_SolAtmosphereLightAvailable"),
        Shader.PropertyToID("_SolAtmosphereTransparentFog"),
        Shader.PropertyToID("_SolSkyFrameActive"),
        Shader.PropertyToID("_SolSkyStellarBackdropActive"),
        Shader.PropertyToID("_SolCloudActive"),
        Shader.PropertyToID("_Sol_RippleSpeed"),
        Shader.PropertyToID("_Sol_RippleFrequency"),
        Shader.PropertyToID("_Sol_RippleLifetime"),
        Shader.PropertyToID("_Sol_RippleTime"),
        Shader.PropertyToID("_WaterSurfaceY"),
        Shader.PropertyToID("_UnderwaterFactor"),
        Shader.PropertyToID("_UnderwaterDepth"),
    };

    static readonly int[] IntIds =
    {
        Shader.PropertyToID("_Sol_RippleCount"),
        Shader.PropertyToID("_SolAtmosphereLocalVolumeCount"),
        Shader.PropertyToID("_SolAtmosphereLocalLightCount"),
    };

    static readonly int[] VectorIds =
    {
        Shader.PropertyToID("_Sol_SunDirection"),
        Shader.PropertyToID("_Sol_WindDirection"),
        Shader.PropertyToID("_Sol_WaterDynamics"),
        Shader.PropertyToID("_Sol_TerrainWetness"),
        Shader.PropertyToID("_Sol_WaveFadeCenter"),
        Shader.PropertyToID("_Sol_RippleSimRegion"),
        Shader.PropertyToID("_Sol_RippleSimParams"),
        Shader.PropertyToID("_SolAtmosphereParams0"),
        Shader.PropertyToID("_SolAtmosphereParams1"),
        Shader.PropertyToID("_SolAtmosphereParams2"),
        Shader.PropertyToID("_SolAtmosphereVolumetricParams"),
        Shader.PropertyToID("_SolAtmosphereUpsampleParams"),
        Shader.PropertyToID("_SolAtmosphereSkyParams"),
        Shader.PropertyToID("_SolAtmosphereLightingParams"),
        Shader.PropertyToID("_SolAtmosphereSunDirection"),
        Shader.PropertyToID("_SolSkyHorizon"),
        Shader.PropertyToID("_SolSkySunDirection"),
        Shader.PropertyToID("_SolSkyGradientParams"),
        Shader.PropertyToID("_SolSkyDirectionalParams"),
        Shader.PropertyToID("_SolSkyStellarParams"),
        Shader.PropertyToID("_SolAtmosphereWindTime"),
        Shader.PropertyToID("_Sol_TerrainSandChannel"),
        Shader.PropertyToID("_Sol_TerrainOriginInvSize"),
        Shader.PropertyToID("_Sol_TerrainShorelineMapping"),
        Shader.PropertyToID("_Sol_TerrainShorelineParams"),
    };

    static readonly int[] ColorIds =
    {
        Shader.PropertyToID("_Sol_SunColor"),
        Shader.PropertyToID("_SolAtmosphereFogColor"),
        Shader.PropertyToID("_SolAtmosphereSunColor"),
        Shader.PropertyToID("_SolSkyZenithColor"),
        Shader.PropertyToID("_SolSkyHorizonColor"),
        Shader.PropertyToID("_SolSkyNadirColor"),
        Shader.PropertyToID("_SolSkyTwilightColor"),
        Shader.PropertyToID("_SolSkyAntiSolarColor"),
    };

    static readonly int[] TextureIds =
    {
        Shader.PropertyToID("_Sol_RippleSimTex"),
        Shader.PropertyToID("_Sol_TerrainSandMask"),
        Shader.PropertyToID("_Sol_TerrainShorelineData"),
        Shader.PropertyToID("_CloudNoiseTex"),
        Shader.PropertyToID("_CloudWeatherMap"),
        Shader.PropertyToID("_SolCloudRenderTexture"),
        Shader.PropertyToID("_SolSkyStellarBackdrop"),
    };

    static readonly int[] VectorArrayIds =
    {
        Shader.PropertyToID("_Sol_Ripples"),
        Shader.PropertyToID("_SolAtmosphereLocalVolumeData0"),
        Shader.PropertyToID("_SolAtmosphereLocalVolumeData1"),
        Shader.PropertyToID("_SolAtmosphereLocalLightData0"),
        Shader.PropertyToID("_SolAtmosphereLocalLightData1"),
        Shader.PropertyToID("_SolAtmosphereLocalLightData2"),
    };

    public static SolEnvironmentCoordinator Resolve(Component requester, bool createIfMissing = false)
    {
        if (Instance != null)
            return Instance;

        if (requester != null)
        {
            SolEnvironmentCoordinator local = requester.GetComponentInParent<SolEnvironmentCoordinator>();
            if (local != null)
                return local;
        }

        SolEnvironmentCoordinator found = FindFirstObjectByType<SolEnvironmentCoordinator>();
        if (found != null)
            return found;

        return createIfMissing && requester != null
            ? requester.gameObject.AddComponent<SolEnvironmentCoordinator>()
            : null;
    }

    void OnEnable()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[SolEnvironmentCoordinator] Duplicate scene coordinator disabled.", this);
            enabled = false;
            return;
        }

        Instance = this;
    }

    void OnDisable()
    {
        RestoreAll();
        _owners.Clear();
        if (Instance == this)
            Instance = null;
    }

    public void Register(Object owner)
    {
        if (owner == null)
            return;

        if (_owners.Count == 0 && !_captured)
            CaptureAll();

        _owners.Add(owner.GetInstanceID());
    }

    public void Unregister(Object owner)
    {
        if (owner == null)
            return;

        _owners.Remove(owner.GetInstanceID());
        if (_owners.Count == 0)
            RestoreAll();
    }

    public Material AcquireSkyboxMaterial(Object owner)
    {
        if (owner == null)
            return RenderSettings.skybox;

        Register(owner);

        int ownerId = owner.GetInstanceID();
        if (_runtimeSkybox != null)
            return _runtimeSkybox;

        Material authored = _captured ? _renderSettings.skybox : RenderSettings.skybox;
        if (authored == null)
            return null;

        _runtimeSkybox = new Material(authored)
        {
            name = authored.name + " (Sol Runtime)",
            hideFlags = HideFlags.HideAndDontSave,
        };
        _skyboxOwnerId = ownerId;
        RenderSettings.skybox = _runtimeSkybox;
        return _runtimeSkybox;
    }

    public void ReleaseSkybox(Object owner)
    {
        if (_runtimeSkybox == null || owner == null || _skyboxOwnerId != owner.GetInstanceID())
            return;

        if (RenderSettings.skybox == _runtimeSkybox && _captured)
            RenderSettings.skybox = _renderSettings.skybox;

        DestroyUnityObject(_runtimeSkybox);
        _runtimeSkybox = null;
        _skyboxOwnerId = 0;
    }

    void CaptureAll()
    {
        _renderSettings = RenderSettingsSnapshot.Capture();
        _floats.Clear();
        _ints.Clear();
        _vectors.Clear();
        _colors.Clear();
        _textures.Clear();
        _vectorArrays.Clear();

        foreach (int id in FloatIds) _floats[id] = Shader.GetGlobalFloat(id);
        foreach (int id in IntIds) _ints[id] = Shader.GetGlobalInt(id);
        foreach (int id in VectorIds) _vectors[id] = Shader.GetGlobalVector(id);
        foreach (int id in ColorIds) _colors[id] = Shader.GetGlobalColor(id);
        foreach (int id in TextureIds) _textures[id] = Shader.GetGlobalTexture(id);
        foreach (int id in VectorArrayIds) _vectorArrays[id] = Shader.GetGlobalVectorArray(id);

        _captured = true;
    }

    void RestoreAll()
    {
        if (!_captured)
            return;

        if (_runtimeSkybox != null)
        {
            DestroyUnityObject(_runtimeSkybox);
            _runtimeSkybox = null;
            _skyboxOwnerId = 0;
        }

        _renderSettings.Restore();
        foreach (var value in _floats) Shader.SetGlobalFloat(value.Key, value.Value);
        foreach (var value in _ints) Shader.SetGlobalInt(value.Key, value.Value);
        foreach (var value in _vectors) Shader.SetGlobalVector(value.Key, value.Value);
        foreach (var value in _colors) Shader.SetGlobalColor(value.Key, value.Value);
        foreach (var value in _textures) Shader.SetGlobalTexture(value.Key, value.Value);
        foreach (var value in _vectorArrays)
        {
            // Unity players reject zero-length global vector arrays. The
            // paired ripple-count global is restored above, so an empty
            // captured array is already functionally cleared when count = 0.
            if (value.Value != null && value.Value.Length > 0)
                Shader.SetGlobalVectorArray(value.Key, value.Value);
        }

        _captured = false;
    }

    static void DestroyUnityObject(Object value)
    {
        if (value == null)
            return;

        if (Application.isPlaying)
            Destroy(value);
        else
            DestroyImmediate(value);
    }

    struct RenderSettingsSnapshot
    {
        public Material skybox;
        public Light sun;
        public AmbientMode ambientMode;
        public Color ambientLight;
        public Color ambientSkyColor;
        public Color ambientEquatorColor;
        public Color ambientGroundColor;
        public bool fog;
        public FogMode fogMode;
        public Color fogColor;
        public float fogDensity;

        public static RenderSettingsSnapshot Capture() => new()
        {
            skybox = RenderSettings.skybox,
            sun = RenderSettings.sun,
            ambientMode = RenderSettings.ambientMode,
            ambientLight = RenderSettings.ambientLight,
            ambientSkyColor = RenderSettings.ambientSkyColor,
            ambientEquatorColor = RenderSettings.ambientEquatorColor,
            ambientGroundColor = RenderSettings.ambientGroundColor,
            fog = RenderSettings.fog,
            fogMode = RenderSettings.fogMode,
            fogColor = RenderSettings.fogColor,
            fogDensity = RenderSettings.fogDensity,
        };

        public void Restore()
        {
            RenderSettings.skybox = skybox;
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = ambientMode;
            RenderSettings.ambientLight = ambientLight;
            RenderSettings.ambientSkyColor = ambientSkyColor;
            RenderSettings.ambientEquatorColor = ambientEquatorColor;
            RenderSettings.ambientGroundColor = ambientGroundColor;
            RenderSettings.fog = fog;
            RenderSettings.fogMode = fogMode;
            RenderSettings.fogColor = fogColor;
            RenderSettings.fogDensity = fogDensity;
        }
    }
}
