using Sol.ToD;
using System;
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
        static bool _environmentWindowOpen;
        static double _lastTickTime;

        /// <summary>
        /// Raised after the editor player loop has been queued. Editor-only presentation
        /// systems can use the bounded wall-clock delta without making runtime assemblies
        /// depend on EditorApplication.
        /// </summary>
        internal static event Action<float> PreviewTick;

        static SolEnvironmentEditorDriver()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            _lastTickTime = EditorApplication.timeSinceStartup;
        }

        internal static void SetEnvironmentWindowOpen(bool open)
        {
            _environmentWindowOpen = open;
            _lastTickTime = EditorApplication.timeSinceStartup;
        }

        static void Tick()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                return;

            TimeOfDay authority = TimeOfDay.Instance;
            if (authority == null)
            {
                // An open environment window is an explicit request for a live preview.
                // Otherwise only pay for the scene search while the author is interacting
                // with an editor window, so an idle non-Sol scene stays free.
                if (!_environmentWindowOpen && EditorWindow.mouseOverWindow == null)
                    return;
                authority = TimeOfDay.ResolveInstance();
            }

            bool clockPreview = authority != null
                             && authority.isActiveAndEnabled
                             && authority.AnimatesInEditMode;
            if (!clockPreview && !_environmentWindowOpen)
                return;

            double now = EditorApplication.timeSinceStartup;
            float deltaSeconds = Mathf.Clamp((float)(now - _lastTickTime), 0f, 0.1f);
            _lastTickTime = now;

            // QueuePlayerLoopUpdate runs the [ExecuteAlways] Updates; the repaint is what
            // makes the result visible. Both are needed -- a repaint alone re-renders the
            // same frozen state, and a loop update alone advances state nobody redraws.
            EditorApplication.QueuePlayerLoopUpdate();
            if (_environmentWindowOpen)
                PreviewTick?.Invoke(deltaSeconds);
            SceneView.RepaintAll();
        }
    }
}
