using UnityEngine;
using UnityEditor;

[CustomEditor(typeof(SolWeatherManager))]
public class WeatherManagerEditor : Editor
{
    bool _instant = true;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var wm = (SolWeatherManager)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Manual Control (Play Mode)", EditorStyles.boldLabel);

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox(
                "Enter Play Mode to force weather states and see live status.",
                MessageType.Info);
            return;
        }

        // -- Live status --
        var targetProfile = wm.TargetProfile;
        string status = targetProfile != null ? targetProfile.name : "(none)";
        if (wm.IsTransitioning) status += "  (blending in...)";
        EditorGUILayout.HelpBox(
            $"Target: {status}\n" +
            $"Blend: {wm.TransitionProgress:P0}    Rain: {wm.CurrentRainIntensity:0.00}    Dim: {wm.CurrentDim:0.00}\n" +
            $"Wind: {wm.CurrentState.WindSpeedMetresPerSecond:0.0} m/s    Waves: {wm.CurrentState.WaveSpeedMultiplier:0.00}    Turbulence: {wm.CurrentState.WaterTurbulence:0.00}\n" +
            $"Fog: {wm.CurrentState.FogBoost:0.00}    Daily: {wm.CurrentDailyFog:0.00}    Mist: {wm.CurrentState.Mistiness:0.00}    Sky: {wm.CurrentState.SkyObscuration:0.00}",
            MessageType.None);

        // -- Instant toggle --
        _instant = EditorGUILayout.ToggleLeft(
            "Instant (skip blend when forcing below)", _instant);

        EditorGUILayout.Space();

        // -- One button per profile --
        if (wm.profiles != null)
        {
            foreach (var p in wm.profiles)
            {
                if (p?.profile == null) continue;
                if (GUILayout.Button($"Set: {p.profile.name}", GUILayout.Height(26)))
                    wm.SetWeather(p.profile.name, _instant);
            }
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Advance Weather (weighted random)", GUILayout.Height(22)))
            wm.NextWeather();

        // Keep the status readout live while playing.
        Repaint();
    }
}

[InitializeOnLoad]
sealed class SolTerrainShaderImportGuard : AssetPostprocessor
{
    const string MainShaderPath =
        "Assets/Earth-Sky-Water/Shaders/Terrain/Sol.TerrainLitWet.shader";
    const string AddPassShaderPath =
        "Assets/Earth-Sky-Water/Shaders/Terrain/Sol.TerrainLitWetAddPass.shader";

    static bool reimportQueued;

    static SolTerrainShaderImportGuard()
    {
        // ShaderLab dependencies are resolved by shader name. On a clean
        // checkout Unity can import the main terrain shader before its custom
        // add pass has entered the shader registry, so resolve it once more
        // after editor assemblies and initial assets are available.
        QueueMainShaderReimport();
    }

    static void OnPostprocessAllAssets(
        string[] importedAssets,
        string[] deletedAssets,
        string[] movedAssets,
        string[] movedFromAssetPaths)
    {
        if (System.Array.IndexOf(importedAssets, AddPassShaderPath) >= 0)
            QueueMainShaderReimport();
    }

    static void QueueMainShaderReimport()
    {
        if (reimportQueued)
            return;

        reimportQueued = true;
        EditorApplication.delayCall += () =>
        {
            reimportQueued = false;
            Shader addPass = AssetDatabase.LoadAssetAtPath<Shader>(AddPassShaderPath);
            Shader main = AssetDatabase.LoadAssetAtPath<Shader>(MainShaderPath);
            if (addPass != null && main != null)
                AssetDatabase.ImportAsset(MainShaderPath, ImportAssetOptions.ForceUpdate);
        };
    }
}
