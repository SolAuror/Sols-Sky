using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Sol.Landscape.Diagnostics
{
    /// <summary>
    /// Ticket 5A's command-line-gated standalone-player measurement harness.
    /// It deliberately measures rendered player-loop frames; it never calls Camera.Render().
    /// </summary>
    public sealed class SolLandscapePerformanceHarness : MonoBehaviour
    {
        private const string EnableArgument = "-solPerf5A";
        private const int SettleFrames = 30;
        private const int DrawControlWarmupFrames = 180;
        private const int MaximumSamplingAttemptsMultiplier = 8;
        private const int TerrainPatchQuads = 64;

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly int SurfaceWetnessId = Shader.PropertyToID("_Sol_SurfaceWetness");
        private static readonly int SurfaceSnowCoverId = Shader.PropertyToID("_Sol_SurfaceSnowCover");
        private static readonly int SurfaceTemperatureId = Shader.PropertyToID("_Sol_SurfaceTemperature");
        private static readonly int TerrainWetnessId = Shader.PropertyToID("_Sol_TerrainWetness");
        private static readonly int GlobalWaterLevelId = Shader.PropertyToID("_Sol_GlobalWaterLevel");
        private const string StochasticKeyword = "_SOL_LANDSCAPE_STOCHASTIC";

        private readonly FrameTiming[] latestTiming = new FrameTiming[1];
        private readonly List<FrameSample> terrainSamples = new List<FrameSample>();
        private readonly List<FrameSample> noTerrainSamples = new List<FrameSample>();

        private RunConfiguration configuration;
        private Camera measurementCamera;
        private Terrain terrain;
        private Material selectedMaterial;
        private ProfilerRecorder drawCallsRecorder;
        private ProfilerRecorder setPassRecorder;
        private ProfilerRecorder trianglesRecorder;
        private ProfilerRecorder batchesRecorder;
        private Phase phase;
        private int phaseFrame;
        private int samplingAttempts;
        private ulong lastTimingTimestamp;
        private bool hasTimingTimestamp;
        private DateTime utcStart;
        private Vector3 terrainBoundsCenter;
        private Vector3 cameraPosition;
        private float cameraDistance;
        private int terrainPatchCount;
        private int disabledMonoBehaviourCount;
        private int disabledRendererCount;
        private int disabledCanvasCount;
        private int disabledShadowCount;
        private string viewMatrixHash;
        private string projectionMatrixHash;
        private WeatherState weatherAtSampleStart;
        private AnisotropicFiltering anisotropyBeforeOverride;
        private bool finishing;
        private string tileSizeReport = "TileSizeOverride=None; layers measured at their baked m_TileSize";

        private enum Phase
        {
            Settle,
            TerrainWarmup,
            TerrainSample,
            NoTerrainWarmup,
            NoTerrainSample,
            Complete
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            string[] arguments = System.Environment.GetCommandLineArgs();
            if (!arguments.Any(argument => string.Equals(argument, EnableArgument, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            GameObject host = new GameObject("Sol Landscape Performance 5A");
            DontDestroyOnLoad(host);
            host.AddComponent<SolLandscapePerformanceHarness>();
        }

        private void Awake()
        {
            utcStart = DateTime.UtcNow;
            try
            {
                configuration = RunConfiguration.Parse(System.Environment.GetCommandLineArgs());
                ConfigurePlayer();
                ConfigureScene();
                StartRecorders();
                phase = Phase.Settle;
                phaseFrame = 0;
                Debug.Log(
                    $"[Sol Landscape 5A] Started {configuration.Label}: {configuration.Width}x{configuration.Height}, " +
                    $"fullscreen={configuration.RequestedFullScreenMode}, shader={configuration.ShaderMode}, stochastic={configuration.StochasticEnabled}.");
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        private void Update()
        {
            if (finishing)
            {
                return;
            }

            try
            {
                FrameTimingManager.CaptureFrameTimings();

                switch (phase)
                {
                    case Phase.Settle:
                        TickSettle();
                        break;
                    case Phase.TerrainWarmup:
                        TickTerrainWarmup();
                        break;
                    case Phase.TerrainSample:
                        TickSample(
                            terrainSamples,
                            configuration.TimerOnly ? Phase.Complete : Phase.NoTerrainWarmup);
                        break;
                    case Phase.NoTerrainWarmup:
                        TickNoTerrainWarmup();
                        break;
                    case Phase.NoTerrainSample:
                        TickSample(noTerrainSamples, Phase.Complete);
                        break;
                    case Phase.Complete:
                        Complete();
                        break;
                }
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        private void OnDestroy()
        {
            DisposeRecorders();
        }

        private void ConfigurePlayer()
        {
            Application.runInBackground = true;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
            anisotropyBeforeOverride = QualitySettings.anisotropicFiltering;
            if (configuration.AnisotropyMode == AnisotropyMode.ForceOn)
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            else if (configuration.AnisotropyMode == AnisotropyMode.Disabled)
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;
            OnDemandRendering.renderFrameInterval = 1;
            Screen.SetResolution(configuration.Width, configuration.Height, configuration.RequestedFullScreenMode);
        }

        private void ConfigureScene()
        {
            terrain = Terrain.activeTerrain != null
                ? Terrain.activeTerrain
                : FindObjectsByType<Terrain>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault();
            if (terrain == null || terrain.terrainData == null)
            {
                throw new InvalidOperationException("No active terrain with TerrainData was found.");
            }

            Camera[] cameras = FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            measurementCamera = cameras.FirstOrDefault(camera => camera.CompareTag("MainCamera"))
                ?? cameras.FirstOrDefault();
            if (measurementCamera == null)
            {
                throw new InvalidOperationException("No live camera was found.");
            }

            foreach (Camera candidate in cameras)
            {
                candidate.enabled = candidate == measurementCamera;
            }

            SolLandscapePerformanceAssets assets = Resources.Load<SolLandscapePerformanceAssets>("SolLandscapePerformanceAssets");
            if (assets == null)
            {
                throw new InvalidOperationException("The build-only SolLandscapePerformanceAssets resource is missing.");
            }

            if (configuration.ShaderMode != ShaderMode.Array && configuration.StochasticEnabled)
            {
                throw new InvalidOperationException("The stochastic control is available only on the array shader.");
            }

            // _SOL_LANDSCAPE_STOCHASTIC is shader_feature_local with no material [Toggle]: Unity's
            // build-time stripper only keeps its ON variant if some SERIALIZED material in the
            // build already has it enabled, so toggling it at runtime on a single shared material
            // is not sufficient by itself (verified in 5F.2: doing only that produced zero
            // post-strip ON variants). Pre-baked harness materials are selected here instead.
            if (configuration.ShaderMode == ShaderMode.Array)
            {
                selectedMaterial = configuration.StochasticEnabled ? assets.arrayMaterialStochastic : assets.arrayMaterial;
            }
            else
            {
                selectedMaterial = assets.legacyMaterial;
            }

            if (selectedMaterial == null || selectedMaterial.shader == null)
            {
                throw new InvalidOperationException($"The {configuration.ShaderMode} measurement material is missing.");
            }

            if (selectedMaterial.IsKeywordEnabled(StochasticKeyword) != configuration.StochasticEnabled)
            {
                throw new InvalidOperationException($"Selected material does not carry {StochasticKeyword}={configuration.StochasticEnabled} as baked.");
            }

            selectedMaterial.enableInstancing = true;
            terrain.materialTemplate = selectedMaterial;
            terrain.drawInstanced = true;
            ApplyTileSizeOverride();
            terrain.Flush();

            Vector3 terrainSize = terrain.terrainData.size;
            Vector3 terrainOrigin = terrain.transform.position;
            terrainBoundsCenter = terrainOrigin + terrainSize * 0.5f;
            cameraPosition = new Vector3(
                terrainBoundsCenter.x,
                terrainOrigin.y + terrainSize.y + 250f,
                terrainBoundsCenter.z);
            measurementCamera.transform.SetPositionAndRotation(cameraPosition, Quaternion.Euler(90f, 0f, 0f));
            measurementCamera.orthographic = true;
            measurementCamera.orthographicSize = terrainSize.z * 0.51f;
            measurementCamera.aspect = (float)configuration.Width / configuration.Height;
            measurementCamera.nearClipPlane = 0.1f;
            measurementCamera.farClipPlane = Math.Max(1000f, cameraPosition.y - terrainOrigin.y + 100f);
            measurementCamera.rect = new Rect(0f, 0f, 1f, 1f);
            measurementCamera.targetTexture = null;
            measurementCamera.allowMSAA = true;
            measurementCamera.allowDynamicResolution = false;
            measurementCamera.clearFlags = CameraClearFlags.SolidColor;
            measurementCamera.backgroundColor = Color.black;

            cameraDistance = Vector3.Distance(cameraPosition, terrainBoundsCenter);
            int heightmapQuads = terrain.terrainData.heightmapResolution - 1;
            int patchesPerAxis = Mathf.CeilToInt(heightmapQuads / (float)TerrainPatchQuads);
            terrainPatchCount = patchesPerAxis * patchesPerAxis;
        }

        /// <summary>
        /// Ticket 5F: in-memory-only tileSize override on the six live TerrainLayer assets so
        /// item 1 (baseline) and item 5 (post-decision) can be measured in the same player build,
        /// per N10. Nothing is written back to the .terrainlayer assets; this player process exits
        /// after one measurement, so there is nothing to restore.
        /// Ticket 5F.1: a uniform value plus an optional per-layer name:value map layered on top,
        /// so a split configuration (e.g. Stone1/Stone2 at 8m, Sand at 2m, the rest at 4m) can be
        /// measured in the same already-built player without a new CLI flag per layer.
        /// </summary>
        private void ApplyTileSizeOverride()
        {
            if (configuration.TileSizeOverride == null && configuration.TileSizeMap.Count == 0)
            {
                return;
            }

            TerrainLayer[] layers = terrain.terrainData.terrainLayers;
            var applied = new List<string>();
            foreach (TerrainLayer layer in layers)
            {
                if (layer == null)
                {
                    continue;
                }

                Vector2 original = layer.tileSize;
                float value = configuration.TileSizeMap.TryGetValue(layer.name, out float perLayer)
                    ? perLayer
                    : configuration.TileSizeOverride ?? original.x;
                layer.tileSize = new Vector2(value, value);
                applied.Add($"{layer.name}:{original.x.ToString("F3", Invariant)}->{value.ToString("F3", Invariant)}");
            }

            string uniformText = configuration.TileSizeOverride.HasValue
                ? configuration.TileSizeOverride.Value.ToString("F3", Invariant)
                : "None(baked)";
            string mapText = configuration.TileSizeMap.Count == 0
                ? "None"
                : string.Join(",", configuration.TileSizeMap.Select(entry => $"{entry.Key}:{entry.Value.ToString("F3", Invariant)}"));
            tileSizeReport = $"TileSizeOverrideUniform={uniformText}; TileSizeOverrideMap=[{mapText}]; AppliedLayers=[{string.Join(",", applied)}]; PersistedToAsset=False";
        }

        private void StartRecorders()
        {
            drawCallsRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count", 1);
            setPassRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count", 1);
            trianglesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count", 1);
            batchesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count", 1);
        }

        private void TickSettle()
        {
            ++phaseFrame;
            if (phaseFrame < SettleFrames)
            {
                return;
            }

            if (Screen.width != configuration.Width || Screen.height != configuration.Height)
            {
                throw new InvalidOperationException(
                    $"Requested {configuration.Width}x{configuration.Height}, but the player settled at {Screen.width}x{Screen.height}.");
            }

            if (Screen.fullScreenMode != configuration.RequestedFullScreenMode)
            {
                throw new InvalidOperationException(
                    $"Requested {configuration.RequestedFullScreenMode}, but the player settled at {Screen.fullScreenMode}.");
            }

            Time.timeScale = 0f;
            terrain.materialTemplate = selectedMaterial;
            terrain.drawHeightmap = true;
            terrain.drawInstanced = true;
            terrain.drawTreesAndFoliage = false;
            terrain.Flush();
            IsolateTerrainFrame();
            // Several environment publishers intentionally clear their globals on disable.
            // Capture after isolation so the declared weather is the state actually sampled.
            weatherAtSampleStart = WeatherState.Capture();
            viewMatrixHash = HashMatrix(measurementCamera.worldToCameraMatrix);
            projectionMatrixHash = HashMatrix(measurementCamera.projectionMatrix);
            phase = Phase.TerrainWarmup;
            phaseFrame = 0;
        }

        private void IsolateTerrainFrame()
        {
            foreach (MonoBehaviour behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (behaviour == this
                    || behaviour.gameObject == measurementCamera.gameObject
                    || behaviour.gameObject == terrain.gameObject)
                {
                    continue;
                }

                behaviour.enabled = false;
                ++disabledMonoBehaviourCount;
            }

            foreach (Renderer renderer in FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                renderer.enabled = false;
                ++disabledRendererCount;
            }

            foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                canvas.enabled = false;
                ++disabledCanvasCount;
            }

            foreach (Light light in FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (light.shadows != LightShadows.None)
                {
                    light.shadows = LightShadows.None;
                    ++disabledShadowCount;
                }
            }

            UniversalAdditionalCameraData cameraData = measurementCamera.GetUniversalAdditionalCameraData();
            cameraData.renderPostProcessing = false;
        }

        private void TickTerrainWarmup()
        {
            ++phaseFrame;
            if (phaseFrame < configuration.WarmupFrames)
            {
                return;
            }

            BeginSampling(Phase.TerrainSample);
        }

        private void TickNoTerrainWarmup()
        {
            ++phaseFrame;
            if (phaseFrame < DrawControlWarmupFrames)
            {
                return;
            }

            BeginSampling(Phase.NoTerrainSample);
        }

        private void BeginSampling(Phase samplingPhase)
        {
            phase = samplingPhase;
            phaseFrame = 0;
            samplingAttempts = 0;
            hasTimingTimestamp = false;
            lastTimingTimestamp = 0;
        }

        private void TickSample(List<FrameSample> destination, Phase nextPhase)
        {
            ++samplingAttempts;
            uint timingCount = FrameTimingManager.GetLatestTimings(1, latestTiming);
            if (timingCount == 1)
            {
                FrameTiming timing = latestTiming[0];
                bool timestampIsNew = !hasTimingTimestamp || timing.frameStartTimestamp != lastTimingTimestamp;
                if (timestampIsNew && IsFinitePositive(timing.cpuFrameTime) && IsFinitePositive(timing.gpuFrameTime))
                {
                    hasTimingTimestamp = true;
                    lastTimingTimestamp = timing.frameStartTimestamp;
                    destination.Add(new FrameSample(
                        destination.Count,
                        timing,
                        ReadRecorder(drawCallsRecorder),
                        ReadRecorder(setPassRecorder),
                        ReadRecorder(trianglesRecorder),
                        ReadRecorder(batchesRecorder)));
                }
            }

            if (destination.Count >= configuration.SampleFrames)
            {
                if (nextPhase == Phase.NoTerrainWarmup)
                {
                    terrain.drawHeightmap = false;
                    terrain.Flush();
                    phase = Phase.NoTerrainWarmup;
                    phaseFrame = 0;
                }
                else
                {
                    phase = nextPhase;
                }

                return;
            }

            int maximumAttempts = configuration.SampleFrames * MaximumSamplingAttemptsMultiplier;
            if (samplingAttempts >= maximumAttempts)
            {
                throw new InvalidOperationException(
                    $"Only {destination.Count} valid unique CPU+GPU timings were returned in {samplingAttempts} player frames.");
            }
        }

        private void Complete()
        {
            finishing = true;
            WeatherState weatherAtEnd = WeatherState.Capture();
            string report = BuildReport(weatherAtEnd);
            string csv = BuildCsv();
            Directory.CreateDirectory(Path.GetDirectoryName(configuration.OutputPath) ?? ".");
            File.WriteAllText(configuration.OutputPath, report, new UTF8Encoding(false));
            File.WriteAllText(Path.ChangeExtension(configuration.OutputPath, ".csv"), csv, new UTF8Encoding(false));
            Debug.Log($"[Sol Landscape 5A] PASS {configuration.Label}; evidence={configuration.OutputPath}");
            DisposeRecorders();
            Application.Quit(0);
        }

        private string BuildReport(WeatherState weatherAtEnd)
        {
            SampleSummary terrainSummary = SampleSummary.Create(terrainSamples);
            SampleSummary? noTerrainSummary = noTerrainSamples.Count > 0
                ? SampleSummary.Create(noTerrainSamples)
                : null;
            StringBuilder output = new StringBuilder();
            output.AppendLine("Sol Landscape Phase 5 ticket 5A standalone-player measurement");
            output.AppendLine($"Result=PASS");
            output.AppendLine($"Label={configuration.Label}");
            output.AppendLine($"UTCStart={utcStart:O}");
            output.AppendLine($"UTCEnd={DateTime.UtcNow:O}");
            output.AppendLine($"Unity={Application.unityVersion}");
            output.AppendLine($"Environment=Standalone Windows player; real player loop; no Camera.Render calls");
            output.AppendLine($"OperatingSystem={SystemInfo.operatingSystem}");
            output.AppendLine($"GraphicsDevice={SystemInfo.graphicsDeviceName}");
            output.AppendLine($"GraphicsDeviceType={SystemInfo.graphicsDeviceType}");
            output.AppendLine($"GraphicsDeviceVersion={SystemInfo.graphicsDeviceVersion}");
            output.AppendLine($"GraphicsMemoryMB={SystemInfo.graphicsMemorySize}");
            output.AppendLine($"RenderPipeline={GraphicsSettings.currentRenderPipeline?.name ?? "Built-in"}");
            output.AppendLine($"ShaderMode={configuration.ShaderMode}");
            output.AppendLine($"ShaderName={selectedMaterial.shader.name}");
            output.AppendLine($"StochasticKeywordEnabled={selectedMaterial.IsKeywordEnabled(StochasticKeyword)}");
            output.AppendLine(tileSizeReport);
            output.AppendLine($"MeasurementMode={(configuration.TimerOnly ? "TimerOnly" : "TerrainAndNoTerrainControl")}");
            output.AppendLine($"ResolutionRequested={configuration.Width}x{configuration.Height}");
            output.AppendLine($"ResolutionActual={Screen.width}x{Screen.height}");
            output.AppendLine($"ResolutionScaleLinear={configuration.ResolutionScale.ToString("F3", Invariant)}");
            output.AppendLine($"PixelCount={Screen.width * Screen.height}");
            output.AppendLine($"FullscreenModeRequested={configuration.RequestedFullScreenMode}");
            output.AppendLine($"FullscreenMode={Screen.fullScreenMode}");
            output.AppendLine($"DisplayRefreshRate={Screen.currentResolution.refreshRateRatio.value.ToString("F6", Invariant)}");
            output.AppendLine($"VSyncCountRuntime={QualitySettings.vSyncCount}");
            output.AppendLine($"AnisotropyModeRequested={configuration.AnisotropyMode}");
            output.AppendLine($"AnisotropyBeforeRuntimeOverride={anisotropyBeforeOverride}");
            output.AppendLine($"AnisotropyRuntime={QualitySettings.anisotropicFiltering}");
            output.AppendLine($"TargetFrameRate={Application.targetFrameRate}");
            output.AppendLine($"RenderFrameInterval={OnDemandRendering.renderFrameInterval}");
            output.AppendLine($"FrameTimingStatsProjectSetting=Enabled");
            output.AppendLine($"MSAAProjectSetting=2x; unchanged; CameraAllowMSAA={measurementCamera.allowMSAA}");
            output.AppendLine($"DynamicResolutionAllowed={measurementCamera.allowDynamicResolution}");
            output.AppendLine($"TerrainOnlyIsolation=True; transient runtime state only");
            output.AppendLine($"DisabledNonMeasurementMonoBehaviours={disabledMonoBehaviourCount}");
            output.AppendLine($"DisabledNonTerrainRenderers={disabledRendererCount}");
            output.AppendLine($"DisabledCanvases={disabledCanvasCount}");
            output.AppendLine($"LightsWithShadowsDisabled={disabledShadowCount}");
            output.AppendLine($"PostProcessingRuntime=False");
            output.AppendLine($"TerrainTreesAndFoliageRuntime=False");
            output.AppendLine($"CameraProjection=Orthographic top-down, entire terrain with 2% vertical margin");
            output.AppendLine($"CameraPosition={FormatVector(cameraPosition)}");
            output.AppendLine($"TerrainBoundsCenter={FormatVector(terrainBoundsCenter)}");
            output.AppendLine($"CameraDistanceToTerrainBoundsCenter={cameraDistance.ToString("F6", Invariant)}");
            output.AppendLine($"CameraOrthographicSize={measurementCamera.orthographicSize.ToString("F6", Invariant)}");
            output.AppendLine($"ViewMatrixSHA256={viewMatrixHash}");
            output.AppendLine($"ProjectionMatrixSHA256={projectionMatrixHash}");
            output.AppendLine($"SettleFramesBeforeFreeze={SettleFrames}");
            output.AppendLine($"TimeScaleDuringWarmupAndSampling={Time.timeScale.ToString("F1", Invariant)}");
            output.AppendLine($"WarmupFramesDiscarded={configuration.WarmupFrames}");
            output.AppendLine($"SampledFrames={terrainSamples.Count}");
            output.AppendLine("TrustedGPUStatistic=P5(gpuFrameTimeMs,terrain)-P5(gpuFrameTimeMs,no-terrain)");
            output.AppendLine("TrustedGPUStatisticReason=Low-percentile cluster isolates GPU work while CPU main thread caps typical frames; valid only while P1/P5/P10 remain clustered and separated from P50");
            output.AppendLine("PercentileMethod=Nearest-rank ceil(P*N), with Min=P0 and Max=P100; no means reported");
            output.AppendLine("LegacyMedianMethod=Average of two middle samples; retained for contamination audit");
            output.AppendLine($"GPUFrameTimeMedianMs={terrainSummary.GpuMedian.ToString("F6", Invariant)}");
            output.AppendLine($"GPUFrameTimeP95Ms={terrainSummary.GpuP95.ToString("F6", Invariant)}");
            AppendGpuDistribution(output, "TerrainGPUFrameTime", terrainSummary.GpuDistribution);
            output.AppendLine($"CPUFrameTimeMedianMs={terrainSummary.CpuMedian.ToString("F6", Invariant)}");
            output.AppendLine($"CPUFrameTimeP95Ms={terrainSummary.CpuP95.ToString("F6", Invariant)}");
            output.AppendLine($"SyncIntervalPerSample=RawSamplesCsv:phase=terrain,column=syncInterval");
            output.AppendLine($"SyncIntervalHistogram={BuildSyncIntervalHistogram(terrainSamples)}");
            output.AppendLine($"SyncIntervalZeroSamples={terrainSamples.Count - terrainSummary.SyncIntervalNonZero}");
            output.AppendLine($"SyncIntervalNonZeroSamples={terrainSummary.SyncIntervalNonZero}");
            output.AppendLine($"WidthScaleMedian={terrainSummary.WidthScaleMedian.ToString("F6", Invariant)}");
            output.AppendLine($"HeightScaleMedian={terrainSummary.HeightScaleMedian.ToString("F6", Invariant)}");
            output.AppendLine($"DrawCallsRecorderValid={drawCallsRecorder.Valid}");
            output.AppendLine($"SetPassRecorderValid={setPassRecorder.Valid}");
            output.AppendLine($"TrianglesRecorderValid={trianglesRecorder.Valid}");
            output.AppendLine($"BatchesRecorderValid={batchesRecorder.Valid}");
            output.AppendLine($"WholeFrameDrawCallsMedian={terrainSummary.DrawCallsMedian.ToString("F3", Invariant)}");
            output.AppendLine($"WholeFrameDrawCallsP95={terrainSummary.DrawCallsP95.ToString("F3", Invariant)}");
            output.AppendLine($"WholeFrameSetPassMedian={terrainSummary.SetPassMedian.ToString("F3", Invariant)}");
            output.AppendLine($"WholeFrameTrianglesMedian={terrainSummary.TrianglesMedian.ToString("F3", Invariant)}");
            output.AppendLine($"WholeFrameBatchesMedian={terrainSummary.BatchesMedian.ToString("F3", Invariant)}");
            if (noTerrainSummary.HasValue)
            {
                SampleSummary controlSummary = noTerrainSummary.Value;
                output.AppendLine($"NoTerrainControlWarmupFramesDiscarded={DrawControlWarmupFrames}");
                output.AppendLine($"NoTerrainControlSampledFrames={noTerrainSamples.Count}");
                output.AppendLine($"NoTerrainGPUFrameTimeMedianMs={controlSummary.GpuMedian.ToString("F6", Invariant)}");
                output.AppendLine($"NoTerrainGPUFrameTimeP95Ms={controlSummary.GpuP95.ToString("F6", Invariant)}");
                AppendGpuDistribution(output, "NoTerrainGPUFrameTime", controlSummary.GpuDistribution);
                AppendDerivedGpuDistribution(
                    output,
                    "DerivedTerrainGPUFrameTime",
                    terrainSummary.GpuDistribution,
                    controlSummary.GpuDistribution);
                output.AppendLine($"TrustedTerrainGPUCostP5Ms={(terrainSummary.GpuDistribution.P5 - controlSummary.GpuDistribution.P5).ToString("F6", Invariant)}");
                output.AppendLine($"TerrainOnlyGPUFrameTimeMedianDerivedMs={(terrainSummary.GpuMedian - controlSummary.GpuMedian).ToString("F6", Invariant)}");
                output.AppendLine($"TerrainOnlyGPUFrameTimeP95DerivedMs={(terrainSummary.GpuP95 - controlSummary.GpuP95).ToString("F6", Invariant)}");
                output.AppendLine($"NoTerrainDrawCallsMedian={controlSummary.DrawCallsMedian.ToString("F3", Invariant)}");
                output.AppendLine($"NoTerrainSetPassMedian={controlSummary.SetPassMedian.ToString("F3", Invariant)}");
                output.AppendLine($"TerrainOnlyDrawCallsDerived={Math.Max(0d, terrainSummary.DrawCallsMedian - controlSummary.DrawCallsMedian).ToString("F3", Invariant)}");
                output.AppendLine($"TerrainOnlySetPassDerived={Math.Max(0d, terrainSummary.SetPassMedian - controlSummary.SetPassMedian).ToString("F3", Invariant)}");
                output.AppendLine($"TerrainPatchCount={terrainPatchCount}; formula=ceil((heightmapResolution-1)/64)^2");
                output.AppendLine($"TerrainFraming=All patches inside the fixed orthographic camera frustum");
                output.AppendLine($"TerrainOnlyDrawsPerPatchDerived={(Math.Max(0d, terrainSummary.DrawCallsMedian - controlSummary.DrawCallsMedian) / terrainPatchCount).ToString("F6", Invariant)}");
                output.AppendLine($"TerrainOnlySetPassPerPatchDerived={(Math.Max(0d, terrainSummary.SetPassMedian - controlSummary.SetPassMedian) / terrainPatchCount).ToString("F6", Invariant)}");
            }
            else
            {
                output.AppendLine("NoTerrainControl=Skipped; P4 is closed and was not re-run in ticket 5A.1");
            }
            output.AppendLine($"WeatherStateAtSampleStart={weatherAtSampleStart}");
            output.AppendLine($"WeatherStateAtEnd={weatherAtEnd}");
            output.AppendLine($"WeatherStateStable={weatherAtSampleStart.Equals(weatherAtEnd)}");
            output.AppendLine($"RawSamplesCsv={Path.ChangeExtension(configuration.OutputPath, ".csv")}");
            return output.ToString();
        }

        private string BuildCsv()
        {
            StringBuilder output = new StringBuilder();
            output.AppendLine("phase,index,frameStartTimestamp,gpuFrameTimeMs,cpuFrameTimeMs,cpuMainThreadMs,cpuRenderThreadMs,presentWaitMs,syncInterval,widthScale,heightScale,drawCalls,setPassCalls,triangles,batches,estimatorPolicy");
            AppendCsvSamples(output, "terrain", terrainSamples);
            AppendCsvSamples(output, "no-terrain", noTerrainSamples);
            return output.ToString();
        }

        private static void AppendCsvSamples(StringBuilder output, string phaseName, IEnumerable<FrameSample> samples)
        {
            foreach (FrameSample sample in samples)
            {
                output.Append(phaseName).Append(',')
                    .Append(sample.Index).Append(',')
                    .Append(sample.FrameStartTimestamp).Append(',')
                    .Append(sample.GpuFrameTime.ToString("R", Invariant)).Append(',')
                    .Append(sample.CpuFrameTime.ToString("R", Invariant)).Append(',')
                    .Append(sample.CpuMainThreadTime.ToString("R", Invariant)).Append(',')
                    .Append(sample.CpuRenderThreadTime.ToString("R", Invariant)).Append(',')
                    .Append(sample.PresentWaitTime.ToString("R", Invariant)).Append(',')
                    .Append(sample.SyncInterval).Append(',')
                    .Append(sample.WidthScale.ToString("R", Invariant)).Append(',')
                    .Append(sample.HeightScale.ToString("R", Invariant)).Append(',')
                    .Append(sample.DrawCalls).Append(',')
                    .Append(sample.SetPassCalls).Append(',')
                    .Append(sample.Triangles).Append(',')
                    .Append(sample.Batches).Append(',')
                    .Append("P5_phase_percentile_difference").AppendLine();
            }
        }

        private void Fail(Exception exception)
        {
            if (finishing)
            {
                return;
            }

            finishing = true;
            Debug.LogException(exception);
            try
            {
                string outputPath = configuration.OutputPath;
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    outputPath = Path.GetFullPath("SolLandscape5A_Failure.txt");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
                File.WriteAllText(
                    outputPath,
                    $"Sol Landscape Phase 5 ticket 5A standalone-player measurement{System.Environment.NewLine}" +
                    $"Result=FAIL{System.Environment.NewLine}" +
                    $"UTC={DateTime.UtcNow:O}{System.Environment.NewLine}" +
                    $"Exception={exception}{System.Environment.NewLine}",
                    new UTF8Encoding(false));
            }
            catch (Exception writeException)
            {
                Debug.LogException(writeException);
            }

            DisposeRecorders();
            Application.Quit(1);
        }

        private void DisposeRecorders()
        {
            drawCallsRecorder.Dispose();
            setPassRecorder.Dispose();
            trianglesRecorder.Dispose();
            batchesRecorder.Dispose();
        }

        private static long ReadRecorder(ProfilerRecorder recorder)
        {
            return recorder.Valid && recorder.Count > 0 ? recorder.LastValue : -1L;
        }

        private static bool IsFinitePositive(double value)
        {
            return value > 0d && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string FormatVector(Vector3 value)
        {
            return string.Format(Invariant, "({0:F6},{1:F6},{2:F6})", value.x, value.y, value.z);
        }

        private static string BuildSyncIntervalHistogram(IEnumerable<FrameSample> samples)
        {
            return string.Join(
                ";",
                samples
                    .GroupBy(sample => sample.SyncInterval)
                    .OrderBy(group => group.Key)
                    .Select(group => $"{group.Key}:{group.Count()}"));
        }

        private static void AppendGpuDistribution(StringBuilder output, string prefix, GpuDistribution distribution)
        {
            output.AppendLine($"{prefix}MinMs={distribution.Min.ToString("F6", Invariant)}");
            output.AppendLine($"{prefix}P1Ms={distribution.P1.ToString("F6", Invariant)}");
            output.AppendLine($"{prefix}P5Ms={distribution.P5.ToString("F6", Invariant)}");
            output.AppendLine($"{prefix}P10Ms={distribution.P10.ToString("F6", Invariant)}");
            output.AppendLine($"{prefix}P25Ms={distribution.P25.ToString("F6", Invariant)}");
            output.AppendLine($"{prefix}P50Ms={distribution.P50.ToString("F6", Invariant)}");
            output.AppendLine($"{prefix}P95Ms={distribution.P95.ToString("F6", Invariant)}");
            output.AppendLine($"{prefix}MaxMs={distribution.Max.ToString("F6", Invariant)}");
        }

        private static void AppendDerivedGpuDistribution(
            StringBuilder output,
            string prefix,
            GpuDistribution terrainDistribution,
            GpuDistribution noTerrainDistribution)
        {
            output.AppendLine("DerivedGPUDistributionMethod=Terrain phase percentile minus no-terrain phase percentile at the same nearest-rank percentile; samples are not paired by frame index");
            output.AppendLine($"{prefix}MinMs={(terrainDistribution.Min - noTerrainDistribution.Min).ToString("F6", Invariant)}");
            output.AppendLine($"{prefix}P1Ms={(terrainDistribution.P1 - noTerrainDistribution.P1).ToString("F6", Invariant)}");
            output.AppendLine($"{prefix}P5Ms={(terrainDistribution.P5 - noTerrainDistribution.P5).ToString("F6", Invariant)}");
            output.AppendLine($"{prefix}P10Ms={(terrainDistribution.P10 - noTerrainDistribution.P10).ToString("F6", Invariant)}");
            output.AppendLine($"{prefix}P25Ms={(terrainDistribution.P25 - noTerrainDistribution.P25).ToString("F6", Invariant)}");
            output.AppendLine($"{prefix}P50Ms={(terrainDistribution.P50 - noTerrainDistribution.P50).ToString("F6", Invariant)}");
            output.AppendLine($"{prefix}P95Ms={(terrainDistribution.P95 - noTerrainDistribution.P95).ToString("F6", Invariant)}");
            output.AppendLine($"{prefix}MaxMs={(terrainDistribution.Max - noTerrainDistribution.Max).ToString("F6", Invariant)}");
        }

        private static string HashMatrix(Matrix4x4 matrix)
        {
            StringBuilder value = new StringBuilder();
            for (int index = 0; index < 16; ++index)
            {
                value.Append(matrix[index].ToString("R", Invariant)).Append(';');
            }

            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToString()));
            return BitConverter.ToString(hash).Replace("-", string.Empty);
        }

        private enum ShaderMode
        {
            Array,
            Legacy
        }

        private enum AnisotropyMode
        {
            Project,
            ForceOn,
            Disabled
        }

        private readonly struct RunConfiguration
        {
            public RunConfiguration(
                string label,
                string outputPath,
                int width,
                int height,
                int warmupFrames,
                int sampleFrames,
                ShaderMode shaderMode,
                FullScreenMode fullScreenMode,
                bool timerOnly)
                : this(label, outputPath, width, height, warmupFrames, sampleFrames, shaderMode,
                    fullScreenMode, timerOnly, AnisotropyMode.Project, null, EmptyTileSizeMap, false)
            {
            }

            private RunConfiguration(
                string label,
                string outputPath,
                int width,
                int height,
                int warmupFrames,
                int sampleFrames,
                ShaderMode shaderMode,
                FullScreenMode fullScreenMode,
                bool timerOnly,
                AnisotropyMode anisotropyMode,
                float? tileSizeOverride,
                IReadOnlyDictionary<string, float> tileSizeMap,
                bool stochasticEnabled)
            {
                Label = label;
                OutputPath = outputPath;
                Width = width;
                Height = height;
                WarmupFrames = warmupFrames;
                SampleFrames = sampleFrames;
                ShaderMode = shaderMode;
                RequestedFullScreenMode = fullScreenMode;
                TimerOnly = timerOnly;
                AnisotropyMode = anisotropyMode;
                TileSizeOverride = tileSizeOverride;
                TileSizeMap = tileSizeMap;
                StochasticEnabled = stochasticEnabled;
            }

            private static readonly IReadOnlyDictionary<string, float> EmptyTileSizeMap = new Dictionary<string, float>();

            public string Label { get; }
            public string OutputPath { get; }
            public int Width { get; }
            public int Height { get; }
            public int WarmupFrames { get; }
            public int SampleFrames { get; }
            public ShaderMode ShaderMode { get; }
            public FullScreenMode RequestedFullScreenMode { get; }
            public bool TimerOnly { get; }
            public AnisotropyMode AnisotropyMode { get; }
            /// <summary>Ticket 5F: null means measure whatever tileSize is baked into the six live TerrainLayer assets (the mixed 2m/4m baseline). A value overrides all six uniformly at runtime, in memory only, so before/after tileSize costs can be measured in one player build (N10).</summary>
            public float? TileSizeOverride { get; }
            /// <summary>Ticket 5F.1: per-layer name:value overrides applied on top of TileSizeOverride (or the baked value if that is null), so a split configuration can be measured in the same build. Keyed by TerrainLayer.name.</summary>
            public IReadOnlyDictionary<string, float> TileSizeMap { get; }
            /// <summary>Ticket 5F.2: enables _SOL_LANDSCAPE_STOCHASTIC on the selected material for this run.</summary>
            public bool StochasticEnabled { get; }
            public float ResolutionScale => Width / 800f;

            public static RunConfiguration Parse(string[] arguments)
            {
                string label = GetArgument(arguments, "-solPerfLabel", "unnamed");
                string output = Path.GetFullPath(GetArgument(arguments, "-solPerfOutput", "SolLandscape5A_Measurement.txt"));
                int width = ParsePositiveInt(GetArgument(arguments, "-solPerfWidth", "800"), "width");
                int height = ParsePositiveInt(GetArgument(arguments, "-solPerfHeight", "450"), "height");
                int warmup = ParsePositiveInt(GetArgument(arguments, "-solPerfWarmup", "600"), "warmup frames");
                int samples = ParsePositiveInt(GetArgument(arguments, "-solPerfSamples", "600"), "sample frames");
                if (samples < 200)
                {
                    throw new ArgumentOutOfRangeException(nameof(samples), "Ticket 5A requires at least 200 sampled frames.");
                }

                string shaderValue = GetArgument(arguments, "-solPerfShader", "array");
                ShaderMode shaderMode = string.Equals(shaderValue, "legacy", StringComparison.OrdinalIgnoreCase)
                    ? ShaderMode.Legacy
                    : string.Equals(shaderValue, "array", StringComparison.OrdinalIgnoreCase)
                        ? ShaderMode.Array
                        : throw new ArgumentException($"Unknown shader mode '{shaderValue}'.");
                string fullscreenValue = GetArgument(arguments, "-solPerfFullscreen", "windowed");
                FullScreenMode fullScreenMode = string.Equals(fullscreenValue, "exclusive", StringComparison.OrdinalIgnoreCase)
                    ? FullScreenMode.ExclusiveFullScreen
                    : string.Equals(fullscreenValue, "windowed", StringComparison.OrdinalIgnoreCase)
                        ? FullScreenMode.Windowed
                        : throw new ArgumentException($"Unknown fullscreen mode '{fullscreenValue}'. Expected exclusive or windowed.");
                bool timerOnly = ParseBoolean(GetArgument(arguments, "-solPerfTimerOnly", "false"), "timer-only mode");
                string anisotropyValue = GetArgument(arguments, "-solPerfAniso", "project");
                AnisotropyMode anisotropyMode = string.Equals(anisotropyValue, "on", StringComparison.OrdinalIgnoreCase)
                    ? AnisotropyMode.ForceOn
                    : string.Equals(anisotropyValue, "off", StringComparison.OrdinalIgnoreCase)
                        ? AnisotropyMode.Disabled
                        : string.Equals(anisotropyValue, "project", StringComparison.OrdinalIgnoreCase)
                            ? AnisotropyMode.Project
                            : throw new ArgumentException($"Unknown anisotropy mode '{anisotropyValue}'. Expected on, off, or project.");
                string tileSizeValue = GetArgument(arguments, "-solPerfTileSize", string.Empty);
                float? tileSizeOverride = null;
                if (!string.IsNullOrEmpty(tileSizeValue))
                {
                    if (!float.TryParse(tileSizeValue, NumberStyles.Float, Invariant, out float parsedTileSize) || parsedTileSize <= 0f)
                    {
                        throw new ArgumentException($"Invalid -solPerfTileSize: '{tileSizeValue}'.");
                    }

                    tileSizeOverride = parsedTileSize;
                }

                string tileSizeMapValue = GetArgument(arguments, "-solPerfTileSizeMap", string.Empty);
                var tileSizeMap = new Dictionary<string, float>();
                if (!string.IsNullOrEmpty(tileSizeMapValue))
                {
                    foreach (string entry in tileSizeMapValue.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] parts = entry.Split(':');
                        if (parts.Length != 2
                            || !float.TryParse(parts[1], NumberStyles.Float, Invariant, out float parsedLayerTileSize)
                            || parsedLayerTileSize <= 0f)
                        {
                            throw new ArgumentException($"Invalid -solPerfTileSizeMap entry: '{entry}'. Expected LayerName:value.");
                        }

                        tileSizeMap[parts[0]] = parsedLayerTileSize;
                    }
                }

                bool stochasticEnabled = ParseBoolean(GetArgument(arguments, "-solPerfStochastic", "false"), "stochastic mode");

                return new RunConfiguration(
                    label,
                    output,
                    width,
                    height,
                    warmup,
                    samples,
                    shaderMode,
                    fullScreenMode,
                    timerOnly,
                    anisotropyMode,
                    tileSizeOverride,
                    tileSizeMap,
                    stochasticEnabled);
            }

            private static bool ParseBoolean(string value, string label)
            {
                if (!bool.TryParse(value, out bool parsed))
                {
                    throw new ArgumentException($"Invalid {label}: '{value}'. Expected true or false.");
                }

                return parsed;
            }

            private static int ParsePositiveInt(string value, string label)
            {
                if (!int.TryParse(value, NumberStyles.Integer, Invariant, out int parsed) || parsed <= 0)
                {
                    throw new ArgumentException($"Invalid {label}: '{value}'.");
                }

                return parsed;
            }

            private static string GetArgument(string[] arguments, string name, string fallback)
            {
                for (int index = 0; index < arguments.Length - 1; ++index)
                {
                    if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                    {
                        return arguments[index + 1];
                    }
                }

                return fallback;
            }
        }

        private readonly struct FrameSample
        {
            public FrameSample(int index, FrameTiming timing, long drawCalls, long setPassCalls, long triangles, long batches)
            {
                Index = index;
                FrameStartTimestamp = timing.frameStartTimestamp;
                GpuFrameTime = timing.gpuFrameTime;
                CpuFrameTime = timing.cpuFrameTime;
                CpuMainThreadTime = timing.cpuMainThreadFrameTime;
                CpuRenderThreadTime = timing.cpuRenderThreadFrameTime;
                PresentWaitTime = timing.cpuMainThreadPresentWaitTime;
                SyncInterval = timing.syncInterval;
                WidthScale = timing.widthScale;
                HeightScale = timing.heightScale;
                DrawCalls = drawCalls;
                SetPassCalls = setPassCalls;
                Triangles = triangles;
                Batches = batches;
            }

            public int Index { get; }
            public ulong FrameStartTimestamp { get; }
            public double GpuFrameTime { get; }
            public double CpuFrameTime { get; }
            public double CpuMainThreadTime { get; }
            public double CpuRenderThreadTime { get; }
            public double PresentWaitTime { get; }
            public uint SyncInterval { get; }
            public float WidthScale { get; }
            public float HeightScale { get; }
            public long DrawCalls { get; }
            public long SetPassCalls { get; }
            public long Triangles { get; }
            public long Batches { get; }
        }

        private readonly struct SampleSummary
        {
            private SampleSummary(
                GpuDistribution gpuDistribution,
                double gpuMedian,
                double gpuP95,
                double cpuMedian,
                double cpuP95,
                int syncIntervalNonZero,
                double widthScaleMedian,
                double heightScaleMedian,
                double drawCallsMedian,
                double drawCallsP95,
                double setPassMedian,
                double trianglesMedian,
                double batchesMedian)
            {
                GpuDistribution = gpuDistribution;
                GpuMedian = gpuMedian;
                GpuP95 = gpuP95;
                CpuMedian = cpuMedian;
                CpuP95 = cpuP95;
                SyncIntervalNonZero = syncIntervalNonZero;
                WidthScaleMedian = widthScaleMedian;
                HeightScaleMedian = heightScaleMedian;
                DrawCallsMedian = drawCallsMedian;
                DrawCallsP95 = drawCallsP95;
                SetPassMedian = setPassMedian;
                TrianglesMedian = trianglesMedian;
                BatchesMedian = batchesMedian;
            }

            public GpuDistribution GpuDistribution { get; }
            public double GpuMedian { get; }
            public double GpuP95 { get; }
            public double CpuMedian { get; }
            public double CpuP95 { get; }
            public int SyncIntervalNonZero { get; }
            public double WidthScaleMedian { get; }
            public double HeightScaleMedian { get; }
            public double DrawCallsMedian { get; }
            public double DrawCallsP95 { get; }
            public double SetPassMedian { get; }
            public double TrianglesMedian { get; }
            public double BatchesMedian { get; }

            public static SampleSummary Create(IReadOnlyCollection<FrameSample> samples)
            {
                if (samples.Count == 0)
                {
                    throw new InvalidOperationException("Cannot summarize an empty sample set.");
                }

                double[] gpuValues = samples.Select(sample => sample.GpuFrameTime).ToArray();
                return new SampleSummary(
                    GpuDistribution.Create(gpuValues),
                    Median(gpuValues),
                    Percentile(gpuValues, 0.95d),
                    Median(samples.Select(sample => sample.CpuFrameTime)),
                    Percentile(samples.Select(sample => sample.CpuFrameTime), 0.95d),
                    samples.Count(sample => sample.SyncInterval != 0),
                    Median(samples.Select(sample => (double)sample.WidthScale)),
                    Median(samples.Select(sample => (double)sample.HeightScale)),
                    Median(samples.Select(sample => (double)sample.DrawCalls)),
                    Percentile(samples.Select(sample => (double)sample.DrawCalls), 0.95d),
                    Median(samples.Select(sample => (double)sample.SetPassCalls)),
                    Median(samples.Select(sample => (double)sample.Triangles)),
                    Median(samples.Select(sample => (double)sample.Batches)));
            }

            private static double Median(IEnumerable<double> values)
            {
                double[] sorted = values.OrderBy(value => value).ToArray();
                int middle = sorted.Length / 2;
                return sorted.Length % 2 == 0
                    ? (sorted[middle - 1] + sorted[middle]) * 0.5d
                    : sorted[middle];
            }

            private static double Percentile(IEnumerable<double> values, double percentile)
            {
                double[] sorted = values.OrderBy(value => value).ToArray();
                int index = Math.Max(0, (int)Math.Ceiling(percentile * sorted.Length) - 1);
                return sorted[index];
            }
        }

        private readonly struct GpuDistribution
        {
            private GpuDistribution(double min, double p1, double p5, double p10, double p25, double p50, double p95, double max)
            {
                Min = min;
                P1 = p1;
                P5 = p5;
                P10 = p10;
                P25 = p25;
                P50 = p50;
                P95 = p95;
                Max = max;
            }

            public double Min { get; }
            public double P1 { get; }
            public double P5 { get; }
            public double P10 { get; }
            public double P25 { get; }
            public double P50 { get; }
            public double P95 { get; }
            public double Max { get; }

            public static GpuDistribution Create(IEnumerable<double> values)
            {
                double[] sorted = values.OrderBy(value => value).ToArray();
                if (sorted.Length == 0)
                {
                    throw new InvalidOperationException("Cannot create a GPU distribution from no samples.");
                }

                return new GpuDistribution(
                    sorted[0],
                    NearestRank(sorted, 0.01d),
                    NearestRank(sorted, 0.05d),
                    NearestRank(sorted, 0.10d),
                    NearestRank(sorted, 0.25d),
                    NearestRank(sorted, 0.50d),
                    NearestRank(sorted, 0.95d),
                    sorted[sorted.Length - 1]);
            }

            private static double NearestRank(IReadOnlyList<double> sorted, double percentile)
            {
                int index = Math.Max(0, (int)Math.Ceiling(percentile * sorted.Count) - 1);
                return sorted[index];
            }
        }

        private readonly struct WeatherState : IEquatable<WeatherState>
        {
            private WeatherState(float surfaceWetness, float snowCover, float temperature, Vector4 terrainWetness, float waterLevel)
            {
                SurfaceWetness = surfaceWetness;
                SnowCover = snowCover;
                Temperature = temperature;
                TerrainWetness = terrainWetness;
                WaterLevel = waterLevel;
            }

            private float SurfaceWetness { get; }
            private float SnowCover { get; }
            private float Temperature { get; }
            private Vector4 TerrainWetness { get; }
            private float WaterLevel { get; }

            public static WeatherState Capture()
            {
                return new WeatherState(
                    Shader.GetGlobalFloat(SurfaceWetnessId),
                    Shader.GetGlobalFloat(SurfaceSnowCoverId),
                    Shader.GetGlobalFloat(SurfaceTemperatureId),
                    Shader.GetGlobalVector(TerrainWetnessId),
                    Shader.GetGlobalFloat(GlobalWaterLevelId));
            }

            public bool Equals(WeatherState other)
            {
                return SurfaceWetness.Equals(other.SurfaceWetness)
                    && SnowCover.Equals(other.SnowCover)
                    && Temperature.Equals(other.Temperature)
                    && TerrainWetness.Equals(other.TerrainWetness)
                    && WaterLevel.Equals(other.WaterLevel);
            }

            public override bool Equals(object obj)
            {
                return obj is WeatherState other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(SurfaceWetness, SnowCover, Temperature, TerrainWetness, WaterLevel);
            }

            public override string ToString()
            {
                return string.Format(
                    Invariant,
                    "SurfaceWetness:{0:F6};SnowCover:{1:F6};TemperatureC:{2:F6};TerrainWetness:({3:F6},{4:F6},{5:F6},{6:F6});WaterLevel:{7:F6}",
                    SurfaceWetness,
                    SnowCover,
                    Temperature,
                    TerrainWetness.x,
                    TerrainWetness.y,
                    TerrainWetness.z,
                    TerrainWetness.w,
                    WaterLevel);
            }
        }
    }
}
