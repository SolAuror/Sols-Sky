using System;
using System.IO;
using Sol.Environment;
using Sol.Water;
using Sol.Water.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>Idempotent installation, lab creation, and one-way scene conversion for Water 2.</summary>
public static class SolWaterOverhaulSetupUtility
{
    const string RendererPath = "Assets/Settings/Sol_Renderer.asset";
    const string RootPath = "Assets/Sky-and-Water/Water2 Assets";
    // The generated scenes live alongside the authored ones, which is where the
    // laboratory scene actually ended up. Writing them to a separate Demo folder
    // left the documented entry points pointing at paths that did not exist.
    const string DemoPath = "Assets/Scenes";
    // These live under Shaders/, not Resources/. They were moved out of a Resources
    // folder when serialized references replaced Resources.Load; the paths here were
    // not updated with them, so this menu item used to null every reference it
    // touched. SolAssetResolver repairs a null in the editor, which is why the
    // damage stayed invisible until a build.
    const string ShaderFolder = "Assets/Sky-and-Water/Shaders/Water2/";
    const string OceanShaderPath = ShaderFolder + "SolOcean.shader";
    const string ResolveShaderPath = ShaderFolder + "SolWaterResolve.shader";
    const string UnderwaterShaderPath = ShaderFolder + "SolUnderwater.shader";
    const string FftShaderPath = ShaderFolder + "SolWaterFFT.compute";
    const string CausticShaderPath = ShaderFolder + "SolWaterCaustic.shader";
    const string VolumetricShaderPath = ShaderFolder + "SolWaterVolumetrics.shader";
    const string WaterProfilePath = RootPath + "/Ocean Profile.asset";
    const string QualityProfilePath = RootPath + "/High Quality.asset";
    const string AtmosphereProfilePath = RootPath + "/Lab Atmosphere.asset";
    const string VolumeProfilePath = RootPath + "/Lab Volume.asset";
    const string LabScenePath = DemoPath + "/Ocean Laboratory.unity";
    const string SourceDemoPath = "Assets/Scenes/SolsWeather_Demo.unity";
    const string RegressionScenePath = DemoPath + "/SolsWeather_Water2_Regression.unity";
    const string Water2DemoScenePath = "Assets/Scenes/Sols_Water2_Demo.unity";
    const string ShorelineDataPath = RootPath + "/Ocean Shoreline Data.asset";
    // The demo terrain spans one kilometre; 512 samples leave a contact texel
    // wider than the authored foam band and visibly staircase oblique beaches.
    const int ShorelineBakeResolution = 1024;

    [MenuItem("Tools/Sol Environment/Water 2/Install Renderer Feature")]
    public static void InstallRendererFeature()
    {
        ScriptableRendererData rendererData = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(RendererPath);
        if (rendererData == null)
            throw new InvalidOperationException($"Sol renderer data was not found at {RendererPath}.");

        SolWaterRendererFeature feature = null;
        foreach (ScriptableRendererFeature existing in rendererData.rendererFeatures)
        {
            if (existing is SolWaterRendererFeature waterFeature)
            {
                feature = waterFeature;
                break;
            }
        }

        if (feature == null)
        {
            feature = ScriptableObject.CreateInstance<SolWaterRendererFeature>();
            feature.name = "SolWaterRendererFeature";
            AssetDatabase.AddObjectToAsset(feature, rendererData);
            rendererData.rendererFeatures.Add(feature);
        }

        SerializedObject featureObject = new(feature);
        AssignAsset<Shader>(featureObject, "oceanShader", OceanShaderPath);
        AssignAsset<Shader>(featureObject, "resolveShader", ResolveShaderPath);
        AssignAsset<Shader>(featureObject, "underwaterShader", UnderwaterShaderPath);
        AssignAsset<ComputeShader>(featureObject, "fftShader", FftShaderPath);
        AssignAsset<Shader>(featureObject, "causticShader", CausticShaderPath);
        AssignAsset<Shader>(featureObject, "volumetricShader", VolumetricShaderPath);
        featureObject.ApplyModifiedPropertiesWithoutUndo();

        // Water 2 samples scene color for refraction and SSR. Force URP to provide
        // an intermediate color target in Game and Scene views instead of allowing
        // the pass to be scheduled directly against the swapchain backbuffer.
        SerializedObject rendererObject = new(rendererData);
        SerializedProperty intermediateTextureMode = rendererObject.FindProperty("m_IntermediateTextureMode");
        if (intermediateTextureMode != null)
            intermediateTextureMode.intValue = 1; // IntermediateTextureMode.Always
        rendererObject.ApplyModifiedPropertiesWithoutUndo();
        RebuildFeatureMap(rendererData);
        EditorUtility.SetDirty(feature);
        EditorUtility.SetDirty(rendererData);
        rendererData.SetDirty();
        AssetDatabase.SaveAssets();
        Debug.Log("[SolWater2] Renderer feature installed and configured.");
    }

    /// <summary>
    /// Points a serialized reference at the asset on disk, but never clears an already
    /// assigned one. A reference that survived into the asset is worth more than a path
    /// constant in this file: the path is what goes stale when shaders move, and
    /// overwriting a good reference with null is silent until the build stops finding
    /// the shader by name.
    /// </summary>
    static void AssignAsset<T>(SerializedObject featureObject, string propertyName,
        string assetPath) where T : UnityEngine.Object
    {
        SerializedProperty property = featureObject.FindProperty(propertyName);
        if (property == null)
        {
            Debug.LogWarning($"[SolWater2] '{propertyName}' is not a serialized field on " +
                "the water renderer feature; skipping.");
            return;
        }

        T asset = AssetDatabase.LoadAssetAtPath<T>(assetPath);
        if (asset == null)
        {
            if (property.objectReferenceValue == null)
                Debug.LogWarning($"[SolWater2] Could not load {typeof(T).Name} at " +
                    $"'{assetPath}' and '{propertyName}' is unassigned. Assign it by hand " +
                    "so it is included in builds.");
            return;
        }

        property.objectReferenceValue = asset;
    }

    [MenuItem("Tools/Sol Environment/Water 2/Create Ocean Laboratory")]
    public static void CreateOceanLaboratory()
    {
        EnsureFolders();
        InstallRendererFeature();
        SolWaterProfile waterProfile = LoadOrCreate<SolWaterProfile>(WaterProfilePath);
        SolWaterQualityProfile qualityProfile = LoadOrCreate<SolWaterQualityProfile>(QualityProfilePath);
        qualityProfile.tier = SolWaterQualityTier.High;
        qualityProfile.planarReflections = false;
        qualityProfile.screenSpaceReflections = true;
        EditorUtility.SetDirty(qualityProfile);
        SolAtmosphereProfile atmosphereProfile = LoadOrCreate<SolAtmosphereProfile>(AtmosphereProfilePath);
        atmosphereProfile.quality = SolAtmosphereQuality.High;
        EditorUtility.SetDirty(atmosphereProfile);
        VolumeProfile volumeProfile = LoadOrCreate<VolumeProfile>(VolumeProfilePath);
        ConfigureVolume(volumeProfile);

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = 0.0035f;
        RenderSettings.fogColor = new Color(0.38f, 0.53f, 0.62f);
        RenderSettings.ambientMode = AmbientMode.Trilight;

        GameObject worldRoot = new("Sol Environment World");
        worldRoot.SetActive(false);
        worldRoot.AddComponent<SolEnvironmentCoordinator>();
        worldRoot.AddComponent<SolEnvironmentWorld>();
        SolWaterWorld waterWorld = worldRoot.AddComponent<SolWaterWorld>();
        SetObjectReference(waterWorld, "defaultProfile", waterProfile);
        SetObjectReference(waterWorld, "qualityProfile", qualityProfile);

        GameObject atmosphereObject = new("Sol Atmosphere");
        atmosphereObject.transform.SetParent(worldRoot.transform);
        SolAtmosphereController atmosphere = atmosphereObject.AddComponent<SolAtmosphereController>();
        SetObjectReference(atmosphere, "profile", atmosphereProfile);

        GameObject oceanObject = new("Ocean");
        oceanObject.transform.SetParent(worldRoot.transform);
        SolWaterBody ocean = oceanObject.AddComponent<SolWaterBody>();
        SetEnum(ocean, "bodyType", (int)SolWaterBodyType.Ocean);
        SetObjectReference(ocean, "profile", waterProfile);
        SetBoolean(ocean, "enablePlanarReflection", false);
        GameObject interactionObject = new("Camera Interaction Zone");
        interactionObject.transform.SetParent(oceanObject.transform);
        SolWaterInteractionZone interaction = interactionObject.AddComponent<SolWaterInteractionZone>();
        SetEnum(interaction, "mode", (int)SolWaterInteractionZoneMode.FollowTarget);

        GameObject cameraObject = new("Main Camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        cameraObject.tag = "MainCamera";
        cameraObject.transform.SetPositionAndRotation(
            new Vector3(0f, 7f, -24f), Quaternion.Euler(10f, 0f, 0f));
        camera.farClipPlane = 12000f;
        camera.allowHDR = true;
        cameraObject.AddComponent<UniversalAdditionalCameraData>();

        GameObject lightObject = new("Sun");
        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        light.shadows = LightShadows.Soft;
        lightObject.transform.rotation = Quaternion.Euler(42f, -28f, 0f);
        RenderSettings.sun = light;

        GameObject volumeObject = new("Environment Volume");
        Volume volume = volumeObject.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 100f;
        volume.sharedProfile = volumeProfile;

        GameObject seabed = GameObject.CreatePrimitive(PrimitiveType.Cube);
        seabed.name = "Seabed Reference";
        seabed.transform.SetPositionAndRotation(new Vector3(0f, -8f, 40f), Quaternion.identity);
        seabed.transform.localScale = new Vector3(180f, 2f, 180f);

        worldRoot.SetActive(true);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, LabScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[SolWater2] Created ocean laboratory at {LabScenePath}.");
    }

    [MenuItem("Tools/Sol Environment/Water 2/Bake Active Terrain Shoreline Data")]
    public static void BakeActiveTerrainShorelineData()
    {
        Terrain terrain = UnityEngine.Object.FindFirstObjectByType<Terrain>();
        SolWaterBody[] bodies = UnityEngine.Object.FindObjectsByType<SolWaterBody>(
            FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        SolWaterBody ocean = Array.Find(bodies,
            body => body != null && body.BodyType == SolWaterBodyType.Ocean && body.Profile != null);
        if (terrain == null || terrain.terrainData == null)
            throw new InvalidOperationException("An active Terrain is required to bake shoreline data.");
        if (ocean == null)
            throw new InvalidOperationException("An active Water 2 ocean with a profile is required.");

        EnsureFolders();
        SolWaterProfile profile = ocean.Profile;
        int resolution = ShorelineBakeResolution;
        int pixelCount = resolution * resolution;
        bool[] water = new bool[pixelCount];
        float[] depths = new float[pixelCount];
        TerrainData terrainData = terrain.terrainData;
        Vector3 terrainPosition = terrain.transform.position;
        Vector3 terrainSize = terrainData.size;
        float waterLevel = ocean.SurfaceLevel;
        float depthRange = Mathf.Max(0.01f, profile.shorelineDepthRange);
        float distanceRange = Mathf.Max(0.01f, profile.shorelineDistanceRange);

        for (int y = 0; y < resolution; y++)
        {
            float v = (y + 0.5f) / resolution;
            for (int x = 0; x < resolution; x++)
            {
                float u = (x + 0.5f) / resolution;
                float terrainHeight = terrainData.GetInterpolatedHeight(u, v) + terrainPosition.y;
                int index = y * resolution + x;
                float depth = waterLevel - terrainHeight;
                depths[index] = depth;
                water[index] = depth >= 0f;
            }
        }

        float metresPerPixelX = terrainSize.x / resolution;
        float metresPerPixelY = terrainSize.z / resolution;
        float[] distanceToLand = BuildDistanceField(water, resolution, resolution,
            false, metresPerPixelX, metresPerPixelY);
        float[] distanceToWater = BuildDistanceField(water, resolution, resolution,
            true, metresPerPixelX, metresPerPixelY);
        Color[] pixels = new Color[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            float signedDistance = water[i] ? distanceToLand[i] : -distanceToWater[i];
            float encodedDepth = Mathf.Clamp01(0.5f + depths[i] / (depthRange * 2f));
            float encodedDistance = Mathf.Clamp01(0.5f + signedDistance / (distanceRange * 2f));
            pixels[i] = new Color(encodedDepth, encodedDistance, 0f, 1f);
        }

        Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(ShorelineDataPath);
        if (texture == null)
        {
            texture = new Texture2D(resolution, resolution, TextureFormat.RGHalf, false, true)
            {
                name = "Ocean Shoreline Data",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            AssetDatabase.CreateAsset(texture, ShorelineDataPath);
        }
        else
        {
            texture.Reinitialize(resolution, resolution, TextureFormat.RGHalf, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
        }
        texture.SetPixels(pixels);
        texture.Apply(false, false);

        SolDouble3 logicalOrigin = SolWorldOriginService.Active?.LogicalOrigin ?? default;
        profile.shorelineData = texture;
        profile.shorelineDataMapping = new Vector4(
            terrainPosition.x + terrainSize.x * 0.5f + (float)logicalOrigin.X,
            terrainPosition.z + terrainSize.z * 0.5f + (float)logicalOrigin.Z,
            terrainSize.x,
            terrainSize.z);
        EditorUtility.SetDirty(texture);
        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();
        Debug.Log($"[SolWater2] Baked {resolution}x{resolution} shoreline depth/SDF data to {ShorelineDataPath}.");
    }

    public static void BakeDemoShorelineDataFromCommandLine()
    {
        EditorSceneManager.OpenScene(Water2DemoScenePath, OpenSceneMode.Single);
        BakeActiveTerrainShorelineData();
    }

    internal static float[] BuildDistanceField(
        bool[] water,
        int width,
        int height,
        bool targetWater,
        float metresPerPixelX,
        float metresPerPixelY)
    {
        if (water == null || water.Length != width * height || width <= 0 || height <= 0)
            throw new ArgumentException("Distance-field input dimensions are invalid.", nameof(water));
        float[] distance = new float[water.Length];
        const float infinity = 1e20f;
        for (int i = 0; i < water.Length; i++)
            distance[i] = water[i] == targetWater ? 0f : infinity;

        float diagonal = Mathf.Sqrt(metresPerPixelX * metresPerPixelX
            + metresPerPixelY * metresPerPixelY);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                float value = distance[index];
                if (x > 0) value = Mathf.Min(value, distance[index - 1] + metresPerPixelX);
                if (y > 0) value = Mathf.Min(value, distance[index - width] + metresPerPixelY);
                if (x > 0 && y > 0) value = Mathf.Min(value, distance[index - width - 1] + diagonal);
                if (x + 1 < width && y > 0) value = Mathf.Min(value, distance[index - width + 1] + diagonal);
                distance[index] = value;
            }
        }
        for (int y = height - 1; y >= 0; y--)
        {
            for (int x = width - 1; x >= 0; x--)
            {
                int index = y * width + x;
                float value = distance[index];
                if (x + 1 < width) value = Mathf.Min(value, distance[index + 1] + metresPerPixelX);
                if (y + 1 < height) value = Mathf.Min(value, distance[index + width] + metresPerPixelY);
                if (x + 1 < width && y + 1 < height) value = Mathf.Min(value, distance[index + width + 1] + diagonal);
                if (x > 0 && y + 1 < height) value = Mathf.Min(value, distance[index + width - 1] + diagonal);
                distance[index] = value;
            }
        }
        return distance;
    }

    [MenuItem("Tools/Sol Environment/Water 2/Create Regression Demo Copy")]
    public static void CreateRegressionDemoCopy()
    {
        EnsureFolders();
        InstallRendererFeature();
        if (!AssetDatabase.LoadAssetAtPath<SceneAsset>(SourceDemoPath))
            throw new FileNotFoundException("Weather demo scene was not found.", SourceDemoPath);
        if (!AssetDatabase.CopyAsset(SourceDemoPath, RegressionScenePath)
            && AssetDatabase.LoadAssetAtPath<SceneAsset>(RegressionScenePath) == null)
            throw new InvalidOperationException("Could not copy the weather demo scene.");

        Scene scene = EditorSceneManager.OpenScene(RegressionScenePath, OpenSceneMode.Single);
        DisableLegacyWaterAuthorities();
        SolWaterProfile waterProfile = LoadOrCreate<SolWaterProfile>(WaterProfilePath);
        SolWaterQualityProfile qualityProfile = LoadOrCreate<SolWaterQualityProfile>(QualityProfilePath);
        CreateReplacementWorldIfMissing(waterProfile, qualityProfile);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, RegressionScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[SolWater2] Created migrated regression copy at {RegressionScenePath}.");
    }

    [MenuItem("Tools/Sol Environment/Water 2/Convert Open Scene (One Way)")]
    public static void ConvertOpenSceneOneWay()
    {
        if (!EditorUtility.DisplayDialog("Convert scene to Sol Water 2?",
            "This disables legacy water authorities in the open scene and adds the clean-break Water 2 world. Save a copy first.",
            "Convert", "Cancel"))
            return;
        EnsureFolders();
        InstallRendererFeature();
        DisableLegacyWaterAuthorities();
        CreateReplacementWorldIfMissing(
            LoadOrCreate<SolWaterProfile>(WaterProfilePath),
            LoadOrCreate<SolWaterQualityProfile>(QualityProfilePath));
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
    }

    static void CreateReplacementWorldIfMissing(
        SolWaterProfile waterProfile,
        SolWaterQualityProfile qualityProfile)
    {
        SolEnvironmentWorld environment = UnityEngine.Object.FindFirstObjectByType<SolEnvironmentWorld>();
        SolWaterWorld water = UnityEngine.Object.FindFirstObjectByType<SolWaterWorld>();
        GameObject root = environment != null ? environment.gameObject : new GameObject("Sol Environment World 2");
        bool wasActive = root.activeSelf;
        root.SetActive(false);
        if (root.GetComponent<SolEnvironmentCoordinator>() == null)
            root.AddComponent<SolEnvironmentCoordinator>();
        if (environment == null)
            environment = root.AddComponent<SolEnvironmentWorld>();
        if (water == null)
            water = root.AddComponent<SolWaterWorld>();
        SetObjectReference(water, "defaultProfile", waterProfile);
        SetObjectReference(water, "qualityProfile", qualityProfile);
        if (UnityEngine.Object.FindFirstObjectByType<SolWaterBody>() == null)
        {
            GameObject oceanObject = new("Ocean Water 2");
            oceanObject.transform.SetParent(root.transform);
            SolWaterBody ocean = oceanObject.AddComponent<SolWaterBody>();
            SetEnum(ocean, "bodyType", (int)SolWaterBodyType.Ocean);
            SetObjectReference(ocean, "profile", waterProfile);
            SetBoolean(ocean, "enablePlanarReflection", false);
        }
        root.SetActive(wasActive || !Application.isPlaying);
    }

    static void DisableLegacyWaterAuthorities()
    {
        MonoBehaviour[] behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < behaviours.Length; i++)
        {
            MonoBehaviour behaviour = behaviours[i];
            if (behaviour == null)
                continue;
            string typeName = behaviour.GetType().Name;
            if (typeName is "SolWaterManager" or "WaterTileGrid" or "UnderwaterController")
            {
                Undo.RecordObject(behaviour, "Disable legacy Sol water authority");
                behaviour.enabled = false;
                EditorUtility.SetDirty(behaviour);
            }
        }
    }

    static void ConfigureVolume(VolumeProfile profile)
    {
        if (!profile.TryGet(out SolAtmosphereVolume atmosphere))
            atmosphere = profile.Add<SolAtmosphereVolume>(true);
        atmosphere.enabledOverride.Override(true);
        atmosphere.densityMultiplier.Override(1f);
        atmosphere.quality.Override((int)SolAtmosphereQuality.High);
        EditorUtility.SetDirty(profile);
    }

    static T LoadOrCreate<T>(string path) where T : ScriptableObject
    {
        T value = AssetDatabase.LoadAssetAtPath<T>(path);
        if (value != null)
            return value;
        value = ScriptableObject.CreateInstance<T>();
        value.name = Path.GetFileNameWithoutExtension(path);
        AssetDatabase.CreateAsset(value, path);
        return value;
    }

    static void EnsureFolders()
    {
        EnsureFolder("Assets/Sky-and-Water", "Water2 Assets");
        EnsureFolder("Assets", "Scenes");
    }

    static void EnsureFolder(string parent, string name)
    {
        string path = parent + "/" + name;
        if (!AssetDatabase.IsValidFolder(path))
            AssetDatabase.CreateFolder(parent, name);
    }

    static void RebuildFeatureMap(ScriptableRendererData rendererData)
    {
        SerializedObject rendererObject = new(rendererData);
        rendererObject.Update();
        SerializedProperty featureMap = rendererObject.FindProperty("m_RendererFeatureMap");
        featureMap.arraySize = rendererData.rendererFeatures.Count;
        for (int i = 0; i < rendererData.rendererFeatures.Count; i++)
        {
            ScriptableRendererFeature feature = rendererData.rendererFeatures[i];
            long localId = 0;
            if (feature != null)
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out localId);
            featureMap.GetArrayElementAtIndex(i).longValue = localId;
        }
        rendererObject.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetObjectReference(UnityEngine.Object target, string property, UnityEngine.Object value)
    {
        SerializedObject serialized = new(target);
        serialized.FindProperty(property).objectReferenceValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetEnum(UnityEngine.Object target, string property, int value)
    {
        SerializedObject serialized = new(target);
        serialized.FindProperty(property).enumValueIndex = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetBoolean(UnityEngine.Object target, string property, bool value)
    {
        SerializedObject serialized = new(target);
        serialized.FindProperty(property).boolValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }
}
