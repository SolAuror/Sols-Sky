using Sol.Environment;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace Sol.Water.Rendering
{
    public enum SolWaterDebugMode : byte
    {
        Disabled,
        RawScreenSpaceReflections,
        ValidatedScreenSpaceReflections,
        ReflectionConfidence,
        FallbackSky,
        FoamConfidence,
        Refraction,
        Caustics,
    }

    /// <summary>
    /// Unity 6 URP RenderGraph water renderer. It owns the ocean prepass, scene-color capture,
    /// SSR resolve, instanced surface draw, and per-camera underwater composition.
    /// </summary>
    [System.Serializable]
    public sealed class SolWaterRendererFeature : ScriptableRendererFeature
    {
        [SerializeField] Shader oceanShader;
        [SerializeField] Shader resolveShader;
        [SerializeField] Shader underwaterShader;
        [SerializeField] ComputeShader fftShader;
        [SerializeField] Shader causticShader;
        [SerializeField] Shader volumetricShader;
        [SerializeField] bool renderInSceneView = true;
        [SerializeField] SolWaterDebugMode debugMode;
        [SerializeField] bool debugLog;

        Material _oceanMaterial;
        Material _resolveMaterial;
        Material _underwaterMaterial;
        Material _causticMaterial;
        Material _volumetricMaterial;
        SolWaterQualityProfile _fallbackQuality;
        SolWaterProfile _fallbackProfile;
        SolOceanClipmap _clipmap;
        SolFiniteWaterDrawSet _finiteDrawSet;
        OceanPass _oceanPass;
        UnderwaterPass _underwaterPass;

        // Keep the packed water prepass resolved and aligned with camera color. Occlusion is
        // evaluated explicitly against camera depth in the prepass shader, avoiding
        // Unity 6 Scene View's illegal resolved-color/MSAA-depth attachment pairing.
        internal static TextureDesc BuildPrepassDescriptor(TextureDesc cameraColorDesc, string name)
        {
            cameraColorDesc.name = name;
            cameraColorDesc.colorFormat = GraphicsFormat.R16G16B16A16_SFloat;
            cameraColorDesc.depthBufferBits = DepthBits.None;
            cameraColorDesc.msaaSamples = MSAASamples.None;
            cameraColorDesc.bindTextureMS = false;
            cameraColorDesc.enableRandomWrite = false;
            cameraColorDesc.clearBuffer = true;
            cameraColorDesc.clearColor = Color.clear;
            return cameraColorDesc;
        }

        public override void Create()
        {
            oceanShader = SolAssetResolver.ResolveShader(
                oceanShader, "Sol/Water2/Ocean", "SolOcean");
            resolveShader = SolAssetResolver.ResolveShader(
                resolveShader, "Hidden/Sol/Water2/Resolve", "SolWaterResolve");
            underwaterShader = SolAssetResolver.ResolveShader(
                underwaterShader, "Hidden/Sol/Water2/Underwater", "SolUnderwater");
            causticShader = SolAssetResolver.ResolveShader(
                causticShader, "Hidden/Sol/Water2/Caustic", "SolWaterCaustic");
            volumetricShader = SolAssetResolver.ResolveShader(
                volumetricShader, "Hidden/Sol/Water2/Volumetrics", "SolWaterVolumetrics");
            fftShader = SolAssetResolver.ResolveCompute(fftShader, "SolWaterFFT");

            _oceanMaterial = CreateMaterial(oceanShader, "Sol Ocean Runtime Material");
            _resolveMaterial = CreateMaterial(resolveShader, "Sol Water Resolve Runtime Material");
            _underwaterMaterial = CreateMaterial(underwaterShader, "Sol Underwater Runtime Material");
            _causticMaterial = CreateMaterial(causticShader, "Sol Water Caustic Runtime Material");
            _volumetricMaterial = CreateMaterial(volumetricShader, "Sol Water Volumetric Runtime Material");

            _fallbackQuality = ScriptableObject.CreateInstance<SolWaterQualityProfile>();
            _fallbackQuality.hideFlags = HideFlags.HideAndDontSave;
            _fallbackProfile = ScriptableObject.CreateInstance<SolWaterProfile>();
            _fallbackProfile.hideFlags = HideFlags.HideAndDontSave;
            _clipmap = new SolOceanClipmap();
            _finiteDrawSet = new SolFiniteWaterDrawSet();

            _oceanPass = new OceanPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents,
            };
            _oceanPass.ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Color);
            _underwaterPass = new UnderwaterPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing,
            };
            _underwaterPass.ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Color);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_oceanMaterial == null || _resolveMaterial == null || SolWaterWorld.Active == null
                || SolPlanarReflectionRenderer.IsRendering)
                return;
            CameraData cameraData = renderingData.cameraData;
            if (cameraData.cameraType == CameraType.Preview || cameraData.cameraType == CameraType.Reflection)
                return;
            if (cameraData.renderType == CameraRenderType.Overlay)
                return;
            if (!renderInSceneView && cameraData.isSceneViewCamera)
                return;

            _oceanPass.requiresIntermediateTexture = true;
            _oceanPass.Setup(
                _oceanMaterial,
                _resolveMaterial,
                _causticMaterial,
                _volumetricMaterial,
                fftShader,
                _clipmap,
                _finiteDrawSet,
                _fallbackQuality,
                _fallbackProfile,
                debugMode,
                debugLog);
            renderer.EnqueuePass(_oceanPass);

            if (_underwaterMaterial != null)
            {
                _underwaterPass.requiresIntermediateTexture = true;
                _underwaterPass.Setup(_underwaterMaterial, _fallbackProfile, debugLog);
                renderer.EnqueuePass(_underwaterPass);
            }
        }

        protected override void Dispose(bool disposing)
        {
            _clipmap?.Dispose();
            _clipmap = null;
            _finiteDrawSet = null;
            CoreUtils.Destroy(_oceanMaterial);
            CoreUtils.Destroy(_resolveMaterial);
            CoreUtils.Destroy(_underwaterMaterial);
            CoreUtils.Destroy(_causticMaterial);
            CoreUtils.Destroy(_volumetricMaterial);
            CoreUtils.Destroy(_fallbackQuality);
            CoreUtils.Destroy(_fallbackProfile);
            _oceanMaterial = null;
            _resolveMaterial = null;
            _underwaterMaterial = null;
            _causticMaterial = null;
            _volumetricMaterial = null;
            _fallbackQuality = null;
            SolWaterCausticRenderGraph.Release();
            SolWaterFftReadback.Release();
            _fallbackProfile = null;
            _oceanPass = null;
            _underwaterPass = null;
            SolEnvironmentCameraRegistry.Clear();
        }

        static Material CreateMaterial(Shader shader, string name)
        {
            if (shader == null)
                return null;
            Material material = CoreUtils.CreateEngineMaterial(shader);
            material.name = name;
            material.enableInstancing = true;
            return material;
        }

        sealed class OceanPass : ScriptableRenderPass
        {
            const int PrepassIndex = 0;
            const int ForwardPassIndex = 1;
            const int DepthWritePassIndex = 2;
            const int RawSsrPassIndex = 0;
            const int TemporalSsrPassIndex = 1;
            const int ValidationHistoryPassIndex = 2;
            const int AnisotropicPassIndex = 3;

            static readonly int PrepassDataId = Shader.PropertyToID("_SolWaterPrepassData");
            static readonly int SceneColorId = Shader.PropertyToID("_SolWaterSceneColor");
            static readonly int SsrTextureId = Shader.PropertyToID("_SolWaterSSRTexture");
            static readonly int AnisoSourceId = Shader.PropertyToID("_SolWaterAnisoSource");
            static readonly int VolumetricParamsId =
                Shader.PropertyToID("_SolWaterVolumetricSurfaceParams");
            static readonly int SsrRawTextureId = Shader.PropertyToID("_SolWaterSSRRawTexture");
            static readonly int SsrHistoryTextureId = Shader.PropertyToID("_SolWaterSSRHistoryTexture");
            static readonly int SsrValidationHistoryTextureId = Shader.PropertyToID("_SolWaterSSRValidationHistoryTexture");
            static readonly int SsrTraceParamsId = Shader.PropertyToID("_SolWaterSSRTraceParams");
            static readonly int SsrTemporalParamsId = Shader.PropertyToID("_SolWaterSSRTemporalParams");
            static readonly int SsrResolveParamsId = Shader.PropertyToID("_SolWaterSSRResolveParams");
            static readonly int SsrPreviousViewProjectionId = Shader.PropertyToID("_SolWaterSSRPreviousViewProjection");

            Material _oceanMaterial;
            Material _resolveMaterial;
            Material _causticMaterial;
            Material _volumetricMaterial;
            ComputeShader _fftShader;
            SolOceanClipmap _clipmap;
            SolFiniteWaterDrawSet _finiteDrawSet;
            SolWaterQualityProfile _fallbackQuality;
            SolWaterProfile _fallbackProfile;
            SolWaterDebugMode _debugMode;
            bool _debug;

            sealed class DrawPassData
            {
                internal Mesh Mesh;
                internal Matrix4x4[] Matrices;
                internal int Count;
                internal Material Material;
                internal MaterialPropertyBlock Properties;
                internal int PassIndex;
                internal TextureHandle SceneColor;
                internal TextureHandle Ssr;
            }

            sealed class SsrPassData
            {
                internal Material Material;
                internal TextureHandle SceneColor;
                internal TextureHandle Depth;
                internal TextureHandle PrepassData;
                internal TextureHandle Raw;
                internal TextureHandle History;
                internal TextureHandle ValidationHistory;
                internal Vector4 TraceParams;
                internal Vector4 TemporalParams;
                internal Vector4 ResolveParams;
                internal Matrix4x4 PreviousViewProjection;
            }

            sealed class FinitePassData
            {
                internal SolFiniteWaterDrawSet.Item[] Items;
                internal int Count;
                internal Material Material;
                internal int PassIndex;
                internal TextureHandle SceneColor;
                internal TextureHandle Ssr;
            }

            public void Setup(
                Material oceanMaterial,
                Material resolveMaterial,
                Material causticMaterial,
                Material volumetricMaterial,
                ComputeShader fftCompute,
                SolOceanClipmap clipmap,
                SolFiniteWaterDrawSet finiteDrawSet,
                SolWaterQualityProfile fallbackQuality,
                SolWaterProfile fallbackProfile,
                SolWaterDebugMode debugMode,
                bool debug)
            {
                _oceanMaterial = oceanMaterial;
                _resolveMaterial = resolveMaterial;
                _causticMaterial = causticMaterial;
                _volumetricMaterial = volumetricMaterial;
                _fftShader = fftCompute;
                _clipmap = clipmap;
                _finiteDrawSet = finiteDrawSet;
                _fallbackQuality = fallbackQuality;
                _fallbackProfile = fallbackProfile;
                _debugMode = debugMode;
                _debug = debug;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                SolWaterWorld world = SolWaterWorld.Active;
                if (world == null)
                    return;

                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (resources.isActiveTargetBackBuffer || cameraData.camera == null)
                {
                    if (_debug) Debug.LogWarning("[SolWater] Ocean pass requires an intermediate camera target.");
                    return;
                }

                TextureHandle activeColor = resources.activeColorTexture;
                TextureHandle depth = resources.activeDepthTexture;
                if (!activeColor.IsValid() || !depth.IsValid())
                    return;

                SolWaterQualityProfile quality = world.QualityProfile != null ? world.QualityProfile : _fallbackQuality;
                bool hasOcean = world.TryGetOcean(out SolWaterBody ocean);
                SolWaterProfile profile = hasOcean && ocean.Profile != null
                    ? ocean.Profile
                    : world.DefaultProfile != null ? world.DefaultProfile : _fallbackProfile;
                Vector2Int pixelSize = new(
                    Mathf.Max(1, cameraData.cameraTargetDescriptor.width),
                    Mathf.Max(1, cameraData.cameraTargetDescriptor.height));
                SolEnvironmentCameraRegistry.Context cameraContext =
                    SolEnvironmentCameraRegistry.BeginCamera(cameraData.camera, pixelSize);
                SolOceanClipmap.DrawSet drawSet = hasOcean
                    ? _clipmap.Build(cameraData.camera, ocean, quality)
                    : null;
                _finiteDrawSet.Build(cameraData.camera, world, _debugMode);
                bool drawOcean = drawSet != null && drawSet.Count > 0 && _clipmap.PatchMesh != null;
                bool drawFinite = _finiteDrawSet.Count > 0;
                if (!drawOcean && !drawFinite)
                {
                    SolEnvironmentCameraRegistry.EndCamera(cameraContext);
                    return;
                }

                ApplyMaterialState(_oceanMaterial, profile, world);
                ApplyMaterialState(_resolveMaterial, profile, world);
                _oceanMaterial.SetVector(SolWaterShaderIds.ReflectionParams,
                    new Vector4(profile.skyReflectionStrength, (float)_debugMode,
                        quality.screenSpaceReflections && quality.ssrResolutionScale > 0f ? 1f : 0f,
                        profile.reflectionIntensity));
                // Finite bodies are Gerstner-only and consume SpectralParams = zero, so
                // running the whole inverse-FFT chain for a frame that draws no ocean is
                // roughly eighteen wasted compute dispatches.
                SolWaterFftRenderGraph.SpectralResources spectral = drawOcean
                    ? SolWaterFftRenderGraph.Record(
                        renderGraph, _fftShader, quality, profile, world, cameraContext)
                    : default;
                _oceanMaterial.SetVector(SolWaterShaderIds.SpectralParams,
                    spectral.IsValid
                        ? new Vector4(quality.FftCascadeCount, profile.spectralStrength,
                            quality.FftResolution, 0f)
                        : Vector4.zero);

                // Caustics rendered from the live FFT displacement. When the tier has no
                // spectrum, or the profile disables them, the surface falls back to the
                // authored caustic texture through the same shader path.
                bool wantsCaustics = quality.caustics && spectral.IsValid
                    && profile.causticStrength > 0.0001f;
                TextureHandle causticArray = wantsCaustics
                    ? SolWaterCausticRenderGraph.Record(renderGraph, _causticMaterial,
                        spectral.Displacement, quality.FftResolution,
                        quality.FftCascadeCount, profile.spectralChoppiness)
                    : default;
                Vector4 causticArrayParams = SolWaterCausticRenderGraph.ArrayParams(
                    quality.FftCascadeCount, causticArray.IsValid());
                _oceanMaterial.SetVector(
                    SolWaterCausticRenderGraph.ArrayParamsId, causticArrayParams);
                if (SolPlanarReflectionRenderer.TryGet(cameraData.camera,
                    out RenderTexture planarTexture,
                    out Matrix4x4 planarViewProjection,
                    out int planarBodyHash,
                    out int planarAge)
                    && planarAge <= quality.planarMaximumAgeFrames)
                {
                    _oceanMaterial.SetTexture(SolWaterShaderIds.PlanarTexture, planarTexture);
                    _oceanMaterial.SetMatrix(SolWaterShaderIds.PlanarViewProjection, planarViewProjection);
                    _oceanMaterial.SetVector(SolWaterShaderIds.PlanarParams, new Vector4(
                        1f, planarBodyHash / 16777215f,
                        1f - planarAge / (float)Mathf.Max(1, quality.planarMaximumAgeFrames + 1), 0f));
                }
                else
                {
                    _oceanMaterial.SetVector(SolWaterShaderIds.PlanarParams, Vector4.zero);
                }
                TextureDesc sourceDesc = renderGraph.GetTextureDesc(activeColor);
                TextureDesc prepassDesc = BuildPrepassDescriptor(
                    sourceDesc, "_SolWaterPrepassData");
                TextureHandle prepassData = renderGraph.CreateTexture(prepassDesc);

                if (drawOcean)
                {
                    RecordOceanDraw(
                        renderGraph,
                        "Sol Ocean Prepass",
                        prepassData,
                        depth,
                        _clipmap.PatchMesh,
                        drawSet,
                        _oceanMaterial,
                        PrepassIndex);
                }
                if (drawFinite)
                {
                    RecordFiniteDraw(renderGraph, "Sol Finite Water Prepass",
                        prepassData, default, depth,
                        default, default, _finiteDrawSet, _oceanMaterial, PrepassIndex);
                }

                TextureDesc sceneDesc = sourceDesc;
                sceneDesc.name = "_SolWaterSceneColor";
                sceneDesc.msaaSamples = MSAASamples.None;
                sceneDesc.bindTextureMS = false;
                sceneDesc.clearBuffer = false;
                TextureHandle sceneColor = renderGraph.CreateTexture(sceneDesc);
                renderGraph.AddBlitPass(activeColor, sceneColor, Vector2.one, Vector2.zero,
                    passName: "Sol Water Capture Scene Color");

                int reflectionSignature = CalculateReflectionSignature(world, quality, _debugMode);
                TextureHandle ssr = renderGraph.defaultResources.blackTexture;
                if (quality.screenSpaceReflections && quality.ssrResolutionScale > 0f)
                {
                    float resolutionScale = Mathf.Clamp(quality.ssrResolutionScale, 0.2f, 1f);
                    TextureDesc rawDesc = sourceDesc;
                    rawDesc.name = "_SolWaterSSRRawTexture";
                    rawDesc.colorFormat = GraphicsFormat.R16G16B16A16_SFloat;
                    rawDesc.depthBufferBits = DepthBits.None;
                    rawDesc.msaaSamples = MSAASamples.None;
                    rawDesc.bindTextureMS = false;
                    rawDesc.sizeMode = TextureSizeMode.Scale;
                    rawDesc.scale = Vector2.one * resolutionScale;
                    rawDesc.filterMode = FilterMode.Bilinear;
                    rawDesc.clearBuffer = true;
                    rawDesc.clearColor = Color.clear;
                    TextureHandle rawSsr = renderGraph.CreateTexture(rawDesc);

                    Vector4 traceParams = new(
                        quality.ssrSteps,
                        quality.ssrMaximumDistance,
                        quality.ssrThickness,
                        quality.ssrEdgeFade);
                    RecordRawSsr(renderGraph, sceneColor, depth, prepassData,
                        rawSsr, traceParams,
                        new Vector4(quality.ssrBinarySearchSteps, 0f,
                            quality.ssrDepthTolerance, quality.ssrNormalTolerance),
                        quality.ssrMaximumLuminance);

                    TextureDesc resolvedDesc = sourceDesc;
                    resolvedDesc.name = "_SolWaterSSRTexture";
                    resolvedDesc.colorFormat = GraphicsFormat.R16G16B16A16_SFloat;
                    resolvedDesc.depthBufferBits = DepthBits.None;
                    resolvedDesc.msaaSamples = MSAASamples.None;
                    resolvedDesc.bindTextureMS = false;
                    resolvedDesc.sizeMode = TextureSizeMode.Explicit;
                    resolvedDesc.width = pixelSize.x;
                    resolvedDesc.height = pixelSize.y;
                    resolvedDesc.filterMode = FilterMode.Bilinear;
                    resolvedDesc.clearBuffer = true;
                    resolvedDesc.clearColor = Color.clear;
                    ssr = renderGraph.CreateTexture(resolvedDesc);

                    bool hasHistory = SolEnvironmentCameraRegistry.EnsureWaterReflectionHistory(
                        cameraContext, cameraData.cameraTargetDescriptor, reflectionSignature);
                    TextureHandle history = hasHistory
                        ? renderGraph.ImportTexture(cameraContext.WaterReflectionHistory)
                        : renderGraph.defaultResources.blackTexture;
                    TextureHandle validationHistory = hasHistory
                        ? renderGraph.ImportTexture(cameraContext.WaterReflectionValidationHistory)
                        : renderGraph.defaultResources.blackTexture;
                    int rawWidth = Mathf.Max(1, Mathf.CeilToInt(pixelSize.x * resolutionScale));
                    int rawHeight = Mathf.Max(1, Mathf.CeilToInt(pixelSize.y * resolutionScale));
                    Vector4 temporalParams = new(
                        quality.ssrBinarySearchSteps,
                        hasHistory && !cameraContext.CameraCut ? quality.ssrHistoryWeight : 0f,
                        quality.ssrDepthTolerance,
                        quality.ssrNormalTolerance);
                    Vector4 resolveParams = new(
                        quality.ssrMaximumLuminance,
                        (float)_debugMode,
                        1f / rawWidth,
                        1f / rawHeight);
                    RecordTemporalSsr(renderGraph, rawSsr, depth, prepassData,
                        history, validationHistory, ssr,
                        temporalParams, resolveParams, cameraContext.PreviousViewProjection);
                    if (hasHistory)
                    {
                        renderGraph.AddBlitPass(ssr, history, Vector2.one, Vector2.zero,
                            passName: "Sol Water Update SSR History");
                        RecordSsrValidationHistory(renderGraph, prepassData, validationHistory);
                    }

                    // Anisotropic smear runs after history is stored, so the widened
                    // reflection never accumulates into the temporal buffer.
                    if (quality.anisotropicReflections && profile.anisotropicReflectionScale > 0.0001f)
                    {
                        TextureDesc anisoDesc = renderGraph.GetTextureDesc(ssr);
                        anisoDesc.name = "_SolWaterSSRAnisotropic";
                        anisoDesc.clearBuffer = false;
                        TextureHandle filtered = renderGraph.CreateTexture(anisoDesc);
                        // Wind widens the angular spread of a reflected ray; the scale
                        // converts that into a viewport-relative smear.
                        float windSpeed = SolEnvironmentWorld.Active != null
                            ? SolEnvironmentWorld.Active.State.Wind.Speed
                            : 0f;
                        Vector4 anisoParams = new(
                            Mathf.Min(0.08f, windSpeed * 0.004f
                                * profile.anisotropicReflectionScale),
                            0.004f,
                            0f,
                            1f);
                        RecordAnisotropicReflections(
                            renderGraph, ssr, prepassData, filtered, anisoParams);
                        ssr = filtered;
                    }
                }
                else
                {
                    // Keep the signature moving while SSR is disabled so reenabling it
                    // cannot resurrect color/validation data from an older camera state.
                    SolEnvironmentCameraRegistry.UpdateWaterReflectionSignature(
                        cameraContext, reflectionSignature);
                }

                // Volumetric scattering needs the prepass and the caustic array, and has
                // to publish before the forward draw reads it.
                bool wantsVolumetrics = quality.volumetricWaterLighting
                    && cameraContext != null && prepassData.IsValid();
                TextureHandle volumetric = wantsVolumetrics
                    ? SolWaterVolumetricsRenderGraph.Record(
                        renderGraph, _volumetricMaterial, activeColor, prepassData,
                        cameraData, cameraContext, quality, profile)
                    : default;
                _oceanMaterial.SetVector(VolumetricParamsId,
                    new Vector4(volumetric.IsValid() ? 1f : 0f, 0f, 0f, 0f));

                if (drawOcean)
                {
                    RecordForwardDraw(
                        renderGraph,
                        activeColor,
                        sceneColor,
                        ssr,
                        _clipmap.PatchMesh,
                        drawSet,
                        _oceanMaterial);
                }
                if (drawFinite)
                {
                    RecordFiniteDraw(renderGraph, "Sol Finite Water Surface",
                        activeColor, default, depth, sceneColor, ssr,
                        _finiteDrawSet, _oceanMaterial, ForwardPassIndex);
                }

                // After the surface has shaded, so the colour pass is unaffected.
                if (quality.writeDepthForPostProcessing && drawOcean && depth.IsValid())
                    RecordDepthWrite(renderGraph, depth, _clipmap.PatchMesh, drawSet, _oceanMaterial);

                if (cameraContext != null)
                    SolEnvironmentCameraRegistry.EndCamera(cameraContext);
            }

            void RecordOceanDraw(
                RenderGraph renderGraph,
                string passName,
                TextureHandle target0,
                TextureHandle depth,
                Mesh mesh,
                SolOceanClipmap.DrawSet drawSet,
                Material material,
                int passIndex)
            {
                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<DrawPassData>(
                    passName, out DrawPassData passData);
                passData.Mesh = mesh;
                passData.Matrices = drawSet.Matrices;
                passData.Count = drawSet.Count;
                passData.Material = material;
                passData.Properties = drawSet.Properties;
                passData.PassIndex = passIndex;
                builder.SetRenderAttachment(target0, 0, AccessFlags.Write);
                builder.UseTexture(depth, AccessFlags.Read);
                builder.UseAllGlobalTextures(true);
                builder.SetGlobalTextureAfterPass(target0, PrepassDataId);
                builder.SetRenderFunc(static (DrawPassData data, RasterGraphContext context) =>
                {
                    context.cmd.DrawMeshInstanced(
                        data.Mesh, 0, data.Material, data.PassIndex,
                        data.Matrices, data.Count, data.Properties);
                });
            }

            void RecordRawSsr(
                RenderGraph renderGraph,
                TextureHandle sceneColor,
                TextureHandle depth,
                TextureHandle prepassData,
                TextureHandle target,
                Vector4 traceParams,
                Vector4 temporalParams,
                float maximumLuminance)
            {
                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<SsrPassData>(
                    "Sol Water Raw Screen Space Reflections", out SsrPassData passData);
                passData.Material = _resolveMaterial;
                passData.SceneColor = sceneColor;
                passData.Depth = depth;
                passData.PrepassData = prepassData;
                passData.TraceParams = traceParams;
                passData.TemporalParams = temporalParams;
                passData.ResolveParams = new Vector4(maximumLuminance, (float)_debugMode, 0f, 0f);
                builder.UseTexture(sceneColor, AccessFlags.Read);
                builder.UseTexture(depth, AccessFlags.Read);
                builder.UseTexture(prepassData, AccessFlags.Read);
                builder.SetRenderAttachment(target, 0, AccessFlags.Write);
                builder.SetGlobalTextureAfterPass(target, SsrRawTextureId);
                builder.SetRenderFunc(static (SsrPassData data, RasterGraphContext context) =>
                {
                    data.Material.SetTexture(SceneColorId, data.SceneColor);
                    data.Material.SetTexture(PrepassDataId, data.PrepassData);
                    data.Material.SetVector(SsrTraceParamsId, data.TraceParams);
                    data.Material.SetVector(SsrTemporalParamsId, data.TemporalParams);
                    data.Material.SetVector(SsrResolveParamsId, data.ResolveParams);
                    Blitter.BlitTexture(context.cmd, data.SceneColor, Vector2.one,
                        data.Material, RawSsrPassIndex);
                });
            }

            void RecordAnisotropicReflections(
                RenderGraph renderGraph,
                TextureHandle source,
                TextureHandle prepassData,
                TextureHandle target,
                Vector4 anisoParams)
            {
                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<SsrPassData>(
                    "Sol Water Anisotropic Reflection Filter", out SsrPassData passData);
                passData.Material = _resolveMaterial;
                passData.Raw = source;
                passData.PrepassData = prepassData;
                passData.ResolveParams = anisoParams;
                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(prepassData, AccessFlags.Read);
                builder.SetRenderAttachment(target, 0, AccessFlags.Write);
                builder.SetGlobalTextureAfterPass(target, SsrTextureId);
                builder.SetRenderFunc(static (SsrPassData data, RasterGraphContext context) =>
                {
                    data.Material.SetTexture(AnisoSourceId, data.Raw);
                    data.Material.SetTexture(PrepassDataId, data.PrepassData);
                    data.Material.SetVector(SolWaterShaderIds.AnisoParams, data.ResolveParams);
                    Blitter.BlitTexture(context.cmd, data.Raw, Vector2.one,
                        data.Material, AnisotropicPassIndex);
                });
            }

            void RecordTemporalSsr(
                RenderGraph renderGraph,
                TextureHandle raw,
                TextureHandle depth,
                TextureHandle prepassData,
                TextureHandle history,
                TextureHandle validationHistory,
                TextureHandle target,
                Vector4 temporalParams,
                Vector4 resolveParams,
                Matrix4x4 previousViewProjection)
            {
                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<SsrPassData>(
                    "Sol Water Temporal Bilateral SSR Resolve", out SsrPassData passData);
                passData.Material = _resolveMaterial;
                passData.Depth = depth;
                passData.PrepassData = prepassData;
                passData.Raw = raw;
                passData.History = history;
                passData.ValidationHistory = validationHistory;
                passData.TemporalParams = temporalParams;
                passData.ResolveParams = resolveParams;
                passData.PreviousViewProjection = previousViewProjection;
                builder.UseTexture(raw, AccessFlags.Read);
                builder.UseTexture(depth, AccessFlags.Read);
                builder.UseTexture(prepassData, AccessFlags.Read);
                builder.UseTexture(history, AccessFlags.Read);
                builder.UseTexture(validationHistory, AccessFlags.Read);
                builder.SetRenderAttachment(target, 0, AccessFlags.Write);
                builder.SetGlobalTextureAfterPass(target, SsrTextureId);
                builder.SetRenderFunc(static (SsrPassData data, RasterGraphContext context) =>
                {
                    data.Material.SetTexture(PrepassDataId, data.PrepassData);
                    data.Material.SetTexture(SsrRawTextureId, data.Raw);
                    data.Material.SetTexture(SsrHistoryTextureId, data.History);
                    data.Material.SetTexture(SsrValidationHistoryTextureId, data.ValidationHistory);
                    data.Material.SetVector(SsrTemporalParamsId, data.TemporalParams);
                    data.Material.SetVector(SsrResolveParamsId, data.ResolveParams);
                    data.Material.SetMatrix(SsrPreviousViewProjectionId, data.PreviousViewProjection);
                    Blitter.BlitTexture(context.cmd, data.Raw, Vector2.one,
                        data.Material, TemporalSsrPassIndex);
                });
            }

            void RecordSsrValidationHistory(
                RenderGraph renderGraph,
                TextureHandle prepassData,
                TextureHandle target)
            {
                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<SsrPassData>(
                    "Sol Water Update SSR Validation History", out SsrPassData passData);
                passData.Material = _resolveMaterial;
                passData.PrepassData = prepassData;
                builder.UseTexture(prepassData, AccessFlags.Read);
                builder.SetRenderAttachment(target, 0, AccessFlags.Write);
                builder.SetRenderFunc(static (SsrPassData data, RasterGraphContext context) =>
                {
                    data.Material.SetTexture(PrepassDataId, data.PrepassData);
                    Blitter.BlitTexture(context.cmd, data.PrepassData, Vector2.one,
                        data.Material, ValidationHistoryPassIndex);
                });
            }

            static int CalculateReflectionSignature(
                SolWaterWorld world,
                SolWaterQualityProfile quality,
                SolWaterDebugMode debugMode)
            {
                unchecked
                {
                    int hash = quality.screenSpaceReflections ? 1 : 0;
                    hash = hash * 397 ^ (int)debugMode;
                    hash = hash * 397 ^ quality.ssrSteps;
                    hash = hash * 397 ^ quality.ssrBinarySearchSteps;
                    hash = hash * 397 ^ quality.ssrResolutionScale.GetHashCode();
                    hash = hash * 397 ^ quality.ssrMaximumDistance.GetHashCode();
                    hash = hash * 397 ^ quality.ssrThickness.GetHashCode();
                    hash = hash * 397 ^ quality.ssrEdgeFade.GetHashCode();
                    hash = hash * 397 ^ quality.ssrHistoryWeight.GetHashCode();
                    hash = hash * 397 ^ quality.ssrDepthTolerance.GetHashCode();
                    hash = hash * 397 ^ quality.ssrNormalTolerance.GetHashCode();
                    hash = hash * 397 ^ quality.ssrMaximumLuminance.GetHashCode();
                    if (world != null)
                    {
                        System.Collections.Generic.IReadOnlyList<SolWaterBody> bodies = world.Bodies;
                        for (int i = 0; i < bodies.Count; i++)
                        {
                            SolWaterBody body = bodies[i];
                            if (body == null || !body.isActiveAndEnabled)
                                continue;
                            hash = hash * 397 ^ body.PrepassHash;
                            hash = hash * 397 ^ (int)body.BodyType;
                            hash = hash * 397 ^ body.SurfaceLevel.GetHashCode();
                        }
                    }
                    return hash;
                }
            }

            void RecordFiniteDraw(
                RenderGraph renderGraph,
                string passName,
                TextureHandle target0,
                TextureHandle target1,
                TextureHandle depth,
                TextureHandle sceneColor,
                TextureHandle ssr,
                SolFiniteWaterDrawSet drawSet,
                Material material,
                int passIndex)
            {
                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<FinitePassData>(
                    passName, out FinitePassData passData);
                passData.Items = drawSet.Items;
                passData.Count = drawSet.Count;
                passData.Material = material;
                passData.PassIndex = passIndex;
                passData.SceneColor = sceneColor;
                passData.Ssr = ssr;
                if (sceneColor.IsValid())
                    builder.UseTexture(sceneColor, AccessFlags.Read);
                if (ssr.IsValid())
                    builder.UseTexture(ssr, AccessFlags.Read);
                builder.UseAllGlobalTextures(true);
                builder.SetRenderAttachment(target0, 0,
                    AccessFlags.ReadWrite);
                if (target1.IsValid())
                    builder.SetRenderAttachment(target1, 1, AccessFlags.ReadWrite);
                if (passIndex == PrepassIndex)
                    builder.UseTexture(depth, AccessFlags.Read);
                if (passIndex == PrepassIndex)
                {
                    builder.SetGlobalTextureAfterPass(target0, PrepassDataId);
                }
                builder.SetRenderFunc(static (FinitePassData data, RasterGraphContext context) =>
                {
                    if (data.SceneColor.IsValid())
                        data.Material.SetTexture(SceneColorId, data.SceneColor);
                    if (data.Ssr.IsValid())
                        data.Material.SetTexture(SsrTextureId, data.Ssr);
                    for (int i = 0; i < data.Count; i++)
                    {
                        SolFiniteWaterDrawSet.Item item = data.Items[i];
                        context.cmd.DrawMesh(item.Mesh, item.Matrix, data.Material,
                            0, data.PassIndex, item.Properties);
                    }
                });
            }

            void RecordDepthWrite(
                RenderGraph renderGraph,
                TextureHandle depth,
                Mesh mesh,
                SolOceanClipmap.DrawSet drawSet,
                Material material)
            {
                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<DrawPassData>(
                    "Sol Water Depth Write", out DrawPassData passData);
                passData.Mesh = mesh;
                passData.Matrices = drawSet.Matrices;
                passData.Count = drawSet.Count;
                passData.Material = material;
                passData.Properties = drawSet.Properties;
                passData.PassIndex = DepthWritePassIndex;
                builder.UseAllGlobalTextures(true);
                // Depth only, no colour attachment. That is what keeps this legal beside
                // Scene View's MSAA depth target.
                builder.SetRenderAttachmentDepth(depth, AccessFlags.ReadWrite);
                builder.SetRenderFunc(static (DrawPassData data, RasterGraphContext context) =>
                {
                    context.cmd.DrawMeshInstanced(
                        data.Mesh, 0, data.Material, data.PassIndex,
                        data.Matrices, data.Count, data.Properties);
                });
            }

            void RecordForwardDraw(
                RenderGraph renderGraph,
                TextureHandle color,
                TextureHandle sceneColor,
                TextureHandle ssr,
                Mesh mesh,
                SolOceanClipmap.DrawSet drawSet,
                Material material)
            {
                using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<DrawPassData>(
                    "Sol Ocean Surface", out DrawPassData passData);
                passData.Mesh = mesh;
                passData.Matrices = drawSet.Matrices;
                passData.Count = drawSet.Count;
                passData.Material = material;
                passData.Properties = drawSet.Properties;
                passData.PassIndex = ForwardPassIndex;
                passData.SceneColor = sceneColor;
                passData.Ssr = ssr;
                builder.UseTexture(sceneColor, AccessFlags.Read);
                builder.UseTexture(ssr, AccessFlags.Read);
                builder.UseAllGlobalTextures(true);
                builder.SetRenderAttachment(color, 0, AccessFlags.ReadWrite);
                // The prepass already depth-tests the water and records the visible
                // body per pixel. Do not bind URP's depth attachment again here:
                // Scene View can expose a resolved color target beside an MSAA depth
                // target, which is not a legal native render-pass combination.
                builder.SetRenderFunc(static (DrawPassData data, RasterGraphContext context) =>
                {
                    data.Material.SetTexture(SceneColorId, data.SceneColor);
                    data.Material.SetTexture(SsrTextureId, data.Ssr);
                    context.cmd.DrawMeshInstanced(
                        data.Mesh, 0, data.Material, data.PassIndex,
                        data.Matrices, data.Count, data.Properties);
                });
            }
        }

        sealed class UnderwaterPass : ScriptableRenderPass
        {
            Material _material;
            SolWaterProfile _fallbackProfile;
            bool _debug;

            public void Setup(Material material, SolWaterProfile fallbackProfile, bool debug)
            {
                _material = material;
                _fallbackProfile = fallbackProfile;
                _debug = debug;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                SolWaterWorld world = SolWaterWorld.Active;
                if (world == null || _material == null)
                    return;

                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                Camera camera = cameraData.camera;
                if (resources.isActiveTargetBackBuffer || camera == null)
                    return;
                if (!world.TrySampleApproximate(camera.transform.position, out SolWaterSurfaceSample sample)
                    || !sample.HasWater || sample.Depth <= 0.001f)
                    return;
                if (!world.TryGetBody(sample.BodyId, out SolWaterBody body))
                    return;

                SolWaterQualityProfile quality = world.QualityProfile;
                if (quality != null && !quality.underwater)
                    return;
                SolWaterProfile profile = body.Profile != null ? body.Profile : _fallbackProfile;
                ApplyMaterialState(_material, profile, world);
                float transition = Mathf.SmoothStep(0f, 1f, sample.Depth / 0.2f);
                _material.SetVector(SolWaterShaderIds.UnderwaterParams, new Vector4(
                    profile.underwaterDensity,
                    profile.underwaterDistortion,
                    transition,
                    sample.Depth));
                _material.SetColor(SolWaterShaderIds.UnderwaterColor, profile.underwaterHaze);
                // The caustic array is published as a graph global by the ocean pass; only
                // its cascade domains need restating on this material.
                _material.SetVector(SolWaterCausticRenderGraph.ArrayParamsId,
                    SolWaterCausticRenderGraph.ArrayParams(
                        quality != null ? quality.FftCascadeCount : 0,
                        quality != null && quality.caustics
                            && profile.causticStrength > 0.0001f));

                if (SolEnvironmentCameraRegistry.TryGet(camera, out SolEnvironmentCameraRegistry.Context context))
                {
                    context.ActiveWaterBodyHash = body.PrepassHash;
                    context.UnderwaterDepth = sample.Depth;
                }

                TextureHandle activeColor = resources.activeColorTexture;
                TextureDesc copyDesc = renderGraph.GetTextureDesc(activeColor);
                copyDesc.name = "_SolUnderwaterColorCopy";
                copyDesc.clearBuffer = false;
                TextureHandle copy = renderGraph.CreateTexture(copyDesc);
                renderGraph.AddBlitPass(activeColor, copy, Vector2.one, Vector2.zero,
                    passName: "Sol Underwater Copy Color");
                RenderGraphUtils.BlitMaterialParameters parameters = new(copy, activeColor, _material, 0);
                renderGraph.AddBlitPass(parameters, passName: "Sol Underwater Composition");

                if (_debug)
                    Debug.Log($"[SolWater] Underwater {body.name}, depth {sample.Depth:F2}m", body);
            }
        }

        static void ApplyMaterialState(Material material, SolWaterProfile profile, SolWaterWorld world)
        {
            if (material == null || profile == null || world == null)
                return;
            SolEnvironmentState environment = SolEnvironmentWorld.Active != null
                ? SolEnvironmentWorld.Active.State
                : default;
            SolDouble3 origin = SolWorldOriginService.Active?.LogicalOrigin ?? default;
            material.SetFloat(SolWaterShaderIds.WaveTime, (float)world.WaveTime);
            material.SetVector(SolWaterShaderIds.WorldOrigin,
                new Vector4((float)origin.X, (float)origin.Y, (float)origin.Z, 0f));
            material.SetVector(SolWaterShaderIds.Wind,
                new Vector4(environment.Wind.Direction.x, environment.Wind.Direction.y,
                    environment.Wind.Direction.z, environment.Wind.Speed));
            material.SetVector(SolWaterShaderIds.Weather,
                new Vector4(environment.Wind.Speed, environment.Weather.WaterTurbulence,
                    environment.Weather.Rain, environment.Weather.WaveSpeedMultiplier));
            material.SetVector(SolWaterShaderIds.WeatherExtended,
                new Vector4(environment.Weather.Cloudiness * profile.cloudShadowStrength,
                    environment.Weather.Lightning * profile.lightningReflectionStrength,
                    profile.rainRoughness, profile.rainNormalStrength));
            material.SetColor(SolWaterShaderIds.ShallowColor, profile.shallowScattering);
            material.SetColor(SolWaterShaderIds.DeepColor, profile.deepScattering);
            material.SetVector(SolWaterShaderIds.Absorption, profile.absorption);
            material.SetVector(SolWaterShaderIds.Optics,
                new Vector4(profile.indexOfRefraction, profile.smoothness,
                    profile.scatteringStrength, profile.waveSpeed));
            material.SetVector(SolWaterShaderIds.SunParams,
                new Vector4(profile.sunSpecularStrength, profile.sunSpecularRoughness,
                    profile.sunSpecularMaximum, 0f));
            material.SetVector(SolWaterShaderIds.RefractionParams,
                new Vector4(profile.refractionStrength, profile.refractionMaximumDistance,
                    profile.refractionDispersion, profile.refractionMaximumScreenOffset));
            material.SetVector(SolWaterShaderIds.VisibilityParams,
                new Vector4(profile.clarityDistance, profile.underwaterDensity,
                    profile.horizonReflectionStrength, 0f));
            material.SetVector(SolWaterShaderIds.Spectrum,
                new Vector4(profile.windResponse, 0f, 0f, 0f));
            material.SetTexture(SolWaterShaderIds.ShorelineMask, profile.shorelineMask);
            material.SetVector(SolWaterShaderIds.ShorelineParams,
                new Vector4(profile.shorelineMaskMapping.x, profile.shorelineMaskMapping.y,
                    profile.shorelineMaskMapping.z, profile.shorelineMaskMapping.w));
            material.SetVector(SolWaterShaderIds.ShorelineDetail,
                new Vector4(profile.shorelineMask != null ? profile.shorelineMaskStrength : 0f,
                    profile.shorelineFoamWidth, profile.shorelineWetness,
                    profile.shorelineMask != null ? 1f : 0f));
            material.SetTexture(SolWaterShaderIds.ShorelineData,
                profile.shorelineData != null ? profile.shorelineData : Texture2D.grayTexture);
            material.SetVector(SolWaterShaderIds.ShorelineDataMapping,
                profile.shorelineDataMapping);
            material.SetVector(SolWaterShaderIds.ShorelineDataParams,
                new Vector4(profile.shorelineDepthRange, profile.shorelineDistanceRange,
                    profile.shallowWaveAttenuationDepth, profile.shorelineData != null ? 1f : 0f));
            material.SetVector(SolWaterShaderIds.ShorelineSurfaceParams,
                new Vector4(profile.shorelineContactFade,
                    profile.shorelineNormalFlattening, 0f, 0f));
            material.SetVector(SolWaterShaderIds.ShorelineBreakerParams,
                new Vector4(profile.shorelineBreakerStrength,
                    profile.shorelineBreakerWidth,
                    profile.shorelineBreakerWavelength,
                    profile.shorelineBreakerSpeed));
            material.SetVector(SolWaterShaderIds.ShorelineBreakerDetail,
                new Vector4(profile.shorelineBreakerChoppiness,
                    profile.shorelineBreakerFoam, 0f, 0f));
            material.SetColor(SolWaterShaderIds.FoamColor, profile.foamColor);
            material.SetTexture(SolWaterShaderIds.FoamTexture, profile.foamTexture);
            material.SetTexture(SolWaterShaderIds.CausticTexture,
                profile.causticTexture != null ? profile.causticTexture : Texture2D.blackTexture);
            material.SetVector(SolWaterShaderIds.FoamDetail,
                new Vector4(profile.foamTextureScale, profile.foamTextureContrast,
                    profile.foamBrightness, profile.foamTexture != null ? 1f : 0f));
            material.SetVector(SolWaterShaderIds.FoamParams,
                new Vector4(profile.crestFoamStrength, profile.shorelineFoamStrength,
                    world.QualityProfile == null || world.QualityProfile.caustics
                        ? profile.causticStrength : 0f,
                    profile.causticScale));
            material.SetVector(SolWaterShaderIds.ReflectionParams,
                new Vector4(profile.skyReflectionStrength, 0f, 0f,
                    profile.reflectionIntensity));
            SolWaterSkyReflectionState.Resolve().Apply(material);
        }
    }
}
