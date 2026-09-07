using System.Collections.Generic;
using UnityEngine;

namespace Sol.Lighting
{
    public enum SolEnvironmentLightActivation : byte
    {
        NightOnly,
        AlwaysOn,
        CustomCurve,
    }

    /// <summary>
    /// Opt-in authoring for local lights governed by the environment lighting budget.
    /// Lights without this component remain ordinary Unity lights and are never mutated.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Light))]
    public sealed class SolEnvironmentLight : MonoBehaviour
    {
        static readonly List<SolEnvironmentLight> RegisteredLights = new(64);

        [SerializeField] Light source;
        [SerializeField] SolEnvironmentLightActivation activation =
            SolEnvironmentLightActivation.NightOnly;
        [SerializeField] AnimationCurve customActivation =
            new(new Keyframe(0f, 1f), new Keyframe(1f, 0f));
        [SerializeField, Range(-100, 100)] int priority;
        [SerializeField] bool shadowEligible = true;
        [SerializeField, Range(0f, 4f)] float volumetricScattering = 1f;
        [SerializeField, Min(0f)] float fadeSeconds = 0.35f;

        float _authoredIntensity;
        LightShadows _authoredShadows;
        LightmapBakeType _authoredBakeType;
        bool _authoredEnabled;
        bool _captured;
        float _fade;

        public Light Source => source;
        public SolEnvironmentLightActivation Activation => activation;
        public AnimationCurve CustomActivation => customActivation;
        public int Priority => priority;
        public bool ShadowEligible => shadowEligible;
        public float VolumetricScattering => volumetricScattering;
        public float AuthoredIntensity => _authoredIntensity;
        public bool IsLocal => source != null &&
            (source.type == LightType.Point || source.type == LightType.Spot);
        public bool CanCastShadows => IsLocal && shadowEligible &&
            _authoredShadows != LightShadows.None;
        public float CurrentFade => _fade;
        public bool IsSelected { get; private set; }
        public bool HasManagedShadow { get; private set; }
        public bool ContributesVolumetrics { get; private set; }

        internal static IReadOnlyList<SolEnvironmentLight> Registered => RegisteredLights;

        void Reset()
        {
            source = GetComponent<Light>();
            customActivation = new AnimationCurve(
                new Keyframe(0f, 1f), new Keyframe(1f, 0f));
        }

        void OnEnable()
        {
            source ??= GetComponent<Light>();
            CaptureAuthoredState();
            if (!RegisteredLights.Contains(this))
                RegisteredLights.Add(this);
        }

        void OnDisable()
        {
            RegisteredLights.Remove(this);
            SolLightingDirector.ReleaseManagedLight(this);
        }

        void OnValidate()
        {
            source ??= GetComponent<Light>();
            volumetricScattering = Mathf.Clamp(volumetricScattering, 0f, 4f);
            fadeSeconds = Mathf.Max(0f, fadeSeconds);
        }

        void CaptureAuthoredState()
        {
            if (_captured || source == null)
                return;

            _authoredIntensity = source.intensity;
            _authoredShadows = source.shadows;
            _authoredBakeType = source.lightmapBakeType;
            _authoredEnabled = source.enabled;
            _fade = _authoredEnabled ? 1f : 0f;
            _captured = true;
        }

        internal float ResolveActivation(float dayFactor, float weatherDim)
            => EvaluateActivation(activation, customActivation, dayFactor, weatherDim);

        internal void ApplyResolvedState(
            bool selected,
            bool managedShadow,
            bool volumetric,
            float activationWeight,
            float deltaSeconds)
        {
            CaptureAuthoredState();
            if (source == null || !_captured)
                return;

            IsSelected = selected;
            HasManagedShadow = selected && managedShadow;
            ContributesVolumetrics = selected && volumetric;
            float target = selected ? Mathf.Clamp01(activationWeight) : 0f;
            _fade = fadeSeconds <= 0f
                ? target
                : Mathf.MoveTowards(_fade, target, Mathf.Max(0f, deltaSeconds) / fadeSeconds);

            source.intensity = _authoredIntensity * _fade;
            source.enabled = _authoredEnabled && _fade > 0.0001f;
            source.lightmapBakeType = LightmapBakeType.Realtime;
            source.shadows = HasManagedShadow && source.enabled
                ? _authoredShadows
                : LightShadows.None;
        }

        internal void RestoreAuthoredState()
        {
            if (!_captured || source == null)
                return;

            source.intensity = _authoredIntensity;
            source.shadows = _authoredShadows;
            source.lightmapBakeType = _authoredBakeType;
            source.enabled = _authoredEnabled;
            _fade = _authoredEnabled ? 1f : 0f;
            IsSelected = false;
            HasManagedShadow = false;
            ContributesVolumetrics = false;
        }

        public static float EvaluateActivation(
            SolEnvironmentLightActivation mode,
            AnimationCurve curve,
            float dayFactor,
            float weatherDim)
        {
            dayFactor = Mathf.Clamp01(dayFactor);
            weatherDim = Mathf.Clamp01(weatherDim);
            if (mode == SolEnvironmentLightActivation.AlwaysOn)
                return 1f;
            if (mode == SolEnvironmentLightActivation.CustomCurve)
                return Mathf.Clamp01(curve != null ? curve.Evaluate(dayFactor) : 0f);

            float night = 1f - Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(0.1f, 0.25f, dayFactor));
            float storm = Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(0.55f, 0.85f, weatherDim));
            return Mathf.Max(night, storm);
        }

        public static float CalculateSelectionScore(
            int authoredPriority,
            float range,
            float distance,
            float intensity,
            bool retained)
        {
            distance = Mathf.Max(0.01f, distance);
            float projectedSize = Mathf.Clamp01(Mathf.Max(0f, range) / distance);
            return authoredPriority * 10000f
                   + projectedSize * 1000f
                   + Mathf.Max(0f, intensity) * 10f
                   - distance
                   + (retained ? 250f : 0f);
        }
    }
}
