using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Sol.Water.Rendering
{
    /// <summary>
    /// The ocean spectrum's persistent GPU targets.
    ///
    /// These used to be transient RenderGraph textures, which was correct only because the
    /// whole chain was re-recorded inside every camera's graph. Now that the spectrum runs
    /// once per world frame, the results have to outlive the graph that produced them: a
    /// transient texture's underlying RT goes back to the pool at the end of that camera's
    /// execution and may be handed to an unrelated pass in the next camera's graph, leaving
    /// the published globals pointing at another pass's scratch memory. Persistent handles
    /// are what make "record once, sample from every camera" legal rather than merely
    /// usually-correct.
    ///
    /// Allocation goes through RenderingUtils.ReAllocateHandleIfNeeded, which is the call the
    /// per-camera normal/foam history used before it moved here. Handles from that path carry
    /// the metadata RenderGraph wants, and staying on a proven allocation route is worth more
    /// than the marginally more direct RTHandles.Alloc over a hand-built RenderTexture.
    ///
    /// Only the caustic array was ever bound as a *render attachment*, and it is no longer
    /// allocated here -- it stayed transient and per camera. Everything in this class is
    /// written by compute and read as a sampled texture, which is the import case with the
    /// fewest sharp edges.
    /// </summary>
    internal static class SolWaterSpectralTargets
    {
        static RTHandle _displacement;
        static RTHandle _normalFoam0;
        static RTHandle _normalFoam1;
        static bool _parity;

        static int _resolution;
        static int _cascades;
        static bool _cut = true;

        internal static RTHandle Displacement => _displacement;

        /// <summary>This frame's normal/foam target -- the one Finalize writes.</summary>
        internal static RTHandle NormalFoam => _parity ? _normalFoam1 : _normalFoam0;

        /// <summary>Last frame's normal/foam result, which the temporal filter blends
        /// against.</summary>
        internal static RTHandle NormalFoamHistory => _parity ? _normalFoam0 : _normalFoam1;

        /// <summary>
        /// Exchanges the write target and the history.
        ///
        /// This replaces a full-array copy. The chain used to write one target and then blit
        /// it into a separate history, which cost a 2 MB array copy every frame at the High
        /// tier and, once the targets became imported, could not be expressed with
        /// AddBlitPass at all -- that helper reads a TextureDesc, and only transient textures
        /// have one. Two buffers and a parity flag give the same read-last-write-this
        /// semantics with no copy and no descriptor lookup.
        /// </summary>
        internal static void Swap() => _parity = !_parity;

        /// <summary>
        /// Allocates, or reuses, targets for this tier. Returns false when the layout is
        /// unusable, in which case the caller must fall back to Gerstner rather than sample
        /// whatever the previous layout left behind.
        /// </summary>
        internal static bool Ensure(int resolution, int cascades)
        {
            if (resolution <= 0 || cascades <= 0)
                return false;

            bool layoutChanged = _resolution != resolution || _cascades != cascades;
            _resolution = resolution;
            _cascades = cascades;

            bool allocated = false;
            allocated |= EnsureArray(ref _displacement, resolution, cascades,
                GraphicsFormat.R16G16B16A16_SFloat, true, "_SolWaterSpectralDisplacement");
            allocated |= EnsureArray(ref _normalFoam0, resolution, cascades,
                GraphicsFormat.R16G16B16A16_SFloat, true, "_SolWaterSpectralNormalFoamA");
            allocated |= EnsureArray(ref _normalFoam1, resolution, cascades,
                GraphicsFormat.R16G16B16A16_SFloat, true, "_SolWaterSpectralNormalFoamB");

            // Freshly allocated targets hold undefined contents, so the temporal normal/foam
            // filter must not blend against them on the first frame of a new layout.
            if (allocated || layoutChanged)
                _cut = true;

            return _displacement != null && _normalFoam0 != null && _normalFoam1 != null;
        }

        /// <summary>
        /// Reports, and clears, the "history is not trustworthy" flag.
        ///
        /// This replaces the per-camera CameraCut that used to drive the normal/foam blend.
        /// That was never a camera-shaped question: the history is an FFT-domain array, not
        /// a screen-space one, so a camera turning around does not invalidate it and moving
        /// the camera does not reproject it. What genuinely invalidates it is a change of
        /// tier or cascade layout, which is exactly what Ensure detects.
        /// </summary>
        internal static bool ConsumeCut()
        {
            bool cut = _cut;
            _cut = false;
            return cut;
        }

        /// <summary>Forces the next frame to rebuild history from scratch. Used when the
        /// wave clock jumps, where blending against the pre-jump surface would smear.</summary>
        internal static void InvalidateHistory() => _cut = true;

        internal static void Release()
        {
            _displacement?.Release();
            _normalFoam0?.Release();
            _normalFoam1?.Release();
            _displacement = null;
            _normalFoam0 = null;
            _normalFoam1 = null;
            _parity = false;
            _resolution = 0;
            _cascades = 0;
            _cut = true;
        }

        /// <summary>
        /// Describes a target for <c>RenderGraph.ImportTexture</c>.
        ///
        /// RenderGraph can infer a transient texture's shape because it allocated it; for an
        /// imported one it has only what the handle exposes. Describing the target explicitly
        /// costs nothing and removes a class of ambiguity around how the import is validated,
        /// so it is done at every import site rather than only where it is strictly needed.
        /// </summary>
        internal static RenderTargetInfo Describe(RTHandle handle)
        {
            RenderTexture texture = handle.rt;
            return new RenderTargetInfo
            {
                width = texture.width,
                height = texture.height,
                volumeDepth = texture.volumeDepth,
                msaaSamples = Mathf.Max(1, texture.antiAliasing),
                format = texture.graphicsFormat,
                bindMS = texture.bindTextureMS,
            };
        }

        static bool EnsureArray(ref RTHandle handle, int resolution, int slices,
            GraphicsFormat format, bool randomWrite, string name)
        {
            RenderTextureDescriptor descriptor = new(resolution, resolution, format, 0)
            {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = Mathf.Max(1, slices),
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
                // The finalize kernel binds all three of these as UAVs.
                enableRandomWrite = randomWrite,
                bindMS = false,
            };
            return RenderingUtils.ReAllocateHandleIfNeeded(
                ref handle, descriptor, FilterMode.Bilinear, TextureWrapMode.Repeat, name: name);
        }
    }
}
