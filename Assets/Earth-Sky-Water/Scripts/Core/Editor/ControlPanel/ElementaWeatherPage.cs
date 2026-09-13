using System;
using System.Linq;
using Sol.ToD;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using UI = Sol.Environment.EditorTools.ElementaPanelGui;

namespace Sol.Environment.EditorTools
{
    sealed class ElementaWeatherPage : ElementaPanelPage
    {
        public override string Title => "Weather";
        public override string Subtitle => "Author conditions and preview how the world moves between them.";
        protected override void Build(ElementaPanelContext context, VisualElement root)
        {
            var manager = context.Weather;
            if (manager == null) { UI.Help(root, "Assign a weather manager in Scene setup."); return; }
            var profile = ElementaCloudAuthoring.WeatherPicker(this, root);
            if (profile != null)
            {
                int index = manager.IndexOfProfile(profile);
                UI.Source(this, root, manager, $"profiles.Array.data[{index}].profile", "Condition profile");
                UI.Button(root, "Preview condition", () => ElementaPanelActions.PreviewCondition(context, profile));
                var appearance = UI.Section(root, "Condition appearance", "Profile asset · " + profile.name);
                UI.Fields(this, appearance, profile, "rainIntensity|Precipitation", "windSpeedMetresPerSecond|Wind speed (m/s)", "fogBoost|Fog boost", "visibilityMetres|Visibility (m)", "dim|Storm darkening");
                var clouds = UI.Foldout(this, root, "weather/clouds", "Cloud formation and shape");
                ElementaCloudAuthoring.Build(this, clouds, profile, true, "weather/clouds");
                var detail = UI.Foldout(this, root, "weather/profile", "Detailed condition appearance");
                UI.GroupedProperties(this, detail, "weather/profile/body", profile);
            }
            BuildPreview(context, root);
            BuildWind(context, root);
            BuildTransition(context, root);
            BuildSelection(context, root);
            UI.Advanced(this, root, "weather/component/rain", "Advanced · precipitation VFX", context.RainVfx);
        }

        void BuildPreview(ElementaPanelContext context, VisualElement root)
        {
            var manager = context.Weather; var state = context.State;
            state.weatherA ??= manager.TargetProfile ?? manager.profiles?.FirstOrDefault(p => p?.profile != null)?.profile;
            state.weatherB ??= manager.profiles?.FirstOrDefault(p => p?.profile != null && p.profile != state.weatherA)?.profile ?? state.weatherA;
            var section = UI.Foldout(this, root, "weather/preview", "Transition preview", true,
                "Live control · selecting presets does not start preview. Ending preview restores weather only.");
            var a = new ObjectField("From") { objectType = typeof(SolWeatherProfileAsset), allowSceneObjects = false, value = state.weatherA };
            var b = new ObjectField("To") { objectType = typeof(SolWeatherProfileAsset), allowSceneObjects = false, value = state.weatherB };
            a.AddToClassList("elementa-field"); b.AddToClassList("elementa-field"); section.Add(a); section.Add(b);
            a.RegisterValueChangedCallback(e => { state.weatherA = e.newValue as SolWeatherProfileAsset; context.ApplyWeatherPreview(); });
            b.RegisterValueChangedCallback(e => { state.weatherB = e.newValue as SolWeatherProfileAsset; context.ApplyWeatherPreview(); });
            var blend = new Slider("Blend", 0, 1) { showInputField = true, value = state.weatherBlend };
            blend.AddToClassList("elementa-field"); section.Add(blend);
            blend.RegisterValueChangedCallback(e => { state.weatherBlend = e.newValue; context.ApplyWeatherPreview(); });
            var actions = UI.Row(section);
            var start = UI.Button(actions, "Start weather preview", context.Window.StartWeatherPreview);
            var end = UI.Button(actions, "End weather preview", context.ClearWeatherPreview);
            UI.Button(actions, "Swap from/to", () =>
            {
                (state.weatherA, state.weatherB) = (state.weatherB, state.weatherA); state.weatherBlend = 1 - state.weatherBlend;
                a.SetValueWithoutNotify(state.weatherA); b.SetValueWithoutNotify(state.weatherB); blend.SetValueWithoutNotify(state.weatherBlend); context.ApplyWeatherPreview();
            });
            var problem = UI.Help(section, "Both profiles must be present in this scene's selection list.", HelpBoxMessageType.Warning);
            Track(() =>
            {
                bool valid = manager.IndexOfProfile(state.weatherA) >= 0 && manager.IndexOfProfile(state.weatherB) >= 0;
                bool otherOwner = manager.HasPreview && !context.HasWeatherPreview;
                start.SetEnabled(valid && !otherOwner && !context.HasWeatherPreview); end.SetEnabled(context.HasWeatherPreview);
                problem.style.display = valid && !otherOwner ? DisplayStyle.None : DisplayStyle.Flex;
                problem.text = otherOwner ? "Another tool owns the weather preview. End it there before starting an Elementa preview." : "Both profiles must be present in this scene's selection list.";
                if (!ElementaTimePage.Editing(a)) a.SetValueWithoutNotify(state.weatherA);
                if (!ElementaTimePage.Editing(b)) b.SetValueWithoutNotify(state.weatherB);
                if (!ElementaTimePage.Editing(blend)) blend.SetValueWithoutNotify(state.weatherBlend);
            });
        }

        void BuildSelection(ElementaPanelContext context, VisualElement root)
        {
            var manager = context.Weather;
            var section = UI.Foldout(this, root, "weather/profiles", "Automatic weather selection", note: "Scene settings · selection weights are independent of the shared appearance profiles.");
            var actions = UI.Row(section);
            var advance = UI.Button(actions, "Choose next weather", () => { manager.NextWeather(); context.Refresh(); });
            Track(() => advance.SetEnabled(Application.isPlaying && !context.HasWeatherPreview));
            UI.Note(section, "Choose next weather runs the weighted selection in Play Mode.");
            foreach (var selection in manager.profiles ?? Array.Empty<SolWeatherSelection>())
            {
                if (selection?.profile == null) continue;
                var selected = selection.profile; var row = new VisualElement(); row.AddToClassList("elementa-list-row"); section.Add(row);
                var label = new Label(selected.name); row.Add(label);
                UI.Button(row, "Edit", () => { context.State.inspectedWeather = selected; context.RefreshLayout(); });
                UI.Button(row, "Preview", () => ElementaPanelActions.PreviewCondition(context, selected));
                var set = UI.Button(row, "Set live", () => { context.ClearWeatherPreview(); manager.SetWeather(manager.IndexOfProfile(selected), context.State.instantWeatherChange); context.Refresh(); });
                Track(() => { set.SetEnabled(Application.isPlaying); label.text = selected.name + " · " + Chance(context, selection).ToString("P0") + " seasonal weight share"; });
            }
            var instant = new Toggle("Set live skips the transition") { value = context.State.instantWeatherChange };
            instant.RegisterValueChangedCallback(e => context.State.instantWeatherChange = e.newValue); section.Add(instant);
            UI.GroupedProperties(this, section, "weather/manager", manager);
            var affinity = UI.Foldout(this, root, "weather/affinity", "Likely next conditions", note: "Derived similarity between conditions. Seasonal weights also affect the final selection.");
            var matrix = new ScrollView(ScrollViewMode.Horizontal); affinity.Add(matrix);
            var profiles = manager.profiles?.Where(p => p?.profile != null).Select(p => p.profile).ToArray() ?? Array.Empty<SolWeatherProfileAsset>();
            var header = UI.Row(matrix); header.style.flexWrap = Wrap.NoWrap;
            var corner = new Label("From / to"); corner.AddToClassList("elementa-matrix-name"); header.Add(corner);
            foreach (var to in profiles) { var label = new Label(to.name.Length > 5 ? to.name.Substring(0, 5) : to.name) { tooltip = to.name }; label.AddToClassList("elementa-matrix-cell"); header.Add(label); }
            foreach (var from in profiles)
            {
                var row = UI.Row(matrix); row.style.flexWrap = Wrap.NoWrap;
                var label = new Label(from.name) { tooltip = from.name }; label.AddToClassList("elementa-matrix-name"); row.Add(label);
                foreach (var to in profiles)
                {
                    var cell = new Label { tooltip = from.name + " → " + to.name }; cell.AddToClassList("elementa-matrix-cell"); row.Add(cell);
                    Track(() => { float value = SolWeatherAdjacency.Affinity(from, to); cell.text = from == to ? "—" : value.ToString(".00"); cell.style.backgroundColor = new Color(.2f, .55f, .8f, from == to ? .04f : .08f + value * .25f); });
                }
            }
        }

        static float Chance(ElementaPanelContext context, SolWeatherSelection entry)
        {
            SolSeason season = context.Calendar != null ? context.Calendar.CurrentSeason : SolSeason.Spring;
            float Weight(SolWeatherSelection s) => s?.profile == null ? 0 : Mathf.Max(0, s.weight) * Mathf.Max(0, season switch
            { SolSeason.Summer => s.summerWeightMultiplier, SolSeason.Autumn => s.autumnWeightMultiplier, SolSeason.Winter => s.winterWeightMultiplier, _ => s.springWeightMultiplier });
            float total = context.Weather.profiles?.Sum(Weight) ?? 0; return total > 0 ? Weight(entry) / total : 0;
        }

        void BuildTransition(ElementaPanelContext context, VisualElement root)
        {
            var manager = context.Weather;
            var section = UI.Foldout(this, root, "weather/transition", "Transition timing");
            UI.Note(section, "Scene setting · " + UI.Describe(manager));
            UI.Fields(this, section, manager, "transitionDurationSeconds|Duration (seconds)");
            var timings = manager.transitionTimings;
            if (timings == null) return;
            string[] names = { "Wind", "Clouds", "Fog", "Water", "Precipitation", "Lightning", "Light" };
            Func<SolWeatherChannelTiming>[] values = { () => timings.wind, () => timings.clouds, () => timings.fog, () => timings.water, () => timings.precipitation, () => timings.lightning, () => timings.light };
            for (int i = 0; i < names.Length; i++)
            {
                var read = values[i]; var row = UI.Row(section);
                var label = new Label(names[i]); label.style.width = 96; row.Add(label);
                var timeline = new ElementaTimeline(new Color(.3f, .65f, .85f)); row.Add(timeline);
                Track(() => { var value = read(); timeline.SetValues(value.delay, value.span, manager.IsTransitioning ? manager.TransitionProgress : context.HasWeatherPreview ? context.State.weatherBlend : -1); timeline.tooltip = $"{value.delay:P0}–{Mathf.Clamp01(value.delay + value.span):P0}"; });
            }
            UI.Note(section, "Timing fractions scale with duration. Rain also waits for cloud cover.");
            UI.Field(this, section, manager, "transitionTimings", "Channel timing fractions");
        }

        void BuildWind(ElementaPanelContext context, VisualElement root)
        {
            var state = context.State; var section = UI.Foldout(this, root, "weather/wind", "Wind heading", note: "Live control · applies only during weather preview.");
            if (!state.windInitialized)
            {
                var direction = context.Environment.Wind.Direction;
                state.windDegrees = Mathf.Repeat(Mathf.Atan2(direction.z, direction.x) * Mathf.Rad2Deg, 360); state.windInitialized = true;
            }
            var dial = new ElementaWindDial(); section.Add(dial);
            dial.Changed += value => { state.windDegrees = value; context.ApplyWeatherPreview(); };
            var heading = new Slider("Heading (°)", 0, 359.9f) { showInputField = true, value = state.windDegrees }; heading.AddToClassList("elementa-field"); section.Add(heading);
            heading.RegisterValueChangedCallback(e => { state.windDegrees = e.newValue; context.ApplyWeatherPreview(); });
            var show = new Toggle("Show Scene View arrows") { value = state.showSceneWind }; section.Add(show);
            show.RegisterValueChangedCallback(e => { state.showSceneWind = e.newValue; SceneView.RepaintAll(); });
            Track(() => { var cloud = context.Environment.Wind.CloudDirection; dial.SetHeadings(state.windDegrees, Mathf.Repeat(Mathf.Atan2(cloud.z, cloud.x) * Mathf.Rad2Deg, 360)); if (!ElementaTimePage.Editing(heading)) heading.SetValueWithoutNotify(state.windDegrees); });
            UI.Note(section, "Thick hand: preview wind. Thin hand: current cloud heading. 0° = +X; 90° = +Z.");
            UI.Metric(this, section, "Wind now", () => ElementaPanelFormat.Wind(context.Environment.Wind.Speed));
            UI.Metric(this, section, "Sea state", () => ElementaPanelFormat.SeaState(context.Environment.Wind));
        }

        public override void DrawSceneGui(ElementaPanelContext context, SceneView sceneView)
        {
            if (!context.State.showSceneWind || context.Weather == null || sceneView == null) return;
            var wind = context.Environment.Wind; Vector3 origin = sceneView.pivot;
            float size = HandleUtility.GetHandleSize(origin) * 1.4f;
            var previous = Handles.zTest; var color = Handles.color;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            try
            {
                Arrow(origin, wind.Direction, size, new Color(.2f, .85f, 1), $"Wind {wind.Speed:0.0} m/s");
                Arrow(origin + Vector3.up * size * .22f, wind.CloudDirection, size * .82f, new Color(.75f, .55f, 1), $"Cloud {wind.CloudSpeed:0.0} m/s");
                Arrow(origin + Vector3.up * size * .44f, wind.FogAdvectionDirection, size * .7f, new Color(.6f, .68f, .78f), $"Fog {wind.FogAdvectionSpeed:0.0} m/s");
            }
            finally { Handles.zTest = previous; Handles.color = color; }
        }
        static void Arrow(Vector3 origin, Vector3 direction, float size, Color color, string label)
        {
            direction.y = 0; if (direction.sqrMagnitude < .0001f) direction = Vector3.right; direction.Normalize();
            Handles.color = color; Handles.ArrowHandleCap(0, origin, Quaternion.LookRotation(direction, Vector3.up), size, EventType.Repaint); Handles.Label(origin + direction * size, label);
        }
    }
}
