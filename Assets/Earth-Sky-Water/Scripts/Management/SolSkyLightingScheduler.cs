using System.Collections.Generic;
using Sol.ToD;
using Sol.Lighting;
using Sol.Environment;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Throttles expensive sky-derived GI and reflection updates from one lightning-free state.
/// Continuous Trilight colours remain authored by TimeOfDay every environment update.
/// </summary>
public static class SolSkyLightingScheduler
{
    public static readonly ProfilerMarker GiUpdateMarker =
        new("Sol.Lighting.DynamicGI");
    public static readonly ProfilerMarker ProbeSchedulingMarker =
        new("Sol.Lighting.ProbeScheduling");

    const double AsyncGiUpdateIntervalSeconds = 1.0;
    const double SynchronousGiUpdateIntervalSeconds = 4.0;
    const double ApvStartupGraceSeconds = 5.0;
    const float SunAngleThresholdDegrees = 2f;
    const float CloudThreshold = 0.04f;
    const float ColorThresholdSquared = 0.0025f;

    static double _lastGiTime = double.NegativeInfinity;
    static Vector3 _lastGiSun;
    static float _lastGiCloud = float.NaN;
    static Color _lastGiAmbient;
    static readonly List<ProbeCandidate> ProbeCandidates = new(16);
    static readonly Plane[] FrustumPlanes = new Plane[6];
    static SolReflectionProbeAnchor _pendingAnchor;
    static double _apvValidationStartedAt = double.NegativeInfinity;
    static bool _warnedMissingApvData;

    public static void Tick(TimeOfDay timeOfDay, Color sky, Color equator, Color ground,
        float cloudCoverage)
    {
        if (!Application.isPlaying || timeOfDay == null)
            return;

        Vector3 sunDirection = timeOfDay.SunDirection;
        Color ambient = (sky + equator + ground) / 3f;
        double now = Time.realtimeSinceStartupAsDouble;
        double giUpdateInterval = ResolveGiUpdateInterval(
            SystemInfo.supportsAsyncGPUReadback);

        if (now - _lastGiTime >= giUpdateInterval
            && HasMeaningfulChange(sunDirection, cloudCoverage, ambient,
                _lastGiSun, _lastGiCloud, _lastGiAmbient))
        {
            // APV sky occlusion consumes Unity's ambient probe. Keep that probe current
            // from the stable (lightning-free) sky, but protect platforms where this call
            // performs a synchronous readback from running it every second.
            SolEnvironmentBudget.AddGiRequest();
            using (GiUpdateMarker.Auto())
                DynamicGI.UpdateEnvironment();
            _lastGiTime = now;
            _lastGiSun = sunDirection;
            _lastGiCloud = cloudCoverage;
            _lastGiAmbient = ambient;
        }

        WarnIfApvDataIsUnavailable(now);
        using (ProbeSchedulingMarker.Auto())
            ScheduleProbe(sunDirection, cloudCoverage, ambient, now);
    }

    public static double ResolveGiUpdateInterval(bool supportsAsyncGpuReadback)
        => supportsAsyncGpuReadback
            ? AsyncGiUpdateIntervalSeconds
            : SynchronousGiUpdateIntervalSeconds;

    public static bool IsApvDataAvailable()
    {
        ProbeReferenceVolume referenceVolume = ProbeReferenceVolume.instance;
        return referenceVolume != null
               && referenceVolume.isInitialized
               && referenceVolume.currentBakingSet != null
               && referenceVolume.DataHasBeenLoaded();
    }

    static void WarnIfApvDataIsUnavailable(double now)
    {
        if (_warnedMissingApvData)
            return;
        if (double.IsNegativeInfinity(_apvValidationStartedAt))
            _apvValidationStartedAt = now;
        if (now - _apvValidationStartedAt < ApvStartupGraceSeconds ||
            IsApvDataAvailable())
            return;

        _warnedMissingApvData = true;
        Debug.LogWarning(
            "[SolSkyLightingScheduler] Adaptive Probe Volume baked data is unavailable. " +
            "Dynamic trilight ambient remains active as the fallback; bake the " +
            "Sol Environment APV set to enable spatial indirect lighting.");
    }

    static void ScheduleProbe(
        Vector3 sunDirection, float cloudCoverage, Color ambient, double now)
    {
        if (_pendingAnchor != null)
        {
            double previousCompletion = _pendingAnchor.LastCompletionTime;
            if (!_pendingAnchor.PollCompletion(now))
                return;
            if (_pendingAnchor.LastCompletionTime > previousCompletion)
                SolEnvironmentBudget.AddProbeCompletion();
            _pendingAnchor = null;
        }

        IReadOnlyList<SolReflectionProbeAnchor> anchors =
            SolReflectionProbeAnchor.Registered;
        if (anchors.Count == 0)
            return;

        float minimumProbeInterval = SolLightingDirector.Active != null
            ? SolLightingDirector.Active.ActiveQualitySettings.ReflectionProbeRefreshInterval
            : SolLightingQualitySettings.ForTier(SolLightingQualityTier.Medium)
                .ReflectionProbeRefreshInterval;
        Camera camera = Camera.main;
        Vector3 cameraPosition = camera != null
            ? camera.transform.position
            : Vector3.zero;
        bool hasFrustum = camera != null;
        if (hasFrustum)
            GeometryUtility.CalculateFrustumPlanes(camera, FrustumPlanes);

        ProbeCandidates.Clear();
        for (int i = 0; i < anchors.Count; i++)
        {
            SolReflectionProbeAnchor anchor = anchors[i];
            ReflectionProbe probe = anchor != null ? anchor.Source : null;
            if (probe == null || !anchor.isActiveAndEnabled ||
                probe.mode != ReflectionProbeMode.Realtime ||
                !anchor.NeedsRefresh(now, minimumProbeInterval,
                    sunDirection, cloudCoverage, ambient))
                continue;

            Bounds bounds = anchor.WorldInfluenceBounds;
            float distance = Vector3.Distance(cameraPosition, bounds.ClosestPoint(cameraPosition));
            bool visible = hasFrustum && GeometryUtility.TestPlanesAABB(
                FrustumPlanes, bounds);
            float staleness = double.IsNegativeInfinity(anchor.LastCompletionTime)
                ? 600f
                : (float)(now - anchor.LastCompletionTime);
            float score = SolReflectionProbeAnchor.CalculatePriorityScore(
                distance, visible, anchor.WaterRelevance, staleness);
            ProbeCandidates.Add(new ProbeCandidate(anchor, score));
        }

        if (ProbeCandidates.Count == 0)
            return;
        ProbeCandidates.Sort(ProbeCandidate.Compare);
        SolReflectionProbeAnchor selected = ProbeCandidates[0].Anchor;
        if (selected.BeginRender(now, sunDirection, cloudCoverage, ambient) < 0)
            return;

        SolEnvironmentBudget.AddProbeRequest();
        _pendingAnchor = selected;
    }

    public static bool HasMeaningfulChange(Vector3 sun, float cloud, Color ambient,
        Vector3 previousSun, float previousCloud, Color previousAmbient)
        => float.IsNaN(previousCloud)
        || Vector3.Angle(sun, previousSun) >= SunAngleThresholdDegrees
        || Mathf.Abs(cloud - previousCloud) >= CloudThreshold
        || ColorDistanceSquared(ambient, previousAmbient) >= ColorThresholdSquared;

    static float ColorDistanceSquared(Color a, Color b)
    {
        float r = a.r - b.r;
        float g = a.g - b.g;
        float blue = a.b - b.b;
        return r * r + g * g + blue * blue;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset()
    {
        _lastGiTime = double.NegativeInfinity;
        _lastGiCloud = float.NaN;
        _pendingAnchor = null;
        _apvValidationStartedAt = double.NegativeInfinity;
        _warnedMissingApvData = false;
        ProbeCandidates.Clear();
    }

    readonly struct ProbeCandidate
    {
        public readonly SolReflectionProbeAnchor Anchor;
        public readonly float Score;

        public ProbeCandidate(SolReflectionProbeAnchor anchor, float score)
        {
            Anchor = anchor;
            Score = score;
        }

        public static int Compare(ProbeCandidate a, ProbeCandidate b)
        {
            int score = b.Score.CompareTo(a.Score);
            if (score != 0)
                return score;
            int aId = a.Anchor != null ? a.Anchor.GetInstanceID() : int.MaxValue;
            int bId = b.Anchor != null ? b.Anchor.GetInstanceID() : int.MaxValue;
            return aId.CompareTo(bId);
        }
    }
}
