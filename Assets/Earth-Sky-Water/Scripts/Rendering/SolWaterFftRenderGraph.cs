using Sol.Environment;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;

namespace Sol.Water.Rendering
{
    /// <summary>Native RenderGraph inverse-FFT cascade recording for Medium and High water tiers.</summary>
    internal static class SolWaterFftRenderGraph
    {
        static readonly int SourceAId = Shader.PropertyToID("_SourceA");
        static readonly int SourceBId = Shader.PropertyToID("_SourceB");
        static readonly int DestinationAId = Shader.PropertyToID("_DestinationA");
        static readonly int DestinationBId = Shader.PropertyToID("_DestinationB");
        static readonly int DisplacementOutputId = Shader.PropertyToID("_DisplacementOutput");
        static readonly int NormalFoamOutputId = Shader.PropertyToID("_NormalFoamOutput");
        static readonly int PreviousNormalFoamId = Shader.PropertyToID("_PreviousNormalFoam");
        static readonly int HistoryBlendId = Shader.PropertyToID("_HistoryBlend");
        static readonly int ResolutionId = Shader.PropertyToID("_Resolution");
        static readonly int CascadeCountId = Shader.PropertyToID("_CascadeCount");
        static readonly int StageId = Shader.PropertyToID("_Stage");
        static readonly int AxisId = Shader.PropertyToID("_Axis");
        static readonly int WaveTimeId = Shader.PropertyToID("_WaveTime");
        static readonly int WindId = Shader.PropertyToID("_Wind");
        static readonly int WeatherId = Shader.PropertyToID("_Weather");
        static readonly int SpectrumParamsId = Shader.PropertyToID("_SpectrumParams");
        static readonly int SpectralDisplacementId = Shader.PropertyToID("_SolWaterSpectralDisplacement");
        static readonly int SpectralNormalFoamId = Shader.PropertyToID("_SolWaterSpectralNormalFoam");
        static readonly int ReadbackSourceId = Shader.PropertyToID("_ReadbackSource");
        static readonly int ReadbackDestinationId = Shader.PropertyToID("_ReadbackDestination");
        static readonly int ReadbackResolutionId = Shader.PropertyToID("_ReadbackResolution");

        internal readonly struct SpectralResources
        {
            internal readonly TextureHandle Displacement;
            internal readonly TextureHandle NormalFoam;
            internal bool IsValid => Displacement.IsValid() && NormalFoam.IsValid();

            internal SpectralResources(TextureHandle displacement, TextureHandle normalFoam)
            {
                Displacement = displacement;
                NormalFoam = normalFoam;
            }
        }

        sealed class PassData
        {
            internal ComputeShader Shader;
            internal int Kernel;
            internal TextureHandle SourceA;
            internal TextureHandle SourceB;
            internal TextureHandle DestinationA;
            internal TextureHandle DestinationB;
            internal TextureHandle PreviousNormalFoam;
            internal int Resolution;
            internal int Cascades;
            internal int Stage;
            internal int Axis;
            internal float WaveTime;
            internal Vector4 Wind;
            internal Vector4 Weather;
            internal Vector4 Spectrum;
            internal float HistoryBlend;
            internal bool Finalize;
        }

        /// <summary>
        /// Binds the spectral arrays as shader globals for the camera about to render.
        ///
        /// Must be called by *every* camera, not just the one that records the chain.
        /// SetGlobalTextureAfterPass only establishes a global for the remainder of the
        /// graph that declares it, so once the spectrum is recorded once per frame the
        /// cameras that skipped it have nothing bound and sample whatever was left over --
        /// which draws as a flat, wave-less sea in whichever view lost the race, alternating
        /// with a correct one in the view that won it.
        ///
        /// Safe to bind before the compute has executed: these are persistent targets, so
        /// the binding refers to the same texture the chain is about to write, and the
        /// ocean draw that samples it is ordered after that write inside the recording
        /// camera's own graph.
        /// </summary>
        internal static void PublishGlobalTextures()
        {
            RTHandle displacement = SolWaterSpectralTargets.Displacement;
            RTHandle normalFoam = SolWaterSpectralTargets.NormalFoam;
            if (displacement == null || normalFoam == null)
                return;
            Shader.SetGlobalTexture(SpectralDisplacementId, displacement);
            Shader.SetGlobalTexture(SpectralNormalFoamId, normalFoam);
        }

        /// <summary>
        /// Records one world frame's spectrum.
        ///
        /// Deliberately takes no camera: every input here is a world-level fact (wind,
        /// weather, spectrum tuning, the wave clock) and the outputs are published as shader
        /// globals that every camera samples. It used to accept a camera context purely to
        /// reach a temporal history that is itself FFT-domain rather than screen-space, and
        /// keeping that parameter is what made "once per camera" look deliberate. The caller
        /// is responsible for invoking this exactly once per frame.
        ///
        /// Note that SolWaterFftReadback.Invalidate is no longer called from the early-out
        /// here. When this ran per camera, a single camera taking the early-out wiped the
        /// world-global CPU mirror that buoyancy reads, even if another camera in the same
        /// frame had just run the full chain. That decision now belongs to SolWaterWorld,
        /// which can see the whole frame.
        /// </summary>
        internal static SpectralResources Record(
            RenderGraph renderGraph,
            ComputeShader shader,
            SolWaterQualityProfile quality,
            SolWaterProfile profile,
            SolWaterWorld world)
        {
            if (shader == null || quality == null || profile == null || world == null
                || quality.FftResolution <= 0 || quality.FftCascadeCount <= 0)
                return default;

            int resolution = quality.FftResolution;
            int cascades = quality.FftCascadeCount;
            if (!SolWaterSpectralTargets.Ensure(resolution, cascades))
                return default;
            int stages = Mathf.RoundToInt(Mathf.Log(resolution, 2f));
            TextureDesc complexDesc = new(resolution, resolution)
            {
                colorFormat = GraphicsFormat.R32G32B32A32_SFloat,
                slices = cascades,
                dimension = TextureDimension.Tex2DArray,
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                clearBuffer = false,
            };
            complexDesc.name = "_SolWaterFftA0";
            TextureHandle a0 = renderGraph.CreateTexture(complexDesc);
            complexDesc.name = "_SolWaterFftA1";
            TextureHandle a1 = renderGraph.CreateTexture(complexDesc);
            complexDesc.name = "_SolWaterFftB0";
            TextureHandle b0 = renderGraph.CreateTexture(complexDesc);
            complexDesc.name = "_SolWaterFftB1";
            TextureHandle b1 = renderGraph.CreateTexture(complexDesc);

            // ResolveState carries the fair-weather fallback, so the local 6 m/s stand-in
            // that used to live here is gone: it built a moderate sea inside culling
            // bounds the clipmap had sized for dead calm from its own 0 m/s fallback.
            SolEnvironmentState environment = SolEnvironmentWorld.ResolveState();
            Vector3 windDirection = environment.Wind.Direction.sqrMagnitude > 0.0001f
                ? environment.Wind.Direction.normalized : Vector3.right;
            float windSpeed = environment.Wind.Speed;
            Vector4 wind = new(windDirection.x, windDirection.z, windSpeed, environment.Wind.Turbulence);
            Vector4 weather = new(environment.Weather.WaterTurbulence, environment.Weather.Rain,
                Mathf.Max(0.01f, environment.Weather.WaveSpeedMultiplier), 0f);
            Vector4 spectrum = new(profile.windResponse, profile.spectralChoppiness,
                profile.spectralFoamThreshold, profile.spectralFoamGain);

            RecordPass(renderGraph, "Sol Water FFT Animate Spectrum", shader, "AnimateSpectrum",
                default, default, a0, b0, default, resolution, cascades, 0, 0,
                (float)world.WaveTime, wind, weather, spectrum, 0f, false);
            RecordPass(renderGraph, "Sol Water FFT Bit Reverse", shader, "BitReverse",
                a0, b0, a1, b1, default, resolution, cascades, stages, 0,
                0f, wind, weather, spectrum, 0f, false);

            TextureHandle sourceA = a1;
            TextureHandle sourceB = b1;
            TextureHandle destinationA = a0;
            TextureHandle destinationB = b0;
            for (int stage = 0; stage < stages; stage++)
            {
                RecordPass(renderGraph, $"Sol Water FFT Horizontal {stage + 1}", shader, "Butterfly",
                    sourceA, sourceB, destinationA, destinationB, default, resolution, cascades, stage, 0,
                    0f, wind, weather, spectrum, 0f, false);
                (sourceA, destinationA) = (destinationA, sourceA);
                (sourceB, destinationB) = (destinationB, sourceB);
            }
            for (int stage = 0; stage < stages; stage++)
            {
                RecordPass(renderGraph, $"Sol Water FFT Vertical {stage + 1}", shader, "Butterfly",
                    sourceA, sourceB, destinationA, destinationB, default, resolution, cascades, stage, 1,
                    0f, wind, weather, spectrum, 0f, false);
                (sourceA, destinationA) = (destinationA, sourceA);
                (sourceB, destinationB) = (destinationB, sourceB);
            }

            // Imported rather than created: these outlive this graph and are sampled by
            // every other camera in the frame. See SolWaterSpectralTargets for why a
            // transient texture cannot serve that role, and why each import carries an
            // explicit RenderTargetInfo rather than letting RenderGraph infer one.
            // Flip before importing: NormalFoam becomes this frame's write target and
            // NormalFoamHistory becomes last frame's result.
            SolWaterSpectralTargets.Swap();
            RTHandle displacementTarget = SolWaterSpectralTargets.Displacement;
            RTHandle normalFoamTarget = SolWaterSpectralTargets.NormalFoam;
            RTHandle historyTarget = SolWaterSpectralTargets.NormalFoamHistory;
            TextureHandle displacement = renderGraph.ImportTexture(
                displacementTarget, SolWaterSpectralTargets.Describe(displacementTarget));
            TextureHandle normalFoam = renderGraph.ImportTexture(
                normalFoamTarget, SolWaterSpectralTargets.Describe(normalFoamTarget));
            TextureHandle previousHistory = renderGraph.ImportTexture(
                historyTarget, SolWaterSpectralTargets.Describe(historyTarget));
            float historyBlend = SolWaterSpectralTargets.ConsumeCut() ? 0f : 0.85f;
            RecordPass(renderGraph, "Sol Water FFT Surface Reconstruction", shader, "Finalize",
                sourceA, sourceB, displacement, normalFoam, previousHistory,
                resolution, cascades, 0, 0, 0f, wind, weather, spectrum, historyBlend, true);
            RecordReadbackDownsample(renderGraph, shader, displacement, resolution, cascades,
                world.WaveTime);
            return new SpectralResources(displacement, normalFoam);
        }

        sealed class ReadbackPassData
        {
            internal ComputeShader Shader;
            internal int Kernel;
            internal TextureHandle Source;
            internal TextureHandle Destination;
            internal int SourceResolution;
            internal int Cascades;
        }

        /// <summary>
        /// Box-filters the displacement array into the persistent CPU-readback target.
        ///
        /// The readback request is issued for the target's <em>previous</em> contents
        /// before this pass overwrites them. That is deliberate: requesting the texture
        /// this pass is about to write would either stall or return a partially written
        /// frame, and a frame of latency is already inside the tolerance the query
        /// contract reports through SampleAgeSeconds.
        /// </summary>
        static void RecordReadbackDownsample(RenderGraph renderGraph, ComputeShader shader,
            TextureHandle displacement, int resolution, int cascades, double waveTime)
        {
            if (!displacement.IsValid() || resolution < SolWaterFftReadback.Resolution)
                return;

            RTHandle target = SolWaterFftReadback.EnsureTarget(cascades);
            if (target == null)
                return;
            SolWaterFftReadback.RequestReadback();

            // Counted here as well as in RecordPass: this pass dispatches too, and leaving
            // it out made the spectrum look one dispatch cheaper than it is.
            SolEnvironmentBudget.AddFftDispatches(1);

            using IComputeRenderGraphBuilder builder = renderGraph.AddComputePass<ReadbackPassData>(
                "Sol Water FFT Readback Downsample", out ReadbackPassData passData);
            passData.Shader = shader;
            passData.Kernel = shader.FindKernel("DownsampleForReadback");
            passData.Source = displacement;
            passData.Destination = renderGraph.ImportTexture(target);
            passData.SourceResolution = resolution;
            passData.Cascades = cascades;
            builder.UseTexture(passData.Source, AccessFlags.Read);
            builder.UseTexture(passData.Destination, AccessFlags.Write);
            // The CPU mirror feeds buoyancy, which has no camera and must keep updating on
            // frames where nothing on screen consumes the readback target.
            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (ReadbackPassData data, ComputeGraphContext context) =>
            {
                ComputeCommandBuffer command = context.cmd;
                command.SetComputeIntParam(data.Shader, ResolutionId, data.SourceResolution);
                command.SetComputeIntParam(data.Shader, CascadeCountId, data.Cascades);
                command.SetComputeIntParam(data.Shader, ReadbackResolutionId,
                    SolWaterFftReadback.Resolution);
                command.SetComputeTextureParam(data.Shader, data.Kernel,
                    ReadbackSourceId, data.Source);
                command.SetComputeTextureParam(data.Shader, data.Kernel,
                    ReadbackDestinationId, data.Destination);
                int groups = (SolWaterFftReadback.Resolution + 7) / 8;
                command.DispatchCompute(data.Shader, data.Kernel, groups, groups, data.Cascades);
            });
            SolWaterFftReadback.NotifyDownsampleRecorded(waveTime);
        }

        static void RecordPass(
            RenderGraph renderGraph, string passName, ComputeShader shader, string kernelName,
            TextureHandle sourceA, TextureHandle sourceB,
            TextureHandle destinationA, TextureHandle destinationB,
            TextureHandle previousNormalFoam,
            int resolution, int cascades, int stage, int axis,
            float waveTime, Vector4 wind, Vector4 weather, Vector4 spectrum,
            float historyBlend, bool finalize)
        {
            // One RecordPass is exactly one DispatchCompute (see the render func below), so
            // this is the honest unit for the spectrum's cost.
            SolEnvironmentBudget.AddFftDispatches(1);

            using IComputeRenderGraphBuilder builder = renderGraph.AddComputePass<PassData>(
                passName, out PassData passData);
            passData.Shader = shader;
            passData.Kernel = shader.FindKernel(kernelName);
            passData.SourceA = sourceA;
            passData.SourceB = sourceB;
            passData.DestinationA = destinationA;
            passData.DestinationB = destinationB;
            passData.PreviousNormalFoam = previousNormalFoam;
            passData.Resolution = resolution;
            passData.Cascades = cascades;
            passData.Stage = stage;
            passData.Axis = axis;
            passData.WaveTime = waveTime;
            passData.Wind = wind;
            passData.Weather = weather;
            passData.Spectrum = spectrum;
            passData.HistoryBlend = historyBlend;
            passData.Finalize = finalize;
            if (sourceA.IsValid())
                builder.UseTexture(sourceA, AccessFlags.Read);
            if (sourceB.IsValid())
                builder.UseTexture(sourceB, AccessFlags.Read);
            if (previousNormalFoam.IsValid())
                builder.UseTexture(previousNormalFoam, AccessFlags.Read);
            builder.UseTexture(destinationA, AccessFlags.Write);
            builder.UseTexture(destinationB, AccessFlags.Write);
            if (finalize)
            {
                builder.SetGlobalTextureAfterPass(destinationA, SpectralDisplacementId);
                builder.SetGlobalTextureAfterPass(destinationB, SpectralNormalFoamId);
                // The camera that owns the frame's spectrum is not necessarily one that
                // draws ocean -- a submerged camera looking straight down selects no
                // clipmap leaves. Without this, RenderGraph is free to cull the chain on
                // exactly that camera and leave every other camera in the frame sampling a
                // spectrum nothing produced.
                builder.AllowPassCulling(false);
            }
            builder.SetRenderFunc(static (PassData data, ComputeGraphContext context) =>
            {
                ComputeCommandBuffer command = context.cmd;
                command.SetComputeIntParam(data.Shader, ResolutionId, data.Resolution);
                command.SetComputeIntParam(data.Shader, CascadeCountId, data.Cascades);
                command.SetComputeIntParam(data.Shader, StageId, data.Stage);
                command.SetComputeIntParam(data.Shader, AxisId, data.Axis);
                command.SetComputeFloatParam(data.Shader, WaveTimeId, data.WaveTime);
                command.SetComputeVectorParam(data.Shader, WindId, data.Wind);
                command.SetComputeVectorParam(data.Shader, WeatherId, data.Weather);
                command.SetComputeVectorParam(data.Shader, SpectrumParamsId, data.Spectrum);
                command.SetComputeFloatParam(data.Shader, HistoryBlendId, data.HistoryBlend);
                if (data.SourceA.IsValid())
                    command.SetComputeTextureParam(data.Shader, data.Kernel, SourceAId, data.SourceA);
                if (data.SourceB.IsValid())
                    command.SetComputeTextureParam(data.Shader, data.Kernel, SourceBId, data.SourceB);
                if (data.PreviousNormalFoam.IsValid())
                    command.SetComputeTextureParam(data.Shader, data.Kernel, PreviousNormalFoamId, data.PreviousNormalFoam);
                if (data.Finalize)
                {
                    command.SetComputeTextureParam(data.Shader, data.Kernel, DisplacementOutputId, data.DestinationA);
                    command.SetComputeTextureParam(data.Shader, data.Kernel, NormalFoamOutputId, data.DestinationB);
                }
                else
                {
                    command.SetComputeTextureParam(data.Shader, data.Kernel, DestinationAId, data.DestinationA);
                    command.SetComputeTextureParam(data.Shader, data.Kernel, DestinationBId, data.DestinationB);
                }
                int groups = (data.Resolution + 7) / 8;
                command.DispatchCompute(data.Shader, data.Kernel, groups, groups, data.Cascades);
            });
        }
    }
}
