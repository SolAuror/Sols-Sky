using System;
using System.Collections.Generic;
using Sol.Environment;
using UnityEngine;

namespace Sol.Streaming
{
    [Serializable]
    public struct SolEnvironmentCellId : IEquatable<SolEnvironmentCellId>
    {
        [SerializeField] string value;

        public string Value => value ?? string.Empty;
        public bool IsValid => !string.IsNullOrWhiteSpace(value);

        public SolEnvironmentCellId(string value) => this.value = value?.Trim() ?? string.Empty;
        public bool Equals(SolEnvironmentCellId other)
            => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is SolEnvironmentCellId other && Equals(other);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value;
        public static bool operator ==(SolEnvironmentCellId left, SolEnvironmentCellId right) => left.Equals(right);
        public static bool operator !=(SolEnvironmentCellId left, SolEnvironmentCellId right) => !left.Equals(right);
    }

    public enum SolEnvironmentCellState : byte
    {
        Unloaded,
        Loading,
        Loaded,
        Unloading,
        Failed,
    }

    /// <summary>Backend-neutral environment cell lifetime and origin notification contract.</summary>
    public interface IEnvironmentCellProvider
    {
        event Action<SolEnvironmentCellId> CellLoaded;
        event Action<SolEnvironmentCellId> CellUnloaded;
        event Action<SolEnvironmentCellId, string> CellFailed;
        event Action<Vector3, SolDouble3> OriginShifted;

        IReadOnlyCollection<SolEnvironmentCellId> LoadedCells { get; }
        SolEnvironmentCellState GetState(SolEnvironmentCellId id);
        bool RequestLoad(SolEnvironmentCellId id);
        bool RequestUnload(SolEnvironmentCellId id);
        void NotifyOriginShift(Vector3 localShift, SolDouble3 logicalOrigin);
    }
}
