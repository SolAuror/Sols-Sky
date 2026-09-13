Shader "Hidden/Sol/Landscape Sculpt"
{
    Properties { _MainTex("Source", 2D) = "black" {} _BrushTex("Brush", 2D) = "white" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Packages/com.unity.terrain-tools/Shaders/TerrainTools.hlsl"
            sampler2D _MainTex, _BrushTex;
            float4 _MainTex_TexelSize, _Sculpt;
            float4 frag(v2f_img i) : SV_Target
            {
                float2 uv = PaintContextUVToBrushUV(i.uv);
                float mask = (1-smoothstep(_Sculpt.w,1,length((uv-.5)*2))) * (all(saturate(uv)==uv)?1:0);
                float stamp = tex2D(_BrushTex,uv).r;
                float h = UnpackHeightmap(tex2D(_MainTex,i.uv));
                float amount = mask * _Sculpt.x;
                if (_Sculpt.z < .5) h += amount * stamp;
                else if (_Sculpt.z < 1.5)
                {
                    float smooth = (UnpackHeightmap(tex2D(_MainTex,i.uv+float2(_MainTex_TexelSize.x,0)))+UnpackHeightmap(tex2D(_MainTex,i.uv-float2(_MainTex_TexelSize.x,0)))+UnpackHeightmap(tex2D(_MainTex,i.uv+float2(0,_MainTex_TexelSize.y)))+UnpackHeightmap(tex2D(_MainTex,i.uv-float2(0,_MainTex_TexelSize.y))))*.25;
                    h=lerp(h,smooth,saturate(amount*stamp));
                }
                else h=lerp(h,_Sculpt.y+(_Sculpt.z>2.5 ? stamp*_Sculpt.x : 0),saturate(mask*abs(_Sculpt.x)*(_Sculpt.z>2.5?100:1)));
                return PackHeightmap(clamp(h,0,32766.0/65535.0));
            }
            ENDHLSL
        }
    }
}
