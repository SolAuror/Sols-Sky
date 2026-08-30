using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sol.Water
{
    /// <summary>
    /// Query front-end. Low-tier/Gerstner results are immediate; FFT-capable GPU readback can replace
    /// ProcessBatch without changing gameplay callers.
    /// </summary>
    // Runs outside play mode to match SolWaterWorld, which adds this component and hands
    // it out as QueryService. A play-only service meant every edit-mode caller resolved a
    // component whose LateUpdate never ran.
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class SolWaterQueryService : MonoBehaviour, ISolWaterQueryService
    {
        sealed class PendingBatch
        {
            internal SolWaterQueryBatchHandle Handle;
            internal Vector3[] Positions;
            internal SolWaterSurfaceSample[] Results;
            internal Action<SolWaterQueryBatchHandle, IReadOnlyList<SolWaterSurfaceSample>> Completed;
            internal int RequestedFrame;
        }

        readonly Queue<PendingBatch> _pending = new();
        readonly Dictionary<uint, PendingBatch> _byId = new();
        uint _nextHandle = 1;

        public bool TrySampleImmediate(Vector3 localPosition, out SolWaterSurfaceSample sample)
        {
            SolWaterWorld world = SolWaterWorld.Active;
            if (world == null)
            {
                sample = SolWaterSurfaceSample.NoWater(localPosition);
                return false;
            }
            return world.TrySampleApproximate(localPosition, out sample);
        }

        public SolWaterQueryBatchHandle RequestBatch(
            IReadOnlyList<Vector3> localPositions,
            Action<SolWaterQueryBatchHandle, IReadOnlyList<SolWaterSurfaceSample>> completed)
        {
            if (localPositions == null || localPositions.Count == 0)
                return default;

            uint id = _nextHandle++;
            if (id == 0)
                id = _nextHandle++;
            PendingBatch batch = new()
            {
                Handle = new SolWaterQueryBatchHandle(id),
                Positions = new Vector3[localPositions.Count],
                Results = new SolWaterSurfaceSample[localPositions.Count],
                Completed = completed,
                RequestedFrame = Time.frameCount,
            };
            for (int i = 0; i < localPositions.Count; i++)
                batch.Positions[i] = localPositions[i];
            _pending.Enqueue(batch);
            _byId.Add(id, batch);
            return batch.Handle;
        }

        public bool Cancel(SolWaterQueryBatchHandle handle)
        {
            if (!handle.IsValid || !_byId.Remove(handle.Value, out PendingBatch batch))
                return false;
            batch.Completed = null;
            return true;
        }

        void LateUpdate()
        {
            // One-frame asynchronous contract now; the FFT pass can fill the same result arrays via AsyncGPUReadback.
            int count = _pending.Count;
            for (int i = 0; i < count; i++)
            {
                PendingBatch batch = _pending.Dequeue();
                if (!_byId.ContainsKey(batch.Handle.Value))
                    continue;
                if (batch.RequestedFrame >= Time.frameCount)
                {
                    _pending.Enqueue(batch);
                    continue;
                }
                ProcessBatch(batch);
                _byId.Remove(batch.Handle.Value);
                batch.Completed?.Invoke(batch.Handle, batch.Results);
            }
        }

        void ProcessBatch(PendingBatch batch)
        {
            SolWaterWorld world = SolWaterWorld.Active;
            for (int i = 0; i < batch.Positions.Length; i++)
            {
                if (world == null || !world.TrySampleApproximate(batch.Positions[i], out SolWaterSurfaceSample sample))
                    sample = SolWaterSurfaceSample.NoWater(batch.Positions[i]);
                batch.Results[i] = sample;
            }
        }

        void OnDisable()
        {
            _pending.Clear();
            _byId.Clear();
        }
    }
}
