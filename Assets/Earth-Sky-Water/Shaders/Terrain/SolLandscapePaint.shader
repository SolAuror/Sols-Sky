Shader "Hidden/Sol/Landscape Paint"
{
    Properties { _MainTex("Source", 2D) = "black" {} _Stamp("Brush", 2D) = "white" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _Stamp;
            float4 _Brush, _Settings;
            float _SelectedMap, _Resolution, _Rotation;
            float4 frag(v2f_img input) : SV_Target
            {
                float2 terrainUV = (input.uv * _Resolution - .5) / (_Resolution - 1);
                float2 delta = (terrainUV - _Brush.xy) / _Brush.zw;
                float2 rotated = float2(cos(_Rotation)*delta.x - sin(_Rotation)*delta.y, sin(_Rotation)*delta.x + cos(_Rotation)*delta.y);
                float amount = (1 - smoothstep(_Settings.y, 1, length(delta))) * _Settings.x * tex2D(_Stamp, rotated * .5 + .5).r;
                float4 value = tex2D(_MainTex, input.uv);
                float4 selected = float4(_Settings.w == 0, _Settings.w == 1, _Settings.w == 2, _Settings.w == 3) * _SelectedMap;
                if (_Settings.z < .5) return value * (1 - amount) + selected * amount;
                if (_Settings.z < 1.5) return value * (1 - amount);
                if (_Settings.z < 2.5) return lerp(value, 1, selected * amount);
                return value * (1 - selected * amount);
            }
            ENDHLSL
        }
    }
}
