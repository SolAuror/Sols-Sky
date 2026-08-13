## External review summary

The architecture is coherent and well documented:

- `TimeOfDay` owns the clock, calendar integration, sky, lighting, fog, celestial bodies, and eclipses.
- `SolWeatherManager` injects weather modulation into both sky and water.
- `SolWaterManager` owns global water state.
- `WaterVolume` plus `SolWaterSurfaceSampler` provide a strong CPU/GPU wave-sync contract.
- Water features are modular: grid, buoyancy, ripples, underwater rendering, and an interface adapter.

The main weakness is global-state and lifecycle management, not the core rendering math.

## Priority findings

1. **Calendar rewind can desynchronize date and elapsed days**

`RewindDay()` clamps `totalDaysElapsed` to zero while still moving the visible date backward. Once the starting point is reached, the calendar date can continue rewinding while `TotalDaysElapsed` remains unchanged. That affects save/load, `DayChanged`, lunar phase, and deterministic aurora selection.

[Calendar.cs:109](B:/Projects/Unity/SkySystem/Sols-Sky-Repo/Assets/Sky-and-Water/TimeOfDay/Calendar.cs:109)

2. **Runtime-created water volumes may use stale wave parameters**

`WaterTileGrid` creates a `WaterVolume`, then assigns `waterMaterial` afterward. `WaterVolume.ReadMaterial()` runs during enable/validation, but not after that runtime assignment. A dynamically created grid can therefore render one wave set while gameplay queries use defaults.

[WaterTileGrid.cs:259](B:/Projects/Unity/SkySystem/Sols-Sky-Repo/Assets/Sky-and-Water/Water/WaterTileGrid.cs:259)  
[WaterTileGrid.cs:286](B:/Projects/Unity/SkySystem/Sols-Sky-Repo/Assets/Sky-and-Water/Water/WaterTileGrid.cs:286)

3. **Disabling a water grid leaves its managed volume alive**

`OnDisable()` clears tiles but does not destroy the generated `_WaterVolume`. The disabled grid can remain in the static volume registry and continue participating in gameplay queries.

[WaterTileGrid.cs:160](B:/Projects/Unity/SkySystem/Sols-Sky-Repo/Assets/Sky-and-Water/Water/WaterTileGrid.cs:160)  
[WaterTileGrid.cs:291](B:/Projects/Unity/SkySystem/Sols-Sky-Repo/Assets/Sky-and-Water/Water/WaterTileGrid.cs:291)

4. **Global shader and RenderSettings state is not consistently cleaned up**

`SolWaterManager.OnDisable()` only clears its singleton reference; its `_Sol_*` shader globals remain. `TimeOfDay` also writes directly to `RenderSettings` and the shared skybox material without restoring previous state. Scene transitions or alternate environment systems could inherit stale lighting, fog, wind, or water values.

[WaterManager.cs:168](B:/Projects/Unity/SkySystem/Sols-Sky-Repo/Assets/Sky-and-Water/Management/WaterManager.cs:168)  
[TimeofDay.cs:979](B:/Projects/Unity/SkySystem/Sols-Sky-Repo/Assets/Sky-and-Water/TimeOfDay/TimeofDay.cs:979)

5. **Weather disable does not restore all owned water fields**

The weather manager resets rain, but leaves wind strength/direction and wave speed untouched, despite the documentation saying the system returns the scene to neutral.

[WeatherManager.cs:210](B:/Projects/Unity/SkySystem/Sols-Sky-Repo/Assets/Sky-and-Water/Management/WeatherManager.cs:210)

6. **Multiple-water and multiple-camera support is only partial**

The system relies on globals for wave fade origin, underwater state, and surface height. Multiple grids or volumes will effectively use “last writer wins.” Celestial bodies cache `Camera.main` once, while the underwater renderer applies one global factor to every camera using that renderer.

[CelestialBody.cs:89](B:/Projects/Unity/SkySystem/Sols-Sky-Repo/Assets/Sky-and-Water/TimeOfDay/ToD.CelestialBody.cs:89)

7. **Several water tuning fields appear unused**

`rainRoughnessBoost`, `rainNormalBoost`, and `rainReflectionDampen` are exposed on `SolWaterManager`, but the shader uses hardcoded values instead. These Inspector controls currently give the impression of configurability without actually affecting output.

[WaterManager.cs:78](B:/Projects/Unity/SkySystem/Sols-Sky-Repo/Assets/Sky-and-Water/Management/WaterManager.cs:78)

## Scene/workflow concern

The demo scene contains approximately 961 serialized water tile GameObjects and is about 4 MB. The grid rebuilds these at runtime, so the scene is carrying a large generated artifact that is largely recreated on load.

[SolsWeather_Demo.unity:594](B:/Projects/Unity/SkySystem/Sols-Sky-Repo/Assets/Scenes/SolsWeather_Demo.unity:594)

Also, “seasons” currently means a binary first-half/second-half year sign. There are no distinct seasonal definitions or seasonal weather rules.

## Overall assessment

This is a promising extraction/prototype with unusually good documentation and a solid wave synchronization approach. Before treating it as production-ready, I would prioritize:

1. lifecycle/global-state cleanup;
2. calendar rewind correctness;
3. runtime water-volume synchronization;
4. tests for scene reloads, multiple volumes, camera switching, and save/load;
5. removing or formalizing generated water content in the demo scene.

No automated tests or validation assets were found.