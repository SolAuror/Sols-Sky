using System;
using System.Collections.Generic;
using Sol.Environment;
using UnityEngine;
using Sol.Water.Rendering;

namespace Sol.Water
{
    /// <summary>Scene-owned water body registry, wave clock, and query authority.</summary>
    // Runs outside play mode so the world matches SolWaterBody and the spline geometry
    // components, which are all [ExecuteAlways]. Without this, Active stayed null in the
    // editor, bodies accumulated in PendingBodies and were never adopted, and nothing
    // registered until play began. ResolveDependencies adds the query service and planar
    // renderer to this object in the editor as a result; that is intended.
    [ExecuteAlways]
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
        [SerializeField] SolWaterWetness wetness;

        readonly List<SolWaterBody> _bodies = new(16);
        readonly Dictionary<SolWaterBodyId, SolWaterBody> _byId = new();

        public event Action<SolWaterBody> BodyRegistered;
        public event Action<SolWaterBody> BodyUnregistered;
        public event Action<SolWaterCommand> CommandApplied;

        public SolWaterProfile DefaultProfile => defaultProfile;
        public SolWaterQualityProfile QualityProfile => qualityProfile;
        public IReadOnlyList<SolWaterBody> Bodies => _bodies;
        public ISolWaterQueryService QueryService => queryService;
        /// <summary>
        /// Wave-domain clock, integrated by <see cref="SolEnvironmentWorld"/> against the
        /// weather's wave speed multiplier. Reading it as absoluteSeconds * multiplier
        /// jumped the whole elapsed session's phase on every weather change.
        /// </summary>
        public double WaveTime => environmentWorld != null
            ? environmentWorld.State.WaveSeconds
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
            body.TrySampleGeometry(localPosition, out SolWaterGeometrySample geometry);

            // Choppy water displaces points horizontally, so the surface sample taken at
            // the query XZ is not the point that ends up above the query. Solve for the
            // source position first; without this, buoyancy reads the wrong height and
            // the error peaks at crests and troughs.
            Vector2 targetXZ = new(geometry.Position.x, geometry.Position.z);
            Vector2 sourceXZ = targetXZ;
            float inversionResidual = 0f;
            if (body.IsInfinite || body.Geometry == null)
            {
                SolWaterDisplacementInversion.HorizontalDisplacement displacement =
                    samplePosition =>
                    {
                        Vector3 probe = new(samplePosition.x, geometry.Position.y, samplePosition.y);
                        Vector3 offset = body.EvaluateWaves(probe, WaveTime, environment).Displacement;
                        return new Vector2(offset.x, offset.z);
                    };
                sourceXZ = SolWaterDisplacementInversion.Solve(targetXZ, displacement);
                inversionResidual = SolWaterDisplacementInversion.ResidualError(
                    targetXZ, sourceXZ, displacement);
            }
            Vector3 wavePosition = new(sourceXZ.x, geometry.Position.y, sourceXZ.y);

            SolWaterWaveSample wave = body.EvaluateWaves(wavePosition, WaveTime, environment);
            bool finiteGeometry = !body.IsInfinite && body.Geometry != null;
            // The solved source position plus its displacement lands back on the query
            // XZ by construction, so only the vertical component is taken forward.
            Vector3 surfacePosition = finiteGeometry
                ? geometry.Position + geometry.Normal * wave.Displacement.y
                : new Vector3(geometry.Position.x,
                    geometry.Position.y + wave.Displacement.y,
                    geometry.Position.z);
            Vector3 surfaceNormal = finiteGeometry
                ? Vector3.Slerp(geometry.Normal, wave.Normal, 0.35f).normalized
                : wave.Normal;
            Vector3 flow = body.AuthoredFlow + geometry.Flow;
            sample = new SolWaterSurfaceSample(
                true,
                body.Id,
                surfacePosition,
                surfaceNormal,
                wave.Velocity + flow,
                flow,
                Mathf.Max(0f, surfacePosition.y - localPosition.y),
                Mathf.Max(wave.Foam, geometry.Foam),
                // Gerstner is evaluated for the current wave time and is exact. The
                // spectral mirror is a GPU readback and is genuinely a few frames old,
                // so report that rather than claiming a fresh sample.
                body.IsInfinite && SolWaterFftReadback.HasData
                    ? SolWaterFftReadback.SampleAgeSeconds : 0f,
                // Confidence now reports how well the inversion actually converged
                // instead of a fixed tier constant: a steep, near-breaking wave is
                // genuinely a less reliable sample than calm water.
                Mathf.Min(
                    qualityProfile != null && qualityProfile.tier != SolWaterQualityTier.Low
                        ? 0.9f : 1f,
                    SolWaterDisplacementInversion.ConfidenceFromResidual(inversionResidual)));
            // Keep the canonical frame construction in the query authority so gameplay
            // and rendering validation consume the same position/normal/foam contract.
            _lastFrame = new SolWaterSurfaceFrame(surfacePosition, surfaceNormal,
                wave.Velocity + flow, sample.Foam, sample.Depth, sample.Confidence);
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
            // Added automatically so converted scenes regain terrain wetness without a
            // second manual step. The legacy authority that used to publish these globals
            // is disabled by the converter.
            if (wetness == null)
                wetness = GetComponent<SolWaterWetness>();
            if (wetness == null)
                wetness = gameObject.AddComponent<SolWaterWetness>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active = null;
            PendingBodies.Clear();
        }
    }
}
