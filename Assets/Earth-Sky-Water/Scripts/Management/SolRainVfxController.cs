using Sol.Environment;
using Sol.ToD;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Camera-following Sol precipitation presentation: rain, rain mist, snowfall, and the
/// near-camera blowing-snow layer that sells a blizzard. Weather owns intensity and wind;
/// gameplay and a lightweight upward probe own shelter exposure.
///
/// The type name predates snow. It is kept so the prefab binding and the environment
/// execution-order contract stay intact.
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

    [Header("Snow")]
    [SerializeField] Material snowMaterial;
    [SerializeField] Material blowingSnowMaterial;
    [Tooltip("Terminal fall speed of a snowflake. Snow falls roughly 15x slower than rain.")]
    [SerializeField, Min(0.05f)] float snowFallSpeed = 1.6f;
    [SerializeField, Min(0f)] float maxSnowEmissionRate = 1800f;
    [SerializeField, Range(64, 10000)] int maxSnowParticles = 7000;
    [SerializeField] Color snowColor = new(0.92f, 0.95f, 1f, 0.85f);
    [Tooltip("Lateral flutter applied to falling snow, scaled by wind speed.")]
    [SerializeField, Range(0f, 3f)] float snowFlutterStrength = 0.9f;
    [Tooltip("How much of the wind a flake actually picks up. A flake has almost no "
        + "momentum, so this sits just under 1; above 1 it outruns the wind and reads wrong.")]
    [SerializeField, Range(0f, 1f)] float snowWindCarry = 0.95f;

    [Tooltip("Near-camera blowing layer. This is what separates a blizzard from snowfall.")]
    [SerializeField] bool enableBlowingSnow = true;
    [Tooltip("Wind speed (m/s) at which the blowing layer starts to appear.")]
    [SerializeField, Min(0f)] float blowingWindThreshold = 9f;
    [Tooltip("Wind speed (m/s) at which the blowing layer reaches full strength.")]
    [SerializeField, Min(0.1f)] float blowingWindFull = 18f;
    [SerializeField, Min(0f)] float maxBlowingEmissionRate = 3600f;
    [SerializeField, Range(64, 20000)] int maxBlowingParticles = 7000;
    [Tooltip("Per-particle spread in the blowing layer's speed. Identical velocities make "
        + "every streak parallel, which perspective then converges into a warp tunnel.")]
    [SerializeField, Range(0f, 0.9f)] float blowingSpeedSpread = 0.45f;

    [Tooltip("Largest distance a flake may drift in one lifetime, as a multiple of the "
        + "volume radius. Lifetime is shortened in strong wind to respect it: an emitter "
        + "stretched to the full physical drift would spread the budget far too thin.")]
    [SerializeField, Range(0.5f, 6f)] float snowMaxDriftRadii = 2f;

    static readonly int UnderwaterFactorId = Shader.PropertyToID("_UnderwaterFactor");
    static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    static readonly int SoftEdgeId = Shader.PropertyToID("_SoftEdge");

    ParticleSystem _rain;
    ParticleSystem _mist;
    ParticleSystem _snow;
    ParticleSystem _blowingSnow;
    Material _runtimeMaterial;
    Material _runtimeSnowMaterial;
    Material _runtimeBlowingMaterial;
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
    public float EffectiveSnowIntensity { get; private set; }
    /// <summary>Blowing-snow layer strength after the wind threshold, for audio hooks.</summary>
    public float EffectiveBlowingSnow { get; private set; }
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
        EffectiveSnowIntensity = 0f;
        EffectiveBlowingSnow = 0f;
        StopAndDestroy(ref _rain);
        StopAndDestroy(ref _mist);
        StopAndDestroy(ref _snow);
        StopAndDestroy(ref _blowingSnow);
        if (_runtimeRoot != null)
            DestroyRuntimeObject(_runtimeRoot.gameObject);
        _runtimeRoot = null;

        if (_runtimeMaterial != null)
            DestroyRuntimeObject(_runtimeMaterial);
        _runtimeMaterial = null;

        if (_runtimeSnowMaterial != null)
            DestroyRuntimeObject(_runtimeSnowMaterial);
        _runtimeSnowMaterial = null;

        if (_runtimeBlowingMaterial != null)
            DestroyRuntimeObject(_runtimeBlowingMaterial);
        _runtimeBlowingMaterial = null;

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
            ApplyIntensity(0f, 0f, 0f, 0f, false, Vector3.right, 0f, 0f, 0f,
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
        SolEnvironmentState environment = SolEnvironmentWorld.ResolveState();
        SolEnvironmentWindState wind = environment.Wind;
        // Rain and snow are read from the environment's temperature split rather than from
        // SolWeatherState.RainIntensity, which is total precipitation. Reading the total
        // here is what made rain fall at -10 C.
        SolEnvironmentWeatherState precipitation = environment.Weather;
        // Submersion comes from the _UnderwaterFactor global rather than from the legacy
        // UnderwaterVolumeController. Both water paths publish that contract -- Water 2
        // from SolWaterRendererFeature, Water 1 from the controller -- but the controller
        // yields entirely while Water 2 is live, so asking it directly left rain falling
        // through the camera underwater in every Water 2 scene. The atmosphere feature
        // reads the same global on the same threshold.
        float underwaterExposure = Shader.GetGlobalFloat(UnderwaterFactorId) > 0.5f ? 0f : 1f;
        float exposure = _manualExposure * _shelterExposure * underwaterExposure;
        EffectiveRainIntensity = Mathf.Clamp01(precipitation.Rain * exposure);
        EffectiveSnowIntensity = Mathf.Clamp01(precipitation.Snow * exposure);

        // The blowing layer is a wind phenomenon, not a precipitation one: it needs snow
        // present and a wind strong enough to lift it. Below the threshold a blizzard
        // reads as ordinary snowfall, which is the point.
        float windLift = enableBlowingSnow
            ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(
                blowingWindThreshold, Mathf.Max(blowingWindThreshold + 0.1f, blowingWindFull),
                wind.Speed))
            : 0f;
        EffectiveBlowingSnow = Mathf.Clamp01(EffectiveSnowIntensity * windLift);

        float day = timeOfDay != null ? timeOfDay.DayFactor : 1f;
        ApplyIntensity(EffectiveRainIntensity, EffectiveSnowIntensity, EffectiveBlowingSnow,
            visualScale, worldRunning, wind.Direction, wind.Speed, day, state.LightningFlash,
            manualEditorSimulation);

#if UNITY_EDITOR
        if (manualEditorSimulation && editorDeltaSeconds > 0f)
        {
            // ParticleSystem does not advance from an edit-mode player-loop update.
            // Simulate explicitly using the same bounded editor tick that moves the
            // camera-follow volume and updates its weather inputs.
            _rain?.Simulate(editorDeltaSeconds, true, false, true);
            _mist?.Simulate(editorDeltaSeconds, true, false, true);
            _snow?.Simulate(editorDeltaSeconds, true, false, true);
            _blowingSnow?.Simulate(editorDeltaSeconds, true, false, true);
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

    enum PrecipitationKind { Rain, Mist, Snow, BlowingSnow }

    void ApplyIntensity(
        float rainIntensity,
        float snowIntensity,
        float blowingIntensity,
        float simulationScale,
        bool worldRunning,
        Vector3 windDirection,
        float windSpeedMetresPerSecond,
        float daylight,
        float lightning,
        bool manualSimulation)
    {
        ConfigureLiveSystem(_rain, rainIntensity, simulationScale, worldRunning,
            windDirection, windSpeedMetresPerSecond, daylight, lightning,
            PrecipitationKind.Rain, manualSimulation);
        ConfigureLiveSystem(_mist, enableMist ? rainIntensity * mistRatio : 0f, simulationScale,
            worldRunning, windDirection, windSpeedMetresPerSecond, daylight, lightning,
            PrecipitationKind.Mist, manualSimulation);
        ConfigureLiveSystem(_snow, snowIntensity, simulationScale, worldRunning,
            windDirection, windSpeedMetresPerSecond, daylight, lightning,
            PrecipitationKind.Snow, manualSimulation);
        ConfigureLiveSystem(_blowingSnow, blowingIntensity, simulationScale, worldRunning,
            windDirection, windSpeedMetresPerSecond, daylight, lightning,
            PrecipitationKind.BlowingSnow, manualSimulation);
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
        PrecipitationKind kind,
        bool manualSimulation)
    {
        if (system == null) return;

        bool snowy = kind is PrecipitationKind.Snow or PrecipitationKind.BlowingSnow;

        var main = system.main;
        main.simulationSpeed = Mathf.Max(0.0001f, simulationScale);
        // Snow is a diffuse reflector rather than a specular streak: it holds much more of
        // its brightness at night than rain does, and must not go black in a storm.
        float brightness = snowy
            ? Mathf.Clamp(0.55f + daylight * 0.35f + lightning * 0.15f, 0.42f, 1f)
            : Mathf.Clamp(0.35f + daylight * 0.45f + lightning * 0.2f, 0.25f, 1f);
        Color source = snowy ? snowColor : rainColor;
        Color color = source * brightness;
        float alphaScale = kind switch
        {
            PrecipitationKind.Mist => 0.65f,
            PrecipitationKind.BlowingSnow => 0.8f,
            _ => 1f,
        };
        color.a = source.a * Mathf.Clamp01(intensity * alphaScale);
        main.startColor = color;

        var emission = system.emission;
        // ParticleSystem emission is measured in simulation time. Compensate
        // for accelerated simulation so visible density stays bounded.
        float densityCompensation = 1f / Mathf.Max(1f, simulationScale);
        float baseRate = kind switch
        {
            PrecipitationKind.Snow => maxSnowEmissionRate,
            PrecipitationKind.BlowingSnow => maxBlowingEmissionRate,
            _ => maxEmissionRate,
        };
        emission.rateOverTime = baseRate * Mathf.Clamp01(intensity)
                              * densityCompensation
                              * (kind == PrecipitationKind.Mist ? 0.3f : 1f);

        Vector3 wind = windDirection.sqrMagnitude > 0.0001f ? windDirection.normalized : Vector3.right;
        var velocity = system.velocityOverLifetime;
        // A flake has almost no momentum of its own, so it is carried at close to the full
        // wind speed; a raindrop is only deflected by it. Nothing should exceed the wind.
        float carry = kind switch
        {
            PrecipitationKind.Snow => snowWindCarry,
            PrecipitationKind.BlowingSnow => 1f,
            _ => 1f,
        };
        float driftSpeed = windSpeedMetresPerSecond * windInfluence * carry;

        if (snowy)
        {
            // Identical velocities leave every particle on a parallel track, which
            // perspective converges to a single vanishing point -- the "driving through
            // snow" tunnel. A per-particle speed spread breaks the convergence.
            float spread = kind == PrecipitationKind.BlowingSnow ? blowingSpeedSpread : 0.2f;
            velocity.x = new ParticleSystem.MinMaxCurve(
                wind.x * driftSpeed * (1f - spread), wind.x * driftSpeed * (1f + spread));
            velocity.z = new ParticleSystem.MinMaxCurve(
                wind.z * driftSpeed * (1f - spread), wind.z * driftSpeed * (1f + spread));
        }
        else
        {
            velocity.x = wind.x * driftSpeed;
            velocity.z = wind.z * driftSpeed;
        }

        if (snowy)
        {
            // Snow falls slowly enough that the wind carries it clear of a camera-centred
            // box long before it descends past the eye, so it only ever appeared to stream
            // overhead. Aim the emitter downwind and push it upwind by half the distance a
            // particle will drift in its lifetime: the volume it sweeps then contains the
            // camera instead of starting at it.
            //
            // Lifetime is capped by drift rather than by fall time alone. A flake really
            // does travel 170 m in eight seconds of gale, but an emitter stretched to
            // cover that spreads the particle budget over a volume far too large to read
            // as snow. Shortening the life in strong wind keeps the visible density up,
            // and a blizzard flake is nearly horizontal anyway, so little fall is lost.
            float maxDrift = Mathf.Max(1f, radius * snowMaxDriftRadii);
            float fallLifetime = kind == PrecipitationKind.BlowingSnow
                ? 1.1f
                : Mathf.Clamp(height * 1.2f / Mathf.Max(0.05f, snowFallSpeed), 0.5f, 8f);
            float lifetime = driftSpeed > 0.05f
                ? Mathf.Min(fallLifetime, Mathf.Max(0.5f, maxDrift / driftSpeed))
                : fallLifetime;
            main.startLifetime = lifetime;
            float drift = driftSpeed * lifetime;
            Transform emitter = system.transform;
            emitter.rotation = driftSpeed > 0.05f
                ? Quaternion.LookRotation(wind, Vector3.up)
                : Quaternion.identity;

            var shape = system.shape;
            Vector3 scale = shape.scale;
            // Stretch the box along the wind so a gale cannot outrun its own emitter.
            scale.z = kind == PrecipitationKind.BlowingSnow
                ? radius + drift
                : radius * 2f + drift;
            shape.scale = scale;

            Vector3 position = shape.position;
            position.z = -drift * 0.5f;
            shape.position = position;

            if (kind == PrecipitationKind.Snow)
            {
                // Flutter is what stops snow reading as slow rain. It scales with wind so
                // calm snow falls almost straight and a blizzard tears it sideways.
                var noise = system.noise;
                noise.strength = snowFlutterStrength
                    * (0.35f + Mathf.Clamp01(windSpeedMetresPerSecond / 20f) * 1.4f);
            }
            else
            {
                // Blowing snow is turbulent, not laminar. Without this it is a sheet of
                // parallel lines however much speed spread it has.
                var noise = system.noise;
                noise.strength = 1.6f + Mathf.Clamp01(windSpeedMetresPerSecond / 24f) * 3.4f;
            }
        }

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
            _runtimeMaterial = CreateRuntimeMaterial(rainMaterial,
                "Sol/RainParticle", "SolRainParticle");
        }

        if (_runtimeSnowMaterial == null)
        {
            _runtimeSnowMaterial = CreateRuntimeMaterial(snowMaterial,
                "Sol/SnowParticle", "SolSnowParticle");
            ApplyFallbackFlake(_runtimeSnowMaterial, "Sol_SnowFlake", 0.35f);
        }

        if (_runtimeBlowingMaterial == null)
        {
            _runtimeBlowingMaterial = CreateRuntimeMaterial(blowingSnowMaterial,
                "Sol/SnowParticle", "SolSnowParticle");
            ApplyFallbackFlake(_runtimeBlowingMaterial, "Sol_BlowingSnow", 0.1f);
        }

        if (_rain == null) _rain = CreateSystem("Rain Streaks", PrecipitationKind.Rain);
        if (enableMist && _mist == null) _mist = CreateSystem("Rain Mist", PrecipitationKind.Mist);
        if (_snow == null) _snow = CreateSystem("Snow Fall", PrecipitationKind.Snow);
        if (enableBlowingSnow && _blowingSnow == null)
            _blowingSnow = CreateSystem("Blowing Snow", PrecipitationKind.BlowingSnow);
    }

    Material CreateRuntimeMaterial(Material authored, string shaderName, string fallbackName)
    {
        Material material = null;
        if (authored != null)
            material = new Material(authored);
        else
        {
            Shader shader = Sol.Environment.SolAssetResolver.ResolveShader(
                null, shaderName, fallbackName);
            if (shader != null) material = new Material(shader);
        }

        if (material != null)
            material.hideFlags = HideFlags.HideAndDontSave;
        return material;
    }

    /// <summary>
    /// Supplies the shipped flake art when no material was authored on the prefab, so a
    /// scene that predates snow shows flakes rather than untextured white quads.
    /// </summary>
    static void ApplyFallbackFlake(Material material, string textureName, float softEdge)
    {
        if (material == null || !material.HasProperty(BaseMapId))
            return;

        if (material.HasProperty(SoftEdgeId))
            material.SetFloat(SoftEdgeId, softEdge);

        if (material.GetTexture(BaseMapId) != null)
            return;

#if UNITY_EDITOR
        string[] found = AssetDatabase.FindAssets(textureName + " t:Texture2D");
        if (found.Length > 0)
        {
            Texture2D flake = AssetDatabase.LoadAssetAtPath<Texture2D>(
                AssetDatabase.GUIDToAssetPath(found[0]));
            if (flake != null)
                material.SetTexture(BaseMapId, flake);
        }
#endif
    }

    ParticleSystem CreateSystem(string systemName, PrecipitationKind kind)
    {
        GameObject go = new(systemName) { hideFlags = HideFlags.DontSave };
        go.transform.SetParent(_runtimeRoot, false);
        ParticleSystem particles = go.AddComponent<ParticleSystem>();

        bool mist = kind == PrecipitationKind.Mist;
        bool snow = kind == PrecipitationKind.Snow;
        bool blowing = kind == PrecipitationKind.BlowingSnow;

        var main = particles.main;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = kind switch
        {
            PrecipitationKind.Mist => Mathf.Max(64, maxParticles / 4),
            PrecipitationKind.Snow => maxSnowParticles,
            PrecipitationKind.BlowingSnow => maxBlowingParticles,
            _ => maxParticles,
        };
        main.startLifetime = kind switch
        {
            PrecipitationKind.Mist => 0.8f,
            // Long enough for a flake to fall from the top of the volume to below the eye.
            // Capped because lifetime multiplies into the upwind offset, and an eight
            // second flake in a gale would need a 200 m emitter.
            PrecipitationKind.Snow => Mathf.Clamp(
                height * 1.2f / Mathf.Max(0.05f, snowFallSpeed), 0.5f, 8f),
            PrecipitationKind.BlowingSnow => 1.1f,
            _ => Mathf.Max(0.2f, height / fallSpeed),
        };
        main.startSize = kind switch
        {
            PrecipitationKind.Mist => new ParticleSystem.MinMaxCurve(0.04f, 0.12f),
            PrecipitationKind.Snow => new ParticleSystem.MinMaxCurve(0.05f, 0.14f),
            PrecipitationKind.BlowingSnow => new ParticleSystem.MinMaxCurve(0.02f, 0.05f),
            _ => new ParticleSystem.MinMaxCurve(0.025f, 0.055f),
        };
        main.startSpeed = 0f;
        if (snow || blowing)
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

        var emission = particles.emission;
        emission.rateOverTime = 0f;

        var shape = particles.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        if (blowing)
        {
            // The donor's second precipitation slot sat a few metres from the camera.
            // That near layer is what reads as a whiteout gust rather than as distant
            // weather, so it gets its own small, dense volume.
            shape.scale = new Vector3(radius, height * 0.5f, radius);
            shape.position = new Vector3(0f, -height * 0.28f, 0f);
        }
        else if (snow)
        {
            // A full-height column rather than the thin ceiling slab rain uses: snow has to
            // be visible falling through the view, not just arriving at the top of it.
            shape.scale = new Vector3(radius * 2f, height, radius * 2f);
            shape.position = new Vector3(0f, height * 0.1f, 0f);
        }
        else
        {
            shape.scale = new Vector3(radius * 2f, mist ? 2f : 0.5f, radius * 2f);
        }

        var velocity = particles.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.World;
        velocity.y = kind switch
        {
            PrecipitationKind.Mist => -2f,
            PrecipitationKind.Snow => -snowFallSpeed,
            // Blowing snow is being driven along the ground, not falling out of the sky.
            PrecipitationKind.BlowingSnow => -snowFallSpeed * 0.35f,
            _ => -fallSpeed,
        };

        if (snow || blowing)
        {
            var noise = particles.noise;
            noise.enabled = true;
            noise.quality = ParticleSystemNoiseQuality.Medium;
            // The blowing layer is small, fast and close, so it needs a tighter, faster
            // field than drifting snowfall or the turbulence reads as a slow wobble.
            noise.frequency = blowing ? 1.1f : 0.35f;
            noise.scrollSpeed = blowing ? 1.6f : 0.4f;
            noise.damping = false;
            noise.strength = snowFlutterStrength;

            var rotation = particles.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(-1.2f, 1.2f);
        }

        ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = mist || snow
            ? ParticleSystemRenderMode.Billboard
            : ParticleSystemRenderMode.Stretch;
        // Blowing snow was rendered like a long rain streak, which at gale speed drew
        // metres-long parallel lines converging on the heading -- a hyperspace tunnel
        // rather than weather. Real blowing snow is a dense haze of short dashes, so the
        // stretch is now barely more than a motion smear.
        renderer.velocityScale = blowing ? 0.012f : mist || snow ? 0f : 0.08f;
        renderer.lengthScale = blowing ? 1.1f : mist || snow ? 1f : 2.5f;
        renderer.sharedMaterial = kind switch
        {
            PrecipitationKind.Snow => _runtimeSnowMaterial,
            PrecipitationKind.BlowingSnow => _runtimeBlowingMaterial,
            _ => _runtimeMaterial,
        };
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
