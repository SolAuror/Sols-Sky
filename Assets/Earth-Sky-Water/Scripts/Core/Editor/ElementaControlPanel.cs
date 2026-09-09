using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// The authoring surface for Elementa: one dockable, play-mode-free window covering time,
    /// sky, weather, clouds, water, landscape, lighting and diagnostics.
    ///
    /// Two rules hold the whole thing together. First, the panel owns no simulation state: it
    /// issues the same time-change requests, reads the same published environment state and
    /// blends the same weather snapshot as runtime, so it can never disagree with the game
    /// about what the world is doing. Second, it is the whole tool surface rather than most of
    /// it - the bakers, migrations and setup steps that used to be scattered across three
    /// separate top-level menus are reachable from the pages they belong to, next to the
    /// checks that say when they are needed.
    ///
    /// The window itself is deliberately thin. It resolves the scene's authorities once per
    /// repaint, owns the weather preview so exactly one thing can hold one, and hands both to
    /// whichever page is visible.
    /// </summary>
    public sealed class ElementaControlPanel : EditorWindow
    {
        /// <summary>Below this width the sidebar costs more room than it earns.</summary>
        const float SidebarBreakpoint = 620f;
        const float SidebarWidth = 138f;
        const double RepaintIntervalSeconds = 0.1d;

        [SerializeField] ElementaPanelState _state = new();

        ElementaPanelContext _context;
        ElementaPanelPage[] _pages;
        SolWeatherManager _previewOwner;
        double _nextWindowRepaint;

        [MenuItem("Tools/Elementa/Control Panel")]
        public static void Open()
        {
            ElementaControlPanel window = GetWindow<ElementaControlPanel>();
            window.titleContent = new GUIContent("Elementa");
            window.minSize = new Vector2(430f, 560f);
            window.Show();
        }

        public bool HasWeatherPreview => _previewOwner != null;

        /// <summary>
        /// Switch to a page by title. Used by the health check, so a repair that belongs on
        /// another page can route the author there instead of describing where to go.
        /// </summary>
        public void ShowPage(string title)
        {
            EnsurePages();
            for (int i = 0; i < _pages.Length; i++)
            {
                if (_pages[i].Title != title)
                    continue;
                _state.page = i;
                Repaint();
                return;
            }
        }

        // -- Lifecycle ---------------------------------------------------------------

        void OnEnable()
        {
            titleContent = new GUIContent("Elementa");
            EnsurePages();
            SolEnvironmentEditorDriver.SetEnvironmentWindowOpen(true);
            SolEnvironmentEditorDriver.PreviewTick -= OnPreviewTick;
            SolEnvironmentEditorDriver.PreviewTick += OnPreviewTick;
            SceneView.duringSceneGui -= DuringSceneGui;
            SceneView.duringSceneGui += DuringSceneGui;

            _context.Resolve(force: true);
            _context.RefreshIssues(force: true);
            for (int i = 0; i < _pages.Length; i++)
                _pages[i].OnWindowEnable(_context);
        }

        void OnDisable()
        {
            SolEnvironmentEditorDriver.PreviewTick -= OnPreviewTick;
            SceneView.duringSceneGui -= DuringSceneGui;

            if (_pages != null)
                for (int i = 0; i < _pages.Length; i++)
                    _pages[i].OnWindowDisable(_context);

            ClearWeatherPreview();
            SolEnvironmentEditorDriver.SetEnvironmentWindowOpen(false);
        }

        void OnHierarchyChange()
        {
            EnsurePages();
            _context.Resolve(force: true);
            _context.InvalidateIssues();
            Repaint();
        }

        void OnPreviewTick(float deltaSeconds)
        {
            // The driver ticks at editor frame rate. Repainting the whole panel that often
            // would spend more editor CPU on the readouts than on the preview they describe.
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextWindowRepaint)
                return;

            _nextWindowRepaint = now + RepaintIntervalSeconds;
            Repaint();
        }

        void EnsurePages()
        {
            _state ??= new ElementaPanelState();
            _context ??= new ElementaPanelContext(this, _state);
            _pages ??= new ElementaPanelPage[]
            {
                new ElementaOverviewPage(),
                new ElementaTimePage(),
                new ElementaSkyPage(),
                new ElementaWeatherPage(),
                new ElementaCloudPage(),
                new ElementaWaterPage(),
                new ElementaLandscapePage(),
                new ElementaLightingPage(),
                new ElementaDiagnosticsPage(),
            };
            _state.page = Mathf.Clamp(_state.page, 0, _pages.Length - 1);
        }

        // -- Drawing -----------------------------------------------------------------

        void OnGUI()
        {
            EnsurePages();

            // Only on the layout pass. Both of these can change how many controls a page
            // draws - an authority appearing, a check clearing - and changing that between
            // the layout pass and the event pass that reuses it is what makes IMGUI complain
            // about mismatched groups.
            if (Event.current.type == EventType.Layout)
            {
                _context.Resolve();
                _context.RefreshIssues();
            }

            DrawStatusBar();

            if (position.width >= SidebarBreakpoint)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawSidebar();
                    using (new EditorGUILayout.VerticalScope())
                        DrawActivePage();
                }
            }
            else
            {
                DrawCompactNavigation();
                DrawActivePage();
            }
        }

        void DrawStatusBar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                string scene = _context.Time != null
                    ? _context.Time.gameObject.scene.name : "no authority";
                GUILayout.Label(scene, EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();

                if (Application.isPlaying)
                    GUILayout.Label("PLAYING", EditorStyles.miniLabel);
                else if (_context.Time != null && _context.Time.AnimatesInEditMode)
                    GUILayout.Label("animating", EditorStyles.miniLabel);

                if (HasWeatherPreview)
                {
                    GUILayout.Label("weather preview", EditorStyles.miniLabel);
                    if (GUILayout.Button("release", EditorStyles.toolbarButton))
                    {
                        _state.previewWeather = false;
                        ClearWeatherPreview();
                    }
                }
            }
        }

        void DrawSidebar()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(SidebarWidth)))
            {
                EditorGUILayout.Space(4f);
                DrawNavigationButtons(SidebarWidth - 8f);
                GUILayout.FlexibleSpace();
            }
        }

        void DrawCompactNavigation()
        {
            EditorGUILayout.Space(2f);
            int half = (_pages.Length + 1) / 2;
            using (new EditorGUILayout.HorizontalScope())
                DrawNavigationRange(0, half, 0f);
            using (new EditorGUILayout.HorizontalScope())
                DrawNavigationRange(half, _pages.Length, 0f);
            EditorGUILayout.Space(2f);
        }

        void DrawNavigationButtons(float width)
        {
            for (int i = 0; i < _pages.Length; i++)
                DrawNavigationButton(i, width);
        }

        void DrawNavigationRange(int start, int end, float width)
        {
            for (int i = start; i < end; i++)
                DrawNavigationButton(i, width);
        }

        void DrawNavigationButton(int index, float width)
        {
            bool active = index == _state.page;
            int badge = _pages[index].Badge(_context);
            string label = badge > 0
                ? $"{_pages[index].Title}  ({badge})"
                : _pages[index].Title;

            bool next = width > 0f
                ? GUILayout.Toggle(active, label, "Button",
                    GUILayout.Width(width), GUILayout.Height(24f))
                : GUILayout.Toggle(active, label, "Button", GUILayout.Height(22f));

            if (!next || active)
                return;

            _state.page = index;
            // A page switch replaces every control in the content column, so the current
            // layout is no longer the one being drawn.
            _context.RefreshLayout();
        }

        void DrawActivePage()
        {
            ElementaPanelPage page = _pages[_state.page];
            EditorGUILayout.Space(4f);
            ElementaPanelGui.PageTitle(page.Title, page.Subtitle);

            _state.contentScroll = EditorGUILayout.BeginScrollView(_state.contentScroll);
            page.Draw(_context);
            EditorGUILayout.Space(12f);
            EditorGUILayout.EndScrollView();
        }

        void DuringSceneGui(SceneView sceneView)
        {
            EnsurePages();
            _pages[_state.page].DrawSceneGui(_context, sceneView);
        }

        // -- Weather preview ownership ----------------------------------------------

        /// <summary>
        /// Blend the two selected presets on the resolved manager.
        ///
        /// A preview mutates the manager, so exactly one thing may hold one at a time and
        /// whoever holds it has to be able to give it back. That is the window rather than the
        /// weather page, because the window is what has an OnDisable to release it from: a
        /// preview left applied when the panel closes would keep the scene blended with no
        /// visible cause.
        /// </summary>
        public void ApplyWeatherPreview()
        {
            SolWeatherManager manager = _state.weatherManager;
            if (manager == null)
                return;

            int indexA = manager.IndexOfProfile(_state.weatherA);
            int indexB = manager.IndexOfProfile(_state.weatherB);
            if (indexA < 0 || indexB < 0)
                return;

            if (_previewOwner != null && _previewOwner != manager)
                _previewOwner.ClearPreview();

            _previewOwner = manager;
            manager.SetPreview(indexA, indexB, _state.weatherBlend);
            manager.SetPreviewWindDirection(_state.windDegrees);
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }

        public void ClearWeatherPreview()
        {
            if (_previewOwner != null)
                _previewOwner.ClearPreview();
            _previewOwner = null;
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }
    }
}
