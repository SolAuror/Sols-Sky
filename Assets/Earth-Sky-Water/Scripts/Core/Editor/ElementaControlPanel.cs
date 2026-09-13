using System;
using System.IO;
using System.Linq;
using Sol.ToD;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Sol.Environment.EditorTools
{
    /// <summary>Elementa's retained authoring surface. Simulation remains owned by the scene.</summary>
    public sealed class ElementaControlPanel : EditorWindow
    {
        [SerializeField] ElementaPanelState _state = new();
        ElementaPanelContext _context;
        ElementaPanelPage[] _pages;
        ElementaPanelPage _active;
        SolWeatherManager _previewOwner;
        ScrollView _scroll;
        VisualElement _sidebar;
        DropdownField _navigation;
        Label _scene, _clockLabel, _previewLabel, _animation, _title, _subtitle;
        Slider _clock;
        Button _endPreview;
        bool _rebuild, _ready;
        string _targets;
        string _section;
        double _nextUpdate;
        internal ElementaPanelContext Context => _context;
        internal ElementaPanelPage ActivePage => _active;
        internal ElementaPanelPage[] Pages => _pages;
        public bool HasWeatherPreview => _previewOwner != null && _previewOwner.HasPreview;

        [MenuItem("Tools/Elementa/Control Panel")]
        public static void Open()
        {
            var window = GetWindow<ElementaControlPanel>();
            window.titleContent = new GUIContent("Elementa");
            window.minSize = new Vector2(460, 580);
            window.Show();
        }

        void EnsurePages()
        {
            _state ??= new ElementaPanelState();
            _context ??= new ElementaPanelContext(this, _state);
            _pages ??= new ElementaPanelPage[] { new ElementaOverviewPage(), new ElementaTimePage(),
                new ElementaSkyPage(), new ElementaWeatherPage(), new ElementaCloudPage(), new ElementaWaterPage(),
                new ElementaLandscapePage(), new ElementaLightingPage(), new ElementaQualityPage(), new ElementaDiagnosticsPage() };
            int remembered = Array.FindIndex(_pages, p => p.Id == _state.pageId);
            _state.page = remembered >= 0 ? remembered : Mathf.Clamp(_state.page, 0, _pages.Length - 1);
            _state.pageId = _pages[_state.page].Id;
        }

        void OnEnable()
        {
            EnsurePages();
            titleContent = new GUIContent("Elementa"); minSize = new Vector2(460, 580);
            _state.previewWeather = false;
            Subscribe(false); Subscribe(true);
            SolEnvironmentEditorDriver.SetEnvironmentWindowOpen(true);
            _context.Resolve(true); _context.RefreshIssues(true);
        }

        void Subscribe(bool add)
        {
            if (add)
            {
                EditorApplication.update += Tick;
                Undo.undoRedoPerformed += OnUndo;
                EditorApplication.projectChanged += OnProjectChanged;
                EditorApplication.playModeStateChanged += OnPlayMode;
                AssemblyReloadEvents.beforeAssemblyReload += ClearWeatherPreview;
                EditorSceneManager.sceneClosing += OnSceneClosing;
                SceneView.duringSceneGui += DuringSceneGui;
            }
            else
            {
                EditorApplication.update -= Tick;
                Undo.undoRedoPerformed -= OnUndo;
                EditorApplication.projectChanged -= OnProjectChanged;
                EditorApplication.playModeStateChanged -= OnPlayMode;
                AssemblyReloadEvents.beforeAssemblyReload -= ClearWeatherPreview;
                EditorSceneManager.sceneClosing -= OnSceneClosing;
                SceneView.duringSceneGui -= DuringSceneGui;
            }
        }

        void OnDisable()
        {
            rootVisualElement.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            _ready = false; Subscribe(false); SaveScroll(); ClearWeatherPreview();
            if (_pages != null) foreach (var page in _pages) page.Dispose();
            _context?.ReleaseBindings();
            SolEnvironmentEditorDriver.SetEnvironmentWindowOpen(false);
        }

        public void CreateGUI()
        {
            EnsurePages(); _ready = false; SaveScroll();
            _active?.Dispose(); rootVisualElement.Clear();
            string script = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this));
            string directory = Path.GetDirectoryName(script)?.Replace('\\', '/') + "/ControlPanel/UI/";
            var layout = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(directory + "ElementaPanel.uxml");
            var styles = AssetDatabase.LoadAssetAtPath<StyleSheet>(directory + "ElementaPanel.uss");
            if (layout == null || styles == null)
            {
                rootVisualElement.Add(new HelpBox("Elementa's UI assets are missing. Restore the ControlPanel/UI folder beside the editor scripts.", HelpBoxMessageType.Error));
                return;
            }
            layout.CloneTree(rootVisualElement); rootVisualElement.styleSheets.Add(styles);
            rootVisualElement.AddToClassList("elementa-window");
            _scroll = rootVisualElement.Q<ScrollView>("page-scroll");
            _sidebar = rootVisualElement.Q("sidebar");
            _navigation = rootVisualElement.Q<DropdownField>("page-picker");
            _scene = rootVisualElement.Q<Label>("scene");
            _clockLabel = rootVisualElement.Q<Label>("clock-label");
            _animation = rootVisualElement.Q<Label>("animation");
            _previewLabel = rootVisualElement.Q<Label>("preview-label");
            _title = rootVisualElement.Q<Label>("page-title");
            _subtitle = rootVisualElement.Q<Label>("page-subtitle");
            _clock = rootVisualElement.Q<Slider>("clock");
            _endPreview = rootVisualElement.Q<Button>("end-preview");
            _endPreview.clicked += ClearWeatherPreview;
            _clock.RegisterValueChangedCallback(e => ElementaPanelActions.SetClock(_context, e.newValue));
            _navigation.choices = _pages.Select(p => p.Title).ToList();
            _navigation.RegisterValueChangedCallback(e => ShowPage(e.newValue));
            rootVisualElement.Q<Button>("scene-setup").clicked += () => ShowPage("Overview", "overview/authorities");
            for (int i = 0; i < _pages.Length; i++)
            {
                if (i == 0 || i == 8)
                {
                    var group = new Label(i == 0 ? "AUTHORING" : "TECHNICAL");
                    group.AddToClassList("elementa-nav-group"); _sidebar.Add(group);
                }
                var page = _pages[i];
                var button = new Button(() => ShowPage(page.Title)) { name = "nav-" + page.Id, text = page.Title };
                button.AddToClassList("elementa-nav-item"); _sidebar.Add(button);
            }
            rootVisualElement.UnregisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            rootVisualElement.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            _ready = true; _rebuild = true; _nextUpdate = 0; UpdateLayout(); Tick();
        }

        void UpdateLayout()
        {
            rootVisualElement.EnableInClassList("elementa-compact", position.width < 620);
            rootVisualElement.EnableInClassList("elementa-dark", EditorGUIUtility.isProSkin);
        }
        void OnGeometryChanged(GeometryChangedEvent evt) => UpdateLayout();

        public void ShowPage(string title) => ShowPage(title, null);
        internal void ShowPage(string title, string section)
        {
            EnsurePages();
            int index = Array.FindIndex(_pages, p => p.Title == title || p.Id == title);
            if (index < 0) return;
            SaveScroll(); _state.page = index; _state.pageId = _pages[index].Id;
            _section = section; RequestRebuild();
        }
        void SaveScroll()
        {
            if (_active != null && _scroll != null)
                _state.SetScroll(Array.IndexOf(_pages, _active), _scroll.scrollOffset);
        }
        internal void RequestRebuild() { _rebuild = true; RequestRefresh(); }
        internal void RequestRefresh() => _nextUpdate = 0;

        void Tick()
        {
            if (!_ready || EditorApplication.isCompiling) return;
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextUpdate) return;
            _nextUpdate = now + (Application.isPlaying || HasWeatherPreview || (_context.Time != null && _context.Time.AnimatesInEditMode) ? .1 : .5);
            _context.Resolve(); _context.CheckEdits(); _context.RefreshIssues();
            string targets = TargetSignature();
            if (_targets != targets) { _targets = targets; _rebuild = true; }
            if (_previewOwner != null && (_previewOwner != _context.Weather || !_previewOwner.HasPreview)) ClearWeatherPreview();
            if (_rebuild)
            {
                _rebuild = false; SaveScroll(); _active?.Dispose(); _scroll.Clear();
                _active = _pages[_state.page];
                _title.text = _active.Title; _subtitle.text = _active.Subtitle;
                _scroll.Add(_active.BuildContent(_context));
                var offset = _state.GetScroll(_state.page);
                _scroll.schedule.Execute(() =>
                {
                    _scroll.scrollOffset = offset;
                    if (string.IsNullOrEmpty(_section)) return;
                    var section = _active.Root?.Q(_section);
                    if (section is Foldout foldout) foldout.value = true;
                    if (section != null) _scroll.ScrollTo(section);
                    _section = null;
                });
            }
            _active.Refresh(); UpdateToolbar(); UpdateLayout();
        }

        string TargetSignature()
        {
            // Background authorities can appear during an editor simulation tick. Only
            // replace controls when the visible page actually depends on that authority.
            UnityEngine.Object[] targets = _state.pageId switch
            {
                "time" => new UnityEngine.Object[] { _context.Time, _context.Calendar },
                "sky" => new UnityEngine.Object[] { _context.Time, _context.Atmosphere, _context.Time?.SkyProfile },
                "clouds" => new UnityEngine.Object[] { _context.Time, _context.Weather, _context.Time?.SkyProfile },
                "weather" => new UnityEngine.Object[] { _context.Time, _context.Weather, _context.RainVfx },
                "water" => new UnityEngine.Object[] { _context.Water, _context.Water?.DefaultProfile, _context.Shoreline, _context.PlanarReflections },
                "landscape" => new UnityEngine.Object[] { _context.Landscape, _context.Landscape?.config, _context.Wetness },
                "lighting" => new UnityEngine.Object[] { _context.Lighting, _context.Time, _context.Time?.SkyProfile },
                _ => new UnityEngine.Object[] {
            _context.Time, _context.Weather, _context.Calendar, _context.World, _context.Water,
            _context.Lighting, _context.Atmosphere, _context.Coordinator, _context.Landscape,
            _context.RainVfx, _context.Shoreline, _context.Wetness, _context.PlanarReflections,
            _context.Time != null ? _context.Time.SkyProfile : null,
            _context.Water != null ? _context.Water.DefaultProfile : null,
            _context.Lighting != null ? _context.Lighting.QualityProfile : null
                }
            };
            return _state.pageId + "|" + string.Join("/", targets.Select(o => o != null ? o.GetInstanceID() + ":" + o.name : "0"))
                + "|" + (_state.pageId == "water" && _context.Water?.Bodies != null
                    ? string.Join(",", _context.Water.Bodies.Select(b => b != null ? b.GetInstanceID() + ":" + b.name : "0")) : "");
        }

        void UpdateToolbar()
        {
            var time = _context.Time;
            _scene.text = time != null ? time.gameObject.scene.name : "No clock assigned";
            _scene.tooltip = "Clock target: " + ElementaPanelGui.Describe(time);
            _clock.SetEnabled(time != null);
            if (time != null) _clock.SetValueWithoutNotify(time.ClockHour);
            _clockLabel.text = time != null ? ElementaPanelFormat.Clock(time.ClockHour) : "--:--";
            _animation.text = Application.isPlaying ? "Play Mode" : time != null && time.AnimatesInEditMode ? "Animating" : "Edit Mode";
            _previewLabel.text = HasWeatherPreview
                ? $"Weather preview · {_state.weatherA?.name} → {_state.weatherB?.name} · {_state.weatherBlend:P0}"
                : "Scene weather";
            _endPreview.style.display = HasWeatherPreview ? DisplayStyle.Flex : DisplayStyle.None;
            _navigation.SetValueWithoutNotify(_pages[_state.page].Title);
            foreach (var page in _pages)
            {
                var button = _sidebar.Q<Button>("nav-" + page.Id);
                int badge = page.Badge(_context); button.text = page.Title + (badge > 0 ? "  (" + badge + ")" : "");
                button.EnableInClassList("elementa-selected", page == _active);
            }
        }

        void OnHierarchyChange() { if (_context == null) return; _context.Resolve(true); _context.CheckEdits(true); _context.InvalidateIssues(); RequestRefresh(); }
        void OnProjectChanged() { _context?.CheckEdits(true); _context?.InvalidateIssues(); RequestRefresh(); }
        void OnUndo() { _context?.CheckEdits(true); _context?.InvalidateIssues(); RequestRefresh(); }
        void OnPlayMode(PlayModeStateChange state) { ClearWeatherPreview(); RequestRebuild(); }
        void OnSceneClosing(Scene scene, bool removingScene) { ClearWeatherPreview(); RequestRebuild(); }
        void DuringSceneGui(SceneView view) => _active?.DrawSceneGui(_context, view);

        public void StartWeatherPreview()
        {
            EnsurePages();
            if (!ValidPreview()) return;
            if (_context.Weather.HasPreview && _previewOwner != _context.Weather) return;
            if (!_state.windInitialized)
            {
                _state.windDegrees = ElementaPanelFormat.WindDegrees(_context.Environment.Wind.Direction);
                _state.windInitialized = true;
            }
            _state.previewWeather = true; ApplyWeatherPreview();
        }
        bool ValidPreview() => _state.weatherManager != null && _state.weatherManager.IndexOfProfile(_state.weatherA) >= 0
            && _state.weatherManager.IndexOfProfile(_state.weatherB) >= 0;
        public void ApplyWeatherPreview()
        {
            if (!_state.previewWeather) return;
            if (!ValidPreview()) { ClearWeatherPreview(); return; }
            var manager = _state.weatherManager;
            if (_previewOwner != null && _previewOwner != manager) _previewOwner.ClearPreview();
            _previewOwner = manager;
            manager.SetPreview(manager.IndexOfProfile(_state.weatherA), manager.IndexOfProfile(_state.weatherB), _state.weatherBlend);
            manager.SetPreviewWindDirection(_state.windDegrees);
            _context.Refresh();
        }
        public void ClearWeatherPreview()
        {
            if (_state != null) _state.previewWeather = false;
            if (_previewOwner != null) _previewOwner.ClearPreview();
            _previewOwner = null;
            EditorApplication.QueuePlayerLoopUpdate(); SceneView.RepaintAll(); RequestRefresh();
        }
    }
}
