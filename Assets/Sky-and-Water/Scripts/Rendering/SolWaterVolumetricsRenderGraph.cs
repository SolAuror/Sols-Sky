using Sol.Environment;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace Sol.Water.Rendering
{
    /// <summary>
    /// In-water volumetric scattering: half-resolution raymarch, temporal reprojection,
    /// then a body-aware spatial filter. Deliberately mirrors
    /// <c>SolAtmosphereRendererFeature.RecordVolumetric</c> rather than introducing a
    /// second volumetric architecture.
    ///
    /// Recorded inside the ocean pass, not as its own renderer feature, because it needs
    /// the water prepass and has to publish before the forward draw consumes it.
    /// </summary>
    internal static class SolWaterVolumetricsRenderGraph
    {
        const int RaymarchPassIndex = 0;
        const int TemporalPassIndex = 1;
        const int FilterPassIndex = 2;

        static readonly int PrepassDataId = Shader.PropertyToID("_SolWaterPrepassData");
        static readonly int HistoryId = Shader.PropertyToID("_SolWaterVolumetricHistory");
        static readonly int ParamsId = Shader.PropertyToID("_SolWaterVolumetricParams");
        static readonly int TurbidityId = Shader.PropertyToID("_SolWaterVolumetricTurbidity");
        static readonly int WorldOriginId = Shader.PropertyToID("_SolWaterWorldOrigin");
        static readonly int TemporalParamsId =
            Shader.PropertyToID("_SolWaterVolumetricTemporalParams");
        static readonly int PreviousViewProjectionId =
            Shader.PropertyToID("_SolWaterVolumetricPreviousViewProjection");
        static readonly int VolumetricTextureId =
            Shader.PropertyToID("_SolWaterVolumetricTexture");

        sealed class PassData
        {
            public Material Material;
            public TextureHandle Source;
            public TextureHandle PrepassData;
            public TextureHandle History;
            public Vector4 Params;
            public Vector4 Turbidity;
            public Vector4 WorldOrigin;
            public Vector4 TemporalParams;
            public Matrix4x4 PreviousViewProjection;
        }

        internal static TextureDesc CreateScaledDescriptor(TextureDesc source, float scale)
        {
            scale = Mathf.Clamp(scale, 0.1f, 1f);
            if (source.sizeMode == TextureSizeMode.Scale)
            {
                source.scale *= scale;
            }
            else if (source.sizeMode == TextureSizeMode.Explicit)
            {
                source.width = Mathf.Max(1, Mathf.CeilToInt(source.width * scale));
                source.height = Mathf.Max(1, Mathf.CeilToInt(source.height * scale));
                source.scale = Vector2.one;
            }
            else
            {
                source.sizeMode = TextureSizeMode.Scale;
                source.scale = Vector2.one * scale;
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
            source.clearBuffer = true;
            source.clearColor = Color.clear;
            return source;
        }

        /// <summary>
        /// Records the volumetric chain and publishes the result globally. Returns an
        /// invalid handle when the tier disables it, in which case the surface keeps the
        /// analytic scattering from SolWaterOptics unchanged.
        /// </summary>
        internal static TextureHandle Record(
            RenderGraph renderGraph,
            Material volumetricMaterial,
            TextureHandle cameraColor,
            TextureHandle prepassData,
            UniversalCameraData cameraData,
            SolEnvironmentCameraRegistry.Context cameraContext,
            SolWaterQualityProfile quality,
            SolWaterProfile profile)
        {
            if (volumetricMaterial == null || !prepassData.IsValid()
                || quality == null || profile == null)
                return default;

            float scale = Mathf.Clamp(quality.volumetricResolutionScale, 0.1f, 1f);
            TextureDesc sourceDesc = renderGraph.GetTextureDesc(cameraColor);
            TextureDesc scaledDesc = CreateScaledDescriptor(sourceDesc, scale);
            scaledDesc.name = "_SolWaterVolumetricRaw";
            TextureHandle raymarched = renderGraph.CreateTexture(scaledDesc);

            Vector4 volumeParams = new(
                quality.volumetricSteps,
                Mathf.Max(1f, profile.clarityDistance * 3f),
                profile.scatteringStrength,
                quality.caustics ? 1f : 0f);
            Vector4 turbidity = new(
                profile.shallowScattering.r,
                profile.shallowScattering.g,
                profile.shallowScattering.b,
                Mathf.Max(0.5f, profile.clarityDistance));

            using (IRasterRenderGraphBuilder builder =
                renderGraph.AddRasterRenderPass<PassData>(
                    "Sol Water Volumetric Raymarch", out PassData passData))
            {
                passData.Material = volumetricMaterial;
                passData.Source = cameraColor;
                passData.PrepassData = prepassData;
                passData.Params = volumeParams;
                passData.Turbidity = turbidity;
                SolDouble3 origin = SolWorldOriginService.Active?.LogicalOrigin ?? default;
                passData.WorldOrigin = new Vector4(
                    (float)origin.X, (float)origin.Y, (float)origin.Z, 0f);
                builder.UseTexture(cameraColor, AccessFlags.Read);
                builder.UseTexture(prepassData, AccessFlags.Read);
                // The caustic array is a graph global published by the caustic pass.
                builder.UseAllGlobalTextures(true);
                builder.SetRenderAttachment(raymarched, 0, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    data.Material.SetTexture(PrepassDataId, data.PrepassData);
                    data.Material.SetVector(ParamsId, data.Params);
                    data.Material.SetVector(TurbidityId, data.Turbidity);
                    data.Material.SetVector(WorldOriginId, data.WorldOrigin);
                    Blitter.BlitTexture(context.cmd, data.Source, Vector2.one,
                        data.Material, RaymarchPassIndex);
                });
            }

            TextureHandle temporalSource = raymarched;
            if (SolEnvironmentCameraRegistry.EnsureWaterVolumetricHistory(
                cameraContext, cameraData.cameraTargetDescriptor, scale))
            {
                TextureHandle history =
                    renderGraph.ImportTexture(cameraContext.WaterVolumetricHistory);
                TextureDesc temporalDesc = scaledDesc;
                temporalDesc.name = "_SolWaterVolumetricTemporal";
                TextureHandle temporal = renderGraph.CreateTexture(temporalDesc);

                using (IRasterRenderGraphBuilder builder =
                    renderGraph.AddRasterRenderPass<PassData>(
                        "Sol Water Volumetric Temporal Reprojection", out PassData passData))
                {
                    passData.Material = volumetricMaterial;
                    passData.Source = raymarched;
                    passData.PrepassData = prepassData;
                    passData.History = history;
                    passData.PreviousViewProjection = cameraContext.PreviousViewProjection;
                    passData.TemporalParams = new Vector4(
                        cameraContext.CameraCut ? 0f : 0.9f, 0.1f, 0f, 0f);
                    builder.UseTexture(raymarched, AccessFlags.Read);
                    builder.UseTexture(prepassData, AccessFlags.Read);
                    builder.UseTexture(history, AccessFlags.Read);
                    builder.SetRenderAttachment(temporal, 0, AccessFlags.Write);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                    {
                        data.Material.SetTexture(PrepassDataId, data.PrepassData);
                        data.Material.SetTexture(HistoryId, data.History);
                        data.Material.SetMatrix(PreviousViewProjectionId,
                            data.PreviousViewProjection);
                        data.Material.SetVector(TemporalParamsId, data.TemporalParams);
                        Blitter.BlitTexture(context.cmd, data.Source, Vector2.one,
                            data.Material, TemporalPassIndex);
                    });
                }

                renderGraph.AddBlitPass(temporal, history, Vector2.one, Vector2.zero,
                    passName: "Sol Water Volumetric Update History");
                temporalSource = temporal;
            }

            TextureDesc filteredDesc = scaledDesc;
            filteredDesc.name = "_SolWaterVolumetricFiltered";
            TextureHandle filtered = renderGraph.CreateTexture(filteredDesc);
            using (IRasterRenderGraphBuilder builder =
                renderGraph.AddRasterRenderPass<PassData>(
                    "Sol Water Volumetric Spatial Filter", out PassData passData))
            {
                passData.Material = volumetricMaterial;
                passData.Source = temporalSource;
                passData.PrepassData = prepassData;
                builder.UseTexture(temporalSource, AccessFlags.Read);
                builder.UseTexture(prepassData, AccessFlags.Read);
                builder.SetRenderAttachment(filtered, 0, AccessFlags.Write);
                builder.SetGlobalTextureAfterPass(filtered, VolumetricTextureId);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    data.Material.SetTexture(PrepassDataId, data.PrepassData);
                    Blitter.BlitTexture(context.cmd, data.Source, Vector2.one,
                        data.Material, FilterPassIndex);
                });
            }

            return filtered;
        }
    }
}
