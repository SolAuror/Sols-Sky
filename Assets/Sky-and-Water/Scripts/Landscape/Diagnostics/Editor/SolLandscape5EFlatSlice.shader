Shader "Hidden/Sol/Landscape 5E Flat Slice"
{
    Properties
    {
        [HideInInspector] _TerrainHolesTexture("Holes Map", 2D) = "white" {}
        [HideInInspector] _Sol5EFlatSlices("Flat Slice Control", 2DArray) = "" {}
        [HideInInspector] _Sol5EInjectBleed("Injected Neighbour Contribution", Float) = 0
        [HideInInspector] _Sol5EForcedSlice("Forced Slice (-1 Uses Weight Winner)", Float) = -1
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Geometry-100"
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "TerrainCompatible" = "True"
        }

        Pass
        {
            Name "FlatSliceControl"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Sol5EVertex
            #pragma fragment Sol5EFragment
            #pragma multi_compile_fragment _ _ALPHATEST_ON
            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap

            #include "Assets/Sky-and-Water/Shaders/Terrain/SolTerrainArrayInput.hlsl"
            #include "Assets/Sky-and-Water/Shaders/Terrain/SolTerrainWetness.hlsl"
            #define SOL_LANDSCAPE_LAYER_COUNT 6
            #include "Assets/Sky-and-Water/Shaders/Terrain/SolTerrainAutoMaterial.hlsl"

            TEXTURE2D_ARRAY(_Sol5EFlatSlices);
            SAMPLER(sampler_Sol5EFlatSlices);
            float _Sol5EInjectBleed;
            float _Sol5EForcedSlice;

            struct Sol5EAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 terrainUV : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Sol5EVaryings
            {
                float2 terrainUV : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Sol5EVaryings Sol5EVertex(Sol5EAttributes input)
            {
                Sol5EVaryings output = (Sol5EVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                SolTerrainInstancing(input.positionOS, input.normalOS, input.terrainUV);
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.terrainUV = input.terrainUV;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionCS = positionInputs.positionCS;
                return output;
            }

            float Sol5ERawWeight(half4 control0, half2 control1, int layerIndex)
            {
                if (layerIndex == 0) return control0.r;
                if (layerIndex == 1) return control0.g;
                if (layerIndex == 2) return control0.b;
                if (layerIndex == 3) return control0.a;
                if (layerIndex == 4) return control1.r;
                return control1.g;
            }

            float2 Sol5ELayerUV(float2 terrainUV, int layerIndex)
            {
                float4 layerST = _Sol_LandscapeLayerST[layerIndex];
                return terrainUV * layerST.xy + layerST.zw;
            }

            float4 Sol5EFragment(Sol5EVaryings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                #ifdef _ALPHATEST_ON
                    SolClipTerrainHoles(input.terrainUV);
                #endif

                float2 splatUV =
                    (input.terrainUV * (_Sol_LandscapeControlTexelSize.zw - 1.0f) + 0.5f)
                    * _Sol_LandscapeControlTexelSize.xy;
                half4 control0 = SAMPLE_TEXTURE2D(
                    _Sol_LandscapeControl0,
                    sampler_Sol_LandscapeControl0,
                    splatUV);
                half2 control1 = SAMPLE_TEXTURE2D(
                    _Sol_LandscapeControl1,
                    sampler_Sol_LandscapeControl1,
                    splatUV).rg;

                float resolvedWeights[SOL_LANDSCAPE_LAYER_COUNT];
                [unroll]
                for (int layer = 0; layer < SOL_LANDSCAPE_LAYER_COUNT; ++layer)
                    resolvedWeights[layer] = Sol5ERawWeight(control0, control1, layer);

                half3 geometricNormalWS = normalize(input.normalWS);
                #ifdef ENABLE_TERRAIN_PERPIXEL_NORMAL
                    float2 normalCoords =
                        (input.terrainUV / _TerrainHeightmapRecipSize.zw + 0.5f)
                        * _TerrainHeightmapRecipSize.xy;
                    half3 geometricNormalOS = normalize(SAMPLE_TEXTURE2D(
                        _TerrainNormalmapTexture,
                        sampler_TerrainNormalmapTexture,
                        normalCoords).rgb * 2.0h - 1.0h);
                    geometricNormalWS = TransformObjectToWorldNormal(geometricNormalOS);
                #endif

                float2 manualAuto;
                SolResolveLandscapeAutoMaterial(
                    resolvedWeights,
                    input.positionWS,
                    geometricNormalWS,
                    manualAuto);

                int winner = 0;
                float winnerWeight = resolvedWeights[0];
                float totalWeight = resolvedWeights[0];
                [unroll]
                for (int layer = 1; layer < SOL_LANDSCAPE_LAYER_COUNT; ++layer)
                {
                    totalWeight += resolvedWeights[layer];
                    if (resolvedWeights[layer] > winnerWeight)
                    {
                        winner = layer;
                        winnerWeight = resolvedWeights[layer];
                    }
                }

                bool fullWeight = winnerWeight >= 0.999999f
                    && totalWeight - winnerWeight <= 0.000001f;
                if (!fullWeight)
                    return 0.0f;

                int sampleSlice = winner;
                if (_Sol5EForcedSlice >= 0.0f)
                    sampleSlice = min(max((int)round(_Sol5EForcedSlice), 0), SOL_LANDSCAPE_LAYER_COUNT - 1);

                float4 sampled = SAMPLE_TEXTURE2D_ARRAY(
                    _Sol5EFlatSlices,
                    sampler_Sol5EFlatSlices,
                    Sol5ELayerUV(input.terrainUV, sampleSlice),
                    sampleSlice);
                if (_Sol5EInjectBleed > 0.0f)
                {
                    int neighbour = (sampleSlice + 1) % SOL_LANDSCAPE_LAYER_COUNT;
                    float4 neighbourSample = SAMPLE_TEXTURE2D_ARRAY(
                        _Sol5EFlatSlices,
                        sampler_Sol5EFlatSlices,
                        Sol5ELayerUV(input.terrainUV, neighbour),
                        neighbour);
                    sampled.rgb = lerp(sampled.rgb, neighbourSample.rgb, saturate(_Sol5EInjectBleed));
                }

                // Alpha encodes sampled slice+1, leaving zero as the non-full-weight/background mask.
                return float4(sampled.rgb, (sampleSlice + 1.0f) / 8.0f);
            }
            ENDHLSL
        }
    }
}
