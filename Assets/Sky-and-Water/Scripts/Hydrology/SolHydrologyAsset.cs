using System;
using Sol.Streaming;
using Sol.Water;
using UnityEngine;

namespace Sol.Hydrology
{
    public enum SolHydrologyNodeType : byte
    {
        Ocean,
        Lake,
        Reservoir,
        RiverReach,
        WaterfallPool,
    }

    [Serializable]
    public struct SolHydrologyNode
    {
        public string id;
        public SolHydrologyNodeType type;
        public SolWaterBodyId bodyId;
        public SolEnvironmentCellId cellId;
        [Min(1f)] public double surfaceArea;
        [Min(0f)] public double initialVolume;
        [Min(0f)] public double minimumVolume;
        [Min(0f)] public double maximumVolume;
        public double datumLevel;
        [Min(0f)] public float catchmentMultiplier;
        [Min(0f)] public float evaporationMillimetresPerDay;
        [Min(0f)] public float snowMeltMillimetresPerDegreeDay;
    }

    [Serializable]
    public struct SolHydrologyEdge
    {
        public string id;
        public string sourceNodeId;
        public string targetNodeId;
        [Min(0f)] public double conductanceCubicMetresPerSecondPerMetre;
        [Min(0f)] public double capacityCubicMetresPerSecond;
        [Min(0f)] public double minimumHead;
        public bool oneWay;
        public bool isWaterfall;
    }

    [CreateAssetMenu(menuName = "Sol/Environment/Hydrology Graph", fileName = "Sol Hydrology")]
    public sealed class SolHydrologyAsset : ScriptableObject
    {
        public SolHydrologyNode[] nodes = Array.Empty<SolHydrologyNode>();
        public SolHydrologyEdge[] edges = Array.Empty<SolHydrologyEdge>();

        void OnValidate()
        {
            if (nodes == null)
                nodes = Array.Empty<SolHydrologyNode>();
            if (edges == null)
                edges = Array.Empty<SolHydrologyEdge>();
            for (int i = 0; i < nodes.Length; i++)
            {
                SolHydrologyNode node = nodes[i];
                node.id = node.id?.Trim();
                node.surfaceArea = Math.Max(1d, node.surfaceArea);
                node.minimumVolume = Math.Max(0d, node.minimumVolume);
                node.maximumVolume = Math.Max(node.minimumVolume, node.maximumVolume);
                node.initialVolume = Math.Clamp(node.initialVolume, node.minimumVolume, node.maximumVolume);
                nodes[i] = node;
            }
        }
    }
}
