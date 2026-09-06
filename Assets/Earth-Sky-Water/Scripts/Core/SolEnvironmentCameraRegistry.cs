using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Sol.Environment
{
    /// <summary>Per-camera state shared by atmosphere, water, reflections, and underwater rendering.</summary>
    public static class SolEnvironmentCameraRegistry
    {
        public sealed class Context : IDisposable
        {
            public Camera Camera { get; internal set; }
            public int CameraId { get; internal set; }
            public int LastFrameUsed { get; internal set; }
            public bool CameraCut { get; internal set; }
            public bool IsReflection { get; internal set; }
            public Vector2Int PixelSize { get; internal set; }
            public Matrix4x4 PreviousViewProjection { get; internal set; }
            public Matrix4x4 CurrentViewProjection { get; internal set; }
            public Vector3 PreviousPosition { get; internal set; }
            public Quaternion PreviousRotation { get; internal set; }
            public float PreviousFieldOfView { get; internal set; }
            public int ActiveWaterBodyHash { get; set; }
            public float UnderwaterDepth { get; set; }
            public bool PlanarReflectionConsumed { get; set; }
            public RTHandle AtmosphereHistory { get; set; }
            public RTHandle CloudHistory { get; set; }
            public int CloudHistorySignature { get; internal set; }
            public RTHandle WaterVolumetricHistory { get; set; }
            public RTHandle WaterReflectionHistory { get; set; }
            public RTHandle WaterReflectionValidationHistory { get; set; }
            public int WaterReflectionSignature { get; internal set; }

            internal bool Initialized;

            public void Dispose()
            {
                AtmosphereHistory?.Release();
                CloudHistory?.Release();
                WaterVolumetricHistory?.Release();
                WaterReflectionHistory?.Release();
                WaterReflectionValidationHistory?.Release();
                AtmosphereHistory = null;
                CloudHistory = null;
                WaterVolumetricHistory = null;
                WaterReflectionHistory = null;
                WaterReflectionValidationHistory = null;
                Camera = null;
            }
        }

        static readonly Dictionary<int, Context> Contexts = new(8);
        static readonly List<int> StaleIds = new(8);

        public static event Action<Context> ContextRemoved;

        public static int Count => Contexts.Count;

        public static Context BeginCamera(Camera camera, Vector2Int pixelSize)
        {
            if (camera == null)
                return null;

            int id = camera.GetInstanceID();
            if (!Contexts.TryGetValue(id, out Context context))
            {
                context = new Context { Camera = camera, CameraId = id };
                Contexts.Add(id, context);
            }
            else if (context.LastFrameUsed == Time.frameCount)
            {
                return context;
            }

            Matrix4x4 viewProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true)
                * camera.worldToCameraMatrix;
            Vector3 position = camera.transform.position;
            Quaternion rotation = camera.transform.rotation;
            float fieldOfView = camera.fieldOfView;
            bool sizeChanged = context.PixelSize != pixelSize;
            bool transformCut = context.Initialized
                && ((position - context.PreviousPosition).sqrMagnitude > 625f
                    || Quaternion.Angle(rotation, context.PreviousRotation) > 55f
                    || Mathf.Abs(fieldOfView - context.PreviousFieldOfView) > 10f);

            Matrix4x4 previousViewProjection = context.Initialized
                ? context.CurrentViewProjection
                : viewProjection;
            context.Camera = camera;
            context.LastFrameUsed = Time.frameCount;
            context.CameraCut = !context.Initialized || sizeChanged || transformCut;
            context.IsReflection = camera.cameraType == CameraType.Reflection;
            context.PixelSize = pixelSize;
            context.PreviousViewProjection = previousViewProjection;
            context.CurrentViewProjection = viewProjection;
            context.PlanarReflectionConsumed = false;

            // Commit transform state at the beginning of a new camera frame. Multiple
            // renderer features can share this context without overwriting the previous
            // frame matrix before later temporal passes consume it.
            context.PreviousPosition = position;
            context.PreviousRotation = rotation;
            context.PreviousFieldOfView = fieldOfView;
            context.Initialized = true;

            return context;
        }

        public static void EndCamera(Context context)
        {
            if (context == null || context.Camera == null)
                return;

            // Camera state is committed by BeginCamera once per frame. This method remains
            // as the public lifecycle counterpart and for compatibility with existing users.
        }

        public static bool TryGet(Camera camera, out Context context)
        {
            context = null;
            return camera != null && Contexts.TryGetValue(camera.GetInstanceID(), out context);
        }

        public static bool EnsureAtmosphereHistory(Context context, RenderTextureDescriptor cameraDescriptor)
        {
            if (context == null)
                return false;
            cameraDescriptor.width = Mathf.Max(1, (cameraDescriptor.width + 1) / 2);
            cameraDescriptor.height = Mathf.Max(1, (cameraDescriptor.height + 1) / 2);
            cameraDescriptor.msaaSamples = 1;
            cameraDescriptor.depthBufferBits = 0;
            cameraDescriptor.depthStencilFormat = GraphicsFormat.None;
            cameraDescriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
            cameraDescriptor.bindMS = false;
            cameraDescriptor.useDynamicScale = false;
            cameraDescriptor.useMipMap = false;
            cameraDescriptor.autoGenerateMips = false;
            cameraDescriptor.enableRandomWrite = false;
            RTHandle history = context.AtmosphereHistory;
            bool allocated = RenderingUtils.ReAllocateHandleIfNeeded(
                ref history,
                cameraDescriptor,
                FilterMode.Bilinear,
                TextureWrapMode.Clamp,
                name: $"_SolAtmosphereHistory_{context.CameraId}");
            context.AtmosphereHistory = history;
            if (allocated)
                context.CameraCut = true;
            return context.AtmosphereHistory != null;
        }

        /// <summary>Half-resolution radiance/transmittance history for volumetric clouds.</summary>
        public static bool EnsureCloudHistory(Context context,
            RenderTextureDescriptor cameraDescriptor, int signature)
        {
            if (context == null)
                return false;
            cameraDescriptor.width = Mathf.Max(1, (cameraDescriptor.width + 1) / 2);
            cameraDescriptor.height = Mathf.Max(1, (cameraDescriptor.height + 1) / 2);
            cameraDescriptor.msaaSamples = 1;
            cameraDescriptor.depthBufferBits = 0;
            cameraDescriptor.depthStencilFormat = GraphicsFormat.None;
            cameraDescriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
            cameraDescriptor.bindMS = false;
            cameraDescriptor.useDynamicScale = false;
            cameraDescriptor.useMipMap = false;
            cameraDescriptor.autoGenerateMips = false;
            cameraDescriptor.enableRandomWrite = false;
            RTHandle history = context.CloudHistory;
            bool allocated = RenderingUtils.ReAllocateHandleIfNeeded(
                ref history, cameraDescriptor, FilterMode.Bilinear, TextureWrapMode.Clamp,
                name: $"_SolCloudHistory_{context.CameraId}");
            context.CloudHistory = history;
            if (allocated || context.CloudHistorySignature != signature)
            {
                context.CloudHistorySignature = signature;
                context.CameraCut = true;
            }
            return context.CloudHistory != null;
        }

        /// <summary>
        /// Temporal history for the water volumetric raymarch. Mirrors the atmosphere
        /// history: half resolution, per camera, and reallocation forces a camera cut so
        /// stale scattering cannot bleed across a resize.
        /// </summary>
        public static bool EnsureWaterVolumetricHistory(Context context,
            RenderTextureDescriptor cameraDescriptor, float resolutionScale)
        {
            if (context == null)
                return false;
            resolutionScale = Mathf.Clamp(resolutionScale, 0.1f, 1f);
            cameraDescriptor.width = Mathf.Max(1,
                Mathf.CeilToInt(cameraDescriptor.width * resolutionScale));
            cameraDescriptor.height = Mathf.Max(1,
                Mathf.CeilToInt(cameraDescriptor.height * resolutionScale));
            cameraDescriptor.msaaSamples = 1;
            cameraDescriptor.depthBufferBits = 0;
            cameraDescriptor.depthStencilFormat = GraphicsFormat.None;
            cameraDescriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
            cameraDescriptor.bindMS = false;
            cameraDescriptor.useDynamicScale = false;
            cameraDescriptor.useMipMap = false;
            cameraDescriptor.autoGenerateMips = false;
            cameraDescriptor.enableRandomWrite = false;
            RTHandle history = context.WaterVolumetricHistory;
            bool allocated = RenderingUtils.ReAllocateHandleIfNeeded(
                ref history,
                cameraDescriptor,
                FilterMode.Bilinear,
                TextureWrapMode.Clamp,
                name: $"_SolWaterVolumetricHistory_{context.CameraId}");
            context.WaterVolumetricHistory = history;
            if (allocated)
                context.CameraCut = true;
            return context.WaterVolumetricHistory != null;
        }

        // EnsureWaterNormalFoamHistory used to live here, allocating one
        // resolution x resolution x cascades array per camera. It was the only Ensure* in
        // this file that ignored the camera descriptor entirely, which was the tell: the
        // FFT normal/foam history is an FFT-domain field, not a screen-space one, so every
        // camera was keeping its own byte-identical copy of it. It now lives on
        // SolWaterSpectralTargets as a single world-level resource.

        /// <summary>
        /// Allocates the SSR colour and validation histories at the resolution the
        /// reflection is actually traced at.
        ///
        /// These used to clone the camera descriptor unscaled, which meant two persistent
        /// full-resolution R16G16B16A16 arrays per camera -- about 32 MB at 1080p, doubled
        /// again with the Scene view open -- holding data that had never contained more than
        /// half-resolution detail. The trace itself always respected ssrResolutionScale; only
        /// the resolve target and these two histories did not, so the extra pixels were
        /// carrying interpolation, not information.
        /// </summary>
        public static bool EnsureWaterReflectionHistory(
            Context context,
            RenderTextureDescriptor cameraDescriptor,
            int width,
            int height,
            int signature)
        {
            if (context == null)
                return false;

            // Dimensions are passed in rather than derived from a scale here, because the
            // caller's resolve target has to match these exactly -- it is the other end of
            // a blit. Two places computing "half of the screen" independently is precisely
            // how they end up disagreeing by a pixel at an odd viewport size.
            cameraDescriptor.width = Mathf.Max(1, width);
            cameraDescriptor.height = Mathf.Max(1, height);
            cameraDescriptor.msaaSamples = 1;
            cameraDescriptor.depthBufferBits = 0;
            cameraDescriptor.depthStencilFormat = GraphicsFormat.None;
            cameraDescriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
            cameraDescriptor.bindMS = false;
            cameraDescriptor.useDynamicScale = false;
            cameraDescriptor.useMipMap = false;
            cameraDescriptor.autoGenerateMips = false;
            cameraDescriptor.enableRandomWrite = false;

            RTHandle color = context.WaterReflectionHistory;
            bool colorAllocated = RenderingUtils.ReAllocateHandleIfNeeded(
                ref color, cameraDescriptor, FilterMode.Bilinear, TextureWrapMode.Clamp,
                name: $"_SolWaterReflectionHistory_{context.CameraId}");
            context.WaterReflectionHistory = color;

            RTHandle validation = context.WaterReflectionValidationHistory;
            bool validationAllocated = RenderingUtils.ReAllocateHandleIfNeeded(
                ref validation, cameraDescriptor, FilterMode.Point, TextureWrapMode.Clamp,
                name: $"_SolWaterReflectionValidationHistory_{context.CameraId}");
            context.WaterReflectionValidationHistory = validation;

            if (colorAllocated || validationAllocated)
                context.CameraCut = true;
            UpdateWaterReflectionSignature(context, signature);

            return color != null && validation != null;
        }

        public static bool UpdateWaterReflectionSignature(Context context, int signature)
        {
            if (context == null || context.WaterReflectionSignature == signature)
                return false;

            context.WaterReflectionSignature = signature;
            context.CameraCut = true;
            return true;
        }

        public static void PruneUnused(int maximumUnusedFrames = 8)
        {
            StaleIds.Clear();
            foreach (KeyValuePair<int, Context> pair in Contexts)
            {
                if (pair.Value.Camera == null || Time.frameCount - pair.Value.LastFrameUsed > maximumUnusedFrames)
                    StaleIds.Add(pair.Key);
            }

            for (int i = 0; i < StaleIds.Count; i++)
                Remove(StaleIds[i]);

            ReportBudget();
        }

        /// <summary>
        /// Publishes the two registry gauges. Whole method is [Conditional] rather than just
        /// the two setter calls, so the dictionary walk and the size arithmetic disappear in
        /// a release player instead of running to feed calls that were compiled away.
        /// </summary>
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        static void ReportBudget()
        {
            long ssrBytes = 0;
            foreach (KeyValuePair<int, Context> pair in Contexts)
            {
                ssrBytes += DescribeBytes(pair.Value.WaterReflectionHistory);
                ssrBytes += DescribeBytes(pair.Value.WaterReflectionValidationHistory);
            }

            SolEnvironmentBudget.SetCameraContexts(Contexts.Count);
            SolEnvironmentBudget.SetSsrHistoryBytes(ssrBytes);
        }

        /// <summary>Approximate footprint of one handle. Uses the descriptor rather than a
        /// profiler query so it stays cheap enough to run every frame.</summary>
        static long DescribeBytes(RTHandle handle)
        {
            if (handle == null || handle.rt == null)
                return 0;

            RenderTexture texture = handle.rt;
            long bytesPerPixel = GraphicsFormatUtility.GetBlockSize(texture.graphicsFormat);
            return (long)texture.width * texture.height * Mathf.Max(1, texture.volumeDepth) * bytesPerPixel;
        }

        public static void Clear()
        {
            StaleIds.Clear();
            foreach (int id in Contexts.Keys)
                StaleIds.Add(id);
            for (int i = 0; i < StaleIds.Count; i++)
                Remove(StaleIds[i]);
        }

        static void Remove(int id)
        {
            if (!Contexts.Remove(id, out Context context))
                return;
            ContextRemoved?.Invoke(context);
            context.Dispose();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Clear();
    }
}
