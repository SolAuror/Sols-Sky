using UnityEditor;
using UnityEngine;

namespace Sol.Landscape.Editor
{
    /// <summary>
    /// Keeps the landscape responding while the editor is not playing.
    /// </summary>
    /// <remarks>
    /// Layer weights are resolved per pixel in the shader from the live terrain normal, world height
    /// and the published rule parameters, so sculpting needs no editor code at all: the Terrain tools
    /// already repaint the Scene view, and the material re-resolves as part of that frame.
    ///
    /// What does need a nudge is editing the rules themselves. Changing a slope centre on the config
    /// asset dirties a ScriptableObject that no [ExecuteAlways] component is watching, so without this
    /// the Scene view keeps showing the previous frame's globals until something else happens to
    /// repaint. Invalidate forces the driver to re-push its cached contract, QueuePlayerLoopUpdate runs
    /// the driver's LateUpdate, and the repaint is what makes the result visible - all three are
    /// needed, and each on its own does nothing useful.
    /// </remarks>
    [InitializeOnLoad]
    internal static class SolLandscapeRealtimeEditor
    {
        static SolLandscapeRealtimeEditor()
        {
            Undo.postprocessModifications -= OnPropertiesModified;
            Undo.postprocessModifications += OnPropertiesModified;
            Undo.undoRedoPerformed -= Refresh;
            Undo.undoRedoPerformed += Refresh;
        }

        private static UndoPropertyModification[] OnPropertiesModified(UndoPropertyModification[] modifications)
        {
            for (int i = 0; i < modifications.Length; i++)
            {
                Object target = modifications[i].currentValue?.target;
                if (target is SolLandscapeConfig
                    || target is SolLandscapeDriver
                    || target is TerrainLayer
                    || target is TerrainData
                    || target is Terrain)
                {
                    Refresh();
                    break;
                }
            }

            return modifications;
        }

        private static void Refresh()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            {
                return;
            }

            SolLandscapeDriver[] drivers = Object.FindObjectsByType<SolLandscapeDriver>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            if (drivers.Length == 0)
            {
                return;
            }

            for (int i = 0; i < drivers.Length; i++)
            {
                drivers[i].Invalidate();
            }

            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }
    }
}
