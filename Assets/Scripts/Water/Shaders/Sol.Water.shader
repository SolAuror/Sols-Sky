// Sol.Water - Stylised water shader for URP (Unity 6 / URP 17)
//
// Visual target: Witcher 3 / Fable 2 style - clean, customisable.
//
// Features:
//   - Non-linear shaped sine waves + low-frequency ocean swell
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
        _FoamBrightness ("Brightness",    Range(0.1, 10)) = 3
        _FoamShoreWidth ("Shore Width",   Range(0.01, 5)) = 0.5
        _FoamShorePower     ("Shore Falloff",    Range(0.5, 10)) = 3
        _CrestFoamThreshold ("Whitecap Threshold", Range(-1, 2)) = 0.3
        _CrestFoamSharpness ("Whitecap Sharpness", Range(0.1, 20)) = 4

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
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex   WaterVert
            #pragma fragment WaterFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

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
            float  _Sol_GlobalWaterLevel;       // gameplay water level

            // Interactive ripples (set by WaterRippleManager)
            // Each float4: xy = world XZ position, z = birth time, w = strength
            #define SOL_MAX_RIPPLES 32
            float4 _Sol_Ripples[SOL_MAX_RIPPLES];
            int    _Sol_RippleCount;
            float  _Sol_RippleSpeed;       // ring expansion speed (world units/sec)
            float  _Sol_RippleFrequency;   // ring density
            float  _Sol_RippleLifetime;    // seconds before full fade

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

                // Tiled gameplay water must keep full geometric waves at tile borders so
                // shader displacement and C# surface sampling agree across edges.
                float dampening = 1.0;

                // Apply global wind influence: bias wave directions toward wind
                float windStr = _Sol_WindStrength;
                float3 windDir = float3(_Sol_WindDirection.x, 0, _Sol_WindDirection.z);
                float3 dir1Raw = float3(_Wave1Direction.x, 0, _Wave1Direction.z) + windDir * windStr * 0.3;
                float3 dir2Raw = float3(_Wave2Direction.x, 0, _Wave2Direction.z) + windDir * windStr * 0.15;
                float3 dir1 = dot(dir1Raw.xz, dir1Raw.xz) > 1e-6 ? normalize(dir1Raw) : float3(1, 0, 0);
                float3 dir2 = dot(dir2Raw.xz, dir2Raw.xz) > 1e-6 ? normalize(dir2Raw) : float3(0.496, 0, 0.868);

                float phase1 = dot(dir1.xz, posWS.xz) * _WaveFrequency + _Sol_WaveTime * _WaveSpeed;
                float phase2 = dot(dir2.xz, posWS.xz) * _WaveFrequency * 1.3 + _Sol_WaveTime * _WaveSpeed * 0.8;

                // Wind boosts wave amplitude
                float amp = _WaveAmplitude * (1.0 + windStr * 0.2);

                // -- Non-linear wave shaping (sharper crests, flatter troughs) --
                float raw1 = sin(phase1);
                float wave1 = sign(raw1) * pow(abs(raw1), 1.5) * amp;
                float raw2 = sin(phase2);
                float wave2 = (pow(raw2 * 0.5 + 0.5, 2.0) * 2.0 - 1.0) * amp * _Wave2Scale;

                float swellPhase = dot(posWS.xz, _SwellDirection.xz) + _Sol_WaveTime * _SwellSpeed;
                float swell = sin(swellPhase) * _SwellAmplitude;

                float totalWave = (wave1 + wave2) * dampening + swell;
                posWS.y += totalWave;

                // -- Analytical normal from shaped wave derivatives --
                float dShape1 = 1.5 * sqrt(abs(raw1) + 1e-6) * cos(phase1);
                float dWave1  = dShape1 * amp * _WaveFrequency * dampening;
                float dShape2 = 2.0 * (raw2 * 0.5 + 0.5) * cos(phase2);
                float dWave2  = dShape2 * amp * _Wave2Scale * _WaveFrequency * 1.3 * dampening;
                float dSwell = cos(swellPhase) * _SwellAmplitude;

                float dX = dWave1 * dir1.x + dWave2 * dir2.x + dSwell * _SwellDirection.x;
                float dZ = dWave1 * dir1.z + dWave2 * dir2.z + dSwell * _SwellDirection.z;

                float3 waveNormal  = normalize(float3(-dX, 1.0, -dZ));
                float3 waveTangent = normalize(float3(1.0, dX, 0.0));
                float3 waveBinorm  = normalize(cross(waveNormal, waveTangent));

                OUT.positionWS   = posWS;
                OUT.positionCS   = TransformWorldToHClip(posWS);
                OUT.screenPos    = ComputeScreenPos(OUT.positionCS);
                OUT.normalWS     = waveNormal;
                OUT.tangentWS    = waveTangent;
                OUT.binormalWS   = waveBinorm;
                OUT.uv           = float4(IN.uv, posWS.xz);
                OUT.fogAndDist.x = ComputeFogFactor(OUT.positionCS.z);
                OUT.fogAndDist.y = length(posWS - GetCameraPositionWS());
                OUT.waveHeight   = totalWave;

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

            // =========================================
            //  FRAGMENT SHADER
            // =========================================
            half4 WaterFrag(Varyings IN) : SV_Target
            {
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
                float rainNormalBoost = 1.0 + _Sol_RainIntensity * 0.8;
                float scrollSpd  = _NormalScrollSpeed;
                float scrollSpd2 = _NormalScrollSpeed2;
                float2 nmUV1 = baseUV + _Time.y * _NormalScrollDir1.xy * scrollSpd;
                float2 nmUV2 = baseUV + _Time.y * _NormalScrollDir2.xy * scrollSpd2;

                float3 nTS1 = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap1, sampler_NormalMap1, nmUV1), _NormalStrength);
                float3 nTS2 = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_NormalMap2, sampler_NormalMap2, nmUV2), _NormalStrength);

                // -- Micro-detail normal (fades with distance) --
                float detailFade = 1.0 - saturate(IN.fogAndDist.y / _NormalDetailDistanceFade);
                float2 detailUV  = worldUV * _NormalDetailTiling + _Time.y * _NormalDetailScrollDir.xy;
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
                        float  age   = _Time.y - _Sol_Ripples[r].z;

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

                // -- View / Light setup --
                float3 viewDirWS = normalize(GetCameraPositionWS() - IN.positionWS);
                float2 screenUV  = IN.screenPos.xy / IN.screenPos.w;

                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light  mainLight   = GetMainLight(shadowCoord);
                float3 lightDir    = normalize(mainLight.direction);
                float  lightAtten  = mainLight.distanceAttenuation;
                float3 lightColor  = mainLight.color * lightAtten;

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
                lightColor = mainLight.color * lightAtten;

                // Precompute roughness terms once - used by main specular and additional lights.
                // Rain increases roughness, reducing specular sharpness.
                float  rainRoughBoost = _Roughness + _Sol_RainIntensity * 0.15;
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
                    _Sol_SunColor.rgb * 0.5 + 0.5, _ToDReflectionInfluence * dayFactor);
                // Rain dampens reflection clarity
                float rainReflDampen = 1.0 - _Sol_RainIntensity * 0.3;
                envColor *= _FresnelColor.rgb * todReflTint * _ReflectionStr * rainReflDampen;

                // Fallback when probe is absent or dark
                half  envLuma      = dot(envColor, half3(0.2126, 0.7152, 0.0722));
                half3 fallbackRefl = horizonBlend * _FresnelColor.rgb
                                    * todReflTint * _ReflectionStr;
                envColor = lerp(fallbackRefl, envColor, smoothstep(0.02, 0.15, envLuma));

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
                    _Sol_SunColor.rgb * 0.7 + 0.3, _ToDColorInfluence * dayFactor);
                refractionColor *= todRefrTint;

                // =====================================
                //  CAUSTICS
                // =====================================
                {
                    float causticsK    = 3.0 / max(_CausticsDepth, 0.01);
                    float causticsAtten = exp(-depthDiff * causticsK);

                    float2 causticsUV1 = worldUV * _CausticsTiling
                        + _Time.y * _CausticsSpeed * float2(1, 0.7);
                    float2 causticsUV2 = worldUV * _CausticsTiling * 0.8
                        - _Time.y * _CausticsSpeed * float2(0.6, 1);

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
                float waveNorm      = IN.waveHeight / (_WaveAmplitude * (1.0 + _Wave2Scale) + 0.001);
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
                half3  sunStreak  = pow(sunAlign, _SunStreakPower) * _Sol_SunColor.rgb
                                  * _SunStreakIntensity * lightAtten * dayFactor;
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
                    float2 foamUV   = worldUV * _FoamTiling + _Time.y * float2(0.01, -0.01) * _FoamTiling;
                    half3  foamTex  = SAMPLE_TEXTURE2D(_FoamMap, sampler_FoamMap, foamUV).rgb
                                    * _FoamColor.rgb;
                    float  foamNoise = SAMPLE_TEXTURE2D(
                        _FoamMap, sampler_FoamMap, worldUV * _FoamTiling * 0.7).r;
                    half   foamToD   = lerp(0.5, 1.0, dayFactor) * eclipseAtten;

                    float shoreFoam = (depthDiff > 0.0)
                        ? pow(saturate(1.0 - depthDiff / _FoamShoreWidth), _FoamShorePower)
                        : 0.0;
                    shoreFoam *= foamNoise;

                    // -- Foam animation pulse --
                    float foamPulse = sin(_Time.y * 2.0 + worldUV.x * 3.0) * 0.5 + 0.5;
                    shoreFoam *= lerp(0.6, 1.0, foamPulse);

                    waterColor = lerp(waterColor, foamTex * _FoamBrightness * foamToD,
                        saturate(shoreFoam) * depthSoftenAlpha);

                    // -- Wave crest whitecaps (steepness + height) --
                    float steepness = 1.0 - saturate(dot(finalNormal, float3(0, 1, 0)));
                    float crestFoam = saturate((steepness + IN.waveHeight * 0.5 - _CrestFoamThreshold) * _CrestFoamSharpness)
                                    * foamNoise;
                    waterColor = lerp(waterColor, foamTex * _FoamBrightness * foamToD, crestFoam);
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
                //  FOG
                // =====================================
                waterColor = MixFog(waterColor, IN.fogAndDist.x);

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

            // Global motion (must match forward pass)
            float4 _Sol_WindDirection;
            float  _Sol_WindStrength;
            float  _Sol_GlobalWaveSpeedMul;
            float  _Sol_WaveTime;

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

                float windStr = _Sol_WindStrength;
                float3 windDir = float3(_Sol_WindDirection.x, 0, _Sol_WindDirection.z);
                float3 dir1Raw = float3(_Wave1Direction.x, 0, _Wave1Direction.z) + windDir * windStr * 0.3;
                float3 dir2Raw = float3(_Wave2Direction.x, 0, _Wave2Direction.z) + windDir * windStr * 0.15;
                float3 dir1 = dot(dir1Raw.xz, dir1Raw.xz) > 1e-6 ? normalize(dir1Raw) : float3(1, 0, 0);
                float3 dir2 = dot(dir2Raw.xz, dir2Raw.xz) > 1e-6 ? normalize(dir2Raw) : float3(0.496, 0, 0.868);
                float  phase1 = dot(dir1.xz, posWS.xz) * _WaveFrequency + _Sol_WaveTime * _WaveSpeed;
                float  phase2 = dot(dir2.xz, posWS.xz) * _WaveFrequency * 1.3 + _Sol_WaveTime * _WaveSpeed * 0.8;
                float amp = _WaveAmplitude * (1.0 + windStr * 0.2);
                float raw1 = sin(phase1);
                float raw2 = sin(phase2);
                posWS.y += sign(raw1) * pow(abs(raw1), 1.5) * amp
                         + (pow(raw2 * 0.5 + 0.5, 2.0) * 2.0 - 1.0) * amp * _Wave2Scale;
                posWS.y += sin(dot(posWS.xz, _SwellDirection.xz) + _Sol_WaveTime * _SwellSpeed)
                         * _SwellAmplitude;

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
