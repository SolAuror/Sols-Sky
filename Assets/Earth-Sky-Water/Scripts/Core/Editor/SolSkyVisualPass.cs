using System;
using System.Collections.Generic;
using System.IO;
using Sol.Lighting;
using Sol.ToD;
using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>Maintained deterministic 4 × 9 × 4 × 3 visual validation runner.</summary>
    public sealed class SolSkyVisualPass : EditorWindow
    {
        const int ConvergenceFrames = 64;
        const int CaptureCount = 4 * 9 * 4 * 3;
        static readonly float[] SolarHours = { 6f, 12f, 18f, 0f };
        static readonly string[] SolarNames = { "Dawn", "Noon", "Sunset", "NearFullMoonNight" };
        static readonly string[] ViewNames = { "CoastGround", "WaterSurface", "Underwater", "CloudTopAerial" };
        static readonly string[] QualityNames = { "Low", "Medium", "High" };

        [SerializeField] SolSkyProfile _profile;
        [SerializeField] string _versionLabel = "New";
        [SerializeField] Camera _camera;
        [SerializeField] Transform[] _views = new Transform[4];
        [SerializeField] int _captureIndex;
        [SerializeField] int _convergedFrames;
        [SerializeField] bool _running;

        TimeOfDay _timeOfDay;
        SolWeatherManager _weather;
        SolSkyProfile _restoreProfile;
        SolWeatherSnapshot _restoreWeather;
        float _restoreHour;
        Vector3 _restorePosition;
        Quaternion _restoreRotation;
        SolLightingQualityTier _restoreTier;
        string _outputFolder;
        readonly List<CaptureEntry> _entries = new(CaptureCount);

        [MenuItem("Tools/Elementa/Visual Validation/Capture Matrix")]
        static void Open()
        {
            SolSkyVisualPass window = GetWindow<SolSkyVisualPass>();
            window.titleContent = new GUIContent("Sol Sky Visual Pass");
            window.minSize = new Vector2(440f, 360f);
            window.Show();
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Deterministic Sky Matrix", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Runs one profile/version: 4 solar states × 9 weather profiles × 4 views × 3 quality tiers = 432 captures. Raw output stays beside the Unity repository after a fixed 64-frame convergence.", MessageType.Info);
            _profile = (SolSkyProfile)EditorGUILayout.ObjectField("Sky Profile", _profile,
                typeof(SolSkyProfile), false);
            _versionLabel = EditorGUILayout.TextField("Version Label", _versionLabel);
            _camera = (Camera)EditorGUILayout.ObjectField("Capture Camera", _camera,
                typeof(Camera), true);
            SerializedObject serialized = new(this);
            serialized.Update();
            EditorGUILayout.PropertyField(serialized.FindProperty("_views"), true);
            serialized.ApplyModifiedProperties();

            EditorGUILayout.Space(8f);
            float progress = CaptureCount > 0 ? _captureIndex / (float)CaptureCount : 0f;
            Rect bar = EditorGUILayout.GetControlRect(false, 20f);
            EditorGUI.ProgressBar(bar, progress,
                _running ? $"{_captureIndex}/{CaptureCount} · converge {_convergedFrames}/{ConvergenceFrames}" : "Idle");

            using (new EditorGUI.DisabledScope(_running || !Application.isPlaying || _profile == null))
                if (GUILayout.Button("Start 432-Capture Pass")) StartPass();
            using (new EditorGUI.DisabledScope(!_running))
                if (GUILayout.Button("Stop and Restore")) StopPass(false);
            if (!Application.isPlaying)
                EditorGUILayout.HelpBox("Enter Play Mode before starting so temporal systems can converge normally.", MessageType.Warning);
        }

        void StartPass()
        {
            _timeOfDay = TimeOfDay.ResolveInstance();
            _weather = SolWeatherManager.Instance;
            _camera ??= Camera.main;
            if (_timeOfDay == null || _weather == null || _camera == null
                || _weather.profiles == null || _weather.profiles.Length < 9)
            {
                Debug.LogError("[SolSkyVisualPass] A TimeOfDay, camera, and WeatherManager with all nine profiles are required.");
                return;
            }

            _restoreProfile = _timeOfDay.SkyProfile;
            _restoreWeather = _weather.CaptureSnapshot();
            _restoreHour = _timeOfDay.ClockHour;
            _restorePosition = _camera.transform.position;
            _restoreRotation = _camera.transform.rotation;
            _restoreTier = SolLightingDirector.Active != null
                ? SolLightingDirector.Active.ActiveTier : SolLightingQualityTier.Medium;
            _outputFolder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..",
                "SolSkyVisualPass_Raw", Safe(_profile.name), Safe(_versionLabel)));
            Directory.CreateDirectory(_outputFolder);
            _entries.Clear();
            _captureIndex = 0;
            _convergedFrames = 0;
            _running = true;
            _timeOfDay.SetSkyProfile(_profile);
            ApplyState(0);
            EditorApplication.update += Tick;
        }

        void Tick()
        {
            if (!_running || !Application.isPlaying)
            {
                StopPass(false);
                return;
            }
            if (++_convergedFrames < ConvergenceFrames)
                return;

            Decode(_captureIndex, out int solar, out int weather, out int view, out int quality);
            string fileName = $"{_captureIndex:000}_{SolarNames[solar]}_{WeatherName(weather)}_{ViewNames[view]}_{QualityNames[quality]}.png";
            string absolutePath = Path.Combine(_outputFolder, fileName);
            ScreenCapture.CaptureScreenshot(absolutePath, 1);
            _entries.Add(new CaptureEntry
            {
                index = _captureIndex,
                solarState = SolarNames[solar],
                solarHour = SolarHours[solar],
                weather = WeatherName(weather),
                view = ViewNames[view],
                quality = QualityNames[quality],
                convergenceFrames = ConvergenceFrames,
                file = fileName,
            });

            _captureIndex++;
            _convergedFrames = 0;
            if (_captureIndex >= CaptureCount)
            {
                WriteManifest();
                StopPass(true);
                return;
            }
            ApplyState(_captureIndex);
            Repaint();
        }

        void ApplyState(int index)
        {
            Decode(index, out int solar, out int weather, out int view, out int quality);
            _timeOfDay.ApplyTimeChange(TimeChangeRequest.SetClockHour(
                SolarHours[solar], this, "SolSkyVisualPass"));
            _weather.SetWeather(weather, true);
            SolLightingDirector.Active?.SetTier((SolLightingQualityTier)quality);
            ApplyView(view);
        }

        void ApplyView(int view)
        {
            if (_views != null && view < _views.Length && _views[view] != null)
            {
                _camera.transform.SetPositionAndRotation(_views[view].position, _views[view].rotation);
                return;
            }

            float waterLevel = Shader.GetGlobalFloat("_Sol_GlobalWaterLevel");
            Vector3 position = _restorePosition;
            position.y = view switch
            {
                1 => waterLevel + 1.2f,
                2 => waterLevel - 3f,
                3 => 6500f,
                _ => Mathf.Max(waterLevel + 8f, _restorePosition.y),
            };
            _camera.transform.SetPositionAndRotation(position, _restoreRotation);
        }

        void StopPass(bool completed)
        {
            EditorApplication.update -= Tick;
            if (_timeOfDay != null)
            {
                _timeOfDay.SetSkyProfile(_restoreProfile);
                _timeOfDay.ApplyTimeChange(TimeChangeRequest.SetClockHour(
                    _restoreHour, this, "SolSkyVisualPass restore"));
            }
            _weather?.RestoreSnapshot(_restoreWeather);
            SolLightingDirector.Active?.SetTier(_restoreTier);
            if (_camera != null)
                _camera.transform.SetPositionAndRotation(_restorePosition, _restoreRotation);
            _running = false;
            if (completed)
                Debug.Log($"[SolSkyVisualPass] Completed {CaptureCount} captures at {_outputFolder}");
            Repaint();
        }

        void WriteManifest()
        {
            CaptureManifest manifest = new()
            {
                profile = _profile.name,
                version = _versionLabel,
                expectedCaptureCount = CaptureCount,
                convergenceFrames = ConvergenceFrames,
                resolution = $"{Screen.width}x{Screen.height}",
                entries = _entries.ToArray(),
            };
            File.WriteAllText(Path.Combine(_outputFolder, "manifest.json"),
                JsonUtility.ToJson(manifest, true));
        }

        string WeatherName(int index)
        {
            SolWeatherProfileAsset profile = _weather.profiles[index]?.profile;
            return profile != null ? profile.name : $"Weather{index}";
        }

        static void Decode(int index, out int solar, out int weather, out int view, out int quality)
        {
            quality = index % 3;
            index /= 3;
            view = index % 4;
            index /= 4;
            weather = index % 9;
            solar = index / 9;
        }

        static string Safe(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');
            return value;
        }

        [Serializable] sealed class CaptureManifest
        {
            public string profile;
            public string version;
            public int expectedCaptureCount;
            public int convergenceFrames;
            public string resolution;
            public CaptureEntry[] entries;
        }

        [Serializable] sealed class CaptureEntry
        {
            public int index;
            public string solarState;
            public float solarHour;
            public string weather;
            public string view;
            public string quality;
            public int convergenceFrames;
            public string file;
        }
    }
}
