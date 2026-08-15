using System;
using System.Collections.Generic;
using Sol.Environment;
using Sol.Streaming;
using Sol.Water;
using UnityEngine;

namespace Sol.Hydrology
{
    [Serializable]
    public readonly struct SolHydrologyNodeState
    {
        public readonly string Id;
        public readonly double Volume;
        public readonly double Level;
        public readonly double SnowStorage;

        public SolHydrologyNodeState(string id, double volume, double level, double snowStorage)
        {
            Id = id;
            Volume = volume;
            Level = level;
            SnowStorage = snowStorage;
        }
    }

    [Serializable]
    public readonly struct SolHydrologySnapshot
    {
        public readonly int Version;
        public readonly long Tick;
        public readonly string[] NodeIds;
        public readonly double[] Volumes;
        public readonly double[] SnowStorage;
        public readonly string[] EdgeIds;
        public readonly double[] CumulativeFlux;

        public SolHydrologySnapshot(
            int version,
            long tick,
            string[] nodeIds,
            double[] volumes,
            double[] snowStorage,
            string[] edgeIds,
            double[] cumulativeFlux)
        {
            Version = version;
            Tick = tick;
            NodeIds = nodeIds;
            Volumes = volumes;
            SnowStorage = snowStorage;
            EdgeIds = edgeIds;
            CumulativeFlux = cumulativeFlux;
        }
    }

    public enum SolHydrologyCommandType : byte
    {
        SetVolume,
        AddVolume,
    }

    [Serializable]
    public readonly struct SolHydrologyCommand
    {
        public readonly SolHydrologyCommandType Type;
        public readonly string NodeId;
        public readonly double CubicMetres;
        public readonly ulong Sequence;

        public SolHydrologyCommand(
            SolHydrologyCommandType type,
            string nodeId,
            double cubicMetres,
            ulong sequence = 0)
        {
            Type = type;
            NodeId = nodeId;
            CubicMetres = cubicMetres;
            Sequence = sequence;
        }
    }

    /// <summary>
    /// Deterministic conservative macro solver. GPU interaction zones may decorate its surfaces,
    /// but this component alone owns stored volume and cross-cell boundary flux.
    /// </summary>
    [DefaultExecutionOrder(-700)]
    [DisallowMultipleComponent]
    public sealed class SolHydrologyWorld : MonoBehaviour
    {
        public const int SnapshotVersion = 1;

        [SerializeField] SolHydrologyAsset asset;
        [SerializeField] SolEnvironmentWorld environmentWorld;
        [SerializeField] MonoBehaviour cellProviderComponent;
        [SerializeField, Min(1f)] double fixedWorldStepSeconds = 60d;
        [SerializeField, Min(0f)] float rainfallMillimetresPerHourAtIntensityOne = 12f;
        [SerializeField] bool simulate = true;

        readonly Dictionary<string, int> _nodeIndices = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> _edgeIndices = new(StringComparer.Ordinal);
        double[] _volumes = Array.Empty<double>();
        double[] _levels = Array.Empty<double>();
        double[] _snow = Array.Empty<double>();
        double[] _edgeRequests = Array.Empty<double>();
        double[] _edgeFlux = Array.Empty<double>();
        double[] _outgoingRequested = Array.Empty<double>();
        double[] _outgoingScale = Array.Empty<double>();
        double _accumulator;
        double _previousWorldSeconds;
        long _tick;
        IEnvironmentCellProvider _cellProvider;

        public event Action<long> Stepped;
        public event Action<SolHydrologyCommand> CommandApplied;

        public SolHydrologyAsset Asset => asset;
        public long Tick => _tick;
        public int NodeCount => _volumes.Length;

        void Awake()
        {
            ResolveReferences();
            Initialize();
        }

        void OnEnable()
        {
            ResolveReferences();
            if (_volumes.Length == 0)
                Initialize();
            _previousWorldSeconds = environmentWorld != null
                ? environmentWorld.State.AbsoluteWorldSeconds
                : Time.timeAsDouble;
        }

        void Update()
        {
            ResolveReferences();
            BindBodyLevels();
            if (!simulate || asset == null || _volumes.Length == 0)
                return;

            double now = environmentWorld != null
                ? environmentWorld.State.AbsoluteWorldSeconds
                : Time.timeAsDouble;
            double delta = Math.Clamp(now - _previousWorldSeconds, 0d, 86400d);
            _previousWorldSeconds = now;
            _accumulator += delta;
            int bounded = 0;
            while (_accumulator >= fixedWorldStepSeconds && bounded++ < 64)
            {
                SimulateStep(fixedWorldStepSeconds);
                _accumulator -= fixedWorldStepSeconds;
            }
            if (_accumulator >= fixedWorldStepSeconds)
            {
                SimulateStep(_accumulator);
                _accumulator = 0d;
            }
        }

        public bool TryGetNodeState(string id, out SolHydrologyNodeState state)
        {
            if (id != null && _nodeIndices.TryGetValue(id, out int index))
            {
                state = new SolHydrologyNodeState(
                    asset.nodes[index].id, _volumes[index], _levels[index], _snow[index]);
                return true;
            }
            state = default;
            return false;
        }

        public bool ApplyCommand(in SolHydrologyCommand command)
        {
            if (command.NodeId == null || !_nodeIndices.TryGetValue(command.NodeId, out int index))
                return false;
            SolHydrologyNode node = asset.nodes[index];
            double value = command.Type == SolHydrologyCommandType.SetVolume
                ? command.CubicMetres
                : _volumes[index] + command.CubicMetres;
            _volumes[index] = Math.Clamp(value, node.minimumVolume, node.maximumVolume);
            UpdateLevel(index);
            BindBodyLevel(index);
            CommandApplied?.Invoke(command);
            return true;
        }

        public void SimulateStep(double deltaSeconds)
        {
            if (asset == null || deltaSeconds <= 0d || _volumes.Length != asset.nodes.Length)
                return;
            SolEnvironmentState environment = environmentWorld != null ? environmentWorld.State : default;
            ApplyClimateForcing(deltaSeconds, environment);
            ComputeEdgeRequests(deltaSeconds);
            ApplyEdgeRequests();
            for (int i = 0; i < _levels.Length; i++)
                UpdateLevel(i);
            _tick++;
            BindBodyLevels();
            Stepped?.Invoke(_tick);
        }

        public SolHydrologySnapshot CaptureSnapshot()
        {
            string[] nodeIds = new string[asset.nodes.Length];
            string[] edgeIds = new string[asset.edges.Length];
            double[] volumes = (double[])_volumes.Clone();
            double[] snow = (double[])_snow.Clone();
            double[] flux = (double[])_edgeFlux.Clone();
            for (int i = 0; i < nodeIds.Length; i++) nodeIds[i] = asset.nodes[i].id;
            for (int i = 0; i < edgeIds.Length; i++) edgeIds[i] = asset.edges[i].id;
            return new SolHydrologySnapshot(
                SnapshotVersion, _tick, nodeIds, volumes, snow, edgeIds, flux);
        }

        public bool RestoreSnapshot(in SolHydrologySnapshot snapshot)
        {
            if (snapshot.Version != SnapshotVersion || asset == null
                || snapshot.NodeIds == null || snapshot.Volumes == null
                || snapshot.NodeIds.Length != snapshot.Volumes.Length)
                return false;
            for (int i = 0; i < snapshot.NodeIds.Length; i++)
            {
                if (!_nodeIndices.TryGetValue(snapshot.NodeIds[i], out int index))
                    continue;
                SolHydrologyNode node = asset.nodes[index];
                _volumes[index] = Math.Clamp(snapshot.Volumes[i], node.minimumVolume, node.maximumVolume);
                if (snapshot.SnowStorage != null && i < snapshot.SnowStorage.Length)
                    _snow[index] = Math.Max(0d, snapshot.SnowStorage[i]);
                UpdateLevel(index);
            }
            Array.Clear(_edgeFlux, 0, _edgeFlux.Length);
            if (snapshot.EdgeIds != null && snapshot.CumulativeFlux != null)
            {
                int count = Math.Min(snapshot.EdgeIds.Length, snapshot.CumulativeFlux.Length);
                for (int i = 0; i < count; i++)
                {
                    if (_edgeIndices.TryGetValue(snapshot.EdgeIds[i], out int edge))
                        _edgeFlux[edge] = snapshot.CumulativeFlux[i];
                }
            }
            _tick = snapshot.Tick;
            _accumulator = 0d;
            BindBodyLevels();
            return true;
        }

        void Initialize()
        {
            _nodeIndices.Clear();
            _edgeIndices.Clear();
            if (asset == null)
                return;
            int nodeCount = asset.nodes.Length;
            int edgeCount = asset.edges.Length;
            _volumes = new double[nodeCount];
            _levels = new double[nodeCount];
            _snow = new double[nodeCount];
            _outgoingRequested = new double[nodeCount];
            _outgoingScale = new double[nodeCount];
            _edgeRequests = new double[edgeCount];
            _edgeFlux = new double[edgeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                SolHydrologyNode node = asset.nodes[i];
                if (string.IsNullOrWhiteSpace(node.id) || !_nodeIndices.TryAdd(node.id, i))
                {
                    Debug.LogError($"[SolHydrology] Missing or duplicate node ID '{node.id}'.", asset);
                    continue;
                }
                _volumes[i] = Math.Clamp(node.initialVolume, node.minimumVolume, node.maximumVolume);
                UpdateLevel(i);
            }
            for (int i = 0; i < edgeCount; i++)
            {
                SolHydrologyEdge edge = asset.edges[i];
                if (string.IsNullOrWhiteSpace(edge.id) || !_edgeIndices.TryAdd(edge.id, i))
                    Debug.LogError($"[SolHydrology] Missing or duplicate edge ID '{edge.id}'.", asset);
                if (!_nodeIndices.ContainsKey(edge.sourceNodeId) || !_nodeIndices.ContainsKey(edge.targetNodeId))
                    Debug.LogError($"[SolHydrology] Edge '{edge.id}' references an unknown node.", asset);
            }
            _previousWorldSeconds = environmentWorld != null
                ? environmentWorld.State.AbsoluteWorldSeconds
                : Time.timeAsDouble;
            BindBodyLevels();
        }

        void ApplyClimateForcing(double deltaSeconds, in SolEnvironmentState environment)
        {
            double rainMetresPerSecond = environment.Weather.Rain
                * rainfallMillimetresPerHourAtIntensityOne * 0.001d / 3600d;
            double snowMetresPerSecond = environment.Weather.Snow
                * rainfallMillimetresPerHourAtIntensityOne * 0.001d / 3600d;
            for (int i = 0; i < asset.nodes.Length; i++)
            {
                SolHydrologyNode node = asset.nodes[i];
                double area = Math.Max(1d, node.surfaceArea);
                double runoff = Math.Max(0d, node.catchmentMultiplier);
                if (environment.Surface.TemperatureCelsius <= 0f)
                {
                    _snow[i] += (rainMetresPerSecond + snowMetresPerSecond) * area * runoff * deltaSeconds;
                }
                else
                {
                    double liquidInput = rainMetresPerSecond * area * runoff * deltaSeconds;
                    double meltDepth = node.snowMeltMillimetresPerDegreeDay * 0.001d
                        * environment.Surface.TemperatureCelsius * deltaSeconds / 86400d;
                    double melt = Math.Min(_snow[i], meltDepth * area);
                    _snow[i] -= melt;
                    _volumes[i] += liquidInput + melt;
                }
                double evaporation = node.evaporationMillimetresPerDay * 0.001d
                    * area * deltaSeconds / 86400d;
                _volumes[i] = Math.Clamp(_volumes[i] - evaporation,
                    node.minimumVolume, node.maximumVolume);
                UpdateLevel(i);
            }
        }

        void ComputeEdgeRequests(double deltaSeconds)
        {
            Array.Clear(_edgeRequests, 0, _edgeRequests.Length);
            Array.Clear(_outgoingRequested, 0, _outgoingRequested.Length);
            for (int i = 0; i < asset.edges.Length; i++)
            {
                SolHydrologyEdge edge = asset.edges[i];
                if (!_nodeIndices.TryGetValue(edge.sourceNodeId, out int source)
                    || !_nodeIndices.TryGetValue(edge.targetNodeId, out int target))
                    continue;
                double head = _levels[source] - _levels[target];
                double direction = head >= 0d ? 1d : -1d;
                if (edge.oneWay && direction < 0d)
                    continue;
                double effectiveHead = Math.Abs(head) - Math.Max(0d, edge.minimumHead);
                if (effectiveHead <= 0d)
                    continue;
                double rate = Math.Min(Math.Max(0d, edge.capacityCubicMetresPerSecond),
                    Math.Max(0d, edge.conductanceCubicMetresPerSecondPerMetre) * effectiveHead);
                double request = rate * deltaSeconds * direction;
                _edgeRequests[i] = request;
                _outgoingRequested[request >= 0d ? source : target] += Math.Abs(request);
            }
            for (int i = 0; i < _outgoingScale.Length; i++)
            {
                double available = Math.Max(0d, _volumes[i] - asset.nodes[i].minimumVolume);
                _outgoingScale[i] = _outgoingRequested[i] > 0d
                    ? Math.Min(1d, available / _outgoingRequested[i])
                    : 0d;
            }
        }

        void ApplyEdgeRequests()
        {
            for (int i = 0; i < asset.edges.Length; i++)
            {
                double request = _edgeRequests[i];
                if (request == 0d)
                    continue;
                SolHydrologyEdge edge = asset.edges[i];
                int source = _nodeIndices[edge.sourceNodeId];
                int target = _nodeIndices[edge.targetNodeId];
                int from = request >= 0d ? source : target;
                int to = request >= 0d ? target : source;
                double transferred = Math.Abs(request) * _outgoingScale[from];
                double targetCapacity = Math.Max(0d, asset.nodes[to].maximumVolume - _volumes[to]);
                transferred = Math.Min(transferred, targetCapacity);
                _volumes[from] -= transferred;
                _volumes[to] += transferred;
                _edgeFlux[i] += request >= 0d ? transferred : -transferred;
            }
        }

        void UpdateLevel(int index)
        {
            SolHydrologyNode node = asset.nodes[index];
            _levels[index] = node.datumLevel
                + (_volumes[index] - node.minimumVolume) / Math.Max(1d, node.surfaceArea);
        }

        void BindBodyLevels()
        {
            for (int i = 0; i < _levels.Length; i++)
                BindBodyLevel(i);
        }

        void BindBodyLevel(int index)
        {
            if (SolWaterWorld.Active == null || index >= asset.nodes.Length)
                return;
            SolHydrologyNode node = asset.nodes[index];
            if (node.bodyId.IsValid && SolWaterWorld.Active.TryGetBody(node.bodyId, out SolWaterBody body))
                body.SetRuntimeSurfaceLevel((float)_levels[index]);
        }

        void ResolveReferences()
        {
            environmentWorld ??= SolEnvironmentWorld.Active;
            _cellProvider = cellProviderComponent as IEnvironmentCellProvider;
        }
    }
}
