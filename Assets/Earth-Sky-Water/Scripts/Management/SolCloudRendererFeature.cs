using Sol.Environment;
using Sol.Lighting;
using Sol.ToD;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Curved-shell, quality-scaled volumetric clouds. The feature is ordered before Sol's
/// atmosphere feature so haze is applied to clouds, while opaque depth keeps terrain in front.
/// </summary>
[System.Serializable]
public sealed class SolCloudRendererFeature : ScriptableRendererFeature
{
    [SerializeField] Shader cloudShader;
    [SerializeField] SolCloudRenderingProfile profile;
    [SerializeField] bool renderInSceneView = true;
    [SerializeField] bool renderInReflectionCameras = true;
    [SerializeField] bool debugLog;

    Material _material;
    CloudPass _pass;

    public override void Create()
    {
        SolCloudController.Active.InvalidateHistory();
        Shader shader = SolAssetResolver.ResolveShader(
            cloudShader, "Hidden/Sol/VolumetricClouds", "SolVolumetricClouds");
        if (shader != null)
            _material = CoreUtils.CreateEngineMaterial(shader);
        _pass = new CloudPass { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents };
        _pass.ConfigureInput(ScriptableRenderPassInput.Depth);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_material == null || TimeOfDay.ResolveInstance() == null)
            return;
        CameraData cameraData = renderingData.cameraData;
        if (cameraData.cameraType == CameraType.Preview || cameraData.renderType == CameraRenderType.Overlay)
            return;
        if (cameraData.camera.orthographic)
            return;
        if (!renderInSceneView && cameraData.isSceneViewCamera)
            return;
        if (!renderInReflectionCameras && cameraData.cameraType == CameraType.Reflection)
            return;

        _pass.requiresIntermediateTexture = true;
        _pass.Setup(_material, profile, debugLog);
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        SolCloudController.Active.InvalidateHistory();
        Shader.SetGlobalFloat(CloudPass.ActiveId, 0f);
        _pass?.Cleanup();
        CoreUtils.Destroy(_material);
        _material = null;
        _pass = null;
    }

    sealed class CloudPass : ScriptableRenderPass
    {
        const int RaymarchPassIndex = 0;
        const int TemporalPassIndex = 1;
        const int CompositePassIndex = 2;
        const int ShadowPassIndex = 3;

        internal static readonly int ActiveId = Shader.PropertyToID("_SolCloudActive");
        static readonly int ShapeTextureId = Shader.PropertyToID("_SolCloudShapeTexture");
        static readonly int WeatherTextureId = Shader.PropertyToID("_SolCloudWeatherTexture");
        static readonly int ShapeVolumeId = Shader.PropertyToID("_SolCloudShapeVolume");
        static readonly int DetailVolumeId = Shader.PropertyToID("_SolCloudDetailVolume");
        static readonly int VolumeAvailableId = Shader.PropertyToID("_SolCloudVolumeAvailable");
        static readonly int HistoryTextureId = Shader.PropertyToID("_SolCloudHistoryTexture");
        static readonly int RenderTextureId = Shader.PropertyToID("_SolCloudRenderTexture");
        static readonly int PlanetId = Shader.PropertyToID("_SolCloudPlanet");
        static readonly int LayerId = Shader.PropertyToID("_SolCloudLayer");
        static readonly int ShapeId = Shader.PropertyToID("_SolCloudShape");
        static readonly int ScalesId = Shader.PropertyToID("_SolCloudScales");
        static readonly int DevelopmentId = Shader.PropertyToID("_SolCloudDevelopment");
        static readonly int WeatherOffsetId = Shader.PropertyToID("_SolCloudWeatherOffset");
        static readonly int ShapeOffsetId = Shader.PropertyToID("_SolCloudShapeOffset");
        static readonly int DetailOffsetId = Shader.PropertyToID("_SolCloudDetailOffset");
        static readonly int WindId = Shader.PropertyToID("_SolCloudWind");
        static readonly int AdvectionSpeedId = Shader.PropertyToID("_SolCloudAdvectionSpeed");
        static readonly int RenderScaleId = Shader.PropertyToID("_SolCloudRenderScale");
        static readonly int PreviousOffsetDeltaId = Shader.PropertyToID("_SolCloudPreviousOffsetDelta");
        static readonly int VariationWeightsId = Shader.PropertyToID("_SolCloudVariationWeights");
        static readonly int LightDirectionId = Shader.PropertyToID("_SolCloudLightDirection");
        static readonly int AmbientColorId = Shader.PropertyToID("_SolCloudAmbientColor");
        static readonly int OpticsId = Shader.PropertyToID("_SolCloudOptics");
        static readonly int LightingId = Shader.PropertyToID("_SolCloudLighting");
        static readonly int TemporalHorizonId = Shader.PropertyToID("_SolCloudTemporalHorizon");
        static readonly int TemporalParamsId = Shader.PropertyToID("_SolCloudTemporalParams");
        static readonly int PreviousViewProjectionId = Shader.PropertyToID("_SolCloudPreviousViewProjection");
        static readonly int AmbientGroundId = Shader.PropertyToID("_SolCloudAmbientGround");
        static readonly int FormationWeightsId = Shader.PropertyToID("_SolCloudFormationWeights");
        static readonly int SculptingId = Shader.PropertyToID("_SolCloudSculpting");
        static readonly int StructureId = Shader.PropertyToID("_SolCloudStructure");
        static readonly int TileBreakupId = Shader.PropertyToID("_SolCloudTileBreakup");
        static readonly int DefinitionId = Shader.PropertyToID("_SolCloudDefinition");
        static readonly int DistanceGainId = Shader.PropertyToID("_SolCloudDistanceGain");
        static readonly int DetailDistanceId = Shader.PropertyToID("_SolCloudDetailDistance");
        static readonly int DebugModeId = Shader.PropertyToID("_SolCloudDebugMode");
        static readonly int StormId = Shader.PropertyToID("_SolCloudStorm");
        static readonly int LightningId = Shader.PropertyToID("_SolCloudLightning");
        static readonly int LightningColorId = Shader.PropertyToID("_SolCloudLightningColor");
        static readonly int ShadowOriginId = Shader.PropertyToID("_SolCloudShadowOrigin");
        static readonly int ShadowAxisUId = Shader.PropertyToID("_SolCloudShadowAxisU");
        static readonly int ShadowAxisVId = Shader.PropertyToID("_SolCloudShadowAxisV");
        static readonly int ShadowParamsId = Shader.PropertyToID("_SolCloudShadowParams");
        internal static readonly int ShadowTextureId = Shader.PropertyToID("_SolCloudShadowTexture");
        internal static readonly int ShadowMatrixId = Shader.PropertyToID("_SolCloudShadowMatrix");
        internal static readonly int ShadowStrengthId = Shader.PropertyToID("_SolCloudShadowStrength");

        Material _material;
        SolCloudRenderingProfile _profile;
        bool _debug;
        Texture2D _fallbackShape;
        Texture2D _fallbackWeather;
        Texture3D _fallbackShapeVolume;
        Texture3D _fallbackDetailVolume;

        RTHandle _shadowMap;
        bool _hasShadowMap;
        int _shadowFrame = -1;
        float _previousLightningFlash;
        float _lightningFlashDelta;
        int _lightningFlashFrame = -1;
        int _shadowRevision = -1;
        float _shadowTime = float.NegativeInfinity;
        Vector3 _shadowAnchor;
        Vector3 _shadowLightDirection = Vector3.up;
        Vector2 _shadowShapeOffset;
        SolDouble3 _shadowWorldOrigin;
        Light _cookieLight;
        Texture _restoreCookie;
        Vector2 _restoreCookieSize = Vector2.one;
        Vector2 _restoreCookieOffset;
        Vector2 _leasedCookieSize = Vector2.one;
        Vector2 _leasedCookieOffset;
        bool _cookieLeased;

        sealed class RaymarchData
        {
            internal Material Material;
            internal TextureHandle Depth;
        }

        sealed class TemporalData
        {
            internal Material Material;
            internal TextureHandle Source;
            internal TextureHandle Depth;
            internal TextureHandle History;
        }

        sealed class CompositeData
        {
            internal Material Material;
            internal TextureHandle Depth;
            internal TextureHandle Cloud;
        }

        sealed class ShadowData
        {
            internal Material Material;
        }

        internal void Setup(Material material, SolCloudRenderingProfile renderingProfile, bool debug)
        {
            _material = material;
            _profile = renderingProfile;
            _debug = debug;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            if (resources.isActiveTargetBackBuffer)
            {
                if (_debug) Debug.LogWarning("[SolClouds] Active target is the back buffer; pass skipped.");
                return;
            }
            TextureHandle activeColor = resources.activeColorTexture;
            TextureHandle depth = resources.cameraDepthTexture;
            if (!activeColor.IsValid() || !depth.IsValid())
                return;

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            Vector2Int pixelSize = new(
                Mathf.Max(1, cameraData.cameraTargetDescriptor.width),
                Mathf.Max(1, cameraData.cameraTargetDescriptor.height));
            SolEnvironmentCameraRegistry.Context cameraContext =
                SolEnvironmentCameraRegistry.BeginCamera(cameraData.camera, pixelSize);

            SolCloudController controller = SolCloudController.Active;
            SolCloudState state = controller.Evaluate(_profile);
            SolCloudQuality quality = cameraData.cameraType == CameraType.Reflection
                ? SolCloudQuality.Low
                : controller.Quality;
            int signature = ((int)quality * 397) ^ controller.HistoryRevision
                ^ (_profile != null ? _profile.GetInstanceID() : 0);
            SetMaterialParameters(state, controller.PreviousState, quality, cameraContext);
            // The map is world space, not camera space, so it is rasterised once per frame
            // from the primary camera and then shared. Reflection probes reuse it rather than
            // regenerating a second copy at a different anchor.
            if (cameraData.cameraType != CameraType.Reflection)
                UpdateWorldShadows(renderGraph, cameraData, state, quality, controller);

            TextureDesc sourceDesc = renderGraph.GetTextureDesc(activeColor);
            float cloudRenderScale = RenderScale(quality);
            TextureDesc cloudDesc = CreateCloudResolutionDescriptor(
                sourceDesc, pixelSize, cloudRenderScale);
            int cloudWidth = Mathf.Max(1, Mathf.CeilToInt(pixelSize.x * cloudRenderScale));
            int cloudHeight = Mathf.Max(1, Mathf.CeilToInt(pixelSize.y * cloudRenderScale));
            _material.SetFloat(RenderScaleId, cloudRenderScale);
            float historyWeight = HistoryWeight(quality);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_profile != null && _profile.debugView != SolCloudDebugView.FinalLighting)
                historyWeight = 0f;
#endif
            _material.SetVector(TemporalParamsId, new Vector4(
                cameraContext.CameraCut ? 0f : historyWeight, 0.12f,
                Time.frameCount & 1023, BilateralDepthThreshold));
            TextureHandle cloud = renderGraph.CreateTexture(cloudDesc);
            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<RaymarchData>(
                "Sol Clouds Curved Shell Raymarch", out RaymarchData passData))
            {
                passData.Material = _material;
                passData.Depth = depth;
                builder.UseTexture(depth, AccessFlags.Read);
                // _SolCloudActive is consumed by transparent celestial materials later in
                // the camera. RenderGraph requires this explicit declaration before a
                // raster command buffer may mutate any global shader state.
                builder.AllowGlobalStateModification(true);
                builder.SetRenderAttachment(cloud, 0, AccessFlags.Write);
                builder.SetRenderFunc(static (RaymarchData data, RasterGraphContext context) =>
                {
                    context.cmd.SetGlobalFloat(ActiveId, 1f);
                    Blitter.BlitTexture(context.cmd, new Vector4(1f, 1f, 0f, 0f),
                        data.Material, RaymarchPassIndex);
                });
            }

            TextureHandle resolvedCloud = cloud;
            if (historyWeight > 0f && SolEnvironmentCameraRegistry.EnsureCloudHistory(
                cameraContext, cameraData.cameraTargetDescriptor,
                cloudWidth, cloudHeight, signature))
            {
                TextureHandle history = renderGraph.ImportTexture(cameraContext.CloudHistory);
                TextureDesc temporalDesc = cloudDesc;
                temporalDesc.name = "_SolCloudTemporal";
                TextureHandle temporal = renderGraph.CreateTexture(temporalDesc);
                _material.SetMatrix(PreviousViewProjectionId, cameraContext.PreviousViewProjection);
                using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<TemporalData>(
                    "Sol Clouds Temporal Reprojection", out TemporalData passData))
                {
                    passData.Material = _material;
                    passData.Source = cloud;
                    passData.Depth = depth;
                    passData.History = history;
                    builder.UseTexture(cloud, AccessFlags.Read);
                    builder.UseTexture(depth, AccessFlags.Read);
                    builder.UseTexture(history, AccessFlags.Read);
                    builder.SetRenderAttachment(temporal, 0, AccessFlags.Write);
                    builder.SetRenderFunc(static (TemporalData data, RasterGraphContext context) =>
                    {
                        data.Material.SetTexture(HistoryTextureId, data.History);
                        Blitter.BlitTexture(context.cmd, data.Source, Vector2.one,
                            data.Material, TemporalPassIndex);
                    });
                }
                renderGraph.AddBlitPass(temporal, history, Vector2.one, Vector2.zero,
                    passName: "Sol Clouds Update History");
                resolvedCloud = temporal;
            }

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<CompositeData>(
                "Sol Clouds Bilateral Composite", out CompositeData passData))
            {
                passData.Material = _material;
                passData.Depth = depth;
                passData.Cloud = resolvedCloud;
                builder.UseTexture(depth, AccessFlags.Read);
                builder.UseTexture(resolvedCloud, AccessFlags.Read);
                builder.SetRenderAttachment(activeColor, 0, AccessFlags.ReadWrite);
                builder.SetGlobalTextureAfterPass(resolvedCloud, RenderTextureId);
                builder.SetRenderFunc(static (CompositeData data, RasterGraphContext context) =>
                {
                    data.Material.SetTexture(RenderTextureId, data.Cloud);
                    Blitter.BlitTexture(context.cmd, new Vector4(1f, 1f, 0f, 0f),
                        data.Material, CompositePassIndex);
                });
            }
            SolEnvironmentCameraRegistry.EndCamera(cameraContext);
        }

        void SetMaterialParameters(in SolCloudState state, in SolCloudState previous,
            SolCloudQuality quality, SolEnvironmentCameraRegistry.Context cameraContext)
        {
            _fallbackShape ??= Resources.Load<Texture2D>("SolEnvironment/Sol_CloudNoisePacked");
            _fallbackWeather ??= Resources.Load<Texture2D>("SolEnvironment/Sol_CloudWeatherMap");
            _fallbackShapeVolume ??= Resources.Load<Texture3D>("SolEnvironment/Sol_CloudShapeVolume");
            _fallbackDetailVolume ??= Resources.Load<Texture3D>("SolEnvironment/Sol_CloudDetailVolume");
            Texture2D shapeTexture = _profile != null && _profile.authoredStructureMap != null
                ? _profile.authoredStructureMap
                : _profile != null && _profile.packedShapeNoise != null
                    ? _profile.packedShapeNoise
                    : _fallbackShape;
            Texture2D weatherTexture = _profile != null && _profile.weatherMap != null
                ? _profile.weatherMap : _fallbackWeather;
            Texture3D shapeVolume = _profile != null && _profile.shapeNoiseVolume != null
                ? _profile.shapeNoiseVolume : _fallbackShapeVolume;
            Texture3D detailVolume = _profile != null && _profile.detailNoiseVolume != null
                ? _profile.detailNoiseVolume : _fallbackDetailVolume;
            _material.SetTexture(ShapeTextureId, shapeTexture != null ? shapeTexture : Texture2D.grayTexture);
            _material.SetTexture(WeatherTextureId, weatherTexture != null ? weatherTexture : Texture2D.grayTexture);
            _material.SetTexture(ShapeVolumeId, shapeVolume);
            _material.SetTexture(DetailVolumeId, detailVolume);
            _material.SetFloat(VolumeAvailableId,
                shapeVolume != null && detailVolume != null ? 1f : 0f);

            float planetRadius = _profile != null ? _profile.planetRadius : 6371000f;
            Vector3 center = new(0f, -planetRadius, 0f);
            if (SolWorldOriginService.Active != null)
                center = SolWorldOriginService.Active.ToLocal(new SolDouble3(0d, -planetRadius, 0d));
            _material.SetVector(PlanetId, new Vector4(center.x, center.y, center.z, planetRadius));
            _material.SetVector(LayerId, new Vector4(state.BaseHeight, state.Thickness,
                _profile != null ? _profile.maximumRayDistance : 160000f, ViewSteps(quality)));
            _material.SetVector(ShapeId, new Vector4(state.Coverage, state.Erosion,
                state.Density, (float)state.DominantFormation));
            _material.SetVector(ScalesId, new Vector4(
                _profile != null ? _profile.shapeScaleMetres : 12000f,
                _profile != null ? _profile.detailScaleMetres : 1800f,
                _profile != null ? _profile.weatherScaleMetres : 85000f,
                _profile != null ? _profile.extinctionPerKilometre : 1.35f));
            _material.SetVector(DevelopmentId, new Vector4(state.VerticalDevelopment,
                state.AnvilAmount, state.CirrusAmount, state.ShadowStrength));
            _material.SetVector(WeatherOffsetId, state.WeatherOffset);
            _material.SetVector(ShapeOffsetId, state.ShapeOffset);
            _material.SetVector(DetailOffsetId, state.DetailOffset);
            float shapePeriod = _profile != null ? _profile.shapeScaleMetres : 12000f;
            Vector2 offsetDelta = SolCloudMath.RotatedPeriodicOffsetDelta(
                state.ShapeOffset, previous.ShapeOffset, shapePeriod);
            _material.SetVector(PreviousOffsetDeltaId, offsetDelta);

            SolEnvironmentState environment = SolEnvironmentWorld.ResolveState();
            Vector3 cloudWind = environment.Wind.CloudDirection;
            float advectionMultiplier = _profile != null
                ? _profile.cloudAdvectionMultiplier : 4f;
            _material.SetVector(WindId, new Vector4(
                cloudWind.x, cloudWind.y, cloudWind.z, environment.Wind.CloudSpeed));
            _material.SetFloat(AdvectionSpeedId,
                environment.Wind.CloudSpeed * advectionMultiplier);

            TimeOfDay time = TimeOfDay.ResolveInstance();
            _material.SetVector(VariationWeightsId, SolCloudMath.DailyVariationWeights(
                time != null ? time.WorldDayIndex : 0L,
                time != null ? time.ClockHour : 0f));
            SolLightingFrame lightingFrame = SolLightingDirector.ResolveFrame();
            SolSkyFrame skyFrame = time != null ? time.CurrentSkyFrame : default;
            Color ambient = skyFrame.IsValid
                ? (skyFrame.PresentedAmbientSky + skyFrame.PresentedAmbientEquator) * 0.5f
                : (RenderSettings.ambientSkyColor + RenderSettings.ambientEquatorColor) * 0.5f;
            // Only the world-shadow pass uses this direction. Cloud radiance uses
            // the independent celestial array published by SolLightingDirector.
            _material.SetVector(LightDirectionId, lightingFrame.DominantState.Direction);
            _material.SetColor(AmbientColorId, ambient);
            // The ground half of the trilight term. Ambient mode may leave this at its
            // authored value rather than a probe-derived one, which is fine: it only needs to
            // be the colour bouncing back up under the deck.
            _material.SetColor(AmbientGroundId, skyFrame.IsValid
                ? skyFrame.PresentedAmbientGround : RenderSettings.ambientGroundColor);

            _material.SetVector(FormationWeightsId, SolCloudController.Active.FormationWeights);
            _material.SetVector(SculptingId, new Vector4(
                _profile != null ? _profile.sculptingStrength : 0.35f,
                _profile != null ? _profile.baseLobeStrength : 0.3f,
                _profile != null ? _profile.multipleScatteringStrength : 0.55f,
                _profile != null ? _profile.innerGlowStrength : 0.35f));
            // Edge/base softness and coverage bias are per-frame weather channels;
            // curl and the distance gain are authored constants. They share one vector
            // because every slot of _SolCloudShape and _SolCloudSculpting is spoken for.
            _material.SetVector(DefinitionId, new Vector4(
                state.EdgeSoftness,
                state.BaseSoftness,
                state.CoverageBias,
                _profile != null ? _profile.curlWarpStrength : 0.6f));
            _material.SetFloat(DistanceGainId,
                _profile != null ? _profile.distanceDensityGain : 0.45f);
            _material.SetVector(StructureId, new Vector4(
                _profile != null ? _profile.structureScaleMetres : 8000f,
                _profile != null ? _profile.structureInfluence : 0.38f,
                _profile != null ? _profile.structureWarpStrength : 0.12f,
                _profile != null ? _profile.surfaceGradientStrength : 0.18f));
            _material.SetFloat(TileBreakupId,
                _profile != null ? _profile.volumeTilingSuppression : 0.38f);
            _material.SetVector(DetailDistanceId, new Vector4(
                _profile != null ? _profile.microDetailFadeStartMetres : 25000f,
                _profile != null ? _profile.microDetailFadeEndMetres : 45000f,
                _profile != null ? _profile.maximumLightMarchMetres : 30000f,
                quality == SolCloudQuality.Low ? 0f : 1f));
            int debugMode = 0;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            debugMode = _profile != null ? (int)_profile.debugView : 0;
#endif
            _material.SetFloat(DebugModeId, debugMode);
            SolWeatherState weather = SolWeatherManager.Instance != null
                ? SolWeatherManager.Instance.CurrentState : default;
            float ambientMultiplier = SolCloudMath.CloudAmbientMultiplier(
                weather.Dim, weather.RainIntensity, state.ShadowStrength);
            _material.SetVector(OpticsId, new Vector4(
                _profile != null ? _profile.forwardAnisotropy : 0.62f,
                _profile != null ? _profile.backwardAnisotropy : -0.2f,
                _profile != null ? _profile.backwardLobeWeight : 0.18f,
                (_profile != null ? _profile.ambientIntensity : 0.72f) * ambientMultiplier));
            _material.SetVector(LightingId, new Vector4(
                _profile != null ? _profile.powderIntensity : 0.75f,
                _profile != null ? _profile.lightStepMetres : 1400f,
                LightSteps(quality), weather.LightningFlash));
            // Storm response comes from the weather simulation: SolCloudState has no
            // precipitation field of its own.
            _material.SetVector(StormId, new Vector4(
                _profile != null ? _profile.stormAbsorption : 0.45f,
                Mathf.Clamp01(weather.RainIntensity),
                _profile != null ? _profile.secondaryCelestialStrength : 0.2f,
                SolCloudMath.CloudAmbientBottomMultiplier(
                    _profile != null ? _profile.ambientBottomMultiplier : 0.38f,
                    weather.Dim, weather.RainIntensity)));
            // Anchoring the flash to the advected weather offset makes it drift with the
            // storm cell instead of jumping around the dome between frames.
            float flashAngle = state.WeatherOffset.x * 0.0004f + state.WeatherOffset.y * 0.0007f;
            Vector3 flashDirection = new Vector3(
                Mathf.Cos(flashAngle), 0.45f, Mathf.Sin(flashAngle)).normalized;
            _material.SetVector(LightningId, new Vector4(flashDirection.x, flashDirection.y,
                flashDirection.z, _profile != null ? _profile.lightningEmissionStrength : 0.8f));
            _material.SetColor(LightningColorId, _profile != null
                ? _profile.lightningColor
                : new Color(0.78f, 0.86f, 1f));
            // Lightning is emitted inside the ray-march, so a flash enters cloud history and
            // smears across the following frames unless history is rejected while the level is
            // moving. Tracked per frame rather than per call: reflection probes and the main
            // camera both run this, and the second would otherwise see a delta of zero.
            if (_lightningFlashFrame != Time.frameCount)
            {
                _lightningFlashDelta = Mathf.Abs(
                    weather.LightningFlash - _previousLightningFlash);
                _previousLightningFlash = weather.LightningFlash;
                _lightningFlashFrame = Time.frameCount;
            }
            _material.SetVector(TemporalHorizonId, new Vector4(
                _profile != null ? _profile.temporalRejectStartMetres : 35000f,
                _profile != null ? _profile.temporalRejectEndMetres : 85000f,
                _lightningFlashDelta, 0f));
            _material.SetVector(TemporalParamsId, new Vector4(0f, 0.12f,
                Time.frameCount & 1023, BilateralDepthThreshold));
            _material.SetMatrix(PreviousViewProjectionId,
                cameraContext != null ? cameraContext.PreviousViewProjection : Matrix4x4.identity);
        }

        /// <summary>
        /// Rasterises a light-space transmittance map for the whole deck and leases it to the
        /// dominant directional light as a cookie. This is the spatial half of cloud
        /// shadowing; the global scalar dim remains as a low-frequency ambient term.
        /// </summary>
        void UpdateWorldShadows(RenderGraph renderGraph, UniversalCameraData cameraData,
            in SolCloudState state, SolCloudQuality quality, SolCloudController controller)
        {
            TimeOfDay time = TimeOfDay.ResolveInstance();
            SolLightingFrame lightingFrame = SolLightingDirector.ResolveFrame();
            Light dominant = lightingFrame.Revision > 0UL
                ? lightingFrame.DominantLight
                : time != null ? time.DominantAtmosphereLight : null;
            bool enabled = (_profile == null || _profile.enableWorldCloudShadows)
                && dominant != null
                && dominant.type == LightType.Directional
                && dominant.shadowStrength > 0.0001f
                && _material != null;
            if (!enabled)
            {
                // Guarded: without it a scene that simply has no directional light would
                // reset the globals and re-release nothing on every camera, every frame.
                if (_hasShadowMap || _cookieLeased)
                    Cleanup();
                return;
            }
            // One camera owns the anchor. With a Game and a Scene view open at once, letting
            // whichever rendered first choose it made the anchor flip between two positions
            // and retrigger the movement threshold every frame. Other cameras still read the
            // published map and cookie, they just do not re-rasterise it.
            bool ownsAnchor = cameraData.cameraType == CameraType.Game
                || (!Application.isPlaying && cameraData.cameraType == CameraType.SceneView);
            if (!ownsAnchor || _shadowFrame == Time.frameCount)
                return;
            _shadowFrame = Time.frameCount;

            int resolution = _profile != null ? _profile.ShadowResolution(quality) : 512;
            RenderTextureDescriptor descriptor = new(resolution, resolution,
                GraphicsFormat.R8_UNorm, 0)
            {
                msaaSamples = 1,
                depthStencilFormat = GraphicsFormat.None,
                useMipMap = false,
                autoGenerateMips = false,
                useDynamicScale = false,
            };
            RTHandle map = _shadowMap;
            bool reallocated = RenderingUtils.ReAllocateHandleIfNeeded(ref map, descriptor,
                FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_SolCloudShadowMap");
            _shadowMap = map;
            if (_shadowMap == null)
                return;
            if (reallocated)
                ClearShadowMapWhite(_shadowMap);

            float footprint = Mathf.Max(1000f,
                _profile != null ? _profile.ShadowFootprint(quality) : 24000f);
            Vector3 lightRight = dominant.transform.right;
            Vector3 lightUp = dominant.transform.up;
            Vector3 lightForward = dominant.transform.forward;
            Vector3 lightDirection = -lightForward;

            // Snapping the anchor to the light-space texel grid stops the map shimmering as
            // the camera moves: without it every update rasterises a field offset by a
            // fraction of a texel and the shadow edges crawl.
            Vector3 cameraPosition = cameraData.camera.transform.position;
            float texelSize = footprint / Mathf.Max(1, resolution);
            Vector3 anchor =
                lightRight * (Mathf.Round(Vector3.Dot(cameraPosition, lightRight) / texelSize) * texelSize)
                + lightUp * (Mathf.Round(Vector3.Dot(cameraPosition, lightUp) / texelSize) * texelSize)
                + lightForward * Vector3.Dot(cameraPosition, lightForward);

            SolDouble3 worldOrigin = SolWorldOriginService.Active != null
                ? SolWorldOriginService.Active.LogicalOrigin
                : default;
            float interval = 1f / Mathf.Max(0.5f,
                _profile != null ? _profile.ShadowUpdatesPerSecond(quality) : 4f);
            float anchorTolerance = footprint * 0.25f;
            bool regenerate = reallocated
                || !_hasShadowMap
                || Time.time - _shadowTime >= interval
                || Vector3.Dot(lightDirection, _shadowLightDirection) < 0.9995f
                || (anchor - _shadowAnchor).sqrMagnitude > anchorTolerance * anchorTolerance
                || !worldOrigin.Equals(_shadowWorldOrigin)
                || _shadowRevision != controller.HistoryRevision;

            float strength = Mathf.Clamp01(
                (_profile != null ? _profile.worldShadowStrength : 0.85f)
                * state.ShadowStrength
                * dominant.shadowStrength);

            if (regenerate)
            {
                int samples = _profile != null ? _profile.ShadowSamples(quality) : 6;
                _material.SetVector(ShadowOriginId, anchor);
                _material.SetVector(ShadowAxisUId, lightRight * footprint);
                _material.SetVector(ShadowAxisVId, lightUp * footprint);
                _material.SetVector(ShadowParamsId, new Vector4(
                    samples, strength, 0.05f, 0f));

                TextureHandle target = renderGraph.ImportTexture(_shadowMap);
                using (IRasterRenderGraphBuilder builder =
                    renderGraph.AddRasterRenderPass<ShadowData>(
                        "Sol Clouds World Shadow Map", out ShadowData passData))
                {
                    passData.Material = _material;
                    builder.SetRenderAttachment(target, 0, AccessFlags.Write);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (ShadowData data, RasterGraphContext context) =>
                    {
                        Blitter.BlitTexture(context.cmd, new Vector4(1f, 1f, 0f, 0f),
                            data.Material, ShadowPassIndex);
                    });
                }

                _shadowAnchor = anchor;
                _shadowLightDirection = lightDirection;
                _shadowShapeOffset = state.ShapeOffset;
                _shadowWorldOrigin = worldOrigin;
                _shadowRevision = controller.HistoryRevision;
                _shadowTime = Time.time;
                _hasShadowMap = true;
            }

            // Between rasterisations the visible clouds keep moving, so the projection is
            // advected by the same accumulated wind displacement rather than sitting still
            // and desynchronising from the deck overhead.
            float shapePeriod = _profile != null ? _profile.shapeScaleMetres : 12000f;
            Vector2 drift = SolCloudMath.RotatedPeriodicOffsetDelta(
                state.ShapeOffset, _shadowShapeOffset, shapePeriod);
            Vector3 advectedAnchor = _shadowAnchor + new Vector3(drift.x, 0f, drift.y);

            Vector3 rowU = lightRight / footprint;
            Vector3 rowV = lightUp / footprint;
            Matrix4x4 worldToShadow = Matrix4x4.identity;
            worldToShadow.SetRow(0, new Vector4(rowU.x, rowU.y, rowU.z,
                0.5f - Vector3.Dot(advectedAnchor, rowU)));
            worldToShadow.SetRow(1, new Vector4(rowV.x, rowV.y, rowV.z,
                0.5f - Vector3.Dot(advectedAnchor, rowV)));
            worldToShadow.SetRow(2, Vector4.zero);
            worldToShadow.SetRow(3, new Vector4(0f, 0f, 0f, 1f));

            // Published for shaders that cannot receive the cookie. Water reaches its main
            // light through GetMainLight(shadowCoord), the overload without a world position,
            // and URP only applies cookies in the overload that takes one.
            Shader.SetGlobalTexture(ShadowTextureId, _shadowMap);
            Shader.SetGlobalMatrix(ShadowMatrixId, worldToShadow);
            Shader.SetGlobalFloat(ShadowStrengthId, strength);

            LeaseCookie(dominant, advectedAnchor, lightRight, lightUp, footprint);
        }

        /// <summary>
        /// URP projects a directional cookie orthographically from the light's own transform,
        /// as uv = (lightSpaceXY - lightCookieOffset) / lightCookieSize + 0.5. Re-centring the
        /// map on the camera anchor is therefore an offset, not a transform move: the light
        /// belongs to TimeOfDay and must not be repositioned.
        /// </summary>
        void LeaseCookie(Light dominant, Vector3 anchor, Vector3 lightRight, Vector3 lightUp,
            float footprint)
        {
            if (!dominant.TryGetComponent(out UniversalAdditionalLightData additional))
                return;
            if (_cookieLeased && _cookieLight != dominant)
                ReleaseCookie();
            if (!_cookieLeased)
            {
                _cookieLight = dominant;
                _restoreCookie = dominant.cookie;
                _restoreCookieSize = additional.lightCookieSize;
                _restoreCookieOffset = additional.lightCookieOffset;
                _cookieLeased = true;
            }

            Vector3 scale = dominant.transform.lossyScale;
            float scaleU = Mathf.Abs(scale.x) > 0.0001f ? scale.x : 1f;
            float scaleV = Mathf.Abs(scale.y) > 0.0001f ? scale.y : 1f;
            Vector3 toAnchor = anchor - dominant.transform.position;
            dominant.cookie = _shadowMap.rt;
            additional.lightCookieSize = new Vector2(footprint / scaleU, footprint / scaleV);
            additional.lightCookieOffset = new Vector2(
                Vector3.Dot(toAnchor, lightRight) / scaleU,
                Vector3.Dot(toAnchor, lightUp) / scaleV);
            _leasedCookieSize = additional.lightCookieSize;
            _leasedCookieOffset = additional.lightCookieOffset;
        }

        void ReleaseCookie()
        {
            if (!_cookieLeased)
                return;
            if (_cookieLight != null)
            {
                bool stillOwnsTexture = _shadowMap != null
                    && _cookieLight.cookie == _shadowMap.rt;
                if (_cookieLight.TryGetComponent(out UniversalAdditionalLightData additional))
                {
                    bool stillOwnsProjection = Approximately(additional.lightCookieSize,
                            _leasedCookieSize)
                        && Approximately(additional.lightCookieOffset, _leasedCookieOffset);
                    if (stillOwnsTexture && stillOwnsProjection)
                    {
                        _cookieLight.cookie = _restoreCookie;
                        additional.lightCookieSize = _restoreCookieSize;
                        additional.lightCookieOffset = _restoreCookieOffset;
                    }
                }
                else if (stillOwnsTexture)
                    _cookieLight.cookie = _restoreCookie;
            }
            _cookieLight = null;
            _restoreCookie = null;
            _restoreCookieSize = Vector2.one;
            _restoreCookieOffset = Vector2.zero;
            _leasedCookieSize = Vector2.one;
            _leasedCookieOffset = Vector2.zero;
            _cookieLeased = false;
        }

        static bool Approximately(Vector2 a, Vector2 b)
            => (a - b).sqrMagnitude <= 0.000001f;

        static void ClearShadowMapWhite(RTHandle target)
        {
            if (target == null || target.rt == null)
                return;
            CommandBuffer command = CommandBufferPool.Get("Initialize Sol Cloud Shadow Map");
            command.SetRenderTarget(target);
            command.ClearRenderTarget(RTClearFlags.Color, Color.white, 1f, 0);
            Graphics.ExecuteCommandBuffer(command);
            CommandBufferPool.Release(command);
        }

        internal void Cleanup()
        {
            ReleaseCookie();
            _shadowMap?.Release();
            _shadowMap = null;
            _hasShadowMap = false;
            _shadowRevision = -1;
            _shadowTime = float.NegativeInfinity;
            Shader.SetGlobalFloat(ShadowStrengthId, 0f);
            Shader.SetGlobalTexture(ShadowTextureId, Texture2D.whiteTexture);
            Shader.SetGlobalMatrix(ShadowMatrixId, Matrix4x4.identity);
        }

        int ViewSteps(SolCloudQuality quality) => _profile != null
            ? _profile.ViewSteps(quality)
            : quality == SolCloudQuality.Low ? 4 : quality == SolCloudQuality.High ? 48 : 32;
        int LightSteps(SolCloudQuality quality) => _profile != null
            ? _profile.LightSteps(quality)
            : quality == SolCloudQuality.Low ? 0 : quality == SolCloudQuality.High ? 6 : 4;
        float HistoryWeight(SolCloudQuality quality) => _profile != null
            ? _profile.HistoryWeight(quality)
            : quality == SolCloudQuality.Low ? 0f : quality == SolCloudQuality.High ? 0.84f : 0.72f;
        float BilateralDepthThreshold => _profile != null ? _profile.bilateralDepthThreshold : 3f;
        float RenderScale(SolCloudQuality quality) => _profile != null
            ? _profile.RenderScale(quality)
            : quality == SolCloudQuality.Low ? 0.5f
                : quality == SolCloudQuality.High ? 0.75f : 0.6f;

        internal static TextureDesc CreateCloudResolutionDescriptor(
            TextureDesc source, Vector2Int cameraSize, float renderScale)
        {
            renderScale = Mathf.Clamp(renderScale, 0.5f, 1f);
            if (source.sizeMode == TextureSizeMode.Scale)
                source.scale *= renderScale;
            else
            {
                source.sizeMode = TextureSizeMode.Explicit;
                source.width = Mathf.Max(1, Mathf.CeilToInt(cameraSize.x * renderScale));
                source.height = Mathf.Max(1, Mathf.CeilToInt(cameraSize.y * renderScale));
                source.scale = Vector2.one;
                source.func = null;
            }
            source.msaaSamples = MSAASamples.None;
            source.bindTextureMS = false;
            source.depthBufferBits = DepthBits.None;
            source.colorFormat = GraphicsFormat.R16G16B16A16_SFloat;
            source.filterMode = FilterMode.Bilinear;
            source.wrapMode = TextureWrapMode.Clamp;
            source.clearBuffer = false;
            source.name = "_SolCloudResolution";
            return source;
        }
    }
}
