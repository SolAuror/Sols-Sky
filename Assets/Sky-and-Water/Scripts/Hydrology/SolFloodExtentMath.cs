using UnityEngine;

namespace Sol.Hydrology
{
    /// <summary>
    /// Turns a hydrology water level plus a terrain height field into a flood extent.
    ///
    /// The rule is just "terrain below the water level is flooded", but two things stop
    /// that being usable directly:
    ///
    /// Rebuilding the mesh every time the level moves by a millimetre would rebuild it
    /// every frame, so rebuilds are hysteretic on a level delta.
    ///
    /// And a raw below/above test produces a hard stair-stepped boundary on the sample
    /// grid. The submergence depth at each corner is kept instead, so callers can fade
    /// the edge over the last few centimetres of depth rather than terminating on a
    /// grid line.
    ///
    /// Separated from the MonoBehaviour so the extent maths is testable without a
    /// terrain, a mesh, or a scene.
    /// </summary>
    public static class SolFloodExtentMath
    {
        /// <summary>Submergence depth at a sample, negative when the terrain is dry.</summary>
        public static float Submergence(float terrainHeight, float waterLevel)
            => waterLevel - terrainHeight;

        /// <summary>
        /// True when a grid cell contributes surface area. A cell counts as flooded when
        /// any corner is under water, so the boundary is captured rather than dropped
        /// the moment one corner pokes out.
        /// </summary>
        public static bool CellIsFlooded(float cornerA, float cornerB, float cornerC,
            float cornerD, float waterLevel)
            => Submergence(cornerA, waterLevel) > 0f
                || Submergence(cornerB, waterLevel) > 0f
                || Submergence(cornerC, waterLevel) > 0f
                || Submergence(cornerD, waterLevel) > 0f;

        /// <summary>
        /// Edge fade for a sample, reaching one once the water is deeper than
        /// <paramref name="fadeDepth"/>. This is what keeps the shoreline off the grid.
        /// </summary>
        public static float EdgeFade(float terrainHeight, float waterLevel, float fadeDepth)
        {
            float submergence = Submergence(terrainHeight, waterLevel);
            if (submergence <= 0f)
                return 0f;
            return fadeDepth <= 0.0001f ? 1f : Mathf.Clamp01(submergence / fadeDepth);
        }

        /// <summary>
        /// Whether a level change is worth rebuilding the mesh for. Without hysteresis a
        /// continuously simulating hydrology node rebuilds its mesh every frame.
        /// </summary>
        public static bool ShouldRebuild(float builtLevel, float currentLevel,
            float rebuildThreshold)
            => !float.IsFinite(builtLevel)
                || Mathf.Abs(currentLevel - builtLevel) >= Mathf.Max(0.0001f, rebuildThreshold);

        /// <summary>
        /// Surface area of the flooded region, in square metres. Reported back to
        /// hydrology so a node's level-to-volume relationship can follow the terrain it
        /// actually covers instead of a single authored constant.
        /// </summary>
        public static double FloodedArea(int floodedCellCount, float cellSize)
            => (double)floodedCellCount * cellSize * cellSize;
    }
}
