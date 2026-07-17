#ifndef SOL_WATER_WAVES_INCLUDED
#define SOL_WATER_WAVES_INCLUDED

// ---------------------------------------------------------------------------
// Shared Gerstner wave evaluation for Sol.Water.
//
// Used by both the Forward and DepthOnly passes of Sol.Water.shader.
//
// IMPORTANT: SolWaterSurfaceSampler (WaterVolume.cs) mirrors this math in C#
// so gameplay height queries (buoyancy / swimming) stay in sync with the
// rendered surface. Any change here MUST be replicated there.
//
// Wave layout (4 Gerstner waves + 1 vertical swell sine):
//   Wave 1: material wave 1 direction (wind-biased x0.3)
//   Wave 2: material wave 2 direction (wind-biased x0.15),
//           amp x wave2Scale, freq x1.3, speed x0.8
//   Wave 3: wave-1 direction rotated +36.87 deg,
//           amp x0.35 x detail, freq x2.4, speed x1.25
//   Wave 4: wave-2 direction rotated -36.87 deg,
//           amp x0.22 x detail, freq x3.9, speed x1.45
//   Swell : vertical-only sine; the RAW direction vector's magnitude is its
//           spatial frequency (see WaterVolume.ReadMaterial).
// ---------------------------------------------------------------------------

void SolAccumGerstner(
    float2 posXZ, float2 dir, float amp, float freq, float phaseSpeed,
    float steepness, float waveTime,
    inout float3 disp, inout float3 dN)
{
    float theta = dot(dir, posXZ) * freq + waveTime * phaseSpeed;
    float s = sin(theta);
    float c = cos(theta);

    // Clamp steepness so horizontal displacement vanishes with amplitude and
    // the 4-wave sum can never self-intersect (sum of Q*k*A <= 1).
    float q = min(steepness, 1.0 / max(freq * amp * 4.0, 1e-4));

    disp.xz += dir * (q * amp * c);
    disp.y  += amp * s;

    // Accumulate normal terms: N = (-dN.x, 1 - dN.y, -dN.z) after all waves.
    dN.x += dir.x * freq * amp * c;
    dN.z += dir.y * freq * amp * c;
    dN.y += q * freq * amp * s;
}

// Distance-based wave LOD fades: x = primary waves, y = detail waves,
// z = steepness (horizontal displacement). Coarse LOD tiles have too few
// vertices to sample short wavelengths, which aliases and opens seams at
// ring boundaries. All geometric waves fully yield to the long swell by
// ~1x fadeDistance, so LOD boundaries beyond it only ever displace the
// swell - which even the coarsest ring samples cleanly. Set fadeDistance
// to about fullDetailRings x tileSize of the WaterTileGrid.
//
// centerXZ is the shared fade centre (_Sol_WaveFadeCenter, published by
// WaterTileGrid) - the SAME origin the mesh LOD rings use, NOT the
// rendering camera. The C# mirror (SolWaterSurfaceSampler) applies these
// fades identically.
float3 SolComputeWaveFades(float2 posXZ, float2 centerXZ, float fadeDistance)
{
    float dist = distance(posXZ, centerXZ);
    float f = max(fadeDistance, 1.0);
    float primary = 1.0 - smoothstep(f * 0.5,  f,        dist);
    float detail  = 1.0 - smoothstep(f * 0.25, f * 0.55, dist);
    float steep   = 1.0 - smoothstep(f * 0.4,  f * 0.8,  dist);
    return float3(primary, detail, steep);
}

void SolEvaluateWaves(
    float2 posXZ, float waveTime,
    float amplitude, float frequency, float speed,
    float2 dir1Raw, float2 dir2Raw, float wave2Scale,
    float steepness, float detailScale,
    float swellAmplitude, float swellSpeed, float2 swellDirRaw,
    float2 windDirXZ, float windStrength,
    float3 lodFades,
    out float3 displacement, out float3 normal)
{
    // Wind biases the RAW (unnormalized) wave directions, then normalize.
    float2 d1raw = dir1Raw + windDirXZ * windStrength * 0.3;
    float2 d2raw = dir2Raw + windDirXZ * windStrength * 0.15;
    float2 dir1 = dot(d1raw, d1raw) > 1e-6 ? normalize(d1raw) : float2(1.0, 0.0);
    float2 dir2 = dot(d2raw, d2raw) > 1e-6 ? normalize(d2raw) : float2(0.496, 0.868);

    // Detail wave directions: fixed +/-36.87 deg rotations (cos 0.8, sin 0.6).
    float2 dir3 = float2(dir1.x * 0.8 - dir1.y * 0.6, dir1.x * 0.6 + dir1.y * 0.8);
    float2 dir4 = float2(dir2.x * 0.8 + dir2.y * 0.6, -dir2.x * 0.6 + dir2.y * 0.8);

    float amp = amplitude * (1.0 + windStrength * 0.2);

    float ampPrimary = amp * lodFades.x;
    float ampDetail  = amp * lodFades.x * lodFades.y * detailScale;
    float steep      = steepness * lodFades.z;

    displacement = float3(0.0, 0.0, 0.0);
    float3 dN = float3(0.0, 0.0, 0.0);

    SolAccumGerstner(posXZ, dir1, ampPrimary,               frequency,       speed,        steep, waveTime, displacement, dN);
    SolAccumGerstner(posXZ, dir2, ampPrimary * wave2Scale,  frequency * 1.3, speed * 0.8,  steep, waveTime, displacement, dN);
    SolAccumGerstner(posXZ, dir3, ampDetail * 0.35,         frequency * 2.4, speed * 1.25, steep, waveTime, displacement, dN);
    SolAccumGerstner(posXZ, dir4, ampDetail * 0.22,         frequency * 3.9, speed * 1.45, steep, waveTime, displacement, dN);

    // Long ocean swell: vertical-only sine.
    float swellPhase = dot(posXZ, swellDirRaw) + waveTime * swellSpeed;
    float dSwell = cos(swellPhase) * swellAmplitude;
    displacement.y += sin(swellPhase) * swellAmplitude;
    dN.x += swellDirRaw.x * dSwell;
    dN.z += swellDirRaw.y * dSwell;

    normal = normalize(float3(-dN.x, 1.0 - dN.y, -dN.z));
}

#endif // SOL_WATER_WAVES_INCLUDED
