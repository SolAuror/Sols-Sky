using System;
using System.Collections.Generic;
using Sol.Environment;
using UnityEngine;
using Sol.Water.Rendering;

namespace Sol.Water
{
    /// <summary>Scene-owned water body registry, wave clock, and query authority.</summary>
    [DefaultExecutionOrder(-800)]
    [DisallowMultipleComponent]
    public sealed class SolWaterWorld : MonoBehaviour
    {
        public const int SnapshotVersion = 1;
        static readonly HashSet<SolWaterBody> PendingBodies = new();

        public static SolWaterWorld Active { get; private set; }

        [SerializeField] SolWaterProfile defaultProfile;
        [SerializeField] SolWaterQualityProfile qualityProfile;
        [SerializeField] SolEnvironmentWorld environmentWorld;
        [SerializeField] SolWaterQueryService queryService;
        [SerializeField] SolPlanarReflectionRenderer planarReflectionRenderer;

        readonly List<SolWaterBody> _bodies = new(16);
        readonly Dictionary<SolWaterBodyId, SolWaterBody> _byId = new();

        public event Action<SolWaterBody> BodyRegistered;
        public event Action<SolWaterBody> BodyUnregistered;
        public event Action<SolWaterCommand> CommandApplied;

        public SolWaterProfile DefaultProfile => defaultProfile;
        public SolWaterQualityProfile QualityProfile => qualityProfile;
        public IReadOnlyList<SolWaterBody> Bodies => _bodies;
        public ISolWaterQueryService QueryService => queryService;
        public double WaveTime => environmentWorld != null
            ? environmentWorld.State.AbsoluteWorldSeconds * environmentWorld.State.Weather.WaveSpeedMultiplier
            : Time.timeAsDouble;

        void Awake()
        {
            if (Active != null && Active != this)
            {
                Debug.LogError("[SolWaterWorld] Only one active water world is allowed.", this);
                enabled = false;
                return;
            }
            Active = this;
            ResolveDependencies();
            AdoptPendingBodies();
        }

        void OnEnable()
        {
            if (Active == null || Active == this)
                Active = this;
            ResolveDependencies();
            AdoptPendingBodies();
        }

        void OnDisable()
        {
            if (Active == this)
                Active = null;
            _bodies.Clear();
            _byId.Clear();
        }

        void LateUpdate()
        {
            AdoptPendingBodies();
            SolEnvironmentCameraRegistry.PruneUnused();
        }

        public bool TryGetBody(SolWaterBodyId id, out SolWaterBody body) => _byId.TryGetValue(id, out body);

        public bool TryGetOcean(out SolWaterBody ocean)
        {
            for (int i = 0; i < _bodies.Count; i++)
            {
                SolWaterBody body = _bodies[i];
                if (body != null && body.isActiveAndEnabled && body.BodyType == SolWaterBodyType.Ocean)
                {
                    ocean = body;
                    return true;
                }
            }
            ocean = null;
            return false;
        }

        public bool TryResolveBody(Vector3 localPosition, out SolWaterBody body)
        {
            body = null;
            int bestPriority = int.MinValue;
            SolWaterBody ocean = null;
            for (int i = 0; i < _bodies.Count; i++)
            {
                SolWaterBody candidate = _bodies[i];
                if (candidate == null || !candidate.isActiveAndEnabled)
                    continue;
                if (candidate.IsInfinite)
                {
                    ocean = candidate;
                    continue;
                }
                if (candidate.Priority >= bestPriority && candidate.ContainsHorizontal(localPosition))
                {
                    body = candidate;
                    bestPriority = candidate.Priority;
                }
            }
            body ??= ocean;
            return body != null;
        }

        public bool TrySampleApproximate(Vector3 localPosition, out SolWaterSurfaceSample sample)
        {
            if (!TryResolveBody(localPosition, out SolWaterBody body))
            {
                sample = SolWaterSurfaceSample.NoWater(localPosition);
                return false;
            }

            SolEnvironmentState environment = environmentWorld != null ? environmentWorld.State : default;
            SolWaterWaveSample wave = body.EvaluateWaves(localPosition, WaveTime, environment);
            Vector3 surfacePosition = new(
                localPosition.x + wave.Displacement.x,
                body.SurfaceLevel + wave.Displacement.y,
                localPosition.z + wave.Displacement.z);
            Vector3 flow = body.AuthoredFlow;
            sample = new SolWaterSurfaceSample(
                true,
                body.Id,
                surfacePosition,
                wave.Normal,
                wave.Velocity + flow,
                flow,
                Mathf.Max(0f, surfacePosition.y - localPosition.y),
                wave.Foam,
                0f,
                qualityProfile != null && qualityProfile.tier != SolWaterQualityTier.Low ? 0.75f : 1f);
            // Keep the canonical frame construction in the query authority so gameplay
            // and rendering validation consume the same position/normal/foam contract.
            _lastFrame = new SolWaterSurfaceFrame(surfacePosition, wave.Normal,
                wave.Velocity + flow, wave.Foam, sample.Depth, sample.Confidence);
            return true;
        }

        SolWaterSurfaceFrame _lastFrame;
        public SolWaterSurfaceFrame LastApproximateFrame => _lastFrame;

        public bool ApplyCommand(in SolWaterCommand command)
        {
            if (!TryGetBody(command.BodyId, out SolWaterBody body))
                return false;
            switch (command.Type)
            {
                case SolWaterCommandType.SetBodyLevel:
                    body.SetRuntimeSurfaceLevel(command.Value);
                    break;
                case SolWaterCommandType.ClearBodyLevelOverride:
                    body.ClearRuntimeSurfaceLevel();
                    break;
                default:
                    return false;
            }
            CommandApplied?.Invoke(command);
            return true;
        }

        public SolWaterSnapshot CaptureSnapshot()
        {
            SolWaterBodyRuntimeState[] states = new SolWaterBodyRuntimeState[_bodies.Count];
            for (int i = 0; i < _bodies.Count; i++)
            {
                SolWaterBody body = _bodies[i];
                states[i] = body != null
                    ? new SolWaterBodyRuntimeState(body.Id, body.SurfaceLevel, (uint)body.PrepassHash)
                    : default;
            }
            return new SolWaterSnapshot(SnapshotVersion, WaveTime, states);
        }

        public bool RestoreSnapshot(in SolWaterSnapshot snapshot)
        {
            if (snapshot.Version != SnapshotVersion || snapshot.Bodies == null)
                return false;
            for (int i = 0; i < snapshot.Bodies.Length; i++)
            {
                SolWaterBodyRuntimeState state = snapshot.Bodies[i];
                if (TryGetBody(state.BodyId, out SolWaterBody body))
                    body.SetRuntimeSurfaceLevel(state.SurfaceLevel);
            }
            return true;
        }

        internal static void RegisterPending(SolWaterBody body)
        {
            if (body == null)
                return;
            PendingBodies.Add(body);
            Active?.Register(body);
        }

        internal static void UnregisterPending(SolWaterBody body)
        {
            if (body == null)
                return;
            PendingBodies.Remove(body);
            Active?.Unregister(body);
        }

        void Register(SolWaterBody body)
        {
            if (body == null || _bodies.Contains(body))
                return;
            if (!body.Id.IsValid)
            {
                Debug.LogError("[SolWaterWorld] Water body has no stable ID.", body);
                return;
            }
            if (_byId.TryGetValue(body.Id, out SolWaterBody duplicate) && duplicate != body)
            {
                Debug.LogError($"[SolWaterWorld] Duplicate water body ID {body.Id}.", body);
                return;
            }
            _bodies.Add(body);
            _byId[body.Id] = body;
            BodyRegistered?.Invoke(body);
        }

        void Unregister(SolWaterBody body)
        {
            if (body == null || !_bodies.Remove(body))
                return;
            if (_byId.TryGetValue(body.Id, out SolWaterBody registered) && registered == body)
                _byId.Remove(body.Id);
            BodyUnregistered?.Invoke(body);
        }

        void AdoptPendingBodies()
        {
            foreach (SolWaterBody body in PendingBodies)
                Register(body);
        }

        void ResolveDependencies()
        {
            if (environmentWorld == null)
                environmentWorld = SolEnvironmentWorld.Active;
            if (queryService == null)
                queryService = GetComponent<SolWaterQueryService>();
            if (queryService == null)
                queryService = gameObject.AddComponent<SolWaterQueryService>();
            if (planarReflectionRenderer == null)
                planarReflectionRenderer = GetComponent<SolPlanarReflectionRenderer>();
            if (planarReflectionRenderer == null)
                planarReflectionRenderer = gameObject.AddComponent<SolPlanarReflectionRenderer>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active = null;
            PendingBodies.Clear();
        }
    }
}
