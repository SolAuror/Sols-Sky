using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// Reads out the per-frame environment budget counters.
    ///
    /// A menu item rather than an overlay on purpose: the counters describe the frame that
    /// just finished, and anything that repaints to display them would itself be part of
    /// the frame being measured. This will be folded into the environment window later; for
    /// now it exists so questions like "is the spectrum recorded once or twice" have an
    /// answer that is not a guess.
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
                + $"  Live camera contexts:  {counters.CameraContexts}\n"
                + $"  SSR history:           {counters.SsrHistoryBytes / (1024f * 1024f):F1} MB");
        }
    }
}
