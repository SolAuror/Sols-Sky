Shader "Hidden/Sol/Landscape/Phase 0 Spike"
{
    Properties
    {
        [HideInInspector] _Control("Stock Control", 2D) = "red" {}
        [HideInInspector] _MainTex("Base Map", 2D) = "grey" {}
        [HideInInspector] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        [HideInInspector] _TerrainHolesTexture("Terrain Holes", 2D) = "white" {}

        [NoScaleOffset] _SolPhase0_Control0("Manually Bound Control 0", 2D) = "red" {}
        [NoScaleOffset] _SolPhase0_Control1("Manually Bound Control 1", 2D) = "black" {}
        [NoScaleOffset] _SolPhase0_LayerArray("Diagnostic Layer Array", 2DArray) = "" {}

        [Enum(All Layer Blend,0,Dynamic Array Slice,1,Single Layer Weight,2,Dominant Weight Slice,3,Instancing Probe,4)]
        _SolPhase0_DebugMode("Debug Mode", Float) = 0
        [IntRange] _SolPhase0_ArraySlice("Dynamic Array Slice", Range(0, 5)) = 0
        [IntRange] _SolPhase0_DebugLayer("Single Layer", Range(0, 5)) = 0
    }

    HLSLINCLUDE
    #pragma multi_compile_fragment __ _ALPHATEST_ON
    ENDHLSL

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry-100"
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Unlit"
            "IgnoreProjector" = "False"
            "TerrainCompatible" = "True"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Phase0TerrainVertex
            #pragma fragment Phase0TerrainFragment
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap

            #include "Packages/com.unity.render-pipelines.universal/Shaders/Terrain/TerrainLitInput.hlsl"

            TEXTURE2D(_SolPhase0_Control0);
            SAMPLER(sampler_SolPhase0_Control0);
            TEXTURE2D(_SolPhase0_Control1);
            SAMPLER(sampler_SolPhase0_Control1);
            TEXTURE2D_ARRAY(_SolPhase0_LayerArray);
            SAMPLER(sampler_SolPhase0_LayerArray);

            float _SolPhase0_DebugMode;
            float _SolPhase0_ArraySlice;
            float _SolPhase0_DebugLayer;

            struct Phase0Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Phase0Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 terrainUV : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Phase0Varyings Phase0TerrainVertex(Phase0Attributes input)
            {
                Phase0Varyings output = (Phase0Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                TerrainInstancing(input.positionOS, input.normalOS, input.texcoord);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.terrainUV = input.texcoord;
                return output;
            }

            half Phase0LayerWeight(half4 control0, half4 control1, int layerIndex)
            {
                if (layerIndex == 0) return control0.r;
                if (layerIndex == 1) return control0.g;
                if (layerIndex == 2) return control0.b;
                if (layerIndex == 3) return control0.a;
                if (layerIndex == 4) return control1.r;
                return control1.g;
            }

            half4 Phase0TerrainFragment(Phase0Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                #ifdef _ALPHATEST_ON
                ClipHoles(input.terrainUV);
                #endif

                half4 control0 = SAMPLE_TEXTURE2D(
                    _SolPhase0_Control0,
                    sampler_SolPhase0_Control0,
                    input.terrainUV);
                half4 control1 = SAMPLE_TEXTURE2D(
                    _SolPhase0_Control1,
                    sampler_SolPhase0_Control1,
                    input.terrainUV);

                if (_SolPhase0_DebugMode > 3.5)
                {
                    #ifdef UNITY_INSTANCING_ENABLED
                    return half4(0.0h, 1.0h, 0.0h, 1.0h);
                    #else
                    return half4(1.0h, 0.0h, 0.0h, 1.0h);
                    #endif
                }

                if (_SolPhase0_DebugMode > 2.5)
                {
                    int dominantLayer = 0;
                    half dominantWeight = control0.r;
                    if (control0.g > dominantWeight) { dominantLayer = 1; dominantWeight = control0.g; }
                    if (control0.b > dominantWeight) { dominantLayer = 2; dominantWeight = control0.b; }
                    if (control0.a > dominantWeight) { dominantLayer = 3; dominantWeight = control0.a; }
                    if (control1.r > dominantWeight) { dominantLayer = 4; dominantWeight = control1.r; }
                    if (control1.g > dominantWeight) { dominantLayer = 5; }

                    return SAMPLE_TEXTURE2D_ARRAY(
                        _SolPhase0_LayerArray,
                        sampler_SolPhase0_LayerArray,
                        input.terrainUV,
                        dominantLayer);
                }

                if (_SolPhase0_DebugMode > 1.5)
                {
                    int debugLayer = clamp((int)round(_SolPhase0_DebugLayer), 0, 5);
                    half weight = Phase0LayerWeight(control0, control1, debugLayer);
                    return half4(weight.xxx, 1.0h);
                }

                if (_SolPhase0_DebugMode > 0.5)
                {
                    int dynamicSlice = clamp((int)round(_SolPhase0_ArraySlice), 0, 5);
                    return SAMPLE_TEXTURE2D_ARRAY(
                        _SolPhase0_LayerArray,
                        sampler_SolPhase0_LayerArray,
                        input.terrainUV,
                        dynamicSlice);
                }

                half weights[6] =
                {
                    control0.r,
                    control0.g,
                    control0.b,
                    control0.a,
                    control1.r,
                    control1.g
                };

                half3 diagnosticColor = 0.0h;
                half totalWeight = 0.0h;
                UNITY_UNROLL
                for (int layerIndex = 0; layerIndex < 6; ++layerIndex)
                {
                    half3 layerColor = SAMPLE_TEXTURE2D_ARRAY(
                        _SolPhase0_LayerArray,
                        sampler_SolPhase0_LayerArray,
                        input.terrainUV,
                        layerIndex).rgb;
                    diagnosticColor += layerColor * weights[layerIndex];
                    totalWeight += weights[layerIndex];
                }

                diagnosticColor /= max(totalWeight, 0.0001h);
                return half4(diagnosticColor, 1.0h);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
