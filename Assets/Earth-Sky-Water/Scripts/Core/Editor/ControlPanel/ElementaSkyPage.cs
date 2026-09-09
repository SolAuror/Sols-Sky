using System.IO;
using Sol.ToD;
using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// The unified sky profile: the one asset that carries authored appearance for the sky,
    /// the atmosphere, the celestial bodies, the stars, the aurora and the cloud baseline.
    ///
    /// Editing it here rather than in the inspector is not just convenience. A sky change has
    /// to be republished to TimeOfDay before the scene view shows it, and the sixteen header
    /// groups are only navigable collapsed - which is what makes this the page an author
    /// actually dials a look in from.
    /// </summary>
    sealed class ElementaSkyPage : ElementaPanelPage
    {
        SerializedObject _serializedProfile;

        public override string Title => "Sky";

        public override string Subtitle =>
            "Authored sky, atmosphere, sun, moon, stars, aurora and the cloud baseline. "
            + "Changes republish to the live sky frame as you make them.";

        public override void Draw(ElementaPanelContext context)
        {
            if (context.Time == null)
            {
                EditorGUILayout.HelpBox(
                    "No TimeOfDay authority. Assign one on the Overview page.",
                    MessageType.Warning);
                return;
            }

            DrawProfileSelection(context);
            DrawAtmosphereQuality(context);
            DrawNightSky(context);
            DrawProfileBody(context);
        }

        // -- Selection ---------------------------------------------------------------

        void DrawProfileSelection(ElementaPanelContext context)
        {
            if (!context.Section("sky/profile", "Profile"))
                return;

            TimeOfDay time = context.Time;
            SolSkyProfile current = time.SkyProfile;

            SolSkyProfile selected = (SolSkyProfile)EditorGUILayout.ObjectField(
                "Sky profile", current, typeof(SolSkyProfile), false);
            if (selected != current)
            {
                Undo.RecordObject(time, "Switch Elementa sky profile");
                time.SetSkyProfile(selected);
                EditorUtility.SetDirty(time);
                _serializedProfile = null;
                context.RefreshLayout();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (ElementaPanelGui.ActionButton("Duplicate to Edit",
                        "Copy this profile beside itself and assign the copy, so a shipped "
                        + "preset is never edited in place.", current != null))
                    Duplicate(context, current);

                if (ElementaPanelGui.ActionButton("New Profile",
                        "Create an empty sky profile at default values and assign it.", true))
                    CreateNew(context);

                if (ElementaPanelGui.ActionButton("Migrate Inline Values",
                        "Build a profile from this scene's hidden inline TimeOfDay and "
                        + "atmosphere values.", current == null))
                    Migrate(context);
            }

            if (current == null)
            {
                EditorGUILayout.HelpBox(
                    "No profile is assigned. Runtime systems are on the original inline "
                    + "TimeOfDay and SolAtmosphereProfile compatibility path, which no asset "
                    + "can author and no other scene can share.",
                    MessageType.Warning);
            }
        }

        void Duplicate(ElementaPanelContext context, SolSkyProfile source)
        {
            string sourcePath = AssetDatabase.GetAssetPath(source);
            string folder = string.IsNullOrEmpty(sourcePath)
                ? "Assets"
                : Path.GetDirectoryName(sourcePath)?.Replace('\\', '/');
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{source.name}_Custom.asset");

            SolSkyProfile duplicate = Object.Instantiate(source);
            duplicate.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(duplicate, path);
            AssetDatabase.SaveAssets();
            Assign(context, duplicate, "Assign duplicated Elementa sky profile");
        }

        void CreateNew(ElementaPanelContext context)
        {
            string path = EditorUtility.SaveFilePanelInProject("Create Sky Profile",
                "Sol Sky Profile", "asset", "Choose where the new sky profile is saved.");
            if (string.IsNullOrEmpty(path))
                return;

            SolSkyProfile created = ScriptableObject.CreateInstance<SolSkyProfile>();
            AssetDatabase.CreateAsset(created, path);
            AssetDatabase.SaveAssets();
            Assign(context, created, "Assign new Elementa sky profile");
        }

        void Migrate(ElementaPanelContext context)
        {
            string path = EditorUtility.SaveFilePanelInProject("Create Sky Profile",
                $"{context.Time.gameObject.scene.name}_Sky", "asset",
                "Choose where the migrated profile should be saved.");
            if (string.IsNullOrEmpty(path))
                return;

            SolSkyProfileMigration.Result result =
                SolSkyProfileMigration.CreateOrReuse(context.Time, path);
            Selection.activeObject = result.Profile;
            _serializedProfile = null;
            context.RefreshLayout();
        }

        void Assign(ElementaPanelContext context, SolSkyProfile profile, string undoName)
        {
            Undo.RecordObject(context.Time, undoName);
            context.Time.SetSkyProfile(profile);
            EditorUtility.SetDirty(context.Time);
            Selection.activeObject = profile;
            _serializedProfile = null;
            context.RefreshLayout();
        }

        // -- Atmosphere quality ------------------------------------------------------

        void DrawAtmosphereQuality(ElementaPanelContext context)
        {
            if (!context.Section("sky/atmosphere", "Atmosphere Quality", false))
                return;

            SolAtmosphereController atmosphere = context.Atmosphere;
            if (atmosphere == null)
            {
                EditorGUILayout.HelpBox(
                    "No SolAtmosphereController is loaded, so the profile's atmosphere section "
                    + "is authored but not rendered.",
                    MessageType.Info);
                return;
            }

            // Same reasoning as the cloud tier: the override is private, and the only thing
            // that can make the resolved quality disagree with the profile is one being held.
            SolSkyProfile authored = context.Time.SkyProfile;
            bool differs = authored != null && atmosphere.Quality != authored.atmosphereQuality;
            ElementaPanelGui.Metric("Active quality", differs
                ? $"{atmosphere.Quality}  (not the authored value)"
                : atmosphere.Quality.ToString());
            if (differs)
                EditorGUILayout.HelpBox(
                    $"The atmosphere is running at {atmosphere.Quality} while "
                    + $"{authored.name} authors {authored.atmosphereQuality}. Clear returns it "
                    + "to the profile.",
                    MessageType.Info);
            ElementaPanelGui.Metric("Volumetric lighting",
                atmosphere.UsesVolumetricLighting ? "yes" : "no (Low tier)");
            ElementaPanelGui.Metric("Current density", $"{atmosphere.CurrentDensity:0.00000}");
            ElementaPanelGui.Metric("Directional scattering",
                $"{atmosphere.CurrentDirectionalScattering:0.000}");
            ElementaPanelGui.ObjectRow("Dominant light", atmosphere.CurrentDominantLight);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Override", GUILayout.Width(60f));
                if (GUILayout.Button("Low"))
                    SetQuality(context, SolAtmosphereQuality.Low);
                if (GUILayout.Button("Medium"))
                    SetQuality(context, SolAtmosphereQuality.Medium);
                if (GUILayout.Button("High"))
                    SetQuality(context, SolAtmosphereQuality.High);
                if (GUILayout.Button("Clear"))
                {
                    atmosphere.ClearQualityOverride();
                    context.Refresh();
                }
            }

            ElementaPanelGui.Note(
                "The override is runtime-only and never written to the profile, so comparing "
                + "tiers cannot accidentally re-author the asset.");
        }

        static void SetQuality(ElementaPanelContext context, SolAtmosphereQuality quality)
        {
            context.Atmosphere.SetQuality(quality);
            context.Refresh();
        }

        // -- Night sky ---------------------------------------------------------------

        void DrawNightSky(ElementaPanelContext context)
        {
            SolSkyProfile profile = context.Time.SkyProfile;
            if (profile == null)
                return;

            if (!context.Section("sky/night", "Stellar Backdrop", false))
                return;

            ElementaPanelGui.ObjectRow("Backdrop cubemap", profile.stellarBackdrop,
                "Without a cubemap the night sky falls back to the procedural star grid, "
                + "which has no Milky Way band.");

            using (new EditorGUILayout.HorizontalScope())
            {
                if (ElementaPanelGui.ActionButton("Bake Default Backdrop",
                        "Regenerate the shared deterministic star cubemap and assign it where "
                        + "the default is expected.", true))
                {
                    SolStellarBackdropBaker.BakeDefault();
                    context.Refresh();
                }

                ElementaPanelGui.Metric("Default bake current",
                    SolStellarBackdropBaker.IsCurrentDefaultBake() ? "yes" : "no");
            }
        }

        // -- Body --------------------------------------------------------------------

        void DrawProfileBody(ElementaPanelContext context)
        {
            SolSkyProfile profile = context.Time.SkyProfile;
            if (profile == null)
                return;

            ElementaPanelGui.Rule();
            EditorGUILayout.LabelField($"Editing  {profile.name}", EditorStyles.boldLabel);

            if (_serializedProfile == null || _serializedProfile.targetObject != profile)
                _serializedProfile = new SerializedObject(profile);

            bool changed = ElementaPanelGui.DrawGroupedProperties(
                context, "sky/body", _serializedProfile, ref context.State.skyFilter);
            if (!changed)
                return;

            EditorUtility.SetDirty(profile);

            // The sky frame is rebuilt from the profile on assignment, so a reassignment is
            // what republishes an edited value. Writing the asset alone leaves the live frame
            // on the values it resolved when the profile was first set.
            context.Time.SetSkyProfile(null);
            context.Time.SetSkyProfile(profile);
            context.Refresh();
        }
    }
}
