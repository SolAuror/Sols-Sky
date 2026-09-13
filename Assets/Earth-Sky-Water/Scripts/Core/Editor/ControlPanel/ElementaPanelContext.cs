using System;
using System.Collections.Generic;
using System.Text;
using Sol.Landscape;
using Sol.Lighting;
using Sol.ToD;
using Sol.Water;
using Sol.Water.Rendering;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// The scene the control panel is authoring, resolved on a throttled schedule and shared by
    /// every page.
    ///
    /// Pages never search the scene themselves. Elementa has a dozen scene-wide authorities
    /// and a page that resolved its own would disagree with the others about which one is
    /// live the moment two scenes are loaded additively - the exact case the overview warns
    /// about.
    ///
    /// Only the Water 2 stack is resolved here. The Water 1 manager, ripple manager and
    /// underwater volume are deliberately absent: they are the superseded system, and a
    /// panel that offered to author them would be inviting edits that change nothing.
    /// </summary>
    sealed class ElementaPanelContext
    {
        const double ResolveIntervalSeconds = 0.5d;
        const double IssueIntervalSeconds = 1.5d;

        readonly Dictionary<Object, SerializedObject> _serialized = new();
        readonly Dictionary<Object, WatchedObject> _watched = new();
        double _nextEditCheck;
        bool _publishing;
        internal int EditPublicationCount { get; private set; }
        sealed class WatchedObject
        {
            public int Dirty;
            public string Json;
            public string Structure;
        }

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

        public Calendar Calendar => Time != null ? Time.Calendar : null;

        public SolEnvironmentWorld World { get; private set; }
        public SolEnvironmentCoordinator Coordinator { get; private set; }

        public SolAtmosphereController Atmosphere { get; private set; }
        public SolLightingDirector Lighting { get; private set; }
        public SolRainVfxController RainVfx { get; private set; }

        public SolWaterWorld Water { get; private set; }
        public SolTerrainShoreline Shoreline { get; private set; }
        public SolWaterWetness Wetness { get; private set; }
        public SolPlanarReflectionRenderer PlanarReflections { get; private set; }

        public SolLandscapeDriver Landscape { get; private set; }

        /// <summary>Every TimeOfDay in the loaded scenes, so the panel can say when there is more than one.</summary>
        public TimeOfDay[] TimeAuthorities { get; private set; } = new TimeOfDay[0];

        public bool HasAuthorities => Time != null && Weather != null;

        public void Resolve(bool force = false)
        {
            double now = EditorApplication.timeSinceStartup;
            if (!force && now < _nextResolve)
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
            RainVfx = Object.FindFirstObjectByType<SolRainVfxController>();
            Shoreline = Object.FindFirstObjectByType<SolTerrainShoreline>();
            Wetness = Object.FindFirstObjectByType<SolWaterWetness>();
            PlanarReflections = Object.FindFirstObjectByType<SolPlanarReflectionRenderer>();
            Landscape = Object.FindFirstObjectByType<SolLandscapeDriver>();
            TimeAuthorities = Object.FindObjectsByType<TimeOfDay>(FindObjectsSortMode.None);
            Watch(Time); Watch(Calendar); Watch(Weather); Watch(World); Watch(Coordinator);
            Watch(Water); Watch(Lighting); Watch(Atmosphere); Watch(Landscape);
            Watch(RainVfx); Watch(Shoreline); Watch(Wetness); Watch(PlanarReflections);
            Watch(Time != null ? Time.SkyProfile : null);
            Watch(Water != null ? Water.DefaultProfile : null);
            Watch(Water != null ? Water.QualityProfile : null);
            Watch(Lighting != null ? Lighting.QualityProfile : null);
            Watch(Landscape != null ? Landscape.config : null);
            if (Weather?.profiles != null)
                foreach (var entry in Weather.profiles) Watch(entry?.profile);
            if (Water?.Bodies != null)
                foreach (var body in Water.Bodies) { Watch(body); if (body != null) Watch(body.Profile); }
        }

        /// <summary>Live environment tick, or the coherent fair-weather stand-in.</summary>
        public SolEnvironmentState Environment => SolEnvironmentWorld.ResolveState();

        /// <summary>
        /// Cached serialized view of an authored object.
        ///
        /// Shared because a page can now show a dozen components and rebuilding a
        /// SerializedObject per repaint would drop in-progress edits on multi-line fields.
        /// Released with the page bindings before window disposal.
        /// </summary>
        public SerializedObject Serialized(Object target)
        {
            if (target == null)
                return null;
            if (_serialized.TryGetValue(target, out SerializedObject cached)
                && cached != null && cached.targetObject == target)
                return cached;

            SerializedObject created = new(target);
            _serialized[target] = created;
            return created;
        }

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
            Window.RequestRefresh();
        }

        /// <summary>Schedule a rebuild after a target or collection structure changes.</summary>
        public void RefreshLayout()
        {
            Refresh();
            Window.RequestRebuild();
        }

        public void Watch(Object target)
        {
            if (target == null || _watched.ContainsKey(target)) return;
            _watched.Add(target, new WatchedObject { Dirty = EditorUtility.GetDirtyCount(target),
                Json = AuthoredSnapshot(target), Structure = Structure(target) });
        }

        // Dirty counters avoid serializing objects on every UI update. JSON comparison filters
        // saves and repeated validation notifications; structural changes alone rebuild controls.
        public void CheckEdits(bool force = false, bool immediate = false)
        {
            double now = EditorApplication.timeSinceStartup;
            if (!force && !immediate && now < _nextEditCheck) return;
            _nextEditCheck = now + 0.5d;
            foreach (var pair in new List<KeyValuePair<Object, WatchedObject>>(_watched))
            {
                Object target = pair.Key;
                if (target == null) { _watched.Remove(pair.Key); RefreshLayoutIfVisible(pair.Key); continue; }
                var old = pair.Value;
                int dirty = EditorUtility.GetDirtyCount(target);
                if (!force && dirty == old.Dirty) continue;
                old.Dirty = dirty;
                string json = AuthoredSnapshot(target);
                if (json == old.Json) continue;
                old.Json = json;
                string structure = Structure(target);
                if (structure != old.Structure) { old.Structure = structure; RefreshLayoutIfVisible(target); }
                PublishEdit(target);
            }
        }

        void RefreshLayoutIfVisible(Object target)
        {
            var entries = Window.ActivePage?.Catalogue.Entries;
            if (entries == null) return;
            foreach (var entry in entries)
                if (ReferenceEquals(entry.Target, target)) { RefreshLayout(); return; }
        }

        string AuthoredSnapshot(Object target)
        {
            if (target is not TimeOfDay && target is not Sol.ToD.Calendar) return EditorJsonUtility.ToJson(target);
            // Live clocks and diagnostic outputs are serialized for Unity, but are not edits.
            // They must never invalidate temporal histories through the authoring observer.
            var serialized = Serialized(target); serialized.UpdateIfRequiredOrScript();
            var property = serialized.GetIterator(); bool children = true;
            var result = new StringBuilder();
            while (property.NextVisible(children))
            {
                children = false; string path = property.propertyPath;
                if (path == "m_Script" || path == "timeOfDay" || path.StartsWith("debug", StringComparison.Ordinal) ||
                    target is Sol.ToD.Calendar && (path.StartsWith("current", StringComparison.Ordinal) || path == "worldDayIndex" || path == "playerDaysElapsed")) continue;
                result.Append(path).Append(property.contentHash);
            }
            return result.ToString();
        }

        string Structure(Object target)
        {
            var serialized = Serialized(target);
            serialized.UpdateIfRequiredOrScript();
            var property = serialized.GetIterator();
            var text = new StringBuilder();
            while (property.NextVisible(true))
            {
                if (property.propertyType == SerializedPropertyType.ObjectReference)
                    text.Append(property.propertyPath).Append('=').Append(property.objectReferenceInstanceIDValue).Append(';');
                else if (property.isArray && property.propertyType != SerializedPropertyType.String)
                    text.Append(property.propertyPath).Append('#').Append(property.arraySize).Append(';');
            }
            return text.ToString();
        }

        public void PublishEdit(Object target)
        {
            if (_publishing || target == null) return;
            _publishing = true;
            try
            {
                EditPublicationCount++;
                if (target is SolSkyProfile || target == Time)
                {
                    Time?.RefreshSkyPresentation();
                }
                if (target is SolCloudRenderingProfile || target is SolWeatherProfileAsset || target is SolCloudRendererFeature)
                    SolCloudController.Active.InvalidateHistory();
                if (target is SolWeatherProfileAsset || target == Weather)
                {
                    if (HasWeatherPreview) ApplyWeatherPreview();
                }
                if (target is SolLandscapeConfig || target == Landscape)
                    Landscape?.Invalidate();
                InvalidateIssues();
                Refresh();
            }
            finally { _publishing = false; }
        }

        public void ReleaseBindings()
        {
            _watched.Clear();
            foreach (var serialized in _serialized.Values) serialized?.Dispose();
            _serialized.Clear();
        }

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
