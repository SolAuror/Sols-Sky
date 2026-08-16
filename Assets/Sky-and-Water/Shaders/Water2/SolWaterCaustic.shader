Shader "Hidden/Sol/Water2/Caustic"
{
    // Renders the caustic pattern the ocean surface actually projects, rather than
    // scrolling an authored texture. A dense grid is displaced by the FFT horizontal
    // displacement; each triangle then covers more or less ground than it did when
    // flat, and that area ratio is the photon density. Additive blending accumulates
    // the contribution of every patch that lands on a texel.
    //
    // Adapted from WaterFX's caustic prepass, with one deliberate divergence: the
    // donor renders each slice over its cascade domain but samples it back with a
    // different hardcoded table. Sol renders and samples over the same cascade
    // domain, so caustics stay locked to the surface being drawn.
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "SolWaterCaustic"
            Blend One One
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex CausticVertex
            #pragma fragment CausticFragment
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_ARRAY(_SolWaterCausticDisplacement);
            SAMPLER(sampler_SolWaterCausticDisplacement);

            // x: cascade index, y: cascade domain size in metres,
            // z: displacement gain, w: intensity scale
            float4 _SolWaterCausticParams;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                // Undisplaced and displaced grid position, in cascade UV space.
                float2 flatUV : TEXCOORD0;
                float2 displacedUV : TEXCOORD1;
            };

            Varyings CausticVertex(Attributes input)
            {
                Varyings output;
                // The grid arrives as a unit quad; treat uv as the cascade domain.
                float2 flatUV = input.uv;
                float slice = _SolWaterCausticParams.x;
                float3 displacement = SAMPLE_TEXTURE2D_ARRAY_LOD(
                    _SolWaterCausticDisplacement, sampler_SolWaterCausticDisplacement,
                    flatUV, slice, 0).xyz;

                // Only the horizontal displacement bends light. Vertical height moves
                // the surface but does not converge neighbouring rays on the floor.
                float2 domain = max(0.001, _SolWaterCausticParams.y);
                float2 displacedUV = flatUV
                    + displacement.xz * _SolWaterCausticParams.z / domain;

                output.flatUV = flatUV;
                output.displacedUV = displacedUV;
                // Render the displaced position into the caustic texture. Wrapping is
                // handled by the sampler, so the domain stays seamless.
                output.positionCS = float4(displacedUV * 2.0 - 1.0, 0.0, 1.0);
                #if UNITY_UV_STARTS_AT_TOP
                    output.positionCS.y = -output.positionCS.y;
                #endif
                return output;
            }

            float SolCausticArea(float2 gradientX, float2 gradientY)
            {
                return abs(gradientX.x * gradientY.y - gradientX.y * gradientY.x);
            }

            half4 CausticFragment(Varyings input) : SV_Target
            {
                // Area the patch covered before displacement versus after. Compression
                // (<1) concentrates light into a caustic cell; expansion darkens it.
                float flatArea = SolCausticArea(ddx(input.flatUV), ddy(input.flatUV));
                float displacedArea = SolCausticArea(
                    ddx(input.displacedUV), ddy(input.displacedUV));

                float density = flatArea / max(displacedArea, flatArea * 0.02 + 1e-9);
                // Additive accumulation across overlapping patches means each fragment
                // should contribute a fraction; the scale keeps the sum near unity for
                // an undisplaced surface.
                float intensity = density * _SolWaterCausticParams.w;
                return half4(intensity.xxx, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
