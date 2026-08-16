using System;

namespace Sol.Water
{
    [Serializable]
    public readonly struct SolWaterBodyRuntimeState
    {
        public readonly SolWaterBodyId BodyId;
        public readonly float SurfaceLevel;
        public readonly uint LocalSimulationSeed;

        public SolWaterBodyRuntimeState(
            SolWaterBodyId bodyId,
            float surfaceLevel,
            uint localSimulationSeed)
        {
            BodyId = bodyId;
            SurfaceLevel = surfaceLevel;
            LocalSimulationSeed = localSimulationSeed;
        }
    }

    [Serializable]
    public readonly struct SolWaterSnapshot
    {
        public readonly int Version;
        public readonly double WaveTime;
        public readonly SolWaterBodyRuntimeState[] Bodies;

        public SolWaterSnapshot(int version, double waveTime, SolWaterBodyRuntimeState[] bodies)
        {
            Version = version;
            WaveTime = waveTime;
            Bodies = bodies;
        }
    }

    public enum SolWaterCommandType : byte
    {
        SetBodyLevel,
        ClearBodyLevelOverride,
    }

    [Serializable]
    public readonly struct SolWaterCommand
    {
        public readonly SolWaterCommandType Type;
        public readonly SolWaterBodyId BodyId;
        public readonly float Value;
        public readonly ulong Sequence;

        public SolWaterCommand(
            SolWaterCommandType type,
            SolWaterBodyId bodyId,
            float value,
            ulong sequence = 0)
        {
            Type = type;
            BodyId = bodyId;
            Value = value;
            Sequence = sequence;
        }
    }
}
