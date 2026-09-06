Shader "Hidden/Sol/VolumetricClouds"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        TEXTURE2D(_SolCloudShapeTexture);
        SAMPLER(sampler_SolCloudShapeTexture);
        TEXTURE2D(_SolCloudWeatherTexture);
        SAMPLER(sampler_SolCloudWeatherTexture);
        TEXTURE3D(_SolCloudShapeVolume);
        SAMPLER(sampler_SolCloudShapeVolume);
        TEXTURE3D(_SolCloudDetailVolume);
        SAMPLER(sampler_SolCloudDetailVolume);
        TEXTURE2D_X(_SolCloudHistoryTexture);
        TEXTURE2D_X(_SolCloudRenderTexture);

        float4 _SolCloudPlanet;          // center xyz, radius
        float4 _SolCloudLayer;           // base, thickness, max distance, view steps
        float4 _SolCloudShape;           // coverage, erosion, density, formation
        float4 _SolCloudScales;          // shape, detail, weather metres, extinction/km
        float4 _SolCloudDevelopment;     // vertical, anvil, cirrus, shadow strength
        float4 _SolCloudWeatherOffset;
        float4 _SolCloudShapeOffset;
        float4 _SolCloudDetailOffset;
        float4 _SolCloudPreviousOffsetDelta;
        float4 _SolCloudVariationWeights;
        float4 _SolCloudWind;            // direction xyz, cloud-layer speed m/s
        float4 _SolCloudLightDirection;
        float4 _SolCloudLightColor;
        float4 _SolCloudSecondaryLightDirection;
        float4 _SolCloudSecondaryLightColor;
        float4 _SolCloudAmbientColor;
        float4 _SolCloudAmbientGround;
        float4 _SolCloudOptics;          // forward g, backward g, backward weight, ambient
        float4 _SolCloudLighting;        // powder, legacy light step, light steps, lightning
        float4 _SolCloudFormationWeights; // cumulus, stratus, nimbostratus, cumulonimbus
        float4 _SolCloudSculpting;       // sculpting, base lobe, multi-scatter, inner glow
        float4 _SolCloudStructure;       // scale metres, influence, warp, surface gradient
        float4 _SolCloudDefinition;      // edge softness, base softness, coverage bias, curl
        float _SolCloudDistanceGain;     // extra density on distant banks
        float4 _SolCloudDetailDistance;  // micro fade start/end, max light march, gradient enabled
        float4 _SolCloudStorm;           // storm absorption, rain, secondary fill, ambient floor
        float4 _SolCloudLightning;       // flash direction xyz, emission strength
        float4 _SolCloudLightningColor;
        float4 _SolCloudShadowOrigin;    // footprint centre, lifted clear of the layer
        float4 _SolCloudShadowAxisU;     // world vector spanning the footprint in U
        float4 _SolCloudShadowAxisV;     // world vector spanning the footprint in V
        float4 _SolCloudShadowParams;    // samples, strength, border fraction, unused
        float4 _SolCloudTemporalHorizon; // temporal reject start/end, flash delta
        float _SolCloudVolumeAvailable;
        float _SolCloudDebugMode;
        float4 _SolCloudTemporalParams;  // history weight, clamp expansion, frame index, depth threshold
        float4x4 _SolCloudPreviousViewProjection;

        // Ratio between the last and first ray-march step. Large enough to keep a
        // near-horizon ray from stepping in kilometres, small enough that the far field
        // still reaches the end of the shell within the step budget.
        #define SOL_CLOUD_STEP_GROWTH 10.0
        #define SOL_CLOUD_GOLDEN_RATIO 0.6180339887
        // The sun march only has to cross the optically relevant part of the deck. Bounding
        // it by layer thickness rather than by the shell exit keeps the stride short enough
        // that a handful of samples still describes a gradient: a grazing-sun shell exit is
        // tens of kilometres, which turned four samples into noise at full cost.
        #define SOL_CLOUD_LIGHT_MARCH_THICKNESS 3.5
        // Below this view transmittance a re-marched sun ray is indistinguishable from the
        // previous step's, and the light march is the most expensive thing in the loop.
        #define SOL_CLOUD_LIGHT_MARCH_CUTOFF 0.15
        // Erosion is invisible behind an almost opaque body, so the detail volume fetch is
        // dropped once the view ray is mostly absorbed.
        #define SOL_CLOUD_DETAIL_CUTOFF 0.5

        float SolCloudRawDepthIsSky(float rawDepth)
        {
            #if UNITY_REVERSED_Z
                return rawDepth <= 0.00001;
            #else
                return rawDepth >= 0.99999;
            #endif
        }

        void SolCloudReconstructRay(float2 uv, out float3 direction, out float sceneDistance)
        {
            float rawDepth = SampleSceneDepth(uv);
            float isSky = SolCloudRawDepthIsSky(rawDepth);
            #if UNITY_REVERSED_Z
                float safeDepth = isSky > 0.5 ? 0.00001 : rawDepth;
            #else
                float safeDepth = isSky > 0.5 ? 0.99999 : rawDepth;
            #endif
            float3 positionWS = ComputeWorldSpacePosition(uv, safeDepth, UNITY_MATRIX_I_VP);
            float3 cameraToPoint = positionWS - _WorldSpaceCameraPos;
            float distanceToPoint = length(cameraToPoint);
            direction = distanceToPoint > 0.0001
                ? cameraToPoint / distanceToPoint
                : float3(0.0, 1.0, 0.0);
            sceneDistance = isSky > 0.5 ? _SolCloudLayer.z : distanceToPoint;
        }

        bool SolCloudRaySphere(float3 origin, float3 direction, float radius,
            out float nearDistance, out float farDistance)
        {
            float b = dot(origin, direction);
            float c = dot(origin, origin) - radius * radius;
            float discriminant = b * b - c;
            if (discriminant < 0.0)
            {
                nearDistance = 0.0;
                farDistance = 0.0;
                return false;
            }
            float root = sqrt(discriminant);
            nearDistance = -b - root;
            farDistance = -b + root;
            return farDistance > 0.0;
        }

        bool SolCloudShellSegment(float3 cameraPosition, float3 direction,
            out float startDistance, out float endDistance)
        {
            float3 origin = cameraPosition - _SolCloudPlanet.xyz;
            float innerRadius = _SolCloudPlanet.w + _SolCloudLayer.x;
            float outerRadius = innerRadius + _SolCloudLayer.y;
            float outerNear, outerFar;
            if (!SolCloudRaySphere(origin, direction, outerRadius, outerNear, outerFar))
            {
                startDistance = endDistance = 0.0;
                return false;
            }

            float cameraRadius = length(origin);
            if (cameraRadius < innerRadius)
            {
                float innerNear, innerFar;
                if (!SolCloudRaySphere(origin, direction, innerRadius, innerNear, innerFar))
                {
                    startDistance = endDistance = 0.0;
                    return false;
                }
                startDistance = max(0.0, innerFar);
                endDistance = outerFar;
            }
            else if (cameraRadius < outerRadius)
            {
                startDistance = 0.0;
                endDistance = outerFar;
            }
            else
            {
                startDistance = max(0.0, outerNear);
                endDistance = outerFar;
            }

            endDistance = min(endDistance, _SolCloudLayer.z);
            return endDistance > startDistance;
        }

        float SolCloudHeightFraction(float3 positionWS)
        {
            float height = length(positionWS - _SolCloudPlanet.xyz) - _SolCloudPlanet.w;
            return saturate((height - _SolCloudLayer.x) / max(10.0, _SolCloudLayer.y));
        }

        float SolCloudVerticalProfile(float heightFraction)
        {
            float vertical = _SolCloudDevelopment.x;
            // Base softness scales the underside ramp only. A crisp flat cumulus base and a
            // smeared nimbostratus base are the same profile at two ends of this width.
            float baseSpread = lerp(0.5, 2.4, saturate(_SolCloudDefinition.y));
            float bottomWidth = lerp(0.05, 0.14, 1.0 - vertical) * baseSpread;
            float bottom = smoothstep(0.0, bottomWidth, heightFraction);
            float topStart = lerp(0.42, 0.78, vertical);
            float towering = bottom * (1.0 - smoothstep(topStart, 1.0, heightFraction));
            float deck = smoothstep(0.0, 0.08 * baseSpread, heightFraction)
                * (1.0 - smoothstep(0.52, 0.82, heightFraction));
            // The formation used to arrive as an integer and be branched on. Weather blends
            // between formations continuously, so an integer resolved at the midpoint of a
            // transition snapped the whole sky in one frame. These weights carry the same
            // per-formation ratios while staying continuous through the handover.
            float deckWeight = saturate(dot(_SolCloudFormationWeights,
                float4(0.0, 0.85, 0.62, 0.0)));
            return lerp(towering, deck, deckWeight);
        }

        /// Linear rescale, clamped. This is the operation Nubis applies wherever a
        /// threshold is wanted, and the reason matters: smoothstep flattens to exactly one
        /// above its upper edge, so every sample in a dense deck returns the same density
        /// and every lighting term downstream resolves it identically. A remap has no such
        /// shoulder -- dense cores keep climbing -- and using the eroding value as the new
        /// minimum carves the silhouette without eating the interior.
        float SolCloudRemap(float value, float originalMin, float originalMax,
            float newMin, float newMax)
        {
            return newMin + saturate((value - originalMin)
                / max(1e-5, originalMax - originalMin)) * (newMax - newMin);
        }

        float2 SolCloudRotateDomain(float2 value)
        {
            // A fixed rotation keeps the volume periodic and cheap, but prevents its tile
            // axes from lining up with the horizon and the world grid.
            return float2(
                value.x * 0.819152 - value.y * 0.573576,
                value.x * 0.573576 + value.y * 0.819152);
        }


        float SolCloudDensity(float3 positionWS, bool includeMicro, float sampleLod,
            out float4 breakdown)
        {
            breakdown = 0.0;
            float heightFraction = SolCloudHeightFraction(positionWS);
            if (heightFraction <= 0.0 || heightFraction >= 1.0)
                return 0.0;

            float distanceToCamera = distance(positionWS, _WorldSpaceCameraPos);
            float microFade = 1.0 - smoothstep(_SolCloudDetailDistance.x,
                _SolCloudDetailDistance.y, distanceToCamera);
            float2 weatherUV = (positionWS.xz - _SolCloudWeatherOffset.xy)
                / max(1000.0, _SolCloudScales.z);
            float2 weather = SAMPLE_TEXTURE2D_LOD(_SolCloudWeatherTexture,
                sampler_SolCloudWeatherTexture, weatherUV, min(sampleLod, 2.0)).rg;

            float shearDistance = min(_SolCloudLayer.y * 0.45, _SolCloudWind.w * 90.0)
                * (heightFraction - 0.35);
            float2 windShear = _SolCloudWind.xz * shearDistance;
            float2 shapePosition = positionWS.xz - _SolCloudShapeOffset.xy - windShear;
            float2 weatherWarp = (weather - 0.5) * 0.38;

            // One artist-replaceable lookup supplies medium-scale form and domain warp.
            // The actual density remains three-dimensional, so this art direction never
            // becomes a flat sky layer.
            float2 structureUV = SolCloudRotateDomain(shapePosition)
                / max(100.0, _SolCloudStructure.x) + weatherWarp * 0.45;
            float4 packedStructure = SAMPLE_TEXTURE2D_LOD(_SolCloudShapeTexture,
                sampler_SolCloudShapeTexture, structureUV, min(sampleLod, 2.0));
            float2 authoredWarp = (packedStructure.gb * 2.0 - 1.0)
                * _SolCloudStructure.z;
            float2 shapeUV = SolCloudRotateDomain(shapePosition)
                / max(100.0, _SolCloudScales.x) + weatherWarp + authoredWarp;

            float coverage = saturate(_SolCloudShape.x + _SolCloudDefinition.z
                + (weather.r - 0.5) * 0.26);
            float anvil = _SolCloudDevelopment.y
                * smoothstep(0.55, 0.86, heightFraction)
                * (1.0 - smoothstep(0.9, 1.0, heightFraction));
            float threshold = saturate(1.0 - coverage) - anvil * 0.16;
            bool useVolume = _SolCloudVolumeAvailable > 0.5 && _SolCloudLayer.w > 8.5;
            float baseShape = packedStructure.r;
            float billow = 0.0;
            if (useVolume)
            {
                float slab = dot(_SolCloudVariationWeights, float4(0.0, 0.27, 0.54, 0.81));
                float volumeHeight = slab
                    + heightFraction * _SolCloudLayer.y / max(100.0, _SolCloudScales.x);
                float3 volumeUVW = float3(shapeUV.x, volumeHeight, shapeUV.y);
                float4 shapeVariants = SAMPLE_TEXTURE3D_LOD(_SolCloudShapeVolume,
                    sampler_SolCloudShapeVolume, volumeUVW,
                    min(sampleLod, 2.0));
                baseShape = dot(shapeVariants, _SolCloudVariationWeights);
                float meanShape = dot(shapeVariants, float4(0.25, 0.25, 0.25, 0.25));
                float4 deviation = shapeVariants - meanShape;
                billow = sqrt(dot(deviation * deviation,
                    float4(0.25, 0.25, 0.25, 0.25))) * 2.0;
            }

            // The boundary width used to come from the formation alone, which left no
            // way to author how defined a cloud mass reads. The formation still sets the
            // baseline; edge softness scales it.
            float formationWidth = dot(_SolCloudFormationWeights,
                float4(0.13, 0.22, 0.18, 0.12));
            // The hard end of this range decides how defined a cloud can ever look. At
            // 0.45 the softest and hardest settings were barely a third apart and nothing
            // could reach a crisp cauliflower edge; the low end now genuinely bites.
            float boundaryWidth = max(0.025,
                formationWidth * lerp(0.18, 1.7, saturate(_SolCloudDefinition.x)));
            // The vertical profile shapes the field the threshold then cuts, rather than
            // fading a finished slab in and out. Cutting a profiled shape puts the boundary
            // at a different place at every altitude, which is what produces a flat base and
            // a cauliflower crown; applying it afterwards can only ever soften both ends of
            // a sheet, and that soft-topped sheet was most of the flat grey look.
            baseShape *= SolCloudVerticalProfile(heightFraction);
            breakdown.x = SolCloudRemap(baseShape, threshold,
                threshold + boundaryWidth, 0.0, 1.0);
            float crown = smoothstep(0.3, 0.95, heightFraction);
            float underside = 1.0 - smoothstep(0.0, 0.32, heightFraction);
            float structureHeight = dot(_SolCloudFormationWeights, float4(
                lerp(0.68, 1.0, max(crown, underside * 0.55)),
                lerp(1.0, 0.68, crown),
                lerp(1.18, 0.7, crown),
                lerp(0.78, 1.25, max(crown, underside * 0.8))));
            float boundaryMask = saturate(1.0
                - abs(baseShape - threshold) / max(0.001, boundaryWidth * 1.8));
            float authoredStructure = packedStructure.r * 2.0 - 1.0;
            baseShape += authoredStructure * _SolCloudStructure.y * 0.22
                * boundaryMask * structureHeight;
            baseShape += billow * boundaryMask * _SolCloudSculpting.y;
            breakdown.y = authoredStructure * 0.5 + 0.5;

            float density = SolCloudRemap(baseShape, threshold,
                threshold + boundaryWidth, 0.0, 1.0);
            // Coverage moved the threshold but nothing else, so a partly cloudy sky was
            // fewer clouds at full density instead of thinner cloud. Weighting by coverage
            // restores the thin end. The reference form is a bare multiply by coverage;
            // the 1.6 gain makes this neutral from 0.625 coverage upward so existing
            // overcast and storm profiles keep their authored density. Drop the 1.6 for
            // the textbook behaviour and retune _SolCloudShape.z to match.
            density *= saturate(coverage * 1.6);
            float microContribution = 0.0;
            if (includeMicro && microFade > 0.001 && density > 0.001)
            {
                float coreDepth = saturate((baseShape - threshold) / boundaryWidth);
                float erosionMask = lerp(1.0, 0.32, coreDepth);
                float carve;
                if (useVolume)
                {
                    float2 detailPosition = positionWS.xz - _SolCloudDetailOffset.xy
                        - windShear * 1.35;
                    // A perpendicular rotation of the authored warp is divergence-free,
                    // so it swirls the erosion without pumping density in or out. It is
                    // applied to the detail lookup only: curling the base shape instead
                    // makes whole masses swim under temporal reprojection.
                    float2 curlWarp = float2(-authoredWarp.y, authoredWarp.x)
                        * _SolCloudDefinition.w * (1.0 - heightFraction);
                    float2 detailHorizontal = SolCloudRotateDomain(detailPosition)
                        / max(10.0, _SolCloudScales.y) + weatherWarp * 0.23
                        + authoredWarp * 0.37 + curlWarp * 0.5;
                    float3 detailUVW = float3(detailHorizontal.x,
                        heightFraction * _SolCloudLayer.y / max(10.0, _SolCloudScales.y),
                        detailHorizontal.y);
                    float3 detailNoise = SAMPLE_TEXTURE3D_LOD(_SolCloudDetailVolume,
                        sampler_SolCloudDetailVolume, detailUVW,
                        min(max(0.0, sampleLod - 0.5), 2.0)).rgb;
                    float lobeWeight = dot(_SolCloudFormationWeights,
                        float4(1.0, 0.3, 0.5, 0.95));
                    float shelfWeight = dot(_SolCloudFormationWeights,
                        float4(0.45, 1.0, 0.9, 0.7));
                    float lobes = (1.0 - detailNoise.r) * lobeWeight
                        * lerp(0.5, 1.0, max(crown, underside * 0.75));
                    float shelves = (1.0 - detailNoise.g) * shelfWeight;
                    float edges = (1.0 - detailNoise.b) * lerp(0.55, 1.35, crown);
                    carve = (lobes * 0.5 + shelves * 0.28 + edges * 0.22)
                        * lerp(1.0, 1.6, _SolCloudSculpting.x);
                }
                else
                    carve = 1.0 - packedStructure.g;

                microContribution = carve * erosionMask * _SolCloudShape.y
                    * 0.5 * microFade;
                // Subtracting from a density that was already crushed to a 0-1 plateau took
                // a near uniform amount off the whole deck, which greys it. Feeding the
                // eroding value in as the remap's new minimum only bites where the base is
                // close to the threshold, so it carves the edge and leaves the core.
                density = SolCloudRemap(density, microContribution, 1.0, 0.0, 1.0);
            }
            breakdown.z = saturate(microContribution * 2.0);

            float cirrusThreshold = lerp(0.84, 0.5, coverage);
            float cirrusShape = saturate((packedStructure.a - cirrusThreshold) * 3.2);
            float cirrusHeight = smoothstep(0.76, 0.9, heightFraction)
                * (1.0 - smoothstep(0.96, 1.0, heightFraction));
            float cirrus = cirrusShape * _SolCloudDevelopment.z * cirrusHeight;
            // The vertical profile is already in baseShape above. Cirrus is authored
            // against its own height window and never went through that cut.
            density = max(density, cirrus * 0.28);
            density *= _SolCloudShape.z;
            // Distant banks otherwise wash out into the atmosphere: the same density seen
            // through more air reads as less cloud. The ramp is deliberately long and the
            // gain small -- most of what looked like distant wash-out was the ray-march
            // under-sampling its own steps, and double-compensating for that here would
            // put a density seam where the two effects crossed over.
            density *= 1.0 + _SolCloudDistanceGain
                * saturate((distanceToCamera - 12000.0) * 0.00002);
            // No saturate here. Authored density legitimately exceeds one -- cumulonimbus
            // ships at 1.2 -- and clamping it flattens exactly the dense cores that give a
            // cloud its definition.
            breakdown.w = density;
            return density;
        }

        float SolCloudDensity(float3 positionWS, bool includeMicro, float sampleLod)
        {
            float4 ignored;
            return SolCloudDensity(positionWS, includeMicro, sampleLod, ignored);
        }

        float SolCloudPhase(float cosine, float g)
        {
            float gg = g * g;
            return (1.0 - gg) / max(0.001,
                4.0 * PI * pow(1.0 + gg - 2.0 * g * cosine, 1.5));
        }

        /// Distance from a point already inside the shell to wherever the ray leaves it:
        /// the top of the layer for an upward ray, the cloud base for a downward one.
        float SolCloudShellExitDistance(float3 positionWS, float3 direction)
        {
            float3 origin = positionWS - _SolCloudPlanet.xyz;
            float innerRadius = _SolCloudPlanet.w + _SolCloudLayer.x;
            float outerRadius = innerRadius + _SolCloudLayer.y;
            float outerNear, outerFar;
            if (!SolCloudRaySphere(origin, direction, outerRadius, outerNear, outerFar))
                return 0.0;
            float exitDistance = outerFar;
            float innerNear, innerFar;
            if (SolCloudRaySphere(origin, direction, innerRadius, innerNear, innerFar)
                && innerNear > 0.0)
                exitDistance = min(exitDistance, innerNear);
            return max(0.0, exitDistance);
        }

        float SolCloudLightTransmittance(float3 positionWS, float sampleLod,
            out float opticalDepth)
        {
            opticalDepth = 0.0;
            int lightSteps = (int)_SolCloudLighting.z;
            if (lightSteps <= 0)
                return 1.0;

            // Cover the complete relevant shell path instead of silently truncating it to
            // step-count times a fixed stride. A thirty-kilometre cap bounds grazing-sun
            // variance while still integrating the optically relevant part of the deck.
            float marchLength = min(min(SolCloudShellExitDistance(
                positionWS, _SolCloudLightDirection.xyz), _SolCloudDetailDistance.z),
                _SolCloudLayer.y * SOL_CLOUD_LIGHT_MARCH_THICKNESS);
            if (marchLength <= 0.0)
                return 1.0;
            float stepLength = marchLength / lightSteps;

            float positionHash = frac(sin(dot(positionWS.xz,
                float2(0.000173, 0.000319))) * 43758.5453);
            float temporalRotation = frac(_SolCloudTemporalParams.z * 0.61803398875);
            float stratumJitter = frac(positionHash + temporalRotation) - 0.5;
            [loop]
            for (int i = 0; i < 10; i++)
            {
                if (i >= lightSteps) break;
                float3 samplePosition = positionWS
                    + _SolCloudLightDirection.xyz
                    * (stepLength * (i + 0.5 + stratumJitter * 0.7));
                // Keep macro and authored mesostructure in illumination. Only the finest
                // erosion volume is skipped, aligning shadows with visible lobes.
                opticalDepth += SolCloudDensity(samplePosition, false, sampleLod);
            }
            opticalDepth *= _SolCloudScales.w * (stepLength * 0.001);
            return exp(-opticalDepth);
        }

        float3 SolCloudMacroGradient(float3 positionWS, float centerDensity, float sampleLod)
        {
            // Three broad forward samples are paid at most once per ray. They describe the
            // macro/authored surface rather than chasing fine erosion at every regular step.
            float offsetMetres = clamp(_SolCloudStructure.x / 48.0, 90.0, 240.0);
            float dx = SolCloudDensity(positionWS + float3(offsetMetres, 0.0, 0.0),
                false, sampleLod) - centerDensity;
            float dy = SolCloudDensity(positionWS + float3(0.0, offsetMetres, 0.0),
                false, sampleLod) - centerDensity;
            float dz = SolCloudDensity(positionWS + float3(0.0, 0.0, offsetMetres),
                false, sampleLod) - centerDensity;
            return float3(dx, dy, dz) / offsetMetres;
        }

        half4 SolCloudRaymarch(float2 uv)
        {
            float3 viewDirection;
            float sceneDistance;
            SolCloudReconstructRay(uv, viewDirection, sceneDistance);
            float startDistance, endDistance;
            if (!SolCloudShellSegment(_WorldSpaceCameraPos, viewDirection,
                startDistance, endDistance))
                return half4(0.0, 0.0, 0.0, 1.0);
            endDistance = min(endDistance, sceneDistance);
            if (endDistance <= startDistance)
                return half4(0.0, 0.0, 0.0, 1.0);

            int stepCount = clamp((int)_SolCloudLayer.w, 2, 64);
            float segmentLength = endDistance - startDistance;
            // Dividing the whole shell segment uniformly is what sliced the deck into
            // horizontal shells. A ray near the horizon crosses tens of kilometres of
            // shell, so an even split gave it kilometre-long steps and every sample
            // landed on a visible band. Grow the step along the ray instead: the near
            // field, where a step subtends the most screen area, stays finely sampled and
            // the far field -- where a band is a few pixels tall -- absorbs the coarseness.
            float stepGrowth = (SOL_CLOUD_STEP_GROWTH - 1.0)
                / max(1.0, (float)stepCount - 1.0);
            float baseStepLength = segmentLength
                / (stepCount * (1.0 + (SOL_CLOUD_STEP_GROWTH - 1.0) * 0.5));
            // Match texture bandwidth to the ray-march footprint. Mip zero on kilometre-
            // long tangent steps aliases the baked voxels into a distant checker/grid.
            // The footprint now changes per step, so the mip has to follow it.
            float shapeVoxelMetres = max(1.0, _SolCloudScales.x / 96.0);
            float jitter = InterleavedGradientNoise(uv * _ScaledScreenParams.xy,
                (uint)_SolCloudTemporalParams.z);
            float distanceAlongRay = startDistance;
            float transmittance = 1.0;
            float3 radiance = 0.0;
            float lightOpticalDepthAccumulation = 0.0;
            // Carried so a step that is too deeply buried to justify its own sun march can
            // reuse the last one that was worth paying for.
            float previousLightTransmittance = 1.0;
            float previousLightOpticalDepth = 0.0;
            float3 debugStructure = 0.0;
            float debugWeight = 0.0;
            float surfaceRelief = 1.0;
            bool gradientEvaluated = false;
            float phaseCosine = dot(viewDirection, _SolCloudLightDirection.xyz);
            float phase = lerp(SolCloudPhase(phaseCosine, _SolCloudOptics.x),
                SolCloudPhase(phaseCosine, _SolCloudOptics.y), _SolCloudOptics.z);

            // Storm response is driven by the weather simulation's precipitation, not by any
            // cloud-state field: SolCloudState carries no precipitation term.
            float storm = saturate(_SolCloudStorm.x * _SolCloudStorm.y);
            // The secondary body reuses the primary's transmittance rather than paying for a
            // second light march. It is a low-strength handover fill, not a key light, so the
            // shared occlusion is not visible.
            float secondaryCosine = dot(viewDirection, _SolCloudSecondaryLightDirection.xyz);
            float3 secondaryFill = _SolCloudSecondaryLightColor.rgb
                * SolCloudPhase(secondaryCosine, _SolCloudOptics.x) * _SolCloudStorm.z;
            float sunFacing = saturate(phaseCosine * 0.5 + 0.5);
            // Constant along the ray, so it is resolved once rather than per step.
            float flashFocus = saturate(dot(viewDirection,
                _SolCloudLightning.xyz) * 0.5 + 0.5);
            float3 flashEmission = _SolCloudLightningColor.rgb
                * (_SolCloudLighting.w * _SolCloudLightning.w
                    * flashFocus * flashFocus);

            [loop]
            for (int stepIndex = 0; stepIndex < 64; stepIndex++)
            {
                // 0.012 kept marching to just over one percent visibility, which the
                // atmosphere blend then buries anyway.
                if (stepIndex >= stepCount || transmittance < 0.05)
                    break;
                float stepLength = baseStepLength * (1.0 + stepIndex * stepGrowth);
                float sampleLod = clamp(log2(max(1.0,
                    stepLength / shapeVoxelMetres)), 0.0, 2.0);
                // Offsetting the ray once leaves every step of every pixel landing on the
                // same set of shells, so the banding survives as a coherent pattern that
                // temporal reprojection then happily preserves. Stratifying each step
                // within its own segment turns the residue into noise the history resolves.
                float stepJitter = frac(jitter + stepIndex * SOL_CLOUD_GOLDEN_RATIO);
                float3 positionWS = _WorldSpaceCameraPos
                    + viewDirection * (distanceAlongRay + stepLength * stepJitter);
                float4 densityBreakdown;
                float density = SolCloudDensity(positionWS,
                    stepCount > 4 && transmittance > SOL_CLOUD_DETAIL_CUTOFF,
                    sampleLod, densityBreakdown);
                if (density > 0.001)
                {
                    float segmentOpticalDepth = density * _SolCloudScales.w
                        * (stepLength * 0.001) * (1.0 + storm * 1.5);
                    float segmentTransmittance = exp(-segmentOpticalDepth);
                    float segmentOpacity = 1.0 - segmentTransmittance;
                    // The sun march dominates the loop: every dense step ran four to six
                    // density evaluations of its own, each costing three or four texture
                    // fetches. Once the view ray is this far absorbed the marched result is
                    // indistinguishable from the previous step's, so it is carried forward.
                    float lightOpticalDepth;
                    float lightTransmittance;
                    if (transmittance > SOL_CLOUD_LIGHT_MARCH_CUTOFF)
                    {
                        lightTransmittance = SolCloudLightTransmittance(positionWS,
                            sampleLod, lightOpticalDepth);
                        previousLightTransmittance = lightTransmittance;
                        previousLightOpticalDepth = lightOpticalDepth;
                    }
                    else
                    {
                        lightTransmittance = previousLightTransmittance;
                        lightOpticalDepth = previousLightOpticalDepth;
                    }
                    float powder = 1.0 - exp(-density * _SolCloudLighting.x * 2.0);

                    if (!gradientEvaluated && density > 0.035
                        && _SolCloudDetailDistance.w > 0.5)
                    {
                        float3 macroGradient = SolCloudMacroGradient(positionWS,
                            density, sampleLod);
                        float gradientLength = length(macroGradient);
                        if (gradientLength > 0.000001)
                        {
                            float3 outwardNormal = -macroGradient / gradientLength;
                            float lightFacing = saturate(dot(outwardNormal,
                                _SolCloudLightDirection.xyz) * 0.5 + 0.5);
                            float sculptedFormation = saturate(dot(
                                _SolCloudFormationWeights, float4(1.0, 0.08, 0.2, 1.0)));
                            surfaceRelief = lerp(1.0,
                                lerp(0.86, 1.18, lightFacing),
                                _SolCloudStructure.w * sculptedFormation);
                        }
                        gradientEvaluated = true;
                    }

                    // Two scattering octaves. The second runs at half the extinction and a
                    // flatter phase, standing in for light that has already bounced inside
                    // the body. Single scattering alone integrates dense cloud to near-black
                    // instead of leaving it a lit interior.
                    float scatter = phase * lightTransmittance;
                    float secondPhase = lerp(phase, 0.25 / PI, 0.5);
                    scatter += _SolCloudSculpting.z * secondPhase * sqrt(lightTransmittance);

                    // Ambient is not one colour through a cloud: sky above, near-darkness in
                    // the body, and a restrained ground bounce underneath. The single flat
                    // term this replaces is what made midday decks read as grey-white sheets.
                    float heightFraction = SolCloudHeightFraction(positionWS);
                    float3 ambient = lerp(
                        _SolCloudAmbientGround.rgb * _SolCloudStorm.w,
                        _SolCloudAmbientColor.rgb,
                        smoothstep(0.0, 0.85, heightFraction));
                    // Ambient arrives from the whole sky, dominantly from above, so what
                    // occludes it is the mass overhead -- not the distance this particular
                    // view ray happened to travel. Including viewOpticalDepth made a grazing
                    // horizon ray as dark as a deep core, which is a screen-space gradient
                    // wearing a volumetric one's clothes. This estimates the column above the
                    // sample from the local density, in the same units as the view march:
                    // metres of layer remaining, to kilometres, times extinction.
                    float columnAbove = (1.0 - heightFraction) * _SolCloudLayer.y * 0.001
                        * density * _SolCloudScales.w;
                    float ambientOcclusion = 1.0 - exp(-columnAbove * 0.72);
                    ambient *= _SolCloudOptics.w * lerp(1.0, 0.22,
                        saturate(ambientOcclusion * _SolCloudDevelopment.w));

                    // Broad diffused illumination bleeding out of sun-facing transition
                    // regions. It peaks where the light is half absorbed and falls to zero at
                    // both ends, so it reads as the reference's soft under-deck glow rather
                    // than a sharp universal silver lining.
                    float transitionBand = saturate(
                        lightTransmittance * (1.0 - lightTransmittance) * 4.0);
                    float innerGlow = transitionBand * sunFacing * _SolCloudSculpting.w
                        * saturate(1.0 - lightOpticalDepth * 0.08);

                    float stormLightAbsorption = exp(-lightOpticalDepth
                        * storm * _SolCloudStorm.x * 0.16);

                    float3 lighting = _SolCloudLightColor.rgb
                        * (scatter * (1.0 + powder) * surfaceRelief + innerGlow)
                        * stormLightAbsorption
                        + secondaryFill * lightTransmittance
                        + ambient;

                    // Storm decks must go dark and grey without losing their lobes, so the
                    // residue is desaturated rather than only dimmed.
                    lighting = lerp(lighting,
                        dot(lighting, float3(0.2126, 0.7152, 0.0722)).xxx, storm * 0.6);

                    // A flash lights the deck from inside, so it is brightest exactly where
                    // the sun is most occluded: the deep shadowed core of the cell. Weighting
                    // it by sun occlusion rather than by screen opacity is what lets a flash
                    // resolve structure. Added after the storm desaturation so it keeps its
                    // own cool colour rather than being greyed with the rest of the deck.
                    lighting += flashEmission
                        * (1.0 - exp(-lightOpticalDepth * 0.5));

                    radiance += transmittance * segmentOpacity * lighting;
                    debugStructure += transmittance * segmentOpacity
                        * densityBreakdown.xyz;
                    debugWeight += transmittance * segmentOpacity;
                    lightOpticalDepthAccumulation += transmittance * segmentOpacity
                        * lightOpticalDepth;
                    transmittance *= segmentTransmittance;
                }
                distanceAlongRay += stepLength;
            }
            if (_SolCloudDebugMode > 0.5)
            {
                float invDebugWeight = rcp(max(0.0001, debugWeight));
                float3 debugValue = debugStructure * invDebugWeight;
                if (_SolCloudDebugMode < 1.5)
                    return half4(debugValue.xxx, 0.0);
                if (_SolCloudDebugMode < 2.5)
                    return half4(float3(debugValue.y, debugValue.y * 0.72,
                        1.0 - debugValue.y), 0.0);
                if (_SolCloudDebugMode < 3.5)
                    return half4(float3(debugValue.z, debugValue.z * 0.55, 0.0), 0.0);
                float opticalDebug = 1.0 - exp(-lightOpticalDepthAccumulation
                    * invDebugWeight * 0.45);
                return half4(float3(opticalDebug, opticalDebug * 0.48,
                    opticalDebug * 0.18), 0.0);
            }
            return half4(radiance, transmittance);
        }
        ENDHLSL

        Pass
        {
            Name "Sol Cloud Raymarch"
            ZWrite Off ZTest Always Cull Off Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(Varyings input) : SV_Target { return SolCloudRaymarch(input.texcoord); }
            ENDHLSL
        }

        Pass
        {
            Name "Sol Cloud Temporal Reprojection"
            ZWrite Off ZTest Always Cull Off Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragTemporal
            half4 FragTemporal(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float4 current = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                float historyWeight = _SolCloudTemporalParams.x;
                if (historyWeight <= 0.0001)
                    return current;

                float3 viewDirection;
                float sceneDistance;
                SolCloudReconstructRay(uv, viewDirection, sceneDistance);
                float startDistance, endDistance;
                if (!SolCloudShellSegment(_WorldSpaceCameraPos, viewDirection,
                    startDistance, endDistance))
                    return current;
                // Reprojection is ill-conditioned around the shell tangent: a tiny
                // vertical camera motion can move the representative point kilometres.
                // Prefer the current raymarch there instead of stretching history into
                // screen-space columns.
                historyWeight *= 1.0 - smoothstep(
                    _SolCloudTemporalHorizon.x, _SolCloudTemporalHorizon.y, startDistance);
                if (historyWeight <= 0.0001)
                    return current;
                float representativeDistance = min(sceneDistance, lerp(startDistance, endDistance, 0.42));
                float3 previousPosition = _WorldSpaceCameraPos
                    + viewDirection * representativeDistance
                    - float3(_SolCloudPreviousOffsetDelta.x, 0.0, _SolCloudPreviousOffsetDelta.y);
                float4 previousClip = mul(_SolCloudPreviousViewProjection,
                    float4(previousPosition, 1.0));
                float2 previousUV = previousClip.xy / max(0.0001, previousClip.w) * 0.5 + 0.5;
                #if UNITY_UV_STARTS_AT_TOP
                    previousUV.y = 1.0 - previousUV.y;
                #endif
                float2 edge = min(previousUV, 1.0 - previousUV);
                if (previousClip.w <= 0.0 || min(edge.x, edge.y) < 0.0)
                    return current;

                float2 texel = _BlitTexture_TexelSize.xy;
                float4 minimumValue = current;
                float4 maximumValue = current;
                float4 tap0 = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp,
                    uv + float2(texel.x, 0.0));
                float4 tap1 = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp,
                    uv - float2(texel.x, 0.0));
                float4 tap2 = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp,
                    uv + float2(0.0, texel.y));
                float4 tap3 = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp,
                    uv - float2(0.0, texel.y));
                minimumValue = min(minimumValue, min(min(tap0, tap1), min(tap2, tap3)));
                maximumValue = max(maximumValue, max(max(tap0, tap1), max(tap2, tap3)));
                float alphaNeighborhood = maximumValue.a - minimumValue.a;
                float densityEdge = saturate(alphaNeighborhood * 4.0);
                float motionResponse = saturate(_SolCloudWind.w / 30.0);
                // A flash changes the deck's internal lighting by far more than the
                // neighbourhood clamp below can absorb, so reprojecting through one smears it
                // across the following frames. Reject on the frame-to-frame change rather than
                // on the flash level, so a sustained glow still resolves temporally and only
                // the rise and fall go un-filtered.
                float flashTransition = saturate(_SolCloudTemporalHorizon.z * 4.0);
                historyWeight *= lerp(1.0, 0.32, densityEdge)
                    * lerp(1.0, 0.72, motionResponse)
                    * lerp(1.0, 0.25, flashTransition);
                float4 expansion = (maximumValue - minimumValue) * _SolCloudTemporalParams.y;
                float4 history = SAMPLE_TEXTURE2D_X(
                    _SolCloudHistoryTexture, sampler_LinearClamp, previousUV);
                history = clamp(history, minimumValue - expansion, maximumValue + expansion);
                return lerp(current, history, historyWeight);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Sol Cloud Bilateral Composite"
            ZWrite Off ZTest Always Cull Off
            Blend One SrcAlpha
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragComposite

            float SolCloudLinearDepth(float2 uv)
            {
                return LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
            }

            void SolCloudCompositeTap(float2 uv, float weight, float centerDepth,
                inout float4 value, inout float totalWeight,
                inout float minimumTransmittance, inout float maximumTransmittance)
            {
                float depthWeight = exp(-abs(SolCloudLinearDepth(uv) - centerDepth)
                    / max(0.01, _SolCloudTemporalParams.w));
                float finalWeight = weight * depthWeight;
                float4 tap = SAMPLE_TEXTURE2D_X(
                    _SolCloudRenderTexture, sampler_PointClamp, uv);
                value += tap * finalWeight;
                totalWeight += finalWeight;
                minimumTransmittance = min(minimumTransmittance, tap.a);
                maximumTransmittance = max(maximumTransmittance, tap.a);
            }

            half4 FragComposite(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float2 lowSize = max(ceil(_ScaledScreenParams.xy * 0.5), 1.0);
                float2 lowPixel = uv * lowSize - 0.5;
                float2 fraction = frac(lowPixel);
                float2 basePixel = floor(lowPixel) + 0.5;
                float2 texel = rcp(lowSize);
                float centerDepth = SolCloudLinearDepth(uv);
                float4 cloud = 0.0;
                float totalWeight = 0.0;
                float minimumTransmittance = 1.0;
                float maximumTransmittance = 0.0;
                SolCloudCompositeTap((basePixel + float2(0, 0)) * texel,
                    (1.0 - fraction.x) * (1.0 - fraction.y), centerDepth, cloud, totalWeight,
                    minimumTransmittance, maximumTransmittance);
                SolCloudCompositeTap((basePixel + float2(1, 0)) * texel,
                    fraction.x * (1.0 - fraction.y), centerDepth, cloud, totalWeight,
                    minimumTransmittance, maximumTransmittance);
                SolCloudCompositeTap((basePixel + float2(0, 1)) * texel,
                    (1.0 - fraction.x) * fraction.y, centerDepth, cloud, totalWeight,
                    minimumTransmittance, maximumTransmittance);
                SolCloudCompositeTap((basePixel + float2(1, 1)) * texel,
                    fraction.x * fraction.y, centerDepth, cloud, totalWeight,
                    minimumTransmittance, maximumTransmittance);
                cloud = totalWeight > 0.00001
                    ? cloud / totalWeight
                    : SAMPLE_TEXTURE2D_X(_SolCloudRenderTexture, sampler_PointClamp, uv);
                float opacity = 1.0 - cloud.a;
                float edgeRange = maximumTransmittance - minimumTransmittance;
                float recoveredOpacity = saturate(opacity
                    + edgeRange * 0.14 * smoothstep(0.08, 0.88, opacity));
                cloud.rgb *= recoveredOpacity / max(0.001, opacity);
                cloud.a = 1.0 - recoveredOpacity;
                // Lightning is emitted inside the ray-march now, weighted per sample by how
                // occluded that sample is from the sun. Adding it here instead lifted every
                // opaque pixel by the same amount, which flattened the deck at exactly the
                // moment it should have shown the most internal structure. The ghosting this
                // placement avoided is handled in the temporal pass, which rejects history
                // while the flash level is changing.
                return cloud;
            }
            ENDHLSL
        }
        Pass
        {
            Name "Sol Cloud Shadow Map"
            ZWrite Off ZTest Always Cull Off Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragShadow

            /// Light-space transmittance through the whole deck, rasterised once per cadence
            /// rather than per camera. This is the spatial half of cloud shadowing; the
            /// global scalar dim stays as a low-frequency ambient term underneath it.
            half4 FragShadow(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;

                // Fade the final strength across the outer five percent so the square
                // footprint cannot become visible during fast weather or low sun.
                float2 edge = min(uv, 1.0 - uv);
                float borderFade = smoothstep(0.0, _SolCloudShadowParams.z,
                    min(edge.x, edge.y));

                float3 originWS = _SolCloudShadowOrigin.xyz
                    + _SolCloudShadowAxisU.xyz * (uv.x - 0.5)
                    + _SolCloudShadowAxisV.xyz * (uv.y - 0.5);

                // The texel plane passes through the camera anchor, below the deck, so the
                // march runs toward the light: SolCloudShellSegment then hands back the
                // segment between the cloud base and the cloud top along that ray. A point
                // already above the deck gets no intersection and stays fully lit.
                float3 direction = _SolCloudLightDirection.xyz;
                float startDistance, endDistance;
                if (!SolCloudShellSegment(originWS, direction, startDistance, endDistance))
                    return half4(1.0, 1.0, 1.0, 1.0);

                int samples = clamp((int)_SolCloudShadowParams.x, 1, 8);
                float stepLength = (endDistance - startDistance) / samples;
                float opticalDepth = 0.0;
                [loop]
                for (int i = 0; i < 8; i++)
                {
                    if (i >= samples) break;
                    float3 samplePosition = originWS
                        + direction * (startDistance + stepLength * (i + 0.5));
                    // Detail is deliberately skipped: at tens of metres per texel it would
                    // only alias, and the deck's macro form is what casts a readable shadow.
                    opticalDepth += SolCloudDensity(samplePosition, false, 1.5);
                }

                float transmittance = exp(-opticalDepth * _SolCloudScales.w
                    * (stepLength * 0.001));
                float shadow = lerp(1.0, transmittance,
                    _SolCloudShadowParams.y * borderFade);
                return half4(shadow, shadow, shadow, 1.0);
            }
            ENDHLSL
        }
    }
}
