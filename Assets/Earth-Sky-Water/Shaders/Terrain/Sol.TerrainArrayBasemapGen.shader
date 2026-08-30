Shader "Hidden/Sol/Terrain/Array Basemap Gen"
{
    Properties
    {
        [HideInInspector] _DstBlend("DstBlend", Float) = 0.0
        [Toggle(_SOL_LANDSCAPE_BLEND_HEIGHT)] _Sol_LandscapeBlendHeight("Height Blend", Float) = 1
    }

    SubShader
    {
        HLSLINCLUDE
        #pragma target 4.5
        #pragma shader_feature_local_fragment _SOL_LANDSCAPE_BLEND_HEIGHT

        #include "SolTerrainArrayInput.hlsl"

        #define SOL_LANDSCAPE_BASEMAP_LAYER_COUNT 6
        struct SolBasemapAttributes
        {
            float4 positionOS : POSITION;
            float2 terrainUV : TEXCOORD0;
        };

        struct SolBasemapVaryings
        {
            float2 terrainUV : TEXCOORD0;
            float4 positionCS : SV_POSITION;
        };

        SolBasemapVaryings SolBasemapVertex(SolBasemapAttributes input)
        {
            SolBasemapVaryings output = (SolBasemapVaryings)0;
            output.positionCS = TransformWorldToHClip(input.positionOS.xyz);
            output.terrainUV = input.terrainUV;
            return output;
        }

        half SolBasemapRawWeight(half4 control0, half2 control1, int layerIndex)
        {
            if (layerIndex == 0) return control0.r;
            if (layerIndex == 1) return control0.g;
            if (layerIndex == 2) return control0.b;
            if (layerIndex == 3) return control0.a;
            if (layerIndex == 4) return control1.r;
            if (layerIndex == 5) return control1.g;
            return 0.0h;
        }

        half4 SolGenerateLandscapeBasemap(float2 terrainUV)
        {
            float2 controlUV =
                (terrainUV * (_Sol_LandscapeControlTexelSize.zw - 1.0f) + 0.5f)
                * _Sol_LandscapeControlTexelSize.xy;
            half4 control0 = SAMPLE_TEXTURE2D(
                _Sol_LandscapeControl0,
                sampler_Sol_LandscapeControl0,
                controlUV);
            half2 control1 = SAMPLE_TEXTURE2D(
                _Sol_LandscapeControl1,
                sampler_Sol_LandscapeControl1,
                controlUV).rg;

            // Basemaps deliberately evaluate every layer. A layer-selection error here would bake into
            // all distant terrain. The production material's height-blend keyword is mirrored so
            // the distant basemap cannot silently disagree with the near terrain.
            float paintedWeights[SOL_LANDSCAPE_BASEMAP_LAYER_COUNT];
            float blendedWeights[SOL_LANDSCAPE_BASEMAP_LAYER_COUNT];
            float blendedWeightSum = 0.0f;
#ifdef _SOL_LANDSCAPE_BLEND_HEIGHT
            float splatHeights[SOL_LANDSCAPE_BASEMAP_LAYER_COUNT];
            float maxSplatHeight = -1.0f;
            [unroll]
            for (int heightLayerIndex = 0; heightLayerIndex < SOL_LANDSCAPE_BASEMAP_LAYER_COUNT; ++heightLayerIndex)
            {
                float paintedWeight = (float)SolBasemapRawWeight(control0, control1, heightLayerIndex);
                float4 layerST = _Sol_LandscapeLayerST[heightLayerIndex];
                float2 layerUV = terrainUV * layerST.xy + layerST.zw;
                half4 noh = SAMPLE_TEXTURE2D_ARRAY(
                    _Sol_LandscapeNOH,
                    sampler_Sol_LandscapeNOH,
                    layerUV,
                    heightLayerIndex);
                float splatHeight = (float)noh.a * paintedWeight;
                paintedWeights[heightLayerIndex] = paintedWeight;
                splatHeights[heightLayerIndex] = splatHeight;
                maxSplatHeight = max(maxSplatHeight, splatHeight);
            }

            float transition = max(_Sol_LandscapeHeightTransition, 1e-5f);
            [unroll]
            for (int blendLayerIndex = 0; blendLayerIndex < SOL_LANDSCAPE_BASEMAP_LAYER_COUNT; ++blendLayerIndex)
            {
                float weightedHeight = max(0.0f, splatHeights[blendLayerIndex] + transition - maxSplatHeight);
                weightedHeight = (weightedHeight + 1e-6f) * paintedWeights[blendLayerIndex];
                blendedWeights[blendLayerIndex] = weightedHeight;
                blendedWeightSum += weightedHeight;
            }
#else
            [unroll]
            for (int normalizeLayerIndex = 0; normalizeLayerIndex < SOL_LANDSCAPE_BASEMAP_LAYER_COUNT; ++normalizeLayerIndex)
            {
                float paintedWeight = (float)SolBasemapRawWeight(control0, control1, normalizeLayerIndex);
                paintedWeights[normalizeLayerIndex] = paintedWeight;
                blendedWeights[normalizeLayerIndex] = paintedWeight;
                blendedWeightSum += paintedWeight;
            }
#endif

            float inverseBlendedWeight = rcp(max(blendedWeightSum, 1e-6f));

            half3 albedo = 0.0h;
            half smoothness = 0.0h;
            [unroll]
            for (int sampleLayerIndex = 0; sampleLayerIndex < SOL_LANDSCAPE_BASEMAP_LAYER_COUNT; ++sampleLayerIndex)
            {
                half weight = (half)(blendedWeights[sampleLayerIndex] * inverseBlendedWeight);
                float4 layerST = _Sol_LandscapeLayerST[sampleLayerIndex];
                float2 layerUV = terrainUV * layerST.xy + layerST.zw;
                half4 cs = SAMPLE_TEXTURE2D_ARRAY(
                    _Sol_LandscapeCS,
                    sampler_Sol_LandscapeCS,
                    layerUV,
                    sampleLayerIndex);
                albedo += cs.rgb * weight;
                smoothness += cs.a * weight;
            }

            return half4(albedo, smoothness);
        }
        ENDHLSL

        Pass
        {
            Tags
            {
                "Name" = "_MainTex"
                "Format" = "ARGB32"
                "Size" = "1"
            }

            ZTest Always Cull Off ZWrite Off
            Blend One [_DstBlend]

            HLSLPROGRAM
            #pragma vertex SolBasemapVertex
            #pragma fragment SolBasemapMainFragment

            half4 SolBasemapMainFragment(SolBasemapVaryings input) : SV_Target
            {
                return SolGenerateLandscapeBasemap(input.terrainUV);
            }
            ENDHLSL
        }

        Pass
        {
            Tags
            {
                "Name" = "_MetallicTex"
                "Format" = "R8"
                "Size" = "1/4"
                "EmptyColor" = "FF000000"
            }

            ZTest Always Cull Off ZWrite Off
            Blend One [_DstBlend]

            HLSLPROGRAM
            #pragma vertex SolBasemapVertex
            #pragma fragment SolBasemapMetallicFragment

            half4 SolBasemapMetallicFragment(SolBasemapVaryings input) : SV_Target
            {
                return 0.0h;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
