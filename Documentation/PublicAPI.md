# Public API Guide

This guide covers the scripting surface that other gameplay systems should use. Paths in this project are under `Assets/Sky-and-Water`.

Most water and weather classes are in the global namespace. Time-of-day classes use `Sol.ToD`. The small water-query interface uses `Shared.Water`.

## Time Of Day

Use `Sol.ToD.TimeOfDay` as the scene-owned clock and sky authority.

```csharp
using Sol.ToD;
using UnityEngine;

public sealed class RestAtCampfire : MonoBehaviour
{
    [SerializeField] TimeOfDay timeOfDay;

    public void RestUntilMorning()
    {
        timeOfDay ??= TimeOfDay.ResolveInstance();
        timeOfDay.SkipToNextSunrise();
    }
}
```

Preferred mutation methods:

| API | Use |
|---|---|
| `ApplyTimeChange(TimeChangeRequest request)` | canonical command-style time mutation |
| `SetClockHour(float hour, Object source = null, string reason = null)` | set civil clock hour, 0-24 |
| `SetNormalizedTime(float normalizedTime, Object source = null, string reason = null)` | set normalized day progress, 0-1 |
| `AdvanceHours(float hours, Object source = null, string reason = null)` | move time forward and advance calendar days |
| `RewindHours(float hours, Object source = null, string reason = null)` | move time backward and rewind calendar days |
| `SetTimeScale(float scale)` | set game-world clock speed without changing `UnityEngine.Time.timeScale` |
| `SetPaused(bool isPaused)` | pause or resume the world clock |
| `RestoreTimeSnapshot(float normalizedTime, int day, int month, int year, int totalDays, Object source = null, string reason = "SaveLoad")` | restore clock and calendar from save data |
| `SkipToNextSunrise()` / `SkipToNextSunset()` | jump to the next sunrise/sunset |
| `SkipForwardOneDay()` / `SkipBackwardOneDay()` | move by full calendar days |

Useful read-only state:

| API | Meaning |
|---|---|
| `ClockHour` | civil hour, 0-24 |
| `CurrentTime` | normalized day progress, 0-1 |
| `DayFactor` | 0 at night, 1 near zenith |
| `SunDirection` / `MoonDirection` | world-space light directions |
| `IsDaytime` | true when sun is above the computed horizon |
| `EffectiveDayRatio` | day length after seasonal variation |
| `LunarPhase` | 0 new moon, 0.5 full moon |
| `MoonIllumination` | 0-1 lit fraction |
| `SolarEclipseStrength` / `LunarEclipseStrength` | eclipse intensity |
| `IsEclipse` | true when either eclipse is active |
| `Calendar` | paired `Calendar` component |

Events:

```csharp
using Sol.ToD;
using UnityEngine;

public sealed class TimeHud : MonoBehaviour
{
    [SerializeField] TimeOfDay timeOfDay;

    void OnEnable()
    {
        timeOfDay ??= TimeOfDay.ResolveInstance();
        if (timeOfDay == null) return;

        timeOfDay.TimeChanged += HandleTimeChanged;
        timeOfDay.HourChanged += HandleHourChanged;
        timeOfDay.DayChanged += HandleDayChanged;
        timeOfDay.TimeScaleChanged += HandleTimeScaleChanged;
    }

    void OnDisable()
    {
        if (timeOfDay == null) return;

        timeOfDay.TimeChanged -= HandleTimeChanged;
        timeOfDay.HourChanged -= HandleHourChanged;
        timeOfDay.DayChanged -= HandleDayChanged;
        timeOfDay.TimeScaleChanged -= HandleTimeScaleChanged;
    }

    void HandleTimeChanged(TimeChangeResult result)
    {
        Debug.Log($"Time changed from {result.OldClockHour:0.0} to {result.NewClockHour:0.0}");
    }

    void HandleHourChanged(int oldHour, int newHour) {}
    void HandleDayChanged(int oldTotalDays, int newTotalDays) {}
    void HandleTimeScaleChanged(float oldScale, float newScale) {}
}
```

`TimeSkipped` fires for explicit jumps/skips such as `AdvanceHours`, `SetClockHour`, and sunrise/sunset skips. Use it for systems that should react to non-natural time changes.

## Calendar

`Calendar` lives on the same GameObject as `TimeOfDay`.

```csharp
using Sol.ToD;
using UnityEngine;

public sealed class CalendarLabel : MonoBehaviour
{
    [SerializeField] Calendar calendar;

    void Start()
    {
        Debug.Log(calendar.DateString);
    }
}
```

Core API:

| API | Use |
|---|---|
| `Day`, `Month`, `MonthName`, `Year` | current date |
| `DateString` | formatted date string |
| `TotalDaysElapsed` | save-friendly running day count |
| `DayOfYear`, `DaysPerYear`, `MonthsPerYear` | calendar structure |
| `AverageMonthLength` | used by lunar timing |
| `IsFirstHalfOfYear`, `SeasonSign` | simple seasonal split |
| `AdvanceDay()`, `AdvanceDays(int count)` | move forward |
| `RewindDay()`, `RewindDays(int count)` | move backward |
| `SetDate(int day, int month, int year)` | set visible date |
| `SetDate(int day, int month, int year, int totalDays)` | restore visible date plus running count |

Events:

| Event | Payload |
|---|---|
| `OnNewDay` | `(day, month, year)` |
| `OnNewMonth` | `(month, year)` |
| `OnNewYear` | `(year)` |
| `OnSeasonChanged` | `true` for first half of year, `false` for second half |

## Time Change Requests

`TimeChangeRequest` is a small command object for time mutation. Use it when you want one validation/replication shape for different time actions.

```csharp
using Sol.ToD;
using UnityEngine;

public sealed class SleepSystem : MonoBehaviour
{
    [SerializeField] TimeOfDay timeOfDay;

    public TimeChangeResult Sleep(float hours)
    {
        var request = TimeChangeRequest.AdvanceHours(hours, this, "Sleep");
        return timeOfDay.ApplyTimeChange(request);
    }
}
```

Factory methods:

- `AdvanceHours(float hours, Object source = null, string reason = null)`
- `RewindHours(float hours, Object source = null, string reason = null)`
- `SetClockHour(float hour, Object source = null, string reason = null)`
- `SetNormalizedTime(float normalizedTime, Object source = null, string reason = null)`
- `SetTimeScale(float scale, Object source = null, string reason = null)`
- `SetPaused(bool paused, Object source = null, string reason = null)`

`TimeChangeResult` includes old/new normalized time, old/new clock hour, old/new total days, `DaysDelta`, and `Changed`.

## Weather

Use `SolWeatherManager` for high-level weather changes.

```csharp
using UnityEngine;

public sealed class WeatherConsole : MonoBehaviour
{
    [SerializeField] SolWeatherManager weather;

    public void ForceStorm()
    {
        weather ??= SolWeatherManager.Instance;
        weather?.SetWeather("Storm", instant: true);
    }
}
```

Core API:

| API | Use |
|---|---|
| `SetWeather(int index, bool instant = false)` | transition to profile index |
| `SetWeather(string profileName, bool instant = false)` | transition by case-insensitive profile name |
| `NextWeather()` | pick the next weighted weather profile |
| `WeatherChanged` | event fired when a new target profile is selected |
| `TargetProfile` | current target or held profile |
| `IsTransitioning` | true while blending |
| `CurrentRainIntensity` | blended rain value for audio/VFX hooks |
| `CurrentDim` | blended storm dimming value |

While enabled, `SolWeatherManager` writes to:

- `TimeOfDay.WeatherCloudiness`
- `TimeOfDay.WeatherFogBoost`
- `TimeOfDay.WeatherDim`
- `TimeOfDay.WeatherLightningFlash`
- `TimeOfDay.WeatherCloudSpeedMul`
- `SolWaterManager.rainIntensity`
- `SolWaterManager.windDirection`
- `SolWaterManager.windStrength`
- `SolWaterManager.globalWaveSpeedMultiplier`

Disable `driveWind` or `driveWaves` if another system should own those water fields.

## Global Water State

Use `SolWaterManager` for shared water state.

| API | Use |
|---|---|
| `SolWaterManager.Instance` | active manager |
| `waterLevel` | global fallback surface Y |
| `windDirection` / `WindDirectionNormalized` | world-space XZ wind |
| `windStrength` | wave and drift influence |
| `globalWaveSpeedMultiplier` | shared wave speed |
| `rainIntensity` | shader rain/ripple intensity |
| `WaveTime` | accumulated shared water time |

The manager pushes global shader properties with the `_Sol_` prefix. Per-material wave look stays on the assigned water material.

## Water Volumes And Height Queries

`WaterVolume` is the gameplay water body. It registers itself statically and mirrors the shader waves in C#.

```csharp
using UnityEngine;

public sealed class SwimProbe : MonoBehaviour
{
    void Update()
    {
        WaterVolume volume = WaterVolume.FindVolumeXZ(transform.position);
        if (volume == null) return;

        float surface = volume.GetSurfaceHeight(transform.position);
        bool underwater = volume.IsUnderwater(transform.position);
        Debug.DrawLine(transform.position, new Vector3(transform.position.x, surface, transform.position.z));
    }
}
```

Core API:

| API | Use |
|---|---|
| `WaterVolume.FindVolume(Vector3 worldPos)` | find a volume if the point is inside XZ and below the animated surface |
| `WaterVolume.FindVolumeXZ(Vector3 worldPos)` | find a volume by XZ footprint only |
| `WaterVolume.DistanceToNearestWaterXZ(Vector3 worldPos)` | distance to nearest volume footprint |
| `WaterVolume.VolumeCount` | active registered volumes |
| `SurfaceY` | flat top-face waterline plus `surfaceOffset` |
| `GetSurfaceHeight(Vector3 worldPos)` | animated wave surface height |
| `ContainsPoint(Vector3 worldPos)` | collider bounds check |
| `IsUnderwater(Vector3 worldPos)` | bounds plus animated surface check |

Events:

| Event | Payload |
|---|---|
| `TriggerEntered` | `Collider` |
| `TriggerExited` | `Collider` |
| `WaterDepthUpdated` | `(Collider tracked, float depth, bool aboveSwimThreshold)` |

Use `GetSurfaceHeight` for gameplay physics, swimming checks, and VFX placement. It is the main waterline entry point.

## Water Surface Sampler

`SolWaterSurfaceSampler` is the lower-level static sampler used by `WaterVolume` and `WaterBuoyancy`.

| API | Use |
|---|---|
| `GetSurfaceHeight(WaterVolume volume, Vector3 worldPos)` | evaluate the full animated surface |
| `GetWaveHeight(Vector2 worldXZ, in WaveSettings settings)` | evaluate waves from explicit settings |
| `EvaluateDisplacement(Vector2 samplePosXZ, in WaveSettings settings)` | low-level Gerstner displacement |
| `GetSurfaceDriftVelocity(WaterVolume volume)` | wind-driven drift velocity for floating objects |
| `SetWaveFadeCenter(Vector2 centerXZ)` / `ClearWaveFadeCenter()` | normally driven by `WaterTileGrid` |

Do not change the sampler formula without making the same change in `Water/Shaders/SolWaterWaves.hlsl`.

## Water Tile Grid

`WaterTileGrid` generates and manages the visible water mesh.

| API | Use |
|---|---|
| `RebuildAll()` | destroy and rebuild tiles in editor or play mode |
| `followCamera` | recenter grid on tracked camera |
| `trackedCamera` | explicit camera; falls back to `Camera.main` |
| `syncWaterLevel` | read `SolWaterManager.waterLevel` each frame |
| `autoWaterVolume` | create and resize a managed child `WaterVolume` |

When `autoWaterVolume` is enabled, the generated `_WaterVolume` child is enough for buoyancy and swimming queries across the grid.

## Ripples

Use `WaterRippleManager.Instance.Emit(...)` for manual ripples.

```csharp
using UnityEngine;

public sealed class SplashOnImpact : MonoBehaviour
{
    void OnCollisionEnter(Collision collision)
    {
        WaterRippleManager.Instance?.Emit(collision.contacts[0].point, 0.5f);
    }
}
```

`WaterRippleSource` is the component path. Add it to a player, NPC, boat, or floating object that should emit ripples while moving through water.

Important setup:

- Keep one `WaterRippleManager` in the scene.
- Assign `Sol.RippleSim.shader` to `simShader` for builds so the hidden shader is not stripped.
- Use `debugAutoSplash` and `debugShowSimTexture` to verify the simulation in Play mode.

## Buoyancy

Add `WaterBuoyancy` to a Rigidbody object that should float.

| API | Use |
|---|---|
| `buoyancy` | force multiplier; below 1 sinks, above 1 floats |
| `submersionDepth` | stiffness/depth range for full force |
| `floatPoints` | optional explicit sample points |
| `SubmergedFraction` | 0-1 runtime wetness |
| `IsInWater` | true while at least one float point is submerged |

If `floatPoints` is empty, the component derives four corner points from collider bounds during `Awake`.

## Underwater Overlay

`UnderwaterVolumeController` drives global overlay properties based on camera/player depth.

```csharp
using UnityEngine;

public sealed class UnderwaterAudio : MonoBehaviour
{
    [SerializeField] UnderwaterVolumeController underwater;

    void OnEnable()
    {
        underwater.UnderwaterStateChanged += HandleUnderwaterState;
    }

    void OnDisable()
    {
        underwater.UnderwaterStateChanged -= HandleUnderwaterState;
    }

    void HandleUnderwaterState(bool isUnderwater)
    {
        Debug.Log(isUnderwater ? "Muffle audio" : "Restore audio");
    }
}
```

Core API:

| API | Use |
|---|---|
| `trackedCamera` | explicit camera; falls back to `Camera.main` |
| `playerTransform` | optional third-person fallback |
| `IsUnderwater` | current overlay state |
| `UnderwaterStateChanged` | event when overlay enters/exits underwater state |

Also add `UnderwaterRendererFeature` to the URP Renderer Asset and assign the underwater overlay material.

## Water Interface Adapter

`WaterSystemAdapter` implements `Shared.Water.IWaterSystem`. Use this when gameplay code should depend on a small interface instead of concrete Sol classes.

```csharp
using Shared.Water;
using UnityEngine;

public sealed class FishingProbe : MonoBehaviour
{
    [SerializeField] MonoBehaviour waterSystemBehaviour;
    IWaterSystem _water;

    void Awake()
    {
        _water = waterSystemBehaviour as IWaterSystem;
    }

    public bool CanCastLine(Vector3 bobberPosition)
    {
        return _water != null && bobberPosition.y >= _water.GetWaterline(bobberPosition);
    }
}
```

Interface:

```csharp
namespace Shared.Water
{
    public interface IWaterSystem
    {
        float GetWaterline(UnityEngine.Vector3 position);
        bool IsUnderwater(UnityEngine.Vector3 position);
    }
}
```

`WaterSystemAdapter` delegates to `WaterVolume.FindVolumeXZ` first, then falls back to `SolWaterManager.waterLevel`, then `0`.

## Celestial Bodies

`CelestialBodyConfig` is a `ScriptableObject` created from `Assets > Create > Sol > Celestial Body Config`.

Use it to configure sun, moon, or planet identity, orbit period, tilt, phase, disc visuals, optional surface texture, attached light behavior, and eclipse tinting. `TimeOfDay.GetTertiaryPlanet(int index)` returns spawned tertiary `CelestialBody` instances at runtime.

## Save/Load Checklist

For a basic save, store:

- `TimeOfDay.CurrentTime`
- `Calendar.Day`
- `Calendar.Month`
- `Calendar.Year`
- `Calendar.TotalDaysElapsed`
- current weather profile name or index, if weather should persist
- `SolWaterManager.waterLevel`, only if your game changes water level at runtime

Restore time and date with:

```csharp
timeOfDay.RestoreTimeSnapshot(savedTime, savedDay, savedMonth, savedYear, savedTotalDays, this);
weather.SetWeather(savedWeatherName, instant: true);
```

## Common Integration Mistakes

- Do not write `TimeOfDay.CurrentTime` every frame from gameplay code. Use the request/mutation methods.
- Do not query water height from `SolWaterManager.waterLevel` when a `WaterVolume` exists; that skips animated waves.
- Do not let two systems fight over weather-owned water fields. Disable `driveWind` or `driveWaves` if needed.
- Do not forget to assign `WaterRippleManager.simShader` for builds.
- Do not edit `SolWaterWaves.hlsl` without updating `SolWaterSurfaceSampler`.
- Do not use fake screenshots in public docs. Capture the actual demo scene.
