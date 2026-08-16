using System;
using UnityEngine;

namespace Sol.Water
{
    /// <summary>Stable serialized identifier used by saves, streamed cells, and network snapshots.</summary>
    [Serializable]
    public struct SolWaterBodyId : IEquatable<SolWaterBodyId>
    {
        [SerializeField] string value;

        public string Value => value ?? string.Empty;
        public bool IsValid => !string.IsNullOrWhiteSpace(value);

        public SolWaterBodyId(string value)
        {
            this.value = value?.Trim() ?? string.Empty;
        }

        public static SolWaterBodyId NewId() => new(Guid.NewGuid().ToString("N"));

        /// <summary>Stable non-random 24-bit value suitable for water-prepass encoding.</summary>
        public int StableHash24
        {
            get
            {
                unchecked
                {
                    uint hash = 2166136261u;
                    string text = Value;
                    for (int i = 0; i < text.Length; i++)
                        hash = (hash ^ text[i]) * 16777619u;
                    return (int)(hash & 0x00ffffffu);
                }
            }
        }

        public bool Equals(SolWaterBodyId other)
            => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is SolWaterBodyId other && Equals(other);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value;
        public static bool operator ==(SolWaterBodyId left, SolWaterBodyId right) => left.Equals(right);
        public static bool operator !=(SolWaterBodyId left, SolWaterBodyId right) => !left.Equals(right);
    }
}
