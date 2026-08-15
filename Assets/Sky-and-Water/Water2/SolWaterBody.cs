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

        public SolWaterBodyId Id => bodyId;
        public SolWaterBodyType BodyType => bodyType;
        public SolWaterProfile Profile => profile != null ? profile : SolWaterWorld.Active?.DefaultProfile;
        public Renderer AuthoredSurface => authoredSurface;
        public SolWaterInteractionZone InteractionZone => interactionZone;
        public int Priority => priority;
        public bool IsInfinite => bodyType == SolWaterBodyType.Ocean;
        public bool EnablePlanarReflection => enablePlanarReflection;
        public Vector3 AuthoredFlow => authoredFlow;
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
        }

        void OnEnable()
        {
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
            if (horizontalBounds != null)
                return horizontalBounds.bounds;
            if (authoredSurface != null)
                return authoredSurface.bounds;
            return new Bounds(transform.position, Vector3.zero);
        }

        public SolWaterWaveSample EvaluateWaves(Vector3 localPosition, double time, in SolEnvironmentState environment)
        {
            SolDouble3 origin = SolWorldOriginService.Active?.LogicalOrigin ?? default;
            return SolWaterWaveEvaluator.Evaluate(
                Profile,
                new Vector2(localPosition.x, localPosition.z),
                time,
                origin,
                environment.Wind.Direction,
                environment.Wind.Speed,
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
