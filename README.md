# Sols Sky & Water

Sols Sky & Water is a Unity 6 / URP environment stack for fantasy RPG and exploration projects. It combines a scene-owned world clock, configurable calendar, procedural sky, weather state machine, animated water, gameplay water queries, buoyancy, ripples, and an underwater overlay.

This repository is currently a clean extraction project, not a packaged Unity package. The demo scene is used to stabilize the system before it is moved into a larger game.

## Requirements

- Unity `6000.3.9f1`
- Universal Render Pipeline `17.3.0`
- Demo scene: `Assets/Scenes/SolsWeather_Demo.unity`
- Main system folder: `Assets/Sky-and-Water`

## What is included

- `TimeOfDay` and `Calendar` for game time, date, seasons, sunrise/sunset skips, lunar phases, eclipses, aurora nights, stars, cloud controls, skybox colors, fog, and ambient lighting.
- `SolWeatherManager` for weighted weather profiles, smooth transitions, rain, storm dimming, wind changes, and lightning flashes.
- `SolWaterManager` for global water shader state, wave time, wind, rain intensity, water level, moonlit night water, and reflection probe rebakes.
- `WaterTileGrid` for generated water tile grids, camera-following ocean surfaces, LOD rings, skirts, and an optional managed `WaterVolume`.
- `WaterVolume` and `SolWaterSurfaceSampler` for gameplay waterline queries that mirror the visual Gerstner wave shader.
- `WaterBuoyancy`, `WaterRippleSource`, and `WaterRippleManager` for floating rigidbodies and interactive water ripples.
- `UnderwaterVolumeController` and `UnderwaterRendererFeature` for camera/player underwater overlay blending in URP.
- `WaterSystemAdapter` implementing `Shared.Water.IWaterSystem` for game code that wants a small water-query interface.

## Quick start

1. Open the project in Unity `6000.3.9f1`.
2. Open `Assets/Scenes/SolsWeather_Demo.unity`.
3. Make sure the active URP renderer has the `UnderwaterRendererFeature` configured with the `Sol/UnderwaterOverlay` material.
4. Make sure the URP asset has Depth Texture and Opaque Texture enabled.
5. Press Play and use the scene objects to inspect the sky, weather, water grid, buoyancy, ripples, and underwater overlay.

For setup details, see [Assets/Sky-and-Water/README.md](Assets/Sky-and-Water/README.md).

## Public API guide

The public scripting surface is documented in [Documentation/PublicAPI.md](Documentation/PublicAPI.md). Start there for code examples covering:

- changing time and listening for time events,
- saving/restoring clock and calendar state,
- switching weather profiles,
- querying water height and underwater state,
- emitting ripples,
- reacting to underwater overlay state,
- using `Shared.Water.IWaterSystem` from gameplay code.

## Screenshots

No real in-engine screenshots are currently checked in. Before publishing this repo publicly, capture these from `SolsWeather_Demo.unity` and place them under [Documentation/Screenshots](Documentation/Screenshots/README.md):

- daytime ocean with sky/clouds,
- sunset or moonlit night,
- storm/rain water surface,
- underwater overlay,
- buoyant object or ripple interaction.

Do not use generated or stock images here; the screenshots should show the actual Unity scene.

## Integration notes

- Most runtime manager classes are in the global namespace. Time-of-day classes live in `Sol.ToD`; the gameplay water interface lives in `Shared.Water`.
- `WaterVolume.GetSurfaceHeight(worldPosition)` is the main gameplay entry point for animated water height.
- `TimeOfDay.ApplyTimeChange(...)` is the preferred mutation path for gameplay clock changes.
- `SolWeatherManager` owns weather-driven `TimeOfDay` and `SolWaterManager` fields while it is enabled.
- `WaterTileGrid` is `[ExecuteAlways]` and may serialize generated helper children in the demo scene.
- The old Fishbox-specific gameplay integrations are intentionally not included: save/load, sleep, NPC schedules, fishing, inventory, and audio should connect through the public events and adapter points.

## License

Licensed under the [Creative Commons Attribution-NonCommercial 4.0 International Public License](LICENSE) (`CC-BY-NC-4.0`).
