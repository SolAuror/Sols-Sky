using Sol.ToD;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Throttles expensive sky-derived GI and reflection updates from one lightning-free state.
/// Continuous Trilight colours remain authored by TimeOfDay every environment update.
/// </summary>
public static class SolSkyLightingScheduler
{
    const double MinimumUpdateIntervalSeconds = 1.0;
    const float SunAngleThresholdDegrees = 2f;
    const float CloudThreshold = 0.04f;
    const float ColorThresholdSquared = 0.0025f;

    static double _lastGiTime = double.NegativeInfinity;
    static double _lastProbeTime = double.NegativeInfinity;
    static Vector3 _lastGiSun;
    static Vector3 _lastProbeSun;
    static float _lastGiCloud = float.NaN;
    static float _lastProbeCloud = float.NaN;
    static Color _lastGiAmbient;
    static Color _lastProbeAmbient;
    static ReflectionProbe _probe;
    static int _pendingProbeId = -1;

    public static void Tick(TimeOfDay timeOfDay, Color sky, Color equator, Color ground,
        float cloudCoverage)
    {
        if (!Application.isPlaying || timeOfDay == null)
            return;

        Vector3 sunDirection = timeOfDay.SunDirection;
        Color ambient = (sky + equator + ground) / 3f;
        double now = Time.realtimeSinceStartupAsDouble;

        if (now - _lastGiTime >= MinimumUpdateIntervalSeconds
            && HasMeaningfulChange(sunDirection, cloudCoverage, ambient,
                _lastGiSun, _lastGiCloud, _lastGiAmbient))
        {
            DynamicGI.UpdateEnvironment();
            _lastGiTime = now;
            _lastGiSun = sunDirection;
            _lastGiCloud = cloudCoverage;
            _lastGiAmbient = ambient;
        }

        ReflectionProbe probe = SolWaterManager.Instance != null
            ? SolWaterManager.Instance.reflectionProbe : null;
        if (_probe != probe)
        {
            _probe = probe;
            _pendingProbeId = -1;
            _lastProbeCloud = float.NaN;
        }
        if (_probe == null)
            return;
        if (_pendingProbeId >= 0 && _probe.IsFinishedRendering(_pendingProbeId))
            _pendingProbeId = -1;
        if (_pendingProbeId >= 0 || now - _lastProbeTime < MinimumUpdateIntervalSeconds)
            return;
        if (!HasMeaningfulChange(sunDirection, cloudCoverage, ambient,
            _lastProbeSun, _lastProbeCloud, _lastProbeAmbient))
            return;

        if (_probe.mode == ReflectionProbeMode.Realtime)
        {
            _probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            _probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
        }
        _pendingProbeId = _probe.RenderProbe();
        _lastProbeTime = now;
        _lastProbeSun = sunDirection;
        _lastProbeCloud = cloudCoverage;
        _lastProbeAmbient = ambient;
    }

    internal static bool HasMeaningfulChange(Vector3 sun, float cloud, Color ambient,
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
        _lastGiTime = _lastProbeTime = double.NegativeInfinity;
        _lastGiCloud = _lastProbeCloud = float.NaN;
        _probe = null;
        _pendingProbeId = -1;
    }
}
