using System;
using System.Collections.Generic;
using Sol.Environment;
using Sol.ToD;
using Sol.Water;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Sol.Lighting
{
    /// <summary>
    /// Publishes the environment's canonical lighting frame and is the sole runtime writer
    /// for its sun/moon lights, dominant RenderSettings light, ambient lighting, lighting
    /// shader globals, GI requests, and reflection scheduling.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(-925)]
    [DisallowMultipleComponent]
    public sealed class SolLightingDirector : MonoBehaviour
    {
        public static readonly ProfilerMarker ResolveMarker = new("Sol.Lighting.Resolve");
        public static readonly ProfilerMarker ApplyMarker = new("Sol.Lighting.Apply");
        public static readonly ProfilerMarker SelectionMarker =
            new("Sol.Lighting.Selection");

        static readonly int SunDirectionId = Shader.PropertyToID("_Sol_SunDirection");
        static readonly int SunColorId = Shader.PropertyToID("_Sol_SunColor");
        static readonly int DayFactorId = Shader.PropertyToID("_Sol_DayFactor");
        static readonly int EclipseFactorId = Shader.PropertyToID("_Sol_EclipseFactor");
        static readonly int LightningFlashId = Shader.PropertyToID("_Sol_LightningFlash");
        static bool s_isQuitting;

        [SerializeField] TimeOfDay timeOfDay;
        [SerializeField] SolWeatherManager weather;
        [SerializeField] SolEnvironmentWorld world;
        [SerializeField] SolLightingQualityProfile qualityProfile;
        [SerializeField] SolLightingQualityTier fallbackTier = SolLightingQualityTier.Medium;

        ulong _revision;
        readonly List<SolDirectionalLightState> _additionalCelestials = new(6);
        readonly List<DirectionalLightSnapshot> _additionalSnapshots = new(6);
        readonly SolCelestialLighting _celestialLighting = new();
        DirectionalLightSnapshot _sunSnapshot;
        DirectionalLightSnapshot _moonSnapshot;
        AmbientSnapshot _ambientSnapshot;
        SolLightingQualityProfile _subscribedQualityProfile;
        SolWaterQualityProfile _overriddenWaterQuality;
        SolAtmosphereController _overriddenAtmosphere;
        UniversalRenderPipelineAsset _overriddenPipeline;
        PipelineQualitySnapshot _pipelineSnapshot;
        readonly List<LocalLightCandidate> _localCandidates = new(160);
        readonly HashSet<int> _warnedUnmanagedLights = new();
        float _unmanagedWarningTimer;

        public static SolLightingDirector Active { get; private set; }
        public SolLightingFrame CurrentFrame { get; private set; }
        public event Action<SolLightingFrame> FrameChanged;
        public event Action<SolLightingQualityTier> TierChanged;

        public SolLightingQualityProfile QualityProfile => qualityProfile;
        public SolLightingQualityTier ActiveTier
            => qualityProfile != null ? qualityProfile.ActiveTier : fallbackTier;
        public SolLightingQualitySettings ActiveQualitySettings
            => SolLightingQualitySettings.ForTier(ActiveTier);
        public int ManagedLightCount { get; private set; }
        public int ManagedShadowCount { get; private set; }
        public int ManagedShadowSliceCount { get; private set; }
        public int ManagedVolumetricLightCount { get; private set; }

#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        static void RegisterEditorQuitGuard()
        {
            s_isQuitting = false;
            EditorApplication.quitting -= MarkQuitting;
            EditorApplication.quitting += MarkQuitting;
        }
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void RegisterRuntimeQuitGuard()
        {
            s_isQuitting = false;
            Application.quitting -= MarkQuitting;
            Application.quitting += MarkQuitting;
        }

        static void MarkQuitting() => s_isQuitting = true;

        public void SetTier(SolLightingQualityTier tier)
        {
            if (qualityProfile != null)
            {
                qualityProfile.SetTier(tier);
                return;
            }

            tier = (SolLightingQualityTier)Mathf.Clamp((int)tier, 0, 2);
            if (fallbackTier == tier)
                return;
            fallbackTier = tier;
            ApplyQualityTier(tier);
            TierChanged?.Invoke(tier);
        }

        public static SolLightingFrame ResolveFrame()
            => Active != null ? Active.CurrentFrame : default;

        /// <summary>
        /// Entry point used by TimeOfDay after sky and ambient presentation have resolved.
        /// Existing scenes gain the director at runtime; editor previews use a non-serialized
        /// hidden instance so opening a scene cannot dirty it.
        /// </summary>
        internal static void PublishFromTimeOfDay(
            TimeOfDay source,
            AmbientMode ambientMode,
            in SolTrilightAmbient ambient,
            in SolTrilightAmbient stableAmbient,
            bool applyAmbient,
            float cloudiness)
        {
            if (source == null)
                return;

            SolLightingDirector director = Resolve(source, createIfMissing: true);
            director?.Publish(
                source, ambientMode, ambient, stableAmbient, applyAmbient, cloudiness);
        }

        public static SolLightingDirector Resolve(Component requester, bool createIfMissing = false)
        {
            if (Active != null)
                return Active;

            SolLightingDirector found = FindFirstObjectByType<SolLightingDirector>();
            if (found != null)
                return found;
            if (!createIfMissing || requester == null)
                return null;

            if (Application.isPlaying)
                return requester.gameObject.AddComponent<SolLightingDirector>();

            GameObject preview = new("Sol Lighting Director (Preview)")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            return preview.AddComponent<SolLightingDirector>();
        }

        internal static void Release(TimeOfDay source)
        {
            SolLightingDirector director = Active;
            if (director == null || director.timeOfDay != source)
                return;
            if (s_isQuitting)
                return;

            director.RestoreOwnedState();
            if (!Application.isPlaying
                && (director.gameObject.hideFlags & HideFlags.DontSave) != 0)
            {
                DestroyImmediate(director.gameObject);
            }
        }

        internal static void ReleaseManagedLight(SolEnvironmentLight environmentLight)
        {
            if (environmentLight == null || s_isQuitting)
                return;
            environmentLight.RestoreAuthoredState();
        }

        void OnEnable()
        {
            if (Active != null && Active != this)
            {
                Debug.LogWarning("[SolLightingDirector] Duplicate scene director disabled.", this);
                enabled = false;
                return;
            }

            Active = this;
            BindQualityProfile();
        }

        void OnDisable()
        {
            // A duplicate disables itself from OnEnable and never owns shared lighting
            // state or quality overrides. Domain reload can leave its cached Unity object
            // references in the destroyed-object state, so teardown must not touch them.
            if (Active != this)
            {
                UnbindQualityProfile();
                return;
            }

            if (!s_isQuitting)
            {
                RestoreOwnedState();
                ReleaseQualityOverrides();
            }
            UnbindQualityProfile();

            if (Active == this)
                Active = null;
        }

        void RestoreOwnedState()
        {
            Restore(ref _sunSnapshot);
            Restore(ref _moonSnapshot);
            for (int i = 0; i < _additionalSnapshots.Count; i++)
            {
                DirectionalLightSnapshot snapshot = _additionalSnapshots[i];
                Restore(ref snapshot);
            }
            _additionalSnapshots.Clear();
            _additionalCelestials.Clear();
            SolCelestialLighting.Clear();
            _ambientSnapshot.Restore();
            if (timeOfDay != null)
                timeOfDay.SetDominantAtmosphereLight(null);
            timeOfDay = null;
            CurrentFrame = default;
            RestoreManagedLights();
        }

        void Publish(
            TimeOfDay source,
            AmbientMode ambientMode,
            in SolTrilightAmbient ambient,
            in SolTrilightAmbient stableAmbient,
            bool applyAmbient,
            float legacyCloudiness)
        {
            timeOfDay = source;
            BindQualityProfile();
            SynchronizeSubsystemQuality(ActiveTier);
            if (weather == null)
                weather = SolWeatherManager.Instance;
            if (world == null)
                world = SolEnvironmentWorld.Active;

            float weatherDim = weather != null ? weather.CurrentState.Dim : source.WeatherDim;
            float cloudiness = weather != null
                ? weather.CurrentState.Cloudiness
                : world != null
                    ? world.State.Weather.Cloudiness
                    : legacyCloudiness;
            float lightning = weather != null
                ? weather.CurrentState.LightningFlash
                : world != null
                    ? world.State.Weather.Lightning
                    : source.WeatherLightningFlash;

            using (ResolveMarker.Auto())
            {
                float attenuation = SolLightingResolver.ResolveWeatherAttenuation(weatherDim);
                SolDirectionalLightState sun = SolLightingResolver.ResolveDirectional(
                    source.SunLightingCandidate, attenuation);
                SolDirectionalLightState moon = SolLightingResolver.ResolveDirectional(
                    source.MoonLightingCandidate, attenuation);
                _additionalCelestials.Clear();
                for (int i = 0; i < source.TertiaryPlanetCount; i++)
                    _additionalCelestials.Add(SolLightingResolver.ResolveDirectional(
                        source.GetTertiaryLightingCandidate(i), attenuation));
                SolDirectionalLightState dominantState = SolLightingResolver.ResolveDominantState(
                    sun, moon, _additionalCelestials, CurrentFrame.DominantLight);
                SolDominantLightKind dominant = dominantState.Source == null
                    ? SolDominantLightKind.None
                    : dominantState.Source == sun.Source ? SolDominantLightKind.Sun
                    : dominantState.Source == moon.Source ? SolDominantLightKind.Moon
                    : SolDominantLightKind.AdditionalCelestial;

                CurrentFrame = new SolLightingFrame(
                    ++_revision,
                    sun,
                    moon,
                    dominant,
                    ambient,
                    stableAmbient,
                    attenuation,
                    SolLightingResolver.ResolveCloudShadowStrength(cloudiness, weatherDim),
                    source.DayFactor,
                    Mathf.Max(source.SolarEclipseStrength, source.LunarEclipseStrength),
                    lightning, dominantState);
            }

            using (ApplyMarker.Auto())
            {
                ApplyFrame(source, CurrentFrame, ambientMode, applyAmbient);
                ApplyAdditionalCelestials();
                ApplyMainShadowTransition();
                _celestialLighting.Publish(CurrentFrame, _additionalCelestials);
                ApplyCompatibilityGlobals(source, CurrentFrame, _additionalCelestials);
            }

            using (SelectionMarker.Auto())
                ResolveManagedLights(CurrentFrame.DayFactor, weatherDim);

            SolSkyLightingScheduler.Tick(
                source,
                stableAmbient.Sky,
                stableAmbient.Equator,
                stableAmbient.Ground,
                cloudiness);
            FrameChanged?.Invoke(CurrentFrame);
        }

        void BindQualityProfile()
        {
            if (qualityProfile == null)
            {
                qualityProfile = Resources.Load<SolLightingQualityProfile>(
                    "SolEnvironment/Sol_LightingQuality");
            }

            if (_subscribedQualityProfile == qualityProfile)
                return;

            UnbindQualityProfile();
            _subscribedQualityProfile = qualityProfile;
            if (_subscribedQualityProfile != null)
                _subscribedQualityProfile.TierChanged += OnQualityTierChanged;
            ApplyQualityTier(ActiveTier);
        }

        void UnbindQualityProfile()
        {
            if (_subscribedQualityProfile != null)
                _subscribedQualityProfile.TierChanged -= OnQualityTierChanged;
            _subscribedQualityProfile = null;
        }

        void OnQualityTierChanged(SolLightingQualityTier tier)
        {
            ApplyQualityTier(tier);
            TierChanged?.Invoke(tier);
        }

        void ApplyQualityTier(SolLightingQualityTier tier)
        {
            SolLightingQualitySettings settings = SolLightingQualitySettings.ForTier(tier);
            ApplyPipelineQuality(settings);
            SynchronizeSubsystemQuality(tier);
        }

        void SynchronizeSubsystemQuality(SolLightingQualityTier tier)
        {
            SolWaterQualityProfile waterQuality = SolWaterWorld.Active != null
                ? SolWaterWorld.Active.QualityProfile
                : null;
            if (_overriddenWaterQuality != waterQuality)
            {
                if (_overriddenWaterQuality != null)
                    _overriddenWaterQuality.ClearTierOverride();
                _overriddenWaterQuality = waterQuality;
            }
            if (_overriddenWaterQuality != null)
                _overriddenWaterQuality.SetTierOverride((SolWaterQualityTier)tier);

            SolCloudController.Active.SetQualityOverride((SolCloudQuality)tier);

            SolAtmosphereController atmosphere = SolAtmosphereController.Active;
            if (_overriddenAtmosphere != atmosphere)
            {
                if (_overriddenAtmosphere != null)
                    _overriddenAtmosphere.ClearQualityOverride();
                _overriddenAtmosphere = atmosphere;
            }
            if (_overriddenAtmosphere != null)
                _overriddenAtmosphere.SetQuality((SolAtmosphereQuality)tier);
        }

        void ApplyPipelineQuality(in SolLightingQualitySettings settings)
        {
            if (!Application.isPlaying)
                return;

            UniversalRenderPipelineAsset pipeline =
                GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (_overriddenPipeline != pipeline)
            {
                RestorePipelineQuality();
                _overriddenPipeline = pipeline;
                if (_overriddenPipeline != null)
                    _pipelineSnapshot = PipelineQualitySnapshot.Capture(_overriddenPipeline);
            }
            if (_overriddenPipeline == null)
                return;

            settings.ApplyTo(_overriddenPipeline);
        }

        void ReleaseQualityOverrides()
        {
            if (_overriddenWaterQuality != null)
                _overriddenWaterQuality.ClearTierOverride();
            _overriddenWaterQuality = null;
            SolCloudController.Active.ClearQualityOverride();
            if (_overriddenAtmosphere != null)
                _overriddenAtmosphere.ClearQualityOverride();
            _overriddenAtmosphere = null;
            RestorePipelineQuality();
        }

        void ResolveManagedLights(float dayFactor, float weatherDim)
        {
            SolLightingQualitySettings settings = ActiveQualitySettings;
            IReadOnlyList<SolEnvironmentLight> registered = SolEnvironmentLight.Registered;
            _localCandidates.Clear();
            Vector3 cameraPosition = Camera.main != null
                ? Camera.main.transform.position
                : transform.position;

            for (int i = 0; i < registered.Count; i++)
            {
                SolEnvironmentLight environmentLight = registered[i];
                if (environmentLight == null || !environmentLight.isActiveAndEnabled ||
                    !environmentLight.IsLocal)
                    continue;

                Light light = environmentLight.Source;
                float activation = environmentLight.ResolveActivation(dayFactor, weatherDim);
                float distance = Vector3.Distance(cameraPosition, light.transform.position);
                float score = activation > 0f
                    ? SolEnvironmentLight.CalculateSelectionScore(
                        environmentLight.Priority,
                        light.range,
                        distance,
                        environmentLight.AuthoredIntensity * activation,
                        environmentLight.IsSelected || environmentLight.HasManagedShadow)
                    : float.NegativeInfinity;
                _localCandidates.Add(new LocalLightCandidate(
                    environmentLight, activation, score));
            }

            _localCandidates.Sort(LocalLightCandidate.Compare);
            int activeCandidateCount = 0;
            while (activeCandidateCount < _localCandidates.Count &&
                   _localCandidates[activeCandidateCount].Activation > 0f)
                activeCandidateCount++;
            int selectedCount = Mathf.Min(settings.ManagedLightLimit, activeCandidateCount);
            int shadowSlices = 0;
            int shadowCount = 0;
            int volumetricCount = 0;
            float deltaSeconds = Application.isPlaying ? Time.unscaledDeltaTime : 1f;

            for (int i = 0; i < _localCandidates.Count; i++)
            {
                LocalLightCandidate candidate = _localCandidates[i];
                bool selected = i < selectedCount && candidate.Activation > 0f;
                int sliceCost = candidate.Light.CanCastShadows
                    ? SolLightingQualitySettings.ShadowSliceCost(candidate.Light.Source.type)
                    : 0;
                bool shadow = selected && sliceCost > 0 &&
                              shadowSlices + sliceCost <= settings.ShadowSliceLimit;
                if (shadow)
                {
                    shadowSlices += sliceCost;
                    shadowCount++;
                }

                bool volumetric = selected && candidate.Light.VolumetricScattering > 0f &&
                                  volumetricCount < settings.LocalVolumetricLightLimit;
                if (volumetric)
                    volumetricCount++;

                candidate.Light.ApplyResolvedState(
                    selected, shadow, volumetric, candidate.Activation, deltaSeconds);
            }

            ManagedLightCount = selectedCount;
            ManagedShadowCount = shadowCount;
            ManagedShadowSliceCount = shadowSlices;
            ManagedVolumetricLightCount = volumetricCount;
            SolEnvironmentBudget.SetLightingState(
                registered.Count,
                ManagedLightCount,
                ManagedShadowSliceCount,
                ManagedVolumetricLightCount,
                (int)ActiveTier);
            WarnAboutUnmanagedShadowLights();
        }

        void RestoreManagedLights()
        {
            IReadOnlyList<SolEnvironmentLight> registered = SolEnvironmentLight.Registered;
            for (int i = 0; i < registered.Count; i++)
                registered[i]?.RestoreAuthoredState();
            ManagedLightCount = 0;
            ManagedShadowCount = 0;
            ManagedShadowSliceCount = 0;
            ManagedVolumetricLightCount = 0;
            SolEnvironmentBudget.SetLightingState(
                SolEnvironmentLight.Registered.Count, 0, 0, 0, -1);
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        void WarnAboutUnmanagedShadowLights()
        {
            if (!Application.isPlaying)
                return;
            _unmanagedWarningTimer -= Time.unscaledDeltaTime;
            if (_unmanagedWarningTimer > 0f)
                return;
            _unmanagedWarningTimer = 5f;

            Light[] lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
            for (int i = 0; i < lights.Length; i++)
            {
                Light light = lights[i];
                if (light == null || !light.isActiveAndEnabled ||
                    (light.type != LightType.Point && light.type != LightType.Spot) ||
                    light.shadows == LightShadows.None ||
                    light.GetComponent<SolEnvironmentLight>() != null ||
                    !_warnedUnmanagedLights.Add(light.GetInstanceID()))
                    continue;

                Debug.LogWarning(
                    $"[SolLightingDirector] Local shadow light '{light.name}' is unmanaged " +
                    "and bypasses the active lighting-tier shadow budget.", light);
            }
        }

        void RestorePipelineQuality()
        {
            if (_overriddenPipeline != null && _pipelineSnapshot.Captured)
                _pipelineSnapshot.Restore(_overriddenPipeline);
            _overriddenPipeline = null;
            _pipelineSnapshot = default;
        }

        void ApplyFrame(
            TimeOfDay source,
            in SolLightingFrame frame,
            AmbientMode ambientMode,
            bool applyAmbient)
        {
            EnsureCaptured(ref _sunSnapshot, frame.Sun.Source);
            EnsureCaptured(ref _moonSnapshot, frame.Moon.Source);
            _ambientSnapshot.CaptureIfNeeded();

            ApplyDirectional(frame.Sun);
            ApplyDirectional(frame.Moon);
            RenderSettings.sun = frame.DominantLight;
            source.SetDominantAtmosphereLight(frame.DominantLight);

            if (!applyAmbient)
                return;

            RenderSettings.ambientMode = ambientMode;
            if (ambientMode == AmbientMode.Flat)
            {
                RenderSettings.ambientLight = frame.Ambient.Sky;
                return;
            }

            RenderSettings.ambientSkyColor = frame.Ambient.Sky;
            RenderSettings.ambientEquatorColor = frame.Ambient.Equator;
            RenderSettings.ambientGroundColor = frame.Ambient.Ground;
        }

        static void ApplyDirectional(in SolDirectionalLightState state)
        {
            Light light = state.Source;
            if (light == null)
                return;

            light.transform.rotation = state.Rotation;
            light.color = state.Color;
            light.intensity = state.Intensity;
            light.lightmapBakeType = LightmapBakeType.Realtime;
            light.shadows = state.ShadowStrength > 0f ? LightShadows.Soft : LightShadows.None;
            light.shadowStrength = state.ShadowStrength;
            light.enabled = state.Enabled;
        }

        static void EnsureCaptured(ref DirectionalLightSnapshot snapshot, Light light)
        {
            if (snapshot.Captured && snapshot.Light == light)
                return;

            Restore(ref snapshot);
            if (light == null)
                return;

            snapshot = DirectionalLightSnapshot.Capture(light);
        }

        void ApplyAdditionalCelestials()
        {
            for (int i = 0; i < Mathf.Max(_additionalCelestials.Count, _additionalSnapshots.Count); i++)
            {
                DirectionalLightSnapshot snapshot = i < _additionalSnapshots.Count
                    ? _additionalSnapshots[i] : default;
                SolDirectionalLightState state = i < _additionalCelestials.Count
                    ? _additionalCelestials[i] : default;
                EnsureCaptured(ref snapshot, state.Source);
                ApplyDirectional(state);
                if (i < _additionalSnapshots.Count) _additionalSnapshots[i] = snapshot;
                else _additionalSnapshots.Add(snapshot);
            }
            if (_additionalSnapshots.Count > _additionalCelestials.Count)
                _additionalSnapshots.RemoveRange(_additionalCelestials.Count,
                    _additionalSnapshots.Count - _additionalCelestials.Count);
        }

        void ApplyMainShadowTransition()
        {
            SolDirectionalLightState main = CurrentFrame.DominantState;
            if (main.Source == null) return;
            float competitor = 0f;
            if (CurrentFrame.Sun.Source != main.Source) competitor = CurrentFrame.Sun.Score;
            if (CurrentFrame.Moon.Source != main.Source) competitor = Mathf.Max(competitor, CurrentFrame.Moon.Score);
            for (int i = 0; i < _additionalCelestials.Count; i++)
                if (_additionalCelestials[i].Source != main.Source)
                    competitor = Mathf.Max(competitor, _additionalCelestials[i].Score);
            main.Source.shadowStrength = main.ShadowStrength
                * SolLightingResolver.MainShadowVisibility(main.Score, competitor);
        }

        static void Restore(ref DirectionalLightSnapshot snapshot)
        {
            if (snapshot.Captured && snapshot.Light != null)
                snapshot.Restore();
            snapshot = default;
        }

        static void ApplyCompatibilityGlobals(TimeOfDay source, in SolLightingFrame frame,
            IReadOnlyList<SolDirectionalLightState> additional)
        {
            // Compatibility consumers receive a real light direction. Direct water
            // highlights use URP's independent main and additional directional lights.
            float day = frame.DayFactor;
            Vector3 direction = frame.DominantState.Direction;
            Color color = (frame.Sun.Enabled ? frame.Sun.Radiance : Color.black)
                + (frame.Moon.Enabled ? frame.Moon.Radiance : Color.black);
            for (int i = 0; i < additional.Count; i++)
                if (additional[i].Enabled) color += additional[i].Radiance;

            Shader.SetGlobalVector(SunDirectionId,
                new Vector4(direction.x, direction.y, direction.z, 0f));
            Shader.SetGlobalColor(SunColorId, color);
            Shader.SetGlobalFloat(DayFactorId, day);
            Shader.SetGlobalFloat(EclipseFactorId, source.SolarEclipseStrength);
            Shader.SetGlobalFloat(LightningFlashId, frame.Lightning);
        }

        struct DirectionalLightSnapshot
        {
            public Light Light;
            public Quaternion Rotation;
            public Color Color;
            public float Intensity;
            public LightmapBakeType BakeType;
            public LightShadows Shadows;
            public float ShadowStrength;
            public bool Enabled;
            public bool Captured;

            public static DirectionalLightSnapshot Capture(Light light) => new()
            {
                Light = light,
                Rotation = light.transform.rotation,
                Color = light.color,
                Intensity = light.intensity,
                BakeType = light.lightmapBakeType,
                Shadows = light.shadows,
                ShadowStrength = light.shadowStrength,
                Enabled = light.enabled,
                Captured = true,
            };

            public void Restore()
            {
                Light.transform.rotation = Rotation;
                Light.color = Color;
                Light.intensity = Intensity;
                Light.lightmapBakeType = BakeType;
                Light.shadows = Shadows;
                Light.shadowStrength = ShadowStrength;
                Light.enabled = Enabled;
            }
        }

        struct AmbientSnapshot
        {
            public Light Sun;
            public AmbientMode Mode;
            public Color AmbientLight;
            public Color Sky;
            public Color Equator;
            public Color Ground;
            public bool Captured;

            public void CaptureIfNeeded()
            {
                if (Captured)
                    return;

                Sun = RenderSettings.sun;
                Mode = RenderSettings.ambientMode;
                AmbientLight = RenderSettings.ambientLight;
                Sky = RenderSettings.ambientSkyColor;
                Equator = RenderSettings.ambientEquatorColor;
                Ground = RenderSettings.ambientGroundColor;
                Captured = true;
            }

            public void Restore()
            {
                if (!Captured)
                    return;

                RenderSettings.sun = Sun;
                RenderSettings.ambientMode = Mode;
                RenderSettings.ambientLight = AmbientLight;
                RenderSettings.ambientSkyColor = Sky;
                RenderSettings.ambientEquatorColor = Equator;
                RenderSettings.ambientGroundColor = Ground;
                Captured = false;
            }
        }

        struct PipelineQualitySnapshot
        {
            public float ShadowDistance;
            public int CascadeCount;
            public int MainAtlasResolution;
            public int PunctualAtlasResolution;
            public bool Captured;

            public static PipelineQualitySnapshot Capture(
                UniversalRenderPipelineAsset pipeline) => new()
            {
                ShadowDistance = pipeline.shadowDistance,
                CascadeCount = pipeline.shadowCascadeCount,
                MainAtlasResolution = pipeline.mainLightShadowmapResolution,
                PunctualAtlasResolution = pipeline.additionalLightsShadowmapResolution,
                Captured = true,
            };

            public void Restore(UniversalRenderPipelineAsset pipeline)
            {
                pipeline.shadowDistance = ShadowDistance;
                pipeline.shadowCascadeCount = CascadeCount;
                pipeline.mainLightShadowmapResolution = MainAtlasResolution;
                pipeline.additionalLightsShadowmapResolution = PunctualAtlasResolution;
            }
        }

        readonly struct LocalLightCandidate
        {
            public readonly SolEnvironmentLight Light;
            public readonly float Activation;
            public readonly float Score;

            public LocalLightCandidate(
                SolEnvironmentLight light, float activation, float score)
            {
                Light = light;
                Activation = activation;
                Score = score;
            }

            public static int Compare(LocalLightCandidate a, LocalLightCandidate b)
            {
                int score = b.Score.CompareTo(a.Score);
                if (score != 0)
                    return score;
                int aId = a.Light != null ? a.Light.GetInstanceID() : int.MaxValue;
                int bId = b.Light != null ? b.Light.GetInstanceID() : int.MaxValue;
                return aId.CompareTo(bId);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active = null;
        }
    }
}
