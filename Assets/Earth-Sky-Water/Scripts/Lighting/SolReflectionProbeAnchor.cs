using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sol.Lighting
{
    /// <summary>Opt-in dynamic environment-probe anchor scheduled by SolLightingDirector.</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ReflectionProbe))]
    public sealed class SolReflectionProbeAnchor : MonoBehaviour
    {
        static readonly List<SolReflectionProbeAnchor> RegisteredAnchors = new(16);

        [SerializeField] ReflectionProbe source;
        [SerializeField, Range(0f, 1f)] float waterRelevance;
        [SerializeField] bool excludeWaterLayer = true;
        [SerializeField] LayerMask additionalExcludedLayers;

        int _authoredCullingMask;
        bool _captured;
        int _pendingRenderId = -1;
        double _lastRequestTime = double.NegativeInfinity;
        double _lastCompletionTime = double.NegativeInfinity;
        Vector3 _lastSunDirection;
        float _lastCloudCoverage = float.NaN;
        Color _lastAmbient;

        public ReflectionProbe Source => source;
        public float WaterRelevance => waterRelevance;
        public bool IsRenderPending => _pendingRenderId >= 0;
        public double LastRequestTime => _lastRequestTime;
        public double LastCompletionTime => _lastCompletionTime;
        internal static IReadOnlyList<SolReflectionProbeAnchor> Registered
            => RegisteredAnchors;

        public Bounds WorldInfluenceBounds
        {
            get
            {
                if (source == null)
                    return new Bounds(transform.position, Vector3.zero);
                Vector3 scale = transform.lossyScale;
                scale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                return new Bounds(
                    transform.TransformPoint(source.center),
                    Vector3.Scale(source.size, scale));
            }
        }

        void Reset() => source = GetComponent<ReflectionProbe>();

        void OnEnable()
        {
            source ??= GetComponent<ReflectionProbe>();
            CaptureAuthoredState();
            if (!RegisteredAnchors.Contains(this))
                RegisteredAnchors.Add(this);
        }

        void OnDisable()
        {
            RegisteredAnchors.Remove(this);
            RestoreCaptureSettings();
            _pendingRenderId = -1;
        }

        void OnValidate()
        {
            source ??= GetComponent<ReflectionProbe>();
            waterRelevance = Mathf.Clamp01(waterRelevance);
        }

        void CaptureAuthoredState()
        {
            if (_captured || source == null)
                return;
            _authoredCullingMask = source.cullingMask;
            _captured = true;
        }

        internal bool NeedsRefresh(
            double now,
            float minimumInterval,
            Vector3 sunDirection,
            float cloudCoverage,
            Color ambient)
            => !IsRenderPending
               && now - _lastRequestTime >= minimumInterval
               && SolSkyLightingScheduler.HasMeaningfulChange(
                   sunDirection, cloudCoverage, ambient,
                   _lastSunDirection, _lastCloudCoverage, _lastAmbient);

        internal int BeginRender(
            double now,
            Vector3 sunDirection,
            float cloudCoverage,
            Color ambient)
        {
            if (source == null || source.mode != ReflectionProbeMode.Realtime)
                return -1;

            CaptureAuthoredState();
            int captureMask = _authoredCullingMask & ~additionalExcludedLayers.value;
            if (excludeWaterLayer)
            {
                int waterLayer = LayerMask.NameToLayer("Water");
                if (waterLayer >= 0)
                    captureMask &= ~(1 << waterLayer);
            }
            source.cullingMask = captureMask;
            source.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            source.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
            _pendingRenderId = source.RenderProbe();
            if (_pendingRenderId < 0)
            {
                RestoreCaptureSettings();
                return -1;
            }

            _lastRequestTime = now;
            _lastSunDirection = sunDirection;
            _lastCloudCoverage = cloudCoverage;
            _lastAmbient = ambient;
            return _pendingRenderId;
        }

        internal bool PollCompletion(double now)
        {
            if (_pendingRenderId < 0)
                return true;
            if (source != null && !source.IsFinishedRendering(_pendingRenderId))
                return false;

            _pendingRenderId = -1;
            _lastCompletionTime = now;
            RestoreCaptureSettings();
            return true;
        }

        void RestoreCaptureSettings()
        {
            if (_captured && source != null)
                source.cullingMask = _authoredCullingMask;
        }

        public static float CalculatePriorityScore(
            float distance,
            bool visibleInfluence,
            float waterRelevance,
            float stalenessSeconds)
            => (visibleInfluence ? 10000f : 0f)
               + Mathf.Clamp01(waterRelevance) * 2000f
               + Mathf.Clamp(Mathf.Max(0f, stalenessSeconds), 0f, 600f) * 10f
               - Mathf.Max(0f, distance);
    }
}
