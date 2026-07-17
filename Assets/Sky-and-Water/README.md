# Sol Sky & Water System

Unity 6 / URP 17 sky, time-of-day, weather, and water framework.

```
TimeOfDay
  - Calendar
  - skybox, sun, moon, planets, stars, aurora, eclipses
  - ambient and fog
        ^
SolWeatherManager
  - Clear / Overcast / Rain / Storm profiles
  - cloud cover, fog boost, dimming, lightning
        v
SolWaterManager
  - global water shader state
  - wind, rain, water level, wave time
  - WaterTileGrid, WaterVolume, ripples, buoyancy, underwater overlay
```

## Scene Setup

1. Add `TimeOfDay` and `Calendar` to one GameObject. Assign the sun/moon prefabs and set the `Sol/Skybox` material in Lighting.
2. Add `SolWaterManager`. Set `waterLevel`. Keep `windDirection` mostly horizontal, for example `(1, 0, 0.5)`.
3. Add `WaterTileGrid` and assign a `Sol/Water` material. Leave `autoWaterVolume` enabled for a generated gameplay query volume.
4. Add `WaterRippleManager`. Assign `Water/Shaders/Sol.RippleSim.shader` to `simShader` before making builds.
5. Add `SolWeatherManager`. References auto-resolve if the managers are in the scene.
6. Add `UnderwaterRendererFeature` to the URP Renderer asset and assign the underwater overlay material.
7. Add `UnderwaterVolumeController` to a persistent GameObject. Assign `playerTransform` for third-person cameras.
8. Enable Depth Texture and Opaque Texture on the URP asset.
9. Tag the gameplay camera as `MainCamera`, or assign cameras explicitly on `WaterTileGrid`, `WaterRippleManager`, and `UnderwaterVolumeController`.

## Public API

See `../../Documentation/PublicAPI.md` for code examples and the full scripting guide.

Common entry points:

| Class | Main use |
|---|---|
| `Sol.ToD.TimeOfDay` | world clock, sky authority, time skip/mutation events |
| `Sol.ToD.Calendar` | day/month/year state and season events |
| `SolWeatherManager` | weather profiles and transitions |
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
| `_Sol_GlobalWaterLevel` | `SolWaterManager` | global fallback water level |
| `_Sol_WaveFadeCenter` | `WaterTileGrid` | LOD and wave-fade origin |
| `_Sol_Ripples*` | `WaterRippleManager` | analytic fallback ripples |
| `_Sol_RippleSimTex`, `_Sol_RippleSimRegion`, `_Sol_RippleSimParams` | `WaterRippleManager` | GPU ripple sim |
| `_WaterSurfaceY` | `WaterVolume` | underwater overlay surface height |
| `_UnderwaterFactor`, `_UnderwaterDepth` | `UnderwaterVolumeController` | underwater overlay blend |

## Tuning

- Time speed: `TimeOfDay.CycleDuration` and `TimeOfDay.TimeScale`.
- Day length: `TimeOfDay.DayRatio`; seasonal variation can modify the effective day ratio.
- Weather pacing: `SolWeatherManager.durationHoursRange`, `transitionHours`, and profile `weight`.
- Water look: edit `M_Ocean` wave amplitude, frequency, steepness, detail, normals, foam, and reflection settings.
- Water mesh cost: lower `WaterTileGrid.tileResolution`, `gridRadius`, or `fullDetailRings`.
- Ripples: tune `WaterRippleManager.simResolution`, `simNormalStrength`, `simWaveSpeed`, and `simDamping`.
- Floating objects: tune `WaterBuoyancy.buoyancy`, `submersionDepth`, and `floatPoints`.

## Gotchas

- Assign `Sol.RippleSim.shader` on `WaterRippleManager` before builds; relying on `Shader.Find` is editor-only fallback behavior.
- `TimeOfDay` is `[ExecuteAlways]` and can dirty the assigned skybox material in edit mode.
- Multiple `WaterVolume` instances at different heights all push `_WaterSurfaceY`; the last writer wins for the overlay global.
- `SolWeatherManager` owns weather-driven water and sky fields while enabled.
- `WaterTileGrid` is `[ExecuteAlways]` and creates helper children named `_Tiles` and `_WaterVolume`.
- New materials may need to be selected once in the Inspector so Unity syncs shader keywords.
