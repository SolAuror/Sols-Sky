using UnityEngine;
using UnityEngine.Rendering;
using Sol.ToD;

/// <summary>Combines authored atmosphere settings with current sky and weather state.</summary>
[DefaultExecutionOrder(-100)]
public sealed class SolAtmosphereController : MonoBehaviour
{
    public static SolAtmosphereController Active { get; private set; }

    [SerializeField] SolAtmosphereProfile profile;
    [SerializeField] TimeOfDay timeOfDay;
    [SerializeField] SolWeatherManager weatherManager;
    [SerializeField] bool disableLegacyFog = true;
    [SerializeField] SolAtmosphereQuality fallbackQuality = SolAtmosphereQuality.Medium;

    float _noiseTime;
    float _referenceRetryTimer;
    bool _legacyFogWasEnabled;
    SolAtmosphereQuality? _runtimeQualityOverride;
    SolEnvironmentCoordinator _coordinator;

    static readonly int ActiveID = Shader.PropertyToID("_SolAtmosphereActive");
    static readonly int FogColorID = Shader.PropertyToID("_SolAtmosphereFogColor");
    static readonly int Params0ID = Shader.PropertyToID("_SolAtmosphereParams0");
    static readonly int Params1ID = Shader.PropertyToID("_SolAtmosphereParams1");
    static readonly int Params2ID = Shader.PropertyToID("_SolAtmosphereParams2");
    static readonly int SunDirectionID = Shader.PropertyToID("_SolAtmosphereSunDirection");
    static readonly int SunColorID = Shader.PropertyToID("_SolAtmosphereSunColor");
    static readonly int WindTimeID = Shader.PropertyToID("_SolAtmosphereWindTime");
    static readonly int LightningID = Shader.PropertyToID("_SolAtmosphereLightning");
    static readonly int VolumetricParamsID = Shader.PropertyToID("_SolAtmosphereVolumetricParams");
    static readonly int UpsampleParamsID = Shader.PropertyToID("_SolAtmosphereUpsampleParams");
    static readonly int SkyParamsID = Shader.PropertyToID("_SolAtmosphereSkyParams");
    static readonly int LightingParamsID = Shader.PropertyToID("_SolAtmosphereLightingParams");
    static readonly int LightningScatteringID = Shader.PropertyToID("_SolAtmosphereLightningScattering");
    static readonly int LightAvailableID = Shader.PropertyToID("_SolAtmosphereLightAvailable");
    static readonly int LocalVolumeCountID = Shader.PropertyToID("_SolAtmosphereLocalVolumeCount");
    static readonly int LocalVolumeData0ID = Shader.PropertyToID("_SolAtmosphereLocalVolumeData0");
    static readonly int LocalVolumeData1ID = Shader.PropertyToID("_SolAtmosphereLocalVolumeData1");
    static readonly int LocalLightCountID = Shader.PropertyToID("_SolAtmosphereLocalLightCount");
    static readonly int LocalLightData0ID = Shader.PropertyToID("_SolAtmosphereLocalLightData0");
    static readonly int LocalLightData1ID = Shader.PropertyToID("_SolAtmosphereLocalLightData1");
    static readonly int LocalLightData2ID = Shader.PropertyToID("_SolAtmosphereLocalLightData2");

    static readonly Vector4[] LocalVolumeData0 = new Vector4[16];
    static readonly Vector4[] LocalVolumeData1 = new Vector4[16];
    static readonly Vector4[] LocalLightData0 = new Vector4[16];
    static readonly Vector4[] LocalLightData1 = new Vector4[16];
    static readonly Vector4[] LocalLightData2 = new Vector4[16];

    public SolAtmosphereQuality Quality => _runtimeQualityOverride
        ?? (profile != null ? profile.quality : fallbackQuality);
    public Color CurrentFogColor { get; private set; }
    public float CurrentDensity { get; private set; }
    public Light CurrentDominantLight { get; private set; }
    public bool UsesVolumetricLighting => Quality != SolAtmosphereQuality.Low;

    public SolAtmosphereQuality ApplyCameraOverrides(VolumeStack stack)
    {
        PushGlobals();
        SolAtmosphereQuality quality = Quality;
        SolAtmosphereVolume volume = stack?.GetComponent<SolAtmosphereVolume>();
        if (volume == null || !volume.IsActive())
            return quality;

        if (volume.quality.overrideState)
            quality = (SolAtmosphereQuality)Mathf.Clamp(volume.quality.value, 0, 2);
        float density = CurrentDensity * (volume.densityMultiplier.overrideState
            ? volume.densityMultiplier.value
            : 1f);
        float start = volume.startDistance.overrideState
            ? volume.startDistance.value
            : SettingsStartDistance;
        float maximum = volume.maximumDistance.overrideState
            ? volume.maximumDistance.value
            : SettingsMaxDistance;
        float opacity = volume.maximumOpacity.overrideState
            ? volume.maximumOpacity.value
            : SettingsMaxOpacity;
        Color color = volume.fogColor.overrideState ? volume.fogColor.value : CurrentFogColor;
        Shader.SetGlobalColor(FogColorID, color);
        Shader.SetGlobalVector(Params0ID, new Vector4(density, start, maximum, opacity));
        Shader.SetGlobalVector(Params2ID, new Vector4(
            SettingsDirectionalScattering, SettingsPhaseAnisotropy, SettingsSkyFog, (float)quality));
        Shader.SetGlobalVector(VolumetricParamsID, new Vector4(
            SettingsShadowedScattering,
            Mathf.Min(SettingsRaymarchDistance, maximum),
            quality == SolAtmosphereQuality.Medium ? 16 : SettingsRaymarchSteps,
            SettingsRaymarchJitter));
        return quality;
    }

    void OnEnable()
    {
        if (Active != null && Active != this)
        {
            Debug.LogWarning("[SolAtmosphereController] Duplicate atmosphere authority disabled.", this);
            enabled = false;
            return;
        }

        Active = this;
        _legacyFogWasEnabled = RenderSettings.fog;
        _coordinator = SolEnvironmentCoordinator.Resolve(this, createIfMissing: true);
        _coordinator?.Register(this);
        ResolveReferences();
        PushGlobals();
    }

    void OnDisable()
    {
        // A duplicate disables itself from OnEnable. It never owned the
        // atmosphere globals, so it must not clear the active controller's
        // state when Unity follows with OnDisable.
        if (Active != this)
            return;

        Active = null;
        CurrentDominantLight = null;
        Shader.SetGlobalFloat(ActiveID, 0f);
        Shader.SetGlobalFloat(LightAvailableID, 0f);
        if (disableLegacyFog && !RenderSettings.fog)
            RenderSettings.fog = _legacyFogWasEnabled;
        _coordinator?.Unregister(this);
        _coordinator = null;
    }

    void Update()
    {
        ResolveReferences();
        float delta = timeOfDay != null ? timeOfDay.WorldDeltaSeconds : Time.deltaTime;
        _noiseTime += Mathf.Max(0f, delta) * SettingsNoiseSpeed;
        PushGlobals();
    }

    public void SetQuality(SolAtmosphereQuality quality)
    {
        _runtimeQualityOverride = quality;
        if (isActiveAndEnabled)
            PushGlobals();
    }

    /// <summary>Returns to the quality authored on the assigned profile or component.</summary>
    public void ClearQualityOverride()
    {
        _runtimeQualityOverride = null;
        if (isActiveAndEnabled)
            PushGlobals();
    }

    void ResolveReferences()
    {
        _referenceRetryTimer -= Time.unscaledDeltaTime;
        if (_referenceRetryTimer > 0f && (timeOfDay == null || weatherManager == null))
            return;

        bool missingReference = timeOfDay == null || weatherManager == null;
        timeOfDay ??= TimeOfDay.ResolveInstance();
        weatherManager ??= SolWeatherManager.Instance;

        if (missingReference && (timeOfDay == null || weatherManager == null))
            _referenceRetryTimer = 0.5f;
    }

    void PushGlobals()
    {
        SolWeatherState weather = weatherManager != null ? weatherManager.CurrentState : default;
        CurrentFogColor = RenderSettings.fogColor;
        CurrentDensity = Mathf.Max(0f, RenderSettings.fogDensity)
                       * SettingsDensityMultiplier;

        Vector3 wind = weather.WindDirection.sqrMagnitude > 0.0001f
            ? weather.WindDirection
            : Vector3.right;
        float day = timeOfDay != null ? timeOfDay.DayFactor : 1f;
        CurrentDominantLight = timeOfDay != null ? timeOfDay.DominantAtmosphereLight : null;
        Vector3 sunDirection = ResolveLightDirection(day);
        Color scatteringColor = Color.Lerp(SettingsNightColor, SettingsDayColor, day);
        Color directionalColor = ResolveLightColor(scatteringColor);
        float scattering = SettingsDirectionalScattering
                         * (weatherManager != null ? weather.LightScattering : 1f);
        float mistiness = weatherManager != null ? weather.Mistiness : 0f;
        float effectiveBaseHeight = Mathf.Lerp(SettingsBaseHeight, SettingsMistBaseHeight, mistiness);
        float effectiveHeightFalloff = Mathf.Lerp(SettingsHeightFalloff, SettingsMistHeightFalloff, mistiness);
        float effectiveNoiseIntensity = Mathf.Clamp01(
            SettingsNoiseIntensity * Mathf.Lerp(1f, 1.6f, mistiness));

        Shader.SetGlobalFloat(ActiveID, 1f);
        Shader.SetGlobalColor(FogColorID, CurrentFogColor);
        Shader.SetGlobalVector(Params0ID, new Vector4(CurrentDensity, SettingsStartDistance, SettingsMaxDistance, SettingsMaxOpacity));
        Shader.SetGlobalVector(Params1ID, new Vector4(
            effectiveBaseHeight,
            effectiveHeightFalloff,
            effectiveNoiseIntensity,
            SettingsNoiseScale));
        Shader.SetGlobalVector(Params2ID, new Vector4(scattering, SettingsPhaseAnisotropy, SettingsSkyFog, (float)Quality));
        Shader.SetGlobalVector(SunDirectionID, sunDirection);
        Shader.SetGlobalColor(SunColorID, directionalColor);
        Shader.SetGlobalVector(WindTimeID, new Vector4(wind.x, wind.z, _noiseTime, weather.WindStrength));
        Shader.SetGlobalFloat(LightningID, weather.LightningFlash);
        Shader.SetGlobalFloat(LightningScatteringID, SettingsLightningScattering);
        Shader.SetGlobalVector(VolumetricParamsID, new Vector4(
            SettingsShadowedScattering,
            Mathf.Min(SettingsRaymarchDistance, SettingsMaxDistance),
            SettingsRaymarchSteps,
            SettingsRaymarchJitter));
        Shader.SetGlobalVector(UpsampleParamsID, new Vector4(
            SettingsBilateralDepthThreshold,
            SettingsSpatialFilterStrength,
            0f,
            0f));
        Shader.SetGlobalVector(SkyParamsID, new Vector4(
            SettingsZenithFogStrength,
            SettingsHorizonFogStrength,
            SettingsFogSaturation,
            SettingsAmbientScattering));
        Shader.SetGlobalVector(LightingParamsID, new Vector4(
            weather.SkyObscuration,
            weather.Cloudiness,
            weather.Dim,
            SettingsMaxScatteringLuminance));
        Shader.SetGlobalFloat(LightAvailableID, CurrentDominantLight != null ? 1f : 0f);
        PushLocalVolumesAndLights();

        if (disableLegacyFog)
            RenderSettings.fog = false;
    }

    float SettingsDensityMultiplier => profile != null ? profile.densityMultiplier : 1f;
    float SettingsStartDistance => profile != null ? profile.startDistance : 5f;
    float SettingsMaxDistance => profile != null ? profile.maxDistance : 500f;
    float SettingsMaxOpacity => profile != null ? profile.maxOpacity : 0.92f;
    float SettingsBaseHeight => profile != null ? profile.baseHeight : 12f;
    float SettingsHeightFalloff => profile != null ? profile.heightFalloff : 0.035f;
    float SettingsMistBaseHeight => profile != null ? profile.mistBaseHeight : 1.5f;
    float SettingsMistHeightFalloff => profile != null ? profile.mistHeightFalloff : 0.12f;
    float SettingsNoiseIntensity => Quality == SolAtmosphereQuality.Low ? 0f : profile != null ? profile.noiseIntensity : 0.1f;
    float SettingsNoiseScale => profile != null ? profile.noiseScale : 0.0035f;
    float SettingsNoiseSpeed => profile != null ? profile.noiseSpeed : 0.08f;
    float SettingsPhaseAnisotropy => Mathf.Clamp(profile != null ? profile.phaseAnisotropy : 0.55f, -0.9f, 0.9f);
    float SettingsDirectionalScattering => profile != null ? profile.directionalScatteringIntensity : 0.65f;
    float SettingsShadowedScattering => Mathf.Clamp01(profile != null ? profile.shadowedScatteringStrength : 0.85f);
    float SettingsRaymarchDistance => Mathf.Max(1f, profile != null ? profile.raymarchDistance : 500f);
    int SettingsRaymarchSteps => Quality == SolAtmosphereQuality.Medium
        ? 16
        : Mathf.Clamp(profile != null ? profile.raymarchStepCount : 32, 8, 32);
    float SettingsRaymarchJitter => Mathf.Clamp01(profile != null ? profile.raymarchJitter : 0.15f);
    float SettingsBilateralDepthThreshold => Mathf.Max(0.01f, profile != null ? profile.bilateralDepthThreshold : 2f);
    float SettingsSpatialFilterStrength => Quality == SolAtmosphereQuality.High
        ? Mathf.Clamp01(profile != null ? profile.spatialFilterStrength : 0.75f)
        : 0f;
    float SettingsSkyFog => profile != null ? profile.skyFogStrength : 1f;
    float SettingsZenithFogStrength => Mathf.Max(0f, profile != null ? profile.zenithFogStrength : 0.12f);
    float SettingsHorizonFogStrength => Mathf.Max(0f, profile != null ? profile.horizonFogStrength : 1f);
    float SettingsFogSaturation => Mathf.Clamp01(profile != null ? profile.fogSaturation : 0.35f);
    float SettingsAmbientScattering => Mathf.Max(0f, profile != null ? profile.ambientScatteringIntensity : 0.65f);
    float SettingsMaxScatteringLuminance => Mathf.Max(0.01f, profile != null ? profile.maxScatteringLuminance : 1.5f);
    float SettingsLightningScattering => Mathf.Max(0f, profile != null ? profile.lightningScatteringIntensity : 0.08f);
    Color SettingsDayColor => profile != null ? profile.dayScatteringColor : new Color(1f, 0.75f, 0.48f, 1f);
    Color SettingsNightColor => profile != null ? profile.nightScatteringColor : new Color(0.22f, 0.34f, 0.62f, 1f);

    Vector3 ResolveLightDirection(float day)
    {
        if (CurrentDominantLight != null)
            return -CurrentDominantLight.transform.forward;
        if (timeOfDay == null)
            return Vector3.up;
        return day >= 0.5f ? timeOfDay.SunDirection : timeOfDay.MoonDirection;
    }

    Color ResolveLightColor(Color authoredScatteringColor)
    {
        if (CurrentDominantLight == null)
            return authoredScatteringColor;

        float intensity = Mathf.Max(0f, CurrentDominantLight.intensity);
        Color lightColor = Color.Lerp(authoredScatteringColor, CurrentDominantLight.color, 0.5f);
        return lightColor * intensity;
    }

    static void PushLocalVolumesAndLights()
    {
        int volumeCount = 0;
        var densityVolumes = SolAtmosphereLocalRegistry.DensityVolumes;
        for (int i = 0; i < densityVolumes.Count && volumeCount < LocalVolumeData0.Length; i++)
        {
            SolAtmosphereDensityVolume volume = densityVolumes[i];
            if (volume == null || !volume.isActiveAndEnabled)
                continue;
            Bounds bounds = volume.WorldBounds;
            LocalVolumeData0[volumeCount] = new Vector4(
                bounds.center.x, bounds.center.y, bounds.center.z, volume.Density);
            LocalVolumeData1[volumeCount] = new Vector4(
                bounds.extents.x, bounds.extents.y, bounds.extents.z, volume.BlendDistance);
            volumeCount++;
        }
        var exclusions = SolAtmosphereLocalRegistry.ExclusionVolumes;
        for (int i = 0; i < exclusions.Count && volumeCount < LocalVolumeData0.Length; i++)
        {
            SolAtmosphereExclusionVolume volume = exclusions[i];
            if (volume == null || !volume.isActiveAndEnabled)
                continue;
            Bounds bounds = volume.WorldBounds;
            LocalVolumeData0[volumeCount] = new Vector4(
                bounds.center.x, bounds.center.y, bounds.center.z, -volume.Strength);
            LocalVolumeData1[volumeCount] = new Vector4(
                bounds.extents.x, bounds.extents.y, bounds.extents.z, volume.BlendDistance);
            volumeCount++;
        }
        Shader.SetGlobalInt(LocalVolumeCountID, volumeCount);
        Shader.SetGlobalVectorArray(LocalVolumeData0ID, LocalVolumeData0);
        Shader.SetGlobalVectorArray(LocalVolumeData1ID, LocalVolumeData1);

        int lightCount = 0;
        var lights = SolAtmosphereLocalRegistry.Lights;
        for (int i = 0; i < lights.Count && lightCount < LocalLightData0.Length; i++)
        {
            SolVolumetricLight volumetric = lights[i];
            Light light = volumetric != null ? volumetric.Source : null;
            if (light == null || !light.isActiveAndEnabled
                || (light.type != LightType.Point && light.type != LightType.Spot))
                continue;
            Transform lightTransform = light.transform;
            Color color = light.color * (light.intensity * volumetric.ScatteringMultiplier);
            LocalLightData0[lightCount] = new Vector4(
                lightTransform.position.x, lightTransform.position.y, lightTransform.position.z,
                Mathf.Max(0.01f, light.range));
            LocalLightData1[lightCount] = new Vector4(
                color.r, color.g, color.b, light.type == LightType.Spot ? 1f : 0f);
            Vector3 direction = lightTransform.forward;
            LocalLightData2[lightCount] = new Vector4(
                direction.x, direction.y, direction.z,
                Mathf.Cos(light.spotAngle * 0.5f * Mathf.Deg2Rad));
            lightCount++;
        }
        Shader.SetGlobalInt(LocalLightCountID, lightCount);
        Shader.SetGlobalVectorArray(LocalLightData0ID, LocalLightData0);
        Shader.SetGlobalVectorArray(LocalLightData1ID, LocalLightData1);
        Shader.SetGlobalVectorArray(LocalLightData2ID, LocalLightData2);
    }

    // CPU mirrors of the HLSL model keep the numerical edge cases testable.
    internal static float EvaluateHeightOpticalDepth(
        float startHeight, float verticalSlope, float distance, float falloff, float density)
    {
        distance = Mathf.Max(0f, distance);
        falloff = Mathf.Max(0.000001f, falloff);
        density = Mathf.Max(0f, density);
        if (distance <= 0f || density <= 0f)
            return 0f;

        float endHeight = startHeight + verticalSlope * distance;
        float integral;
        if (startHeight <= 0f && endHeight <= 0f)
        {
            integral = distance;
        }
        else if (startHeight >= 0f && endHeight >= 0f)
        {
            integral = Mathf.Abs(verticalSlope) < 0.000001f
                ? distance * Mathf.Exp(-falloff * startHeight)
                : (Mathf.Exp(-falloff * startHeight) - Mathf.Exp(-falloff * endHeight))
                  / (falloff * verticalSlope);
        }
        else
        {
            float crossing = Mathf.Clamp(-startHeight / verticalSlope, 0f, distance);
            if (startHeight < 0f)
            {
                float upperDistance = distance - crossing;
                integral = crossing + (1f - Mathf.Exp(-falloff * verticalSlope * upperDistance))
                           / (falloff * verticalSlope);
            }
            else
            {
                float upperIntegral = (Mathf.Exp(-falloff * startHeight) - 1f)
                                    / (falloff * verticalSlope);
                integral = upperIntegral + distance - crossing;
            }
        }

        return Mathf.Max(0f, integral) * density;
    }

    internal static float EvaluateCornetteShanksPhase(float cosine, float anisotropy)
    {
        float g = Mathf.Clamp(anisotropy, -0.9f, 0.9f);
        float c = Mathf.Clamp(cosine, -1f, 1f);
        float denominatorBase = Mathf.Max(0.0001f, 1f + g * g - 2f * g * c);
        float normalized = 3f * (1f - g * g) * (1f + c * c)
                         / (8f * Mathf.PI * (2f + g * g) * Mathf.Pow(denominatorBase, 1.5f));
        return normalized;
    }

    internal static float EvaluateSkyOpticalDepthScale(
        float viewDirectionY,
        float skyFogStrength,
        float zenithStrength,
        float horizonStrength,
        float skyObscuration)
    {
        float horizon = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(viewDirectionY)), 3f);
        float shaped = Mathf.Lerp(Mathf.Max(0f, zenithStrength), Mathf.Max(0f, horizonStrength), horizon);
        shaped = Mathf.Lerp(shaped, Mathf.Max(0f, horizonStrength), Mathf.Clamp01(skyObscuration));
        return Mathf.Max(0f, skyFogStrength) * shaped;
    }
}
