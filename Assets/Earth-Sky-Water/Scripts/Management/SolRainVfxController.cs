using Sol.Environment;
using Sol.ToD;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Camera-following Sol rain presentation. Weather owns intensity and wind;
/// gameplay and a lightweight upward probe own shelter exposure.
/// </summary>
[ExecuteAlways]
[DefaultExecutionOrder(100)]
public sealed class SolRainVfxController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] SolWeatherManager weatherManager;
    [SerializeField] TimeOfDay timeOfDay;
    [SerializeField] Camera trackedCamera;
    [SerializeField] Material rainMaterial;

    [Header("Rain Volume")]
    [SerializeField, Min(1f)] float radius = 18f;
    [SerializeField, Min(1f)] float height = 18f;
    [SerializeField, Min(1f)] float fallSpeed = 28f;
    [SerializeField, Range(64, 10000)] int maxParticles = 2400;
    [SerializeField, Min(0f)] float maxEmissionRate = 850f;
    [SerializeField, Range(0f, 1f)] float windInfluence = 1f;
    [SerializeField, Range(1f, 100f)] float maxParticleTimeScale = 10f;
    [SerializeField] Color rainColor = new(0.68f, 0.78f, 0.9f, 0.42f);

    [Header("Shelter")]
    [SerializeField] bool probeShelter = true;
    [SerializeField] LayerMask shelterMask = ~0;
    [SerializeField, Min(0.1f)] float shelterProbeDistance = 30f;
    [SerializeField, Min(0f)] float shelterProbeRadius = 0.15f;
    [SerializeField, Min(0.02f)] float shelterProbeInterval = 0.15f;
    [SerializeField, Min(0f)] float exposureFadeSpeed = 5f;

    [Header("Mist")]
    [SerializeField] bool enableMist = true;
    [SerializeField, Range(0f, 1f)] float mistRatio = 0.18f;

    static readonly int UnderwaterFactorId = Shader.PropertyToID("_UnderwaterFactor");

    ParticleSystem _rain;
    ParticleSystem _mist;
    Material _runtimeMaterial;
    Transform _runtimeRoot;
    SolEnvironmentCoordinator _coordinator;
    float _manualExposure = 1f;
    float _shelterExposure = 1f;
    float _targetShelterExposure = 1f;
    float _probeTimer;
    float _referenceRetryTimer;
#if UNITY_EDITOR
    double _editorSimulationStamp;
#endif

    public float RainExposure => _manualExposure;
    public float ShelterExposure => _shelterExposure;
    public float EffectiveRainIntensity { get; private set; }
    public Camera ActiveCamera { get; private set; }

    void OnEnable()
    {
#if UNITY_EDITOR
        _editorSimulationStamp = EditorApplication.timeSinceStartup;
#endif
        _coordinator = SolEnvironmentCoordinator.Resolve(this, createIfMissing: true);
        _coordinator?.Register(this);
        ResolveReferences();
        EnsureSystems();
    }

    void OnDisable()
    {
        EffectiveRainIntensity = 0f;
        StopAndDestroy(ref _rain);
        StopAndDestroy(ref _mist);
        if (_runtimeRoot != null)
            DestroyRuntimeObject(_runtimeRoot.gameObject);
        _runtimeRoot = null;

        if (_runtimeMaterial != null)
            DestroyRuntimeObject(_runtimeMaterial);
        _runtimeMaterial = null;

        _coordinator?.Unregister(this);
        _coordinator = null;
    }

    void Update()
    {
        float editorDeltaSeconds = 0f;
        bool manualEditorSimulation = false;
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            double now = EditorApplication.timeSinceStartup;
            editorDeltaSeconds = Mathf.Clamp((float)(now - _editorSimulationStamp), 0f, 0.1f);
            _editorSimulationStamp = now;
            manualEditorSimulation = true;
        }
#endif

        ResolveReferences();
        EnsureSystems();

        ActiveCamera = ResolveActiveCamera();
        if (ActiveCamera == null || _runtimeRoot == null)
        {
            ApplyIntensity(0f, 0f, false, Vector3.right, 0f, 0f, 0f,
                manualEditorSimulation);
            return;
        }

        Vector3 cameraPosition = ActiveCamera.transform.position;
        _runtimeRoot.position = new Vector3(cameraPosition.x, cameraPosition.y + height * 0.45f, cameraPosition.z);

        float presentationDelta = manualEditorSimulation
            ? editorDeltaSeconds
            : timeOfDay != null ? timeOfDay.PresentationDeltaSeconds : Time.deltaTime;
        bool worldRunning = manualEditorSimulation || timeOfDay == null || presentationDelta > 0f;
        float visualScale = manualEditorSimulation
            ? 1f
            : timeOfDay != null && worldRunning
            ? Mathf.Min(timeOfDay.TimeScale, maxParticleTimeScale)
            : worldRunning ? 1f : 0f;

        UpdateShelter(cameraPosition, presentationDelta);

        SolWeatherState state = weatherManager != null ? weatherManager.CurrentState : default;
        SolEnvironmentWindState wind = SolEnvironmentWorld.ResolveState().Wind;
        // Submersion comes from the _UnderwaterFactor global rather than from the legacy
        // UnderwaterVolumeController. Both water paths publish that contract -- Water 2
        // from SolWaterRendererFeature, Water 1 from the controller -- but the controller
        // yields entirely while Water 2 is live, so asking it directly left rain falling
        // through the camera underwater in every Water 2 scene. The atmosphere feature
        // reads the same global on the same threshold.
        float underwaterExposure = Shader.GetGlobalFloat(UnderwaterFactorId) > 0.5f ? 0f : 1f;
        float exposure = _manualExposure * _shelterExposure * underwaterExposure;
        EffectiveRainIntensity = Mathf.Clamp01(state.RainIntensity * exposure);

        float day = timeOfDay != null ? timeOfDay.DayFactor : 1f;
        ApplyIntensity(EffectiveRainIntensity, visualScale, worldRunning,
            wind.Direction, wind.Speed, day, state.LightningFlash, manualEditorSimulation);

#if UNITY_EDITOR
        if (manualEditorSimulation && editorDeltaSeconds > 0f)
        {
            // ParticleSystem does not advance from an edit-mode player-loop update.
            // Simulate explicitly using the same bounded editor tick that moves the
            // camera-follow volume and updates its weather inputs.
            _rain?.Simulate(editorDeltaSeconds, true, false, true);
            _mist?.Simulate(editorDeltaSeconds, true, false, true);
        }
#endif
    }

    public void SetRainExposure(float exposure) => _manualExposure = Mathf.Clamp01(exposure);
    public void ResetRainExposure() => _manualExposure = 1f;

    Camera ResolveActiveCamera()
    {
        if (trackedCamera != null)
            return trackedCamera;

        Camera main = Camera.main;
        if (main != null)
            return main;

#if UNITY_EDITOR
        if (!Application.isPlaying)
            return SceneView.lastActiveSceneView != null
                ? SceneView.lastActiveSceneView.camera
                : null;
#endif
        return null;
    }

    void ResolveReferences()
    {
        _referenceRetryTimer -= Time.unscaledDeltaTime;
        if (_referenceRetryTimer > 0f && (weatherManager == null || timeOfDay == null))
            return;

        bool missingReference = weatherManager == null || timeOfDay == null;
        weatherManager ??= SolWeatherManager.Instance;
        timeOfDay ??= TimeOfDay.ResolveInstance();

        // Missing optional scene authorities are common in small scenes. Do
        // not perform a hierarchy scan every frame while waiting for one to
        // appear; retry shortly so late bootstrap/spawn still works.
        if (missingReference && (weatherManager == null || timeOfDay == null))
            _referenceRetryTimer = 0.5f;
    }

    void UpdateShelter(Vector3 cameraPosition, float deltaSeconds)
    {
        if (!probeShelter)
        {
            _shelterExposure = 1f;
            _targetShelterExposure = 1f;
            return;
        }

        _probeTimer -= Mathf.Max(0f, deltaSeconds);
        if (_probeTimer <= 0f)
        {
            _probeTimer = shelterProbeInterval;
            Vector3 origin = cameraPosition + Vector3.up * 0.05f;
            bool covered = shelterProbeRadius > 0f
                ? Physics.SphereCast(origin, shelterProbeRadius, Vector3.up, out _, shelterProbeDistance, shelterMask, QueryTriggerInteraction.Ignore)
                : Physics.Raycast(origin, Vector3.up, shelterProbeDistance, shelterMask, QueryTriggerInteraction.Ignore);
            _targetShelterExposure = covered ? 0f : 1f;
        }

        _shelterExposure = Mathf.MoveTowards(
            _shelterExposure,
            _targetShelterExposure,
            exposureFadeSpeed * Mathf.Max(0f, deltaSeconds));
    }

    void ApplyIntensity(
        float intensity,
        float simulationScale,
        bool worldRunning,
        Vector3 windDirection,
        float windSpeedMetresPerSecond,
        float daylight,
        float lightning,
        bool manualSimulation)
    {
        ConfigureLiveSystem(_rain, intensity, simulationScale, worldRunning,
            windDirection, windSpeedMetresPerSecond, daylight, lightning, false,
            manualSimulation);
        ConfigureLiveSystem(_mist, enableMist ? intensity * mistRatio : 0f, simulationScale, worldRunning,
            windDirection, windSpeedMetresPerSecond, daylight, lightning, true,
            manualSimulation);
    }

    void ConfigureLiveSystem(
        ParticleSystem system,
        float intensity,
        float simulationScale,
        bool worldRunning,
        Vector3 windDirection,
        float windSpeedMetresPerSecond,
        float daylight,
        float lightning,
        bool mist,
        bool manualSimulation)
    {
        if (system == null) return;

        var main = system.main;
        main.simulationSpeed = Mathf.Max(0.0001f, simulationScale);
        float brightness = Mathf.Clamp(0.35f + daylight * 0.45f + lightning * 0.2f, 0.25f, 1f);
        Color color = rainColor * brightness;
        color.a = rainColor.a * Mathf.Clamp01(intensity * (mist ? 0.65f : 1f));
        main.startColor = color;

        var emission = system.emission;
        // ParticleSystem emission is measured in simulation time. Compensate
        // for accelerated simulation so visible density stays bounded.
        float densityCompensation = 1f / Mathf.Max(1f, simulationScale);
        emission.rateOverTime = maxEmissionRate * Mathf.Clamp01(intensity)
                              * densityCompensation * (mist ? 0.3f : 1f);

        Vector3 wind = windDirection.sqrMagnitude > 0.0001f ? windDirection.normalized : Vector3.right;
        var velocity = system.velocityOverLifetime;
        velocity.x = wind.x * windSpeedMetresPerSecond * windInfluence;
        velocity.z = wind.z * windSpeedMetresPerSecond * windInfluence;

        // Edit mode is advanced explicitly by Simulate after both systems have been
        // configured. Play/Pause does not advance particles there and can leave the
        // inspector reporting a misleading runtime state.
        if (manualSimulation)
            return;

        if (!worldRunning)
        {
            if (system.isPlaying)
                system.Pause(true);
            return;
        }

        if (intensity > 0.001f)
        {
            if (system.isPaused || system.isStopped)
                system.Play(true);
            return;
        }

        // Dry weather is not a time pause: stop producing drops and allow the
        // existing particles to finish instead of freezing stale rain in place.
        if (system.isPaused)
            system.Play(true);
        if (system.isPlaying)
            system.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }

    void EnsureSystems()
    {
        if (_runtimeRoot == null)
        {
            GameObject root = new("_SolRainVFX") { hideFlags = HideFlags.DontSave };
            root.transform.SetParent(transform, false);
            _runtimeRoot = root.transform;
        }

        if (_runtimeMaterial == null)
        {
            if (rainMaterial != null)
                _runtimeMaterial = new Material(rainMaterial);
            else
            {
                Shader shader = Sol.Environment.SolAssetResolver.ResolveShader(
                    null, "Sol/RainParticle", "SolRainParticle");
                if (shader != null) _runtimeMaterial = new Material(shader);
            }

            if (_runtimeMaterial != null)
                _runtimeMaterial.hideFlags = HideFlags.HideAndDontSave;
        }

        if (_rain == null) _rain = CreateSystem("Rain Streaks", false);
        if (enableMist && _mist == null) _mist = CreateSystem("Rain Mist", true);
    }

    ParticleSystem CreateSystem(string systemName, bool mist)
    {
        GameObject go = new(systemName) { hideFlags = HideFlags.DontSave };
        go.transform.SetParent(_runtimeRoot, false);
        ParticleSystem particles = go.AddComponent<ParticleSystem>();

        var main = particles.main;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = mist ? Mathf.Max(64, maxParticles / 4) : maxParticles;
        main.startLifetime = mist ? 0.8f : Mathf.Max(0.2f, height / fallSpeed);
        main.startSize = mist ? new ParticleSystem.MinMaxCurve(0.04f, 0.12f) : new ParticleSystem.MinMaxCurve(0.025f, 0.055f);
        main.startSpeed = 0f;

        var emission = particles.emission;
        emission.rateOverTime = 0f;

        var shape = particles.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(radius * 2f, mist ? 2f : 0.5f, radius * 2f);

        var velocity = particles.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.World;
        velocity.y = mist ? -2f : -fallSpeed;

        ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = mist ? ParticleSystemRenderMode.Billboard : ParticleSystemRenderMode.Stretch;
        renderer.velocityScale = mist ? 0f : 0.08f;
        renderer.lengthScale = mist ? 1f : 2.5f;
        renderer.sharedMaterial = _runtimeMaterial;
        return particles;
    }

    static void StopAndDestroy(ref ParticleSystem system)
    {
        if (system == null) return;
        GameObject go = system.gameObject;
        system = null;
        DestroyRuntimeObject(go);
    }

    static void DestroyRuntimeObject(Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying) Object.Destroy(obj);
        else Object.DestroyImmediate(obj);
    }

    void OnDrawGizmosSelected()
    {
        Camera camera = trackedCamera != null ? trackedCamera : Camera.main;
        Vector3 center = camera != null ? camera.transform.position : transform.position;
        Gizmos.color = new Color(0.3f, 0.65f, 1f, 0.35f);
        Gizmos.DrawWireCube(center + Vector3.up * height * 0.45f, new Vector3(radius * 2f, height, radius * 2f));
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(center, center + Vector3.up * shelterProbeDistance);
    }
}
