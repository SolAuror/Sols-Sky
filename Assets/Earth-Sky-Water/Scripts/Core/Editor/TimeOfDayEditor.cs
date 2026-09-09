using System.Collections.Generic;
using Sol.ToD;
using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>Keeps migrated inline appearance serialized but out of the normal authoring path.</summary>
    [CustomEditor(typeof(TimeOfDay))]
    [CanEditMultipleObjects]
    public sealed class TimeOfDayEditor : UnityEditor.Editor
    {
        static readonly HashSet<string> ProfileOwned = new()
        {
            // Direct-light settings still belong to TimeOfDay, even with a sky profile.
            "eclipseAmbientColor", "eclipseFogColor", "ambientSkyIntensity", "ambientDayColor",
            "ambientNightColor", "enableNightFog", "fogDayColor", "fogNightColor",
            "fogDayDensity", "fogNightDensity", "skyZenithDay", "skyHorizonDay",
            "skyHorizonSunrise", "skyHorizonSunset", "skyNadirDay", "skyZenithBlendDay",
            "skyHorizonBlendDay", "skyNadirBlendDay", "skyZenithNight", "skyHorizonNight",
            "skyNadirNight", "skyZenithBlendNight", "skyHorizonBlendNight", "skyNadirBlendNight",
            "skyZenithEclipse", "skyHorizonEclipse", "starIntensityNight", "starPower",
            "starHeight", "starTwinkleAmount", "starTwinkleSpeed", "enableAurora", "auroraNightChance", "auroraIntensity",
            "cloudScale", "cloudBaseSpeed", "cloudHeight", "cloudCoverageDay",
            "cloudCoverageNight", "cloudDensityDay", "cloudDensityNight", "cloudColorDay",
            "cloudColorSunset", "cloudColorNight", "cloudShadowDay", "cloudShadowNight",
            "sunDiscSize", "sunDiscIntensity", "sunGlowFalloff", "sunGlowIntensityDay",
            "sunGlowIntensityNight", "sunGlowIntensityHorizon", "coronaColor", "moonDiscSize",
            "moonLitColor", "moonDarkColor", "moonTerminatorSharpness", "hazeIntensityDay",
            "hazeIntensityNight", "sunGlowWideWeight", "sunGlowWideFalloff",
            "sunAirMassExtinction", "horizonOcclusionLevel", "horizonOcclusionSoftness",
            "horizonRefraction", "horizonFlatten", "horizonGlowRetention",
            "twilightBandIntensity", "earthShadowColor", "beltOfVenusColor"
        };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            SerializedProperty skyProfile = serializedObject.FindProperty("skyProfile");
            bool usesProfile = skyProfile != null && skyProfile.objectReferenceValue != null;
            SolSkyProfile shown = skyProfile != null
                ? skyProfile.objectReferenceValue as SolSkyProfile : null;
            EditorGUI.showMixedValue = skyProfile != null && skyProfile.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            SolSkyProfile selected = (SolSkyProfile)EditorGUILayout.ObjectField(
                "Sky Profile", shown, typeof(SolSkyProfile), false);
            if (EditorGUI.EndChangeCheck())
            {
                foreach (Object value in targets)
                {
                    TimeOfDay timeOfDay = (TimeOfDay)value;
                    Undo.RecordObject(timeOfDay, "Switch Elementa sky profile");
                    timeOfDay.SetSkyProfile(selected);
                    EditorUtility.SetDirty(timeOfDay);
                }
                serializedObject.Update();
                usesProfile = selected != null;
            }
            EditorGUI.showMixedValue = false;

            SerializedProperty iterator = serializedObject.GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.propertyPath == "skyProfile")
                    continue;
                if (usesProfile && ProfileOwned.Contains(iterator.propertyPath))
                    continue;
                using (new EditorGUI.DisabledScope(iterator.propertyPath == "m_Script"))
                    EditorGUILayout.PropertyField(iterator, true);
            }
            serializedObject.ApplyModifiedProperties();

            if (!usesProfile)
                EditorGUILayout.HelpBox("Inline appearance is the compatibility fallback. Use Tools/Elementa/Control Panel to migrate it into a reusable profile.", MessageType.Info);
            else if (GUILayout.Button("Open Elementa Control Panel"))
                EditorWindow.GetWindow<ElementaControlPanel>().Show();
        }
    }
}
