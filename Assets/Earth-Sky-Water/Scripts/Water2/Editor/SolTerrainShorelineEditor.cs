using System.IO;
using UnityEditor;
using UnityEngine;

namespace Sol.Water.Editor
{
    /// <summary>
    /// Inspector for the live shoreline field, plus the way back to a baked texture.
    ///
    /// The bake button is not how the field is meant to be used -- the whole point of
    /// generating it is that it tracks the terrain -- but the profile still carries a
    /// fallback slot for scenes with no terrain to measure against, and until now nothing
    /// in the project could fill it.
    /// </summary>
    [CustomEditor(typeof(SolTerrainShoreline))]
    internal sealed class SolTerrainShorelineEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var shoreline = (SolTerrainShoreline)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Live Field", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                shoreline.IsBuilding ? "Building..." : shoreline.LastStatus,
                shoreline.IsBuilding ? MessageType.Info : MessageType.None);

            SolShorelineField field = SolTerrainShoreline.OceanField;
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("Rebuild Now"))
                {
                    if (!shoreline.RebuildImmediate())
                        Debug.LogWarning($"[SolTerrainShoreline] {shoreline.LastStatus}", shoreline);
                    SceneView.RepaintAll();
                }
            }

            using (new EditorGUI.DisabledScope(field == null))
            {
                if (GUILayout.Button("Save Field As Texture Asset"))
                    SaveField(shoreline, field);
            }

            if (field != null)
            {
                EditorGUILayout.LabelField("Mapping",
                    $"centre ({field.Mapping.x:0.#}, {field.Mapping.y:0.#})  "
                    + $"size {field.Mapping.z:0.#} x {field.Mapping.w:0.#} m");
                EditorGUILayout.LabelField("Ranges",
                    $"depth ±{field.DepthRange:0.##} m, distance ±{field.DistanceRange:0.##} m");
            }
        }

        /// <summary>
        /// Writes the live field out as an asset and points the ocean profile's fallback
        /// slot at it, mapping and ranges included. Those four values only mean anything
        /// together, so setting the texture alone would produce a shoreline in the wrong
        /// place at the wrong scale.
        /// </summary>
        static void SaveField(SolTerrainShoreline shoreline, SolShorelineField field)
        {
            Texture2D source = field.Texture;
            if (source == null)
                return;

            string path = EditorUtility.SaveFilePanelInProject(
                "Save Shoreline Data",
                "Shoreline Data",
                "asset",
                "Choose where to write the baked shoreline texture.");
            if (string.IsNullOrEmpty(path))
                return;

            var baked = new Texture2D(source.width, source.height, source.format, false, true)
            {
                name = Path.GetFileNameWithoutExtension(path),
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0,
            };
            baked.SetPixelData(source.GetRawTextureData<byte>(), 0);
            baked.Apply(false, false);
            AssetDatabase.CreateAsset(baked, path);
            AssetDatabase.SaveAssets();

            SolWaterWorld world = SolWaterWorld.Active;
            SolWaterProfile profile = world != null && world.TryGetOcean(out SolWaterBody ocean)
                ? ocean.Profile
                : null;
            if (profile != null)
            {
                Undo.RecordObject(profile, "Assign Baked Shoreline Data");
                profile.shorelineData = baked;
                profile.shorelineDataMapping = field.Mapping;
                profile.shorelineDepthRange = field.DepthRange;
                profile.shorelineDistanceRange = field.DistanceRange;
                EditorUtility.SetDirty(profile);
            }
            else
            {
                Debug.LogWarning(
                    "[SolTerrainShoreline] Saved the field, but found no ocean profile to "
                    + "assign it to. Set shorelineData, shorelineDataMapping and both "
                    + "ranges by hand.", shoreline);
            }

            EditorGUIUtility.PingObject(baked);
        }
    }

    /// <summary>
    /// Drives the shoreline build to completion while the editor is not playing.
    ///
    /// The build spans frames on purpose, and in edit mode "next frame" only happens when
    /// something pumps the player loop. Without this a rebuild triggered by a sculpt or a
    /// water-level change would schedule its jobs and then sit unfinished until the user
    /// happened to move something -- the same gap SolLandscapeRealtimeEditor exists to
    /// close for the landscape contract.
    /// </summary>
    [InitializeOnLoad]
    internal static class SolTerrainShorelineRealtimeEditor
    {
        static bool _wasBuilding;

        static SolTerrainShorelineRealtimeEditor()
        {
            EditorApplication.update -= Pump;
            EditorApplication.update += Pump;
        }

        static void Pump()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                return;

            SolTerrainShoreline active = SolTerrainShoreline.Active;
            bool building = active != null && active.IsBuilding;
            if (building)
            {
                EditorApplication.QueuePlayerLoopUpdate();
            }
            else if (_wasBuilding)
            {
                // One repaint once the field lands, so the new waterline is visible
                // without the user having to nudge the view.
                SceneView.RepaintAll();
            }
            _wasBuilding = building;
        }
    }
}
