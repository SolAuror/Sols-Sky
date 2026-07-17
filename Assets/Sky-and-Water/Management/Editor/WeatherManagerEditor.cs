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
        var target = wm.TargetProfile;
        string status = target != null ? target.name : "(none)";
        if (wm.IsTransitioning) status += "  (blending in...)";
        EditorGUILayout.HelpBox(
            $"Target: {status}\n" +
            $"Rain: {wm.CurrentRainIntensity:0.00}    Dim: {wm.CurrentDim:0.00}",
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
                if (p == null) continue;
                if (GUILayout.Button($"Set: {p.name}", GUILayout.Height(26)))
                    wm.SetWeather(p.name, _instant);
            }
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Advance Weather (weighted random)", GUILayout.Height(22)))
            wm.NextWeather();

        // Keep the status readout live while playing.
        Repaint();
    }
}
