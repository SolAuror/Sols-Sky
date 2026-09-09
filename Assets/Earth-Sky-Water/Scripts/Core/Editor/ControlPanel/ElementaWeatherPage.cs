using Sol.ToD;
using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// Weather authoring: the A/B preview, the selection policy, and the two things about
    /// this system that are derived rather than authored and so have nowhere else to be read.
    ///
    /// The affinity matrix and the transition timeline are the point of this page. Adjacency
    /// is computed from the profiles' own values, so the only way to see that Clear can no
    /// longer reach Storm in one step is to show the matrix; the channel choreography is
    /// fourteen delay/span numbers whose entire meaning is their ordering.
    /// </summary>
    sealed class ElementaWeatherPage : ElementaPanelPage
    {
        static readonly string[] ChannelNames =
        {
            "Wind", "Clouds", "Fog", "Water", "Precipitation", "Lightning", "Light",
        };

        static readonly Color[] ChannelColors =
        {
            new(0.20f, 0.85f, 1.00f), new(0.75f, 0.55f, 1.00f), new(0.60f, 0.68f, 0.78f),
            new(0.25f, 0.55f, 0.95f), new(0.35f, 0.80f, 0.55f), new(1.00f, 0.85f, 0.35f),
            new(1.00f, 0.60f, 0.35f),
        };

        SerializedObject _serializedManager;
        SerializedObject _serializedProfile;

        public override string Title => "Weather";

        public override string Subtitle =>
            "Preview any pair of conditions, review how selection and transitions are derived, "
            + "and author the profiles and the selection policy.";

        public override void Draw(ElementaPanelContext context)
        {
            if (context.Weather == null)
            {
                EditorGUILayout.HelpBox(
                    "No SolWeatherManager. Assign one on the Overview page.",
                    MessageType.Warning);
                return;
            }

            EnsureDefaultProfiles(context);
            DrawPreview(context);
            DrawLiveState(context);
            DrawProfiles(context);
            DrawAffinity(context);
            DrawTransition(context);
            DrawWind(context);
            DrawManagerSettings(context);
            DrawInspectedProfile(context);
        }

        public override void DrawSceneGui(ElementaPanelContext context, SceneView sceneView)
        {
            if (!context.State.showSceneWind || context.Weather == null || sceneView == null)
                return;

            SolEnvironmentWindState wind = context.Environment.Wind;
            Vector3 origin = sceneView.pivot;
            float size = HandleUtility.GetHandleSize(origin) * 1.4f;
            UnityEngine.Rendering.CompareFunction previousZTest = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            try
            {
                DrawSceneArrow(origin, wind.Direction, size,
                    new Color(0.2f, 0.85f, 1f, 0.95f), $"Wind {wind.Speed:0.0} m/s");
                DrawSceneArrow(origin + Vector3.up * size * 0.22f,
                    wind.CloudDirection, size * 0.82f,
                    new Color(0.75f, 0.55f, 1f, 0.95f), $"Cloud {wind.CloudSpeed:0.0} m/s");
                DrawSceneArrow(origin + Vector3.up * size * 0.44f,
                    wind.FogAdvectionDirection, size * 0.7f,
                    new Color(0.6f, 0.68f, 0.78f, 0.95f), $"Fog {wind.FogAdvectionSpeed:0.0} m/s");
            }
            finally
            {
                Handles.zTest = previousZTest;
            }
        }

        static void DrawSceneArrow(
            Vector3 origin, Vector3 direction, float size, Color color, string label)
        {
            Vector3 horizontal = new(direction.x, 0f, direction.z);
            if (horizontal.sqrMagnitude < 0.0001f)
                horizontal = Vector3.right;
            horizontal.Normalize();

            Handles.color = color;
            Handles.ArrowHandleCap(0, origin,
                Quaternion.LookRotation(horizontal, Vector3.up), size, EventType.Repaint);
            Handles.Label(origin + horizontal * size, label);
        }

        // -- Preview -----------------------------------------------------------------

        void DrawPreview(ElementaPanelContext context)
        {
            if (!context.Section("weather/preview", "Preview"))
                return;

            ElementaPanelState state = context.State;
            SolWeatherManager manager = context.Weather;

            EditorGUI.BeginChangeCheck();
            state.previewWeather = EditorGUILayout.ToggleLeft(
                "Preview in edit mode", state.previewWeather);
            state.weatherA = (SolWeatherProfileAsset)EditorGUILayout.ObjectField(
                "Preset A", state.weatherA, typeof(SolWeatherProfileAsset), false);
            state.weatherB = (SolWeatherProfileAsset)EditorGUILayout.ObjectField(
                "Preset B", state.weatherB, typeof(SolWeatherProfileAsset), false);
            state.weatherBlend = EditorGUILayout.Slider("Blend", state.weatherBlend, 0f, 1f);
            bool changed = EditorGUI.EndChangeCheck();

            int indexA = manager.IndexOfProfile(state.weatherA);
            int indexB = manager.IndexOfProfile(state.weatherB);
            bool valid = indexA >= 0 && indexB >= 0;

            if (!valid)
            {
                EditorGUILayout.HelpBox(
                    "Both presets must appear in this manager's Profiles list. Selection "
                    + "policy is scene-local, so a profile the scene does not list has no "
                    + "index to blend from.",
                    MessageType.Warning);
            }

            if (!state.previewWeather)
                context.ClearWeatherPreview();
            else if (valid && (changed || !manager.HasPreview || !context.HasWeatherPreview))
                context.ApplyWeatherPreview();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (ElementaPanelGui.ActionButton("Clear Preview",
                        "Release the preview and restore the state that was live beforehand.",
                        context.HasWeatherPreview, 120f))
                {
                    state.previewWeather = false;
                    context.ClearWeatherPreview();
                }

                if (ElementaPanelGui.ActionButton("Swap A/B",
                        "Exchange the two presets and mirror the blend.", valid, 90f))
                {
                    (state.weatherA, state.weatherB) = (state.weatherB, state.weatherA);
                    state.weatherBlend = 1f - state.weatherBlend;
                    context.ApplyWeatherPreview();
                }

                if (ElementaPanelGui.ActionButton("Advance (weighted)",
                        "Roll the next weather using the real weighted, season-biased, "
                        + "adjacency-weighted selection. Play mode only.",
                        Application.isPlaying, 150f))
                {
                    manager.NextWeather();
                    context.Refresh();
                }
            }

            if (valid && state.weatherA != state.weatherB)
            {
                float affinity = SolWeatherAdjacency.Affinity(state.weatherA, state.weatherB);
                float distance = SolWeatherAdjacency.Distance(state.weatherA, state.weatherB);
                ElementaPanelGui.Note(
                    $"A -> B separation {distance:0.000} (sigma {SolWeatherAdjacency.Sigma:0.00}), "
                    + $"selection affinity {affinity:0.00}. Below about 0.2 the two are not "
                    + "plausible successors and the system will route through a middle rung.");
            }
        }

        // -- Live state --------------------------------------------------------------

        void DrawLiveState(ElementaPanelContext context)
        {
            if (!context.Section("weather/state", "Resolved State"))
                return;

            SolWeatherManager manager = context.Weather;
            SolWeatherState current = manager.CurrentState;

            SolWeatherProfileAsset target = manager.TargetProfile;
            ElementaPanelGui.Metric("Target", target != null ? target.name : "(none)");
            ElementaPanelGui.MetricBar("Transition", manager.TransitionProgress,
                manager.IsTransitioning ? $"{manager.TransitionProgress:P0}" : "settled");

            ElementaPanelGui.MetricBar("Cloudiness", current.Cloudiness);
            ElementaPanelGui.MetricBar("Cloud erosion", current.CloudErosion);
            ElementaPanelGui.MetricBar("Rain", current.RainIntensity);
            ElementaPanelGui.MetricBar("Snow bias", Mathf.InverseLerp(-1f, 1f, current.SnowBias),
                current.SnowBias.ToString("0.00"));
            ElementaPanelGui.MetricBar("Fog boost", Mathf.Clamp01(current.FogBoost * 0.5f),
                current.FogBoost.ToString("0.00"));
            ElementaPanelGui.MetricBar("Mistiness", current.Mistiness);
            ElementaPanelGui.MetricBar("Sky obscuration", current.SkyObscuration);
            ElementaPanelGui.MetricBar("Dim", current.Dim);
            ElementaPanelGui.MetricBar("Water turbulence", current.WaterTurbulence);
            ElementaPanelGui.MetricBar("Lightning", current.LightningIntensity);
            ElementaPanelGui.Metric("Wave speed", $"{current.WaveSpeedMultiplier:0.00}x");
            ElementaPanelGui.Metric("Fog density floor", $"{current.FogDensityFloor:0.00000}");

            EditorGUILayout.Space(2f);
            ElementaPanelGui.Metric("Daily fog target", $"{manager.DailyFogTarget:0.00}");
            ElementaPanelGui.Metric("Daily fog now", $"{manager.CurrentDailyFog:0.00}");
            ElementaPanelGui.Metric("Fog diurnal factor", $"{manager.FogDiurnalFactor:0.00}");
            ElementaPanelGui.Metric("Daily coverage offset", $"{manager.DailyCoverageOffset:+0.000;-0.000;0.000}");
            ElementaPanelGui.Note(
                "Daily fog and the coverage offset are derived from the climate seed and the "
                + "world day, so the same profile does not look identical every time it recurs.");
        }

        // -- Profiles ----------------------------------------------------------------

        void DrawProfiles(ElementaPanelContext context)
        {
            SolWeatherManager manager = context.Weather;
            SolWeatherSelection[] profiles = manager.profiles;
            int count = profiles != null ? profiles.Length : 0;

            if (!context.Section("weather/profiles", $"Profiles ({count})"))
                return;

            if (count == 0)
            {
                EditorGUILayout.HelpBox(
                    "This manager lists no profiles, so automatic selection has nothing to "
                    + "choose between.",
                    MessageType.Warning);
                return;
            }

            SolWeatherProfileAsset target = manager.TargetProfile;
            SolSeason season = ResolveSeason(context);
            float totalWeight = 0f;
            for (int i = 0; i < count; i++)
                if (profiles[i]?.profile != null)
                    totalWeight += SeasonalWeight(profiles[i], season);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Profile", EditorStyles.miniBoldLabel, GUILayout.Width(140f));
                EditorGUILayout.LabelField("Chance", EditorStyles.miniBoldLabel, GUILayout.Width(56f));
                EditorGUILayout.LabelField("Cover", EditorStyles.miniBoldLabel, GUILayout.Width(50f));
                EditorGUILayout.LabelField("Affinity", EditorStyles.miniBoldLabel, GUILayout.Width(56f));
                GUILayout.FlexibleSpace();
            }

            for (int i = 0; i < count; i++)
            {
                SolWeatherSelection selection = profiles[i];
                SolWeatherProfileAsset profile = selection?.profile;
                if (profile == null)
                {
                    EditorGUILayout.LabelField($"{i}. (empty selection entry)", EditorStyles.miniLabel);
                    continue;
                }

                bool isTarget = profile == target;
                using (new EditorGUILayout.HorizontalScope())
                {
                    string label = isTarget ? $"* {profile.name}" : profile.name;
                    if (GUILayout.Button(label, EditorStyles.miniButton, GUILayout.Width(140f)))
                    {
                        context.State.inspectedWeatherIndex =
                            context.State.inspectedWeatherIndex == i ? -1 : i;
                        context.RefreshLayout();
                    }

                    float chance = totalWeight > 0f
                        ? SeasonalWeight(selection, season) / totalWeight : 0f;
                    EditorGUILayout.LabelField($"{chance:P0}", GUILayout.Width(56f));
                    EditorGUILayout.LabelField(
                        $"{SolWeatherAdjacency.EffectiveCoverage(profile):0.00}",
                        GUILayout.Width(50f));
                    EditorGUILayout.LabelField(
                        isTarget ? "-" : $"{SolWeatherAdjacency.Affinity(target, profile):0.00}",
                        GUILayout.Width(56f));

                    if (GUILayout.Button("A", EditorStyles.miniButtonLeft, GUILayout.Width(22f)))
                    {
                        context.State.weatherA = profile;
                        context.ApplyWeatherPreview();
                    }

                    if (GUILayout.Button("B", EditorStyles.miniButtonMid, GUILayout.Width(22f)))
                    {
                        context.State.weatherB = profile;
                        context.ApplyWeatherPreview();
                    }

                    if (GUILayout.Button("Set", EditorStyles.miniButtonRight, GUILayout.Width(38f)))
                        SetWeather(context, i);
                }
            }

            EditorGUILayout.Space(2f);
            context.State.instantWeatherChange = EditorGUILayout.ToggleLeft(
                "Set skips the blend (play mode)", context.State.instantWeatherChange);
            ElementaPanelGui.Note(
                $"Chance is the weighted roll for {season}, before adjacency biases it toward "
                + "the plausible successors of the current condition. Affinity is that bias.");
        }

        void SetWeather(ElementaPanelContext context, int index)
        {
            if (Application.isPlaying)
            {
                context.Weather.SetWeather(index, context.State.instantWeatherChange);
            }
            else
            {
                // Edit mode has no transition clock, so a "set" is a preview pinned at one end.
                context.State.weatherA = context.Weather.profiles[index].profile;
                context.State.weatherB = context.State.weatherA;
                context.State.weatherBlend = 1f;
                context.State.previewWeather = true;
                context.ApplyWeatherPreview();
            }

            context.Refresh();
        }

        static SolSeason ResolveSeason(ElementaPanelContext context)
        {
            Calendar calendar = context.Time != null ? context.Time.Calendar : null;
            return calendar != null ? calendar.CurrentSeason : SolSeason.Spring;
        }

        static float SeasonalWeight(SolWeatherSelection selection, SolSeason season)
        {
            float multiplier = season switch
            {
                SolSeason.Summer => selection.summerWeightMultiplier,
                SolSeason.Autumn => selection.autumnWeightMultiplier,
                SolSeason.Winter => selection.winterWeightMultiplier,
                _ => selection.springWeightMultiplier,
            };
            return Mathf.Max(0f, selection.weight) * Mathf.Max(0f, multiplier);
        }

        // -- Affinity ----------------------------------------------------------------

        void DrawAffinity(ElementaPanelContext context)
        {
            if (!context.Section("weather/affinity", "Selection Adjacency", false))
                return;

            SolWeatherSelection[] profiles = context.Weather.profiles;
            if (profiles == null || profiles.Length == 0)
                return;

            ElementaPanelGui.Note(
                "How plausible each condition is as the successor of each other one, derived "
                + "from cover, precipitation amount and phase, wind and darkening. Bright is "
                + "adjacent. Nothing here is authored, and nothing is ever fully vetoed: "
                + $"{SolWeatherAdjacency.MinimumAffinity:0.00} is the floor.");

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(104f);
                for (int column = 0; column < profiles.Length; column++)
                {
                    SolWeatherProfileAsset profile = profiles[column]?.profile;
                    GUILayout.Label(Short(profile, 4), EditorStyles.miniLabel, GUILayout.Width(34f));
                }

                GUILayout.FlexibleSpace();
            }

            for (int row = 0; row < profiles.Length; row++)
            {
                SolWeatherProfileAsset from = profiles[row]?.profile;
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(Short(from, 14), EditorStyles.miniLabel, GUILayout.Width(100f));
                    for (int column = 0; column < profiles.Length; column++)
                    {
                        SolWeatherProfileAsset to = profiles[column]?.profile;
                        Rect cell = GUILayoutUtility.GetRect(34f, 16f, GUILayout.Width(34f));
                        if (from == null || to == null)
                            continue;

                        if (from == to)
                        {
                            EditorGUI.DrawRect(cell, new Color(0f, 0f, 0f, 0.35f));
                            continue;
                        }

                        float affinity = SolWeatherAdjacency.Affinity(from, to);
                        EditorGUI.DrawRect(cell, ElementaPanelGui.Heat(affinity));
                        GUI.Label(cell, affinity.ToString(".00"), EditorStyles.miniLabel);
                    }

                    GUILayout.FlexibleSpace();
                }
            }
        }

        static string Short(SolWeatherProfileAsset profile, int length)
        {
            if (profile == null)
                return "-";
            string name = profile.name;
            return name.Length <= length ? name : name.Substring(0, length);
        }

        // -- Transition --------------------------------------------------------------

        void DrawTransition(ElementaPanelContext context)
        {
            if (!context.Section("weather/transition", "Transition Choreography", false))
                return;

            SolWeatherManager manager = context.Weather;
            SolWeatherTransitionTimings timings = manager.transitionTimings;
            if (timings == null)
            {
                EditorGUILayout.HelpBox("This manager has no transition timings.", MessageType.Info);
                return;
            }

            ElementaPanelGui.Metric("Master duration", $"{manager.transitionDurationSeconds:0.00} s");
            float master = manager.IsTransitioning ? manager.TransitionProgress : -1f;

            SolWeatherChannelTiming[] channels =
            {
                timings.wind, timings.clouds, timings.fog, timings.water,
                timings.precipitation, timings.lightning, timings.light,
            };

            for (int i = 0; i < channels.Length; i++)
                ElementaPanelGui.TimelineRow(
                    ChannelNames[i], channels[i].delay, channels[i].span, ChannelColors[i], master);

            ElementaPanelGui.Note(
                "Fractions of the master transition, so retiming the whole change rescales the "
                + "choreography without disturbing the order. Precipitation is additionally "
                + "gated on cloud cover while cover is building, so rain cannot fall out of a "
                + "sky that has not arrived.");

            SerializedObject serialized = ResolveSerializedManager(manager);
            serialized.Update();
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(
                serialized.FindProperty("transitionTimings"), new GUIContent("Edit timings"), true);
            if (EditorGUI.EndChangeCheck())
            {
                serialized.ApplyModifiedProperties();
                context.Refresh();
            }
        }

        // -- Wind --------------------------------------------------------------------

        void DrawWind(ElementaPanelContext context)
        {
            if (!context.Section("weather/wind", "Wind"))
                return;

            ElementaPanelState state = context.State;
            if (!state.windInitialized)
            {
                state.windDegrees = context.Weather.WindDirectionDegrees;
                state.windInitialized = true;
            }

            SolEnvironmentWindState wind = context.Environment.Wind;
            float cloudDegrees = ElementaPanelFormat.WindDegrees(wind.CloudDirection);

            EditorGUI.BeginChangeCheck();
            float direction = ElementaPanelGui.WindDial(state.windDegrees, cloudDegrees);
            direction = EditorGUILayout.Slider("Direction (X/Z)", direction, 0f, 359.9f);
            state.showSceneWind = EditorGUILayout.ToggleLeft(
                "Show Scene View arrows", state.showSceneWind);
            bool changed = EditorGUI.EndChangeCheck();
            state.windDegrees = Mathf.Repeat(direction, 360f);

            if (changed && state.previewWeather)
                context.ApplyWeatherPreview();

            if (!context.HasWeatherPreview)
                ElementaPanelGui.Note(
                    "Direction only reaches the scene while a preview is active: outside a "
                    + "preview the manager owns the heading and wanders it on its own.");

            ElementaPanelGui.Metric("Wind", ElementaPanelFormat.Wind(wind.Speed));
            ElementaPanelGui.Metric("Sea state", ElementaPanelFormat.SeaState(wind));
            ElementaPanelGui.Metric("Cloud response",
                $"{wind.CloudSpeed:0.0} m/s at {cloudDegrees:0}° (2 min lag)");
            ElementaPanelGui.Metric("Fog response",
                $"{wind.FogAdvectionSpeed:0.0} m/s (20 s lag)");
            ElementaPanelGui.MetricBar("Turbulence", wind.Turbulence);
        }

        // -- Manager settings --------------------------------------------------------

        void DrawManagerSettings(ElementaPanelContext context)
        {
            ElementaPanelGui.Rule();
            EditorGUILayout.LabelField(
                $"Selection policy  ({context.Weather.name})", EditorStyles.boldLabel);
            ElementaPanelGui.Note(
                "Scene-local: two scenes can share the same Storm presentation and choose it "
                + "at different frequencies, which is why weights live here and not on the "
                + "profile asset.");

            SerializedObject serialized = ResolveSerializedManager(context.Weather);
            if (ElementaPanelGui.DrawGroupedProperties(
                    context, "weather/manager", serialized, ref context.State.weatherFilter))
                context.Refresh();
        }

        // -- Inspected profile -------------------------------------------------------

        void DrawInspectedProfile(ElementaPanelContext context)
        {
            SolWeatherSelection[] profiles = context.Weather.profiles;
            int index = context.State.inspectedWeatherIndex;
            if (profiles == null || index < 0 || index >= profiles.Length)
                return;

            SolWeatherProfileAsset profile = profiles[index]?.profile;
            if (profile == null)
                return;

            ElementaPanelGui.Rule();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Editing  {profile.name}", EditorStyles.boldLabel);
                if (GUILayout.Button("Close", GUILayout.Width(60f)))
                {
                    context.State.inspectedWeatherIndex = -1;
                    context.RefreshLayout();
                }
            }

            SolCloudState resolved = profile.ResolveCloudState();
            ElementaPanelGui.Metric("Resolved formation", resolved.DominantFormation.ToString());
            ElementaPanelGui.Metric("Effective coverage",
                $"{SolWeatherAdjacency.EffectiveCoverage(profile):0.000} at the nominal "
                + $"{SolWeatherAdjacency.NominalAuthoredCoverage:0.000} deck");
            ElementaPanelGui.Metric("Fog density floor",
                $"{profile.ResolveFogDensityFloor():0.00000}");
            ElementaPanelGui.Note(
                "Cloudiness is an influence toward overcast, not an absolute cover, so two "
                + "profiles can author the same value and render differently. Effective "
                + "coverage above is the number to compare.");

            if (_serializedProfile == null || _serializedProfile.targetObject != profile)
                _serializedProfile = new SerializedObject(profile);

            string filter = context.State.weatherFilter;
            if (ElementaPanelGui.DrawGroupedProperties(
                    context, "weather/profile", _serializedProfile, ref filter))
            {
                EditorUtility.SetDirty(profile);
                if (context.HasWeatherPreview)
                    context.ApplyWeatherPreview();
                context.Refresh();
            }
        }

        // -- Shared ------------------------------------------------------------------

        SerializedObject ResolveSerializedManager(SolWeatherManager manager)
        {
            if (_serializedManager == null || _serializedManager.targetObject != manager)
                _serializedManager = new SerializedObject(manager);
            return _serializedManager;
        }

        static void EnsureDefaultProfiles(ElementaPanelContext context)
        {
            SolWeatherManager manager = context.Weather;
            if (manager.profiles == null || manager.profiles.Length == 0)
                return;

            int target = Mathf.Max(0, manager.IndexOfProfile(manager.TargetProfile));
            if (context.State.weatherA == null)
                context.State.weatherA = ProfileAt(manager, target);
            if (context.State.weatherB == null)
                context.State.weatherB = ProfileAt(manager,
                    Mathf.Min(target + 1, manager.profiles.Length - 1));
        }

        static SolWeatherProfileAsset ProfileAt(SolWeatherManager manager, int index)
        {
            if (manager.profiles == null || index < 0 || index >= manager.profiles.Length)
                return null;
            return manager.profiles[index]?.profile;
        }
    }
}
