using UnityEditor;
using UnityEngine;
using Sol.Lighting;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// Reads out the per-frame environment budget counters.
    ///
    /// A menu item rather than an overlay on purpose: the counters describe the frame that
    /// just finished, and anything that repaints to display them would itself be part of
    /// the frame being measured.
    ///
    /// The control panel's Diagnostics page now shows and logs the same counters. This is kept
    /// because it reaches them without opening a window - which matters when the question is
    /// what a frame cost with no editor window repainting on top of it - and because the menu
    /// path is what batch-mode invocations already name.
    /// </summary>
    static class SolEnvironmentBudgetMenu
    {
        [MenuItem("Tools/Sol Environment/Log Frame Budget")]
        static void LogBudget()
        {
            SolEnvironmentBudget.Counters counters = SolEnvironmentBudget.Previous;
            Debug.Log(
                "[SolBudget] last completed frame\n"
                + $"  FFT dispatches:        {counters.FftDispatches}\n"
                + $"  Full-res colour copies:{counters.FullResColorCopies}\n"
                + $"  Environment updates:   {counters.EnvironmentUpdates}\n"
                + $"  Atmosphere pushes:     {counters.AtmosphereGlobalPushes}\n"
                + $"  Lighting tier:          {(counters.HasLightingDirector ? ((SolLightingQualityTier)counters.ActiveLightingTier).ToString() : "none")}\n"
                + $"  Lights registered/active: {counters.RegisteredLights}/{counters.ActiveLights}\n"
                + $"  Shadow slices:         {counters.ShadowSlices}\n"
                + $"  Volumetric lights:     {counters.VolumetricLights}\n"
                + $"  GI requests:           {counters.GiRequests}\n"
                + $"  Probe requests/done:   {counters.ProbeRequests}/{counters.ProbeCompletions}\n"
                + $"  Live camera contexts:  {counters.CameraContexts}\n"
                + $"  SSR history:           {counters.SsrHistoryBytes / (1024f * 1024f):F1} MB");
        }
    }
}
