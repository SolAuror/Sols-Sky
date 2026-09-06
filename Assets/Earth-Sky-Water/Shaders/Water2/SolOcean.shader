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

        // Debug view selectors, in _SolWaterReflectionParams.y.
        //
        // These MUST stay in the same order as SolWaterDebugMode in
        // SolWaterRendererFeature.cs, which is what the renderer feature's inspector
        // dropdown writes. Reordering the enum without editing this block silently
        // relabels every debug view, so the names live here rather than as bare
        // integers scattered through the fragment shader.
        #define SOL_WATER_DEBUG_DISABLED                        0
        #define SOL_WATER_DEBUG_RAW_SSR                         1
        #define SOL_WATER_DEBUG_VALIDATED_SSR                   2
        #define SOL_WATER_DEBUG_REFLECTION_CONFIDENCE           3
        #define SOL_WATER_DEBUG_FALLBACK_SKY                    4
        #define SOL_WATER_DEBUG_FOAM_CONFIDENCE                 5
        #define SOL_WATER_DEBUG_REFRACTION                      6
        #define SOL_WATER_DEBUG_CAUSTICS                        7
        #define SOL_WATER_DEBUG_SCATTERING                      8
        #define SOL_WATER_DEBUG_TRANSMITTANCE                   9
        #define SOL_WATER_DEBUG_OPACITY                         10
        #define SOL_WATER_DEBUG_SUN_SPECULAR                    11
        #define SOL_WATER_DEBUG_VOLUMETRIC_SCATTERING           12
        #define SOL_WATER_DEBUG_SURFACE_OVERLAP                 13
        #define SOL_WATER_DEBUG_PATCH_SKIRTS                    14
        #define SOL_WATER_DEBUG_REFRACTION_CONFIDENCE           15
        #define SOL_WATER_DEBUG_ABSORPTION_PATH_LENGTH          16
        #define SOL_WATER_DEBUG_SURFACE_NORMAL                  17
        #define SOL_WATER_DEBUG_SPECTRAL_DETAIL_FADE            18
        #define SOL_WATER_DEBUG_CAUSTIC_RESPONSE                19

        float _SolWaterBodyHash;
        // Metres the crack-hiding skirt hangs below the surface. The patch mesh authors
        // its skirt one unit down and this scales it, so a sea state that moves with the
        // wind cannot force a mesh rebuild.
        //
        // Only the clipmap draw set publishes it, and only the clipmap patch mesh carries
        // skirt vertices: the three spline generators all keep uv.x within [0,1] and tile
        // on uv.y, so isSkirt below is identically zero on every finite body and this is
        // never read there.
        float _SolWaterSkirtDepth;
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
        TEXTURE2D_X(_SolWaterSSRTexture);
        SAMPLER(sampler_SolWaterSSRTexture);
        TEXTURE2D_X(_SolWaterSSRRawTexture);
        SAMPLER(sampler_SolWaterSSRRawTexture);
        TEXTURE2D(_SolWaterPlanarReflectionTexture);
        SAMPLER(sampler_SolWaterPlanarReflectionTexture);
        float4x4 _SolWaterPlanarViewProjection;
        float4 _SolWaterPlanarParams; // valid, body hash, temporal confidence, reserved

        // True only for the fragment that won the prepass at this pixel.
        //
        // The prepass now resolves water against water with its own depth buffer, so
        // _SolWaterPrepassData carries the nearest water surface: which body it belongs
        // to, and the device depth it was at. Every later pass has to reject anything
        // behind that, because those passes alpha blend — a fragment that is genuinely
        // occluded would otherwise still composite over the surface in front of it.
        // Two things arrive here occluded: a distant patch seen through a near crest,
        // and the skirt wall hanging below a detail boundary.
        //
        // The comparison is on linear eye depth with a one-sided relative tolerance,
        // not on equality. The prepass target is a half float, so the stored depth
        // carries roughly a 0.05% relative error, and an exact test would drop the
        // winning fragment on a one-ULP disagreement — the failure mode there is water
        // vanishing rather than looking wrong. A relative tolerance also tracks the
        // artefact: it widens with distance at the same rate the occluded sliver
        // shrinks toward sub-pixel.
        bool SolWaterFragmentIsNearest(float2 screenUV, float fragmentDeviceDepth,
            out float4 prepassData)
        {
            prepassData = SAMPLE_TEXTURE2D_X_LOD(
                _SolWaterPrepassData, sampler_PointClamp, screenUV, 0);
            // Point sampling matters as much as the depth test: a bilinear tap blends
            // the body id and depth of up to four different fragments, which both
            // erodes a hairline at every water silhouette and feeds the test below a
            // depth that belongs to no actual surface.
            if (abs(prepassData.x - _SolWaterBodyHash) >= 0.002)
                return false;
            float nearestEyeDepth = LinearEyeDepth(prepassData.y, _ZBufferParams);
            float fragmentEyeDepth = LinearEyeDepth(fragmentDeviceDepth, _ZBufferParams);
            return fragmentEyeDepth <= nearestEyeDepth * 1.0015 + 0.001;
        }

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
            float4 data : TEXCOORD3; // x foam, y linear eye depth, z skirt, w mesh v
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
            float3 positionOS = input.positionOS.xyz;
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

            // A skirt only has a crack to plug where this patch meets a coarser one.
            // The mesh carries a skirt around the whole perimeter because it is shared
            // by every patch, so on the three edges that typically face a same-sized
            // neighbour it is pure overdraw hanging below the surface — plainly visible
            // from underwater or side-on at a crest. Collapse those back onto the
            // surface so they occupy no volume.
            //
            // The mesh authors the skirt exactly one unit down, and the depth arrives as
            // _SolWaterSkirtDepth, so the needed skirts are scaled here rather than baked.
            // The depth is derived from the sea state, which moves with the wind; baking
            // it made the shared patch mesh a function of wind speed and regenerated it
            // through every weather transition.
            if (isSkirt > 0.5)
            {
                bool skirtNeeded =
                    (vertexIndex.x == 0 && (edgeMask & 1) != 0)
                    || (vertexIndex.x == resolution && (edgeMask & 2) != 0)
                    || (vertexIndex.y == 0 && (edgeMask & 4) != 0)
                    || (vertexIndex.y == resolution && (edgeMask & 8) != 0);
                positionOS.y = skirtNeeded ? positionOS.y * _SolWaterSkirtDepth : 0.0;
            }
            float3 baseWS = TransformObjectToWorld(positionOS);

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
            // Water against water is resolved here, and only here. The pass owns a
            // private non-MSAA depth buffer sized to its own colour target, so the
            // nearest water fragment per pixel is the one that survives into
            // _SolWaterPrepassData and the alpha-blending passes downstream can gate
            // themselves on it.
            //
            // Binding depth here does not reintroduce the Scene View problem the rest
            // of this shader works around. That constraint is about pairing a resolved
            // colour target with an MSAA depth attachment; both attachments here are
            // ours and both are non-MSAA, so the sample counts cannot disagree.
            //
            // Water against *scene* geometry is still a clip() against the camera depth
            // texture below, not a depth test, because the camera depth buffer is
            // exactly the attachment that cannot be bound safely.
            ZWrite On
            ZTest LEqual
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
            // Still no depth state, and still deliberately so: this pass alpha blends,
            // so a fragment that passes a hardware depth test would go on to composite
            // over whatever already blended, and the occluded surface would survive in
            // the result anyway. Occlusion needs exactly one *shaded* fragment per
            // pixel, not merely one that passes a test.
            //
            // That single fragment is selected by SolWaterFragmentIsNearest against the
            // prepass, which is the pass that actually owns a depth buffer. Doing it in
            // the shader rather than through an attachment also keeps this pass legal
            // beside URP Scene View's resolved-colour/MSAA-depth pairing.
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
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                // Visibility comes from the depth-resolved water prepass: the right body
                // for this pixel, and the nearest surface of it. Besides preventing
                // hidden water from shading through terrain, this lets the forward pass
                // work with URP Scene View cameras whose resolved color and MSAA depth
                // targets cannot legally share a native pass.
                //
                // Resolved before anything else so an occluded fragment costs one texture
                // fetch rather than a full spectral normal evaluation. Derivatives taken
                // further down stay valid: a discarded fragment becomes a helper
                // invocation and keeps feeding its quad neighbours.
                float4 visibleWater;
                bool isNearestSurface = SolWaterFragmentIsNearest(
                    screenUV, input.positionCS.z, visibleWater);
                int debugMode = (int)round(_SolWaterReflectionParams.y);
                // These two views run before the discard on purpose: they exist to show
                // what the nearest-surface test throws away, and discarding first would
                // leave nothing to look at.
                if (debugMode == SOL_WATER_DEBUG_SURFACE_OVERLAP)
                {
                    if (isNearestSurface
                        || abs(visibleWater.x - _SolWaterBodyHash) >= 0.002)
                        discard;
                    return half4(1.0, 0.0, 0.0, 1.0);
                }
                if (debugMode == SOL_WATER_DEBUG_PATCH_SKIRTS)
                {
                    if (!isNearestSurface)
                        discard;
                    return input.data.z > 0.01
                        ? half4(1.0, 0.0, 1.0, 1.0)
                        : half4(0.08, 0.08, 0.08, 1.0);
                }
                if (!isNearestSurface)
                    discard;

                float3 normalWS = normalize(input.normalWS);
                float surfaceFoam = input.data.x;
                if (_SolWaterSpectralParams.x > 0.5)
                    SolEvaluateWaterPixelNormalFoam(input.positionWS.xz, normalWS, surfaceFoam);
                float3 viewDirection = SafeNormalize(GetCameraPositionWS() - input.positionWS);
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
                //
                // This test used to be one-sided, which is what produced the thin dark
                // strokes on the surface. It rejected a refracted sample that landed in
                // front of the water but accepted one that landed arbitrarily far
                // *behind* it — so wherever the bounded screen-space offset carried the
                // sample across a scene depth edge (a sandbar silhouetted against open
                // water, or off the terrain onto sky) the seabed a metre down was
                // swapped for geometry tens or hundreds of metres away.
                //
                // rayLength below turns that straight into blackness rather than a soft
                // error: SolWaterComputeAbsorption raises path length to the power 1.5,
                // so even a 1 m -> 20 m jump drives transmittance onto its
                // (0.0005, 0.001, 0.025) floor in one step. The band is thin because
                // only pixels within the maximum screen offset of an edge can cross it,
                // and it is dark rather than merely blue because the remaining volume
                // scattering is itself dim under a low sun.
                //
                // A legitimate refracted sample describes roughly the same seabed as the
                // unrefracted one, and the ray can only bend within the water column, so
                // the column depth bounds how far apart they may plausibly land.
                float refractionSpread = abs(refractedEyeDepth - sceneDepth);
                float refractionSpreadLimit = max(0.5, thickness * 2.0 + 0.5);
                // Only meaningful where the unrefracted sample hit something: over open
                // water sceneDepth is the far plane and the comparison is noise.
                float refractionFarConfidence = lerp(1.0,
                    1.0 - smoothstep(refractionSpreadLimit,
                        refractionSpreadLimit * 2.0, refractionSpread),
                    hasSceneGeometry);
                float refractionConfidence = smoothstep(0.0, 0.12,
                    refractedEyeDepth - input.data.y) * refractionFarConfidence;
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
                // Nothing behind the surface means nothing to refract. Over open water the
                // scene colour at refractUV is the sky, sun disc included. rayLength below
                // already snaps to SOL_WATER_MAX_RAY_LENGTH for exactly this case, to absorb
                // that sky away -- but SolWaterComputeAbsorption floors transmittance at
                // (0.0005, 0.001, 0.025) so deep water keeps its blue rather than collapsing
                // to black, and its extinction clamps the path to clarityDistance so the
                // blend never fully reaches the scattering term either. The product leaks a
                // few percent of an HDR sun through, which is a blown-out pixel wherever a
                // normal happens to offset the sample onto the disc and nothing at all in the
                // neighbouring pixel. The floor exists to tint an absorbed sea bed; it was
                // never meant to transmit sky. Deep water gets its colour from `scattering`
                // below, which is what that composite already documents as the intent.
                refracted *= refractedHasGeometry;

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                // The scalar term is demoted to the low-frequency ambient share so the
                // spatial map is not multiplied on top of an already-darkened surface.
                float cloudShadow = lerp(1.0, 0.72, _SolWaterWeatherExtended.x)
                    * SolSampleCloudShadow(input.positionWS);
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
                    float2 causticSurfaceXZ = SolWaterCausticSurfaceXZ(
                        refractedWorldPosition.xz, waterColumn, causticLight.direction);
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
                    float2 warpedCausticXZ = SolWaterCausticSampleXZ(
                        logicalCausticXZ, waterColumn, causticNormal);
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
                // WaterFX gains by 5x here so the pattern reads at all against a lit bed.
                //
                // Both sides saturate smoothly rather than clamping; see
                // SolWaterApplyCausticResponse for why the old hard clamps flattened the
                // field into blotches instead of a pattern.
                refracted *= SolWaterApplyCausticResponse(causticLighting);

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
                // The cast-shadow term belongs here as well as the cloud term. This value
                // is not just the fallback when volumetrics are off: the composite below
                // takes max() against it, so an unshadowed analytic term acts as a floor
                // that a shadowed volumetric sample cannot darken past. That floor is what
                // stopped cliff and cloud shadows from reading on the water body at all.
                // Every other lighting term in this shader already multiplies both.
                float3 scattering = SolWaterVolumeScattering(
                    _SolWaterShallowColor.rgb,
                    SolWaterDynamicSky(float3(0.0, 1.0, 0.0)),
                    mainLight.color, mainLight.direction,
                    mainLight.shadowAttenuation * cloudShadow,
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
                // Shadowed: the screen-space atmosphere pass marches the shadow map for
                // every other pixel in the frame, and this analytic call is what fogs the
                // water. Handing it the surface's own shadow term is what lets a light
                // shaft crossing the shoreline carry on over the water instead of ending
                // at the waterline.
                sourceColor = SolApplyAtmosphereShadowed(
                    sourceColor,
                    GetCameraPositionWS(),
                    input.positionWS,
                    -viewDirection,
                    0.0,
                    mainLight.shadowAttenuation);

                if (debugMode == SOL_WATER_DEBUG_RAW_SSR)
                    return half4(rawSsr.rgb, 1.0);
                if (debugMode == SOL_WATER_DEBUG_VALIDATED_SSR)
                    return half4(ssr.rgb, 1.0);
                if (debugMode == SOL_WATER_DEBUG_REFLECTION_CONFIDENCE)
                    return half4(ssr.aaa, 1.0);
                if (debugMode == SOL_WATER_DEBUG_FALLBACK_SKY)
                    return half4(dynamicSky, 1.0);
                if (debugMode == SOL_WATER_DEBUG_FOAM_CONFIDENCE)
                    return half4(foamConfidence.xxx, 1.0);
                if (debugMode == SOL_WATER_DEBUG_REFRACTION)
                    return half4(refracted, 1.0);
                if (debugMode == SOL_WATER_DEBUG_CAUSTICS)
                    return half4(causticLighting, 1.0);
                if (debugMode == SOL_WATER_DEBUG_SCATTERING)
                    return half4(scattering, 1.0);
                if (debugMode == SOL_WATER_DEBUG_TRANSMITTANCE)
                    return half4(transmittance, 1.0);
                if (debugMode == SOL_WATER_DEBUG_OPACITY)
                    return half4(opacity.xxx, 1.0);
                if (debugMode == SOL_WATER_DEBUG_SUN_SPECULAR)
                    return half4(sunSpecular, 1.0);
                if (debugMode == SOL_WATER_DEBUG_VOLUMETRIC_SCATTERING)
                    return half4(SAMPLE_TEXTURE2D_X_LOD(_SolWaterVolumetricTexture,
                        sampler_SolWaterVolumetricTexture, screenUV, 0).rgb, 1.0);
                // Black here means the refracted sample was rejected as a leak and the
                // fragment fell back to the unrefracted seabed. Before the two-sided
                // test above, those same pixels kept a sample from far behind the
                // surface and absorption turned them into the dark strokes.
                if (debugMode == SOL_WATER_DEBUG_REFRACTION_CONFIDENCE)
                    return half4(refractionConfidence.xxx, 1.0);
                // Path length feeding absorption, normalized against the clarity
                // distance. A thin bright filament crossing otherwise dim shallow water
                // is a refraction leak; absorption is superlinear, so anything past a
                // few multiples of clarity is already on the transmittance floor.
                if (debugMode == SOL_WATER_DEBUG_ABSORPTION_PATH_LENGTH)
                    return half4(saturate(rayLength
                        / max(1.0, _SolWaterVisibilityParams.x * 4.0)).xxx, 1.0);
                // Shaded surface normal. A stroke of uniform colour through otherwise
                // varied water means the normal itself is discontinuous, which puts the
                // cause in the vertex or spectral path rather than in the optics.
                if (debugMode == SOL_WATER_DEBUG_SURFACE_NORMAL)
                    return half4(normalWS * 0.5 + 0.5, 1.0);
                // Spectral detail fade. Black strokes here are pixels whose derivative
                // footprint suppressed all cascade detail, leaving a flat, foamless
                // normal. If the reported lines appear in this view they are a
                // derivative artefact, not a lighting or absorption one.
                if (debugMode == SOL_WATER_DEBUG_SPECTRAL_DETAIL_FADE)
                    return half4(
                        SolWaterDebugSpectralDetailFade(input.positionWS.xz).xxx, 1.0);
                // Caustic response headroom. Green is how far the sea bed is being
                // brightened as a fraction of the ceiling, red how far it is darkened
                // against its own limit. Flat saturated green over most of the bed means
                // the gain is too hot and the field has no pattern left in it, which is
                // the failure the response curve exists to avoid; a healthy field is a
                // sparse bright web over mostly dim ground.
                if (debugMode == SOL_WATER_DEBUG_CAUSTIC_RESPONSE)
                {
                    float3 causticResponse =
                        SolWaterApplyCausticResponse(causticLighting);
                    float causticUp = saturate((causticResponse.g - 1.0)
                        / SOL_WATER_CAUSTIC_MAX_BRIGHTENING);
                    float causticDown = saturate((1.0 - causticResponse.g)
                        / SOL_WATER_CAUSTIC_MAX_DARKENING);
                    return half4(causticDown, causticUp, 0.0, 1.0);
                }
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
                // terrain would still occlude post-process effects, and the skirt walls
                // would hand depth-of-field a surface 8-18 cm below the one on screen.
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float4 visibleWater;
                if (!SolWaterFragmentIsNearest(screenUV, input.positionCS.z, visibleWater))
                    discard;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
