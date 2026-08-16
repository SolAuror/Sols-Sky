Shader "Sol/Water2/Ocean"
{
    // Shared water globals are declared by SolWaterWaves2.hlsl.
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent-10" "RenderType"="Transparent" }

        HLSLINCLUDE
        #pragma target 4.5
        #pragma multi_compile_instancing
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "SolWaterWaves2.hlsl"
        #include "SolWaterOptics.hlsl"
        #include "../../Water/Shaders/SolAtmosphere.hlsl"

        float _SolWaterBodyHash;
        float4 _SolWaterGeometryParams; // mode: 0 ocean, 1 finite horizontal, 2 waterfall; reserved; static foam; reserved
        float4 _SolWaterBodyFlow; // representative world flow xyz, speed
        float4 _SolWaterShallowColor;
        float4 _SolWaterDeepColor;
        float3 _SolWaterAbsorption;
        float4 _SolWaterFoamColor;
        TEXTURE2D(_SolWaterFoamTexture);
        SAMPLER(sampler_SolWaterFoamTexture);
        TEXTURE2D(_SolWaterCausticTexture);
        SAMPLER(sampler_SolWaterCausticTexture);
        float4 _SolWaterFoamDetail; // world scale, contrast, brightness, texture valid
        float4 _SolWaterRefractionParams; // strength, maximum distance, dispersion, maximum screen offset
        float4 _SolWaterSunParams; // specular strength, lobe roughness, HDR ceiling, reserved
        TEXTURE2D_X(_SolWaterVolumetricTexture);
        SAMPLER(sampler_SolWaterVolumetricTexture);
        float4 _SolWaterVolumetricSurfaceParams; // x: volumetric available
        float4 _SolWaterVisibilityParams; // clarity distance, underwater density, horizon reflection, reserved
        float4 _SolWaterReflectionParams; // dynamic sky blend, debug mode, SSR valid, fallback intensity
        // The Sol sky gradient uniforms and SolWaterDynamicSky live in SolWaterOptics.hlsl
        // so the underwater composition lights its scattering from the same sky.
        TEXTURE2D_X(_SolWaterSceneColor);
        SAMPLER(sampler_SolWaterSceneColor);
        TEXTURE2D_X(_SolWaterPrepassData);
        SAMPLER(sampler_SolWaterPrepassData);
        TEXTURE2D_X(_SolWaterSSRTexture);
        SAMPLER(sampler_SolWaterSSRTexture);
        TEXTURE2D_X(_SolWaterSSRRawTexture);
        SAMPLER(sampler_SolWaterSSRRawTexture);
        TEXTURE2D(_SolWaterPlanarReflectionTexture);
        SAMPLER(sampler_SolWaterPlanarReflectionTexture);
        float4x4 _SolWaterPlanarViewProjection;
        float4 _SolWaterPlanarParams; // valid, body hash, temporal confidence, reserved

        float3 SolWaterOpenSkyReflectionDirection(float3 reflectionDirectionWS)
        {
            // WaterFX folds perturbed water reflections into the upper hemisphere.
            // Without this guard a choppy, upward-facing water surface can sample the
            // ground/nadir sky gradient. At grazing angles that becomes a large grey
            // sheet separated from valid sky samples by a hard directional boundary.
            float3 directionWS = SafeNormalize(reflectionDirectionWS);
            directionWS.y = abs(directionWS.y);
            return SafeNormalize(directionWS);
        }

        float2 SolWaterMirrorUv(float2 uv)
        {
            // WaterFX mirrors a refracted ray at the viewport edge instead of
            // pinning it to a stretched border texel.
            return 1.0 - abs(frac(uv * 0.5) * 2.0 - 1.0);
        }

        float SolWaterArea2(float2 dx, float2 dy)
        {
            return abs(dx.x * dy.y - dx.y * dy.x);
        }


        float2 SolWaterEncodeNormalOct(float3 normalWS)
        {
            normalWS /= max(0.00001,
                abs(normalWS.x) + abs(normalWS.y) + abs(normalWS.z));
            float2 encoded = normalWS.xz;
            if (normalWS.y < 0.0)
            {
                float2 signValue = float2(encoded.x >= 0.0 ? 1.0 : -1.0,
                    encoded.y >= 0.0 ? 1.0 : -1.0);
                encoded = (1.0 - abs(encoded.yx)) * signValue;
            }
            return encoded * 0.5 + 0.5;
        }

        float SolWaterFoamTextureMask(float2 positionXZ, float confidence, float logicalWorldCoordinates)
        {
            confidence = saturate(confidence);
            if (confidence <= 0.0001 || _SolWaterFoamDetail.w < 0.5)
                return confidence;

            float2 logicalXZ = positionXZ + _SolWaterWorldOrigin.xz * logicalWorldCoordinates;
            float2 wind = _SolWaterWind.xz;
            if (_SolWaterGeometryParams.x > 0.5 && dot(_SolWaterBodyFlow.xz, _SolWaterBodyFlow.xz) > 0.0001)
                wind = _SolWaterBodyFlow.xz;
            wind = dot(wind, wind) > 0.0001 ? normalize(wind) : float2(1.0, 0.0);
            float2 crossWind = float2(-wind.y, wind.x);
            float scale = max(0.0001, _SolWaterFoamDetail.x);

            // Stochastic tiling removes the repeat; advection stops the pattern
            // sliding as one rigid sheet. Flow speed is the surface drift: body flow
            // for rivers, wind for open water.
            float2 baseUv = logicalXZ * scale;
            float2 flowDirection = wind + crossWind * 0.22;
            float advectionSpeed = _SolWaterGeometryParams.x > 0.5
                ? max(0.15, length(_SolWaterBodyFlow.xz)) * scale * 2.0
                : max(0.15, _SolWaterWind.w) * scale * 0.9;
            SolWaterAdvectedUv advected = SolWaterBuildAdvectedUv(
                baseUv, flowDirection, advectionSpeed, _SolWaterWaveTime);

            float2 gradientX = ddx(baseUv);
            float2 gradientY = ddy(baseUv);
            float noise0 = SolWaterSampleStochastic(
                TEXTURE2D_ARGS(_SolWaterFoamTexture, sampler_SolWaterFoamTexture),
                advected.uv0, gradientX, gradientY).r;
            float noise1 = SolWaterSampleStochastic(
                TEXTURE2D_ARGS(_SolWaterFoamTexture, sampler_SolWaterFoamTexture),
                advected.uv1, gradientX, gradientY).r;
            // Max rather than a sum: overlapping foam patches occlude, they do not
            // accumulate into a brighter wash.
            float noise = max(noise0 * advected.weight0, noise1 * advected.weight1);

            // Blending three samples averages toward the texture mean, so restore the
            // contrast that costs before thresholding against confidence.
            float contrast = max(0.1, _SolWaterFoamDetail.y);
            float detail = saturate((noise - 0.5) * contrast + 0.5);
            // SAMPLE_TEXTURE2D_GRAD already mips correctly, so this only needs to stop
            // sub-texel shimmer rather than carry the whole distance falloff.
            float footprint = max(length(gradientX), length(gradientY));
            detail = lerp(detail, 0.5, saturate((footprint - 1.2) * 0.8));
            detail = pow(max(detail, 0.0001), max(0.1, contrast * 0.65));
            return saturate(detail - (1.0 - sqrt(confidence)));
        }

        UNITY_INSTANCING_BUFFER_START(SolOceanPatch)
            UNITY_DEFINE_INSTANCED_PROP(float4, _SolOceanPatchData) // tile size, resolution, edge mask, lod
        UNITY_INSTANCING_BUFFER_END(SolOceanPatch)

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float4 tangentOS : TANGENT;
            float4 color : COLOR;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 normalWS : TEXCOORD1;
            float3 velocityWS : TEXCOORD2;
            float4 data : TEXCOORD3; // x foam, y linear eye depth, z skirt, w unused
            float2 surfaceUV : TEXCOORD4;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings WaterVertex(Attributes input)
        {
            Varyings output;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            float3 baseWS = TransformObjectToWorld(input.positionOS.xyz);
            float isSkirt = step(1.5, input.uv.x);
            float4 patchData = UNITY_ACCESS_INSTANCED_PROP(SolOceanPatch, _SolOceanPatchData);
            int resolution = max(2, (int)round(patchData.y));
            int edgeMask = (int)round(patchData.z);
            float vertexStep = patchData.x / resolution;
            // Skirt vertices are authored as their source perimeter vertex with +2 added
            // to the UV, so the offset has to come back off before deriving the grid
            // index. Without this, saturate() collapsed every skirt UV to 1.0 and each
            // skirt vertex believed it sat at the (resolution, resolution) corner. That
            // forced the edge morph on for the whole skirt whenever the patch had a
            // coarse neighbour to the right or above, so the skirt sampled a different
            // wave set than the surface edge it hangs from and visibly tore away from it
            // — the vertical striped walls standing along the patch boundaries.
            float2 patchUV = input.uv - isSkirt * 2.0;
            int2 vertexIndex = (int2)round(saturate(patchUV) * resolution);
            float edgeMorph = 0.0;
            if ((edgeMask & 1) != 0) edgeMorph = max(edgeMorph, 1.0 - saturate(vertexIndex.x * 0.5));
            if ((edgeMask & 2) != 0) edgeMorph = max(edgeMorph, 1.0 - saturate((resolution - vertexIndex.x) * 0.5));
            if ((edgeMask & 4) != 0) edgeMorph = max(edgeMorph, 1.0 - saturate(vertexIndex.y * 0.5));
            if ((edgeMask & 8) != 0) edgeMorph = max(edgeMorph, 1.0 - saturate((resolution - vertexIndex.y) * 0.5));
            float evaluationSpacing = vertexStep * lerp(1.0, 2.0, edgeMorph);
            bool verticalBoundary = (((edgeMask & 1) != 0) && vertexIndex.x == 0)
                || (((edgeMask & 2) != 0) && vertexIndex.x == resolution);
            bool horizontalBoundary = (((edgeMask & 4) != 0) && vertexIndex.y == 0)
                || (((edgeMask & 8) != 0) && vertexIndex.y == resolution);
            // Skirts take the same collapse as the surface vertex they hang from. They
            // used to be excluded, which was necessary while their grid index was being
            // mangled by saturate() — but now that the index is correct, excluding them
            // means the surface edge shifts sideways by one step at every odd vertex
            // while its skirt stays put, tearing a thin sliver open along the boundary.
            // That is the residual hairline left after the skirts stopped tearing wholesale.
            bool stitchVertical = verticalBoundary && ((vertexIndex.y & 1) != 0);
            bool stitchHorizontal = horizontalBoundary && ((vertexIndex.x & 1) != 0);
            if (stitchVertical)
                baseWS.z += ((edgeMask & 2) != 0 ? 1.0 : -1.0) * vertexStep;
            if (stitchHorizontal)
                baseWS.x += ((edgeMask & 8) != 0 ? 1.0 : -1.0) * vertexStep;

            // Odd fine-edge vertices now duplicate a real coarse-grid endpoint before
            // displacement. The resulting degenerate edge triangles cannot form a
            // perspective-space T-junction.
            float3 meshNormalWS = TransformObjectToWorldNormal(input.normalOS);
            meshNormalWS = dot(meshNormalWS, meshNormalWS) > 0.0001
                ? normalize(meshNormalWS) : float3(0.0, 1.0, 0.0);
            float3 meshTangentWS = TransformObjectToWorldDir(input.tangentOS.xyz, true);
            if (dot(meshTangentWS, meshTangentWS) < 0.0001
                || abs(dot(meshTangentWS, meshNormalWS)) > 0.999)
                meshTangentWS = float3(1.0, 0.0, 0.0);
            if (abs(dot(meshTangentWS, meshNormalWS)) > 0.999)
                meshTangentWS = float3(0.0, 0.0, 1.0);
            float3 meshBitangentWS = cross(meshNormalWS, meshTangentWS);
            meshBitangentWS = dot(meshBitangentWS, meshBitangentWS) > 0.0001
                ? normalize(meshBitangentWS) : float3(0.0, 0.0, 1.0);
            float finiteGeometry = step(0.5, _SolWaterGeometryParams.x);
            float2 localFlowDirection = finiteGeometry > 0.5
                ? meshTangentWS.xz : float2(0.0, 0.0);
            SolWaterWaveResult wave = SolEvaluateWaterWaves(baseWS.xz,
                evaluationSpacing, localFlowDirection, finiteGeometry);
            float3 finitePositionWS = baseWS + meshNormalWS * wave.displacement.y;
            float3 finiteNormalWS = normalize(meshBitangentWS * wave.normal.x
                + meshNormalWS * wave.normal.y + meshTangentWS * wave.normal.z);
            float3 positionWS = lerp(baseWS + wave.displacement, finitePositionWS, finiteGeometry);
            output.positionWS = positionWS;
            output.normalWS = normalize(lerp(wave.normal, finiteNormalWS, finiteGeometry));
            output.velocityWS = lerp(wave.velocity,
                meshNormalWS * wave.velocity.y + _SolWaterBodyFlow.xyz, finiteGeometry);
            output.positionCS = TransformWorldToHClip(positionWS);
            float authoredFoam = input.color.r * _SolWaterGeometryParams.z;
            output.data = float4(max(wave.foam, authoredFoam),
                -TransformWorldToView(positionWS).z, isSkirt, input.uv.y);
            output.surfaceUV = input.uv;
            return output;
        }
        ENDHLSL

        Pass
        {
            Name "SolWaterPrepass"
            Tags { "LightMode"="SolWaterPrepass" }
            ZWrite Off
            // Depth is sampled explicitly because Unity 6 Scene View may expose a
            // resolved color target beside an incompatible MSAA depth attachment.
            ZTest Always
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma vertex WaterVertex
            #pragma fragment WaterPrepassFragment

            float4 WaterPrepassFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float sceneRawDepth = SampleSceneDepth(screenUV);
                #if UNITY_REVERSED_Z
                    clip(input.positionCS.z - sceneRawDepth + 0.000001);
                #else
                    clip(sceneRawDepth - input.positionCS.z + 0.000001);
                #endif
                float3 pixelNormal = input.normalWS;
                float pixelFoam = input.data.x;
                if (_SolWaterSpectralParams.x > 0.5)
                    SolEvaluateWaterPixelNormalFoam(input.positionWS.xz, pixelNormal, pixelFoam);
                float2 encodedNormal = SolWaterEncodeNormalOct(normalize(pixelNormal));
                // One resolved target avoids Unity 6 Scene View's native-render-pass
                // MRT compatibility failure: body id, device depth, oct normal.
                return float4(_SolWaterBodyHash, input.positionCS.z, encodedNormal);
            }
            ENDHLSL
        }

        Pass
        {
            Name "SolWaterForward"
            Tags { "LightMode"="SolWaterForward" }
            ZWrite Off
            // Visible water pixels are accepted from the depth-tested prepass.
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex WaterVertex
            #pragma fragment WaterForwardFragment
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            half4 WaterForwardFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float3 normalWS = normalize(input.normalWS);
                float surfaceFoam = input.data.x;
                if (_SolWaterSpectralParams.x > 0.5)
                    SolEvaluateWaterPixelNormalFoam(input.positionWS.xz, normalWS, surfaceFoam);
                float3 viewDirection = SafeNormalize(GetCameraPositionWS() - input.positionWS);
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                // Visibility comes from the depth-tested water prepass. Besides
                // preventing hidden water from shading through terrain, this lets
                // the forward pass work with URP Scene View cameras whose resolved
                // color and MSAA depth targets cannot legally share a native pass.
                float4 visibleWater = SAMPLE_TEXTURE2D_X_LOD(
                    _SolWaterPrepassData, sampler_SolWaterPrepassData, screenUV, 0);
                clip(0.002 - abs(visibleWater.x - _SolWaterBodyHash));
                float rawDepth = SampleSceneDepth(screenUV);
                float sceneDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                float thickness = max(0, sceneDepth - input.data.y);
                #if UNITY_REVERSED_Z
                    float hasSceneGeometry = step(0.00001, rawDepth);
                #else
                    float hasSceneGeometry = step(rawDepth, 0.99999);
                #endif
                float safeSceneDepth = lerp(input.positionCS.z, rawDepth, hasSceneGeometry);
                float3 scenePositionWS = ComputeWorldSpacePosition(
                    screenUV, safeSceneDepth, UNITY_MATRIX_I_VP);
                float initialWaterColumn = max(0.0,
                    input.positionWS.y - scenePositionWS.y);
                float4 shorelineData = SolSampleShorelineData(input.positionWS.xz);
                float finiteBody = step(0.5, _SolWaterGeometryParams.x);
                shorelineData.z *= 1.0 - finiteBody;

                float roughness = saturate(1.0 - _SolWaterOptics.y
                    + _SolWaterWeather.z * _SolWaterWeatherExtended.z);
                float3 normalVS = TransformWorldToViewDir(normalWS, true);
                float eta = max(1.001, _SolWaterOptics.x);
                float3 refractedRay = refract(-viewDirection, normalWS, rcp(eta));
                refractedRay = dot(refractedRay, refractedRay) > 0.0001
                    ? normalize(refractedRay) : -viewDirection;
                // Use vertical water depth rather than eye-depth separation. This
                // prevents a near object at a grazing angle from producing an
                // implausibly long refraction ray.
                float refractionDistance = min(initialWaterColumn
                    / max(0.2, -refractedRay.y), max(0.1, _SolWaterRefractionParams.y))
                    * max(0.0, _SolWaterRefractionParams.x);
                float3 refractedTargetWS = input.positionWS
                    + refractedRay * refractionDistance;
                float4 refractedTargetCS = TransformWorldToHClip(refractedTargetWS);
                float4 refractedScreen = ComputeScreenPos(refractedTargetCS);
                float2 refractUV = refractedScreen.xy
                    / max(0.0001, refractedScreen.w);
                float2 refractDelta = refractUV - screenUV;
                float refractDeltaLength = length(refractDelta);
                refractDelta *= min(1.0, max(0.001, _SolWaterRefractionParams.w)
                    / max(0.000001, refractDeltaLength));
                refractUV = screenUV + refractDelta;
                refractUV = SolWaterMirrorUv(refractUV);
                float refractedRawDepth = SampleSceneDepth(refractUV);
                float refractedEyeDepth = LinearEyeDepth(refractedRawDepth, _ZBufferParams);
                // WaterFX leak rejection: any ray that lands in front of the
                // surface returns to the undisplaced scene/depth sample.
                float refractionConfidence = smoothstep(0.0, 0.12,
                    refractedEyeDepth - input.data.y);
                refractUV = lerp(screenUV, refractUV, refractionConfidence);
                refractedRawDepth = lerp(rawDepth, refractedRawDepth, refractionConfidence);
                refractedEyeDepth = lerp(sceneDepth, refractedEyeDepth, refractionConfidence);
                #if UNITY_REVERSED_Z
                    float refractedHasGeometry = step(0.00001, refractedRawDepth);
                #else
                    float refractedHasGeometry = step(refractedRawDepth, 0.99999);
                #endif
                float safeRefractedDepth = lerp(input.positionCS.z,
                    refractedRawDepth, refractedHasGeometry);
                float3 refractedWorldPosition = ComputeWorldSpacePosition(
                    refractUV, safeRefractedDepth, UNITY_MATRIX_I_VP);
                float2 dispersionOffset = float2(normalVS.x, -normalVS.y)
                    * _SolWaterRefractionParams.z / max(_ScaledScreenParams.xy, 1.0);
                float3 refractedCenter = SAMPLE_TEXTURE2D_X_LOD(
                    _SolWaterSceneColor, sampler_SolWaterSceneColor, refractUV, 0).rgb;
                float3 refracted;
                refracted.r = SAMPLE_TEXTURE2D_X_LOD(_SolWaterSceneColor,
                    sampler_SolWaterSceneColor,
                    SolWaterMirrorUv(refractUV - dispersionOffset), 0).r;
                refracted.g = refractedCenter.g;
                refracted.b = SAMPLE_TEXTURE2D_X_LOD(_SolWaterSceneColor,
                    sampler_SolWaterSceneColor,
                    SolWaterMirrorUv(refractUV + dispersionOffset), 0).b;

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float cloudShadow = lerp(1.0, 0.45, _SolWaterWeatherExtended.x);
                float lightning = 1.0 + _SolWaterWeatherExtended.y
                    * saturate(_SolWaterWeatherExtended.y);

                float f0Base = (1.0 - eta) / (1.0 + eta);
                float f0 = f0Base * f0Base;
                float fresnel = f0 + (1.0 - f0) * pow(1.0 - saturate(dot(normalWS, viewDirection)), 5.0);
                float4 ssr = SAMPLE_TEXTURE2D_X_LOD(
                    _SolWaterSSRTexture, sampler_SolWaterSSRTexture, screenUV, 0);
                float4 rawSsr = SAMPLE_TEXTURE2D_X_LOD(
                    _SolWaterSSRRawTexture, sampler_SolWaterSSRRawTexture, screenUV, 0);
                float3 reflectionDirection = reflect(-viewDirection, normalWS);
                // Keep physically traced SSR on the unmodified ray. Only the open-sky
                // fallback and probe use WaterFX's upper-hemisphere reflection guard.
                float3 openSkyReflectionDirection =
                    SolWaterOpenSkyReflectionDirection(reflectionDirection);
                float3 probe = GlossyEnvironmentReflection(
                    openSkyReflectionDirection, roughness, 1.0);
                float3 dynamicSky = SolWaterDynamicSky(openSkyReflectionDirection);
                float3 reflection = lerp(probe, dynamicSky,
                    saturate(_SolWaterReflectionParams.x));
                float grazingReflection = pow(1.0
                    - saturate(dot(normalWS, viewDirection)), 3.0);
                reflection *= max(0.0, _SolWaterReflectionParams.w)
                    * lerp(1.0, saturate(_SolWaterVisibilityParams.z), grazingReflection);
                float ssrConfidence = saturate(ssr.a) * (1.0 - roughness * 0.65)
                    * saturate(fresnel * 4.0) * saturate(_SolWaterReflectionParams.z);
                reflection = lerp(reflection, ssr.rgb, ssrConfidence);
                float4 planarClip = mul(_SolWaterPlanarViewProjection, float4(input.positionWS, 1.0));
                float2 planarUV = planarClip.xy / max(0.0001, planarClip.w) * 0.5 + 0.5;
                #if UNITY_UV_STARTS_AT_TOP
                    planarUV.y = 1.0 - planarUV.y;
                #endif
                float2 planarEdge = min(planarUV, 1.0 - planarUV);
                float planarConfidence = _SolWaterPlanarParams.x * _SolWaterPlanarParams.z
                    * step(0.0, planarClip.w)
                    * saturate(min(planarEdge.x, planarEdge.y) * 30.0)
                    * step(abs(_SolWaterBodyHash - _SolWaterPlanarParams.y), 0.000001);
                float3 planar = SAMPLE_TEXTURE2D_LOD(_SolWaterPlanarReflectionTexture,
                    sampler_SolWaterPlanarReflectionTexture, saturate(planarUV), 0).rgb;
                reflection = lerp(reflection, planar, planarConfidence);

                // The sun highlight is deliberately NOT folded into `reflection`. That
                // path is multiplied by Fresnel, which is 0.02-0.15 at the shallow view
                // angles where glitter actually lives, so the highlight was two orders
                // of magnitude too dim. It is added to the composed colour instead.

                // Project caustics onto the refracted scene point. The seamless
                // caustic map provides coherent cell shapes while Water 2's live
                // spectral compression subtly focuses them. Logical-world sampling,
                // rotated scales, and derivative filtering keep the result stable.
                float3 causticLighting = 0.0;
                if (_SolWaterFoamParams.z > 0.0001)
                {
                    float waterColumn = max(0.0,
                        input.positionWS.y - refractedWorldPosition.y);
                    Light causticLight = GetMainLight(
                        TransformWorldToShadowCoord(refractedWorldPosition));
                    float lightElevation = max(0.08, causticLight.direction.y);
                    float2 causticSurfaceXZ = refractedWorldPosition.xz
                        + causticLight.direction.xz * (waterColumn / lightElevation);
                    float3 causticNormal = float3(0.0, 1.0, 0.0);
                    float causticFoam = 0.0;
                    SolEvaluateWaterPixelNormalFoam(
                        causticSurfaceXZ, causticNormal, causticFoam);
                    float causticSlope = max(0.01, _SolWaterFoamParams.w) * 0.35;
                    causticNormal = normalize(float3(causticNormal.x * causticSlope,
                        max(0.08, causticNormal.y), causticNormal.z * causticSlope));
                    float3 causticRay = refract(-causticLight.direction,
                        causticNormal, rcp(eta));
                    float causticRayDepth = max(0.08, -causticRay.y);
                    float2 focusedPosition = causticSurfaceXZ
                        + causticRay.xz * (waterColumn / causticRayDepth);
                    float originalArea = SolWaterArea2(ddx(refractedWorldPosition.xz),
                        ddy(refractedWorldPosition.xz));
                    float focusedArea = SolWaterArea2(ddx(focusedPosition),
                        ddy(focusedPosition));
                    float focusingRatio = originalArea
                        / max(originalArea * 0.08, focusedArea + 0.000001);

                    float2 logicalCausticXZ = causticSurfaceXZ + _SolWaterWorldOrigin.xz;
                    float2 causticWind = _SolWaterWind.xz;
                    causticWind = dot(causticWind, causticWind) > 0.0001
                        ? normalize(causticWind) : float2(1.0, 0.0);
                    float causticScale = max(0.25, _SolWaterFoamParams.w);
                    float2 causticDrift = causticWind * _SolWaterWaveTime * 0.035;
                    float2 warpedCausticXZ = logicalCausticXZ
                        + causticNormal.xz * waterColumn * 1.4;
                    float focusingModulation = lerp(0.78, 1.22,
                        saturate((focusingRatio - 1.0) * 0.4));
                    float causticPattern;

                    if (_SolWaterCausticArrayParams.w > 0.5)
                    {
                        // Live caustics rendered from this frame's FFT displacement,
                        // sampled over exactly the cascade domain each slice was drawn
                        // across so the cells sit on the crests that produced them.
                        causticPattern = SolWaterSampleCausticArray(
                            warpedCausticXZ, waterColumn, input.positionCS.xy)
                            * focusingModulation;
                    }
                    else
                    {
                        // Low tier, or caustics disabled: the authored seamless texture,
                        // two rotated incommensurate taps as before.
                        float2 causticUv0 = warpedCausticXZ / causticScale + causticDrift;
                        float2 rotatedCausticXZ = mul(float2x2(0.819152, -0.573576,
                            0.573576, 0.819152), warpedCausticXZ);
                        float2 causticUv1 = rotatedCausticXZ / (causticScale * 1.731)
                            - causticDrift * 0.63 + float2(0.37, 0.61);
                        float causticTexture0 = SAMPLE_TEXTURE2D_GRAD(_SolWaterCausticTexture,
                            sampler_SolWaterCausticTexture, causticUv0,
                            ddx(causticUv0), ddy(causticUv0)).r;
                        float causticTexture1 = SAMPLE_TEXTURE2D_GRAD(_SolWaterCausticTexture,
                            sampler_SolWaterCausticTexture, causticUv1,
                            ddx(causticUv1), ddy(causticUv1)).r;
                        causticPattern = smoothstep(0.12, 0.82,
                            causticTexture0 * 0.68 + causticTexture1 * 0.32)
                            * focusingModulation;
                    }
                    // Signed, so the dark interstitials survive. The authored-texture
                    // fallback is positive-only, so it is re-centred to match.
                    if (_SolWaterCausticArrayParams.w <= 0.5)
                        causticPattern = (causticPattern - 0.35) * 1.6;
                    causticPattern = clamp(causticPattern, -1.0, 4.0);
                    // WaterFX ramps in quadratically with depth and never fades back out;
                    // absorption already dims the sea floor with distance. The previous
                    // window closed at 2.5x the clarity coefficient, which at the shipped
                    // value of 10 deleted caustics past about 25 m and treated a unitless
                    // coefficient as if it were a distance in metres.
                    float causticDepthFade = saturate(waterColumn * waterColumn);
                    float causticVisibility = refractedHasGeometry
                        * step(0.02, causticLight.direction.y)
                        * causticLight.shadowAttenuation * cloudShadow
                        * causticDepthFade * (1.0 - saturate(causticFoam));
                    causticLighting = causticLight.color * causticPattern
                        * causticVisibility * max(0.0, _SolWaterFoamParams.z);
                }
                // Caustics brighten the seabed before the water column absorbs it, so
                // depth still dims them. Applying them after absorption also scaled the
                // volume scattering term, which has nothing to do with the sea floor.
                // The lower bound lets the dark cells actually darken the floor without
                // ever driving it to black; WaterFX gains by 5x here for the same reason
                // the pattern reads at all against a lit sea bed.
                refracted *= max(0.45, 1.0 + clamp(causticLighting * 5.0, -0.45, 4.0));

                // refractionMaximumDistance bounds the screen-space refraction offset
                // only. Using it as the optical path as well capped open water at a few
                // metres of absorption, so the sky sampled behind the surface was
                // transmitted almost intact and the ocean read as flat grey.
                float rayLength = lerp(SOL_WATER_MAX_RAY_LENGTH,
                    length(refractedWorldPosition - input.positionWS),
                    refractedHasGeometry);
                float3 absorptionColor = SolWaterNormalizeAbsorption(_SolWaterAbsorption);
                float clarityDistance = max(0.5, _SolWaterVisibilityParams.x);
                float4 absorption = SolWaterComputeAbsorption(clarityDistance,
                    absorptionColor, rayLength);
                float3 transmittance = absorption.rgb;
                // Volume scattering is lit by sun elevation and the live sky instead of
                // being a fixed authored colour, so the water tracks time of day.
                float3 scattering = SolWaterVolumeScattering(
                    _SolWaterShallowColor.rgb,
                    SolWaterDynamicSky(float3(0.0, 1.0, 0.0)),
                    mainLight.color, mainLight.direction, cloudShadow,
                    _SolWaterOptics.z);
                // Volumetric scattering, when available, replaces the analytic term with
                // a shadowed and caustic-modulated one. The analytic value stays as the
                // floor so disabling the pass changes the detail, not the water colour.
                if (_SolWaterVolumetricSurfaceParams.x > 0.5)
                {
                    float4 volume = SAMPLE_TEXTURE2D_X_LOD(_SolWaterVolumetricTexture,
                        sampler_SolWaterVolumetricTexture, screenUV, 0);
                    scattering = max(scattering * lerp(0.35, 1.0, volume.a),
                        volume.rgb);
                }
                // Scattering replaces the absorbed scene by extinction. Adding it on top
                // instead is what let deep water stay bright and desaturated.
                refracted = lerp(absorption.rgb * refracted, scattering, absorption.a);

                float2 shorelineUV = (input.positionWS.xz + _SolWaterWorldOrigin.xz
                    - _SolWaterShorelineParams.xy)
                    / max(_SolWaterShorelineParams.zw, 0.001) + 0.5;
                float2 shorelineInside = step(0.0, shorelineUV) * step(shorelineUV, 1.0);
                float authoredShoreline = shorelineInside.x * shorelineInside.y
                    * SAMPLE_TEXTURE2D_LOD(_SolWaterShorelineMask,
                        sampler_SolWaterShorelineMask, shorelineUV, 0).r;
                authoredShoreline *= 1.0 - finiteBody;
                float shorelineWidth = max(_SolWaterShorelineDetail.y, 0.001);
                float widthContact = (1.0 - smoothstep(0.025, shorelineWidth,
                    thickness)) * step(0.002, thickness) * hasSceneGeometry;
                float shorelineDistanceAa = fwidth(shorelineData.y) + 0.02;
                float signedDistanceContact = 1.0 - smoothstep(shorelineWidth,
                    shorelineWidth + shorelineDistanceAa, abs(shorelineData.y));
                signedDistanceContact *= step(0.0, shorelineData.x);
                widthContact = lerp(widthContact, signedDistanceContact,
                    saturate(shorelineData.z));
                float shorelineFoam = saturate(max(widthContact, authoredShoreline
                    * _SolWaterShorelineDetail.x)) * saturate(_SolWaterFoamParams.y);
                // Breaking-wave foam is already a confidence value from the horizontal
                // displacement Jacobian. Squaring it suppresses isolated numerical specks.
                float crestFoam = surfaceFoam * surfaceFoam;
                float foamConfidence = saturate(crestFoam
                    + shorelineFoam * (1.0 - crestFoam));
                float waterfallMode = step(1.5, _SolWaterGeometryParams.x);
                float2 foamCoordinates = lerp(input.positionWS.xz,
                    float2(input.surfaceUV.y, input.surfaceUV.x) * 4.0, waterfallMode);
                float foam = SolWaterFoamTextureMask(foamCoordinates, foamConfidence,
                    1.0 - waterfallMode);
                float foamNdotL = saturate(dot(normalWS, mainLight.direction));
                float3 foamLighting = SolWaterDynamicSky(normalWS) * 0.55
                    + mainLight.color * lerp(0.15, 1.0, foamNdotL)
                    * mainLight.shadowAttenuation * cloudShadow;
                // Ceiling only. A lower clamp made foam self-illuminate in shadow.
                foamLighting = min(foamLighting, 1.15);
                float3 litFoam = _SolWaterFoamColor.rgb * foamLighting
                    * max(0.0, _SolWaterFoamDetail.z);
                float3 waterSurface = lerp(reflection, litFoam, foam);
                waterSurface = lerp(waterSurface,
                    waterSurface * lerp(1.0, 0.72, saturate(_SolWaterShorelineDetail.z)),
                    saturate(shorelineFoam * _SolWaterShorelineDetail.z));
                // Refraction and absorption already own the background, so the surface
                // is opaque except where it meets land. Blending the framebuffer back in
                // a second time only lowered contrast.
                float contactMeasure = shorelineData.z > 0.5
                    ? min(max(0.0, shorelineData.x), max(0.0, shorelineData.y))
                    : max(thickness, initialWaterColumn);
                float contactFade = smoothstep(0.0,
                    max(0.01, _SolWaterShorelineSurfaceParams.x) + fwidth(contactMeasure),
                    contactMeasure);
                float opacity = lerp(contactFade, 1.0, saturate(foam * 0.85));

                float3 sourceColor = lerp(refracted, waterSurface, saturate(fresnel + foam * 0.65));
                float sunFade = pow(saturate(abs(sceneDepth - input.data.y)), 5.0);
                // Weather roughens the lobe: rain and cloud widen the highlight into a
                // sheen rather than a tight glitter path.
                float sunRoughness = max(0.005, _SolWaterSunParams.y
                    + _SolWaterWeather.z * _SolWaterWeatherExtended.z * 0.5);
                float3 sunSpecular = SolWaterSunSpecular(normalWS, viewDirection,
                    mainLight.direction, mainLight.color,
                    mainLight.shadowAttenuation * cloudShadow * lightning,
                    input.data.y, sunRoughness,
                    max(0.0, _SolWaterSunParams.x), max(0.0, _SolWaterSunParams.z),
                    SolWaterNormalVariance(normalWS));
                sunSpecular *= saturate((1.0 - foam) * sunFade);
                sourceColor += sunSpecular;

                // Atmosphere is applied once, to the composed colour. The scene colour
                // sampled for refraction was already fogged over camera-to-scene; this
                // fogs the shorter camera-to-surface span, and the reflection and
                // scattering terms were previously never fogged at all.
                sourceColor = SolApplyAtmosphere(
                    sourceColor,
                    GetCameraPositionWS(),
                    input.positionWS,
                    -viewDirection,
                    0.0);

                int debugMode = (int)round(_SolWaterReflectionParams.y);
                if (debugMode == 1)
                    return half4(rawSsr.rgb, 1.0);
                if (debugMode == 2)
                    return half4(ssr.rgb, 1.0);
                if (debugMode == 3)
                    return half4(ssr.aaa, 1.0);
                if (debugMode == 4)
                    return half4(dynamicSky, 1.0);
                if (debugMode == 5)
                    return half4(foamConfidence.xxx, 1.0);
                if (debugMode == 6)
                    return half4(refracted, 1.0);
                if (debugMode == 7)
                    return half4(causticLighting, 1.0);
                if (debugMode == 8)
                    return half4(scattering, 1.0);
                if (debugMode == 9)
                    return half4(transmittance, 1.0);
                if (debugMode == 10)
                    return half4(opacity.xxx, 1.0);
                if (debugMode == 11)
                    return half4(sunSpecular, 1.0);
                if (debugMode == 12)
                    return half4(SAMPLE_TEXTURE2D_X_LOD(_SolWaterVolumetricTexture,
                        sampler_SolWaterVolumetricTexture, screenUV, 0).rgb, 1.0);
                return half4(sourceColor, opacity);
            }
            ENDHLSL
        }

        Pass
        {
            // Optional depth write for post-process consumers.
            //
            // The forward pass writes no depth, so depth-of-field, SSAO and motion
            // vectors all see straight through the water to whatever is behind it.
            // This pass fills the camera depth buffer with the displaced water surface
            // and writes no colour at all.
            //
            // Binding depth here is legal despite the Scene View constraint the other
            // passes work around: that constraint is about pairing a *resolved colour*
            // target with an MSAA depth target. A depth-only pass binds no colour
            // attachment, so the incompatible pairing cannot arise.
            Name "SolWaterDepthWrite"
            Tags { "LightMode"="SolWaterDepthWrite" }
            ZWrite On
            ZTest LEqual
            Cull Off
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex WaterVertex
            #pragma fragment WaterDepthFragment

            void WaterDepthFragment(Varyings input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                // Same visibility test the forward pass uses, so depth is only written
                // where this body actually shaded. Without it, water hidden behind
                // terrain would still occlude post-process effects.
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float4 visibleWater = SAMPLE_TEXTURE2D_X_LOD(
                    _SolWaterPrepassData, sampler_SolWaterPrepassData, screenUV, 0);
                clip(0.002 - abs(visibleWater.x - _SolWaterBodyHash));
            }
            ENDHLSL
        }
    }
    Fallback Off
}
