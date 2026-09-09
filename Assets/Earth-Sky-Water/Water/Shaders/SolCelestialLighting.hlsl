#ifndef SOL_CELESTIAL_LIGHTING_INCLUDED
#define SOL_CELESTIAL_LIGHTING_INCLUDED

#define SOL_MAX_CELESTIAL_LIGHTS 8
float _SolCelestialLightingActive;
int _SolCelestialLightCount;
// xyz points toward the emitter; w selects the URP main-light shadow map.
float4 _SolCelestialDirections[SOL_MAX_CELESTIAL_LIGHTS];
float4 _SolCelestialColors[SOL_MAX_CELESTIAL_LIGHTS];

#endif
