Shader "Hidden/Sol/Water2/Volumetrics"
{
    // In-water volumetric scattering: light shafts and shadowed water volume.
    //
    // WaterFX derives the entire underwater colour from this pass. Sol keeps the
    // analytic scattering in SolWaterOptics as the base, and treats this as the
    // shadowed, caustic-modulated refinement layered on top, so the water still
    // reads correctly when the pass is disabled by quality tier.
    //
    // Structured to mirror SolAtmosphere.shader: half-resolution raymarch, then
    // temporal reprojection, then a depth-aware spatial filter.
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "SolWaterOptics.hlsl"

        TEXTURE2D_X(_SolWaterPrepassData);
        TEXTURE2D_X(_SolWaterVolumetricHistory);
        SAMPLER(sampler_SolWaterVolumetricHistory);

        // x: step count, y: max ray length, z: scattering intensity, w: caustic influence
        float4 _SolWaterVolumetricParams;
        // rgb: turbidity colour of the resolved body, a: clarity distance
        float4 _SolWaterVolumetricTurbidity;
        // Double-precision logical origin, so caustics sampled from the marched position
        // stay put through a floating-origin shift. Declared here rather than pulled in
        // from SolWaterWaves2.hlsl, which this pass does not otherwise need.
        float4 _SolWaterWorldOrigin;
        // x: history weight, y: neighbourhood clamp expansion
        float4 _SolWaterVolumetricTemporalParams;
        float4x4 _SolWaterVolumetricPreviousViewProjection;
        // x: camera is submerged, y: water surface height, zw reserved
        float4 _SolWaterVolumetricViewParams;

        float4 SolVolumePrepass(float2 uv)
        {
            return SAMPLE_TEXTURE2D_X_LOD(_SolWaterPrepassData, sampler_PointClamp, uv, 0);
        }

        bool SolVolumeHasSceneDepth(float rawDepth)
        {
        #if UNITY_REVERSED_Z
            return rawDepth > 0.00001;
        #else
            return rawDepth < 0.99999;
        #endif
        }
        ENDHLSL

        Pass
        {
            Name "WaterVolumetricRaymarch"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment VolumetricFragment
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            half4 VolumetricFragment(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float4 waterData = SolVolumePrepass(uv);
                bool submerged = _SolWaterVolumetricViewParams.x > 0.5;
                // Seen from above, only pixels the surface covers have a volume behind
                // them. Submerged, the eye is already inside the medium and every pixel
                // does — including the ones looking at sea bed the surface never covers,
                // which is most of the screen looking down.
                if (!submerged && waterData.x <= 0.000001)
                    return 0;

                // A mid-range depth rather than the far plane: inverse-VP precision at
                // the far plane is poor enough to produce infinities, and this is only
                // needed as a direction.
                float3 viewDirectionWS = SafeNormalize(
                    ComputeWorldSpacePosition(uv, 0.5, UNITY_MATRIX_I_VP)
                        - GetCameraPositionWS());

                // Entry is where the ray enters the water: the surface when looking in
                // from outside, the camera itself when already under it.
                float3 entryWS = submerged
                    ? GetCameraPositionWS()
                    : ComputeWorldSpacePosition(uv, waterData.y, UNITY_MATRIX_I_VP);
                float rawSceneDepth = SampleSceneDepth(uv);
                float3 exitWS = SolVolumeHasSceneDepth(rawSceneDepth)
                    ? ComputeWorldSpacePosition(uv, rawSceneDepth, UNITY_MATRIX_I_VP)
                    : entryWS + viewDirectionWS
                        * max(1.0, _SolWaterVolumetricParams.y);

                // Submerged, a ray angled upward leaves through the surface and stops
                // scattering there; without this the march keeps accumulating water above
                // the waterline and the sky gains a haze that belongs to the sea.
                if (submerged)
                {
                    float3 span = exitWS - entryWS;
                    if (span.y > 0.0001)
                    {
                        float surfaceT =
                            (_SolWaterVolumetricViewParams.y - entryWS.y) / span.y;
                        if (surfaceT >= 0.0 && surfaceT < 1.0)
                            exitWS = entryWS + span * surfaceT;
                    }
                }

                float3 rayVector = exitWS - entryWS;
                float rayLength = length(rayVector);
                if (rayLength < 0.001)
                    return 0;
                // Past the clarity distance nothing further contributes, so marching
                // beyond it only wastes samples.
                rayLength = min(rayLength, max(1.0, _SolWaterVolumetricParams.y));
                float3 rayDirection = rayVector / max(0.0001, length(rayVector));

                int steps = clamp((int)_SolWaterVolumetricParams.x, 4, 32);
                float stepSize = rayLength / steps;
                Light mainLight = GetMainLight();

                // Blue-noise-free interleaved gradient jitter breaks the banding that a
                // fixed step start produces; the temporal pass then resolves the noise.
                float jitter = frac(52.9829189 * frac(dot(input.positionCS.xy,
                    float2(0.06711056, 0.00583715))));

                float3 scattering = 0.0;
                float transmittance = 1.0;
                float shadowAccumulation = 0.0;

                [loop]
                for (int i = 0; i < 32; i++)
                {
                    if (i >= steps)
                        break;
                    float distanceAlongRay = (i + jitter) * stepSize;
                    float3 samplePosition = entryWS + rayDirection * distanceAlongRay;

                    float shadow = MainLightRealtimeShadow(
                        TransformWorldToShadowCoord(samplePosition));
                    shadowAccumulation += shadow;

                    // Caustic convergence brightens the shafts where the surface above
                    // focuses light, which is what makes them read as underwater rather
                    // than generic fog.
                    float caustic = 0.0;
                    if (_SolWaterCausticArrayParams.w > 0.5
                        && _SolWaterVolumetricParams.w > 0.0001)
                    {
                        // Depth of water above the sample, which sets how far the
                        // caustic field has converged by the time light reaches it.
                        // Measured from the surface, not from the entry point: submerged
                        // those differ by however deep the camera is, and using the
                        // camera would make the shafts change character as it descends.
                        float causticSurfaceY = submerged
                            ? _SolWaterVolumetricViewParams.y : entryWS.y;
                        float columnAbove = max(0.0, causticSurfaceY - samplePosition.y);
                        caustic = SolWaterSampleCausticArray(
                            samplePosition.xz + _SolWaterWorldOrigin.xz,
                            columnAbove, input.positionCS.xy)
                            * _SolWaterVolumetricParams.w;
                    }

                    // Exponential extinction along the segment, integrated per slice.
                    float sliceTransmittance = exp(-stepSize / max(0.5,
                        _SolWaterVolumetricTurbidity.a));
                    scattering += transmittance * (1.0 - sliceTransmittance)
                        * shadow * (1.0 + caustic);
                    transmittance *= sliceTransmittance;
                    if (transmittance < 0.003)
                        break;
                }

                float sunPhase = SolWaterSunElevationPhase(mainLight.direction);
                float3 inscatter = _SolWaterVolumetricTurbidity.rgb
                    * (mainLight.color * sunPhase
                        + 0.5 * SolWaterDynamicSky(float3(0.0, 1.0, 0.0)))
                    * scattering * max(0.0, _SolWaterVolumetricParams.z);

                // Alpha carries mean surface shadowing so the surface pass can darken
                // its own scattering under the same occluders.
                return half4(inscatter, saturate(shadowAccumulation / max(1, steps)));
            }
            ENDHLSL
        }

        Pass
        {
            Name "WaterVolumetricTemporal"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment TemporalFragment

            half4 TemporalFragment(Varyings input) : SV_Target
            {
                float4 current = SAMPLE_TEXTURE2D_X_LOD(
                    _BlitTexture, sampler_LinearClamp, input.texcoord, 0);
                float weight = _SolWaterVolumetricTemporalParams.x;
                if (weight <= 0.0001)
                    return current;

                bool submerged = _SolWaterVolumetricViewParams.x > 0.5;
                float4 waterData = SolVolumePrepass(input.texcoord);
                if (!submerged && waterData.x <= 0.000001)
                    return current;

                // Reprojection needs the depth of whatever the marched column ended on.
                // Looking in from above that is the surface the prepass recorded;
                // submerged the prepass is empty over most of the screen, so the sea bed
                // stands in. With neither, there is nothing to reproject against.
                float reprojectDepth = waterData.y;
                if (submerged)
                {
                    reprojectDepth = SampleSceneDepth(input.texcoord);
                    if (!SolVolumeHasSceneDepth(reprojectDepth))
                        return current;
                }
                float3 positionWS = ComputeWorldSpacePosition(
                    input.texcoord, reprojectDepth, UNITY_MATRIX_I_VP);
                float4 previousClip = mul(_SolWaterVolumetricPreviousViewProjection,
                    float4(positionWS, 1.0));
                if (previousClip.w <= 0.0001)
                    return current;
                float2 previousUv = previousClip.xy / previousClip.w * 0.5 + 0.5;
                #if UNITY_UV_STARTS_AT_TOP
                    previousUv.y = 1.0 - previousUv.y;
                #endif
                if (any(previousUv < 0.001) || any(previousUv > 0.999))
                    return current;

                float4 history = SAMPLE_TEXTURE2D_X_LOD(_SolWaterVolumetricHistory,
                    sampler_SolWaterVolumetricHistory, previousUv, 0);

                // Neighbourhood clamp: reprojection cannot know that an occluder moved,
                // so bound the history to what this frame's neighbours actually produced.
                float2 texel = _BlitTexture_TexelSize.xy;
                float4 minimumValue = current;
                float4 maximumValue = current;
                [unroll]
                for (int i = 0; i < 4; i++)
                {
                    float2 offset = float2(i == 0 ? -1 : i == 1 ? 1 : 0,
                        i == 2 ? -1 : i == 3 ? 1 : 0) * texel;
                    float4 neighbour = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture,
                        sampler_LinearClamp, input.texcoord + offset, 0);
                    minimumValue = min(minimumValue, neighbour);
                    maximumValue = max(maximumValue, neighbour);
                }
                float4 expansion = (maximumValue - minimumValue)
                    * _SolWaterVolumetricTemporalParams.y;
                history = clamp(history, minimumValue - expansion, maximumValue + expansion);
                return lerp(current, history, weight);
            }
            ENDHLSL
        }

        Pass
        {
            Name "WaterVolumetricSpatialFilter"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FilterFragment

            half4 FilterFragment(Varyings input) : SV_Target
            {
                float4 center = SAMPLE_TEXTURE2D_X_LOD(
                    _BlitTexture, sampler_LinearClamp, input.texcoord, 0);
                // Submerged the prepass is empty over most of the screen, but the march
                // still produced a jittered result there that needs resolving; the body
                // comparison below then trivially matches and the taps blend as intended.
                bool submerged = _SolWaterVolumetricViewParams.x > 0.5;
                float4 waterData = SolVolumePrepass(input.texcoord);
                if (!submerged && waterData.x <= 0.000001)
                    return center;

                float2 texel = _BlitTexture_TexelSize.xy;
                float4 accumulated = center;
                float weightSum = 1.0;
                [unroll]
                for (int i = 0; i < 4; i++)
                {
                    float2 offset = float2(i == 0 ? -1 : i == 1 ? 1 : 0,
                        i == 2 ? -1 : i == 3 ? 1 : 0) * texel;
                    float2 sampleUv = input.texcoord + offset;
                    // Never blend across a water body boundary or a depth discontinuity;
                    // that is what smears shafts over the shoreline.
                    float4 neighbourData = SolVolumePrepass(sampleUv);
                    if (abs(neighbourData.x - waterData.x) > 0.002)
                        continue;
                    accumulated += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture,
                        sampler_LinearClamp, sampleUv, 0);
                    weightSum += 1.0;
                }
                return accumulated / max(1.0, weightSum);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
