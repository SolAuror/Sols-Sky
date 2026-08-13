using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>Idempotent project setup for the Sol environment renderer integration.</summary>
public static class SolEnvironmentSetupUtility
{
    const string RendererPath = "Assets/Settings/Sol_Renderer.asset";
    const string AtmosphereShaderPath = "Assets/Sky-and-Water/Resources/SolAtmosphere.shader";

    [MenuItem("Tools/Sol Environment/Ensure Atmosphere Renderer Feature")]
    public static void EnsureAtmosphereRendererFeature()
    {
        ScriptableRendererData rendererData = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(RendererPath);
        if (rendererData == null)
            throw new System.InvalidOperationException($"Sol renderer data was not found at {RendererPath}.");

        foreach (ScriptableRendererFeature existing in rendererData.rendererFeatures)
        {
            if (existing is SolAtmosphereRendererFeature)
            {
                ConfigureFeature(existing);
                Save(rendererData, existing);
                Debug.Log("[SolEnvironmentSetup] Atmosphere renderer feature is already installed.");
                return;
            }
        }

        SolAtmosphereRendererFeature feature = ScriptableObject.CreateInstance<SolAtmosphereRendererFeature>();
        feature.name = "SolAtmosphereRendererFeature";
        ConfigureFeature(feature);
        AssetDatabase.AddObjectToAsset(feature, rendererData);
        rendererData.rendererFeatures.Add(feature);

        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);
        SerializedObject rendererObject = new(rendererData);
        rendererObject.Update();
        SerializedProperty featureMap = rendererObject.FindProperty("m_RendererFeatureMap");
        featureMap.arraySize = rendererData.rendererFeatures.Count;
        featureMap.GetArrayElementAtIndex(featureMap.arraySize - 1).longValue = localId;
        rendererObject.ApplyModifiedPropertiesWithoutUndo();

        Save(rendererData, feature);
        Debug.Log("[SolEnvironmentSetup] Installed atmosphere renderer feature without modifying existing features.");
    }

    static void ConfigureFeature(Object feature)
    {
        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(AtmosphereShaderPath);
        if (shader == null)
            throw new System.InvalidOperationException($"Sol atmosphere shader was not found at {AtmosphereShaderPath}.");

        SerializedObject featureObject = new(feature);
        featureObject.FindProperty("atmosphereShader").objectReferenceValue = shader;
        featureObject.ApplyModifiedPropertiesWithoutUndo();
    }

    static void Save(ScriptableRendererData rendererData, Object feature)
    {
        EditorUtility.SetDirty(feature);
        EditorUtility.SetDirty(rendererData);
        rendererData.SetDirty();
        AssetDatabase.SaveAssets();
    }
}
