using UnityEngine;
using UnityEngine.Rendering;
using Sol.ToD;

/// <summary>
/// Manages interactive water ripples - waves that expand outward from
/// objects moving through the water surface.
///
/// Singleton with two backends:
///
///   GPU SIMULATION (default, play mode)
///     A damped wave-equation simulation ping-ponged between two render
///     textures (Hidden/Sol/RippleSim). The sim window follows a focus
///     point (player/camera) and is published to the water shader as
///     _Sol_RippleSimTex + region globals. Ripples propagate, reflect and
///     interfere naturally; rain intensity from SolWaterManager adds
///     procedural droplets. Unlimited concurrent sources.
///
///   ANALYTIC FALLBACK (edit mode, or useGpuSim = false)
///     The original fixed-size ring buffer of up to 32 analytic ring
///     ripples, uploaded as _Sol_Ripples for the shader's per-pixel loop.
///
/// Gameplay code emits via WaterRippleManager.Instance.Emit() either way.
/// </summary>
[ExecuteAlways]
public class WaterRippleManager : MonoBehaviour
{
    // --- Singleton -------------------------------------------------------
    public static WaterRippleManager Instance { get; private set; }

    // --- GPU Simulation ----------------------------------------------------
    [Header("GPU Simulation")]
    [Tooltip("Use the render-texture wave simulation (play mode only). " +
             "When off (or in edit mode) the analytic 32-ripple fallback is used.")]
    public bool useGpuSim = true;

    [Tooltip("Simulation step shader. Leave null to locate Hidden/Sol/RippleSim " +
             "automatically in the editor; ASSIGN IT for builds so it isn't stripped.")]
    public Shader simShader;

    [Tooltip("Simulation texture resolution (square).")]
    [Range(128, 1024)]
    public int simResolution = 512;

    [Tooltip("World-space size of the simulated window (metres). The window " +
             "follows the focus; ripples fade out at its border.")]
    [Range(10f, 200f)]
    public float simRegionSize = 60f;

    [Tooltip("Focus the sim window follows. Leave null to use Camera.main.")]
    public Transform simFocus;

    [Tooltip("Wave propagation factor per step (c^2). Higher = faster ripples. " +
             "Values above ~0.49 are clamped for stability.")]
    [Range(0.05f, 0.49f)]
    public float simWaveSpeed = 0.35f;

    [Tooltip("Velocity damping per step. Lower = ripples die out faster.")]
    [Range(0.9f, 1f)]
    public float simDamping = 0.985f;

    [Tooltip("How strongly the simulated height field perturbs water normals.")]
    [Range(0.1f, 20f)]
    public float simNormalStrength = 6f;

    [Tooltip("World-space radius of an injected splat (metres).")]
    [Range(0.1f, 3f)]
    public float injectionRadius = 0.6f;

    [Tooltip("Height (metres) an Emit() with strength 1 displaces the sim surface.")]
    [Range(0.01f, 1f)]
    public float injectionAmplitude = 0.25f;

    [Header("Rain Ripples")]
    [Tooltip("Spawn procedural rain droplets scaled by SolWaterManager.rainIntensity.")]
    public bool rainRipples = true;

    [Tooltip("Droplets injected per simulation step at full rain intensity.")]
    [Range(0, 16)]
    public int rainMaxDropsPerStep = 6;

    [Tooltip("Height dent per droplet (metres).")]
    [Range(0.005f, 0.3f)]
    public float rainDropletStrength = 0.05f;

    [Tooltip("World-space droplet radius (metres).")]
    [Range(0.05f, 1f)]
    public float rainDropletRadius = 0.25f;

    // --- Debug ---------------------------------------------------------------
    [Header("Debug")]
    [Tooltip("Continuously emit a test splash at the sim focus every 0.75s " +
             "(play mode). Use to verify the simulation without any " +
             "WaterRippleSource in the scene.")]
    public bool debugAutoSplash;

    [Tooltip("Draw the raw simulation texture in the top-left corner of the " +
             "Game view (play mode). Gray = calm; ripples show as light/dark rings.")]
    public bool debugShowSimTexture;

    float _nextDebugSplash;

    /// <summary>Emit one strong test splash at the sim focus position.</summary>
    [ContextMenu("Emit Test Splash")]
    public void EmitTestSplash()
    {
        Vector3 f = GetFocusPosition();
        Emit(new Vector3(f.x, 0f, f.z), 1f);
        Debug.Log($"[WaterRippleManager] Test splash at ({f.x:F1}, {f.z:F1}). " +
                  $"simActive={_simActive}, pending={_pending.Count}", this);
    }

    // --- Analytic Fallback -------------------------------------------------
    [Header("Analytic Fallback Settings")]
    [Tooltip("Expansion speed of each ring (world units / second).")]
    [Range(0.5f, 20f)]
    public float rippleSpeed = 5f;

    [Tooltip("Ring density - higher values produce tighter concentric rings.")]
    [Range(1f, 60f)]
    public float rippleFrequency = 20f;

    [Tooltip("Seconds before a ripple fades out completely.")]
    [Range(0.5f, 10f)]
    public float rippleLifetime = 3f;

    // --- Internals: GPU sim ------------------------------------------------
    const int MaxInjectionsPerStep = 16;
    const float FixedDt = 1f / 60f;
    const int MaxStepsPerFrame = 8;

    Material _simMat;
    readonly RenderTexture[] _rt = new RenderTexture[2];
    readonly RTHandle[] _rtHandles = new RTHandle[2];
    RenderTexture _debugRT;
    int _cur;
    int _appliedResolution = -1;
    Vector2 _regionMin;
    float _timeAcc;
    float _worldTime;
    float _worldDeltaSeconds;
    bool _simActive;

    // Pending injections: xy = world XZ, z = radius (m), w = height delta (m).
    readonly System.Collections.Generic.List<Vector4> _pending = new();
    readonly Vector4[] _injectionArray = new Vector4[MaxInjectionsPerStep];

    // --- Internals: analytic fallback ---------------------------------------
    const int MaxRipples = 32;

    // Ring buffer: each entry is (worldX, worldZ, birthTime, strength)
    readonly Vector4[] _ripples = new Vector4[MaxRipples];
    readonly Vector4[] _gpuRipples = new Vector4[MaxRipples];
    int _writeIndex;
    int _activeCount;

    bool _gpuDirty = true;
    float _lastRippleSpeed = float.NaN;
    float _lastRippleFrequency = float.NaN;
    float _lastRippleLifetime = float.NaN;
    SolEnvironmentCoordinator _environmentCoordinator;
    TimeOfDay _timeOfDay;
    float _referenceRetryTimer;

    /// <summary>Accumulated canonical Sol world seconds used by both ripple backends.</summary>
    public float WorldTime => _worldTime;

    /// <summary>Canonical Sol world seconds consumed during the current frame.</summary>
    public float WorldDeltaSeconds => _worldDeltaSeconds;

    // Shader property IDs - analytic path
    static readonly int _RipplesID         = Shader.PropertyToID("_Sol_Ripples");
    static readonly int _RippleCountID     = Shader.PropertyToID("_Sol_RippleCount");
    static readonly int _RippleSpeedID     = Shader.PropertyToID("_Sol_RippleSpeed");
    static readonly int _RippleFrequencyID = Shader.PropertyToID("_Sol_RippleFrequency");
    static readonly int _RippleLifetimeID  = Shader.PropertyToID("_Sol_RippleLifetime");
    static readonly int _RippleTimeID      = Shader.PropertyToID("_Sol_RippleTime");

    // Shader property IDs - sim globals consumed by Sol.Water
    static readonly int _SimTexID       = Shader.PropertyToID("_Sol_RippleSimTex");
    static readonly int _SimRegionID    = Shader.PropertyToID("_Sol_RippleSimRegion");
    static readonly int _SimShadeID     = Shader.PropertyToID("_Sol_RippleSimParams");

    // Shader property IDs - sim material
    static readonly int _SimParamsID      = Shader.PropertyToID("_SimParams");
    static readonly int _ScrollID         = Shader.PropertyToID("_Scroll");
    static readonly int _InjectionsID     = Shader.PropertyToID("_Injections");
    static readonly int _InjectionCountID = Shader.PropertyToID("_InjectionCount");
    static readonly int _RainID           = Shader.PropertyToID("_Rain");
    static readonly int _DebugGainID      = Shader.PropertyToID("_DebugGain");

    static readonly Vector4 FullScaleBias = new(1f, 1f, 0f, 0f);

    // --- Lifecycle -------------------------------------------------------

    void OnEnable()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[WaterRippleManager] Duplicate instance detected. Destroying this one.", this);
            enabled = false;
            return;
        }
        Instance = this;
        _environmentCoordinator = SolEnvironmentCoordinator.Resolve(this, createIfMissing: true);
        _environmentCoordinator?.Register(this);
        _gpuDirty = true;
    }

    void OnDisable()
    {
        if (Instance == this) Instance = null;

        // Clear GPU data so ripples don't persist after disable.
        Shader.SetGlobalInt(_RippleCountID, 0);
        DisableSim();
        ReleaseSimResources();
        _environmentCoordinator?.Unregister(this);
        _environmentCoordinator = null;
    }

    void Update()
    {
        _referenceRetryTimer -= Time.unscaledDeltaTime;
        if (_timeOfDay == null && _referenceRetryTimer <= 0f)
        {
            _timeOfDay = TimeOfDay.ResolveInstance();
            if (_timeOfDay == null)
                _referenceRetryTimer = 0.5f;
        }
        _worldDeltaSeconds = _timeOfDay != null ? _timeOfDay.WorldDeltaSeconds : Time.deltaTime;
        _worldTime += Mathf.Max(0f, _worldDeltaSeconds);

        bool wantSim = useGpuSim && Application.isPlaying;

        if (wantSim && EnsureSimResources())
        {
            if (debugAutoSplash && _worldTime >= _nextDebugSplash)
            {
                _nextDebugSplash = _worldTime + 0.75f;
                Vector3 f = GetFocusPosition();
                Emit(new Vector3(f.x, 0f, f.z), 0.8f);
            }

            UpdateSim(_worldDeltaSeconds);

            // Keep the analytic per-pixel loop off while the sim runs.
            Shader.SetGlobalInt(_RippleCountID, 0);
        }
        else
        {
            if (_simActive)
                DisableSim();

            UpdateAnalytic();
        }
    }

    // --- Public API ------------------------------------------------------

    /// <summary>
    /// Emit a ripple at the given world position.
    /// Call from WaterRippleSource or any gameplay code.
    /// </summary>
    /// <param name="worldPos">World-space position (only XZ is used).</param>
    /// <param name="strength">Disturbance strength (0.01-1 typical).</param>
    public void Emit(Vector3 worldPos, float strength = 0.15f)
    {
        if (_simActive)
        {
            if (_pending.Count >= MaxInjectionsPerStep)
                _pending.RemoveAt(0);

            // Objects push the surface DOWN; the wave equation rebounds it.
            _pending.Add(new Vector4(
                worldPos.x, worldPos.z,
                injectionRadius,
                -strength * injectionAmplitude));
            return;
        }

        _ripples[_writeIndex] = new Vector4(worldPos.x, worldPos.z, _worldTime, strength);
        _writeIndex = (_writeIndex + 1) % MaxRipples;
        if (_activeCount < MaxRipples) _activeCount++;
        _gpuDirty = true;
    }

    // --- GPU Simulation ----------------------------------------------------

    bool EnsureSimResources()
    {
        if (_simMat == null)
        {
            Shader shader = simShader;
#if UNITY_EDITOR
            if (shader == null)
                shader = Shader.Find("Hidden/Sol/RippleSim");
#endif
            if (shader == null)
            {
                Debug.LogWarning("[WaterRippleManager] Sim shader not assigned; falling back to analytic ripples.", this);
                useGpuSim = false;
                return false;
            }
            _simMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        if (_appliedResolution != simResolution || _rt[0] == null || _rt[1] == null)
        {
            ReleaseRenderTextures();

            RenderTextureFormat fmt = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGFloat)
                ? RenderTextureFormat.RGFloat
                : RenderTextureFormat.RGHalf;

            for (int i = 0; i < 2; i++)
            {
                _rt[i] = new RenderTexture(simResolution, simResolution, 0, fmt)
                {
                    name = $"SolRippleSim_{i}",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _rt[i].Create();
                ClearRT(_rt[i]);
                _rtHandles[i] = RTHandles.Alloc(_rt[i]);
            }

            _appliedResolution = simResolution;
            _cur = 0;
            _timeAcc = 0f;

            // Fresh window centred on the focus.
            Vector3 f = GetFocusPosition();
            float texelWorld = simRegionSize / simResolution;
            _regionMin = SnapToTexel(
                new Vector2(f.x, f.z) - Vector2.one * (simRegionSize * 0.5f), texelWorld);
        }

        _simActive = true;
        return true;
    }

    void UpdateSim(float deltaSeconds)
    {
        _timeAcc += Mathf.Max(0f, deltaSeconds);
        int steps = Mathf.FloorToInt(_timeAcc / FixedDt);

        if (steps > MaxStepsPerFrame && deltaSeconds >= rippleLifetime)
        {
            ClearRT(_rt[0]);
            ClearRT(_rt[1]);
            _pending.Clear();
            _timeAcc = 0f;
            steps = 0;
        }

        if (steps > 0)
        {
            steps = Mathf.Min(steps, MaxStepsPerFrame);
            // Drop any backlog beyond what we run (avoids a death spiral).
            _timeAcc = Mathf.Min(_timeAcc - steps * FixedDt, FixedDt);

            // Recentre the window only when stepping, so the stored state
            // and the published region always describe the same texels.
            float texelWorld = simRegionSize / simResolution;
            Vector3 f = GetFocusPosition();
            Vector2 desiredMin = SnapToTexel(
                new Vector2(f.x, f.z) - Vector2.one * (simRegionSize * 0.5f), texelWorld);
            Vector2 scrollUV = (desiredMin - _regionMin) / simRegionSize;
            _regionMin = desiredMin;

            float c2 = Mathf.Min(simWaveSpeed, 0.49f);

            float rain = 0f;
            if (rainRipples && SolWaterManager.Instance != null)
                rain = Mathf.Clamp01(SolWaterManager.Instance.rainIntensity);

            for (int s = 0; s < steps; s++)
            {
                // Scroll and interactive injections apply on the first
                // sub-step only; rain runs every step.
                _simMat.SetVector(_ScrollID, s == 0 ? (Vector4)scrollUV : Vector4.zero);

                int count = 0;
                if (s == 0 && _pending.Count > 0)
                {
                    count = Mathf.Min(_pending.Count, MaxInjectionsPerStep);
                    for (int i = 0; i < count; i++)
                    {
                        Vector4 p = _pending[i];
                        _injectionArray[i] = new Vector4(
                            (p.x - _regionMin.x) / simRegionSize,
                            (p.y - _regionMin.y) / simRegionSize,
                            p.z / simRegionSize,
                            p.w);
                    }
                    _pending.Clear();
                }
                for (int i = count; i < MaxInjectionsPerStep; i++)
                    _injectionArray[i] = Vector4.zero;

                _simMat.SetInt(_InjectionCountID, count);
                _simMat.SetVectorArray(_InjectionsID, _injectionArray);

                int drops = Mathf.RoundToInt(rain * rainMaxDropsPerStep);
                _simMat.SetVector(_RainID, new Vector4(
                    drops,
                    rainDropletStrength,
                    (_worldTime * 63.19f + s * 17.7f) % 977f,
                    rainDropletRadius / simRegionSize));

                _simMat.SetVector(_SimParamsID,
                    new Vector4(c2, simDamping, 1f / simResolution, 0f));

                // Blitter draws a matrix-free fullscreen triangle - safe to
                // run from Update(), outside any camera render (a matrix-
                // based Graphics.Blit can rasterize nowhere there).
                CommandBuffer cmd = CommandBufferPool.Get("SolRippleSim");
                cmd.SetRenderTarget(_rt[1 - _cur]);
                Blitter.BlitTexture(cmd, _rtHandles[_cur], FullScaleBias, _simMat, 0);
                Graphics.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
                _cur = 1 - _cur;
            }
        }

        // Publish to the water shader.
        float texel = simRegionSize / simResolution;
        Shader.SetGlobalTexture(_SimTexID, _rt[_cur]);
        Shader.SetGlobalVector(_SimRegionID,
            new Vector4(_regionMin.x, _regionMin.y, 1f / simRegionSize, 1f));
        Shader.SetGlobalVector(_SimShadeID,
            new Vector4(1f / simResolution, simNormalStrength / texel, 0f, 0f));

        UpdateDebugPreview();
    }

    void UpdateDebugPreview()
    {
        if (!debugShowSimTexture)
            return;

        if (_debugRT == null)
        {
            _debugRT = new RenderTexture(256, 256, 0, RenderTextureFormat.ARGB32)
            {
                name = "SolRippleSimDebug",
                hideFlags = HideFlags.HideAndDontSave,
            };
            _debugRT.Create();
        }

        _simMat.SetFloat(_DebugGainID, 10f);
        CommandBuffer cmd = CommandBufferPool.Get("SolRippleSimDebug");
        cmd.SetRenderTarget(_debugRT);
        Blitter.BlitTexture(cmd, _rtHandles[_cur], FullScaleBias, _simMat, 1);
        Graphics.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    void OnGUI()
    {
        if (debugShowSimTexture && _debugRT != null && _simActive)
        {
            GUI.DrawTexture(new Rect(10, 10, 256, 256), _debugRT, ScaleMode.ScaleToFit, false);
            GUI.Label(new Rect(10, 268, 300, 22), "Ripple sim (gray = calm)");
        }
    }

    void DisableSim()
    {
        _simActive = false;
        _pending.Clear();
        Shader.SetGlobalVector(_SimRegionID, Vector4.zero);
    }

    void ReleaseSimResources()
    {
        ReleaseRenderTextures();
        if (_simMat != null)
        {
            if (Application.isPlaying) Destroy(_simMat);
            else DestroyImmediate(_simMat);
            _simMat = null;
        }
    }

    void ReleaseRenderTextures()
    {
        for (int i = 0; i < 2; i++)
        {
            _rtHandles[i]?.Release();
            _rtHandles[i] = null;

            if (_rt[i] != null)
            {
                _rt[i].Release();
                if (Application.isPlaying) Destroy(_rt[i]);
                else DestroyImmediate(_rt[i]);
                _rt[i] = null;
            }
        }

        if (_debugRT != null)
        {
            _debugRT.Release();
            if (Application.isPlaying) Destroy(_debugRT);
            else DestroyImmediate(_debugRT);
            _debugRT = null;
        }

        _appliedResolution = -1;
    }

    static void ClearRT(RenderTexture rt)
    {
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, Color.clear);
        RenderTexture.active = prev;
    }

    Vector3 GetFocusPosition()
    {
        if (simFocus != null) return simFocus.position;
        Camera cam = Camera.main;
        return cam != null ? cam.transform.position : transform.position;
    }

    static Vector2 SnapToTexel(Vector2 v, float texelWorld)
    {
        return new Vector2(
            Mathf.Floor(v.x / texelWorld) * texelWorld,
            Mathf.Floor(v.y / texelWorld) * texelWorld);
    }

    // --- Analytic fallback ---------------------------------------------------

    void UpdateAnalytic()
    {
        if (_activeCount > 0)
            PurgeExpired();

        PushToGPU();

        Shader.SetGlobalFloat(_RippleTimeID, _worldTime);
    }

    void PurgeExpired()
    {
        float now = _worldTime;
        for (int i = 0; i < MaxRipples; i++)
        {
            if (_ripples[i].w == 0f) continue;
            if (now - _ripples[i].z > rippleLifetime)
            {
                _ripples[i] = Vector4.zero;
                _activeCount = Mathf.Max(0, _activeCount - 1);
                _gpuDirty = true;
            }
        }
    }

    void PushToGPU()
    {
        bool settingsDirty = !Mathf.Approximately(_lastRippleSpeed, rippleSpeed)
            || !Mathf.Approximately(_lastRippleFrequency, rippleFrequency)
            || !Mathf.Approximately(_lastRippleLifetime, rippleLifetime);

        if (_gpuDirty)
        {
            int count = 0;
            float now = _worldTime;

            for (int i = 0; i < MaxRipples; i++)
            {
                Vector4 ripple = _ripples[i];
                if (ripple.w <= 0f)
                    continue;

                if (now - ripple.z > rippleLifetime)
                {
                    _ripples[i] = Vector4.zero;
                    continue;
                }

                _gpuRipples[count++] = ripple;
            }

            for (int i = count; i < MaxRipples; i++)
                _gpuRipples[i] = Vector4.zero;

            _activeCount = count;
            Shader.SetGlobalVectorArray(_RipplesID, _gpuRipples);
            Shader.SetGlobalInt(_RippleCountID, _activeCount);
            _gpuDirty = false;
        }

        if (settingsDirty)
        {
            Shader.SetGlobalFloat(_RippleSpeedID, rippleSpeed);
            Shader.SetGlobalFloat(_RippleFrequencyID, rippleFrequency);
            Shader.SetGlobalFloat(_RippleLifetimeID, rippleLifetime);
            _lastRippleSpeed = rippleSpeed;
            _lastRippleFrequency = rippleFrequency;
            _lastRippleLifetime = rippleLifetime;
        }
    }
}
