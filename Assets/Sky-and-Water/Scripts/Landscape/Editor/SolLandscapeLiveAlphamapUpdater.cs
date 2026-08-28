using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Sol.Landscape.Editor
{
    /// <summary>
    /// Keeps baked Manual-mode alphamaps responsive to Terrain sculpting in the editor. A compact R8
    /// mask records texels touched by texture painting; those texels and explicitly preserved layers
    /// are never changed by a procedural refresh.
    /// </summary>
    [InitializeOnLoad]
    internal static class SolLandscapeLiveAlphamapUpdater
    {
        private const string ConfigPath = "Assets/Sky-and-Water/Landscape/SolLandscapeConfig.asset";
        private const string ProtectionMaskPath = "Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeManualPaintProtection.asset";
        private const string LiveUpdatePreference = "Sol.Landscape.LiveAlphamapUpdates";
        private const float RuleEpsilon = 1e-6f;
        private const float InferenceTolerance = 2f / 255f;
        private const double UpdateDelaySeconds = 0.12d;

        private static Terrain pendingTerrain;
        private static RectInt pendingAlphaRegion;
        private static double processAfter;
        private static bool updateQueued;
        private static bool suppressTextureCallback;
        private static bool suppressHeightCallback;
        private static bool maskSaveQueued;
        private static double maskSaveAfter;

        static SolLandscapeLiveAlphamapUpdater()
        {
            if (Application.isBatchMode)
                return;

            TerrainCallbacks.heightmapChanged += OnHeightmapChanged;
            TerrainCallbacks.textureChanged += OnTextureChanged;
            EditorApplication.delayCall += UpdateMenuCheck;
        }

        private static bool LiveUpdatesEnabled
        {
            get => !EditorPrefs.HasKey(LiveUpdatePreference) || EditorPrefs.GetBool(LiveUpdatePreference, true);
            set
            {
                EditorPrefs.SetBool(LiveUpdatePreference, value);
                UpdateMenuCheck();
            }
        }

        internal static bool LiveUpdatesAreEnabled
        {
            get => LiveUpdatesEnabled;
            set => LiveUpdatesEnabled = value;
        }

        [MenuItem("Tools/Sol Landscape/Live Sculpt Texture Updates")]
        private static void ToggleLiveUpdates()
        {
            LiveUpdatesEnabled = !LiveUpdatesEnabled;
            Debug.Log($"[Sol Landscape] Live sculpt texture updates {(LiveUpdatesEnabled ? "enabled" : "disabled")}.");
        }

        [MenuItem("Tools/Sol Landscape/Live Sculpt Texture Updates", true)]
        private static bool ValidateToggleLiveUpdates()
        {
            UpdateMenuCheck();
            return true;
        }

        [MenuItem("Tools/Sol Landscape/Regenerate Procedural Areas (Keep Painted Textures)")]
        private static void RegenerateSafeFromMenu()
        {
            GetTarget(out Terrain terrain, out SolLandscapeConfig config);
            Texture2D mask = EnsureProtectionMask(terrain, config, inferFromCurrentAlphamap: true, out int protectedTexels);
            if (!EditorUtility.DisplayDialog(
                    "Regenerate procedural terrain textures?",
                    $"Only unpainted procedural texels will update. {protectedTexels:N0} painted texels and all explicitly preserved layer weights will remain unchanged.",
                    "Regenerate Safely",
                    "Cancel"))
                return;

            Undo.RegisterCompleteObjectUndo(terrain.terrainData, "Regenerate procedural terrain textures");
            RectInt fullRegion = new RectInt(0, 0, terrain.terrainData.alphamapWidth, terrain.terrainData.alphamapHeight);
            int updated = RegenerateRegion(terrain, config, mask, fullRegion);
            AssetDatabase.SaveAssetIfDirty(terrain.terrainData);
            AssetDatabase.SaveAssetIfDirty(mask);
            Debug.Log($"[Sol Landscape] Safely regenerated {updated:N0} procedural texels; preserved {protectedTexels:N0} painted texels.");
        }

        [MenuItem("Tools/Sol Landscape/Protect Current Texture Work")]
        private static void ProtectCurrentTextureWorkFromMenu()
        {
            GetTarget(out Terrain terrain, out SolLandscapeConfig config);
            Texture2D existing = LoadProtectionMask(terrain.terrainData);
            if (existing != null && !EditorUtility.DisplayDialog(
                    "Re-detect painted terrain texels?",
                    "This replaces the existing protection mask by comparing the current alphamap with the procedural rules. Current differences become protected paint.",
                    "Re-detect",
                    "Cancel"))
                return;

            Texture2D mask;
            int protectedTexels;
            if (existing == null)
            {
                mask = EnsureProtectionMask(terrain, config, inferFromCurrentAlphamap: true, out protectedTexels);
            }
            else
            {
                mask = existing;
                byte[] previousMask = mask.GetRawTextureData<byte>().ToArray();
                Undo.RegisterCompleteObjectUndo(mask, "Re-detect protected terrain texture work");
                try
                {
                    ClearMask(mask);
                    protectedTexels = InferProtectionMask(terrain, config, mask);
                    AssetDatabase.SaveAssetIfDirty(mask);
                }
                catch
                {
                    RestoreMask(mask, previousMask);
                    AssetDatabase.SaveAssetIfDirty(mask);
                    throw;
                }
            }
            Selection.activeObject = mask;
            Debug.Log($"[Sol Landscape] Protected {protectedTexels:N0} texels whose current texture weights differ from the procedural result.");
        }

        [MenuItem("Tools/Sol Landscape/Advanced/Clear Paint Protection and Regenerate Everything")]
        private static void ClearProtectionAndRegenerateFromMenu()
        {
            GetTarget(out Terrain terrain, out SolLandscapeConfig config);
            if (!EditorUtility.DisplayDialog(
                    "Clear all texture-paint protection?",
                    "This permanently replaces Dirt, Grass, Stone and Sand paint with the procedural rules. Path remains preserved. This cannot reconstruct paint that was already overwritten.",
                    "Clear Protection and Regenerate",
                    "Cancel"))
                return;

            Texture2D mask = EnsureProtectionMask(terrain, config, inferFromCurrentAlphamap: false, out _);
            byte[] previousMask = mask.GetRawTextureData<byte>().ToArray();
            Undo.RegisterCompleteObjectUndo(mask, "Clear terrain texture-paint protection");
            Undo.RegisterCompleteObjectUndo(terrain.terrainData, "Clear paint protection and regenerate terrain textures");
            int updated;
            try
            {
                ClearMask(mask);
                RectInt fullRegion = new RectInt(0, 0, terrain.terrainData.alphamapWidth, terrain.terrainData.alphamapHeight);
                updated = RegenerateRegion(terrain, config, mask, fullRegion);
            }
            catch
            {
                RestoreMask(mask, previousMask);
                throw;
            }
            AssetDatabase.SaveAssetIfDirty(terrain.terrainData);
            AssetDatabase.SaveAssetIfDirty(mask);
            Debug.Log($"[Sol Landscape] Cleared paint protection and regenerated {updated:N0} procedural texels. Preserved configured layers were untouched.");
        }

        /// <summary>Creates the persistent protection mask without changing TerrainData.</summary>
        public static void InitializeProtectionFromCommandLine()
        {
            try
            {
                SolLandscapeConfig config = AssetDatabase.LoadAssetAtPath<SolLandscapeConfig>(ConfigPath);
                Terrain terrain = SolLandscapeAlphamapGenerator.FindTerrain(config, openDemoSceneIfNeeded: true);
                EnsureProtectionMask(terrain, config, inferFromCurrentAlphamap: true, out int protectedTexels);
                AssetDatabase.SaveAssets();
                Debug.Log($"[Sol Landscape] Protection mask initialized; protectedTexels={protectedTexels}; totalTexels={terrain.terrainData.alphamapWidth * terrain.terrainData.alphamapHeight}.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>Exercises regional sculpt regeneration and protected-paint preservation on an unsaved TerrainData clone.</summary>
        public static void VerifyLiveUpdateFromCommandLine()
        {
            TerrainData cloneData = null;
            SolLandscapeConfig cloneConfig = null;
            Texture2D cloneMask = null;
            GameObject cloneObject = null;
            int exitCode = 1;
            try
            {
                SolLandscapeConfig sourceConfig = AssetDatabase.LoadAssetAtPath<SolLandscapeConfig>(ConfigPath);
                Terrain sourceTerrain = SolLandscapeAlphamapGenerator.FindTerrain(sourceConfig, openDemoSceneIfNeeded: true);
                cloneData = UnityEngine.Object.Instantiate(sourceTerrain.terrainData);
                cloneData.name = "G1 Live Update Verification TerrainData";
                cloneConfig = UnityEngine.Object.Instantiate(sourceConfig);
                var serializedConfig = new SerializedObject(cloneConfig);
                serializedConfig.FindProperty("terrainData").objectReferenceValue = cloneData;
                serializedConfig.ApplyModifiedPropertiesWithoutUndo();
                cloneObject = Terrain.CreateTerrainGameObject(cloneData);
                cloneObject.name = "G1 Live Update Verification Terrain";
                cloneObject.hideFlags = HideFlags.HideAndDontSave;
                cloneObject.transform.position = sourceTerrain.transform.position;
                Terrain cloneTerrain = cloneObject.GetComponent<Terrain>();

                cloneMask = new Texture2D(cloneData.alphamapWidth, cloneData.alphamapHeight, TextureFormat.R8, false, true);
                ClearMask(cloneMask);

                const int heightPatchSize = 33;
                int heightStartX = cloneData.heightmapResolution / 2 - heightPatchSize / 2;
                int heightStartY = cloneData.heightmapResolution / 2 - heightPatchSize / 2;
                var heightRegion = new RectInt(heightStartX, heightStartY, heightPatchSize, heightPatchSize);
                RectInt alphaRegion = HeightToAlphaRegion(cloneData, heightRegion);
                float[,,] alphaBeforeSculpt = cloneData.GetAlphamaps(alphaRegion.x, alphaRegion.y, alphaRegion.width, alphaRegion.height);
                float[,] heights = cloneData.GetHeights(heightStartX, heightStartY, heightPatchSize, heightPatchSize);
                for (int y = 0; y < heightPatchSize; ++y)
                {
                    for (int x = 0; x < heightPatchSize; ++x)
                    {
                        float ramp = 0.06f * x / (heightPatchSize - 1f);
                        heights[y, x] = Mathf.Clamp01(heights[y, x] + ramp);
                    }
                }
                cloneData.SetHeights(heightStartX, heightStartY, heights);
                int sculptUpdated = RegenerateRegion(cloneTerrain, cloneConfig, cloneMask, alphaRegion);
                float[,,] alphaAfterSculpt = cloneData.GetAlphamaps(alphaRegion.x, alphaRegion.y, alphaRegion.width, alphaRegion.height);

                int pathIndex = Array.FindIndex(cloneData.terrainLayers, layer => layer != null && layer.name == "TerrainLayer_Path");
                int grassIndex = Array.FindIndex(cloneData.terrainLayers, layer => layer != null && layer.name == "TerrainLayer_Grass");
                if (pathIndex < 0 || grassIndex < 0)
                    throw new InvalidOperationException("Verification requires Path and Grass terrain layers.");

                int changedBySculpt = 0;
                int pathChangedBySculpt = 0;
                for (int y = 0; y < alphaRegion.height; ++y)
                {
                    for (int x = 0; x < alphaRegion.width; ++x)
                    {
                        bool generatedChanged = false;
                        for (int layer = 0; layer < cloneData.alphamapLayers; ++layer)
                        {
                            if (alphaBeforeSculpt[y, x, layer] == alphaAfterSculpt[y, x, layer])
                                continue;
                            if (layer == pathIndex)
                                ++pathChangedBySculpt;
                            else
                                generatedChanged = true;
                        }
                        if (generatedChanged)
                            ++changedBySculpt;
                    }
                }

                int paintX = alphaRegion.x + alphaRegion.width / 2;
                int paintY = alphaRegion.y + alphaRegion.height / 2;
                float[,,] painted = cloneData.GetAlphamaps(paintX, paintY, 1, 1);
                float pathWeight = painted[0, 0, pathIndex];
                for (int layer = 0; layer < cloneData.alphamapLayers; ++layer)
                    painted[0, 0, layer] = layer == pathIndex ? pathWeight : 0f;
                painted[0, 0, grassIndex] = 1f - pathWeight;
                bool textureCallbackObserved = false;
                bool textureCallbackWasSynchronous = false;
                string callbackTextureName = string.Empty;
                bool setAlphamapsInProgress = true;
                TerrainCallbacks.TextureChangedCallback callbackProbe = (callbackTerrain, textureName, callbackRegion, callbackSynched) =>
                {
                    if (callbackTerrain != cloneTerrain)
                        return;
                    textureCallbackObserved = true;
                    textureCallbackWasSynchronous = setAlphamapsInProgress;
                    callbackTextureName = textureName;
                };
                TerrainCallbacks.textureChanged += callbackProbe;
                try
                {
                    cloneData.SetAlphamaps(paintX, paintY, painted);
                }
                finally
                {
                    setAlphamapsInProgress = false;
                    TerrainCallbacks.textureChanged -= callbackProbe;
                }
                float[,,] paintedPersisted = cloneData.GetAlphamaps(paintX, paintY, 1, 1);
                int newlyProtected = MarkProtected(cloneMask, new RectInt(paintX, paintY, 1, 1));
                int protectedUpdateCount = RegenerateRegion(cloneTerrain, cloneConfig, cloneMask, new RectInt(paintX, paintY, 1, 1));
                float[,,] afterProtectedRefresh = cloneData.GetAlphamaps(paintX, paintY, 1, 1);
                bool protectedPaintBitExact = true;
                for (int layer = 0; layer < cloneData.alphamapLayers; ++layer)
                    protectedPaintBitExact &= paintedPersisted[0, 0, layer] == afterProtectedRefresh[0, 0, layer];

                bool passed = sculptUpdated > 0
                    && changedBySculpt > 0
                    && pathChangedBySculpt == 0
                    && textureCallbackObserved
                    && textureCallbackWasSynchronous
                    && callbackTextureName == TerrainData.AlphamapTextureName
                    && newlyProtected == 1
                    && protectedUpdateCount == 0
                    && protectedPaintBitExact;
                string outputDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../../LandscapeG1Evidence"));
                Directory.CreateDirectory(outputDirectory);
                var evidence = new StringBuilder();
                evidence.AppendLine("Sol Landscape live sculpt alphamap and paint-protection verification");
                evidence.AppendLine($"UTC={DateTime.UtcNow:O}; Unity={Application.unityVersion}");
                evidence.AppendLine($"HeightPatch={heightRegion}; AlphaRefreshRegion={alphaRegion}; ProceduralTexelsVisited={sculptUpdated}; TexelsChangedBySculpt={changedBySculpt}");
                evidence.AppendLine($"PathValuesChangedBySculpt={pathChangedBySculpt}; Expected=0");
                evidence.AppendLine($"TextureCallbackObserved={textureCallbackObserved}; Synchronous={textureCallbackWasSynchronous}; TextureName={callbackTextureName}; ExpectedName={TerrainData.AlphamapTextureName}");
                evidence.AppendLine($"NewlyProtectedPaintTexels={newlyProtected}; ProtectedTexelsRegenerated={protectedUpdateCount}; ProtectedPaintBitExact={protectedPaintBitExact}");
                evidence.AppendLine($"PersistentTerrainDataChanged=False; Method=unsaved TerrainData and config clones");
                evidence.AppendLine($"RESULT={(passed ? "PASS" : "FAIL")}");
                File.WriteAllText(Path.Combine(outputDirectory, "G1_LiveSculptProtection_Evidence.txt"), evidence.ToString(), new UTF8Encoding(false));
                Debug.Log($"[Sol Landscape] Live update verification {(passed ? "PASS" : "FAIL")}; changedBySculpt={changedBySculpt}; pathChanges={pathChangedBySculpt}; textureCallback={textureCallbackObserved}/{textureCallbackWasSynchronous}/{callbackTextureName}; protectedBitExact={protectedPaintBitExact}.");
                exitCode = passed ? 0 : 1;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                exitCode = 1;
            }
            finally
            {
                if (cloneObject != null)
                    UnityEngine.Object.DestroyImmediate(cloneObject);
                if (cloneData != null)
                    UnityEngine.Object.DestroyImmediate(cloneData);
                if (cloneConfig != null)
                    UnityEngine.Object.DestroyImmediate(cloneConfig);
                if (cloneMask != null)
                    UnityEngine.Object.DestroyImmediate(cloneMask);
                EditorApplication.Exit(exitCode);
            }
        }

        /// <summary>Read-only semantic audit of the persistent TerrainData after editor-tool verification.</summary>
        public static void AuditCurrentTerrainFromCommandLine()
        {
            Texture2D transientMask = null;
            try
            {
                SolLandscapeConfig config = AssetDatabase.LoadAssetAtPath<SolLandscapeConfig>(ConfigPath);
                Terrain terrain = SolLandscapeAlphamapGenerator.FindTerrain(config, openDemoSceneIfNeeded: true);
                TerrainData data = terrain.terrainData;
                transientMask = new Texture2D(data.alphamapWidth, data.alphamapHeight, TextureFormat.R8, false, true);
                ClearMask(transientMask);
                int differingTexels = InferProtectionMask(terrain, config, transientMask);
                float centerHeight = terrain.transform.position.y + data.GetInterpolatedHeight(0.5f, 0.5f);
                float centerSlope = data.GetSteepness(0.5f, 0.5f);
                Debug.Log($"[Sol Landscape] Persistent audit; differingFromProcedural={differingTexels}; centerHeight={centerHeight:R}; centerSlope={centerSlope:R}; maskProtected={CountProtected(LoadProtectionMask(data))}.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
            finally
            {
                if (transientMask != null)
                    UnityEngine.Object.DestroyImmediate(transientMask);
            }
        }

        private static void UpdateMenuCheck()
        {
            Menu.SetChecked("Tools/Sol Landscape/Live Sculpt Texture Updates", LiveUpdatesEnabled);
        }

        private static void OnHeightmapChanged(Terrain terrain, RectInt heightRegion, bool synched)
        {
            if (suppressHeightCallback
                || !LiveUpdatesEnabled
                || EditorApplication.isCompiling
                || EditorApplication.isPlayingOrWillChangePlaymode
                || terrain == null
                || terrain.terrainData == null)
                return;

            SolLandscapeConfig config = AssetDatabase.LoadAssetAtPath<SolLandscapeConfig>(ConfigPath);
            if (config == null || config.TerrainData != terrain.terrainData || config.Layers.Any(entry => entry.mode != SolLandscapeLayerMode.Manual))
                return;

            RectInt alphaRegion = HeightToAlphaRegion(terrain.terrainData, heightRegion);
            if (updateQueued && pendingTerrain == terrain)
                pendingAlphaRegion = Union(pendingAlphaRegion, alphaRegion);
            else
            {
                pendingTerrain = terrain;
                pendingAlphaRegion = alphaRegion;
                updateQueued = true;
                processAfter = EditorApplication.timeSinceStartup + UpdateDelaySeconds;
                EditorApplication.update += ProcessPendingUpdate;
            }
        }

        private static void ProcessPendingUpdate()
        {
            if (!updateQueued || EditorApplication.timeSinceStartup < processAfter)
                return;

            EditorApplication.update -= ProcessPendingUpdate;
            updateQueued = false;
            Terrain terrain = pendingTerrain;
            RectInt region = pendingAlphaRegion;
            pendingTerrain = null;
            pendingAlphaRegion = default;
            if (EditorApplication.isCompiling
                || EditorApplication.isPlayingOrWillChangePlaymode
                || terrain == null
                || terrain.terrainData == null)
                return;

            try
            {
                suppressHeightCallback = true;
                try
                {
                    terrain.terrainData.SyncHeightmap();
                }
                finally
                {
                    suppressHeightCallback = false;
                }
                SolLandscapeConfig config = AssetDatabase.LoadAssetAtPath<SolLandscapeConfig>(ConfigPath);
                if (config == null || config.TerrainData != terrain.terrainData)
                    return;
                Texture2D mask = EnsureProtectionMask(terrain, config, inferFromCurrentAlphamap: true, out _);
                int updated = RegenerateRegion(terrain, config, mask, region);
                SceneView.RepaintAll();
                Debug.Log($"[Sol Landscape] Sculpt refresh updated {updated:N0} procedural alphamap texels in {region}.");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        private static void OnTextureChanged(Terrain terrain, string textureName, RectInt texelRegion, bool synched)
        {
            if (suppressTextureCallback
                || EditorApplication.isCompiling
                || EditorApplication.isPlayingOrWillChangePlaymode
                || terrain == null
                || terrain.terrainData == null
                || textureName != TerrainData.AlphamapTextureName)
                return;

            SolLandscapeConfig config = AssetDatabase.LoadAssetAtPath<SolLandscapeConfig>(ConfigPath);
            if (config == null || config.TerrainData != terrain.terrainData)
                return;

            try
            {
                Texture2D mask = EnsureProtectionMask(terrain, config, inferFromCurrentAlphamap: true, out _);
                int newlyProtected = MarkProtected(mask, ClampRegion(texelRegion, mask.width, mask.height));
                if (newlyProtected > 0)
                    Debug.Log($"[Sol Landscape] Protected {newlyProtected:N0} newly painted terrain texels from procedural sculpt refreshes.");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        internal static int RegenerateRegion(Terrain terrain, SolLandscapeConfig config, Texture2D mask, RectInt requestedRegion)
        {
            SolLandscapeAlphamapGenerator.ValidateInputs(terrain, config);
            TerrainData data = terrain.terrainData;
            RectInt region = ClampRegion(requestedRegion, data.alphamapWidth, data.alphamapHeight);
            if (region.width <= 0 || region.height <= 0)
                return 0;
            if (mask == null || mask.width != data.alphamapWidth || mask.height != data.alphamapHeight || mask.format != TextureFormat.R8)
                throw new InvalidOperationException("The manual-paint protection mask does not match the TerrainData alphamap.");

            int layerCount = data.alphamapLayers;
            int[] preservedIndices = Enumerable.Range(0, layerCount)
                .Where(index => config.Layers[index].preservePaintedWeightDuringGeneration)
                .ToArray();
            int[] generatedIndices = Enumerable.Range(0, layerCount)
                .Where(index => !config.Layers[index].preservePaintedWeightDuringGeneration)
                .ToArray();
            float[,,] current = data.GetAlphamaps(region.x, region.y, region.width, region.height);
            float[,,] output = (float[,,])current.Clone();
            var maskBytes = mask.GetRawTextureData<byte>();
            float[] weights = new float[layerCount];
            float waterLevel = SolLandscapeAlphamapGenerator.ResolveWaterLevel(config);
            int updated = 0;

            for (int localY = 0; localY < region.height; ++localY)
            {
                int alphaY = region.y + localY;
                float v = (alphaY + 0.5f) / data.alphamapHeight;
                for (int localX = 0; localX < region.width; ++localX)
                {
                    int alphaX = region.x + localX;
                    if (maskBytes[alphaY * data.alphamapWidth + alphaX] != 0)
                        continue;

                    float u = (alphaX + 0.5f) / data.alphamapWidth;
                    BuildProceduralWeights(terrain, config, current, localX, localY, generatedIndices, preservedIndices, u, v, waterLevel, weights);
                    for (int layer = 0; layer < layerCount; ++layer)
                        output[localY, localX, layer] = weights[layer];
                    ++updated;
                }
            }

            if (updated == 0)
                return 0;
            suppressTextureCallback = true;
            try
            {
                data.SetAlphamaps(region.x, region.y, output);
                data.SetBaseMapDirty();
                terrain.Flush();
                EditorUtility.SetDirty(data);
            }
            finally
            {
                suppressTextureCallback = false;
            }
            return updated;
        }

        internal static void ClearProtectionForDestructiveGeneration(TerrainData data)
        {
            Texture2D mask = LoadProtectionMask(data);
            if (mask != null)
            {
                Undo.RegisterCompleteObjectUndo(mask, "Clear terrain texture-paint protection");
                ClearMask(mask);
                AssetDatabase.SaveAssetIfDirty(mask);
            }
        }

        private static void BuildProceduralWeights(
            Terrain terrain,
            SolLandscapeConfig config,
            float[,,] current,
            int localX,
            int localY,
            int[] generatedIndices,
            int[] preservedIndices,
            float u,
            float v,
            float waterLevel,
            float[] weights)
        {
            Array.Clear(weights, 0, weights.Length);
            float preservedTotal = 0f;
            foreach (int index in preservedIndices)
            {
                float value = current[localY, localX, index];
                weights[index] = value;
                preservedTotal += value;
            }

            float slope = terrain.terrainData.GetSteepness(u, v);
            float worldY = terrain.transform.position.y + terrain.terrainData.GetInterpolatedHeight(u, v);
            float claimTotal = 0f;
            foreach (int index in generatedIndices)
            {
                float claim = SolLandscapeAlphamapGenerator.EvaluateRule(config.Layers[index], slope, worldY, waterLevel);
                weights[index] = claim;
                claimTotal += claim;
            }
            if (!(claimTotal > RuleEpsilon) || float.IsNaN(claimTotal) || float.IsInfinity(claimTotal))
                throw new InvalidOperationException($"No finite landscape rule claims alphamap coordinate ({u:R}, {v:R}).");

            float budgetScale = Mathf.Max(0f, 1f - preservedTotal) / claimTotal;
            foreach (int index in generatedIndices)
                weights[index] *= budgetScale;
        }

        private static Texture2D EnsureProtectionMask(
            Terrain terrain,
            SolLandscapeConfig config,
            bool inferFromCurrentAlphamap,
            out int protectedTexels)
        {
            TerrainData data = terrain.terrainData;
            Texture2D existing = LoadProtectionMask(data);
            if (existing != null)
            {
                protectedTexels = CountProtected(existing);
                return existing;
            }

            var mask = new Texture2D(data.alphamapWidth, data.alphamapHeight, TextureFormat.R8, false, true)
            {
                name = "SolLandscapeManualPaintProtection",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            ClearMask(mask);
            protectedTexels = inferFromCurrentAlphamap ? InferProtectionMask(terrain, config, mask) : 0;
            AssetDatabase.CreateAsset(mask, ProtectionMaskPath);
            EditorUtility.SetDirty(mask);
            AssetDatabase.SaveAssetIfDirty(mask);
            return mask;
        }

        private static Texture2D LoadProtectionMask(TerrainData data)
        {
            Texture2D mask = AssetDatabase.LoadAssetAtPath<Texture2D>(ProtectionMaskPath);
            if (mask == null)
                return null;
            if (mask.width == data.alphamapWidth && mask.height == data.alphamapHeight && mask.format == TextureFormat.R8 && mask.isReadable)
                return mask;
            throw new InvalidOperationException("The existing manual-paint protection mask is incompatible with the current TerrainData. Delete it explicitly before rebuilding protection.");
        }

        private static int InferProtectionMask(Terrain terrain, SolLandscapeConfig config, Texture2D mask)
        {
            SolLandscapeAlphamapGenerator.ValidateInputs(terrain, config);
            TerrainData data = terrain.terrainData;
            int layerCount = data.alphamapLayers;
            int[] preservedIndices = Enumerable.Range(0, layerCount)
                .Where(index => config.Layers[index].preservePaintedWeightDuringGeneration)
                .ToArray();
            int[] generatedIndices = Enumerable.Range(0, layerCount)
                .Where(index => !config.Layers[index].preservePaintedWeightDuringGeneration)
                .ToArray();
            float[,,] current = data.GetAlphamaps(0, 0, data.alphamapWidth, data.alphamapHeight);
            var maskBytes = mask.GetRawTextureData<byte>();
            float[] expected = new float[layerCount];
            float waterLevel = SolLandscapeAlphamapGenerator.ResolveWaterLevel(config);
            int protectedCount = 0;

            for (int y = 0; y < data.alphamapHeight; ++y)
            {
                float v = (y + 0.5f) / data.alphamapHeight;
                for (int x = 0; x < data.alphamapWidth; ++x)
                {
                    float u = (x + 0.5f) / data.alphamapWidth;
                    BuildProceduralWeights(terrain, config, current, x, y, generatedIndices, preservedIndices, u, v, waterLevel, expected);
                    bool differs = false;
                    foreach (int index in generatedIndices)
                    {
                        if (Mathf.Abs(current[y, x, index] - expected[index]) <= InferenceTolerance)
                            continue;
                        differs = true;
                        break;
                    }
                    if (!differs)
                        continue;
                    maskBytes[y * data.alphamapWidth + x] = byte.MaxValue;
                    ++protectedCount;
                }
            }
            mask.Apply(false, false);
            EditorUtility.SetDirty(mask);
            return protectedCount;
        }

        private static int MarkProtected(Texture2D mask, RectInt region)
        {
            var bytes = mask.GetRawTextureData<byte>();
            int newlyProtected = 0;
            for (int y = region.yMin; y < region.yMax; ++y)
            {
                int row = y * mask.width;
                for (int x = region.xMin; x < region.xMax; ++x)
                {
                    int index = row + x;
                    if (bytes[index] != 0)
                        continue;
                    bytes[index] = byte.MaxValue;
                    ++newlyProtected;
                }
            }
            if (newlyProtected > 0)
            {
                mask.Apply(false, false);
                EditorUtility.SetDirty(mask);
                QueueMaskSave();
            }
            return newlyProtected;
        }

        private static void QueueMaskSave()
        {
            maskSaveAfter = EditorApplication.timeSinceStartup + 1d;
            if (maskSaveQueued)
                return;
            maskSaveQueued = true;
            EditorApplication.update += SaveMaskWhenIdle;
        }

        private static void SaveMaskWhenIdle()
        {
            if (EditorApplication.timeSinceStartup < maskSaveAfter)
                return;
            EditorApplication.update -= SaveMaskWhenIdle;
            maskSaveQueued = false;
            Texture2D mask = AssetDatabase.LoadAssetAtPath<Texture2D>(ProtectionMaskPath);
            if (mask != null)
                AssetDatabase.SaveAssetIfDirty(mask);
        }

        private static void ClearMask(Texture2D mask)
        {
            var bytes = mask.GetRawTextureData<byte>();
            for (int i = 0; i < bytes.Length; ++i)
                bytes[i] = 0;
            mask.Apply(false, false);
            EditorUtility.SetDirty(mask);
        }

        private static void RestoreMask(Texture2D mask, byte[] bytes)
        {
            var destination = mask.GetRawTextureData<byte>();
            if (destination.Length != bytes.Length)
                throw new InvalidOperationException("Cannot restore a protection mask whose dimensions changed during the operation.");
            for (int i = 0; i < bytes.Length; ++i)
                destination[i] = bytes[i];
            mask.Apply(false, false);
            EditorUtility.SetDirty(mask);
        }

        private static int CountProtected(Texture2D mask)
        {
            var bytes = mask.GetRawTextureData<byte>();
            int count = 0;
            for (int i = 0; i < bytes.Length; ++i)
            {
                if (bytes[i] != 0)
                    ++count;
            }
            return count;
        }

        private static RectInt HeightToAlphaRegion(TerrainData data, RectInt heightRegion)
        {
            float denominator = Mathf.Max(1, data.heightmapResolution - 1);
            float uMin = Mathf.Clamp01((heightRegion.xMin - 2f) / denominator);
            float vMin = Mathf.Clamp01((heightRegion.yMin - 2f) / denominator);
            float uMax = Mathf.Clamp01((heightRegion.xMax + 2f) / denominator);
            float vMax = Mathf.Clamp01((heightRegion.yMax + 2f) / denominator);
            int xMin = Mathf.FloorToInt(uMin * data.alphamapWidth) - 1;
            int yMin = Mathf.FloorToInt(vMin * data.alphamapHeight) - 1;
            int xMax = Mathf.CeilToInt(uMax * data.alphamapWidth) + 1;
            int yMax = Mathf.CeilToInt(vMax * data.alphamapHeight) + 1;
            return ClampRegion(new RectInt(xMin, yMin, xMax - xMin, yMax - yMin), data.alphamapWidth, data.alphamapHeight);
        }

        private static RectInt ClampRegion(RectInt region, int width, int height)
        {
            int xMin = Mathf.Clamp(region.xMin, 0, width);
            int yMin = Mathf.Clamp(region.yMin, 0, height);
            int xMax = Mathf.Clamp(region.xMax, xMin, width);
            int yMax = Mathf.Clamp(region.yMax, yMin, height);
            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        private static RectInt Union(RectInt left, RectInt right)
        {
            int xMin = Mathf.Min(left.xMin, right.xMin);
            int yMin = Mathf.Min(left.yMin, right.yMin);
            int xMax = Mathf.Max(left.xMax, right.xMax);
            int yMax = Mathf.Max(left.yMax, right.yMax);
            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        private static void GetTarget(out Terrain terrain, out SolLandscapeConfig config)
        {
            config = AssetDatabase.LoadAssetAtPath<SolLandscapeConfig>(ConfigPath);
            terrain = SolLandscapeAlphamapGenerator.FindTerrain(config, openDemoSceneIfNeeded: false);
        }
    }
}
