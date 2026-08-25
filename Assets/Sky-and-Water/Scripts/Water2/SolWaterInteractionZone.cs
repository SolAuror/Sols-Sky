using System;
using System.Collections.Generic;
using Sol.Environment;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Sol.Water
{
    public enum SolWaterInteractionZoneMode : byte
    {
        Static,
        FollowTarget,
        Prebaked,
    }

    /// <summary>
    /// Bounded visual shallow-water simulation. It deliberately does not own a body's volume;
    /// hydrology remains deterministic on the CPU while this zone supplies local ripples and foam.
    /// </summary>
    // Runs outside play mode so the Scene View shows the interaction field the scene
    // actually has. While this was play-only, SolOceanClipmap saw IsReady false and forced
    // InteractionStrength to zero in the editor, so a zone could not be positioned or
    // sized against what it does. It only dispatches when the environment clock advances,
    // so a static preview allocates its targets and then sits idle.
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class SolWaterInteractionZone : MonoBehaviour, ISolOriginShiftParticipant
    {
        const int MaximumImpulses = 32;
        static readonly List<SolWaterInteractionZone> ActiveZonesInternal = new(16);
        static readonly int CurrentId = Shader.PropertyToID("_Current");
        static readonly int ResultId = Shader.PropertyToID("_Result");
        static readonly int ObstacleId = Shader.PropertyToID("_ObstacleMask");
        static readonly int ResolutionId = Shader.PropertyToID("_Resolution");
        static readonly int DeltaTimeId = Shader.PropertyToID("_DeltaTime");
        static readonly int SimulationId = Shader.PropertyToID("_SimulationParams");
        static readonly int ImpulseCountId = Shader.PropertyToID("_ImpulseCount");
        static readonly int ImpulsesId = Shader.PropertyToID("_Impulses");
        static readonly int ShiftUvId = Shader.PropertyToID("_ShiftUV");

        [SerializeField] SolWaterBody body;
        [SerializeField] SolWaterInteractionZoneMode mode;
        [SerializeField] Transform followTarget;
        [SerializeField] Vector2 worldSize = new(64f, 64f);
        [SerializeField, Range(64, 512)] int resolution = 256;
        [SerializeField, Range(0.001f, 0.05f)] float fixedStep = 1f / 60f;
        [SerializeField, Range(0f, 1f)] float damping = 0.985f;
        [SerializeField, Min(0.01f)] float propagationSpeed = 4f;
        [SerializeField, Range(0f, 4f)] float heightStrength = 1f;
        [SerializeField, Range(0f, 4f)] float foamGain = 1f;
        [SerializeField] Texture2D obstacleMask;
        [SerializeField] Texture prebakedState;
        [SerializeField] ComputeShader simulationShader;

        readonly Vector4[] _impulses = new Vector4[MaximumImpulses];
        RenderTexture _current;
        RenderTexture _next;
        int _stepKernel = -1;
        int _clearKernel = -1;
        int _shiftKernel = -1;
        int _impulseCount;
        float _accumulator;
        Vector2 _centerXZ;

        public static IReadOnlyList<SolWaterInteractionZone> ActiveZones => ActiveZonesInternal;
        public SolWaterBody Body => body;
        public Texture StateTexture => mode == SolWaterInteractionZoneMode.Prebaked ? prebakedState : _current;
        public Vector2 CenterXZ => _centerXZ;
        public Vector2 WorldSize => worldSize;
        public float HeightStrength => heightStrength;
        public bool IsReady => StateTexture != null;

        void Reset()
        {
            body = GetComponentInParent<SolWaterBody>();
            _centerXZ = new Vector2(transform.position.x, transform.position.z);
        }

        void OnValidate()
        {
            worldSize = Vector2.Max(worldSize, Vector2.one);
            resolution = Mathf.ClosestPowerOfTwo(Mathf.Clamp(resolution, 64, 512));
            fixedStep = Mathf.Clamp(fixedStep, 0.001f, 0.05f);
            if (body == null)
                body = GetComponentInParent<SolWaterBody>();
        }

        void OnEnable()
        {
            if (!ActiveZonesInternal.Contains(this))
                ActiveZonesInternal.Add(this);
            _centerXZ = new Vector2(transform.position.x, transform.position.z);
            SolWorldOriginService.Active?.Register(this);
            if (mode != SolWaterInteractionZoneMode.Prebaked)
                EnsureResources();
        }

        void OnDisable()
        {
            ActiveZonesInternal.Remove(this);
            SolWorldOriginService.Active?.Unregister(this);
            ReleaseResources();
        }

        void Update()
        {
            if (mode == SolWaterInteractionZoneMode.Prebaked)
                return;
            EnsureResources();
            if (_current == null || simulationShader == null)
                return;

            if (mode == SolWaterInteractionZoneMode.FollowTarget)
                UpdateFollowCenter();

            // Raw Time.deltaTime here ignored the Sol pause and both time scales, so
            // ripples kept propagating in a paused world and crawled relative to the
            // swell whenever time was scaled up.
            SolEnvironmentWorld environment = SolEnvironmentWorld.Active;
            float worldDelta = environment != null
                ? (float)environment.WorldDeltaSeconds
                : Time.deltaTime;
            _accumulator += Mathf.Min(Mathf.Max(0f, worldDelta), 0.1f);
            int iterations = 0;
            while (_accumulator >= fixedStep && iterations++ < 4)
            {
                DispatchStep(fixedStep);
                _accumulator -= fixedStep;
            }
            if (iterations >= 4)
                _accumulator = 0f;
        }

        public bool Contains(Vector3 worldPosition)
        {
            Vector2 delta = new(worldPosition.x - _centerXZ.x, worldPosition.z - _centerXZ.y);
            return Mathf.Abs(delta.x) <= worldSize.x * 0.5f
                && Mathf.Abs(delta.y) <= worldSize.y * 0.5f;
        }

        public void AddImpulse(Vector3 worldPosition, float radius, float strength)
        {
            if (_impulseCount >= MaximumImpulses || radius <= 0f || !Contains(worldPosition))
                return;
            Vector2 uv = new(
                (worldPosition.x - _centerXZ.x) / worldSize.x + 0.5f,
                (worldPosition.z - _centerXZ.y) / worldSize.y + 0.5f);
            _impulses[_impulseCount++] = new Vector4(
                uv.x, uv.y, radius / Mathf.Max(worldSize.x, worldSize.y), strength);
        }

        public Vector4 GetShaderMapping()
            => new(_centerXZ.x, _centerXZ.y, worldSize.x, worldSize.y);

        public void OnSolOriginShift(Vector3 localShift, SolDouble3 logicalOrigin)
        {
            _centerXZ -= new Vector2(localShift.x, localShift.z);
        }

        void EnsureResources()
        {
            if (simulationShader == null)
                simulationShader = SolAssetResolver.ResolveCompute(
                    simulationShader, "SolWaterInteraction");
            if (simulationShader == null || (_current != null && _current.width == resolution))
                return;

            ReleaseResources();
            _stepKernel = simulationShader.FindKernel("Step");
            _clearKernel = simulationShader.FindKernel("Clear");
            _shiftKernel = simulationShader.FindKernel("Shift");
            _current = CreateState("Sol Water Interaction Current");
            _next = CreateState("Sol Water Interaction Next");
            DispatchClear(_current);
            DispatchClear(_next);
        }

        RenderTexture CreateState(string resourceName)
        {
            RenderTextureDescriptor descriptor = new(resolution, resolution)
            {
                graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat,
                depthBufferBits = 0,
                msaaSamples = 1,
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
            };
            RenderTexture texture = new(descriptor)
            {
                name = resourceName,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            texture.Create();
            return texture;
        }

        void DispatchClear(RenderTexture target)
        {
            simulationShader.SetTexture(_clearKernel, ResultId, target);
            simulationShader.SetInt(ResolutionId, resolution);
            simulationShader.Dispatch(_clearKernel, (resolution + 7) / 8, (resolution + 7) / 8, 1);
        }

        void DispatchStep(float deltaTime)
        {
            simulationShader.SetTexture(_stepKernel, CurrentId, _current);
            simulationShader.SetTexture(_stepKernel, ResultId, _next);
            simulationShader.SetTexture(_stepKernel, ObstacleId,
                obstacleMask != null ? obstacleMask : Texture2D.blackTexture);
            simulationShader.SetInt(ResolutionId, resolution);
            simulationShader.SetFloat(DeltaTimeId, deltaTime);
            float texelWorld = Mathf.Max(worldSize.x, worldSize.y) / resolution;
            simulationShader.SetVector(SimulationId, new Vector4(
                propagationSpeed, damping, foamGain, texelWorld));
            simulationShader.SetInt(ImpulseCountId, _impulseCount);
            simulationShader.SetVectorArray(ImpulsesId, _impulses);
            simulationShader.Dispatch(_stepKernel, (resolution + 7) / 8, (resolution + 7) / 8, 1);
            Swap();
            _impulseCount = 0;
        }

        void UpdateFollowCenter()
        {
            Transform target = followTarget;
            if (target == null && Camera.main != null)
                target = Camera.main.transform;
            if (target == null)
                return;

            float texelX = worldSize.x / resolution;
            float texelY = worldSize.y / resolution;
            Vector2 desired = new(
                Mathf.Round(target.position.x / texelX) * texelX,
                Mathf.Round(target.position.z / texelY) * texelY);
            Vector2 delta = desired - _centerXZ;
            if (delta.sqrMagnitude < Mathf.Min(texelX * texelX, texelY * texelY))
                return;

            simulationShader.SetTexture(_shiftKernel, CurrentId, _current);
            simulationShader.SetTexture(_shiftKernel, ResultId, _next);
            simulationShader.SetInt(ResolutionId, resolution);
            simulationShader.SetVector(ShiftUvId, new Vector4(
                delta.x / worldSize.x, delta.y / worldSize.y, 0f, 0f));
            simulationShader.Dispatch(_shiftKernel, (resolution + 7) / 8, (resolution + 7) / 8, 1);
            Swap();
            _centerXZ = desired;
        }

        void Swap() => (_current, _next) = (_next, _current);

        void ReleaseResources()
        {
            Release(ref _current);
            Release(ref _next);
        }

        static void Release(ref RenderTexture texture)
        {
            if (texture == null)
                return;
            texture.Release();
            Destroy(texture);
            texture = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => ActiveZonesInternal.Clear();
    }
}
