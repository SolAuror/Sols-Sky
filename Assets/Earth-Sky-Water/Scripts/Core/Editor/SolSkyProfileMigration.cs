using System.Collections.Generic;
using Sol.ToD;
using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>Idempotent conversion of legacy inline authoring into one sky profile.</summary>
    public static class SolSkyProfileMigration
    {
        public readonly struct Result
        {
            public readonly SolSkyProfile Profile;
            public readonly IReadOnlyList<string> ApproximateFields;
            public readonly bool Created;

            internal Result(SolSkyProfile profile, IReadOnlyList<string> approximateFields,
                bool created)
            {
                Profile = profile;
                ApproximateFields = approximateFields;
                Created = created;
            }
        }

        [MenuItem("Tools/Elementa/Migrate Selected Time Of Day")]
        static void MigrateSelected()
        {
            TimeOfDay timeOfDay = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponentInParent<TimeOfDay>()
                : null;
            if (timeOfDay == null)
            {
                EditorUtility.DisplayDialog("Elementa Sky Migration",
                    "Select a GameObject below a TimeOfDay authority.", "OK");
                return;
            }

            string path = EditorUtility.SaveFilePanelInProject("Create Sky Profile",
                $"{timeOfDay.gameObject.scene.name}_Sky", "asset",
                "Choose where the migrated profile should be saved.");
            if (string.IsNullOrEmpty(path))
                return;

            Result result = CreateOrReuse(timeOfDay, path);
            Selection.activeObject = result.Profile;
            string approximations = result.ApproximateFields.Count > 0
                ? "\n\nApproximate mappings:\n• " + string.Join("\n• ", result.ApproximateFields)
                : string.Empty;
            EditorUtility.DisplayDialog("Elementa Sky Migration",
                (result.Created ? "Created and assigned a sky profile."
                    : "The existing assigned profile was reused.") + approximations, "OK");
        }

        [MenuItem("Tools/Elementa/Migrate Selected Time Of Day", true)]
        static bool ValidateMigrateSelected() => Selection.activeGameObject != null
            && Selection.activeGameObject.GetComponentInParent<TimeOfDay>() != null;

        public static Result CreateOrReuse(TimeOfDay timeOfDay, string assetPath)
        {
            if (timeOfDay == null)
                return default;
            if (timeOfDay.SkyProfile != null)
                return new Result(timeOfDay.SkyProfile, System.Array.Empty<string>(), false);

            SolSkyProfile profile = ScriptableObject.CreateInstance<SolSkyProfile>();
            profile.name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            SerializedObject source = new(timeOfDay);
            CopyColor(source, "skyZenithDay", value => profile.dayZenith = value);
            CopyColor(source, "skyHorizonDay", value => profile.dayHorizon = value);
            CopyColor(source, "skyNadirDay", value => profile.dayNadir = value);
            CopyColor(source, "skyZenithNight", value => profile.nightZenith = value);
            CopyColor(source, "skyHorizonNight", value => profile.nightHorizon = value);
            CopyColor(source, "skyNadirNight", value => profile.nightNadir = value);
            CopyColor(source, "skyHorizonSunrise", value => profile.sunriseHorizon = value);
            CopyColor(source, "skyHorizonSunset", value => profile.sunsetHorizon = value);
            CopyColor(source, "fogDayColor", value => profile.fogDayColor = value);
            CopyColor(source, "fogNightColor", value => profile.fogNightColor = value);
            CopyFloat(source, "fogDayDensity", value => profile.fogDayDensity = value);
            CopyFloat(source, "fogNightDensity", value => profile.fogNightDensity = value);
            CopyFloat(source, "ambientSkyIntensity", value => profile.ambientIntensity = value);
            CopyFloat(source, "starIntensityNight", value => profile.starIntensity = value);
            CopyFloat(source, "starPower", value => profile.starPower = value);
            CopyFloat(source, "starHeight", value => profile.starHeight = value);
            CopyFloat(source, "starTwinkleAmount", value => profile.twinkleAmount = value);
            CopyFloat(source, "starTwinkleSpeed", value => profile.twinkleSpeed = value);
            CopyFloat(source, "sunDiscIntensity", value => profile.sunIntensity = value);
            CopyFloat(source, "moonTerminatorSharpness", value => profile.moonTerminatorSharpness = value);
            CopyColor(source, "moonLitColor", value => profile.moonLitColor = value);
            CopyColor(source, "moonDarkColor", value => profile.moonDarkColor = value);
            CopyColor(source, "coronaColor", value => profile.coronaColor = value);
            CopyColor(source, "lunarEclipseTint", value => profile.lunarEclipseTint = value);
            CopyFloat(source, "horizonOcclusionLevel", value => profile.horizonOcclusionLevel = value);
            CopyFloat(source, "horizonOcclusionSoftness", value => profile.horizonOcclusionSoftness = value);
            CopyFloat(source, "horizonRefraction", value => profile.horizonRefraction = value);
            CopyFloat(source, "horizonFlatten", value => profile.horizonFlatten = value);
            CopyFloat(source, "horizonGlowRetention", value => profile.horizonGlowRetention = value);
            CopyFloat(source, "sunAirMassExtinction", value => profile.airMassExtinction = value);
            CopyFloat(source, "auroraIntensity", value => profile.auroraIntensity = value);
            CopyFloat(source, "auroraNightChance", value => profile.auroraNightChance = value);

            float sunCos = Float(source, "sunDiscSize", 0.9995f);
            float moonCos = Float(source, "moonDiscSize", 0.9993f);
            profile.sunAngularDiameter = CosineThresholdToDiameter(sunCos);
            profile.moonAngularDiameter = CosineThresholdToDiameter(moonCos);

            SolAtmosphereController atmosphere = Object.FindFirstObjectByType<SolAtmosphereController>();
            if (atmosphere != null)
            {
                SerializedProperty legacyProperty = new SerializedObject(atmosphere).FindProperty("profile");
                if (legacyProperty != null && legacyProperty.objectReferenceValue is SolAtmosphereProfile legacy)
                    CopyAtmosphere(legacy, profile);
            }

            AssetDatabase.CreateAsset(profile, assetPath);
            AssetDatabase.SaveAssets();
            Undo.RecordObject(timeOfDay, "Assign migrated Elementa sky profile");
            timeOfDay.SetSkyProfile(profile);
            EditorUtility.SetDirty(timeOfDay);
            EditorUtility.SetDirty(profile);
            SceneView.RepaintAll();

            string[] approximate =
            {
                "legacy additive sun glow and haze were folded into directional atmospheric response",
                "day/night gradient sharpness was translated into one continuous normalized shape",
                "cloud day/night thresholds were translated into one authored baseline"
            };
            return new Result(profile, approximate, true);
        }

        static void CopyAtmosphere(SolAtmosphereProfile source, SolSkyProfile target)
        {
            target.atmosphereDensityMultiplier = source.densityMultiplier;
            target.atmosphereStartDistance = source.startDistance;
            target.atmosphereMaxDistance = source.maxDistance;
            target.atmosphereMaxOpacity = source.maxOpacity;
            target.atmosphereBaseHeight = source.baseHeight;
            target.atmosphereHeightFalloff = source.heightFalloff;
            target.atmosphereMistBaseHeight = source.mistBaseHeight;
            target.atmosphereMistHeightFalloff = source.mistHeightFalloff;
            target.atmosphereNoiseIntensity = source.noiseIntensity;
            target.atmosphereNoiseScale = source.noiseScale;
            target.atmosphereNoiseSpeed = source.noiseSpeed;
            target.skyFogStrength = source.skyFogStrength;
            target.zenithFogStrength = source.zenithFogStrength;
            target.horizonFogStrength = source.horizonFogStrength;
            target.phaseAnisotropy = source.phaseAnisotropy;
            target.directionalScatteringIntensity = source.directionalScatteringIntensity;
            target.shadowedScatteringStrength = source.shadowedScatteringStrength;
            target.fogSaturation = source.fogSaturation;
            target.ambientScatteringIntensity = source.ambientScatteringIntensity;
            target.maxScatteringLuminance = source.maxScatteringLuminance;
            target.lightningScatteringIntensity = source.lightningScatteringIntensity;
            target.dayScatteringColor = source.dayScatteringColor;
            target.nightScatteringColor = source.nightScatteringColor;
            target.atmosphereRaymarchDistance = source.raymarchDistance;
            target.atmosphereRaymarchStepCount = source.raymarchStepCount;
            target.atmosphereRaymarchJitter = source.raymarchJitter;
            target.atmosphereBilateralDepthThreshold = source.bilateralDepthThreshold;
            target.atmosphereSpatialFilterStrength = source.spatialFilterStrength;
            target.atmosphereQuality = source.quality;
        }

        static float CosineThresholdToDiameter(float cosineThreshold)
            => Mathf.Acos(Mathf.Clamp(cosineThreshold, -1f, 1f)) * 2f * Mathf.Rad2Deg;

        static float Float(SerializedObject source, string name, float fallback)
        {
            SerializedProperty property = source.FindProperty(name);
            return property != null ? property.floatValue : fallback;
        }

        static void CopyFloat(SerializedObject source, string name, System.Action<float> assign)
        {
            SerializedProperty property = source.FindProperty(name);
            if (property != null)
                assign(property.floatValue);
        }

        static void CopyColor(SerializedObject source, string name, System.Action<Color> assign)
        {
            SerializedProperty property = source.FindProperty(name);
            if (property != null)
                assign(property.colorValue);
        }
    }
}
