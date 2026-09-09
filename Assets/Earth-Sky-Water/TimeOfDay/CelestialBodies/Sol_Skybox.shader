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
        _StarTwinkleSpeed  ("Star Twinkle Speed (cycles per second)",  Range(0, 5)) = 1.25
        _StarTwinkleAmount ("Star Twinkle Amount", Range(0, 1))  = 0.65
        [HideInInspector] _StarTime ("Star Presentation Time", Float) = 0

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
        _SunLimbDarkening("Sun Limb Darkening", Range(0, 1)) = 0.12
        _CoronaPower("Corona Power", Range(1, 96)) = 24
        [HDR]
        _CoronaColor     ("Eclipse Corona Color", Color) = (1.5, 0.4, 0.15, 1)
        _SunGlowWideWeight ("Wide Aureole Weight",   Range(0, 4)) = 0.75
        _SunGlowWideFalloff("Wide Aureole Falloff",  Range(0.05, 4)) = 0.6
        _SunExtinction     ("Air Mass Extinction",   Range(0, 1)) = 0.35

        [Header(Moon)]
        _MoonDirection   ("Moon Direction",   Vector) = (0, -1, 0, 0)
        _MoonColor       ("Moon Lit Color",   Color)  = (0.85, 0.9, 1, 1)
        _MoonDarkColor   ("Moon Dark Color",  Color)  = (0.01, 0.01, 0.02, 1)
        _MoonDiscSize    ("Moon Disc Size",   Range(0.990, 0.9999)) = 0.9993
        _MoonSharpness   ("Terminator Sharpness", Range(1, 10)) = 3
        [NoScaleOffset] _MoonSurfaceTex ("Moon Surface", 2D) = "white" {}
        _MoonSurfaceRotation ("Moon Surface Rotation", Range(0, 360)) = 0

        [Header(Eclipses)]
        _SolarEclipseFactor ("Solar Eclipse", Range(0, 1)) = 0
        _LunarEclipseFactor ("Lunar Eclipse", Range(0, 1)) = 0
        _EclipseTint        ("Lunar Eclipse Tint", Color)  = (0.6, 0.15, 0.1, 1)

        [Header(Horizon)]
        _HorizonLevel      ("Horizon Occlusion Level",      Range(-0.25, 0.25)) = 0
        _HorizonSoftness   ("Horizon Occlusion Softness",   Range(0.0005, 0.05)) = 0.0035
        _HorizonRefraction ("Refraction Strength",          Range(0, 4)) = 1
        _HorizonFlatten    ("Low Body Flattening",          Range(0, 1)) = 0.45
        _HorizonGlowFloor  ("Below Horizon Glow Retention", Range(0, 1)) = 0.2

        [Header(Twilight)]
        _EarthShadowColor  ("Earth Shadow Tint (A = strength)", Color) = (0.45, 0.5, 0.7, 0.85)
        _BeltOfVenusColor  ("Belt of Venus (A = strength)",     Color) = (0.55, 0.24, 0.26, 0.7)
        _TwilightIntensity ("Twilight Band Intensity", Range(0, 2)) = 1

        [Header(Clouds)]
        _CloudScale       ("Cloud Scale",       Float)          = 5
        _CloudSpeed       ("Cloud Speed",       Float)          = 0.01
        _CloudTime        ("Cloud Time",        Float)          = 0
        _CloudWindDirection ("Cloud Wind Direction", Vector)    = (1, 0.3, 0, 0)
        _CloudErosion     ("Cloud Edge Erosion", Range(0, 1))   = 0.35
        _CloudNoiseTiling ("Packed Noise UV Scale", Float) = 0.125
        _CloudWeatherScale ("Weather Map Frequency", Range(0.01, 0.2)) = 0.06
        _CloudWeatherInfluence ("Regional Coverage Influence", Range(0, 0.5)) = 0.22
        _CloudTypeInfluence ("Regional Cloud Type Influence", Range(0, 0.5)) = 0.18
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
        _AuroraElevation ("Aurora Elevation (min max softness)", Vector) = (0.08, 0.62, 0.08, 0)

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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Hashes.hlsl"
            #include "../../Water/Shaders/SolSkyCommon.hlsl"

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
                float  _StarTime;
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
                float  _SunLimbDarkening;
                float  _CoronaPower;
                float4 _CoronaColor;
                float  _SunGlowWideWeight;
                float  _SunGlowWideFalloff;
                float  _SunExtinction;
                // Moon
                float4 _MoonDirection;
                float4 _MoonColor;
                float4 _MoonDarkColor;
                float  _MoonDiscSize;
                float  _MoonSharpness;
                float  _MoonSurfaceRotation;
                // Eclipses
                float  _SolarEclipseFactor;
                float  _LunarEclipseFactor;
                float4 _EclipseTint;
                // Cloud-derived aurora animation clock; stars use _StarTime.
                float  _CloudTime;
                // Aurora
                float  _AuroraIntensity;
                float4 _AuroraColor1;
                float4 _AuroraColor2;
                float4 _AuroraElevation;
                // Atmosphere
                float  _HazeIntensity;
                // Horizon
                float  _HorizonLevel;
                float  _HorizonSoftness;
                float  _HorizonRefraction;
                float  _HorizonFlatten;
                float  _HorizonGlowFloor;
                // Twilight
                float4 _EarthShadowColor;
                float4 _BeltOfVenusColor;
                float  _TwilightIntensity;
            CBUFFER_END

            TEXTURE2D(_MoonSurfaceTex);
            SAMPLER(sampler_MoonSurfaceTex);
            TEXTURECUBE(_SolSkyStellarBackdrop);
            SAMPLER(sampler_SolSkyStellarBackdrop);
            float _SolSkyStellarBackdropActive;
            float4 _SolSkyStellarParams; // milky way intensity, twinkle amount, reserved

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

            float SolStarTwinkle(float phase)
            {
                float angle = _StarTime * _StarTwinkleSpeed * TWO_PI
                            * lerp(0.75, 1.3, phase) + phase * TWO_PI;
                float pulse = 0.65 * sin(angle) + 0.35 * sin(angle * 1.73 + phase * 9.0);
                return 1.0 + pulse * _StarTwinkleAmount;
            }

            // Point-star field on a 3D cell grid over the star dome.
            // Each cell may hold one star, jittered away from cell borders
            // so stars never clip at boundaries and only the containing
            // cell needs evaluating. Rotation-safe (no planar projection).
            // Returns chroma-normalized star color * brightness.
            float3 StarField(float3 sdir)
            {
                // Profiles authored with the former elevation-like default (-0.08)
                // must still have visible stars when their cubemap is unavailable.
                float gridScale = _StarHeight >= 1.0 ? _StarHeight : 100.0;
                float3 p = sdir * (gridScale * 0.3);
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
                float tw = SolStarTwinkle(h3);

                float brightness = star * (0.25 + 0.75 * h2 * h2) * tw;

                // Gradient colors are extreme HDR; keep the chroma only and
                // let _StarIntensity control brightness.
                float3 c = SampleStarGradient(h3);
                c /= max(max(c.r, max(c.g, c.b)), 1.0);
                return c * brightness;
            }

            // ────────────────────────────────────────
            // Atmospheric optics
            // ────────────────────────────────────────

            // Zenith optical depth per channel: Rayleigh (0.008735 * lambda^-4.08 at
            // 650/550/450 nm) plus a modest Angstrom aerosol term. Scaling this by the
            // relative air mass is what turns a high white sun into a low red one
            // without any authored colour ramp having to describe the transition.
            static const float3 SOL_ZENITH_OPTICAL_DEPTH = float3(0.1385, 0.2101, 0.3702);

            // Kasten-Young relative optical air mass for an apparent altitude, in
            // degrees. 1 at the zenith, ~38 at the horizon. Altitudes below the horizon
            // are clamped: the body is occluded there anyway, and letting the fit run
            // past its domain inverts it.
            float SolAirMass(float altitudeDegrees)
            {
                float h = max(altitudeDegrees, 0.0);
                return 1.0 / (sin(radians(h)) + 0.50572 * pow(h + 6.07995, -1.6364));
            }

            // Extinction relative to a zenith body, so the authored disc colour keeps
            // its meaning overhead and only the approach to the horizon changes it.
            float3 SolAirMassExtinction(float altitudeDegrees, float strength)
            {
                float airMass = SolAirMass(altitudeDegrees);
                return exp(-SOL_ZENITH_OPTICAL_DEPTH * strength * (airMass - 1.0));
            }

            // Bennett's astronomical refraction, in degrees, for a geometric altitude
            // in degrees: 0.575 deg at the horizon, under 0.03 deg above 30 deg.
            float SolRefractionDegrees(float altitudeDegrees)
            {
                float h = max(altitudeDegrees, -1.0);
                return max(1.0 / tan(radians(h + 7.31 / (h + 4.4))), 0.0) / 60.0;
            }

            // ────────────────────────────────────────
            // Celestial disc helpers
            // ────────────────────────────────────────

            // Horizon-aligned tangent frame around a body direction: `right` is
            // horizontal, so the vertical axis of a disc built on this frame lines up
            // with the horizon and can be squashed against it.
            void SolBodyFrame(float3 bodyDir, out float3 right, out float3 up)
            {
                float3 reference = abs(bodyDir.y) < 0.999 ? float3(0, 1, 0) : float3(1, 0, 0);
                right = normalize(cross(reference, bodyDir));
                up    = cross(bodyDir, right);
            }

            // Lifts a body toward the zenith by the refraction its altitude earns, and
            // reports how much its disc should be squashed vertically. The squash is
            // the visible half of the same effect: the lower limb is refracted further
            // than the upper, so a setting body reads as an oval rather than a circle.
            float3 SolApparentBodyDirection(float3 bodyDir, float refractionStrength,
                float flattenStrength, out float flatten)
            {
                float altitude = degrees(asin(clamp(bodyDir.y, -1.0, 1.0)));
                flatten = flattenStrength * saturate(1.0 - altitude / 6.0);

                float lift = radians(SolRefractionDegrees(altitude) * refractionStrength);
                float3 vertical = float3(0.0, 1.0, 0.0) - bodyDir * bodyDir.y;
                float verticalLength = length(vertical);
                if (verticalLength < 1e-4)
                    return bodyDir;
                return normalize(bodyDir + (vertical / verticalLength) * lift);
            }

            // View-direction offset from a disc centre, in units of the disc's angular
            // radius, with the vertical axis compressed by `flatten`.
            float2 SolDiscOffset(float3 viewDir, float3 right, float3 up,
                float cosRadius, float flatten)
            {
                float angularRadius = sqrt(max(1.0 - cosRadius * cosRadius, 1e-8));
                float2 offset = float2(dot(viewDir, right), dot(viewDir, up)) / angularRadius;
                offset.y *= 1.0 + flatten;
                return offset;
            }

            // Anti-aliased coverage of a disc `radius` angular radii across, from an
            // offset produced by SolDiscOffset. `alignment` is dot(viewDir, bodyDir):
            // the tangent-plane offset projects a body and its antipode onto the same
            // point, so without it a second sun appears opposite the real one.
            float SolDiscCoverage(float2 offset, float radius, float alignment)
            {
                float d = length(offset) / max(radius, 1e-4);
                float edge = max(fwidth(d), 1e-5);
                return (1.0 - smoothstep(1.0 - edge, 1.0 + edge, d)) * step(0.0, alignment);
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

                float3 legacySky = max(_ZenithColor.rgb * zenithMask
                                     + horizonCol       * horizonMask
                                     + _NadirColor.rgb  * nadirMask, 0.0);
                float3 sky = _SolSkyFrameActive > 0.5
                    ? SolEvaluateResolvedSkyRadiance(dir)
                    : legacySky;

                // ── Twilight: Earth's shadow and the Belt of Venus ──
                // Once the sun is down, the planet's own shadow rises out of the
                // anti-solar horizon as a blue-grey wedge, capped by the pink band of
                // sunlight still grazing the upper atmosphere. Both are anchored to the
                // anti-solar point, whose elevation is exactly the negative of the
                // sun's, so the pair climbs the eastern sky as the sun sinks in the west.
                float twilight = _TwilightIntensity
                               * smoothstep(0.14, 0.02, sunDir.y)   // in as the sun sets
                               * smoothstep(-0.16, -0.05, sunDir.y); // out as night falls
                if (_SolSkyFrameActive < 0.5 && twilight > 0.001)
                {
                    // 1 opposite the sun, 0 toward it. This is the same azimuth
                    // alignment the horizon warmth uses, mirrored.
                    float antiAlign = saturate(0.5 - 0.5 * (dot(dirAz, sunAz) / azNorm));
                    float shadowTop = -sunDir.y;
                    const float bandWidth = 0.055;

                    // Above the horizon only: below it the viewer is looking at ground
                    // or sea, and a band there would read as a floating stripe.
                    float aboveGround = saturate(y * 14.0);
                    float beltT = (y - shadowTop) / bandWidth;
                    float belt = exp(-beltT * beltT) * aboveGround;
                    float shadowBand = smoothstep(shadowTop + bandWidth * 0.35,
                                                  shadowTop - bandWidth * 0.9, y) * aboveGround;

                    float twilightMask = twilight * antiAlign;
                    sky = lerp(sky, sky * _EarthShadowColor.rgb,
                               saturate(shadowBand * twilightMask * _EarthShadowColor.a));
                    sky += _BeltOfVenusColor.rgb * (belt * twilightMask * _BeltOfVenusColor.a);
                }

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
                float3 stars;
                float packedGalaxy = 0.0;
                if (_SolSkyStellarBackdropActive > 0.5)
                {
                    float4 packedStars = SAMPLE_TEXTURECUBE_LOD(
                        _SolSkyStellarBackdrop, sampler_SolSkyStellarBackdrop, sdir, 0);
                    float3 warmStar = float3(1.0, 0.58, 0.32);
                    float3 coolStar = float3(0.58, 0.72, 1.0);
                    float3 starColor = lerp(warmStar, coolStar, packedStars.g);
                    float twinkle = SolStarTwinkle(packedStars.b);
                    stars = starColor * packedStars.r * twinkle * _StarIntensity * zenithMask;
                    packedGalaxy = packedStars.a;
                }
                else
                {
                    stars = StarField(sdir) * (_StarIntensity * 0.06) * zenithMask;
                }

                // ── Milky way ─────────────────────────
                // Nebula band around a great circle of the dome; rotates
                // with the stars and fades in only at night.
                float bandDist = dot(sdir, normalize(float3(0.2, 0.35, 0.91)));
                float bandMask = exp(-bandDist * bandDist * 18.0);
                float neb = CloudFBM(sdir.xz * 4.0 + sdir.y * 2.0);
                float3 galaxy = lerp(_GalaxyColor2.rgb, _GalaxyColor1.rgb, neb)
                              * (bandMask * neb * neb * _GalaxyIntensity * _NightFactor);
                if (_SolSkyStellarBackdropActive > 0.5)
                    galaxy = _GalaxyColor1.rgb * packedGalaxy
                           * _SolSkyStellarParams.x * _NightFactor;
                stars += galaxy * zenithMask;

                // ── Aurora ────────────────────────────
                // Vertical curtains: anisotropically stretched, domain-warped
                // noise confined to a mid-elevation band. Intensity is driven
                // by TimeOfDay (aurora nights only, storm-suppressed).
                if (_AuroraIntensity > 0.001)
                {
                    float auroraBand = smoothstep(
                        _AuroraElevation.x - _AuroraElevation.z,
                        _AuroraElevation.x + _AuroraElevation.z, y)
                        * (1.0 - smoothstep(
                            _AuroraElevation.y - _AuroraElevation.z,
                            _AuroraElevation.y + _AuroraElevation.z, y));
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

                // ── Horizon occlusion ─────────────────
                // The sky dome is drawn in every direction, including below the
                // horizon, where the world's geometry runs out before the view ray
                // does. Nothing celestial may be drawn there: a body that has set is
                // behind the planet, not behind whatever happens to be modelled. This
                // is a view-direction test rather than a body-direction one, so a body
                // straddling the horizon is clipped along it and genuinely sets.
                float horizonVisibility = smoothstep(_HorizonLevel - _HorizonSoftness,
                                                     _HorizonLevel + _HorizonSoftness, y);

                // ── Apparent (refracted) body directions ──
                // Refraction lifts a low body toward the zenith, which is what keeps a
                // sunset going for a few minutes after the sun is geometrically down,
                // and squashes its disc into the oval every photograph of one shows.
                float sunFlatten, moonFlatten;
                float3 sunApparent  = SolApparentBodyDirection(sunDir,
                    _HorizonRefraction, _HorizonFlatten, sunFlatten);
                float3 moonApparent = SolApparentBodyDirection(moonDir,
                    _HorizonRefraction, _HorizonFlatten, moonFlatten);

                float3 sunRight, sunUp, moonRight, moonUp;
                SolBodyFrame(sunApparent,  sunRight,  sunUp);
                SolBodyFrame(moonApparent, moonRight, moonUp);

                float2 sunOffset  = SolDiscOffset(dir, sunRight,  sunUp,
                    _SunDiscSize,  sunFlatten);
                float2 moonOffset = SolDiscOffset(dir, moonRight, moonUp,
                    _MoonDiscSize, moonFlatten);
                float sunAlignment  = dot(dir, sunApparent);
                float moonAlignment = dot(dir, moonApparent);

                // Air-mass reddening. The disc and the aureole around it share one
                // transmittance, so the sun and the sky it lights redden together
                // instead of drifting apart on separate authored ramps.
                float sunAltitude = degrees(asin(clamp(sunApparent.y, -1.0, 1.0)));
                float3 sunExtinction = SolAirMassExtinction(sunAltitude, _SunExtinction);
                float moonAltitude = degrees(asin(clamp(moonApparent.y, -1.0, 1.0)));
                float3 moonExtinction = SolAirMassExtinction(moonAltitude, _SunExtinction);

                // ── Sun disc & glow ───────────────────
                float sunCoverage = SolDiscCoverage(sunOffset, 1.0, sunAlignment);
                float moonCoverage = SolDiscCoverage(moonOffset, 1.0, moonAlignment);
                // The opaque moon is always closer. Use its actual refracted,
                // flattened silhouette even at first contact, independent of the
                // aggregate eclipse fraction used for environment lighting.
                float visibleSun = sunCoverage * (1.0 - moonCoverage) * horizonVisibility;
                float visibleMoon = moonCoverage * horizonVisibility;

                // Corona: soft halo around the sun, several disc radii wide,
                // minus the occluder — leaves a glowing ring at the moon's limb.
                float halo = (1.0 - smoothstep(0.0, 3.0, length(sunOffset)))
                           * step(0.0, sunAlignment);
                float coronaRing = pow(saturate(halo - moonCoverage),
                                       max(1.0, _CoronaPower * 0.08))
                                 * _SolarEclipseFactor * horizonVisibility;
                float3 corona = _CoronaColor.rgb * coronaRing;

                float sunRadius = saturate(length(sunOffset));
                float limb = lerp(1.0, sqrt(saturate(1.0 - sunRadius * sunRadius)),
                                  _SunLimbDarkening);
                float3 sunDisc = _SunDiscColor.rgb * sunExtinction
                               * visibleSun * limb + corona;

                // Atmospheric glow: Mie forward scatter around the sun, in two lobes.
                // The tight one is the aureole hugging the disc; the broad one is the
                // whole-quadrant wash that makes a low sun light up half the sky. A
                // single lobe can be one or the other but never both, which is why
                // dawn and dusk read as a bright dot on a flat gradient without it.
                float glowFade = 1.0 - _SolarEclipseFactor;
                float forward  = saturate(dot(dir, sunApparent));
                float glowTight = pow(forward, _SunGlowFalloff);
                float glowWide  = pow(forward, _SunGlowWideFalloff) * _SunGlowWideWeight;
                // The wash is an atmospheric effect: it belongs to a sun whose light is
                // crossing the most atmosphere to arrive, and to no other. Left standing
                // at noon it would flood the entire sun-facing half of the sky, so it is
                // gated off above roughly 20 degrees.
                float lowSun    = saturate(1.0 - sunApparent.y * 3.0);
                float glow      = (glowTight + glowWide * lowSun)
                                * _SunGlowIntensity * glowFade;

                // Scattered light still reaches a view ray aimed just under the
                // horizon, but the surface there is lit, not the sky. Fading rather
                // than clipping keeps the waterline free of a hard bright edge.
                float glowHorizon = lerp(_HorizonGlowFloor, 1.0,
                    smoothstep(_HorizonLevel - 0.06, _HorizonLevel + 0.005, y));
                glow *= glowHorizon;

                // ── Moon disc with phase ──────────────
                // Phase lighting: reconstruct a sphere normal and light it. The normal
                // comes from the unflattened offset so refraction squashes the disc's
                // silhouette without also bending its terminator.
                float2 moonRoundOffset = SolDiscOffset(dir, moonRight, moonUp,
                    _MoonDiscSize, 0.0);
                float3 moonN = float3(moonRoundOffset,
                    sqrt(max(1.0 - dot(moonRoundOffset, moonRoundOffset), 0.0)));
                // Transform sun direction into the moon's tangent frame for lighting.
                float3 sunInMoon = float3(dot(sunDir, moonRight),
                                          dot(sunDir, moonUp),
                                          -dot(sunDir, moonApparent));
                float phaseLit = saturate(dot(moonN, normalize(sunInMoon))
                               * _MoonSharpness * 0.5 + 0.5);

                // Equirectangular LROC colour map. Surface rotation advances lunar
                // longitude while the geometric normal continues to drive the phase.
                float longitude = atan2(moonN.x, moonN.z) / TWO_PI + 0.5
                                + _MoonSurfaceRotation / 360.0;
                float latitude = asin(clamp(moonN.y, -1.0, 1.0)) / PI + 0.5;
                float2 moonUV = float2(frac(longitude), saturate(latitude));
                float3 lunarAlbedo = SAMPLE_TEXTURE2D(
                    _MoonSurfaceTex, sampler_MoonSurfaceTex, moonUV).rgb;
                float lunarLuminance = dot(lunarAlbedo, float3(0.299, 0.587, 0.114));
                float3 moonBase = lerp(_MoonDarkColor.rgb * lerp(1.0, lunarLuminance, 0.35),
                    _MoonColor.rgb * lunarAlbedo, phaseLit);

                // Lunar eclipse: tint the lit portion red.
                moonBase = lerp(moonBase, moonBase * _EclipseTint.rgb, _LunarEclipseFactor);
                moonBase *= 1.0 - _LunarEclipseFactor * 0.5;

                // A low moon reddens for the same reason a low sun does.
                float3 moonDisc = moonBase * moonExtinction * visibleMoon;

                // ── Atmosphere ─────────────────────────
                float haze = pow(1.0 - abs(y), 4.0) * _HazeIntensity;

                // ── Composite ─────────────────────────
                // Clouds are composited later by SolCloudRendererFeature. Keeping the
                // sky pass cloud-free makes every quality tier share the same physical
                // transmittance and depth ordering.

                // Celestial bodies — block stars behind them.
                float bodyMask = saturate(visibleSun + visibleMoon);
                float3 bodies  = sunDisc + moonDisc;

                // The aureole is sunlight scattered toward the viewer, so it carries the
                // sun's own reddening. The haze band is ambient horizon scatter and
                // keeps the sky's colour.
                float3 aureoleCol = horizonCol * lerp(1.0, sunExtinction, 0.75);

                float3 color = sky;
                color += stars * (1.0 - bodyMask);   // stars & aurora behind bodies
                color += bodies;                      // sun & moon on top of sky
                if (_SolSkyFrameActive < 0.5)
                    color += aureoleCol * glow + horizonCol * haze;

                return float4(color, 1.0);
            }

            ENDHLSL
        }
    }

    FallBack Off
}
