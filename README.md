# Sols Sky & Water

Elementa is a Unity 6 / URP environment stack for fantasy RPG and exploration projects. It combines a scene-owned world clock, configurable calendar, procedural sky, volumetric clouds, a weather state machine, spectral ocean and finite water bodies, gameplay water queries, buoyancy, and an auto-material terrain system.

This repository is currently a clean extraction project, not a packaged Unity package. The demo scene is used to stabilize the system before it is moved into a larger game.

## Requirements

- Unity `6000.3.9f1`
- Universal Render Pipeline `17.3.0`
- Demo scene: `Assets/Scenes/Elementa_Demo.unity`
- Main system folder: `Assets/Earth-Sky-Water`

## What is included

- `TimeOfDay` and `Calendar` for rewindable world date, forward-only player-time, four seasons, continuous seasonal day length, sunrise/sunset skips, lunar phases and spring/neap water response, eclipses, aurora nights, stars, cloud controls, skybox colors, fog, and ambient lighting.
- `SolEnvironmentCoordinator` for scene-owned RenderSettings, skybox-clone, and shader-global lifecycle cleanup.
- `SolWeatherManager` for weighted profiles and one immutable blended state shared by sky, fog, rain, water, wind, and lightning.
- `SolAtmosphereController` and an original URP RenderGraph feature with analytic Beer-Lambert height fog at Low/Medium and half-resolution shadowed sun/moon scattering at High.
- `SolRainVfxController` for bounded camera-following rain particles with gameplay exposure, shelter probing, and underwater suppression.
- `SolCloudRendererFeature` for curved-shell volumetric clouds with per-formation shape, temporal reconstruction, and world cloud shadows.
- `SolWaterWorld` and `SolWaterBody` for typed water surfaces: an infinite clipmap ocean plus bounded lakes, rivers, pools and waterfalls.
- `SolWaterRendererFeature` for the FFT ocean spectrum, screen-space reflections, caustics, in-water volumetrics, and underwater composition.
- `SolWaterProfile` and `SolWaterQualityProfile` for authored water appearance and a deterministic Low/Medium/High simulation budget.
- `ISolWaterQueryService` for gameplay surface sampling against the displacement that was actually rendered, with `SolWaterBuoyancy` and `SolWaterInteractor` on top.
- `SolTerrainShoreline` and `SolWaterWetness` for a realtime shoreline field and rain/shoreline terrain wetness.
- `SolLandscapeGroup` / `SolLandscapeProfile` for connected eight-material landscapes with live rules, local paint/exclusions, Scene View sculpting and scoped previews. `SolLandscapeDriver` retains legacy configuration compatibility. See [Landscape Designer](Documentation/Landscape%20Designer.md).
- `SolLightingDirector` for unified sun, moon and ambient authority with deterministic quality tiers.

## Quick start

1. Open the project in Unity `6000.3.9f1`.
2. Open `Assets/Scenes/Elementa_Demo.unity`.
3. Make sure the active URP renderer has `SolAtmosphereRendererFeature`, `SolCloudRendererFeature` and `SolWaterRendererFeature` installed.
4. Make sure the URP asset has Depth Texture and Opaque Texture enabled.
5. Open `Tools > Elementa > Control Panel`. Its native UI Toolkit pages provide profile and scene authoring, explicit temporary weather preview, quality controls and diagnostics. Overview offers repairs for supported setup issues. See [Elementa authoring](Documentation/Elementa%20Authoring.md) for editing targets, saving and preview behavior.
6. Press Play, or turn on Animate in Edit Mode from the panel's Time page, to watch the sky, weather and water run without entering play mode.

For setup details, see [Assets/Earth-Sky-Water/README.md](Assets/Earth-Sky-Water/README.md).

## Public API guide

The public scripting surface is documented in [Documentation/PublicAPI.md](Documentation/PublicAPI.md). Start there for code examples covering:

- changing time and listening for time events,
- saving/restoring clock and calendar state,
- switching weather profiles,
- querying water height and underwater state,
- emitting wakes and ripples,
- floating rigidbodies on the sampled surface.

## Screenshots

No real in-engine screenshots are currently checked in. I will eventually place them under [Documentation/Screenshots](Documentation/Screenshots/README.md):

- daytime ocean with sky/clouds,
- sunset or moonlit night,
- storm/rain water surface,
- underwater overlay,
- buoyant object or ripple interaction.

## Integration notes

- Time-of-day classes live in `Sol.ToD`, water in `Sol.Water`, lighting in `Sol.Lighting`, terrain in `Sol.Landscape`, and the shared environment state in `Sol.Environment`. Some older manager classes remain in the global namespace.
- `ISolWaterQueryService.TrySampleImmediate(position, out sample)` is the main gameplay entry point for animated water height.
- `TimeOfDay.ApplyTimeChange(...)` is the preferred mutation path for gameplay clock changes.
- Use `WorldDayIndex` for rewindable world chronology and `PlayerDaysElapsed` for forward-only progression metrics.
- `SolWeatherManager` owns weather-driven `TimeOfDay` fields while enabled, including deterministic seasonal/daily mist and season-biased automatic selection. Water reads the resulting state through `SolEnvironmentWorld` rather than being written to directly.
- The legacy Water 1 stack (`SolWaterManager`, `WaterTileGrid`, `WaterVolume`, ripple and underwater overlay components) has been retired. `Sol.Water` is the only water system.
- The old Fishbox-specific gameplay integrations are intentionally not included: save/load, sleep, NPC schedules, fishing, inventory, and audio should connect through the public events and adapter points.

## License

Licensed under the [Creative Commons Attribution-NonCommercial 4.0 International Public License](LICENSE) (`CC-BY-NC-4.0`).
