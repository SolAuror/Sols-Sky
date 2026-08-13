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
        Shader shader = atmosphereShader != null
            ? atmosphereShader
            : Resources.Load<Shader>("SolAtmosphere");
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

        static readonly int UnderwaterFactorID = Shader.PropertyToID("_UnderwaterFactor");
        static readonly int VolumetricTextureID = Shader.PropertyToID("_SolAtmosphereVolumetricTexture");

        Material _material;
        bool _debug;

        sealed class RaymarchPassData
        {
            internal TextureHandle source;
            internal TextureHandle depth;
            internal Material material;
        }

        sealed class CompositePassData
        {
            internal TextureHandle source;
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

            if (SolAtmosphereController.Active.UsesVolumetricLighting)
                RecordVolumetric(renderGraph, activeColor, depth);
            else
                RecordAnalytic(renderGraph, activeColor);
        }

        void RecordAnalytic(RenderGraph renderGraph, TextureHandle activeColor)
        {
            TextureDesc copyDesc = renderGraph.GetTextureDesc(activeColor);
            copyDesc.name = "_SolAtmosphereColorCopy";
            copyDesc.clearBuffer = false;
            TextureHandle copiedColor = renderGraph.CreateTexture(copyDesc);

            renderGraph.AddBlitPass(activeColor, copiedColor, Vector2.one, Vector2.zero,
                passName: "Sol Atmosphere Copy Color");
            RenderGraphUtils.BlitMaterialParameters parameters = new(
                copiedColor, activeColor, _material, AnalyticPassIndex);
            renderGraph.AddBlitPass(parameters, passName: "Sol Atmosphere Analytic");
        }

        void RecordVolumetric(RenderGraph renderGraph, TextureHandle activeColor, TextureHandle depth)
        {
            TextureDesc sourceDesc = renderGraph.GetTextureDesc(activeColor);
            TextureDesc halfDesc = CreateHalfResolutionDescriptor(sourceDesc);
            TextureHandle volumetric = renderGraph.CreateTexture(halfDesc);

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<RaymarchPassData>(
                "Sol Atmosphere Directional Raymarch", out RaymarchPassData passData))
            {
                passData.source = activeColor;
                passData.depth = depth;
                passData.material = _material;
                builder.UseTexture(activeColor, AccessFlags.Read);
                builder.UseTexture(depth, AccessFlags.Read);
                builder.UseAllGlobalTextures(true);
                builder.SetRenderAttachment(volumetric, 0, AccessFlags.Write);
                builder.SetRenderFunc(static (RaymarchPassData data, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, data.source, Vector2.one,
                        data.material, RaymarchPassIndex);
                });
            }

            TextureDesc filteredDesc = halfDesc;
            filteredDesc.name = "_SolAtmosphereVolumetricFiltered";
            TextureHandle filteredVolumetric = renderGraph.CreateTexture(filteredDesc);
            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<FilterPassData>(
                "Sol Atmosphere Spatial Filter", out FilterPassData passData))
            {
                passData.source = volumetric;
                passData.depth = depth;
                passData.material = _material;
                builder.UseTexture(volumetric, AccessFlags.Read);
                builder.UseTexture(depth, AccessFlags.Read);
                builder.SetRenderAttachment(filteredVolumetric, 0, AccessFlags.Write);
                builder.SetRenderFunc(static (FilterPassData data, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, data.source, Vector2.one,
                        data.material, SpatialFilterPassIndex);
                });
            }

            TextureDesc copyDesc = sourceDesc;
            copyDesc.name = "_SolAtmosphereVolumetricColorCopy";
            copyDesc.clearBuffer = false;
            TextureHandle copiedColor = renderGraph.CreateTexture(copyDesc);
            renderGraph.AddBlitPass(activeColor, copiedColor, Vector2.one, Vector2.zero,
                passName: "Sol Atmosphere Copy Color");

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<CompositePassData>(
                "Sol Atmosphere Bilateral Composite", out CompositePassData passData))
            {
                passData.source = copiedColor;
                passData.depth = depth;
                passData.volumetric = filteredVolumetric;
                passData.material = _material;
                builder.UseTexture(copiedColor, AccessFlags.Read);
                builder.UseTexture(depth, AccessFlags.Read);
                builder.UseTexture(filteredVolumetric, AccessFlags.Read);
                builder.SetRenderAttachment(activeColor, 0, AccessFlags.Write);
                builder.SetRenderFunc(static (CompositePassData data, RasterGraphContext context) =>
                {
                    data.material.SetTexture(VolumetricTextureID, data.volumetric);
                    Blitter.BlitTexture(context.cmd, data.source, Vector2.one,
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
