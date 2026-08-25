Shader "Hidden/Sol/Terrain/Array Basemap Gen"
{
    Properties
    {
        [HideInInspector] _DstBlend("DstBlend", Float) = 0.0
    }

    SubShader
    {
        HLSLINCLUDE
        #pragma target 4.5

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
            half totalWeight = dot(control0, 1.0h) + control1.x + control1.y;
            half inverseWeight = rcp(max(totalWeight, HALF_MIN));

            half3 albedo = 0.0h;
            half smoothness = 0.0h;
            [unroll]
            for (int layerIndex = 0; layerIndex < SOL_LANDSCAPE_BASEMAP_LAYER_COUNT; ++layerIndex)
            {
                half weight = SolBasemapRawWeight(control0, control1, layerIndex) * inverseWeight;
                float4 layerST = _Sol_LandscapeLayerST[layerIndex];
                float2 layerUV = terrainUV * layerST.xy + layerST.zw;
                half4 cs = SAMPLE_TEXTURE2D_ARRAY(
                    _Sol_LandscapeCS,
                    sampler_Sol_LandscapeCS,
                    layerUV,
                    layerIndex);
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
