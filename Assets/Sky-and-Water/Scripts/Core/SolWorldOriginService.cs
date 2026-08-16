using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sol.Environment
{
    [Serializable]
    public readonly struct SolDouble3 : IEquatable<SolDouble3>
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public SolDouble3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public SolDouble3(Vector3 value) : this(value.x, value.y, value.z) { }
        public static SolDouble3 operator +(SolDouble3 a, SolDouble3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static SolDouble3 operator -(SolDouble3 a, SolDouble3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
        public bool Equals(SolDouble3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
        public override bool Equals(object obj) => obj is SolDouble3 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    }

    public interface ISolOriginShiftParticipant
    {
        void OnSolOriginShift(Vector3 localShift, SolDouble3 logicalOrigin);
    }

    /// <summary>Maintains a double-precision logical origin while Unity objects stay near float zero.</summary>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class SolWorldOriginService : MonoBehaviour
    {
        public static SolWorldOriginService Active { get; private set; }

        [SerializeField, Min(100f)] float automaticShiftThreshold = 5000f;
        [SerializeField] Transform trackingTarget;
        [SerializeField] bool automaticShifts;

        readonly List<ISolOriginShiftParticipant> _participants = new(32);

        public SolDouble3 LogicalOrigin { get; private set; }
        public event Action<Vector3, SolDouble3> OriginShifted;

        void OnEnable()
        {
            if (Active != null && Active != this)
            {
                Debug.LogError("[SolWorldOriginService] Duplicate service disabled.", this);
                enabled = false;
                return;
            }
            Active = this;
        }

        void OnDisable()
        {
            if (Active == this)
                Active = null;
        }

        void LateUpdate()
        {
            if (!automaticShifts || trackingTarget == null)
                return;
            Vector3 position = trackingTarget.position;
            if (position.sqrMagnitude >= automaticShiftThreshold * automaticShiftThreshold)
                ShiftOrigin(position);
        }

        public void Register(ISolOriginShiftParticipant participant)
        {
            if (participant != null && !_participants.Contains(participant))
                _participants.Add(participant);
        }

        public void Unregister(ISolOriginShiftParticipant participant) => _participants.Remove(participant);

        public void ShiftOrigin(Vector3 localShift)
        {
            if (localShift.sqrMagnitude < 0.000001f)
                return;

            LogicalOrigin += new SolDouble3(localShift);
            for (int i = _participants.Count - 1; i >= 0; i--)
            {
                ISolOriginShiftParticipant participant = _participants[i];
                if (participant == null)
                {
                    _participants.RemoveAt(i);
                    continue;
                }
                participant.OnSolOriginShift(localShift, LogicalOrigin);
            }
            OriginShifted?.Invoke(localShift, LogicalOrigin);
            SolEnvironmentCameraRegistry.Clear();
        }

        public SolDouble3 ToLogical(Vector3 localPosition) => LogicalOrigin + new SolDouble3(localPosition);
        public Vector3 ToLocal(in SolDouble3 logicalPosition) => (logicalPosition - LogicalOrigin).ToVector3();
    }

    /// <summary>Moves an authored root when the floating origin changes.</summary>
    public sealed class SolOriginShiftRoot : MonoBehaviour, ISolOriginShiftParticipant
    {
        [SerializeField] Transform targetRoot;

        void OnEnable() => SolWorldOriginService.Active?.Register(this);
        void Start() => SolWorldOriginService.Active?.Register(this);
        void OnDisable() => SolWorldOriginService.Active?.Unregister(this);

        public void OnSolOriginShift(Vector3 localShift, SolDouble3 logicalOrigin)
        {
            Transform target = targetRoot != null ? targetRoot : transform;
            target.position -= localShift;
        }
    }
}
