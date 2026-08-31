using Sol.Environment;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

/// <summary>URP 17 RenderGraph atmosphere with analytic and directional volumetric quality paths.</summary>
[System.Serializable]
public sealed class SolAtmosphereRendererFeature : ScriptableRendererFeature
{
    [SerializeField] Shader atmosphereShader;
    [SerializeField] bool renderInSceneView = true;
    [SerializeField] bool debugLog;

    Material _material;
    AtmospherePass _pass;

    public override void Create()
    {
        Shader shader = SolAssetResolver.ResolveShader(
            atmosphereShader, "Hidden/Sol/Atmosphere", "SolAtmosphere");
        if (shader != null)
            _material = CoreUtils.CreateEngineMaterial(shader);

        _pass = new AtmospherePass
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents,
        };
        _pass.ConfigureInput(ScriptableRenderPassInput.Depth);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_material == null || SolAtmosphereController.Active == null)
            return;

        CameraType type = renderingData.cameraData.cameraType;
        if (type == CameraType.Preview || type == CameraType.Reflection)
            return;
        if (!renderInSceneView && renderingData.cameraData.isSceneViewCamera)
            return;
        if (renderingData.cameraData.renderType == CameraRenderType.Overlay)
            return;

        _pass.requiresIntermediateTexture = true;
        _pass.SetMaterial(_material, debugLog);
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(_material);
        _material = null;
        _pass = null;
    }

    sealed class AtmospherePass : ScriptableRenderPass
    {
        const int AnalyticPassIndex = 0;
        const int RaymarchPassIndex = 1;
        const int CompositePassIndex = 2;
        const int SpatialFilterPassIndex = 3;
        const int TemporalPassIndex = 4;

        // Mirrors the `strength <= 0.001` early-out in FragSpatialFilter. Kept equal so the
        // pass is skipped exactly when the shader would have been a pass-through.
        const float SpatialFilterEpsilon = 0.001f;

        static readonly int UnderwaterFactorID = Shader.PropertyToID("_UnderwaterFactor");
        static readonly int VolumetricTextureID = Shader.PropertyToID("_SolAtmosphereVolumetricTexture");
        static readonly int HistoryTextureID = Shader.PropertyToID("_SolAtmosphereHistoryTexture");
        static readonly int PreviousViewProjectionID = Shader.PropertyToID("_SolAtmospherePreviousViewProjection");
        static readonly int TemporalParamsID = Shader.PropertyToID("_SolAtmosphereTemporalParams");

        Material _material;
        bool _debug;

        sealed class AnalyticPassData
        {
            internal Material material;
        }

        sealed class RaymarchPassData
        {
            internal TextureHandle depth;
            internal Material material;
        }

        sealed class CompositePassData
        {
            internal TextureHandle depth;
            internal TextureHandle volumetric;
            internal Material material;
        }

        sealed class FilterPassData
        {
            internal TextureHandle source;
            internal TextureHandle depth;
            internal Material material;
        }

        sealed class TemporalPassData
        {
            internal TextureHandle source;
            internal TextureHandle depth;
            internal TextureHandle history;
            internal Material material;
            internal Matrix4x4 previousViewProjection;
            internal float historyWeight;
        }

        public void SetMaterial(Material material, bool debug)
        {
            _material = material;
            _debug = debug;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_material == null || Shader.GetGlobalFloat(UnderwaterFactorID) > 0.5f)
                return;

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            if (resources.isActiveTargetBackBuffer)
            {
                if (_debug) Debug.LogWarning("[SolAtmosphere] Active target is the back buffer; pass skipped.");
                return;
            }

            TextureHandle activeColor = resources.activeColorTexture;
            TextureHandle depth = resources.cameraDepthTexture;
            if (!activeColor.IsValid() || !depth.IsValid())
                return;

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            SolAtmosphereQuality quality = SolAtmosphereController.Active.ApplyCameraOverrides(
                VolumeManager.instance.stack);
            Vector2Int pixelSize = new(
                Mathf.Max(1, cameraData.cameraTargetDescriptor.width),
                Mathf.Max(1, cameraData.cameraTargetDescriptor.height));
            SolEnvironmentCameraRegistry.Context cameraContext =
                SolEnvironmentCameraRegistry.BeginCamera(cameraData.camera, pixelSize);

            if (quality != SolAtmosphereQuality.Low)
                RecordVolumetric(renderGraph, activeColor, depth, cameraData, cameraContext, quality);
            else
                RecordAnalytic(renderGraph, activeColor, depth);

            SolEnvironmentCameraRegistry.EndCamera(cameraContext);
        }

        void RecordAnalytic(
            RenderGraph renderGraph,
            TextureHandle activeColor,
            TextureHandle depth)
        {
            using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<AnalyticPassData>(
                "Sol Atmosphere Analytic", out AnalyticPassData passData);
            passData.material = _material;
            builder.UseTexture(depth, AccessFlags.Read);
            builder.SetRenderAttachment(activeColor, 0, AccessFlags.ReadWrite);
            builder.SetRenderFunc(static (AnalyticPassData data, RasterGraphContext context) =>
            {
                Blitter.BlitTexture(context.cmd, new Vector4(1f, 1f, 0f, 0f),
                    data.material, AnalyticPassIndex);
            });
        }

        void RecordVolumetric(
            RenderGraph renderGraph,
            TextureHandle activeColor,
            TextureHandle depth,
            UniversalCameraData cameraData,
            SolEnvironmentCameraRegistry.Context cameraContext,
            SolAtmosphereQuality quality)
        {
            TextureDesc sourceDesc = renderGraph.GetTextureDesc(activeColor);
            TextureDesc halfDesc = CreateHalfResolutionDescriptor(sourceDesc);
            TextureHandle volumetric = renderGraph.CreateTexture(halfDesc);

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<RaymarchPassData>(
                "Sol Atmosphere Directional Raymarch", out RaymarchPassData passData))
            {
                passData.depth = depth;
                passData.material = _material;
                builder.UseTexture(depth, AccessFlags.Read);
                builder.UseAllGlobalTextures(true);
                builder.SetRenderAttachment(volumetric, 0, AccessFlags.Write);
                builder.SetRenderFunc(static (RaymarchPassData data, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, new Vector4(1f, 1f, 0f, 0f),
                        data.material, RaymarchPassIndex);
                });
            }

            TextureHandle temporalSource = volumetric;
            if (quality == SolAtmosphereQuality.High
                && SolEnvironmentCameraRegistry.EnsureAtmosphereHistory(
                    cameraContext, cameraData.cameraTargetDescriptor))
            {
                TextureHandle history = renderGraph.ImportTexture(cameraContext.AtmosphereHistory);
                TextureDesc temporalDesc = halfDesc;
                temporalDesc.name = "_SolAtmosphereVolumetricTemporal";
                TextureHandle temporal = renderGraph.CreateTexture(temporalDesc);
                using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<TemporalPassData>(
                    "Sol Atmosphere Temporal Reprojection", out TemporalPassData passData))
                {
                    passData.source = volumetric;
                    passData.depth = depth;
                    passData.history = history;
                    passData.material = _material;
                    passData.previousViewProjection = cameraContext.PreviousViewProjection;
                    passData.historyWeight = cameraContext.CameraCut ? 0f : 0.88f;
                    builder.UseTexture(volumetric, AccessFlags.Read);
                    builder.UseTexture(depth, AccessFlags.Read);
                    builder.UseTexture(history, AccessFlags.Read);
                    builder.SetRenderAttachment(temporal, 0, AccessFlags.Write);
                    builder.SetRenderFunc(static (TemporalPassData data, RasterGraphContext context) =>
                    {
                        data.material.SetTexture(HistoryTextureID, data.history);
                        data.material.SetMatrix(PreviousViewProjectionID, data.previousViewProjection);
                        data.material.SetVector(TemporalParamsID,
                            new Vector4(data.historyWeight, 0.1f, 0f, 0f));
                        Blitter.BlitTexture(context.cmd, data.source, Vector2.one,
                            data.material, TemporalPassIndex);
                    });
                }
                renderGraph.AddBlitPass(temporal, history, Vector2.one, Vector2.zero,
                    passName: "Sol Atmosphere Update History");
                temporalSource = temporal;
            }

            // Only High authors a non-zero filter strength. Below that the shader's own
            // `strength <= 0.001` guard returns the centre tap unchanged, so recording the
            // pass bought a half-resolution read/write and an extra render target for a
            // bit-identical result. Skipping it on the same epsilon the shader uses keeps
            // the two in agreement rather than duplicating the tier rule here.
            TextureHandle filteredVolumetric = temporalSource;
            if (SolAtmosphereController.Active.CurrentSpatialFilterStrength > SpatialFilterEpsilon)
            {
                TextureDesc filteredDesc = halfDesc;
                filteredDesc.name = "_SolAtmosphereVolumetricFiltered";
                filteredVolumetric = renderGraph.CreateTexture(filteredDesc);
                using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<FilterPassData>(
                    "Sol Atmosphere Spatial Filter", out FilterPassData passData))
                {
                    passData.source = temporalSource;
                    passData.depth = depth;
                    passData.material = _material;
                    builder.UseTexture(temporalSource, AccessFlags.Read);
                    builder.UseTexture(depth, AccessFlags.Read);
                    builder.SetRenderAttachment(filteredVolumetric, 0, AccessFlags.Write);
                    builder.SetRenderFunc(static (FilterPassData data, RasterGraphContext context) =>
                    {
                        Blitter.BlitTexture(context.cmd, data.source, Vector2.one,
                            data.material, SpatialFilterPassIndex);
                    });
                }
            }

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<CompositePassData>(
                "Sol Atmosphere Bilateral Composite", out CompositePassData passData))
            {
                passData.depth = depth;
                passData.volumetric = filteredVolumetric;
                passData.material = _material;
                builder.UseTexture(depth, AccessFlags.Read);
                builder.UseTexture(filteredVolumetric, AccessFlags.Read);
                builder.SetRenderAttachment(activeColor, 0, AccessFlags.ReadWrite);
                builder.SetRenderFunc(static (CompositePassData data, RasterGraphContext context) =>
                {
                    data.material.SetTexture(VolumetricTextureID, data.volumetric);
                    Blitter.BlitTexture(context.cmd, new Vector4(1f, 1f, 0f, 0f),
                        data.material, CompositePassIndex);
                });
            }
        }

        internal static TextureDesc CreateHalfResolutionDescriptor(TextureDesc source)
        {
            if (source.sizeMode == TextureSizeMode.Scale)
            {
                source.scale *= 0.5f;
            }
            else if (source.sizeMode == TextureSizeMode.Explicit)
            {
                source.width = Mathf.Max(1, (source.width + 1) / 2);
                source.height = Mathf.Max(1, (source.height + 1) / 2);
                source.scale = Vector2.one;
            }
            else
            {
                source.sizeMode = TextureSizeMode.Scale;
                source.scale = Vector2.one * 0.5f;
                source.func = null;
                source.width = 0;
                source.height = 0;
            }
            source.msaaSamples = MSAASamples.None;
            source.bindTextureMS = false;
            source.depthBufferBits = DepthBits.None;
            source.colorFormat = GraphicsFormat.R16G16B16A16_SFloat;
            source.filterMode = FilterMode.Bilinear;
            source.wrapMode = TextureWrapMode.Clamp;
            source.clearBuffer = false;
            source.name = "_SolAtmosphereVolumetricHalfResolution";
            return source;
        }
    }
}
