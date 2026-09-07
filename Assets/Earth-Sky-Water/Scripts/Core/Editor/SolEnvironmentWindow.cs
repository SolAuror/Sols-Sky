using Sol.Lighting;
using Sol.ToD;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// Dockable, play-mode-free authoring surface for the coupled Sol environment.
    /// It issues the same time requests and uses the same weather snapshot as runtime;
    /// it owns no duplicate simulation state.
    /// </summary>
    public sealed class SolEnvironmentWindow : EditorWindow
    {
        const float SignificantWaveHeightFactor = 0.007f;

        [SerializeField] TimeOfDay _timeOfDay;
        [SerializeField] SolWeatherManager _weatherManager;
        [SerializeField] SolWeatherProfileAsset _weatherA;
        [SerializeField] SolWeatherProfileAsset _weatherB;
        [SerializeField, Range(0f, 1f)] float _weatherBlend;
        [SerializeField] bool _previewWeather = true;
        [SerializeField] bool _showSceneWind = true;
        [SerializeField] float _windDegrees;
        [SerializeField] bool _windInitialized;

        SolWeatherManager _previewOwner;
        double _nextWindowRepaint;

        [MenuItem("Tools/Sol Environment/Environment Window")]
        static void Open()
        {
            SolEnvironmentWindow window = GetWindow<SolEnvironmentWindow>();
            window.titleContent = new GUIContent("Sol Environment");
            window.minSize = new Vector2(340f, 520f);
            window.Show();
        }

        void OnEnable()
        {
            titleContent = new GUIContent("Sol Environment");
            SolEnvironmentEditorDriver.SetEnvironmentWindowOpen(true);
            SolEnvironmentEditorDriver.PreviewTick -= OnPreviewTick;
            SolEnvironmentEditorDriver.PreviewTick += OnPreviewTick;
            SceneView.duringSceneGui -= DuringSceneGui;
            SceneView.duringSceneGui += DuringSceneGui;
            ResolveAuthorities();
        }

        void OnDisable()
        {
            SolEnvironmentEditorDriver.PreviewTick -= OnPreviewTick;
            SceneView.duringSceneGui -= DuringSceneGui;
            ClearWeatherPreview();
            SolEnvironmentEditorDriver.SetEnvironmentWindowOpen(false);
        }

        void OnHierarchyChange()
        {
            if (_timeOfDay == null || _weatherManager == null)
                ResolveAuthorities();
            Repaint();
        }

        void OnPreviewTick(float deltaSeconds)
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextWindowRepaint)
                return;

            _nextWindowRepaint = now + 0.1d;
            Repaint();
        }

        void ResolveAuthorities()
        {
            _timeOfDay ??= TimeOfDay.ResolveInstance();
            _weatherManager ??= SolWeatherManager.Instance != null
                ? SolWeatherManager.Instance
                : FindFirstObjectByType<SolWeatherManager>();
            EnsureDefaultProfiles();

            if (!_windInitialized && _weatherManager != null)
            {
                _windDegrees = _weatherManager.WindDirectionDegrees;
                _windInitialized = true;
            }
        }

        void OnGUI()
        {
            ResolveAuthorities();

            EditorGUILayout.Space(4f);
            DrawAuthorityFields();
            EditorGUILayout.Space(6f);
            DrawTime();
            EditorGUILayout.Space(8f);
            DrawWeather();
            EditorGUILayout.Space(8f);
            DrawWindAndSea();
            EditorGUILayout.Space(8f);
            DrawBudget();
        }

        void DrawAuthorityFields()
        {
            EditorGUILayout.LabelField("Scene Authorities", EditorStyles.boldLabel);
            TimeOfDay previousTime = _timeOfDay;
            SolWeatherManager previousWeather = _weatherManager;
            _timeOfDay = (TimeOfDay)EditorGUILayout.ObjectField(
                "Time of Day", _timeOfDay, typeof(TimeOfDay), true);
            _weatherManager = (SolWeatherManager)EditorGUILayout.ObjectField(
                "Weather", _weatherManager, typeof(SolWeatherManager), true);

            if (previousWeather != _weatherManager)
            {
                if (_previewOwner != null)
                    _previewOwner.ClearPreview();
                _previewOwner = null;
                _weatherA = null;
                _weatherB = null;
                _windInitialized = false;
                EnsureDefaultProfiles();
            }

            if (previousTime != _timeOfDay)
                SceneView.RepaintAll();

            if (_timeOfDay == null || _weatherManager == null)
                EditorGUILayout.HelpBox(
                    "Open a scene with TimeOfDay and SolWeatherManager, or assign them above.",
                    MessageType.Warning);
        }

        void DrawTime()
        {
            EditorGUILayout.LabelField("Time & Sky", EditorStyles.boldLabel);
            if (_timeOfDay == null)
                return;

            float hour = _timeOfDay.ClockHour;
            EditorGUI.BeginChangeCheck();
            float changedHour = EditorGUILayout.Slider(
                $"Clock  {FormatClock(hour)}", hour, 0f, 23.999f);
            if (EditorGUI.EndChangeCheck())
            {
                _timeOfDay.ApplyTimeChange(TimeChangeRequest.SetClockHour(
                    changedHour, this, "Environment Window Scrub"));
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
            }

            EditorGUILayout.LabelField(
                "Sun / moon / fog / cloud lighting use the normal TimeSkipped request path.",
                EditorStyles.miniLabel);
        }

        void DrawWeather()
        {
            EditorGUILayout.LabelField("Weather A/B", EditorStyles.boldLabel);
            if (_weatherManager == null)
                return;

            EnsureDefaultProfiles();
            EditorGUI.BeginChangeCheck();
            _previewWeather = EditorGUILayout.ToggleLeft("Preview in Edit Mode", _previewWeather);
            _weatherA = (SolWeatherProfileAsset)EditorGUILayout.ObjectField(
                "Preset A", _weatherA, typeof(SolWeatherProfileAsset), false);
            _weatherB = (SolWeatherProfileAsset)EditorGUILayout.ObjectField(
                "Preset B", _weatherB, typeof(SolWeatherProfileAsset), false);
            _weatherBlend = EditorGUILayout.Slider("Blend", _weatherBlend, 0f, 1f);
            bool changed = EditorGUI.EndChangeCheck();

            int indexA = _weatherManager.IndexOfProfile(_weatherA);
            int indexB = _weatherManager.IndexOfProfile(_weatherB);
            bool valid = indexA >= 0 && indexB >= 0;
            if (!valid)
            {
                EditorGUILayout.HelpBox(
                    "Both preset assets must be present in this manager's Profiles list.",
                    MessageType.Warning);
            }

            if (!_previewWeather)
            {
                ClearWeatherPreview();
            }
            else if (valid && (changed || !_weatherManager.HasPreview
                || _previewOwner != _weatherManager))
            {
                ApplyWeatherPreview(indexA, indexB);
            }

            using (new EditorGUI.DisabledScope(_previewOwner == null))
            {
                if (GUILayout.Button("Clear Preview"))
                {
                    _previewWeather = false;
                    ClearWeatherPreview();
                }
            }

            SolWeatherState current = _weatherManager.CurrentState;
            EditorGUILayout.LabelField(
                $"Rain {current.RainIntensity:0.00}    Clouds {current.Cloudiness:0.00}    "
                + $"Fog {current.FogBoost:0.00}    Turbulence {current.WaterTurbulence:0.00}",
                EditorStyles.miniLabel);
        }

        void DrawWindAndSea()
        {
            EditorGUILayout.LabelField("Wind, Clouds & Waves", EditorStyles.boldLabel);
            if (_weatherManager == null)
                return;

            EditorGUI.BeginChangeCheck();
            float direction = DrawWindDial(_windDegrees);
            direction = EditorGUILayout.Slider("Direction (X/Z)", direction, 0f, 359.9f);
            _showSceneWind = EditorGUILayout.ToggleLeft("Show Scene View arrows", _showSceneWind);
            bool changed = EditorGUI.EndChangeCheck();
            _windDegrees = Mathf.Repeat(direction, 360f);

            if (changed && _previewWeather)
            {
                int indexA = _weatherManager.IndexOfProfile(_weatherA);
                int indexB = _weatherManager.IndexOfProfile(_weatherB);
                if (indexA >= 0 && indexB >= 0)
                    ApplyWeatherPreview(indexA, indexB);
            }

            SolEnvironmentWindState wind = SolEnvironmentWorld.ResolveState().Wind;
            EditorGUILayout.LabelField(
                $"{wind.Speed:0.0} m/s  ·  Beaufort {BeaufortNumber(wind.Speed)} "
                + $"({BeaufortName(wind.Speed)})");
            EditorGUILayout.LabelField(BuildSeaStateLabel(wind));
            EditorGUILayout.LabelField(
                $"Cloud response: {wind.CloudSpeed:0.0} m/s    Fog response: "
                + $"{wind.FogAdvectionSpeed:0.0} m/s",
                EditorStyles.miniLabel);
        }

        void DrawBudget()
        {
            EditorGUILayout.LabelField("Last Completed Frame Budget", EditorStyles.boldLabel);
            SolEnvironmentBudget.Counters counters = SolEnvironmentBudget.Previous;
            using (new EditorGUI.IndentLevelScope())
            {
                DrawMetric("FFT dispatches", counters.FftDispatches.ToString());
                DrawMetric("Full-res colour copies", counters.FullResColorCopies.ToString());
                DrawMetric("Environment updates", counters.EnvironmentUpdates.ToString());
                DrawMetric("Atmosphere pushes", counters.AtmosphereGlobalPushes.ToString());
                DrawMetric("Lighting tier", counters.HasLightingDirector
                    ? ((SolLightingQualityTier)counters.ActiveLightingTier).ToString()
                    : "—");
                DrawMetric("Lights registered / active",
                    $"{counters.RegisteredLights} / {counters.ActiveLights}");
                DrawMetric("Shadow slices", counters.ShadowSlices.ToString());
                DrawMetric("Volumetric lights", counters.VolumetricLights.ToString());
                DrawMetric("GI requests", counters.GiRequests.ToString());
                DrawMetric("Probe requests / done",
                    $"{counters.ProbeRequests} / {counters.ProbeCompletions}");
                DrawMetric("Camera contexts", counters.CameraContexts.ToString());
                DrawMetric("SSR history", $"{counters.SsrHistoryBytes / (1024f * 1024f):0.0} MB");
            }
        }

        void ApplyWeatherPreview(int indexA, int indexB)
        {
            if (_previewOwner != null && _previewOwner != _weatherManager)
                _previewOwner.ClearPreview();

            _previewOwner = _weatherManager;
            _weatherManager.SetPreview(indexA, indexB, _weatherBlend);
            _weatherManager.SetPreviewWindDirection(_windDegrees);
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }

        void ClearWeatherPreview()
        {
            if (_previewOwner != null)
                _previewOwner.ClearPreview();
            _previewOwner = null;
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }

        void EnsureDefaultProfiles()
        {
            if (_weatherManager == null || _weatherManager.profiles == null
                || _weatherManager.profiles.Length == 0)
                return;

            int target = Mathf.Max(0, _weatherManager.IndexOfProfile(
                _weatherManager.TargetProfile));
            _weatherA ??= ProfileAt(target);
            _weatherB ??= ProfileAt(Mathf.Min(target + 1,
                _weatherManager.profiles.Length - 1));
        }

        SolWeatherProfileAsset ProfileAt(int index)
        {
            if (_weatherManager == null || _weatherManager.profiles == null
                || index < 0 || index >= _weatherManager.profiles.Length)
                return null;
            return _weatherManager.profiles[index]?.profile;
        }

        void DuringSceneGui(SceneView sceneView)
        {
            if (!_showSceneWind || _weatherManager == null || sceneView == null)
                return;

            SolEnvironmentWindState wind = SolEnvironmentWorld.ResolveState().Wind;
            Vector3 origin = sceneView.pivot;
            float size = HandleUtility.GetHandleSize(origin) * 1.4f;
            CompareFunction oldZTest = Handles.zTest;
            Handles.zTest = CompareFunction.Always;
            try
            {
                DrawSceneArrow(origin, wind.Direction, size,
                    new Color(0.2f, 0.85f, 1f, 0.95f),
                    $"Wind {wind.Speed:0.0} m/s");
                DrawSceneArrow(origin + Vector3.up * size * 0.22f,
                    wind.CloudDirection, size * 0.82f,
                    new Color(0.75f, 0.55f, 1f, 0.95f),
                    $"Cloud {wind.CloudSpeed:0.0} m/s");
            }
            finally
            {
                Handles.zTest = oldZTest;
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
                Quaternion.LookRotation(horizontal, Vector3.up), size,
                EventType.Repaint);
            Handles.Label(origin + horizontal * size, label);
        }

        static float DrawWindDial(float degrees)
        {
            Rect rect = GUILayoutUtility.GetRect(80f, 92f, GUILayout.ExpandWidth(true));
            Vector2 center = new(rect.x + rect.width * 0.5f, rect.y + 44f);
            float radius = 34f;
            int controlId = GUIUtility.GetControlID("SolWindDial".GetHashCode(), FocusType.Passive);
            Event current = Event.current;

            switch (current.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    if (current.button == 0
                        && Vector2.Distance(current.mousePosition, center) <= radius + 8f)
                    {
                        GUIUtility.hotControl = controlId;
                        degrees = DirectionFromPointer(center, current.mousePosition);
                        current.Use();
                        GUI.changed = true;
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == controlId)
                    {
                        degrees = DirectionFromPointer(center, current.mousePosition);
                        current.Use();
                        GUI.changed = true;
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        current.Use();
                    }
                    break;
                case EventType.Repaint:
                    Handles.BeginGUI();
                    Color old = Handles.color;
                    Handles.color = new Color(0.55f, 0.6f, 0.68f, 1f);
                    Handles.DrawWireDisc(center, Vector3.forward, radius);
                    Vector2 arrow = new(
                        Mathf.Cos(degrees * Mathf.Deg2Rad),
                        -Mathf.Sin(degrees * Mathf.Deg2Rad));
                    Handles.color = new Color(0.2f, 0.85f, 1f, 1f);
                    Handles.DrawAAPolyLine(3f, center, center + arrow * radius);
                    Handles.color = old;
                    Handles.EndGUI();
                    GUI.Label(new Rect(center.x - 8f, rect.y + 2f, 16f, 18f), "Z+");
                    GUI.Label(new Rect(center.x + radius + 4f, center.y - 9f, 22f, 18f), "X+");
                    break;
            }

            return Mathf.Repeat(degrees, 360f);
        }

        static float DirectionFromPointer(Vector2 center, Vector2 pointer)
        {
            Vector2 delta = pointer - center;
            return Mathf.Repeat(Mathf.Atan2(-delta.y, delta.x) * Mathf.Rad2Deg, 360f);
        }

        static string BuildSeaStateLabel(in SolEnvironmentWindState wind)
        {
            float targetSpeed = Mathf.Max(0f, wind.Speed);
            float seaSpeed = Mathf.Max(0f, wind.SeaStateSpeed);
            float targetHeight = SignificantWaveHeight(targetSpeed);
            float currentHeight = SignificantWaveHeight(seaSpeed);
            if (targetSpeed < 0.05f && seaSpeed < 0.05f)
                return "Sea state: calm · Hs 0.0 m";

            bool building = seaSpeed <= targetSpeed;
            float progress = building
                ? targetSpeed > 0.001f ? seaSpeed / targetSpeed : 1f
                : seaSpeed > 0.001f ? targetSpeed / seaSpeed : 1f;
            string phase = building ? "building" : "settling";
            string relation = building ? "of" : "toward";
            return $"Sea state: {phase} {Mathf.Clamp01(progress):P0}  →  "
                 + $"Hs {currentHeight:0.0} m {relation} {targetHeight:0.0} m";
        }

        static float SignificantWaveHeight(float windSpeedMetresPerSecond)
            => SignificantWaveHeightFactor * windSpeedMetresPerSecond
             * windSpeedMetresPerSecond;

        static int BeaufortNumber(float speed)
        {
            float[] thresholds =
            {
                0.5f, 1.6f, 3.4f, 5.5f, 8f, 10.8f, 13.9f,
                17.2f, 20.8f, 24.5f, 28.5f, 32.7f
            };
            for (int i = 0; i < thresholds.Length; i++)
                if (speed < thresholds[i])
                    return i;
            return 12;
        }

        static string BeaufortName(float speed)
        {
            string[] names =
            {
                "calm", "light air", "light breeze", "gentle breeze",
                "moderate breeze", "fresh breeze", "strong breeze",
                "near gale", "gale", "strong gale", "storm",
                "violent storm", "hurricane force"
            };
            return names[BeaufortNumber(speed)];
        }

        static string FormatClock(float hour)
        {
            int totalMinutes = Mathf.RoundToInt(Mathf.Repeat(hour, 24f) * 60f) % (24 * 60);
            return $"{totalMinutes / 60:00}:{totalMinutes % 60:00}";
        }

        static void DrawMetric(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label);
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField(value, GUILayout.Width(80f));
            }
        }
    }
}
