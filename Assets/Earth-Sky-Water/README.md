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

## Public API

See `../../Documentation/PublicAPI.md` for code examples and the full scripting guide

Common entry points:

| Class | Main use |
|---|---|
| `Sol.ToD.TimeOfDay` | world clock, sky authority, time skip/mutation events |
| `Sol.ToD.Calendar` | day/month/year state and season events |
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
| `_Sol_SunDirection`, `_Sol_SunColor` | `SolWaterManager` | dominant water lighting |
| `_Sol_DayFactor` | `SolWaterManager` | night-to-day blend |
| `_Sol_EclipseFactor` | `SolWaterManager` | solar eclipse strength |
| `_Sol_WindDirection`, `_Sol_WindStrength` | `SolWaterManager` | wave direction and amplitude bias |
| `_Sol_WaveTime`, `_Sol_GlobalWaveSpeedMul` | `SolWaterManager` | shared water clock |
| `_Sol_RainIntensity` | `SolWaterManager` | rain roughness, normals, reflections, droplets |
| `_Sol_RainRoughnessBoost`, `_Sol_RainNormalBoost`, `_Sol_RainReflectionDampen` | `SolWaterManager` | configured rain response tuning |
| `_Sol_GlobalWaterLevel` | `SolWaterManager` | global fallback water level |
| `_Sol_LightningFlash` | `SolWaterManager` | reflection and surface strike illumination |
| `_SolAtmosphere*` | `SolAtmosphereController` | analytic fog, noise, sun/moon scattering, High volumetric settings, quality, and lightning |
| `_Sol_WaveFadeCenter` | `WaterTileGrid` | LOD and wave-fade origin |
| `_Sol_Ripples*` | `WaterRippleManager` | analytic fallback ripples |
| `_Sol_RippleSimTex`, `_Sol_RippleSimRegion`, `_Sol_RippleSimParams` | `WaterRippleManager` | GPU ripple sim |
| `_WaterSurfaceY` | `WaterVolume` | underwater overlay surface height |
| `_UnderwaterFactor`, `_UnderwaterDepth` | `UnderwaterVolumeController` | underwater overlay blend |

## Tuning

- Time speed: `TimeOfDay.CycleDuration` and `TimeOfDay.TimeScale`.
- Environment clocks: consume `WorldDeltaSeconds` for continuous motion, `WorldDeltaHours` for chronology, and `PresentationDeltaSeconds` only for bounded transitions/transient envelopes. All freeze when Sol time is not moving forward; presentation time deliberately ignores the 10x/100x Sol multiplier.
- Day length: `TimeOfDay.DayRatio`; the four-season annual curve modifies `EffectiveDayRatio` and exposes `SunriseClockHour`/`SunsetClockHour`.
- Climate: `SolWeatherManager` derives `DailyFogTarget` deterministically from `Calendar.WorldDayIndex`, applies dawn-biased `FogDiurnalFactor`, and smooths `CurrentDailyFog` using presentation time. Tune the seasonal fog ranges, `climateSeed`, and `ClimateTransitionDurationSeconds`.
- Weather pacing: `SolWeatherManager.durationHoursRange`, `transitionDurationSeconds`, profile `weight`, and the four seasonal weight multipliers. Seasonal weights affect automatic selection only; `transitionHours` is obsolete.
- Default weather identities: Clear keeps light date-driven haze and calm water; Overcast is cloud-driven with little fog; Rain and Storm use ground-hugging mist; Storm adds maximum coordinated water turbulence and bounded lightning.
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
- The demo's generated water content is intentionally retained until an authored prefab/asset workflow is chosen.
- Surface atmosphere and underwater rendering still use one active camera/global state. True multi-camera and local-volume scoping remain deferred.
