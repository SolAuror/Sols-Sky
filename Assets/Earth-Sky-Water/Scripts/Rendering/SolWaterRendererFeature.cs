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
    /// Water debug views, selected from the renderer feature inspector and passed to the
    /// shaders as an ordinal in <c>_SolWaterReflectionParams.y</c>.
    ///
    /// The ordinals are mirrored by the <c>SOL_WATER_DEBUG_*</c> defines at the top of
    /// SolOcean.shader, and by one define in SolWaterResolve.shader. Adding a mode at the
    /// end is safe; inserting or reordering one silently relabels every view downstream,
    /// so edit both sides together.
    /// </summary>
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
        // These seven existed in SolOcean.shader's debug branch but had no enum entry,
        // so they were unreachable from the renderer feature inspector.
        Scattering,
        Transmittance,
        Opacity,
        SunSpecular,
        VolumetricScattering,
        /// <summary>
        /// Paints red exactly the water fragments the nearest-surface test rejects —
        /// distant patches behind a near crest, and the skirt walls at detail
        /// boundaries. Before the depth-resolved prepass every red pixel here was a
        /// fragment that shaded and alpha-composited over the surface in front of it.
        /// </summary>
        SurfaceOverlap,
        /// <summary>
        /// Magenta where the visible surface is a patch skirt rather than the water
        /// surface proper. If a reported dark stroke follows the magenta, the skirt
        /// walls are shading it and the fix belongs in the clipmap, not the optics.
        /// </summary>
        PatchSkirts,
        /// <summary>
        /// Refraction leak rejection. Black means the refracted sample was discarded as
        /// implausible and the fragment fell back to the unrefracted seabed.
        /// </summary>
        RefractionConfidence,
        /// <summary>
        /// Optical path length feeding absorption, normalized against clarity distance.
        /// Absorption is superlinear, so a bright filament here is already black on
        /// screen — this is the direct view of what causes the dark strokes.
        /// </summary>
        AbsorptionPathLength,
        /// <summary>
        /// Shaded surface normal. A stroke of uniform colour through otherwise varied
        /// water means the normal is discontinuous, placing the cause in the vertex or
        /// spectral path rather than in the optics.
        /// </summary>
        SurfaceNormal,
        /// <summary>
        /// Spectral detail fade. Black strokes are pixels whose derivative footprint
        /// suppressed all cascade detail, leaving a flat, foamless normal.
        /// </summary>
        SpectralDetailFade,
        /// <summary>
        /// Caustic response headroom: green is brightening against its ceiling, red is
        /// darkening against its limit. A healthy field is a sparse bright web over
        /// mostly dim ground. Flat saturated green means the gain is too hot and the
        /// pattern has been clipped away — raise or lower `causticStrength` on the water
        /// profile until the web is visible.
        /// </summary>
        CausticResponse,
    }

    /// <summary>
    /// Unity 6 URP RenderGraph water renderer. It owns the ocean prepass, scene-color capture,
    /// SSR resolve, instanced surface draw, and per-camera underwater composition.
    /// </summary>
    [System.Serializable]
    public sealed class SolWaterRendererFeature : ScriptableRendererFeature
    {
        // The ocean is the only water shader drawn through DrawMeshInstanced, so it is
        // the only one whose INSTANCING_ON variant has to survive into a player build.
        // Built-in shader stripping keeps that variant only for shaders a *material
        // asset* in the build enables instancing on, and every material here is created
        // from a shader at runtime, which the build cannot see. So the ocean material is
        // authored as an asset with GPU instancing ticked and instantiated from, rather
        // than created from `oceanShader`. Without it the player keeps only the
        // non-instanced variant, unity_ObjectToWorld and _SolOceanPatchData arrive as
        // zero for every patch, and the whole clipmap collapses to nothing — silently,
        // with no error anywhere, and only in builds.
        [Header("Resources")]
        [Tooltip("Ocean surface material. Authored as an asset with GPU instancing "
            + "ticked, which is what keeps the instancing variant alive in a player "
            + "build; the feature copies it and never dirties the asset. All of these "
            + "resolve by name when empty, so they normally need no attention.")]
        [SerializeField] Material oceanMaterial;
        [Tooltip("Ocean surface shader. Used only if the material above is missing.")]
        [SerializeField] Shader oceanShader;
        [Tooltip("Screen-space reflection trace, temporal resolve and anisotropic filter.")]
        [SerializeField] Shader resolveShader;
        [Tooltip("Submerged-camera composition.")]
        [SerializeField] Shader underwaterShader;
        [Tooltip("Ocean spectrum simulation and inverse FFT.")]
        [SerializeField] ComputeShader fftShader;
        [Tooltip("Renders the caustic field from live FFT displacement.")]
        [SerializeField] Shader causticShader;
        [Tooltip("In-water light shafts and shadowed volume.")]
        [SerializeField] Shader volumetricShader;

        [Header("Editor")]
        [Tooltip("Draw water in the Scene view. Turn off to author terrain or geometry "
            + "under the surface without the water in the way.")]
        [SerializeField] bool renderInSceneView = true;

        [Header("Debug")]
        [Tooltip("Replaces the water surface with one intermediate term, on every body. "
            + "Disabled ships the final image; each mode's tooltip is on the enum itself. "
            + "Leave this Disabled outside diagnosis, and never ship it enabled.")]
        [SerializeField] SolWaterDebugMode debugMode;
        [Tooltip("Log pass-level water decisions to the console. Noisy: it prints per "
            + "frame while the camera is submerged.")]
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
            // Every channel here is an identifier or a depth, never a colour. Filtering
            // blends the body hash and the surface depth of neighbouring fragments into
            // a value that describes no real surface, so consumers point sample and the
            // descriptor refuses to hand them anything else.
            cameraColorDesc.filterMode = FilterMode.Point;
            cameraColorDesc.clearBuffer = true;
            cameraColorDesc.clearColor = Color.clear;
            return cameraColorDesc;
        }

        /// <summary>
        /// Private depth buffer for the water prepass, matching its colour target.
        /// This is what resolves water against water: without it the prepass is
        /// last-writer-wins, so a distant patch or a detail-boundary skirt can claim a
        /// pixel that a near crest actually occupies, and every pass reading the prepass
        /// downstream inherits that wrong surface.
        /// </summary>
        internal static TextureDesc BuildPrepassDepthDescriptor(TextureDesc prepassDesc, string name)
        {
            prepassDesc.name = name;
            prepassDesc.colorFormat = GraphicsFormat.None;
            prepassDesc.depthBufferBits = DepthBits.Depth32;
            // Matching the colour target's sample count is the whole reason binding
            // depth is safe here; see the SolWaterPrepass pass block in SolOcean.shader.
            prepassDesc.msaaSamples = MSAASamples.None;
            prepassDesc.bindTextureMS = false;
            prepassDesc.enableRandomWrite = false;
            prepassDesc.clearBuffer = true;
            return prepassDesc;
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

            oceanMaterial = SolAssetResolver.ResolveMaterial(oceanMaterial, "M_SolOcean");

            _oceanMaterial = oceanMaterial != null
                ? CreateMaterial(oceanMaterial, "Sol Ocean Runtime Material")
                : CreateMaterial(oceanShader, "Sol Ocean Runtime Material");
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

            PublishUnderwaterState(cameraData.camera, SolWaterWorld.Active);

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

        // Shared with the legacy Water 1 stack. SolAtmosphereRendererFeature gates its
        // aerial perspective on _UnderwaterFactor, and only the legacy
        // UnderwaterVolumeController ever wrote it -- so in a Water 2 scene the gate never
        // fired and a submerged camera still got full-strength fog over the underwater
        // view. The legacy UnderwaterRendererFeature reads the same global, so it has to
        // stay disabled in the renderer asset or both stacks composite underwater.
        static readonly int UnderwaterFactorId = Shader.PropertyToID("_UnderwaterFactor");
        static readonly int UnderwaterDepthId = Shader.PropertyToID("_UnderwaterDepth");

        /// <summary>
        /// Resolves camera submersion and publishes the _UnderwaterFactor contract.
        ///
        /// This runs from AddRenderPasses rather than from UnderwaterPass, because the
        /// atmosphere reads the global in its own RecordRenderGraph at
        /// BeforeRenderingTransparents, which is earlier than the underwater pass records
        /// at BeforeRenderingPostProcessing. Publishing from the pass left the fog gate a
        /// frame behind, so surfacing and submerging both flashed the wrong sky for a
        /// frame. Every path here publishes, including the not-submerged one: leaving the
        /// global stranded keeps the sky suppressed after the camera leaves the water.
        /// </summary>
        /// <summary>
        /// One water sample per camera position per frame, shared by everything in this
        /// feature that needs to know where the camera sits relative to the surface.
        ///
        /// Three places asked the same question about the same camera in the same frame --
        /// <see cref="PublishUnderwaterState"/> from AddRenderPasses, the ocean pass, and
        /// the underwater pass -- and each answer costs a full displacement inversion:
        /// up to nine wave evaluations, each sampling four spectral cascades bilinearly and
        /// reading the shoreline data texture on the CPU five more times for the breaker
        /// term. Roughly two thousand array reads per camera per frame, three times over,
        /// for one value that cannot change between them.
        ///
        /// Keyed on camera and frame, and additionally validated against the position the
        /// sample was taken at, so this stays a memo of a pure function rather than an
        /// assumption about when Unity calls what.
        /// </summary>
        static class CameraWaterSample
        {
            struct Entry
            {
                internal int Frame;
                internal Vector3 Position;
                internal bool HasResult;
                internal SolWaterSurfaceSample Sample;
            }

            static readonly System.Collections.Generic.Dictionary<int, Entry> Entries = new(4);

            internal static bool Resolve(SolWaterWorld world, Camera camera,
                out SolWaterSurfaceSample sample)
            {
                if (world == null || camera == null)
                {
                    sample = default;
                    return false;
                }

                int id = camera.GetInstanceID();
                Vector3 position = camera.transform.position;
                int frame = Time.frameCount;
                if (Entries.TryGetValue(id, out Entry entry)
                    && entry.Frame == frame && entry.Position == position)
                {
                    sample = entry.Sample;
                    return entry.HasResult;
                }

                bool hasResult = world.TrySampleApproximate(position, out sample);
                Entries[id] = new Entry
                {
                    Frame = frame,
                    Position = position,
                    HasResult = hasResult,
                    Sample = sample,
                };
                return hasResult;
            }

            internal static void Clear() => Entries.Clear();
        }

        static void PublishUnderwaterState(Camera camera, SolWaterWorld world)
        {
            float factor = 0f;
            float depth = 0f;
            if (camera != null && world != null)
            {
                SolWaterQualityProfile quality = world.QualityProfile;
                bool underwaterEnabled = quality == null || quality.underwater;
                if (underwaterEnabled
                    && CameraWaterSample.Resolve(world, camera,
                        out SolWaterSurfaceSample sample)
                    && sample.HasWater && sample.Depth > 0.001f)
                {
                    // Matches the transition term UnderwaterPass hands the composition
                    // shader, so the fog gate and the underwater tint cross over together.
                    factor = Mathf.SmoothStep(0f, 1f, sample.Depth / 0.2f);
                    depth = sample.Depth;
                }
            }

            Shader.SetGlobalFloat(UnderwaterFactorId, factor);
            Shader.SetGlobalFloat(UnderwaterDepthId, depth);
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
            CameraWaterSample.Clear();
            _fallbackProfile = null;
            _oceanPass = null;
            _underwaterPass = null;
            SolEnvironmentCameraRegistry.Clear();
        }

        /// <summary>
        /// Copies the authored material so the per-frame state this feature writes never
        /// dirties the asset. The copy inherits the source's instancing flag, and more
        /// importantly the source's presence in the build is what kept the instancing
        /// variants compilable in the first place.
        /// </summary>
        static Material CreateMaterial(Material source, string name)
        {
            if (source == null)
                return null;
            Material material = new(source)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
            };
            material.enableInstancing = true;
            return material;
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
                // A submerged camera still needs the spectrum and the caustic array even
                // when it can see no surface at all. The clipmap frustum-culls its
                // patches, so looking straight down from underwater selects no leaves,
                // `drawOcean` goes false, and bailing here would take the FFT and the
                // caustic pass with it — leaving the underwater composition to sample a
                // caustic array nothing rendered this frame.
                //
                // Gated on there being an ocean: a camera submerged in a finite body is
                // Gerstner-only and consumes no spectrum, so the FFT would be wasted.
                bool cameraSubmerged = false;
                float waterSurfaceHeight = 0f;
                if (quality.underwater
                    && CameraWaterSample.Resolve(world, cameraData.camera,
                        out SolWaterSurfaceSample cameraSample)
                    && cameraSample.HasWater && cameraSample.Depth > 0.001f)
                {
                    cameraSubmerged = true;
                    waterSurfaceHeight = cameraSample.Position.y;
                }
                // Submersion itself carries no ocean gate: a camera inside a lake is as
                // underwater as one in the sea, and requiring an ocean here is what left
                // finite-body volumetrics switching on and off with whether the lake
                // surface happened to be in frustum. The ocean gate belongs only on the
                // spectral chain, where it means something -- a Gerstner-only body
                // consumes no FFT, so running one for it would be wasted work.
                bool submergedNeedsSpectrum = cameraSubmerged && hasOcean;
                if (!drawOcean && !drawFinite && !cameraSubmerged)
                {
                    // Both of these are shader globals, so leaving them untouched here
                    // would strand last frame's value and let a later pass sample a
                    // caustic array or volumetric buffer this frame never rendered.
                    Shader.SetGlobalVector(SolWaterCausticRenderGraph.ArrayParamsId,
                        SolWaterCausticRenderGraph.ArrayParams(0, false));
                    Shader.SetGlobalVector(VolumetricParamsId, Vector4.zero);
                    SolEnvironmentCameraRegistry.EndCamera(cameraContext);
                    return;
                }

                // The debug mode and the SSR flag go in through the writer rather than
                // as a follow-up SetVector that overwrote _SolWaterReflectionParams a
                // line after ApplyMaterialState had just written zeros into it.
                bool screenSpaceReflections =
                    quality.screenSpaceReflections && quality.ssrResolutionScale > 0f;
                ApplyMaterialState(_oceanMaterial, profile, world,
                    _debugMode, screenSpaceReflections);
                // The resolve material reads its own _SolWaterSSRResolveParams for the
                // debug mode, so it takes the defaults.
                ApplyMaterialState(_resolveMaterial, profile, world);
                // Finite bodies are Gerstner-only and consume SpectralParams = zero, so
                // running the whole inverse-FFT chain for a frame that draws no ocean is
                // roughly eighteen wasted compute dispatches. A submerged camera is the
                // exception: the underwater composition projects the same live caustics
                // the surface does, so the chain has to run even with no patch on screen.
                SolWaterFftRenderGraph.SpectralResources spectral = drawOcean || submergedNeedsSpectrum
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
                // Global rather than per material, so every consumer sees what this frame
                // actually rendered. The underwater pass used to restate these from the
                // quality and profile settings alone, which asserted the array was valid
                // whenever caustics were merely *enabled* — so on a frame where no array
                // was produced it still took the live-array branch and sampled a target
                // nothing had drawn into. That reads back as a uniform -1.15 and dimmed
                // the sea bed flat instead of lighting it.
                Shader.SetGlobalVector(
                    SolWaterCausticRenderGraph.ArrayParamsId, causticArrayParams);

                // With no surface on screen there is nothing to reflect or refract, but
                // the volumetric march below still has a water volume to walk when the
                // camera is inside it, so this does not return early.
                bool hasSurfaceToDraw = drawOcean || drawFinite;
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
                // Ocean and finite bodies share this buffer, so a finite body in front
                // of the ocean now wins the pixel on depth instead of on draw order.
                TextureHandle prepassDepth = renderGraph.CreateTexture(
                    BuildPrepassDepthDescriptor(prepassDesc, "_SolWaterPrepassDepth"));

                if (drawOcean)
                {
                    RecordOceanDraw(
                        renderGraph,
                        "Sol Ocean Prepass",
                        prepassData,
                        depth,
                        prepassDepth,
                        _clipmap.PatchMesh,
                        drawSet,
                        _oceanMaterial,
                        PrepassIndex);
                }
                if (drawFinite)
                {
                    RecordFiniteDraw(renderGraph, "Sol Finite Water Prepass",
                        prepassData, default, depth, prepassDepth,
                        default, default, _finiteDrawSet, _oceanMaterial, PrepassIndex);
                }

                // Only the surface refracts, so a frame that draws none needs neither the
                // scene colour copy nor the reflection chain.
                TextureHandle sceneColor = default;
                if (hasSurfaceToDraw)
                {
                    TextureDesc sceneDesc = sourceDesc;
                    sceneDesc.name = "_SolWaterSceneColor";
                    sceneDesc.msaaSamples = MSAASamples.None;
                    sceneDesc.bindTextureMS = false;
                    sceneDesc.clearBuffer = false;
                    sceneColor = renderGraph.CreateTexture(sceneDesc);
                    renderGraph.AddBlitPass(activeColor, sceneColor, Vector2.one, Vector2.zero,
                        passName: "Sol Water Capture Scene Color");
                }

                int reflectionSignature = CalculateReflectionSignature(world, quality, _debugMode);
                TextureHandle ssr = renderGraph.defaultResources.blackTexture;
                if (hasSurfaceToDraw && quality.screenSpaceReflections
                    && quality.ssrResolutionScale > 0f)
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
                        float windSpeed = SolEnvironmentWorld.ResolveState().Wind.Speed;
                        Vector4 anisoParams = new(
                            Mathf.Min(0.08f, windSpeed * 0.004f
                                * profile.anisotropicReflectionScale),
                            // Falloff per metre of eye depth: the kernel closes at 250 m.
                            // The shader used to apply this to the prepass's raw device
                            // depth, where it did nothing; it now linearizes first, so
                            // this value finally means what it reads as.
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
                    && cameraContext != null && prepassData.IsValid()
                    && (hasSurfaceToDraw || cameraSubmerged);
                TextureHandle volumetric = wantsVolumetrics
                    ? SolWaterVolumetricsRenderGraph.Record(
                        renderGraph, _volumetricMaterial, activeColor, prepassData,
                        cameraData, cameraContext, quality, profile,
                        cameraSubmerged, waterSurfaceHeight)
                    : default;
                // Global rather than on the ocean material: the underwater composition
                // reads the same flag, and it never sees a material set made here.
                Shader.SetGlobalVector(VolumetricParamsId,
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
                        activeColor, default, depth, default, sceneColor, ssr,
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
                TextureHandle resolveDepth,
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
                // The private prepass depth attachment, not the camera's. `depth` stays
                // a plain texture read for the SampleSceneDepth clip against terrain.
                if (resolveDepth.IsValid())
                    builder.SetRenderAttachmentDepth(resolveDepth, AccessFlags.ReadWrite);
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
                            // SurfaceLevel deliberately excluded. Every other member here
                            // is an authored setting or a structural identity; the level is
                            // animated state. A tide or flood driving it through
                            // SetRuntimeSurfaceLevel changed this hash every frame, which
                            // raised CameraCut every frame, which zeroed the SSR, the
                            // volumetric and the FFT foam history blends at once -- so all
                            // three temporal accumulations died for as long as the water
                            // level moved. Geometry that moves is what depth reprojection
                            // and the SSR validation history are already there to handle.
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
                TextureHandle resolveDepth,
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
                {
                    builder.UseTexture(depth, AccessFlags.Read);
                    // Shared with the ocean prepass, so both resolve into one nearest
                    // water surface per pixel regardless of which recorded first.
                    if (resolveDepth.IsValid())
                        builder.SetRenderAttachmentDepth(resolveDepth, AccessFlags.ReadWrite);
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
            sealed class UnderwaterPassData
            {
                internal Material Material;
                internal TextureHandle Source;
            }

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
                if (!CameraWaterSample.Resolve(world, camera, out SolWaterSurfaceSample sample)
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
                // Caustic array params are deliberately NOT set here. The ocean pass
                // publishes them as a shader global describing what it actually rendered,
                // and a material set would shadow that global with this pass's guess.
                // Restating them from the settings is what made the underwater
                // composition claim a live array on frames where none was produced.

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
                // Explicit raster pass rather than AddBlitPass. SolUnderwater.shader calls
                // GetMainLight and TransformWorldToShadowCoord for its caustic visibility
                // term, so the pass has to declare access to the shadow map and the other
                // global textures. AddBlitPass declares none -- the ocean and volumetric
                // passes both call UseAllGlobalTextures for exactly this reason and this
                // one was the odd pass out.
                using (IRasterRenderGraphBuilder builder =
                    renderGraph.AddRasterRenderPass<UnderwaterPassData>(
                        "Sol Underwater Composition", out UnderwaterPassData passData))
                {
                    passData.Material = _material;
                    passData.Source = copy;
                    builder.UseTexture(copy, AccessFlags.Read);
                    builder.UseAllGlobalTextures(true);
                    builder.SetRenderAttachment(activeColor, 0, AccessFlags.Write);
                    builder.SetRenderFunc(
                        static (UnderwaterPassData data, RasterGraphContext context) =>
                        {
                            Blitter.BlitTexture(context.cmd, data.Source, Vector2.one,
                                data.Material, 0);
                        });
                }

                if (_debug)
                    Debug.Log($"[SolWater] Underwater {body.name}, depth {sample.Depth:F2}m", body);
            }
        }

        /// <summary>
        /// Writes the profile and environment state onto one of this feature's runtime
        /// materials. A thin wrapper over the shared writer, which the finite bodies drive
        /// through a MaterialPropertyBlock instead — see SolWaterMaterialState for why the
        /// two destinations exist and why the writes themselves are not duplicated.
        /// </summary>
        static void ApplyMaterialState(
            Material material,
            SolWaterProfile profile,
            SolWaterWorld world,
            SolWaterDebugMode debugMode = SolWaterDebugMode.Disabled,
            bool screenSpaceReflectionsEnabled = false)
        {
            if (material == null)
                return;
            SolWaterMaterialState.ApplyProfile(
                new SolWaterPropertyTarget(material), profile, world,
                debugMode, screenSpaceReflectionsEnabled);
        }
    }
}
