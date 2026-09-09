Shader "Hidden/Sol/Tests/CelestialAtmosphere"
{
    SubShader
    {
        Pass
        {
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "../../Earth-Sky-Water/Water/Shaders/SolAtmosphere.hlsl"
            float4 _TestView;
            float _TestShadow;
            struct Attributes { float4 positionOS : POSITION; };
            float4 Vert(Attributes input) : SV_POSITION { return TransformObjectToHClip(input.positionOS.xyz); }
            float4 Frag() : SV_Target
            {
                return float4(SolAtmosphereLighting(normalize(_TestView.xyz), _TestShadow), 1);
            }
            ENDHLSL
        }
    }
}
