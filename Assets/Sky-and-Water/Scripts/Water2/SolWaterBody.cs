using Sol.Environment;
using UnityEngine;

namespace Sol.Water
{
    public enum SolWaterBodyType : byte
    {
        Ocean,
        Lake,
        River,
        Pool,
        Waterfall,
    }

    /// <summary>Authored water-body identity and local overrides.</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class SolWaterBody : MonoBehaviour, ISolOriginShiftParticipant
    {
        [SerializeField] SolWaterBodyId bodyId;
        [SerializeField] SolWaterBodyType bodyType = SolWaterBodyType.Ocean;
        [SerializeField] SolWaterProfile profile;
        [SerializeField] Collider horizontalBounds;
        [SerializeField] Renderer authoredSurface;
        [SerializeField] SolWaterInteractionZone interactionZone;
        [SerializeField] int priority;
        [SerializeField] float levelOffset;
        [SerializeField] Vector3 authoredFlow;
        [SerializeField] bool useTransformYAsLevel = true;
        [SerializeField] bool enablePlanarReflection;

        bool _hasRuntimeLevel;
        float _runtimeSurfaceLevel;
        bool _authoredForceRenderingOff;
        bool _ownsAuthoredRendererState;
        ISolWaterGeometry _geometry;

        public SolWaterBodyId Id => bodyId;
        public SolWaterBodyType BodyType => bodyType;
        public SolWaterProfile Profile => profile != null ? profile : SolWaterWorld.Active?.DefaultProfile;
        public Renderer AuthoredSurface => authoredSurface;
        public SolWaterInteractionZone InteractionZone => interactionZone;
        public int Priority => priority;
        public bool IsInfinite => bodyType == SolWaterBodyType.Ocean;
        public bool EnablePlanarReflection => enablePlanarReflection;
        public Vector3 AuthoredFlow => authoredFlow;
        public ISolWaterGeometry Geometry => ResolveGeometry();
        public float SurfaceLevel => _hasRuntimeLevel
            ? _runtimeSurfaceLevel
            : (useTransformYAsLevel ? transform.position.y : 0f) + levelOffset;
        public int PrepassHash => bodyId.StableHash24;

        void Reset()
        {
            bodyId = SolWaterBodyId.NewId();
            horizontalBounds = GetComponent<Collider>();
            authoredSurface = GetComponent<Renderer>();
            interactionZone = GetComponentInChildren<SolWaterInteractionZone>();
            ResolveGeometry(true);
        }

        void OnValidate()
        {
            if (!bodyId.IsValid)
                bodyId = SolWaterBodyId.NewId();
            if (horizontalBounds == null)
                horizontalBounds = GetComponent<Collider>();
            if (authoredSurface == null)
                authoredSurface = GetComponent<Renderer>();
            if (interactionZone == null)
                interactionZone = GetComponentInChildren<SolWaterInteractionZone>();
            ISolWaterGeometry geometry = ResolveGeometry(true);
            if (authoredSurface == null && geometry != null)
                authoredSurface = geometry.SurfaceRenderer;
        }

        void OnEnable()
        {
            // Reset only runs when the component is added through the Inspector, and
            // OnValidate is not guaranteed to have run before the first OnEnable. A body
            // created from script therefore reached registration with an empty id, which
            // Register rejects outright, so procedurally created and streamed-in water
            // silently never appeared.
            if (!bodyId.IsValid)
                bodyId = SolWaterBodyId.NewId();
            AcquireAuthoredRenderer();
            SolWaterWorld.RegisterPending(this);
            SolWorldOriginService.Active?.Register(this);
        }

        void Start()
        {
            SolWaterWorld.RegisterPending(this);
            SolWorldOriginService.Active?.Register(this);
        }

        void OnDisable()
        {
            ReleaseAuthoredRenderer();
            SolWaterWorld.UnregisterPending(this);
            SolWorldOriginService.Active?.Unregister(this);
        }

        public bool ContainsHorizontal(Vector3 localPosition)
        {
            if (IsInfinite)
                return true;
            ISolWaterGeometry geometry = ResolveGeometry();
            if (geometry != null)
                return geometry.ContainsPoint(localPosition);
            if (horizontalBounds != null)
            {
                Bounds bounds = horizontalBounds.bounds;
                return localPosition.x >= bounds.min.x && localPosition.x <= bounds.max.x
                    && localPosition.z >= bounds.min.z && localPosition.z <= bounds.max.z;
            }
            if (authoredSurface != null)
            {
                Bounds bounds = authoredSurface.bounds;
                return localPosition.x >= bounds.min.x && localPosition.x <= bounds.max.x
                    && localPosition.z >= bounds.min.z && localPosition.z <= bounds.max.z;
            }
            return false;
        }

        public Bounds GetWorldBounds(float oceanExtent = 100000f)
        {
            if (IsInfinite)
                return new Bounds(new Vector3(0f, SurfaceLevel, 0f), new Vector3(oceanExtent * 2f, 1000f, oceanExtent * 2f));
            ISolWaterGeometry geometry = ResolveGeometry();
            if (geometry != null)
                return geometry.WorldBounds;
            if (horizontalBounds != null)
                return horizontalBounds.bounds;
            if (authoredSurface != null)
                return authoredSurface.bounds;
            return new Bounds(transform.position, Vector3.zero);
        }

        public SolWaterWaveSample EvaluateWaves(Vector3 localPosition, double time, in SolEnvironmentState environment)
        {
            ISolWaterGeometry geometry = ResolveGeometry();
            if (geometry != null && !geometry.SupportsSurfaceWaves)
                return new SolWaterWaveSample(Vector3.zero, Vector3.up, Vector3.zero, 0f);
            SolDouble3 origin = SolWorldOriginService.Active?.LogicalOrigin ?? default;
            Vector3 waveDirection = environment.Wind.Direction;
            if (geometry != null && geometry.TrySample(localPosition, out SolWaterGeometrySample geometrySample)
                && geometrySample.Flow.sqrMagnitude > 0.0001f)
                waveDirection = geometrySample.Flow.normalized;
            return geometry != null
                ? SolWaterWaveEvaluator.EvaluateFinite(
                    Profile, new Vector2(localPosition.x, localPosition.z), time,
                    origin, waveDirection, environment.Wind.Speed,
                    environment.Weather.WaterTurbulence)
                : SolWaterWaveEvaluator.Evaluate(
                    Profile, new Vector2(localPosition.x, localPosition.z), time,
                    origin, waveDirection, environment.Wind.Speed,
                    environment.Weather.WaterTurbulence);
        }

        public void OnSolOriginShift(Vector3 localShift, SolDouble3 logicalOrigin)
        {
            transform.position -= localShift;
            if (_hasRuntimeLevel)
                _runtimeSurfaceLevel -= localShift.y;
        }

        public void SetRuntimeSurfaceLevel(float localLevel)
        {
            _runtimeSurfaceLevel = localLevel;
            _hasRuntimeLevel = true;
        }

        public void ClearRuntimeSurfaceLevel() => _hasRuntimeLevel = false;

        public bool TrySampleGeometry(Vector3 localPosition, out SolWaterGeometrySample sample)
        {
            ISolWaterGeometry geometry = ResolveGeometry();
            if (geometry != null && geometry.TrySample(localPosition, out sample))
                return true;
            sample = new SolWaterGeometrySample(
                new Vector3(localPosition.x, SurfaceLevel, localPosition.z),
                Vector3.up, authoredFlow, 0f, 0f);
            return IsInfinite || ContainsHorizontal(localPosition);
        }

        internal void ConfigureGeneratedGeometry(SolWaterBodyType generatedType, Renderer surface)
        {
            bool reacquire = isActiveAndEnabled
                && (IsInfinite || authoredSurface != surface || !_ownsAuthoredRendererState);
            if (reacquire)
                ReleaseAuthoredRenderer();
            bodyType = generatedType;
            if (surface != null)
                authoredSurface = surface;
            ResolveGeometry(true);
            if (reacquire)
                AcquireAuthoredRenderer();
        }

        ISolWaterGeometry ResolveGeometry(bool refresh = false)
        {
            if (!refresh && _geometry != null)
                return _geometry;
            _geometry = null;
            MonoBehaviour[] behaviours = GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] is ISolWaterGeometry geometry)
                {
                    _geometry = geometry;
                    break;
                }
            }
            return _geometry;
        }

        void AcquireAuthoredRenderer()
        {
            if (IsInfinite || authoredSurface == null || _ownsAuthoredRendererState)
                return;
            _authoredForceRenderingOff = authoredSurface.forceRenderingOff;
            authoredSurface.forceRenderingOff = true;
            _ownsAuthoredRendererState = true;
        }

        void ReleaseAuthoredRenderer()
        {
            if (!_ownsAuthoredRendererState || authoredSurface == null)
                return;
            authoredSurface.forceRenderingOff = _authoredForceRenderingOff;
            _ownsAuthoredRendererState = false;
        }
    }
}
