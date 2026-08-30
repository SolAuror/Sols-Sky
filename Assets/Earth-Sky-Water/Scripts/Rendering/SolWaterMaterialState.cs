using Sol.Environment;
using UnityEngine;

namespace Sol.Water.Rendering
{
    /// <summary>
    /// A destination for water shader state: either a <see cref="Material"/> or a
    /// <see cref="MaterialPropertyBlock"/>.
    ///
    /// The two exist because the ocean and the finite bodies reach the GPU differently.
    /// The ocean is one instanced draw of one shared material, so its profile state is set
    /// on the material itself and only per-patch data rides a property block. A finite
    /// body is one draw per body against that same shared material, so every one of its
    /// values has to ride a property block or bodies would overwrite each other.
    ///
    /// That difference is real, but it is a difference in *where* the values go, not in
    /// what they are — which is why the writes themselves are shared through this type
    /// rather than duplicated per destination.
    ///
    /// A readonly struct with a plain reference test, not an interface: MaterialPropertyBlock
    /// is an ordinary managed class, so the branch is a null compare rather than
    /// UnityEngine.Object's overloaded, alive-checking operator.
    /// </summary>
    internal readonly struct SolWaterPropertyTarget
    {
        readonly Material _material;
        readonly MaterialPropertyBlock _properties;

        internal SolWaterPropertyTarget(Material material)
        {
            _material = material;
            _properties = null;
        }

        internal SolWaterPropertyTarget(MaterialPropertyBlock properties)
        {
            _material = null;
            _properties = properties;
        }

        internal void SetFloat(int id, float value)
        {
            if (_properties != null)
                _properties.SetFloat(id, value);
            else
                _material.SetFloat(id, value);
        }

        internal void SetInt(int id, int value)
        {
            if (_properties != null)
                _properties.SetInt(id, value);
            else
                _material.SetInteger(id, value);
        }

        internal void SetVector(int id, Vector4 value)
        {
            if (_properties != null)
                _properties.SetVector(id, value);
            else
                _material.SetVector(id, value);
        }

        internal void SetColor(int id, Color value)
        {
            if (_properties != null)
                _properties.SetColor(id, value);
            else
                _material.SetColor(id, value);
        }

        internal void SetTexture(int id, Texture value)
        {
            // Backstop only. MaterialPropertyBlock.SetTexture throws ArgumentNullException
            // on null while Material.SetTexture treats it as "clear the override", so a
            // null reaching here would abort the whole write on the property block path
            // and silently succeed on the material one. Callers substitute a neutral
            // texture instead of relying on this; see ApplyProfile.
            if (value == null)
                return;
            if (_properties != null)
                _properties.SetTexture(id, value);
            else
                _material.SetTexture(id, value);
        }

        internal void SetVectorArray(int id, Vector4[] value)
        {
            if (_properties != null)
                _properties.SetVectorArray(id, value);
            else
                _material.SetVectorArray(id, value);
        }
    }

    /// <summary>
    /// The single writer for water profile and environment state.
    ///
    /// This used to exist twice: once in SolWaterRendererFeature.ApplyMaterialState against
    /// a Material, and once in SolFiniteWaterDrawSet.FillProperties against a
    /// MaterialPropertyBlock — about forty-five near-identical writes each, which had to be
    /// edited in lockstep for every profile field added. They had already drifted on
    /// `_SolWaterReflectionParams`, where the finite path published the debug mode and SSR
    /// availability and the material path published zeros.
    ///
    /// The interaction-zone block below had drifted into a third copy, in
    /// SolOceanClipmap.PrepareProperties.
    /// </summary>
    internal static class SolWaterMaterialState
    {
        /// <summary>
        /// Writes everything derived from the water profile and the current environment.
        ///
        /// Deliberately does NOT write the per-draw identity — body hash, geometry mode,
        /// body flow, wave arrays, patch data, spectral params. Those differ per body and
        /// per draw path, so they stay with the caller that knows them.
        /// </summary>
        /// <param name="debugMode">
        /// Published in <c>_SolWaterReflectionParams.y</c>. The ocean and the finite bodies
        /// both need the real value or their debug views silently show final colour; the
        /// resolve and underwater materials do not read it and pass Disabled.
        /// </param>
        /// <param name="screenSpaceReflectionsEnabled">
        /// Published in <c>_SolWaterReflectionParams.z</c> so the surface knows whether the
        /// SSR texture it samples was actually produced this frame.
        /// </param>
        internal static void ApplyProfile(
            SolWaterPropertyTarget target,
            SolWaterProfile profile,
            SolWaterWorld world,
            SolWaterDebugMode debugMode = SolWaterDebugMode.Disabled,
            bool screenSpaceReflectionsEnabled = false)
        {
            if (profile == null || world == null)
                return;

            SolEnvironmentState environment = SolEnvironmentWorld.ResolveState();
            SolDouble3 origin = SolWorldOriginService.Active?.LogicalOrigin ?? default;

            target.SetFloat(SolWaterShaderIds.WaveTime, (float)world.WaveTime);
            target.SetVector(SolWaterShaderIds.WorldOrigin,
                new Vector4((float)origin.X, (float)origin.Y, (float)origin.Z, 0f));
            target.SetVector(SolWaterShaderIds.Wind,
                new Vector4(environment.Wind.Direction.x, environment.Wind.Direction.y,
                    environment.Wind.Direction.z, environment.Wind.Speed));
            target.SetVector(SolWaterShaderIds.Weather,
                new Vector4(environment.Wind.Speed, environment.Weather.WaterTurbulence,
                    environment.Weather.Rain, environment.Weather.WaveSpeedMultiplier));
            target.SetVector(SolWaterShaderIds.WeatherExtended,
                new Vector4(environment.Weather.Cloudiness * profile.cloudShadowStrength,
                    environment.Weather.Lightning * profile.lightningReflectionStrength,
                    profile.rainRoughness, profile.rainNormalStrength));

            target.SetColor(SolWaterShaderIds.ShallowColor, profile.shallowScattering);
            target.SetColor(SolWaterShaderIds.DeepColor, profile.deepScattering);
            target.SetVector(SolWaterShaderIds.Absorption, profile.absorption);
            target.SetVector(SolWaterShaderIds.Optics,
                new Vector4(profile.indexOfRefraction, profile.smoothness,
                    profile.scatteringStrength, profile.waveSpeed));
            target.SetVector(SolWaterShaderIds.SunParams,
                new Vector4(profile.sunSpecularStrength, profile.sunSpecularRoughness,
                    profile.sunSpecularMaximum, 0f));
            target.SetVector(SolWaterShaderIds.RefractionParams,
                new Vector4(profile.refractionStrength, profile.refractionMaximumDistance,
                    profile.refractionDispersion, profile.refractionMaximumScreenOffset));
            target.SetVector(SolWaterShaderIds.VisibilityParams,
                new Vector4(profile.clarityDistance, profile.underwaterDensity,
                    profile.horizonReflectionStrength, 0f));
            target.SetVector(SolWaterShaderIds.Spectrum,
                new Vector4(profile.windResponse, 0f, 0f, 0f));

            // Every optional texture is substituted with a neutral one rather than left
            // null. A Material accepts null as "clear the override", but a
            // MaterialPropertyBlock throws ArgumentNullException — so the finite path,
            // which is all property block, aborted mid-write on any profile with an
            // unassigned texture. The shipped Ocean Profile leaves shorelineMask empty,
            // so every finite body using it lost every property after this line: the
            // whole shoreline block, all foam and caustic state, the reflection params,
            // the sky gradient and its interaction zone. Those then fell through to
            // whatever the shared material was holding, which is the ocean's state.
            //
            // Black is inert for both of these: each has a companion validity flag
            // (ShorelineDetail.w, FoamDetail.w) that is already zero when the texture is
            // absent, and SolWaterFoamTextureMask returns early on it.
            target.SetTexture(SolWaterShaderIds.ShorelineMask,
                profile.shorelineMask != null ? profile.shorelineMask : Texture2D.blackTexture);
            target.SetVector(SolWaterShaderIds.ShorelineParams,
                new Vector4(profile.shorelineMaskMapping.x, profile.shorelineMaskMapping.y,
                    profile.shorelineMaskMapping.z, profile.shorelineMaskMapping.w));
            target.SetVector(SolWaterShaderIds.ShorelineDetail,
                new Vector4(profile.shorelineMask != null ? profile.shorelineMaskStrength : 0f,
                    profile.shorelineFoamWidth, profile.shorelineWetness,
                    profile.shorelineMask != null ? 1f : 0f));
            // The live field wins over the profile's baked texture wherever one is being
            // generated, because only it still describes the terrain that is actually in
            // the scene. A baked texture is a snapshot of a heightmap and a water level at
            // the moment somebody exported it, and nothing in the project has been able to
            // re-export one since the baker was removed. Absent a provider this falls all
            // the way back to the old behaviour, so scenes with no terrain are unchanged.
            SolShorelineField shoreline = ResolveShorelineField(profile, world);
            bool hasShoreline = shoreline != null || profile.shorelineData != null;
            target.SetTexture(SolWaterShaderIds.ShorelineData,
                shoreline != null ? shoreline.Texture
                    : profile.shorelineData != null ? profile.shorelineData
                    : Texture2D.grayTexture);
            target.SetVector(SolWaterShaderIds.ShorelineDataMapping,
                shoreline != null ? shoreline.Mapping : profile.shorelineDataMapping);
            target.SetVector(SolWaterShaderIds.ShorelineDataParams,
                new Vector4(
                    shoreline != null ? shoreline.DepthRange : profile.shorelineDepthRange,
                    shoreline != null ? shoreline.DistanceRange : profile.shorelineDistanceRange,
                    profile.shallowWaveAttenuationDepth,
                    hasShoreline ? 1f : 0f));
            target.SetVector(SolWaterShaderIds.ShorelineSurfaceParams,
                new Vector4(profile.shorelineContactFade,
                    profile.shorelineNormalFlattening, 0f, 0f));
            target.SetVector(SolWaterShaderIds.ShorelineBreakerParams,
                new Vector4(profile.shorelineBreakerStrength,
                    profile.shorelineBreakerWidth,
                    profile.shorelineBreakerWavelength,
                    profile.shorelineBreakerSpeed));
            target.SetVector(SolWaterShaderIds.ShorelineBreakerDetail,
                new Vector4(profile.shorelineBreakerChoppiness,
                    profile.shorelineBreakerFoam, 0f, 0f));

            target.SetColor(SolWaterShaderIds.FoamColor, profile.foamColor);
            target.SetTexture(SolWaterShaderIds.FoamTexture,
                profile.foamTexture != null ? profile.foamTexture : Texture2D.blackTexture);
            target.SetTexture(SolWaterShaderIds.CausticTexture,
                profile.causticTexture != null ? profile.causticTexture : Texture2D.blackTexture);
            target.SetVector(SolWaterShaderIds.FoamDetail,
                new Vector4(profile.foamTextureScale, profile.foamTextureContrast,
                    profile.foamBrightness, profile.foamTexture != null ? 1f : 0f));
            target.SetVector(SolWaterShaderIds.FoamParams,
                new Vector4(profile.crestFoamStrength, profile.shorelineFoamStrength,
                    world.QualityProfile == null || world.QualityProfile.caustics
                        ? profile.causticStrength : 0f,
                    profile.causticScale));

            target.SetVector(SolWaterShaderIds.ReflectionParams,
                new Vector4(profile.skyReflectionStrength, (float)debugMode,
                    screenSpaceReflectionsEnabled ? 1f : 0f, profile.reflectionIntensity));
            SolWaterSkyReflectionState.Resolve().Apply(target);
        }

        /// <summary>
        /// The live shoreline field, when this write is for the body it was measured
        /// against.
        ///
        /// The field encodes depth relative to one water level, so it is only meaningful
        /// for the ocean. Rather than threading that decision through every call site --
        /// three of them here write ocean materials and a fourth writes a finite body's
        /// property block -- the writer asks whether the profile it was handed is the
        /// ocean's. That keeps the destination the only thing a caller has to know, which
        /// is the whole point of this type.
        /// </summary>
        static SolShorelineField ResolveShorelineField(SolWaterProfile profile, SolWaterWorld world)
        {
            SolShorelineField field = SolTerrainShoreline.OceanField;
            if (field == null)
                return null;
            return world.TryGetOcean(out SolWaterBody ocean) && ocean.Profile == profile
                ? field
                : null;
        }

        /// <summary>
        /// Publishes the bounded ripple simulation covering a body, or switches it off.
        ///
        /// Both branches matter: leaving the strength untouched when there is no zone lets
        /// a previous body's field bleed onto this one through the shared material.
        /// </summary>
        internal static void ApplyInteractionZone(
            SolWaterPropertyTarget target, SolWaterInteractionZone zone)
        {
            if (zone == null || !zone.IsReady)
            {
                target.SetFloat(SolWaterShaderIds.InteractionStrength, 0f);
                return;
            }

            Texture state = zone.StateTexture;
            target.SetTexture(SolWaterShaderIds.InteractionTexture, state);
            target.SetVector(SolWaterShaderIds.InteractionMapping, zone.GetShaderMapping());
            target.SetVector(SolWaterShaderIds.InteractionTexel,
                new Vector4(1f / Mathf.Max(1, state.width),
                    1f / Mathf.Max(1, state.height), 0f, 0f));
            target.SetFloat(SolWaterShaderIds.InteractionStrength, zone.HeightStrength);
        }
    }
}
