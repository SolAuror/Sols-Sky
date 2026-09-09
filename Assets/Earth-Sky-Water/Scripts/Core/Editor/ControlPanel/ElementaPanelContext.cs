using System.Collections.Generic;
using Sol.Landscape;
using Sol.Lighting;
using Sol.ToD;
using Sol.Water;
using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// The scene the control panel is authoring, resolved once per repaint and shared by
    /// every page.
    ///
    /// Pages never search the scene themselves. Elementa has eight authorities and a page
    /// that resolved its own would disagree with the others about which one is live the
    /// moment two scenes are loaded additively - the exact case the overview warns about.
    /// </summary>
    sealed class ElementaPanelContext
    {
        const double ResolveIntervalSeconds = 0.5d;
        const double IssueIntervalSeconds = 1.5d;

        double _nextResolve;
        double _nextIssueRefresh;

        public ElementaPanelContext(ElementaControlPanel window, ElementaPanelState state)
        {
            Window = window;
            State = state;
        }

        public ElementaControlPanel Window { get; }
        public ElementaPanelState State { get; }

        /// <summary>Clock, calendar and sky authority. The one authority an author picks by hand.</summary>
        public TimeOfDay Time => State.timeOfDay;

        /// <summary>Weather state machine. Also picked by hand, because previews mutate it.</summary>
        public SolWeatherManager Weather => State.weatherManager;

        public SolEnvironmentWorld World { get; private set; }
        public SolWaterWorld Water { get; private set; }
        public SolLandscapeDriver Landscape { get; private set; }
        public SolLightingDirector Lighting { get; private set; }
        public SolAtmosphereController Atmosphere { get; private set; }
        public SolEnvironmentCoordinator Coordinator { get; private set; }

        /// <summary>Every TimeOfDay in the loaded scenes, so the panel can say when there is more than one.</summary>
        public TimeOfDay[] TimeAuthorities { get; private set; } = new TimeOfDay[0];

        public bool HasAuthorities => Time != null && Weather != null;

        public void Resolve(bool force = false)
        {
            double now = EditorApplication.timeSinceStartup;
            if (!force && now < _nextResolve && Time != null)
                return;
            _nextResolve = now + ResolveIntervalSeconds;

            if (State.timeOfDay == null)
                State.timeOfDay = TimeOfDay.ResolveInstance();
            if (State.weatherManager == null)
                State.weatherManager = SolWeatherManager.Instance != null
                    ? SolWeatherManager.Instance
                    : Object.FindFirstObjectByType<SolWeatherManager>();

            World = SolEnvironmentWorld.Active != null
                ? SolEnvironmentWorld.Active
                : Object.FindFirstObjectByType<SolEnvironmentWorld>();
            Water = SolWaterWorld.Active != null
                ? SolWaterWorld.Active
                : Object.FindFirstObjectByType<SolWaterWorld>();
            Lighting = SolLightingDirector.Active != null
                ? SolLightingDirector.Active
                : Object.FindFirstObjectByType<SolLightingDirector>();
            Atmosphere = SolAtmosphereController.Active != null
                ? SolAtmosphereController.Active
                : Object.FindFirstObjectByType<SolAtmosphereController>();
            Coordinator = SolEnvironmentCoordinator.Instance != null
                ? SolEnvironmentCoordinator.Instance
                : Object.FindFirstObjectByType<SolEnvironmentCoordinator>();
            Landscape = Object.FindFirstObjectByType<SolLandscapeDriver>();
            TimeAuthorities = Object.FindObjectsByType<TimeOfDay>(FindObjectsSortMode.None);
        }

        /// <summary>Live environment tick, or the coherent fair-weather stand-in.</summary>
        public SolEnvironmentState Environment => SolEnvironmentWorld.ResolveState();

        // -- Health checks -----------------------------------------------------------
        // Cached on the context rather than on the overview page so the navigation badge
        // stays current whichever page is visible: a badge that only updates while you are
        // already looking at the page it points to is no use.

        public List<ElementaIssue> Issues { get; private set; } = new();

        public void RefreshIssues(bool force = false)
        {
            double now = EditorApplication.timeSinceStartup;
            if (!force && now < _nextIssueRefresh)
                return;
            _nextIssueRefresh = now + IssueIntervalSeconds;
            Issues = ElementaPanelDoctor.Collect(this);
        }

        /// <summary>Drop the cached checks so the next layout pass re-runs them.</summary>
        public void InvalidateIssues() => _nextIssueRefresh = 0d;

        // -- Preview ownership -------------------------------------------------------
        // Weather previews mutate the manager, so exactly one thing may own one. The
        // window owns it because it is what has an OnDisable to release it from.

        public bool HasWeatherPreview => Window.HasWeatherPreview;

        public void ApplyWeatherPreview() => Window.ApplyWeatherPreview();

        public void ClearWeatherPreview() => Window.ClearWeatherPreview();

        /// <summary>
        /// Push a change through to the visible scene. Both halves are needed: the loop
        /// update runs the [ExecuteAlways] systems, and the repaint is what redraws them.
        /// </summary>
        public void Refresh()
        {
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
            Window.Repaint();
        }

        /// <summary>
        /// Apply a change that alters which controls the current page draws, then abandon
        /// this GUI pass.
        ///
        /// IMGUI builds its layout on the layout event and reuses it for the event pass, so
        /// adding or removing controls in response to a click inside the same pass is what
        /// produces "Mismatched LayoutGroup". Bailing out and repainting is the sanctioned
        /// way; every structural action in the panel goes through here.
        /// </summary>
        public void RefreshLayout()
        {
            Refresh();
            GUIUtility.ExitGUI();
        }

        public bool Section(string key, string label, bool defaultOpen = true)
            => ElementaPanelGui.SectionHeader(this, key, label, defaultOpen);

        public GameObject ResolveAuthorityRoot()
        {
            if (Time != null)
                return Time.gameObject;
            if (Weather != null)
                return Weather.gameObject;
            if (Coordinator != null)
                return Coordinator.gameObject;
            return null;
        }
    }
}
