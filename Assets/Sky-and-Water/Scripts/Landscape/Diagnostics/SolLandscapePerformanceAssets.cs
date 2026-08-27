using UnityEngine;

namespace Sol.Landscape.Diagnostics
{
    /// <summary>
    /// Build-only references required by the ticket 5A standalone measurement player.
    /// Keeping both materials reachable ensures the retained legacy shader and its add pass, and
    /// the array shader's _SOL_LANDSCAPE_STOCHASTIC ON variant (a shader_feature_local_fragment
    /// with no material [Toggle], so it only survives build stripping if some serialized material
    /// in the build already has it enabled) survive player-build stripping.
    ///
    /// Ticket 5G (N12): this asset and the materials it references are persisted OUTSIDE any
    /// Resources/ folder (Diagnostics/HarnessAssets/) so a normal production build carries none of
    /// this. The editor-only build script stages a copy of THIS asset into Resources/ immediately
    /// before BuildPipeline.BuildPlayer and deletes it immediately after; Unity pulls the
    /// referenced materials in as build dependencies only while that staged copy exists.
    /// </summary>
    public sealed class SolLandscapePerformanceAssets : ScriptableObject
    {
        public Material arrayMaterial;
        public Material arrayMaterialStochastic;
        public Material legacyMaterial;
    }
}
