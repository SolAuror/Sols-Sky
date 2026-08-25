using System;
using UnityEngine;

/// <summary>
/// ---------------------------------------------------------------------------
/// UNDERWATER VOLUME CONTROLLER
/// ---------------------------------------------------------------------------
///
/// Attach to ANY persistent GameObject (player root, InputManager, etc.).
///
/// Each frame it finds the active camera (supports Cinemachine Brain) and
/// checks whether it is below the animated wave surface of the nearest
/// WaterVolume. Two global shader properties are updated:
///
///   _UnderwaterFactor   0 = fully above water      1 = fully submerged
///   _UnderwaterDepth    metres the camera is below the surface (clamped = 0)
///
/// For third-person cameras that orbit above the water while the player is
/// submerged, an optional playerTransform reference is provided. When assigned,
/// the controller checks BOTH the camera position AND the player position -
/// whichever is deeper underwater drives the effect. This prevents the
/// underwater overlay from disappearing just because the orbit camera is
/// above the surface.
///
/// SETUP:
///   1. Add to any GameObject that persists while playing.
/// 2. Optionally assign trackedCamera - if left null, Camera.main is used
///      each frame (which is the Cinemachine Brain output camera).
///   3. Optionally assign playerTransform for third-person support.
///   4. Add UnderwaterRendererFeature to your URP Renderer Asset.
/// ---------------------------------------------------------------------------
/// </summary>
public class UnderwaterVolumeController : MonoBehaviour
{
    [Header("Camera")]
    [Tooltip("Camera to track. If null, Camera.main is used each frame " +
             "(works with Cinemachine Brain).")]
    public Camera trackedCamera;

    [Header("Third-Person Support")]
    [Tooltip("Player root transform. When assigned the controller also checks " +
             "if the player (not just the camera) is underwater, so the overlay " +
             "works even if the orbit camera is above the surface.")]
    public Transform playerTransform;

    [Header("Transition")]
    [Tooltip("How fast _UnderwaterFactor blends when entering water.")]
    [Range(0f, 40f)]
    public float blendSpeed = 10f;

    [Tooltip("How fast _UnderwaterFactor blends out when leaving water. Keep higher than enter speed.")]
    [Range(0f, 80f)]
    public float exitBlendSpeed = 24f;

    [Tooltip("Depth below the surface (metres) at which _UnderwaterFactor reaches 1.")]
    public float fullSubmersionDepth = 0.4f;

    [Tooltip("Extra depth offset added when computing how far the camera is below the surface. " +
             "A small positive value (e.g. 0.15) closes the visual gap between the water mesh " +
             "and where underwater FX begin to appear.")]
    public float surfaceEnterBias = 0.05f;

    [Tooltip("How far above the local surface the camera may be while still allowing " +
             "third-person player fallback to drive underwater FX.")]
    [Range(0f, 1f)]
    public float playerFallbackSurfaceGrace = 0.05f;

    [Header("Debug")]
    [Tooltip("Enable to print pipeline diagnostics every second to the Console.")]
    public bool debugLog = false;

    // -----------------------------------------------------------------------
    float _underwaterFactor;
    float _debugTimer;
    bool _isUnderwater;
    float _timeOfDayRetryTimer;
    SolEnvironmentCoordinator _environmentCoordinator;
    Sol.ToD.TimeOfDay _timeOfDay;

    public event Action<bool> UnderwaterStateChanged;
    public bool IsUnderwater => _isUnderwater;

    static readonly int _SID_UnderwaterFactor = Shader.PropertyToID("_UnderwaterFactor");
    static readonly int _SID_UnderwaterDepth  = Shader.PropertyToID("_UnderwaterDepth");

    // -----------------------------------------------------------------------

    void OnEnable()
    {
        _environmentCoordinator = SolEnvironmentCoordinator.Resolve(this, createIfMissing: true);
        _environmentCoordinator?.Register(this);
    }

    void OnDisable()
    {
        Shader.SetGlobalFloat(_SID_UnderwaterFactor, 0f);
        Shader.SetGlobalFloat(_SID_UnderwaterDepth,  0f);
        _underwaterFactor = 0f;
        if (_isUnderwater)
            UnderwaterStateChanged?.Invoke(false);
        _isUnderwater = false;
        _environmentCoordinator?.Unregister(this);
        _environmentCoordinator = null;
    }

    void LateUpdate()
    {
        // Water 2 owns the submersion contract wherever it is live: SolWaterRendererFeature
        // publishes _UnderwaterFactor / _UnderwaterDepth from its own bodies during
        // AddRenderPasses. This controller resolves submersion from Water 1 WaterVolume
        // components, so in a Water 2 scene it found nothing and pinned the globals to
        // zero every frame, which is what kept the atmosphere's underwater gate from ever
        // firing. Yield rather than fight; a Water 1 only scene is unaffected.
        if (Sol.Water.SolWaterWorld.Active != null)
            return;

        _timeOfDayRetryTimer -= Time.unscaledDeltaTime;
        if (_timeOfDay == null && _timeOfDayRetryTimer <= 0f)
        {
            _timeOfDay = Sol.ToD.TimeOfDay.ResolveInstance();
            if (_timeOfDay == null)
                _timeOfDayRetryTimer = 0.5f;
        }
        float deltaSeconds = _timeOfDay != null ? _timeOfDay.WorldDeltaSeconds : Time.deltaTime;

        // Resolve the active camera every frame so Cinemachine camera swaps
        // and Brain output changes are handled automatically.
        Camera cam = trackedCamera != null ? trackedCamera : Camera.main;

        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;

        float targetFactor    = 0f;
        float underwaterDepth = 0f;

        string debugSource = "none";

        // --- Check camera position ---
        if (cam != null)
        {
            var vol = WaterVolume.FindVolume(camPos);
            if (vol != null)
            {
                float surface   = vol.GetSurfaceHeight(camPos);
                float trueDepth = surface - camPos.y;
                if (trueDepth > 0f)
                {
                    // Bias only after true submersion so above-water cameras never trigger.
                    underwaterDepth = trueDepth + surfaceEnterBias;
                    targetFactor = Mathf.Clamp01(underwaterDepth / Mathf.Max(fullSubmersionDepth, 0.01f));
                    debugSource = $"camera (depth={underwaterDepth:F2})";
                }
            }
        }

        // --- Third-person fallback: check player position ---
        // Only applies when the camera is within the water volume's XZ footprint AND
        // within fullSubmersionDepth above the surface. This prevents the orbit camera
        // being several metres above the water from triggering underwater FX just because
        // the player's feet are submerged.
        bool cameraIsNearSurface = false;
        if (cam != null)
        {
            var camXZVol = WaterVolume.FindVolumeXZ(camPos);
            if (camXZVol != null)
            {
                float camSurf = camXZVol.GetSurfaceHeight(camPos);
                float cameraAboveSurface = camPos.y - camSurf;
                cameraIsNearSurface = cameraAboveSurface <= playerFallbackSurfaceGrace;
            }
        }

        if (playerTransform != null && targetFactor < 1f && cameraIsNearSurface)
        {
            Vector3 playerPos = playerTransform.position;
            var playerVol = WaterVolume.FindVolume(playerPos);
            if (playerVol != null)
            {
                float playerSurface = playerVol.GetSurfaceHeight(playerPos);
                float playerDepth   = playerSurface - playerPos.y;

                if (playerDepth > underwaterDepth)
                {
                    underwaterDepth = playerDepth;
                    float playerFactor = Mathf.Clamp01(playerDepth / Mathf.Max(fullSubmersionDepth, 0.01f));
                    if (playerFactor > targetFactor)
                    {
                        targetFactor = playerFactor;
                        debugSource = $"player (depth={playerDepth:F2})";
                    }
                }
            }
        }

        float blend = targetFactor > _underwaterFactor ? blendSpeed : exitBlendSpeed;
        _underwaterFactor = Mathf.MoveTowards(_underwaterFactor, targetFactor, blend * deltaSeconds);
        if (_underwaterFactor < 0.001f) _underwaterFactor = 0f;

        Shader.SetGlobalFloat(_SID_UnderwaterFactor, _underwaterFactor);
        Shader.SetGlobalFloat(_SID_UnderwaterDepth,  Mathf.Max(underwaterDepth, 0f));

        bool shouldBeUnderwater = _underwaterFactor > 0.01f;
        if (shouldBeUnderwater != _isUnderwater)
        {
            _isUnderwater = shouldBeUnderwater;
            UnderwaterStateChanged?.Invoke(_isUnderwater);
        }

        // --- Debug logging (once per second) ---
        if (debugLog)
        {
        _debugTimer += deltaSeconds;
            if (_debugTimer >= 1f)
            {
                _debugTimer = 0f;
                int volumeCount = WaterVolume.VolumeCount;
                bool hasCam = cam != null;
                bool hasPlayer = playerTransform != null;
                string camName = hasCam ? cam.name : "NULL";
                string playerName = hasPlayer ? playerTransform.name : "NULL";
                Debug.Log($"[UnderwaterDebug] " +
                    $"volumes={volumeCount} | " +
                    $"cam={camName} pos={camPos:F1} | " +
                    $"player={playerName} pos={(hasPlayer ? playerTransform.position.ToString("F1") : "N/A")} | " +
                    $"source={debugSource} | " +
                    $"targetFactor={targetFactor:F3} | " +
                    $"_UnderwaterFactor={_underwaterFactor:F3}");
            }
        }
    }
}
