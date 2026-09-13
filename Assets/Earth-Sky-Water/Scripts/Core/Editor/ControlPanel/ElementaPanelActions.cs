using System.IO;
using Sol.ToD;
using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>Authoring commands shared by the window and its pages.</summary>
    static class ElementaPanelActions
    {
        public static void CopyProfile(ElementaPanelContext context, Object owner, string path)
        {
            var serialized = context.Serialized(owner);
            serialized?.UpdateIfRequiredOrScript();
            if (serialized?.FindProperty(path)?.objectReferenceValue is not ScriptableObject source) return;
            string destination = EditorUtility.SaveFilePanelInProject("Make a profile copy",
                source.name + " Custom", "asset", "Choose where the editable copy is saved.");
            if (string.IsNullOrEmpty(destination)) return;
            CopyProfileTo(context, owner, path, destination);
        }

        internal static ScriptableObject CopyProfileTo(ElementaPanelContext context,
            Object owner, string propertyPath, string destination)
        {
            var serialized = context.Serialized(owner);
            serialized?.UpdateIfRequiredOrScript();
            if (serialized?.FindProperty(propertyPath)?.objectReferenceValue is not ScriptableObject source) return null;
            destination = AssetDatabase.GenerateUniqueAssetPath(destination);
            var copy = Object.Instantiate(source);
            copy.name = Path.GetFileNameWithoutExtension(destination);
            AssetDatabase.CreateAsset(copy, destination);
            serialized.Update();
            serialized.FindProperty(propertyPath).objectReferenceValue = copy;
            serialized.ApplyModifiedProperties();
            if (copy is SolWeatherProfileAsset weather) context.State.inspectedWeather = weather;
            context.PublishEdit(owner);
            context.RefreshLayout();
            return copy;
        }

        public static void SetClock(ElementaPanelContext context, float hour)
        {
            if (context.Time == null) return;
            context.Time.ApplyTimeChange(TimeChangeRequest.SetClockHour(hour, context.Window, "Elementa clock"));
            context.Refresh();
        }

        public static void PreviewCondition(ElementaPanelContext context, SolWeatherProfileAsset profile)
        {
            if (profile == null || context.Weather == null || context.Weather.IndexOfProfile(profile) < 0) return;
            context.State.weatherA = profile;
            context.State.weatherB = profile;
            context.State.weatherBlend = 0;
            context.Window.StartWeatherPreview();
        }

        public static void Tool(ElementaPanelContext context, System.Action action)
        {
            action(); context.Resolve(true); context.InvalidateIssues(); context.RefreshLayout();
        }
    }
}
