using Sol.ToD;
using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// Drives the environment preview while the editor is not playing.
    ///
    /// [ExecuteAlways] Update only runs when something has already caused the editor to
    /// repaint, so without a driver an edit-mode preview advances in stutters while the
    /// mouse moves and freezes the moment it stops. There was no such driver anywhere in
    /// the project: the only repaint call was a single SceneView.RepaintAll in the water
    /// tile grid editor.
    ///
    /// This ticks only when a TimeOfDay in the open scenes is actually asking to animate,
    /// so an ordinary editing session costs nothing and no scene is kept dirty by a
    /// preview nobody turned on.
    /// </summary>
    [InitializeOnLoad]
    static class SolEnvironmentEditorDriver
    {
        static SolEnvironmentEditorDriver()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                return;

            TimeOfDay authority = TimeOfDay.Instance;
            if (authority == null)
            {
                // Only pay for the scene search when the pointer is over a Scene View, so
                // an idle editor with no Sol scene loaded does no work per tick.
                if (EditorWindow.mouseOverWindow == null)
                    return;
                authority = TimeOfDay.ResolveInstance();
            }

            if (authority == null || !authority.isActiveAndEnabled || !authority.AnimatesInEditMode)
                return;

            // QueuePlayerLoopUpdate runs the [ExecuteAlways] Updates; the repaint is what
            // makes the result visible. Both are needed -- a repaint alone re-renders the
            // same frozen state, and a loop update alone advances state nobody redraws.
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }
    }
}
