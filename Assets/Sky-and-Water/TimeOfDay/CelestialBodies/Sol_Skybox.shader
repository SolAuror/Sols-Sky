Shader "Sol/Skybox"
{
    Properties
    {
        [Header(Sky Gradient)]
        _ZenithColor  ("Zenith Color",  Color) = (0.7411765, 0.9333334, 0.9490197, 1)
        _HorizonColor ("Horizon Color", Color) = (0.9960785, 0.8000001, 0.5921569, 1)
        _NadirColor   ("Nadir Color",   Color) = (0.8196079, 0.6392157, 0.7137255, 1)
        _ZenithBlend  ("Zenith Blend",  Range(0, 10)) = 0.2
        _NadirBlend   ("Nadir Blend",   Range(0, 10)) = 0.1
        _HorizonBlend ("Horizon Blend", Range(0, 10)) = 1
        _HorizonWarmColor ("Horizon Warm Color (A = strength)", Color) = (1, 0.5, 0.2, 0)
        _HorizonWarmthFalloff ("Horizon Warmth Falloff", Range(0.5, 16)) = 4

        [Header(Stars)]
        _StarHeight    ("Star Height",    Float) = 100
        _StarIntensity ("Star Intensity", Float) = 50
        _StarPower     ("Star Power",     Float) = 30
        _StarRotation  ("Star Rotation (radians, driven by ToD)", Float) = 0
        _StarTwinkleSpeed  ("Star Twinkle Speed",  Range(0, 10)) = 2
        _StarTwinkleAmount ("Star Twinkle Amount", Range(0, 1))  = 0.35

        [Header(Milky Way)]
        _GalaxyIntensity ("Milky Way Intensity", Range(0, 3)) = 0.6
        _GalaxyColor1    ("Milky Way Core", Color) = (0.55, 0.45, 0.75, 1)
        _GalaxyColor2    ("Milky Way Edge", Color) = (0.15, 0.25, 0.5, 1)
        _NightFactor     ("Night Factor (driven by ToD)", Range(0, 1)) = 1

        [Header(Sun)]
        _SunDirection    ("Sun Direction",    Vector) = (0, 1, 0, 0)
        [HDR]
        _SunDiscColor    ("Sun Disc Color",   Color)  = (10, 8, 5, 1)
        _SunDiscSize     ("Sun Disc Size",    Range(0.990, 0.9999)) = 0.9995
        _SunGlowFalloff  ("Sun Glow Falloff", Float)  = 8
        _SunGlowIntensity("Sun Glow Intensity", Float) = 1.5
        [HDR]
        _CoronaColor     ("Eclipse Corona Color", Color) = (1.5, 0.4, 0.15, 1)

        [Header(Moon)]
        _MoonDirection   ("Moon Direction",   Vector) = (0, -1, 0, 0)
        _MoonColor       ("Moon Lit Color",   Color)  = (0.85, 0.9, 1, 1)
        _MoonDarkColor   ("Moon Dark Color",  Color)  = (0.01, 0.01, 0.02, 1)
        _MoonDiscSize    ("Moon Disc Size",   Range(0.990, 0.9999)) = 0.9993
        _MoonSharpness   ("Terminator Sharpness", Range(1, 10)) = 3

        [Header(Eclipses)]
        _SolarEclipseFactor ("Solar Eclipse", Range(0, 1)) = 0
        _LunarEclipseFactor ("Lunar Eclipse", Range(0, 1)) = 0
        _EclipseTint        ("Lunar Eclipse Tint", Color)  = (0.6, 0.15, 0.1, 1)

        [Header(Clouds)]
        _CloudScale       ("Cloud Scale",       Float)          = 5
        _CloudSpeed       ("Cloud Speed",       Float)          = 0.01
        _CloudTime        ("Cloud Time",        Float)          = 0
        _CloudWindDirection ("Cloud Wind Direction", Vector)    = (1, 0.3, 0, 0)
        _CloudErosion     ("Cloud Edge Erosion", Range(0, 1))   = 0.35
        _CloudCoverage    ("Cloud Coverage",    Range(0, 1))    = 0.5
        _CloudDensity     ("Cloud Density",     Range(0, 1))    = 0.8
        _CloudHeight      ("Cloud Height",      Range(0.01, 1)) = 0.15
        _CloudColor       ("Cloud Color",       Color)          = (1, 1, 1, 1)
        _CloudShadowColor ("Cloud Shadow",      Color)          = (0.4, 0.45, 0.55, 1)
        _CloudLighting    ("Cloud Sun Lighting",     Range(0, 1))  = 0.5
        _CloudSilverIntensity ("Silver Lining Intensity", Range(0, 3)) = 1.0
        _CloudSilverPower ("Silver Lining Tightness", Range(1, 32)) = 8

        [Header(Cirrus)]
        _CirrusIntensity ("Cirrus Intensity", Range(0, 1)) = 0.35
        _CirrusScale     ("Cirrus Scale",     Float)       = 3

        [Header(Aurora)]
        _AuroraIntensity ("Aurora Intensity (driven by ToD)", Range(0, 3)) = 0
        _AuroraColor1    ("Aurora Low Color",  Color) = (0.1, 0.9, 0.45, 1)
        _AuroraColor2    ("Aurora High Color", Color) = (0.4, 0.2, 0.8, 1)

        [Header(Atmosphere)]
        _HazeIntensity    ("Haze Intensity",    Range(0, 1))    = 0.15
    }
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType"     = "Background"
            "Queue"          = "Background"
        }

        Pass
        {
            Cull  Off
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma multi_compile_instancing
            #pragma shader_feature_local _SOL_CLOUD_LOW _SOL_CLOUD_MEDIUM _SOL_CLOUD_HIGH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Hashes.hlsl"

            // ────────────────────────────────────────
            // OkLab perceptual color space
            // (Björn Ottosson — https://bottosson.github.io/posts/oklab/)
            // ────────────────────────────────────────

            float3 LinearToOklab(float3 rgb)
            {
                float l = 0.4122214708 * rgb.r + 0.5363325363 * rgb.g + 0.0514459929 * rgb.b;
                float m = 0.2119034982 * rgb.r + 0.6806995451 * rgb.g + 0.1073969566 * rgb.b;
                float s = 0.0883024619 * rgb.r + 0.2817188376 * rgb.g + 0.6299787005 * rgb.b;
                float l_ = pow(max(l, 0.0), 0.333333);
                float m_ = pow(max(m, 0.0), 0.333333);
                float s_ = pow(max(s, 0.0), 0.333333);
                return float3(
                    0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_,
                    1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_,
                    0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_);
            }

            float3 OklabToLinear(float3 lab)
            {
                float l_ = lab.r + 0.3963377774 * lab.g + 0.2158037573 * lab.b;
                float m_ = lab.r - 0.1055613458 * lab.g - 0.0638541728 * lab.b;
                float s_ = lab.r - 0.0894841775 * lab.g - 1.2914855480 * lab.b;
                float l = l_ * l_ * l_;
                float m = m_ * m_ * m_;
                float s = s_ * s_ * s_;
                return float3(
                     4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                    -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                    -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
            }

            // ────────────────────────────────────────
            // Uniforms
            // ────────────────────────────────────────

            CBUFFER_START(UnityPerMaterial)
                // Sky
                float4 _ZenithColor;
                float4 _HorizonColor;
                float4 _NadirColor;
                float  _ZenithBlend;
                float  _NadirBlend;
                float  _HorizonBlend;
                float4 _HorizonWarmColor;
                float  _HorizonWarmthFalloff;
                // Stars
                float  _StarHeight;
                float  _StarIntensity;
                float  _StarPower;
                float  _StarRotation;
                float  _StarTwinkleSpeed;
                float  _StarTwinkleAmount;
                // Milky way
                float  _GalaxyIntensity;
                float4 _GalaxyColor1;
                float4 _GalaxyColor2;
                float  _NightFactor;
                // Sun
                float4 _SunDirection;
                float4 _SunDiscColor;
                float  _SunDiscSize;
                float  _SunGlowFalloff;
                float  _SunGlowIntensity;
                float4 _CoronaColor;
                // Moon
                float4 _MoonDirection;
                float4 _MoonColor;
                float4 _MoonDarkColor;
                float  _MoonDiscSize;
                float  _MoonSharpness;
                // Eclipses
                float  _SolarEclipseFactor;
                float  _LunarEclipseFactor;
                float4 _EclipseTint;
                // Clouds
                float  _CloudScale;
                float  _CloudSpeed;
                float  _CloudTime;
                float4 _CloudWindDirection;
                float  _CloudErosion;
                float  _CloudCoverage;
                float  _CloudDensity;
                float  _CloudHeight;
                float4 _CloudColor;
                float4 _CloudShadowColor;
                float  _CloudLighting;
                float  _CloudSilverIntensity;
                float  _CloudSilverPower;
                // Cirrus
                float  _CirrusIntensity;
                float  _CirrusScale;
                // Aurora
                float  _AuroraIntensity;
                float4 _AuroraColor1;
                float4 _AuroraColor2;
                // Atmosphere
                float  _HazeIntensity;
            CBUFFER_END

            // ────────────────────────────────────────
            // Star gradient (HDR, linear RGB)
            // ────────────────────────────────────────

            static const float4 STAR_GRADIENT[6] =
            {
                float4(5.656854,   0.0,        0.0,       0.0000000),
                float4(0.9999999,  0.0,        0.0,       0.2411841),
                float4(41.82591,   23.26371,   0.0,       0.4647135),
                float4(21.44502,   1168.754,   0.0,       0.5970550),
                float4(0.0,        758.7851,   1024.0,    0.7852903),
                float4(0.0,        88.25633,   776.0471,  1.0000000),
            };

            // ────────────────────────────────────────
            // Noise functions
            // ────────────────────────────────────────

            float ValueNoise(float2 uv)
            {
                float2 fi = floor(uv);
                float2 ff = frac(uv);
                ff = ff * ff * (3.0 - 2.0 * ff);
                float r0, r1, r2, r3;
                Hash_Tchou_2_1_float(fi,                r0);
                Hash_Tchou_2_1_float(fi + float2(1, 0), r1);
                Hash_Tchou_2_1_float(fi + float2(0, 1), r2);
                Hash_Tchou_2_1_float(fi + float2(1, 1), r3);
                return lerp(lerp(r0, r1, ff.x), lerp(r2, r3, ff.x), ff.y);
            }

            // 5-octave FBM for clouds.
            float CloudFBM(float2 uv)
            {
                float result = 0.0;
                float amp    = 0.5;
                float2 p     = uv;
                [unroll]
                for (int i = 0; i < 5; i++)
                {
                    result += ValueNoise(p) * amp;
                    p   *= 2.01;
                    amp *= 0.5;
                }
                return result;
            }

            float CloudVolumeFBM(float2 uv)
            {
                #if defined(_SOL_CLOUD_LOW)
                    const int octaveCount = 3;
                #elif defined(_SOL_CLOUD_HIGH)
                    const int octaveCount = 5;
                #else
                    const int octaveCount = 4;
                #endif

                float result = 0.0;
                float amp = 0.5;
                float2 p = uv;
                [unroll]
                for (int i = 0; i < octaveCount; i++)
                {
                    result += ValueNoise(p) * amp;
                    p = mul(float2x2(1.83, -0.64, 0.64, 1.83), p);
                    amp *= 0.5;
                }
                return result;
            }

            float SampleCloudDensity(float2 uv)
            {
                float baseShape = CloudVolumeFBM(uv);
                float erosion = CloudVolumeFBM(uv * 2.37 + 19.4);
                return baseShape - (erosion - 0.5) * _CloudErosion * 0.28;
            }

            // Perceptual (OkLab) blend across the star color gradient.
            float3 SampleStarGradient(float t)
            {
                float3 col = LinearToOklab(STAR_GRADIENT[0].rgb);
                [unroll]
                for (int c = 1; c < 6; c++)
                {
                    float pos = saturate((t - STAR_GRADIENT[c - 1].w) /
                                        (STAR_GRADIENT[c].w - STAR_GRADIENT[c - 1].w));
                    col = lerp(col, LinearToOklab(STAR_GRADIENT[c].rgb), pos);
                }
                return OklabToLinear(col);
            }

            // Point-star field on a 3D cell grid over the star dome.
            // Each cell may hold one star, jittered away from cell borders
            // so stars never clip at boundaries and only the containing
            // cell needs evaluating. Rotation-safe (no planar projection).
            // Returns chroma-normalized star color * brightness.
            float3 StarField(float3 sdir)
            {
                float3 p = sdir * (_StarHeight * 0.3);
                float3 cellId = floor(p);
                float3 f = frac(p);

                float h1, h2, h3;
                Hash_Tchou_2_1_float(cellId.xy + cellId.z * 17.1717, h1);
                Hash_Tchou_2_1_float(cellId.yz + h1 * 31.3131, h2);
                Hash_Tchou_2_1_float(cellId.zx + h2 * 23.2323, h3);

                // ~60% of cells hold a star.
                if (h1 > 0.6) return float3(0.0, 0.0, 0.0);

                float3 starPos = 0.25 + 0.5 * float3(h1 * 1.6667, h2, h3);
                float dist = length(f - starPos);

                // Brighter stars are slightly larger.
                float radius  = 0.11 + 0.14 * h2 * h2;
                float core    = saturate(1.0 - dist / radius);
                float falloff = 1.0 + _StarPower * 0.15;
                float star    = pow(core, falloff);

                // Per-star twinkle phase and speed.
                float tw = 1.0 - _StarTwinkleAmount * (0.5 + 0.5 *
                    sin((_CloudTime * 20.0) * (_StarTwinkleSpeed * (0.5 + h3)) + h1 * 40.0));

                float brightness = star * (0.25 + 0.75 * h2 * h2) * tw;

                // Gradient colors are extreme HDR; keep the chroma only and
                // let _StarIntensity control brightness.
                float3 c = SampleStarGradient(h3);
                c /= max(max(c.r, max(c.g, c.b)), 1.0);
                return c * brightness;
            }

            // ────────────────────────────────────────
            // Celestial disc helpers
            // ────────────────────────────────────────

            // Smooth disc mask: 1 inside the disc, 0 outside, anti-aliased edge.
            // `cosAngle` = dot(viewDir, bodyDir), `cosRadius` = disc size threshold.
            float DiscMask(float cosAngle, float cosRadius)
            {
                float edge = fwidth(cosAngle) * 1.5;
                return smoothstep(cosRadius - edge, cosRadius + edge, cosAngle);
            }

            // Reconstruct a sphere normal for a point on the disc.
            // Returns a tangent-space normal (Z = toward viewer).
            float3 DiscSphereNormal(float3 viewDir, float3 bodyDir, float cosAngle, float cosRadius)
            {
                // Build tangent frame around bodyDir
                float3 up    = abs(bodyDir.y) < 0.999 ? float3(0, 1, 0) : float3(1, 0, 0);
                float3 right = normalize(cross(up, bodyDir));
                up = cross(bodyDir, right);

                // Project viewDir into the disc's tangent plane
                float2 offset = float2(dot(viewDir, right), dot(viewDir, up));
                // Normalize by angular radius of the disc
                float angularRadius = sqrt(max(1.0 - cosRadius * cosRadius, 1e-6));
                offset /= angularRadius;

                float r2 = dot(offset, offset);
                float z  = sqrt(max(1.0 - r2, 0.0));
                return float3(offset.x, offset.y, z);
            }

            // ────────────────────────────────────────
            // Vertex / Fragment
            // ────────────────────────────────────────

            struct Attributes
            {
                float3 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionCS = TransformObjectToHClip(IN.positionOS);
                OUT.positionWS = TransformObjectToWorld(IN.positionOS);
                return OUT;
            }

            float4 Frag(Varyings IN) : SV_Target
            {
                float3 dir = normalize(IN.positionWS);
                float  y   = dir.y;

                float3 sunDir  = normalize(_SunDirection.xyz);
                float3 moonDir = normalize(_MoonDirection.xyz);
                float  sunDot  = dot(dir, sunDir);
                float  moonDot = dot(dir, moonDir);

                // ── Sky gradient ──────────────────────
                float zenithMask  = smoothstep(0.0, _ZenithBlend,  y);
                float nadirMask   = smoothstep(0.0, _NadirBlend,  -y);
                float horizonMask = pow(max(1.0 - zenithMask - nadirMask, 0.0), _HorizonBlend);

                // Sun-relative horizon warmth: warm tint concentrated around
                // the sun's azimuth so dawn/dusk glow sits behind the sun
                // instead of ringing the whole horizon. Strength (alpha) is
                // driven per-frame by TimeOfDay.
                float2 dirAz  = dir.xz;
                float2 sunAz  = sunDir.xz;
                float  azNorm = max(length(dirAz) * length(sunAz), 1e-4);
                float  azAlign = saturate((dot(dirAz, sunAz) / azNorm) * 0.5 + 0.5);
                float  warmth = pow(azAlign, _HorizonWarmthFalloff) * _HorizonWarmColor.a;
                float3 horizonCol = lerp(_HorizonColor.rgb, _HorizonWarmColor.rgb, warmth);

                float3 sky = saturate(_ZenithColor.rgb  * zenithMask
                                    + horizonCol        * horizonMask
                                    + _NadirColor.rgb   * nadirMask);

                // ── Stars ─────────────────────────────
                // Rotate the star dome around world X (the sun's path axis,
                // driven by TimeOfDay) so stars rise and set over the night.
                float ss, cs;
                sincos(_StarRotation, ss, cs);
                float3 sdir = float3(dir.x, dir.y * cs - dir.z * ss,
                                            dir.y * ss + dir.z * cs);

                // 3D cell-based point stars: sampled directly on the rotated
                // dome direction, so there is no projection singularity to
                // hide as the dome turns. Twinkle is per-star inside
                // StarField.
                float3 stars = StarField(sdir)
                             * (_StarIntensity * 0.06)
                             * zenithMask;

                // ── Milky way ─────────────────────────
                // Nebula band around a great circle of the dome; rotates
                // with the stars and fades in only at night.
                float bandDist = dot(sdir, normalize(float3(0.2, 0.35, 0.91)));
                float bandMask = exp(-bandDist * bandDist * 18.0);
                float neb = CloudFBM(sdir.xz * 4.0 + sdir.y * 2.0);
                float3 galaxy = lerp(_GalaxyColor2.rgb, _GalaxyColor1.rgb, neb)
                              * (bandMask * neb * neb * _GalaxyIntensity * _NightFactor);
                stars += galaxy * zenithMask;

                // ── Aurora ────────────────────────────
                // Vertical curtains: anisotropically stretched, domain-warped
                // noise confined to a mid-elevation band. Intensity is driven
                // by TimeOfDay (aurora nights only, storm-suppressed).
                if (_AuroraIntensity > 0.001)
                {
                    float auroraBand = smoothstep(0.05, 0.3, y)
                                     * (1.0 - smoothstep(0.5, 0.85, y));
                    float2 aUV  = dir.xz / (y + 0.8);
                    float warpA = CloudFBM(aUV * 1.3 + (_CloudTime * 20.0) * 0.015);
                    float rays  = CloudFBM(float2(aUV.x * 2.6 + warpA * 1.4, aUV.y * 0.6)
                                + float2((_CloudTime * 20.0) * 0.02, (_CloudTime * 20.0) * 0.005));
                    rays = pow(saturate(rays * 1.8 - 0.62), 2.0);
                    float flicker = 0.75 + 0.25 * sin((_CloudTime * 20.0) * 0.7 + warpA * 9.0);
                    float3 aurCol = lerp(_AuroraColor1.rgb, _AuroraColor2.rgb,
                                         saturate((y - 0.15) * 2.2));
                    stars += aurCol * (rays * auroraBand * flicker * _AuroraIntensity);
                }

                // ── Sun disc & glow ───────────────────
                float sunMask = DiscMask(sunDot, _SunDiscSize);

                // During solar eclipse, the moon overlaps the sun — carve it out.
                // The occluder disc is slightly larger to create a clean silhouette.
                // Disc size is a cosine threshold, so a LARGER disc needs a
                // SMALLER threshold: scale the (1 - cos) angular term instead.
                float occluderCos = 1.0 - (1.0 - _SunDiscSize) * 1.2;
                float moonOverSun = DiscMask(moonDot, occluderCos);
                float occluder    = moonOverSun * _SolarEclipseFactor;

                // Corona: soft halo around the sun, several disc radii wide,
                // minus the occluder — leaves a glowing ring at the moon's limb.
                float coronaHaloCos = 1.0 - (1.0 - _SunDiscSize) * 8.0;
                float halo = smoothstep(coronaHaloCos, 1.0, sunDot);
                float coronaRing = saturate(halo - moonOverSun) * _SolarEclipseFactor;
                float3 corona = _CoronaColor.rgb * coronaRing;

                // The visible sun = disc minus occluder, plus corona.
                float visibleSun = saturate(sunMask - occluder);
                float3 sunDisc   = _SunDiscColor.rgb * visibleSun + corona;

                // Atmospheric glow: Mie-like forward scatter around the sun.
                // Dims during eclipse (the sky darkens).
                float glowFade = 1.0 - _SolarEclipseFactor * 0.9;
                float glow     = pow(saturate(sunDot), _SunGlowFalloff)
                               * _SunGlowIntensity * glowFade;

                // ── Moon disc with phase ──────────────
                float moonMask = DiscMask(moonDot, _MoonDiscSize);

                // Don't draw the moon when it's behind the sun disc (new-moon transit).
                float moonBehindSun = DiscMask(sunDot, _MoonDiscSize);
                float moonOcclusion = moonBehindSun * (1.0 - _SolarEclipseFactor);
                float visibleMoon   = saturate(moonMask - moonOcclusion);

                // Phase lighting: reconstruct a sphere normal and light it.
                float3 moonN   = DiscSphereNormal(dir, moonDir, moonDot, _MoonDiscSize);
                // Transform sun direction into the moon's tangent frame for lighting.
                float3 moonUp    = abs(moonDir.y) < 0.999 ? float3(0, 1, 0) : float3(1, 0, 0);
                float3 moonRight = normalize(cross(moonUp, moonDir));
                moonUp = cross(moonDir, moonRight);
                float3 sunInMoon = float3(dot(sunDir, moonRight),
                                          dot(sunDir, moonUp),
                                          dot(sunDir, moonDir));
                float phaseLit = saturate(dot(moonN, normalize(sunInMoon))
                               * _MoonSharpness * 0.5 + 0.5);

                float3 moonBase = lerp(_MoonDarkColor.rgb, _MoonColor.rgb, phaseLit);

                // Lunar eclipse: tint the lit portion red.
                moonBase = lerp(moonBase, moonBase * _EclipseTint.rgb, _LunarEclipseFactor);
                moonBase *= 1.0 - _LunarEclipseFactor * 0.5;

                float3 moonDisc = moonBase * visibleMoon;

                // ── Clouds ────────────────────────────
                float2 cloudUV = dir.xz / max(dir.y + _CloudHeight, 0.01);
                float2 cloudWind = normalize(_CloudWindDirection.xy + float2(1e-4, 0.0));
                cloudUV += _CloudTime * _CloudSpeed * cloudWind;

                float warp1 = CloudVolumeFBM(cloudUV * _CloudScale);
                float warp2 = CloudVolumeFBM(cloudUV * _CloudScale + 5.2);
                float2 warped = cloudUV + float2(warp1, warp2) * 0.15;

                float rawDensity = SampleCloudDensity(warped * _CloudScale);
                #if !defined(_SOL_CLOUD_LOW)
                    float shell1 = SampleCloudDensity((warped + cloudWind * 0.055 + dir.xz * 0.025) * _CloudScale + 11.3);
                    rawDensity = rawDensity * 0.66 + shell1 * 0.34;
                #endif
                #if defined(_SOL_CLOUD_HIGH)
                    float shell2 = SampleCloudDensity((warped - cloudWind * 0.08 + dir.xz * 0.045) * _CloudScale + 23.7);
                    rawDensity = rawDensity * 0.78 + shell2 * 0.22;
                #endif

                float density = smoothstep(_CloudCoverage, _CloudCoverage + 0.2, rawDensity);
                density *= _CloudDensity * saturate(dir.y * 10.0);

                #if defined(_SOL_CLOUD_LOW)
                    const int lightSampleCount = 1;
                #elif defined(_SOL_CLOUD_HIGH)
                    const int lightSampleCount = 5;
                #else
                    const int lightSampleCount = 3;
                #endif

                float densityTowardSun = 0.0;
                [unroll]
                for (int lightSample = 1; lightSample <= lightSampleCount; lightSample++)
                {
                    float stepDistance = 0.026 * lightSample;
                    densityTowardSun += SampleCloudDensity(
                        (warped + sunDir.xz * stepDistance) * _CloudScale);
                }
                densityTowardSun /= lightSampleCount;
                float dirLit     = saturate((rawDensity - densityTowardSun) * 4.0 + 0.7);
                float ambientLit = saturate(sunDot * 0.5 + 0.6);
                float litTerm    = ambientLit * lerp(1.0, dirLit * 1.4, _CloudLighting);
                float opticalDepth = saturate((rawDensity - _CloudCoverage) * 3.5);
                litTerm *= lerp(1.0, 0.58, opticalDepth * saturate(1.0 - dir.y));

                float3 cloudCol = lerp(_CloudShadowColor.rgb, _CloudColor.rgb,
                                       saturate(litTerm));

                // Silver lining: forward-scatter glow on thin cloud fringes
                // near the sun, tinted by the sun's chromaticity. Fades once
                // the sun drops well below the horizon.
                float fringe = saturate(density * (1.0 - density) * 4.0);
                float3 sunChroma = _SunDiscColor.rgb
                    / max(max(_SunDiscColor.r, max(_SunDiscColor.g, _SunDiscColor.b)), 1e-3);
                float sunUpFade = saturate(sunDir.y * 4.0 + 0.6);
                cloudCol += sunChroma * (pow(saturate(sunDot), _CloudSilverPower)
                          * _CloudSilverIntensity * fringe * sunUpFade);

                // ── Cirrus (high thin layer) ──────────
                // Anisotropically stretched wisps on a higher virtual plane,
                // scrolling on a different heading. Drawn under the main
                // cumulus layer (higher altitude), sharing its lit color so
                // day/night/sunset tints apply automatically.
                float cirrus = 0.0;
                if (_CirrusIntensity > 0.001)
                {
                    float2 cirUV = dir.xz / max(dir.y + 0.45, 0.02);
                    cirUV += _CloudTime * _CloudSpeed * float2(cloudWind.y, -cloudWind.x) * 0.42;
                    float cir = CloudFBM(float2(cirUV.x * 0.5, cirUV.y * 2.2) * _CirrusScale + 3.7);
                    cirrus = smoothstep(0.5, 0.85, cir)
                           * _CirrusIntensity * saturate(dir.y * 6.0);
                }
                float3 cirrusCol = lerp(_CloudShadowColor.rgb, _CloudColor.rgb,
                                        saturate(sunDot * 0.5 + 0.65));

                // ── Atmosphere ─────────────────────────
                float haze = pow(1.0 - abs(y), 4.0) * _HazeIntensity;

                // ── Composite ─────────────────────────
                // Layer order: sky → sun/moon (additive) → stars/aurora
                //            (behind clouds & bodies) → cirrus → clouds
                //            → atmosphere (additive on top)

                // Celestial bodies — block stars behind them.
                float bodyMask = saturate(visibleSun + visibleMoon + occluder);
                float3 bodies  = sunDisc + moonDisc;

                float3 color = sky;
                color += stars * (1.0 - bodyMask);   // stars & aurora behind bodies
                color += bodies;                      // sun & moon on top of sky
                color  = lerp(color, cirrusCol, cirrus * 0.6); // high cirrus
                color  = lerp(color, cloudCol, density); // cumulus over everything
                color += horizonCol * (glow + haze); // atmospheric scatter (warm near sun)

                return float4(color, 1.0);
            }

            ENDHLSL
        }
    }

    FallBack Off
}
