# Sol Sky & Water System

Unity 6 / URP 17 sky, time-of-day, weather, and water framework.

```
TimeOfDay
  - Calendar
  - skybox, sun, moon, planets, stars, aurora, eclipses
  - ambient and fog
        ^
SolWeatherManager
  - immutable blended SolWeatherState
  - cloud, fog, wind, rain, dimming, lightning
        |-----------------------------|
        v                             v
SolAtmosphereController         SolRainVfxController
  - URP height/distance fog       - camera-following particles
  - scattering and haze           - shelter/underwater exposure
        |                             |
        |-----------------------------|
                      v
SolWaterManager
  - global water shader state
  - wind, rain, water level, wave time
  - WaterTileGrid, WaterVolume, ripples, buoyancy, underwater overlay
```

## Scene Setup

1. Add `TimeOfDay`, `Calendar`, and `SolEnvironmentCoordinator` to one scene-owned system-manager GameObject. Assign the sun/moon prefabs and set the authored `Sol/Skybox` material in Lighting.
2. Add `SolWaterManager`. Set `waterLevel`. Keep `windDirection` mostly horizontal, for example `(1, 0, 0.5)`.
3. Add `WaterTileGrid` and assign a `Sol/Water` material. Leave `autoWaterVolume` enabled for a generated gameplay query volume.
4. Add `WaterRippleManager`. Assign `Water/Shaders/Sol.RippleSim.shader` to `simShader` before making builds.
5. Add `SolWeatherManager`. References auto-resolve if the managers are in the scene.
6. Add `SolAtmosphereController` and `SolRainVfxController` to the same scene-owned manager. Optional atmosphere profiles are created from `Create > Sol > Environment > Atmosphere Profile`.
7. Add `SolAtmosphereRendererFeature` and `UnderwaterRendererFeature` to the URP Renderer asset. Keep existing features such as SSAO. Assign the underwater overlay material.
8. Add `UnderwaterVolumeController` to a persistent scene GameObject. Assign `playerTransform` for third-person cameras.
9. Enable Depth Texture and Opaque Texture on the URP asset.
10. Tag the gameplay camera as `MainCamera`, or assign cameras explicitly on the relevant controllers.

## Adaptive Probe Volumes

The three shipped environment scenes share `Assets/Settings/SolEnvironmentAPV.asset`.
It uses 2 m minimum probe spacing, three simplification levels, disk/GPU streaming,
sky occlusion, and sky shading direction. Lighting scenarios are intentionally disabled:
the live ambient probe follows the canonical Sol sky instead of blending separate day and
night bakes.

After finalizing static geometry, open `Window > Rendering > Lighting`, select the
`SolEnvironmentAPV` baking set, and run **Bake Probe Volumes**. Only renderers marked
**Contribute Global Illumination** should define placement and occlusion. Sun, moon, and
settlement lights stay Realtime; `SolEnvironmentLight` enforces this while it owns a local
light. If baked data is absent at runtime, the system warns once and retains trilight
ambient as a deterministic fallback.

Use Probe Adjustment Volumes around caves, enclosed buildings, and shoreline/interior
transitions where automatic placement produces leaking or invalid probes. Keep these
volumes local to the problem area rather than increasing global probe density.

## Celestial Lighting Handoff

The lighting frame publishes a continuous radiance-weighted blend of eligible sun and
moon contributions. Atmosphere and clouds consume that same blend, so overlapping
contributors crossfade naturally. If neither body is eligible around dawn or dusk,
direct celestial lighting and cloud shadows fade to zero while ambient twilight remains;
renderers must not substitute a synthetic white key light.

## Public API

See `../../Documentation/PublicAPI.md` for code examples and the full scripting guide

Common entry points:

| Class | Main use |
|---|---|
| `Sol.ToD.TimeOfDay` | world clock, sky authority, time skip/mutation events |
| `Sol.ToD.Calendar` | day/month/year state and season events |
| `SolLightingDirector` | unified sun, moon, ambient, shader-global, and quality authority |
| `SolLightingQualityProfile` | deterministic Low/Medium/High lighting budgets |
| `SolEnvironmentLight` | opt-in local-light activation, shadow, and volumetric budgeting |
| `SolReflectionProbeAnchor` | ranked, time-sliced realtime environment-probe scheduling |
| `SolWeatherManager` | weather profiles and transitions |
| `SolWeatherState` | immutable effective cloud/rain/wind/fog/wave/lightning state |
| `SolAtmosphereController` | analytic fog plus High-quality directional volumetric scattering and quality state |
| `SolRainVfxController` | camera-following rain, shelter, and exposure |
| `SolWaterManager` | global water level, wind, rain, and wave time |
| `WaterVolume` | animated waterline and underwater queries |
| `WaterTileGrid` | generated water mesh and optional managed volume |
| `WaterRippleManager` | manual ripple emission |
| `WaterRippleSource` | component-based ripple emission for moving objects |
| `WaterBuoyancy` | Rigidbody floating |
| `UnderwaterVolumeController` | underwater overlay state and events |
| `Shared.Water.IWaterSystem` | small gameplay-facing water query interface |

## Wave Sync Contract

Visual waves are defined in `Water/Shaders/SolWaterWaves.hlsl` and mirrored in `SolWaterSurfaceSampler` inside `Water/WaterVolume.cs`.

`WaterVolume.GetSurfaceHeight(worldPosition)` is the gameplay entry point for animated water height. If the shader wave formula changes, update the C# sampler in the same pass.

`WaterTileGrid` publishes the wave LOD fade center so the GPU shader and CPU sampler agree at distance. Without a grid, the sampler assumes full-detail waves.

## Shader Globals

| Global | Set by | Meaning |
|---|---|---|
| `_Sol_SunDirection`, `_Sol_SunColor` | `SolLightingDirector` | dominant water lighting compatibility feed |
| `_Sol_DayFactor` | `SolLightingDirector` | night-to-day blend |
| `_Sol_EclipseFactor` | `SolLightingDirector` | solar eclipse strength |
| `_Sol_WindDirection`, `_Sol_WindStrength` | `SolWaterManager` | wave direction and amplitude bias |
| `_Sol_WaveTime`, `_Sol_GlobalWaveSpeedMul` | `SolWaterManager` | shared water clock |
| `_Sol_RainIntensity` | `SolWaterManager` | rain roughness, normals, reflections, droplets |
| `_Sol_RainRoughnessBoost`, `_Sol_RainNormalBoost`, `_Sol_RainReflectionDampen` | `SolWaterManager` | configured rain response tuning |
| `_Sol_GlobalWaterLevel` | `SolWaterManager` | global fallback water level |
| `_Sol_LightningFlash` | `SolLightingDirector` | reflection and surface strike illumination |
| `_SolAtmosphere*` | `SolAtmosphereController` | analytic fog, noise, sun/moon scattering, High volumetric settings, quality, and lightning |
| `_Sol_WaveFadeCenter` | `WaterTileGrid` | LOD and wave-fade origin |
| `_Sol_Ripples*` | `WaterRippleManager` | analytic fallback ripples |
| `_Sol_RippleSimTex`, `_Sol_RippleSimRegion`, `_Sol_RippleSimParams` | `WaterRippleManager` | GPU ripple sim |
| `_WaterSurfaceY` | `WaterVolume` | underwater overlay surface height |
| `_UnderwaterFactor`, `_UnderwaterDepth` | `UnderwaterVolumeController` | underwater overlay blend |

## Tuning

- Time speed: `TimeOfDay.CycleDuration` and `TimeOfDay.TimeScale`.
- Lighting quality: call `SolLightingDirector.SetTier` to switch the shared Low/Medium/High shadow, reflection-probe, cloud, atmosphere, and water budgets. The shipping default is the Medium `Sol_LightingQuality` resource profile.
- Managed local lights: add `SolEnvironmentLight` beside a point or spot light. Choose night-only, always-on, or a custom day-factor curve, then set its priority, shadow eligibility, and volumetric scattering.
- Dynamic probes: add `SolReflectionProbeAnchor` beside each realtime probe. Captures are ranked by camera visibility/distance, water relevance, and staleness; only one individual-face capture runs at once.
- APV authoring: use `Tools > Sol Environment > Configure Adaptive Probe Volumes` after adding an environment scene, then add that scene to `SolApvSetupUtility.EnvironmentScenePaths` so membership remains testable.
- Lighting diagnostics: open `Tools > Sol Environment > Environment Window` for the active tier, registered/selected lights, shadow slices, volumetric lights, GI requests, and reflection-probe requests/completions. The same values are available from `SolEnvironmentBudget` in editor and development builds.
- Environment clocks: consume `WorldDeltaSeconds` for continuous motion, `WorldDeltaHours` for chronology, and `PresentationDeltaSeconds` only for bounded transitions/transient envelopes. All freeze when Sol time is not moving forward; presentation time deliberately ignores the 10x/100x Sol multiplier.
- Day length: `TimeOfDay.DayRatio`; the four-season annual curve modifies `EffectiveDayRatio` and exposes `SunriseClockHour`/`SunsetClockHour`.
- Climate: `SolWeatherManager` derives `DailyFogTarget` deterministically from `Calendar.WorldDayIndex`, applies dawn-biased `FogDiurnalFactor`, and smooths `CurrentDailyFog` using presentation time. Tune the seasonal fog ranges, `climateSeed`, and `ClimateTransitionDurationSeconds`.
- Weather pacing: `SolWeatherManager.durationHoursRange`, `transitionDurationSeconds`, profile `weight`, and the four seasonal weight multipliers. Seasonal weights affect automatic selection only; `transitionHours` is obsolete. Selection is also weighted by `SolWeatherAdjacency`, so a change steps through neighbouring states rather than jumping anywhere in the set.
- Default weather identities: Clear is an open sky that varies from cloudless to lightly clouded; Fair holds the scattered-cumulus look Clear used to have; Fog is calm, bright and ground-hugging; Overcast is a dry deck; Drizzle, Rain and Storm escalate precipitation, wind and mist; Snow and Blizzard are the frozen branch, with Blizzard obscuring brightly rather than darkening.
- Cloud cover varies per world day. Each profile's `cloudCoverageVariance` scales `SolWeatherManager.DailyCoverageOffset`, a stable hash of the date crossfaded across the day boundary, so the same weather never renders identically twice.
- Profile tuning: use `cloudiness` for coverage, `cloudErosion` for edge breakup, `fogBoost` for extinction, `mistiness` for low height distribution, and `skyObscuration` only for additional sky-wide masking. `waterTurbulence` controls geometric disorder, foam, roughness, and drift.
- Water look: edit `M_Ocean` wave amplitude, frequency, steepness, detail, normals, foam, and reflection settings.
- Storm water polish: strong-wind steering saturates before wave headings collapse, and fixed wave-family phase offsets plus restrained crest foam keep side views from resolving into uniform white rows.
- Lunar water response: `TimeOfDay.LunarTideFactor` peaks at new/full moon; `SolWaterManager.lunarResponseStrength` applies subtle visual swell/foam/reflection changes without changing mean water height.
- Water mesh cost: lower `WaterTileGrid.tileResolution`, `gridRadius`, or `fullDetailRings`.
- Ripples: tune `WaterRippleManager.simResolution`, `simNormalStrength`, `simWaveSpeed`, and `simDamping`.
- Floating objects: tune `WaterBuoyancy.buoyancy`, `submersionDepth`, and `floatPoints`.

## Gotchas

- Assign `Sol.RippleSim.shader` on `WaterRippleManager` before builds; relying on `Shader.Find` is editor-only fallback behavior.
- `TimeOfDay` writes to a hidden runtime skybox clone and restores the authored material when the scene authority releases.
- Edit mode does not drive RenderSettings or skybox properties; use Play mode for the live environment preview so scene saves cannot serialize a temporary clone or preview state.
- Multiple `WaterVolume` instances at different heights all push `_WaterSurfaceY`; the last writer wins for the overlay global.
- `SolWeatherManager` owns weather-driven water and sky fields while enabled.
- `WaterTileGrid` is `[ExecuteAlways]` and creates helper children named `_Tiles` and `_WaterVolume`.
- New materials may need to be selected once in the Inspector so Unity syncs shader keywords.
- Environment shader globals are still scene-wide; true per-volume water state and per-camera underwater state are deferred.
- APV placement is authored, but probe coefficients are not shipped until **Bake Probe Volumes** is run against final static geometry. The one-time runtime warning is expected before that bake.
- The demo's generated water content is intentionally retained until an authored prefab/asset workflow is chosen.
- Surface atmosphere and underwater rendering still use one active camera/global state. True multi-camera and local-volume scoping remain deferred.
