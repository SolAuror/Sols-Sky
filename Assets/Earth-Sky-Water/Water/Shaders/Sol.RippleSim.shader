// Sol.RippleSim - GPU interactive ripple simulation step
//
// 2D damped wave equation, ping-ponged between two RG render textures by
// WaterRippleManager:  R = current height, G = previous height.
//
// Rendered with the URP Blitter API (fullscreen triangle from vertex IDs,
// Blit.hlsl). This is deliberately matrix-free: the sim steps run from
// Update(), outside any camera render, where view/projection constants
// are stale - a matrix-based fullscreen quad can rasterize nowhere there.
//
// The simulation window follows a focus point in the world; _Scroll shifts
// the previous state so ripples stay world-anchored while the window moves.
// Texels scrolled in from outside the window start as calm water.
//
// Disturbances:
//   - _Injections: interactive splats queued via WaterRippleManager.Emit()
//   - _Rain:       procedural random droplets, driven by rain intensity
//
// Pass 1 is a debug visualization (signed height around mid-gray).

Shader "Hidden/Sol/RippleSim"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        // =============================================
        //  PASS 0 -- SIMULATION STEP
        // =============================================
        Pass
        {
            Name "SimStep"
            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float4 _SimParams;       // x = c^2 (propagation, <= 0.49), y = velocity damping,
                                     // z = texel size (1/resolution)
            float4 _Scroll;          // xy = uv offset of the window since the last step
            float4 _Injections[16];  // xy = uv position, z = radius (uv), w = height delta
            int    _InjectionCount;
            float4 _Rain;            // x = droplet count, y = strength, z = seed, w = radius (uv)

            float Hash(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            // Sample the previous state; anything outside the window is calm.
            float2 SampleState(float2 uv)
            {
                if (any(uv < 0.0) || any(uv > 1.0))
                    return float2(0.0, 0.0);
                return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0).rg;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                // Where this texel's world position lived in the previous step.
                float2 uv = input.texcoord + _Scroll.xy;
                float texel = _SimParams.z;

                float2 c = SampleState(uv);
                float l = SampleState(uv - float2(texel, 0)).r;
                float r = SampleState(uv + float2(texel, 0)).r;
                float d = SampleState(uv - float2(0, texel)).r;
                float u = SampleState(uv + float2(0, texel)).r;

                // Damped wave equation (velocity form).
                float lap  = l + r + u + d - 4.0 * c.x;
                float newH = c.x + (c.x - c.y) * _SimParams.y + lap * _SimParams.x;

                // Interactive injections (already in current-window uv space).
                [loop]
                for (int i = 0; i < _InjectionCount; i++)
                {
                    float dist = distance(input.texcoord, _Injections[i].xy);
                    float m = saturate(1.0 - dist / max(_Injections[i].z, 1e-5));
                    newH += _Injections[i].w * m * m;
                }

                // Rain: procedural random dents each step.
                int drops = (int)_Rain.x;
                [loop]
                for (int k = 0; k < drops; k++)
                {
                    float2 p = float2(Hash(float2(_Rain.z, k * 1.37)),
                                      Hash(float2(k * 2.11, _Rain.z + 4.7)));
                    float dist = distance(input.texcoord, p);
                    float m = saturate(1.0 - dist / max(_Rain.w, 1e-5));
                    newH -= _Rain.y * m * m;
                }

                // Safety clamp so runaway feedback can never explode.
                newH = clamp(newH, -4.0, 4.0);

                return float4(newH, c.x, 0.0, 0.0);
            }
            ENDHLSL
        }

        // =============================================
        //  PASS 1 -- DEBUG VISUALIZATION
        // =============================================
        Pass
        {
            Name "DebugView"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment FragDebug

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _DebugGain;

            float4 FragDebug(Varyings input) : SV_Target
            {
                float h = SAMPLE_TEXTURE2D_X_LOD(
                    _BlitTexture, sampler_LinearClamp, input.texcoord, 0).r;
                float v = saturate(0.5 + h * _DebugGain);
                return float4(v, v, v, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
