using System.Collections.Generic;
using Sol.Environment;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Sol.Water.Rendering
{
    /// <summary>
    /// Experimental planar-reflection camera retained for future scheduled rendering work.
    /// Nested camera rendering is disabled by default because Unity 6 Forward+ jobs can remain
    /// active at endCameraRendering. Water uses SSR and probe/sky fallback in production.
    /// </summary>
    [DefaultExecutionOrder(-650)]
    [DisallowMultipleComponent]
    public sealed class SolPlanarReflectionRenderer : MonoBehaviour
    {
        sealed class ReflectionContext
        {
            internal Camera Source;
            internal Camera ReflectionCamera;
            internal RenderTexture Texture;
            internal Matrix4x4 ViewProjection;
            internal int BodyHash;
            internal int LastFrame;
            internal int Width;
            internal int Height;
        }

        static readonly Dictionary<int, ReflectionContext> Contexts = new(4);
        static readonly List<int> Stale = new(4);

        [SerializeField] LayerMask cullingMask = ~0;
        [SerializeField, Min(0f)] float clipPlaneOffset = 0.07f;
        [SerializeField] bool renderSceneView;
        [SerializeField, Tooltip("Experimental: nested URP camera rendering can conflict with active Forward+ jobs in Unity 6.")]
        bool enableExperimentalNestedRendering;
        readonly Plane[] _frustumPlanes = new Plane[6];

        internal static bool IsRendering { get; private set; }

        void OnEnable()
        {
            if (enableExperimentalNestedRendering)
                RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        void OnDisable()
        {
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            ReleaseAll();
        }

        internal static bool TryGet(
            Camera source,
            out RenderTexture texture,
            out Matrix4x4 viewProjection,
            out int bodyHash,
            out int age)
        {
            if (source != null && Contexts.TryGetValue(source.GetInstanceID(), out ReflectionContext context)
                && context.Texture != null)
            {
                texture = context.Texture;
                viewProjection = context.ViewProjection;
                bodyHash = context.BodyHash;
                age = Mathf.Max(0, Time.frameCount - context.LastFrame);
                return true;
            }
            texture = null;
            viewProjection = Matrix4x4.identity;
            bodyHash = 0;
            age = int.MaxValue;
            return false;
        }

        void OnEndCameraRendering(ScriptableRenderContext renderContext, Camera source)
        {
            if (IsRendering || source == null || SolWaterWorld.Active == null)
                return;
            if (source.cameraType is CameraType.Preview or CameraType.Reflection)
                return;
            if (!renderSceneView && source.cameraType == CameraType.SceneView)
                return;
            SolWaterQualityProfile quality = SolWaterWorld.Active.QualityProfile;
            if (quality == null || !quality.planarReflections)
                return;
            if (!TrySelectBody(source, out SolWaterBody body))
                return;

            int width = Mathf.Max(64, Mathf.RoundToInt(source.pixelWidth * quality.planarResolutionScale));
            int height = Mathf.Max(64, Mathf.RoundToInt(source.pixelHeight * quality.planarResolutionScale));
            ReflectionContext context = GetOrCreate(source, width, height);
            RenderReflection(renderContext, source, body, context);
            Prune();
        }

        bool TrySelectBody(Camera camera, out SolWaterBody selected)
        {
            selected = null;
            float bestDistance = float.PositiveInfinity;
            var bodies = SolWaterWorld.Active.Bodies;
            GeometryUtility.CalculateFrustumPlanes(camera, _frustumPlanes);
            for (int i = 0; i < bodies.Count; i++)
            {
                SolWaterBody body = bodies[i];
                if (body == null || !body.isActiveAndEnabled || !body.EnablePlanarReflection)
                    continue;
                if (!body.IsInfinite && !GeometryUtility.TestPlanesAABB(_frustumPlanes, body.GetWorldBounds()))
                    continue;
                float distance = Mathf.Abs(camera.transform.position.y - body.SurfaceLevel);
                if (distance >= bestDistance)
                    continue;
                selected = body;
                bestDistance = distance;
            }
            return selected != null;
        }

        ReflectionContext GetOrCreate(Camera source, int width, int height)
        {
            int id = source.GetInstanceID();
            if (!Contexts.TryGetValue(id, out ReflectionContext context))
            {
                GameObject cameraObject = new($"Sol Planar Reflection ({source.name})")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                Camera reflectionCamera = cameraObject.AddComponent<Camera>();
                reflectionCamera.enabled = false;
                context = new ReflectionContext
                {
                    Source = source,
                    ReflectionCamera = reflectionCamera,
                };
                Contexts.Add(id, context);
            }

            if (context.Texture == null || context.Width != width || context.Height != height)
            {
                ReleaseTexture(context);
                RenderTextureDescriptor descriptor = new(width, height)
                {
                    graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat,
                    depthStencilFormat = GraphicsFormat.D24_UNorm_S8_UInt,
                    msaaSamples = 1,
                    useMipMap = false,
                    autoGenerateMips = false,
                };
                context.Texture = new RenderTexture(descriptor)
                {
                    name = $"Sol Planar Reflection {source.name}",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                context.Texture.Create();
                context.Width = width;
                context.Height = height;
            }
            return context;
        }

        void RenderReflection(
            ScriptableRenderContext renderContext,
            Camera source,
            SolWaterBody body,
            ReflectionContext context)
        {
            Camera reflection = context.ReflectionCamera;
            reflection.CopyFrom(source);
            reflection.enabled = false;
            reflection.targetTexture = context.Texture;
            reflection.cullingMask = cullingMask;
            reflection.useOcclusionCulling = false;
            reflection.allowMSAA = false;

            Vector3 planePosition = new(0f, body.SurfaceLevel, 0f);
            Vector3 planeNormal = Vector3.up;
            Matrix4x4 reflectionMatrix = CalculateReflectionMatrix(new Vector4(
                planeNormal.x, planeNormal.y, planeNormal.z,
                -Vector3.Dot(planeNormal, planePosition) - clipPlaneOffset));
            reflection.worldToCameraMatrix = source.worldToCameraMatrix * reflectionMatrix;
            Vector3 reflectedPosition = reflectionMatrix.MultiplyPoint(source.transform.position);
            reflection.transform.SetPositionAndRotation(
                reflectedPosition,
                Quaternion.LookRotation(
                    reflectionMatrix.MultiplyVector(source.transform.forward),
                    reflectionMatrix.MultiplyVector(source.transform.up)));
            Vector4 clipPlane = CameraSpacePlane(reflection, planePosition, planeNormal, 1f);
            reflection.projectionMatrix = source.CalculateObliqueMatrix(clipPlane);
            context.ViewProjection = GL.GetGPUProjectionMatrix(reflection.projectionMatrix, true)
                * reflection.worldToCameraMatrix;
            context.BodyHash = body.PrepassHash;
            context.LastFrame = Time.frameCount;

            bool previousInvert = GL.invertCulling;
            IsRendering = true;
            try
            {
                GL.invertCulling = !previousInvert;
#pragma warning disable CS0618
                UniversalRenderPipeline.RenderSingleCamera(renderContext, reflection);
#pragma warning restore CS0618
            }
            finally
            {
                GL.invertCulling = previousInvert;
                IsRendering = false;
            }
        }

        Vector4 CameraSpacePlane(Camera camera, Vector3 position, Vector3 normal, float sideSign)
        {
            Vector3 offsetPosition = position + normal * clipPlaneOffset;
            Matrix4x4 matrix = camera.worldToCameraMatrix;
            Vector3 cameraPosition = matrix.MultiplyPoint(offsetPosition);
            Vector3 cameraNormal = matrix.MultiplyVector(normal).normalized * sideSign;
            return new Vector4(cameraNormal.x, cameraNormal.y, cameraNormal.z,
                -Vector3.Dot(cameraPosition, cameraNormal));
        }

        static Matrix4x4 CalculateReflectionMatrix(Vector4 plane)
        {
            Matrix4x4 matrix = Matrix4x4.identity;
            matrix.m00 = 1f - 2f * plane.x * plane.x;
            matrix.m01 = -2f * plane.x * plane.y;
            matrix.m02 = -2f * plane.x * plane.z;
            matrix.m03 = -2f * plane.w * plane.x;
            matrix.m10 = -2f * plane.y * plane.x;
            matrix.m11 = 1f - 2f * plane.y * plane.y;
            matrix.m12 = -2f * plane.y * plane.z;
            matrix.m13 = -2f * plane.w * plane.y;
            matrix.m20 = -2f * plane.z * plane.x;
            matrix.m21 = -2f * plane.z * plane.y;
            matrix.m22 = 1f - 2f * plane.z * plane.z;
            matrix.m23 = -2f * plane.w * plane.z;
            return matrix;
        }

        static void Prune()
        {
            Stale.Clear();
            foreach (KeyValuePair<int, ReflectionContext> pair in Contexts)
            {
                if (pair.Value.Source == null || Time.frameCount - pair.Value.LastFrame > 16)
                    Stale.Add(pair.Key);
            }
            for (int i = 0; i < Stale.Count; i++)
            {
                int id = Stale[i];
                ReflectionContext context = Contexts[id];
                Release(context);
                Contexts.Remove(id);
            }
        }

        static void ReleaseAll()
        {
            foreach (ReflectionContext context in Contexts.Values)
                Release(context);
            Contexts.Clear();
        }

        static void Release(ReflectionContext context)
        {
            ReleaseTexture(context);
            if (context.ReflectionCamera != null)
                Destroy(context.ReflectionCamera.gameObject);
        }

        static void ReleaseTexture(ReflectionContext context)
        {
            if (context.Texture == null)
                return;
            context.Texture.Release();
            Destroy(context.Texture);
            context.Texture = null;
        }
    }
}
