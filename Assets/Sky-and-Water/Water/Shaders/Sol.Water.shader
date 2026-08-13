// Sol.Water - Stylised water shader for URP (Unity 6 / URP 17)
//
// Visual target: Witcher 3 / Fable 2 style - clean, customisable.
//
// Features:
//   - 4-wave Gerstner displacement + low-frequency ocean swell (SolWaterWaves.hlsl)
//   - Dual scrolling normal maps + micro-detail normal
//   - Specular highlight with noise masking + directional sun streak
//   - Environment cubemap reflections with Fresnel (+ dark-probe fallback)
//   - Fake refraction with depth-aware distortion
//   - Depth-based colour absorption + stylized banding + horizon sheen
//   - Shore foam (depth-based, noise-masked, animated)
//   - Caustics projection on underwater surfaces
//   - ToD integration: _Sol_SunDirection, _Sol_DayFactor, _Sol_SunColor, _Sol_EclipseFactor
//   - Distance-based normal detail fade + mid-distance relaxation
//
// Architecture:
//   - Ocean-focused single path (no water-type feature variants).
//   - Material properties still control the visual look per water body.
//   - Global shader properties are set by TimeOfDay or external systems.
//
// Requirements:
//   - Enable "Depth Texture" and "Opaque Texture" in your URP Renderer Asset
//   - A system should set global shader properties: _Sol_SunDirection, _Sol_DayFactor, etc.

Shader "Sol/Water"
{
    Properties
    {
        // ================================================
        //  SURFACE COLOURS
        // ================================================
        [Header(Surface Colors)]
        [HDR] _ShallowColor ("Shallow Color",      Color) = (0.325, 0.807, 0.971, 0.725)
        [HDR] _DeepColor    ("Deep Color",          Color) = (0.003, 0.400, 0.650, 0.95)
        [HDR] _HorizonColor ("Horizon / Far Color", Color) = (0.15, 0.35, 0.55, 1)
        [HDR] _FresnelColor ("Reflection Tint",     Color) = (0.465, 0.797, 0.991, 1)

        // ================================================
        //  SUBSURFACE SCATTERING
        // ================================================
        [Header(Subsurface Scattering)]
        [HDR] _SSSColor   ("SSS Tint",   Color)        = (0.0, 0.8, 0.6, 1)
        _SSSIntensity      ("Intensity",  Range(0, 5)) = 1.0
        _SSSPower          ("Power",      Range(1, 10)) = 3.0

        // ================================================
        //  WAVES (2 simple sine waves)
        // ================================================
        [Header(Waves)]
        _WaveAmplitude  ("Amplitude",             Range(0, 2))    = 0.3
        _WaveFrequency  ("Frequency",             Range(0.1, 10)) = 1.5
        _WaveSpeed      ("Speed",                 Range(0, 5))    = 1.0
        _Wave1Direction ("Wave 1 Dir (XZ)",       Vector)          = (0.8, 0, 0.6, 0)
        _Wave2Direction ("Wave 2 Dir (XZ)",       Vector)          = (-0.5, 0, 0.75, 0)
        _Wave2Scale     ("Wave 2 Relative Scale", Range(0, 1))    = 0.5
        _WaveColorInfluence ("Wave Color Influence", Range(0, 1)) = 0.35
        _WaveSteepness   ("Crest Steepness (Gerstner)", Range(0, 1)) = 0.55
        _WaveDetailScale ("Detail Wave Strength",  Range(0, 1))   = 0.5
        _WaveLODFadeDistance ("Wave LOD Fade Distance", Range(20, 500)) = 100
        _SwellAmplitude  ("Swell Amplitude",       Range(0, 1))   = 0.15
        _SwellSpeed      ("Swell Speed",           Range(0, 1))   = 0.2
        _SwellDirection  ("Swell Direction (XZ)",  Vector)         = (0.05, 0, 0.03, 0)

        // ================================================
        //  NORMAL MAPS
        // ================================================
        [Header(Normal Maps)]
        [NoScaleOffset] _NormalMap1      ("Normal Map 1",          2D) = "bump" {}
        [NoScaleOffset] _NormalMap2      ("Normal Map 2",          2D) = "bump" {}
        [NoScaleOffset] _NormalMapDetail ("Detail Normal (micro)", 2D) = "bump" {}
        _NormalTiling         ("Tiling",          Range(0.01, 50)) = 1.0
        _NormalDetailTiling   ("Detail Tiling",   Range(1, 100))   = 8.0
        _NormalStrength       ("Strength",        Range(0, 3))     = 1.0
        _NormalDetailStrength ("Detail Strength",  Range(0, 2))    = 0.5
        _NormalScrollDir1     ("Scroll Dir 1 (XY)", Vector)        = (0.7, 0.5, 0, 0)
        _NormalScrollDir2     ("Scroll Dir 2 (XY)", Vector)        = (-0.4, 0.8, 0, 0)
        _NormalScrollSpeed    ("Scroll Speed 1",  Range(0, 1))     = 0.03
        _NormalScrollSpeed2   ("Scroll Speed 2",  Range(0, 1))     = 0.03
        _NormalDetailScrollDir ("Detail Scroll Dir (XY)", Vector)  = (0.02, -0.01, 0, 0)

        // ================================================
        //  REFRACTION
        // ================================================
        [Header(Refraction)]
        _RefractionStrength     ("Distortion",    Range(0, 0.5)) = 0.04
        _RefractionDepthFade    ("Depth Fade",    Range(0.1, 30)) = 3.0
        _RefractionDistanceFade ("Distance Fade", Range(1, 500)) = 80
        _RefractionNearSurfaceFade ("Distortion Near Surface Fade", Range(0.01, 2)) = 0.25
        _RefractionEdgeFadeDistance ("Distortion Edge Clamp", Range(0.02, 5)) = 0.5

        // ================================================
        //  SPECULAR
        // ================================================
        [Header(Specular)]
        _Roughness     ("Roughness",          Range(0.01, 1)) = 0.06
        _SpecIntensity ("Specular Intensity", Range(0, 300))  = 100
        _SparkleSharpness  ("Sparkle Sharpness",  Range(1, 8))   = 3.0
        _SparkleThreshold  ("Sparkle Threshold",  Range(0, 1))   = 0.6
        _SparkleIntensity  ("Sparkle Intensity",  Range(0, 10))  = 4.0
        [NoScaleOffset] _NoiseMap ("Sparkle Noise", 2D) = "white" {}
        _SunStreakPower     ("Sun Streak Power",     Range(10, 500)) = 200
        _SunStreakIntensity ("Sun Streak Intensity", Range(0, 5))    = 2.0

        // ================================================
        //  FOAM (shore only)
        // ================================================
        [Header(Foam)]
        [NoScaleOffset] _FoamMap ("Foam Texture", 2D) = "white" {}
        _FoamColor      ("Foam Color",    Color)          = (1, 1, 1, 1)
        _FoamTiling     ("Tiling",        Range(0.1, 20)) = 2
        _FoamBrightness ("Brightness",    Range(0.1, 10)) = 2.5
        _FoamShoreWidth ("Shore Width",   Range(0.01, 5)) = 0.5
        _FoamShorePower     ("Shore Falloff",    Range(0.5, 10)) = 3
        _CrestFoamThreshold ("Whitecap Threshold", Range(-1, 2)) = 0.95
        _CrestFoamSharpness ("Whitecap Sharpness", Range(0.1, 20)) = 2

        // ================================================
        //  CAUSTICS
        // ================================================
        [Header(Caustics)]
        [NoScaleOffset] _CausticsMap ("Caustics Texture", 2D) = "black" {}
        _CausticsTiling    ("Tiling",    Range(0.1, 20)) = 3
        _CausticsSpeed     ("Speed",     Range(0, 2))    = 0.3
        _CausticsIntensity ("Intensity", Range(0, 5))    = 1.5
        _CausticsDepth     ("Max Depth", Range(0.1, 20)) = 3

        // ================================================
        //  DEPTH & TRANSPARENCY
        // ================================================
        [Header(Depth and Transparency)]
        _DepthSofteningDistance ("Shore Softening",        Range(0.01, 5))  = 0.5
        _DepthColorDistance     ("Absorption Distance",    Range(0.1, 80))  = 8
        _DepthHorizonDistance   ("Horizon Blend Distance", Range(10, 500))  = 80
        _HorizonFalloff         ("Horizon Falloff Power",  Range(0.5, 5))   = 2.0
        _NormalDetailDistanceFade ("Detail Fade Distance", Range(5, 200))   = 50
        _DepthBanding ("Depth Banding", Range(0, 1)) = 0.3

        // ================================================
        //  FRESNEL & REFLECTIONS
        // ================================================
        [Header(Fresnel)]
        _FresnelPower  ("Fresnel Power",       Range(0.1, 10)) = 4
        _FresnelBias   ("Fresnel Bias",        Range(0, 0.5))  = 0.02
        _FresnelSplitStrength ("Fresnel Split Strength", Range(0.5, 4)) = 1.7
        _ReflectionStr ("Reflection Strength", Range(0, 1))    = 1
        _HorizonSheenStr ("Horizon Sheen", Range(0, 1)) = 0.5

        // ================================================
        //  SCREEN-SPACE REFLECTIONS
        // ================================================
        [Header(Screen Space Reflections)]
        [Toggle(_SSR_ON)] _SSREnabled ("Enable SSR", Float) = 1
        _SSRMaxDistance ("Max Distance",     Range(1, 200))   = 50
        _SSRSteps       ("March Steps",      Range(8, 64))    = 24
        _SSRThickness   ("Hit Thickness",    Range(0.05, 5))  = 0.6
        _SSREdgeFade    ("Screen Edge Fade", Range(0.01, 0.5)) = 0.15
        _SSRIntensity   ("Intensity",        Range(0, 1))     = 1
        _SSRDebugView   ("Debug View (red = miss, green = hit)", Range(0, 1)) = 0

        [Header(Shadowing)]
        _UnderwaterShadowStrength ("Underwater Shadow Strength", Range(0, 1)) = 0.6
        _UnderwaterShadowFadeDistance ("Underwater Shadow Fade Distance", Range(0.1, 30)) = 4.0

        // ================================================
        //  EDGE DAMPENING
        // ================================================
        [Header(Edge Dampening)]
        _DampeningFactor ("Dampening Factor", Range(1, 20)) = 5

        // ================================================
        //  ToD INTEGRATION
        // ================================================
        [Header(Time of Day)]
        _ToDReflectionInfluence ("ToD Reflection Tint",    Range(0, 1)) = 0.5
        _ToDColorInfluence      ("ToD Color Influence",    Range(0, 1)) = 0.3
        _ToDSpecInfluence       ("ToD Specular Influence", Range(0, 1)) = 0.5
        _NightShallowColor     ("Night Shallow Color",  Color) = (0.02, 0.08, 0.15, 0.8)
        _NightDeepColor        ("Night Deep Color",     Color) = (0.01, 0.03, 0.08, 0.95)
        _NightHorizonColor     ("Night Horizon Color",  Color) = (0.05, 0.08, 0.15, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Transparent"
            "Queue"          = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        // =============================================
        //  PASS 1 -- FORWARD
        // =============================================
        Pass
        {
            Name "WaterForward"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            // ZWrite ON is deliberate for a transparent surface: the water
            // must self-occlude so LOD skirts and the back sides of waves
            // never show through the surface (the water is visually
            // near-opaque anyway - the scene below comes from refraction,
            // not blending). URP copies the depth texture after opaques,
            // so refraction/shore-depth reads are unaffected.
            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma vertex   WaterVert
            #pragma fragment WaterFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fog
            #pragma shader_feature_local_fragment _SSR_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "SolWaterWaves.hlsl"
            #include "SolAtmosphere.hlsl"

            // -----------------------------------------
            //  Globals (set by SolWaterManager)
            // -----------------------------------------
            float4 _Sol_SunDirection;
            float4 _Sol_SunColor;
            float  _Sol_DayFactor;
            float  _Sol_EclipseFactor;

            // Global motion & weather (set by SolWaterManager)
            float4 _Sol_WindDirection;         // normalised XZ wind dir
            float  _Sol_WindStrength;           // wind strength multiplier
            float  _Sol_GlobalWaveSpeedMul;     // global wave speed multiplier
            float  _Sol_WaveTime;               // accumulated time scaled by global wave speed
            float  _Sol_RainIntensity;          // 0 = dry, 1 = heavy rain
            float  _Sol_RainRoughnessBoost;     // configured roughness increase at full rain
            float  _Sol_RainNormalBoost;        // configured detail-normal increase at full rain
            float  _Sol_RainReflectionDampen;   // configured reflection reduction at full rain
            float  _Sol_LightningFlash;         // momentary storm illumination
            float  _Sol_GlobalWaterLevel;       // gameplay water level
            float4 _Sol_WaterDynamics;          // turbulence, spring tide, illumination, lunar response
            float4 _Sol_WaveFadeCenter;         // xyz = LOD ring centre, w = 1 while driven

            // Interactive ripples (set by WaterRippleManager)
            // Each float4: xy = world XZ position, z = birth time, w = strength
            #define SOL_MAX_RIPPLES 32
            float4 _Sol_Ripples[SOL_MAX_RIPPLES];
            int    _Sol_RippleCount;
            float  _Sol_RippleSpeed;       // ring expansion speed (world units/sec)
            float  _Sol_RippleFrequency;   // ring density
            float  _Sol_RippleLifetime;    // seconds before full fade
            float  _Sol_RippleTime;        // Time.time clock matching ripple birth stamps

            // GPU ripple simulation (set by WaterRippleManager when active)
            TEXTURE2D(_Sol_RippleSimTex);
            SAMPLER(sampler_Sol_RippleSimTex);
            float4 _Sol_RippleSimRegion;   // xy = window min XZ, z = 1/size, w = active
            float4 _Sol_RippleSimParams;   // x = 1/resolution, y = gradient-to-normal scale

            // -----------------------------------------
            //  Material CBUFFER (SRP Batcher)
            // -----------------------------------------
            CBUFFER_START(UnityPerMaterial)
                half4  _ShallowColor;
                half4  _DeepColor;
                half4  _HorizonColor;
                half4  _FresnelColor;
                half4  _NightShallowColor;
                half4  _NightDeepColor;
                half4  _NightHorizonColor;

                float  _WaveAmplitude;
                float  _WaveFrequency;
                float  _WaveSpeed;
                float4 _Wave1Direction;
                float4 _Wave2Direction;
                float  _Wave2Scale;
                float  _WaveColorInfluence;
                float  _WaveSteepness;
                float  _WaveDetailScale;
                float  _WaveLODFadeDistance;

                float  _NormalTiling;
                float  _NormalDetailTiling;
                float  _NormalStrength;
                float  _NormalDetailStrength;
                float4 _NormalScrollDir1;
                float4 _NormalScrollDir2;
                float  _NormalScrollSpeed;
                float  _NormalScrollSpeed2;
                float4 _NormalDetailScrollDir;

                float  _RefractionStrength;
                float  _RefractionDepthFade;
                float  _RefractionDistanceFade;
                float  _RefractionNearSurfaceFade;
                float  _RefractionEdgeFadeDistance;

                float  _Roughness;
                float  _SpecIntensity;
                float  _SparkleSharpness;
                float  _SparkleThreshold;
                float  _SparkleIntensity;

                half4  _FoamColor;
                float  _FoamTiling;
                float  _FoamBrightness;
                float  _FoamShorePower;
                float  _FoamShoreWidth;

                float  _CausticsTiling;
                float  _CausticsSpeed;
                float  _CausticsIntensity;
                float  _CausticsDepth;

                float  _DepthSofteningDistance;
                float  _DepthColorDistance;
                float  _DepthHorizonDistance;
                float  _HorizonFalloff;
                float  _NormalDetailDistanceFade;

                float  _FresnelPower;
                float  _FresnelBias;
                float  _FresnelSplitStrength;
                float  _ReflectionStr;
                float  _UnderwaterShadowStrength;
                float  _UnderwaterShadowFadeDistance;

                float  _DampeningFactor;

                float  _ToDReflectionInfluence;
                float  _ToDColorInfluence;
                float  _ToDSpecInfluence;

                half4  _SSSColor;
                float  _SSSIntensity;
                float  _SSSPower;
                float  _CrestFoamThreshold;
                float  _CrestFoamSharpness;
                float  _SwellAmplitude;
                float  _SwellSpeed;
                float4 _SwellDirection;
                float  _SunStreakPower;
                float  _SunStreakIntensity;
                float  _DepthBanding;
                float  _HorizonSheenStr;

                float  _SSRMaxDistance;
                float  _SSRSteps;
                float  _SSRThickness;
                float  _SSREdgeFade;
                float  _SSRIntensity;
                float  _SSRDebugView;
            CBUFFER_END

            TEXTURE2D(_NormalMap1);       SAMPLER(sampler_NormalMap1);
            TEXTURE2D(_NormalMap2);       SAMPLER(sampler_NormalMap2);
            TEXTURE2D(_NormalMapDetail);  SAMPLER(sampler_NormalMapDetail);
            TEXTURE2D(_NoiseMap);         SAMPLER(sampler_NoiseMap);
            TEXTURE2D(_FoamMap);          SAMPLER(sampler_FoamMap);
            TEXTURE2D(_CausticsMap);      SAMPLER(sampler_CausticsMap);

            // =========================================
            //  STRUCTS
            // =========================================
            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 uv         : TEXCOORD0; // xy = mesh uv, zw = world xz
                float3 positionWS : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
                float3 tangentWS  : TEXCOORD3;
                float3 binormalWS : TEXCOORD4;
                float4 screenPos  : TEXCOORD5;
                float2 fogAndDist : TEXCOORD6; // x = fogFactor, y = camDist
                float  waveHeight : TEXCOORD8; // world-space Y displacement from waves
            };

            // =========================================
            //  VERTEX SHADER
            // =========================================
            Varyings WaterVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);

                // Gerstner evaluation shared with the DepthOnly pass and the
                // C# gameplay sampler (SolWaterWaves.hlsl). Distance fades
                // keep far tiles to frequencies their vertex density can
                // sample, closing LOD seams and cleaning the horizon. The
                // fade centre is the grid's LOD centre - NOT the rendering
                // camera - so every camera sees consistent geometry.
                float2 fadeCenter = _Sol_WaveFadeCenter.w > 0.5
                    ? _Sol_WaveFadeCenter.xz
                    : GetCameraPositionWS().xz;
                float3 lodFades = SolComputeWaveFades(
                    posWS.xz, fadeCenter, _WaveLODFadeDistance);
                float3 waveDisp, waveNormal;
                SolEvaluateWaves(
                    posWS.xz, _Sol_WaveTime,
                    _WaveAmplitude, _WaveFrequency, _WaveSpeed,
                    _Wave1Direction.xz, _Wave2Direction.xz, _Wave2Scale,
                    _WaveSteepness, _WaveDetailScale,
                    _SwellAmplitude, _SwellSpeed, _SwellDirection.xz,
                    _Sol_WindDirection.xz, _Sol_WindStrength,
                    _Sol_WaterDynamics.x, _Sol_WaterDynamics.y, _Sol_WaterDynamics.w,
                    lodFades,
                    waveDisp, waveNormal);

                posWS += waveDisp;

                // World-X-aligned orthonormal frame for normal mapping
                // (T ~ +X, B ~ -Z on a flat surface, matching the previous
                // convention so authored normal maps read the same).
                float3 waveTangent = normalize(cross(waveNormal, float3(0, 0, 1)));
                float3 waveBinorm  = cross(waveNormal, waveTangent);

                OUT.positionWS   = posWS;
                OUT.positionCS   = TransformWorldToHClip(posWS);
                OUT.screenPos    = ComputeScreenPos(OUT.positionCS);
                OUT.normalWS     = waveNormal;
                OUT.tangentWS    = waveTangent;
                OUT.binormalWS   = waveBinorm;
                OUT.uv           = float4(IN.uv, posWS.xz);
                OUT.fogAndDist.x = ComputeFogFactor(OUT.positionCS.z);
                OUT.fogAndDist.y = length(posWS - GetCameraPositionWS());
                OUT.waveHeight   = waveDisp.y;

                return OUT;
            }

            // =========================================
            //  UTILITIES
            // =========================================
            float DistributionGGX(float NdotH, float a2)
            {
                float d = NdotH * NdotH * (a2 - 1.0) + 1.0;
                return a2 / (PI * d * d + 1e-7);
            }

            float SchlickFresnel(float NdotV, float bias, float power)
            {
                return bias + (1.0 - bias) * pow(1.0 - NdotV, power);
            }

            float3 BlendNormalsUDN(float3 a, float3 b)
            {
                return normalize(float3(a.xz + b.xz, a.y).xzy);
            }

            #if defined(_SSR_ON)
            // =========================================
            //  SCREEN-SPACE REFLECTION TRACE
            //  Marches through the ray's projected pixel footprint instead of
            //  fixed world-space distances. This keeps sample density coherent
            //  in screen space, which matters most for shallow water views.
            //  The opaque textures exclude transparents, so the water never
            //  self-hits. Returns hit confidence (0 = miss) and writes hit UV.
            // =========================================

            // Explicit-LOD depth read: implicit-LOD sampling (SampleSceneDepth)
            // is undefined inside the divergent march loop and some compilers
            // silently produce garbage there.
            float SampleSceneDepthLod(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture,
                    sampler_CameraDepthTexture, uv, 0).r;
            }

            float2 ClipToScreenUV(float4 clipPos)
            {
                float4 sp = ComputeScreenPos(clipPos);
                return sp.xy / max(sp.w, 1e-4);
            }

            float PerspectiveRayEyeDepth(float startW, float endW, float p)
            {
                float startInvW = rcp(max(startW, 1e-4));
                float endInvW   = rcp(max(endW,   1e-4));
                return rcp(lerp(startInvW, endInvW, saturate(p)));
            }

            float TraceScreenSpaceReflection(float3 origin, float3 dir, float2 pixelPos, out float2 hitUV)
            {
                hitUV = float2(0.0, 0.0);

                float maxDistance = max(_SSRMaxDistance, 0.01);
                float startT = max(0.05, _SSRThickness * 0.25);
                if (startT >= maxDistance)
                    return 0.0;

                float4 startClip = TransformWorldToHClip(origin + dir * startT);
                float4 endClip   = TransformWorldToHClip(origin + dir * maxDistance);
                if (startClip.w < 0.1 || endClip.w < 0.1)
                    return 0.0;

                float2 screenSize = max(_ScaledScreenParams.xy, float2(1.0, 1.0));
                float2 startUV = ClipToScreenUV(startClip);
                float2 endUV   = ClipToScreenUV(endClip);
                if (any(startUV < 0.0) || any(startUV > 1.0))
                    return 0.0;

                float2 uvDelta = endUV - startUV;
                float screenExitP = 1.0;
                if (uvDelta.x > 0.0) screenExitP = min(screenExitP, (1.0 - startUV.x) / uvDelta.x);
                if (uvDelta.x < 0.0) screenExitP = min(screenExitP, (0.0 - startUV.x) / uvDelta.x);
                if (uvDelta.y > 0.0) screenExitP = min(screenExitP, (1.0 - startUV.y) / uvDelta.y);
                if (uvDelta.y < 0.0) screenExitP = min(screenExitP, (0.0 - startUV.y) / uvDelta.y);
                screenExitP = saturate(screenExitP);

                float2 startPixel = startUV * screenSize;
                float2 endPixel   = lerp(startUV, endUV, screenExitP) * screenSize;
                float2 deltaPixel = endPixel - startPixel;

                float pixelSpan = max(abs(deltaPixel.x), abs(deltaPixel.y));
                if (pixelSpan < 1.0)
                    return 0.0;

                float maxSteps = max(_SSRSteps, 4.0);
                float marchSteps = clamp(pixelSpan, 4.0, maxSteps);
                float stridePixels = max(1.0, pixelSpan / marchSteps);
                float2 pixelDir = deltaPixel / max(pixelSpan, 1e-4);

                // Sub-pixel jitter avoids locked bands without shifting whole
                // world-space steps, which was the main source of speckled
                // hit/miss disagreement on water.
                float jitter = frac(52.9829189 * frac(dot(pixelPos, float2(0.06711056, 0.00583715))));
                float jitterPixels = (jitter - 0.5) * 0.5;

                float prevP = 0.0;
                float prevDepthDelta = -1e6;
                float hitP = -1.0;

                [loop]
                for (float s = 1.0; s <= _SSRSteps; s += 1.0)
                {
                    if (s > marchSteps)
                        break;

                    float travelPixels = min(pixelSpan, s * stridePixels + jitterPixels);
                    float p = travelPixels / pixelSpan;
                    float fullRayP = p * screenExitP;
                    float2 uv = (startPixel + pixelDir * travelPixels) / screenSize;
                    if (any(uv < 0.0) || any(uv > 1.0))
                        break;

                    float sceneZ = LinearEyeDepth(SampleSceneDepthLod(uv), _ZBufferParams);
                    if (sceneZ >= _ProjectionParams.z * 0.99)
                    {
                        prevP = p;
                        prevDepthDelta = -1e6;
                        continue;
                    }

                    float rayZ = PerspectiveRayEyeDepth(startClip.w, endClip.w, fullRayP);
                    float rayT = lerp(startT, maxDistance, fullRayP);
                    float thickness = min(_SSRThickness * (1.0 + rayT * 0.01), _SSRThickness * 2.5);
                    float depthDelta = rayZ - sceneZ;

                    bool crossedOpaqueDepth = prevDepthDelta <= 0.0 && depthDelta >= 0.0;
                    bool insideThickness = depthDelta > 0.0 && depthDelta < thickness;
                    if (crossedOpaqueDepth && insideThickness)
                    {
                        hitP = p;
                        break;
                    }

                    prevP = p;
                    prevDepthDelta = depthDelta;
                }

                if (hitP < 0.0)
                    return 0.0;

                // Binary refinement in screen-param space between the last
                // front-of-depth sample and the first accepted hit.
                float pNear = prevP;
                float pFar  = hitP;
                [unroll]
                for (int r = 0; r < 4; r++)
                {
                    float pMid = 0.5 * (pNear + pFar);
                    float2 uv = lerp(startPixel, endPixel, pMid) / screenSize;
                    float sceneZ = LinearEyeDepth(SampleSceneDepthLod(uv), _ZBufferParams);
                    float rayZ = PerspectiveRayEyeDepth(startClip.w, endClip.w, pMid * screenExitP);
                    if (rayZ > sceneZ) pFar = pMid; else pNear = pMid;
                }

                hitUV = lerp(startPixel, endPixel, pFar) / screenSize;
                if (any(hitUV < 0.0) || any(hitUV > 1.0))
                    return 0.0;

                float finalSceneZ = LinearEyeDepth(SampleSceneDepthLod(hitUV), _ZBufferParams);
                if (finalSceneZ >= _ProjectionParams.z * 0.99)
                    return 0.0;

                float finalFullRayP = pFar * screenExitP;
                float finalRayZ = PerspectiveRayEyeDepth(startClip.w, endClip.w, finalFullRayP);
                float finalRayT = lerp(startT, maxDistance, finalFullRayP);
                float finalThickness = min(_SSRThickness * (1.0 + finalRayT * 0.01), _SSRThickness * 2.5);
                float finalDepthDelta = abs(finalRayZ - finalSceneZ);

                // Confidence: depth fit rejects uncertain hit/miss gaps, while
                // edge and distance fades hand off to the reflection probe.
                float2 edge = min(hitUV, 1.0 - hitUV);
                float edgeFade = saturate(min(edge.x, edge.y) / max(_SSREdgeFade, 1e-3));
                float distFade = 1.0 - saturate(finalRayT / maxDistance);
                float depthFit = 1.0 - saturate(finalDepthDelta / max(finalThickness, 1e-3));
                float travelFade = smoothstep(1.0, 8.0, pFar * pixelSpan);
                return edgeFade * saturate(distFade * 2.0) * depthFit * travelFade;
            }
            #endif // _SSR_ON

            // =========================================
            //  FRAGMENT SHADER
            // =========================================
            half4 WaterFrag(Varyings IN) : SV_Target
            {
                // -- LOD skirt handling --
                // Skirt vertices carry mesh UVs shifted by +2 (WaterTileGrid).
                // Seen from above they fill LOD seams and shade like the
                // adjacent surface; underwater they would read as vertical
                // walls, so discard them there.
                if (IN.uv.x > 1.5 && GetCameraPositionWS().y < _Sol_GlobalWaterLevel)
                    discard;

                // -- ToD + eclipse factors --
                float dayFactor    = saturate(_Sol_DayFactor);
                float eclipseAtten = 1.0 - _Sol_EclipseFactor * 0.7;

                // -- Day/night blended surface colours --
                half3 shallowCol = lerp(_NightShallowColor.rgb, _ShallowColor.rgb, dayFactor);
                half3 deepCol    = lerp(_NightDeepColor.rgb,    _DeepColor.rgb,    dayFactor);
                half3 horizonBlend = lerp(_NightHorizonColor.rgb, _HorizonColor.rgb, dayFactor);

                shallowCol   *= eclipseAtten;
                deepCol      *= eclipseAtten;
                horizonBlend *= eclipseAtten;

                // -- Basis vectors --
                float3 N = normalize(IN.normalWS);
                float3 T = normalize(IN.tangentWS);
                float3 B = normalize(IN.binormalWS);

                // -- UV setup (world-space XZ for seamless tiling) --
                float2 worldUV = IN.uv.zw;
                float2 baseUV  = worldUV * _NormalTiling;

                // -- Scrolling normal maps (rain boosts detail) --
                float rainNormalBoost = 1.0 + _Sol_RainIntensity * _Sol_RainNormalBoost;
                float scrollSpd  = _NormalScrollSpeed;
                float scrollSpd2 = _NormalScrollSpeed2;
                float2 nmUV1 = baseUV + _Sol_WaveTime * _NormalScrollDir1.xy * scrollSpd;
                float2 nmUV2 = baseUV + _Sol_WaveTime * _NormalScrollDir2.xy * scrollSpd2;

                float3 nTS1 = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap1, sampler_NormalMap1, nmUV1), _NormalStrength);
                float3 nTS2 = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap2, sampler_NormalMap2, nmUV2), _NormalStrength);

                // -- Micro-detail normal (fades with distance) --
                float detailFade = 1.0 - saturate(IN.fogAndDist.y / _NormalDetailDistanceFade);
                float2 detailUV  = worldUV * _NormalDetailTiling + _Sol_WaveTime * _NormalDetailScrollDir.xy;
                float3 nDetail   = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMapDetail, sampler_NormalMapDetail, detailUV),
                    _NormalDetailStrength * detailFade * rainNormalBoost);

                // -- Combine normals --
                float3x3 TBN = float3x3(T, B, N);
                float3 worldN1 = mul(nTS1, TBN);
                float3 worldN2 = mul(nTS2, TBN);
                float3 worldND = mul(nDetail, TBN);
                float3 finalNormal = BlendNormalsUDN(worldN1, worldN2);
                finalNormal = BlendNormalsUDN(finalNormal, worldND);

                // -- Mid-distance normal relaxation (reduces shimmer band) --
                float midFade = saturate((IN.fogAndDist.y - 20.0) / 80.0);
                finalNormal = normalize(lerp(finalNormal, N, midFade * 0.5));

                // =====================================
                //  INTERACTIVE RIPPLES
                //  Analytical concentric rings emitted by objects in the water.
                //  Each ripple is a point source expanding outward as a ring;
                //  the cos of the ring distance gives the radial slope which
                //  is converted directly to a normal perturbation.
                // =====================================
                {
                    float3 rippleNOff = 0;
                    for (int r = 0; r < min(_Sol_RippleCount, SOL_MAX_RIPPLES); r++)
                    {
                        float2 delta = worldUV - _Sol_Ripples[r].xy;
                        float  dist  = length(delta);
                        float  age   = _Sol_RippleTime - _Sol_Ripples[r].z;

                        // Ring expansion: signed distance to the expanding ring front
                        float ringDist = dist - age * _Sol_RippleSpeed;

                        // Attenuation: quadratic age fade + distance falloff
                        float ageFade  = saturate(1.0 - age / _Sol_RippleLifetime);
                        ageFade *= ageFade; // quadratic for a natural tail-off
                        float distAtten = 1.0 / (1.0 + dist * 0.4);

                        // cos = slope of the sin wave  correct normal direction
                        float slope = cos(ringDist * _Sol_RippleFrequency)
                                    * ageFade * distAtten * _Sol_Ripples[r].w;

                        // Radial direction for the perturbation
                        float2 dir = delta / (dist + 0.001);
                        rippleNOff.xz += dir * slope;
                    }
                    finalNormal = normalize(finalNormal + rippleNOff);
                }

                // =====================================
                //  GPU RIPPLE SIMULATION
                //  Height-field gradient from the wave-equation sim texture
                //  (WaterRippleManager). Replaces the analytic rings when
                //  active (_Sol_RippleCount is forced to 0 by the manager).
                //  Influence fades at the sim window border so ripples
                //  vanish smoothly as the window follows the player.
                // =====================================
                if (_Sol_RippleSimRegion.w > 0.5)
                {
                    float2 simUV = (worldUV - _Sol_RippleSimRegion.xy) * _Sol_RippleSimRegion.z;
                    float2 border = min(simUV, 1.0 - simUV);
                    float simFade = saturate(min(border.x, border.y) * 12.0);
                    if (simFade > 0.001)
                    {
                        // Explicit LOD: implicit-LOD sampling is undefined in
                        // divergent branches (the sim RT has no mips anyway).
                        float texel = _Sol_RippleSimParams.x;
                        float hC = SAMPLE_TEXTURE2D_LOD(_Sol_RippleSimTex, sampler_Sol_RippleSimTex, simUV, 0).r;
                        float hX = SAMPLE_TEXTURE2D_LOD(_Sol_RippleSimTex, sampler_Sol_RippleSimTex, simUV + float2(texel, 0), 0).r;
                        float hZ = SAMPLE_TEXTURE2D_LOD(_Sol_RippleSimTex, sampler_Sol_RippleSimTex, simUV + float2(0, texel), 0).r;
                        float2 slope = float2(hX - hC, hZ - hC) * _Sol_RippleSimParams.y;
                        finalNormal = normalize(finalNormal + float3(-slope.x, 0, -slope.y) * simFade);
                    }
                }

                // -- View / Light setup --
                float3 viewDirWS = normalize(GetCameraPositionWS() - IN.positionWS);
                float2 screenUV  = IN.screenPos.xy / IN.screenPos.w;

                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light  mainLight   = GetMainLight(shadowCoord);
                float3 todLightDir = _Sol_SunDirection.xyz;
                todLightDir = dot(todLightDir, todLightDir) > 0.0001
                    ? normalize(todLightDir)
                    : normalize(mainLight.direction);
                float3 lightDir    = normalize(lerp(mainLight.direction, todLightDir, _ToDSpecInfluence));
                float  lightAtten  = mainLight.distanceAttenuation;
                float3 lightColor  = lerp(mainLight.color, _Sol_SunColor.rgb, _ToDSpecInfluence) * lightAtten;

                float3 todSunColor = lerp(float3(1,1,1), _Sol_SunColor.rgb, _ToDSpecInfluence);

                // -- Scene depth --
                float rawDepth     = SampleSceneDepth(screenUV);
                float sceneEyeZ    = LinearEyeDepth(rawDepth, _ZBufferParams);
                float surfaceEyeZ  = IN.screenPos.w;
                float depthDiff    = sceneEyeZ - surfaceEyeZ;

                float depthSoftenAlpha = smoothstep(0.0, _DepthSofteningDistance, depthDiff);

                // Soften shadow contrast with depth through the water body.
                // Deeper bottoms get less hard shadow imprint for more natural attenuation.
                float underwaterShadowDepthFade = exp(-max(depthDiff, 0.0) / max(_UnderwaterShadowFadeDistance, 0.001));
                float underwaterShadowInfluence = saturate(_UnderwaterShadowStrength * underwaterShadowDepthFade * depthSoftenAlpha);
                float softenedShadowAtten = lerp(1.0, mainLight.shadowAttenuation, underwaterShadowInfluence);
                lightAtten *= softenedShadowAtten;
                lightColor = lerp(mainLight.color, _Sol_SunColor.rgb, _ToDSpecInfluence) * lightAtten;

                // Precompute roughness terms once - used by main specular and additional lights.
                // Rain increases roughness, reducing specular sharpness.
                float  rainRoughBoost = saturate(_Roughness
                    + _Sol_RainIntensity * _Sol_RainRoughnessBoost
                    + _Sol_WaterDynamics.x * 0.08);
                float  linRough  = rainRoughBoost * rainRoughBoost;
                float  roughness4 = linRough * linRough;

                // =====================================
                //  SPECULAR
                // =====================================
                float3 halfVec  = normalize(viewDirWS + lightDir);
                float  NdotH    = saturate(dot(finalNormal, halfVec));
                float  NdotL    = saturate(dot(finalNormal, lightDir));
                float  specTerm = DistributionGGX(NdotH, roughness4) * NdotL;

                float  noise = SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, nmUV1 * 0.5).r;
                noise        *= SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, nmUV2 * 0.5).r;
                noise          = pow(saturate(noise), _SparkleSharpness);
                noise          = saturate((noise - _SparkleThreshold) * _SparkleIntensity);

                half3 specColor = specTerm * _SpecIntensity * noise * lightColor * todSunColor;

                // =====================================
                //  ENVIRONMENT REFLECTIONS
                // =====================================
                float3 reflectDir = reflect(-viewDirWS, finalNormal);
                float  mipLevel   = (1.0 - (1.0 - _Roughness) * (1.0 - _Roughness)) * UNITY_SPECCUBE_LOD_STEPS;
                half4  envSample  = SAMPLE_TEXTURECUBE_LOD(
                    unity_SpecCube0, samplerunity_SpecCube0, reflectDir, mipLevel);
                half3 envColor = DecodeHDREnvironment(envSample, unity_SpecCube0_HDR);

                half3 todReflTint = lerp(half3(1,1,1),
                    _Sol_SunColor.rgb * 0.5 + 0.5, _ToDReflectionInfluence);
                half3 todReflectionColor = lerp(horizonBlend, _Sol_SunColor.rgb, 0.5);
                half3 fresnelTint = lerp(_FresnelColor.rgb, todReflectionColor,
                    _ToDReflectionInfluence * (1.0 - dayFactor));
                // Rain dampens reflection clarity
                float lunarResponse = _Sol_WaterDynamics.y * _Sol_WaterDynamics.w;
                float rainReflDampen = saturate(1.0
                    - _Sol_RainIntensity * _Sol_RainReflectionDampen
                    - _Sol_WaterDynamics.x * 0.12
                    - lunarResponse * 0.08);
                envColor *= fresnelTint * todReflTint * _ReflectionStr * rainReflDampen;

                // Fallback when probe is absent or dark
                half  envLuma      = dot(envColor, half3(0.2126, 0.7152, 0.0722));
                half3 fallbackRefl = horizonBlend * fresnelTint
                                    * todReflTint * _ReflectionStr;
                envColor = lerp(fallbackRefl, envColor, smoothstep(0.02, 0.15, envLuma));

                // =====================================
                //  SCREEN-SPACE REFLECTIONS
                //  Real scene reflections (terrain, props, characters)
                //  marched from the opaque depth buffer; the probe result
                //  above remains as the miss/off-screen fallback.
                // =====================================
                #if defined(_SSR_ON)
                float ssrDebugConf = 0.0;
                {
                    float2 ssrUV;
                    float ssrConf = TraceScreenSpaceReflection(
                        IN.positionWS, reflectDir, IN.positionCS.xy, ssrUV);
                    ssrDebugConf = ssrConf;
                    ssrConf *= _SSRIntensity;
                    if (ssrConf > 0.0)
                    {
                        // SSR reflects ALREADY-SHADED scene geometry, so it
                        // should stay close to true color. Only a light water
                        // tint is applied (lerp toward white), unlike the sky
                        // probe which takes the full _FresnelColor.
                        half3 ssrTint = lerp(half3(1,1,1), fresnelTint, 0.35);
                        half3 ssrColor = SampleSceneColor(ssrUV)
                                       * ssrTint * todReflTint * rainReflDampen;
                        envColor = lerp(envColor, ssrColor, ssrConf);
                    }
                }
                #endif

                // =====================================
                //  REFRACTION
                // =====================================
                // Anti-smear: reduce distortion near the surface intersection and at depth discontinuities.
                float nearSurfaceFade = saturate(depthDiff / max(_RefractionNearSurfaceFade, 0.001));
                float2 distortUV = screenUV + finalNormal.xz * (_RefractionStrength * nearSurfaceFade);

                float distortedEyeZ = LinearEyeDepth(SampleSceneDepth(distortUV), _ZBufferParams);
                float depthMismatch = abs(distortedEyeZ - sceneEyeZ);
                float edgeFade = 1.0 - saturate(depthMismatch / max(_RefractionEdgeFadeDistance, 0.001));
                float distortionAccept = nearSurfaceFade * edgeFade;
                float2 candidateUV = lerp(screenUV, distortUV, distortionAccept);

                float candidateEyeZ = LinearEyeDepth(SampleSceneDepth(candidateUV), _ZBufferParams);
                // Reject distorted UV when it samples sky (depth at or beyond the far plane).
                bool useDistortedRefraction = (candidateEyeZ > surfaceEyeZ + 0.01 && candidateEyeZ < _ProjectionParams.z * 0.99);
                float2 refractionUV = useDistortedRefraction ? candidateUV : screenUV;
                // Reuse already-sampled depth when distortion is accepted, else reuse screen depth.
                float refractionEyeZ = useDistortedRefraction ? candidateEyeZ : sceneEyeZ;
                half3 sceneColor      = SampleSceneColor(refractionUV);
                float refractionDepth = max(0, refractionEyeZ - surfaceEyeZ);
                float refrDepthT      = saturate(refractionDepth / _RefractionDepthFade);
                half3 refractionColor = lerp(sceneColor * shallowCol, deepCol, refrDepthT);

                // -- ToD tint on refraction --
                half3 todRefrTint = lerp(half3(1,1,1),
                    _Sol_SunColor.rgb * 0.7 + 0.3, _ToDColorInfluence);
                refractionColor *= todRefrTint;

                // =====================================
                //  CAUSTICS
                // =====================================
                {
                    float causticsK    = 3.0 / max(_CausticsDepth, 0.01);
                    float causticsAtten = exp(-depthDiff * causticsK);

                    float2 causticsUV1 = worldUV * _CausticsTiling
                        + _Sol_WaveTime * _CausticsSpeed * float2(1, 0.7);
                    float2 causticsUV2 = worldUV * _CausticsTiling * 0.8
                        - _Sol_WaveTime * _CausticsSpeed * float2(0.6, 1);

                    half c1 = SAMPLE_TEXTURE2D(_CausticsMap, sampler_CausticsMap, causticsUV1).r;
                    half c2 = SAMPLE_TEXTURE2D(_CausticsMap, sampler_CausticsMap, causticsUV2).r;
                    half caustics = min(c1, c2);

                    // Keep water-surface caustics very subtle and only when
                    // the camera is below the water plane. Primary caustics
                    // for underwater readability are handled by the overlay.
                    float camUnderMask = step(GetCameraPositionWS().y, IN.positionWS.y);
                    refractionColor += caustics * _CausticsIntensity
                        * causticsAtten * lightColor * dayFactor * camUnderMask * 0.15;
                }

                // =====================================
                //  DEPTH ABSORPTION (shallow - deep - horizon)
                //  Wave height shifts the gradient so crests look shallower
                //  and troughs look deeper, providing colour breakup at
                //  every viewing angle.
                // =====================================
                float depthGradient = 1.0 - exp(-depthDiff / _DepthColorDistance);

                // Wave-driven colour variation: crests push toward shallow,
                // troughs push toward deep.  _WaveAmplitude guards divByZero.
                float waveNorm      = IN.waveHeight
                    / (_WaveAmplitude * (1.0 + _Wave2Scale + 0.57 * _WaveDetailScale) + 0.001);
                float waveColorShift = waveNorm * _WaveColorInfluence;
                depthGradient       = saturate(depthGradient - waveColorShift);

                // -- Stylized depth banding (painterly depth zones) --
                float bands = floor(depthGradient * 4.0) / 4.0;
                depthGradient = lerp(depthGradient, bands, _DepthBanding);

                half3 absorptionColor = lerp(shallowCol, deepCol, depthGradient);

                float horizonFade = saturate(IN.fogAndDist.y / _DepthHorizonDistance);
                float horizonPow  = pow(horizonFade, _HorizonFalloff);
                half3 horizonCol  = lerp(horizonBlend,
                    horizonBlend * todSunColor, _ToDColorInfluence);
                absorptionColor   = lerp(absorptionColor, horizonCol, horizonPow);

                refractionColor = lerp(refractionColor, absorptionColor, horizonFade);

                // =====================================
                //  FRESNEL + REFLECTION / REFRACTION BLEND
                //  Use the smooth wave-geometric normal (N) for Fresnel
                //  so the reflection/refraction blend varies gradually
                //  across the surface instead of following every normal-map
                //  ripple (which caused blobby bright/dark patches).
                // =====================================
                float NdotV_smooth = saturate(dot(N, viewDirWS));
                float NdotV_detail = saturate(dot(finalNormal, viewDirWS));
                float fresnel = SchlickFresnel(NdotV_smooth, _FresnelBias, _FresnelPower);
                float fresnelSplit = pow(saturate(fresnel), _FresnelSplitStrength);

                float distanceFade = saturate(IN.fogAndDist.y / _RefractionDistanceFade);

                float reflectionAmount = saturate(lerp(fresnelSplit, 1.0, distanceFade));
                half3 waterColor = lerp(refractionColor, envColor, reflectionAmount);

                // Attenuate specular at grazing angles where Fresnel reflection already dominates.
                // Use the detail NdotV so specular retains high-frequency variation.
                waterColor += specColor * (1.0 - SchlickFresnel(NdotV_detail, _FresnelBias, _FresnelPower));

                // =====================================
                //  SUN STREAK (directional highlight toward camera)
                // =====================================
                float3 sunReflect = reflect(-lightDir, finalNormal);
                float  sunAlign   = saturate(dot(sunReflect, viewDirWS));
                // Keep a floor at night: _Sol_SunColor is moon-tinted then
                // (SolWaterManager), giving a moonlight streak on the water.
                half3  sunStreak  = pow(sunAlign, _SunStreakPower) * _Sol_SunColor.rgb
                                  * _SunStreakIntensity * lightAtten
                                  * lerp(0.35, 1.0, dayFactor);
                waterColor += sunStreak;

                // =====================================
                //  HORIZON SHEEN (soft glow at grazing angles)
                // =====================================
                float horizonSheen = pow(1.0 - NdotV_smooth, 6.0);
                waterColor += horizonSheen * horizonBlend * _HorizonSheenStr;

                // =====================================
                //  SHORE FOAM
                // =====================================
                {
                    float2 foamUV   = worldUV * _FoamTiling
                                    + _Sol_WaveTime * float2(0.01, -0.01) * _FoamTiling;
                    half3  foamTex  = SAMPLE_TEXTURE2D(_FoamMap, sampler_FoamMap, foamUV).rgb
                                    * _FoamColor.rgb;
                    float  foamNoise = SAMPLE_TEXTURE2D(
                        _FoamMap, sampler_FoamMap, worldUV * _FoamTiling * 0.7).r;
                    half   foamToD   = lerp(0.35, 0.95, dayFactor) * eclipseAtten;

                    float shoreFoam = (depthDiff > 0.0)
                        ? pow(saturate(1.0 - depthDiff / _FoamShoreWidth), _FoamShorePower)
                        : 0.0;
                    shoreFoam *= foamNoise;

                    // -- Foam animation pulse --
                    float foamPulse = sin(_Sol_WaveTime * 2.0 + worldUV.x * 3.0) * 0.5 + 0.5;
                    shoreFoam *= lerp(0.6, 1.0, foamPulse);

                    waterColor = lerp(waterColor, foamTex * _FoamBrightness * foamToD,
                        saturate(shoreFoam) * depthSoftenAlpha);

                    // -- Wave crest whitecaps (steepness + height) --
                    float steepness = 1.0 - saturate(dot(finalNormal, float3(0, 1, 0)));
                    float dynamicsFoam = _Sol_WaterDynamics.x * 0.10 + lunarResponse * 0.08;
                    float crestFoam = saturate((steepness + IN.waveHeight * 0.5
                        - (_CrestFoamThreshold - dynamicsFoam)) * _CrestFoamSharpness)
                                    * foamNoise;
                    float dynamicsFoamBrightness = 1.0 + _Sol_WaterDynamics.x * 0.12
                        + lunarResponse * 0.12;
                    waterColor = lerp(waterColor,
                        foamTex * _FoamBrightness * dynamicsFoamBrightness * foamToD,
                        crestFoam);
                }

                // =====================================
                //  SUBSURFACE SCATTERING
                //  Backscatter: max when viewer looks toward the sun (light transmits through).
                // =====================================
                {
                    float sssWrap  = saturate(dot(lightDir, viewDirWS));
                    half3 sssColor = pow(sssWrap, _SSSPower) * _SSSColor.rgb
                                   * _SSSIntensity * (1.0 - depthGradient)
                                   * lightColor * dayFactor;
                    waterColor += sssColor;
                }

                // =====================================
                //  ADDITIONAL LIGHTS
                // =====================================
                #if defined(_ADDITIONAL_LIGHTS)
                {
                    uint pixelLightCount = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(pixelLightCount)
                        Light  addLight = GetAdditionalLight(lightIndex, IN.positionWS, half4(1,1,1,1));
                        float3 addDir   = normalize(addLight.direction);
                        float  addAtten = addLight.distanceAttenuation * addLight.shadowAttenuation;
                        float  addNdotL = saturate(dot(finalNormal, addDir));
                        // Diffuse
                        waterColor += addLight.color * addNdotL * addAtten * shallowCol * 0.3;
                        // Specular (GGX)
                        float3 addHalf  = normalize(viewDirWS + addDir);
                        float  addNdotH = saturate(dot(finalNormal, addHalf));
                        float  addSpec  = DistributionGGX(addNdotH, roughness4) * addNdotL;
                        waterColor += (half3)(addLight.color * addSpec * addAtten * _SpecIntensity * 0.1 * noise);
                    LIGHT_LOOP_END
                }
                #endif

                // =====================================
                //  SSR DEBUG OVERLAY (red = miss, green = hit confidence)
                // =====================================
                #if defined(_SSR_ON)
                if (_SSRDebugView > 0.001)
                {
                    half3 dbg = lerp(half3(0.6, 0.0, 0.0), half3(0.0, 0.8, 0.0),
                                     saturate(ssrDebugConf));
                    waterColor = lerp(waterColor, dbg, _SSRDebugView);
                }
                #endif

                // =====================================
                //  FOG
                // =====================================
                waterColor += _Sol_LightningFlash.xxx * (0.12 + fresnel * 0.22);
                if (_SolAtmosphereActive > 0.5)
                {
                    waterColor = SolApplyAtmosphere(
                        waterColor,
                        GetCameraPositionWS(),
                        IN.positionWS,
                        -viewDirWS,
                        0.0);
                }
                else
                {
                    waterColor = MixFog(waterColor, IN.fogAndDist.x);
                }

                return half4(waterColor, depthSoftenAlpha);
            }
            ENDHLSL
        }

        // =============================================
        //  PASS 2 -- DEPTH ONLY
        // =============================================
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex   DepthVert
            #pragma fragment DepthFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "SolWaterWaves.hlsl"

            // Global motion (must match forward pass)
            float4 _Sol_WindDirection;
            float  _Sol_WindStrength;
            float  _Sol_GlobalWaveSpeedMul;
            float  _Sol_WaveTime;
            float4 _Sol_WaterDynamics;
            float4 _Sol_WaveFadeCenter;

            CBUFFER_START(UnityPerMaterial)
                half4  _ShallowColor;
                half4  _DeepColor;
                half4  _HorizonColor;
                half4  _FresnelColor;
                half4  _NightShallowColor;
                half4  _NightDeepColor;
                half4  _NightHorizonColor;

                float  _WaveAmplitude;
                float  _WaveFrequency;
                float  _WaveSpeed;
                float4 _Wave1Direction;
                float4 _Wave2Direction;
                float  _Wave2Scale;
                float  _WaveColorInfluence;
                float  _WaveSteepness;
                float  _WaveDetailScale;
                float  _WaveLODFadeDistance;

                float  _NormalTiling;
                float  _NormalDetailTiling;
                float  _NormalStrength;
                float  _NormalDetailStrength;
                float4 _NormalScrollDir1;
                float4 _NormalScrollDir2;
                float  _NormalScrollSpeed;
                float  _NormalScrollSpeed2;
                float4 _NormalDetailScrollDir;

                float  _RefractionStrength;
                float  _RefractionDepthFade;
                float  _RefractionDistanceFade;
                float  _RefractionNearSurfaceFade;
                float  _RefractionEdgeFadeDistance;

                float  _Roughness;
                float  _SpecIntensity;
                float  _SparkleSharpness;
                float  _SparkleThreshold;
                float  _SparkleIntensity;

                half4  _FoamColor;
                float  _FoamTiling;
                float  _FoamBrightness;
                float  _FoamShorePower;
                float  _FoamShoreWidth;

                float  _CausticsTiling;
                float  _CausticsSpeed;
                float  _CausticsIntensity;
                float  _CausticsDepth;

                float  _DepthSofteningDistance;
                float  _DepthColorDistance;
                float  _DepthHorizonDistance;
                float  _HorizonFalloff;
                float  _NormalDetailDistanceFade;

                float  _FresnelPower;
                float  _FresnelBias;
                float  _FresnelSplitStrength;
                float  _ReflectionStr;
                float  _UnderwaterShadowStrength;
                float  _UnderwaterShadowFadeDistance;

                float  _DampeningFactor;

                float  _ToDReflectionInfluence;
                float  _ToDColorInfluence;
                float  _ToDSpecInfluence;

                half4  _SSSColor;
                float  _SSSIntensity;
                float  _SSSPower;
                float  _CrestFoamThreshold;
                float  _CrestFoamSharpness;
                float  _SwellAmplitude;
                float  _SwellSpeed;
                float4 _SwellDirection;
                float  _SunStreakPower;
                float  _SunStreakIntensity;
                float  _DepthBanding;
                float  _HorizonSheenStr;

                float  _SSRMaxDistance;
                float  _SSRSteps;
                float  _SSRThickness;
                float  _SSREdgeFade;
                float  _SSRIntensity;
                float  _SSRDebugView;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings DepthVert(Attributes IN)
            {
                Varyings OUT;
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);

                float2 fadeCenter = _Sol_WaveFadeCenter.w > 0.5
                    ? _Sol_WaveFadeCenter.xz
                    : GetCameraPositionWS().xz;
                float3 lodFades = SolComputeWaveFades(
                    posWS.xz, fadeCenter, _WaveLODFadeDistance);
                float3 waveDisp, waveNormal;
                SolEvaluateWaves(
                    posWS.xz, _Sol_WaveTime,
                    _WaveAmplitude, _WaveFrequency, _WaveSpeed,
                    _Wave1Direction.xz, _Wave2Direction.xz, _Wave2Scale,
                    _WaveSteepness, _WaveDetailScale,
                    _SwellAmplitude, _SwellSpeed, _SwellDirection.xz,
                    _Sol_WindDirection.xz, _Sol_WindStrength,
                    _Sol_WaterDynamics.x, _Sol_WaterDynamics.y, _Sol_WaterDynamics.w,
                    lodFades,
                    waveDisp, waveNormal);

                posWS += waveDisp;

                OUT.positionCS = TransformWorldToHClip(posWS);
                return OUT;
            }

            half4 DepthFrag(Varyings IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
