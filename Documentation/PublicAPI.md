# Public API Guide

This guide covers the scripting surface that other gameplay systems should use. Paths in this project are under `Assets/Sky-and-Water`.

Most water and weather classes are in the global namespace. Time-of-day classes use `Sol.ToD`. The small water-query interface uses `Shared.Water`.

## Time Of Day

Use `Sol.ToD.TimeOfDay` as the scene-owned clock and sky authority.

The system has two deliberately independent clocks:

- **World-date time** is `Calendar.Day/Month/Year` plus signed `WorldDayIndex`. It can advance or rewind and drives astronomy, aurora selection, and world-date events.
- **Player-time** is fractional `PlayerDaysElapsed`. Natural ticking and positive gameplay skips increase it; rewinds and arbitrary clock corrections never decrease or increase it.

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
| `RestoreTimeSnapshot(float normalizedTime, int day, int month, int year, long worldDayIndex, double playerDaysElapsed, Object source = null, string reason = "SaveLoad")` | restore civil time, world date, world offset, and player-time independently |
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
| `WorldDayIndex` | signed day offset from the configured starting date |
| `PlayerDaysElapsed` | forward-only fractional player-experienced days |
| `WorldDeltaSeconds` | canonical per-frame environment simulation seconds; already includes Unity scale, Sol scale, and pause |
| `WorldDeltaHours` | canonical civil hours for weather chronology |
| `PresentationDeltaSeconds` | Unity-scaled transition time; zero unless Sol time is moving forward and independent of the Sol multiplier |
| `CloudQuality` | active `Low`, `Medium`, or `High` pseudo-volume cloud tier |

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
        timeOfDay.PlayerTimeChanged += HandlePlayerTimeChanged;
        timeOfDay.Calendar.OnNewDay += HandleWorldDateChanged;
        timeOfDay.TimeScaleChanged += HandleTimeScaleChanged;
    }

    void OnDisable()
    {
        if (timeOfDay == null) return;

        timeOfDay.TimeChanged -= HandleTimeChanged;
        timeOfDay.HourChanged -= HandleHourChanged;
        timeOfDay.PlayerTimeChanged -= HandlePlayerTimeChanged;
        timeOfDay.Calendar.OnNewDay -= HandleWorldDateChanged;
        timeOfDay.TimeScaleChanged -= HandleTimeScaleChanged;
    }

    void HandleTimeChanged(TimeChangeResult result)
    {
        Debug.Log($"Time changed from {result.OldClockHour:0.0} to {result.NewClockHour:0.0}");
    }

    void HandleHourChanged(int oldHour, int newHour) {}
    void HandlePlayerTimeChanged(double oldDays, double newDays) {}
    void HandleWorldDateChanged(int day, int month, int year) {}
    void HandleTimeScaleChanged(float oldScale, float newScale) {}
}
```

`TimeSkipped` fires for explicit jumps/skips such as `AdvanceHours`, `SetClockHour`, and sunrise/sunset skips. Use it for systems that should react to non-natural time changes.

`DayChanged` is obsolete. Use `PlayerTimeChanged` for player-time and the calendar events for world-date changes. `TotalDaysElapsed` is also obsolete and returns only completed player days for compatibility.

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
| `WorldDayIndex` | signed world-date offset from the configured start |
| `PlayerDaysElapsed` | forward-only fractional player-time |
| `TotalDaysElapsed` | obsolete compatibility alias returning completed player days |
| `DayOfYear`, `DaysPerYear`, `MonthsPerYear` | calendar structure |
| `AverageMonthLength` | used by lunar timing |
| `CurrentSeason` | `Spring`, `Summer`, `Autumn`, or `Winter`; months 1/4/7/10 begin each season |
| `SeasonProgress`, `YearProgress` | normalized progress using authored month lengths |
| `IsFirstHalfOfYear`, `SeasonSign` | obsolete compatibility view of the former binary split |
| `AdvanceDay()`, `AdvanceDays(int count)` | move forward |
| `RewindDay()`, `RewindDays(int count)` | move backward |
| `SetDate(int day, int month, int year)` | set visible date |
| `SetDate(int day, int month, int year, long worldDayIndex, double playerDaysElapsed)` | restore world date and both counters independently |
| `ResetToStart()` | explicit new-game initialization of configured date and zeroed counters |

Events:

| Event | Payload |
|---|---|
| `OnNewDay` | `(day, month, year)` |
| `OnNewMonth` | `(month, year)` |
| `OnNewYear` | `(year)` |
| `SeasonChanged` | new `SolSeason` value |
| `OnSeasonChanged` | obsolete: `true` for first half of year, `false` for second half |

The calendar year starts in Spring: months 1–3 are Spring, 4–6 Summer, 7–9 Autumn, and 10–12 Winter. `TimeOfDay` uses `YearProgress` for a continuous annual day-length curve. `SunriseClockHour` and `SunsetClockHour` expose the current seasonal sunrise and sunset.

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

`TimeChangeResult` includes old/new normalized time and clock hour, old/new world-day indices, old/new player-time, `AppliedWorldHours`, `WorldDaysDelta`, `PlayerDaysDelta`, and `Changed`. `AppliedWorldHours` is positive for forward chronology, negative for rewinds, and zero for arbitrary clock corrections and snapshot restoration.

Environment consumers should use `WorldDeltaSeconds` for continuous environment motion and `WorldDeltaHours` for civil chronology. `PresentationDeltaSeconds` is Unity-scaled time that is zero while Sol time is paused, rewinding, or set to zero, but does not accelerate at 10x/100x. Weather transitions and transient flash decay use it to remain readable during time lapse.

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
| `TargetState` | logical profile state selected by world chronology |
| `IsTransitioning` | true while blending |
| `TransitionProgress` | shared 0-1 presentation progress for every weather channel |
| `transitionDurationSeconds` | Unity-scaled presentation duration; defaults to three seconds |
| `CurrentRainIntensity` | blended rain value for audio/VFX hooks |
| `CurrentDim` | blended storm dimming value |
| `CurrentState` | immutable effective `SolWeatherState` shared by all environment consumers |
| `WeatherStateChanged` | event fired when the effective blended state changes |
| `LightningTriggered` | one-shot event when a visible strike begins |
| `DailyFogTarget` | deterministic date-specific fog tendency before diurnal shaping |
| `CurrentDailyFog` | smoothed daily/diurnal contribution currently presented |
| `FogDiurnalFactor` | dawn-biased time-of-day climate multiplier |
| `ClimateTransitionDurationSeconds` | presentation-time smoothing duration, five seconds by default |
| `DailyCoverageOffset` | deterministic date-specific cloud-cover draw in -1..1, scaled per profile by `cloudCoverageVariance` |
| `CaptureSnapshot()` | serializable sequencer state: target, blend, hold, stream position |
| `RestoreSnapshot(in SolWeatherSnapshot)` | restores one; returns false on a version mismatch |
| `SnapshotVersion` | snapshot contract version |

While enabled, `SolWeatherManager` writes to:

- `TimeOfDay.WeatherCloudiness`
- `TimeOfDay.WeatherFogBoost`
- `TimeOfDay.WeatherDim`
- `TimeOfDay.WeatherLightningFlash`
- `TimeOfDay.WeatherCloudSpeedMul`
- `TimeOfDay.WeatherCloudErosion`
- `TimeOfDay.WeatherWindDirection`
- `SolWaterManager.rainIntensity`
- `SolWaterManager.windDirection`
- `SolWaterManager.windStrength`
- `SolWaterManager.globalWaveSpeedMultiplier`
- `SolWaterManager.waterTurbulence`

Disable `driveWind` or `driveWaves` if another system should own those water fields.

Positive `AdvanceHours` and forward day/sunrise/sunset skips advance weather chronology and update `TargetState`. `CurrentState` then settles toward that target using presentation time; a skip never simulates hours of visual blending in one frame. Rewinds and arbitrary clock corrections do not rewind weather because this release has no weather-history model.

`WeatherProfile.mistiness` moves atmosphere density toward the profile's low-mist height/falloff without independently adding density. `skyObscuration` controls sky-wide fog independently from surface/horizon extinction. `waterTurbulence` coordinates wave amplitude, detail, steepness, swell, foam, roughness, and drift. `lightningIntensity` is the profile peak used by the effective `LightningFlash`. These fields are included in `SolWeatherState` and share one weather transition progress.

Daily climate is a stable hash of `Calendar.WorldDayIndex` and `climateSeed`. Its squared
distribution favors low fog, then a dawn-biased curve and presentation-time smoothing are
applied. Spring, Summer, Autumn, and Winter have separate min/max fog ranges. A restored or
rewound date reproduces the same target.

Cloud cover carries the same treatment. `DailyCoverageOffset` is a second stable hash of the
world date, crossfaded into the next day's draw so cover does not step at midnight, and each
profile scales it by its own `cloudCoverageVariance`. This is what stops every Clear day from
rendering identically: shipped Clear varies between a cloudless sky and a lightly clouded one.

Weather selection is a seeded deterministic stream sharing `climateSeed` with the climate
model, so two runs of the same save produce the same weather. It is a forward-only sequence
rather than a function of the date, so a rewind does not un-advance it; capture the stream
position with `CaptureSnapshot()` and a reload replays the identical future.

Successor selection is weighted by plausibility as well as by season. `SolWeatherAdjacency`
derives how close two profiles are from their own values -- effective cover, precipitation
amount and phase, wind and dimming -- so fronts build and clear through intermediate states
instead of stepping straight from Clear to Blizzard. The weighting is multiplicative and
floored, so a seasonal weight of zero still means never and no state can dead-end.

Each weather profile also has four seasonal weight multipliers. They affect automatic cycling and `NextWeather()` only; direct `SetWeather(...)` remains exact and no default season makes a profile impossible.

The checked-in demo profiles are balanced around distinct responsibilities:

`cloudiness` is an influence toward overcast, not an absolute cover: 0 means "keep the sky
TimeOfDay authored". Effective cover is therefore
`lerp(authoredCover, 1, cloudiness) + cloudCoverageBias`, and it is the column to compare
profiles on. The shipped decks author `cloudCoverageDay: 0.437`, so `authoredCover` is 0.563.

| Profile | Effective cover | Visual target | Primary controls |
|---|---:|---|---|
| Clear | 0.06 ±0.12 | Open sky, usually carrying a little cloud; fully cloudless on roughly one day in seven | Coverage bias -0.50; no fog/mist/obscuration; wind 2.6 m/s, waves 0.85, turbulence 0.05 |
| Fair | 0.32 ±0.18 | Scattered fair-weather cumulus; the authored-deck identity Clear used to hold | Coverage bias -0.24; fog 0.02, mist 0.03; wind 4.4 m/s |
| Fog | 0.59 ±0.10 | Calm, bright, ground-hugging murk with a low grey ceiling | Visibility 180 m, mist 1.0, scattering 0.95; wind 1.2 m/s, dim 0.10 |
| Overcast | 0.79 ±0.08 | Soft continuous dry deck with long-distance visibility | Fog 0.05, mist 0.10; coverage and dimming carry the state |
| Drizzle | 0.87 ±0.06 | Light continuous precipitation under a stratus deck | Rain 0.22, fog 0.22, mist 0.42; wind 7.6 m/s |
| Snow | 0.95 ±0.05 | Bright, calm, heavy-falling snow | Snow bias 0.70, visibility 300 m, scattering 0.62; wind 5.2 m/s |
| Rain | 0.98 ±0.05 | Textured wet weather with low drifting mist | Rain 0.62, fog 0.40, mist 0.68; wind 11 m/s, waves 1.28, turbulence 0.50 |
| Storm | 1.00 ±0.03 | Dark, turbulent water and ground-hugging mist | Fog 0.55, mist 0.82; wind 21.2 m/s, waves 1.85, turbulence 1.0, lightning 0.28 |
| Blizzard | 1.00 | Bright whiteout rather than a dark one | Snow bias 1.0, visibility 55 m, fog 2.0, obscuration 1.0, dim only 0.24; wind 22 m/s |

For custom profiles, prefer increasing `dim`, wind, waves, and rain before pushing both `fogBoost` and `skyObscuration`. The latter combination obscures geometry and the sky simultaneously and is best treated as a deliberate whiteout effect.

## Rain VFX

`SolRainVfxController` creates bounded built-in particle systems at runtime, follows the active main camera, and consumes `SolWeatherManager.CurrentState`.

| API | Use |
|---|---|
| `SetRainExposure(float exposure)` | set gameplay exposure from 0 sheltered to 1 exposed |
| `ResetRainExposure()` | restore authored full exposure |
| `RainExposure` | gameplay-authored exposure value |
| `ShelterExposure` | current upward-probe result after smoothing |
| `EffectiveRainIntensity` | weather × gameplay × shelter × underwater exposure |
| `ActiveCamera` | camera currently followed by the emitter |

The optional shelter probe casts upward using the configured layer mask. Entering water suppresses the particle presentation; surface rain roughness and ripples remain water-system responsibilities. A world pause freezes live drops without clearing them, while dry weather stops emission and lets existing drops expire. Time-lapse simulation is capped and emission-compensated so particle density remains bounded.

## Sol Atmosphere

`SolAtmosphereController` publishes exponential-height extinction, sky haze, weather noise, Cornette-Shanks directional scattering, lightning state, and the active sun/moon light. `SolAtmosphereRendererFeature` applies the selected quality before transparents in URP RenderGraph. Sol water and rain share the analytic `SolAtmosphere.hlsl` path so transparent objects receive matching fog without a second screen-space pass.

Quality behavior:

- `Low`: analytic Beer-Lambert extinction without procedural noise.
- `Medium`: analytic extinction with weather-driven noise; this is the default desktop tier.
- `High`: transient half-resolution, shadowed directional raymarch with at most 32 steps, transmittance early exit, depth-aware spatial filtering, and four-tap depth-aware upsampling. High has no temporal history.

| API | Use |
|---|---|
| `Quality` | current `Low`, `Medium`, or `High` quality |
| `SetQuality(SolAtmosphereQuality quality)` | apply a runtime quality override without mutating the profile asset |
| `ClearQualityOverride()` | return to the profile/component-authored quality |
| `CurrentFogColor` / `CurrentDensity` | effective values currently sent to shaders |
| `CurrentDominantLight` | sun or moon currently supplying directional atmosphere lighting |
| `UsesVolumetricLighting` | true when the High raymarch path is selected |

`SolAtmosphereProfile` exposes `phaseAnisotropy`, directional/shadowed scattering, `skyFogStrength`, separate zenith/horizon strengths, `mistBaseHeight` (default `1.5`) and `mistHeightFalloff` (default `0.12`), fog saturation, ambient and lightning scattering, maximum scattering luminance, raymarch distance/steps/jitter, bilateral depth threshold, and High spatial-filter strength. Horizon strength `1` matches distant surface optical depth while the default zenith strength `0.12` preserves overhead sky and cloud detail. The former serialized `scatteringPower` value remains stored but is no longer used.

`TimeOfDay` exposes `SunLight`, `MoonLight`, and `DominantAtmosphereLight`. The dominant enabled directional light is selected using intensity/luminance and twilight hysteresis, then assigned to `RenderSettings.sun` so URP main-light shadows and atmospheric scattering agree. `SolEnvironmentCoordinator` restores the authored `RenderSettings.sun` when environment ownership ends.

`TimeOfDay.LunarTideFactor` is `1` at new and full moon and `0` at the quarter moons, continuously interpolated between them. Water combines it with `MoonIllumination` in `_Sol_WaterDynamics`: new/full moons receive the same subtle spring-tide swell/foam response, while only illuminated phases brighten reflections. This is visual-only and never changes `waterLevel` or a volume's mean surface height.

Add `SolAtmosphereRendererFeature` to the active URP renderer while keeping SSAO and `UnderwaterRendererFeature`. The feature skips preview/reflection cameras, overlays, and surface atmosphere while underwater. Legacy `RenderSettings.fog` is retained as a fallback and restored when the atmosphere controller releases ownership.

Only the sun/moon directional authority participates in High-quality shadowed scattering. Point/spot lights, localized density volumes, temporal reprojection, quarter-resolution checkerboarding, and volumetric cloud modeling are deferred.

## Global Water State

Use `SolWaterManager` for shared water state.

| API | Use |
|---|---|
| `SolWaterManager.Instance` | active manager |
| `waterLevel` | global fallback surface Y |
| `windDirection` / `WindDirectionNormalized` | world-space XZ wind |
| `windStrength` | wave and drift influence |
| `globalWaveSpeedMultiplier` | shared wave speed |
| `waterTurbulence` | normalized weather disorder applied to geometry, foam, roughness, and drift |
| `lunarResponseStrength` | subtle spring/neap response scale; defaults to `0.12` |
| `LunarTideFactor` | current new/full versus quarter-moon signal from `TimeOfDay` |
| `MoonIllumination` | current lunar lighting fraction |
| `rainIntensity` | shader rain/ripple intensity |
| `WaveTime` | accumulated shared water time |

The manager pushes global shader properties with the `_Sol_` prefix. `_Sol_WaterDynamics` contains turbulence, lunar tide factor, moon illumination, and lunar response strength. Per-material wave look stays on the assigned water material, and the C# surface sampler mirrors every geometric turbulence/lunar multiplier used by HLSL.

Strong wind steering is intentionally saturated so authored crossing-wave directions remain visible during storms. Fixed per-wave phase offsets prevent all wave families from cresting together, while turbulence shifts energy away from one dominant swell toward crossing and detail waves. Foam, normal maps, and caustics use the same canonical `_Sol_WaveTime`, so visual detail freezes with the water simulation.

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
- `Calendar.WorldDayIndex`
- `Calendar.PlayerDaysElapsed`
- `SolWeatherManager.CaptureSnapshot()`, if weather should persist
- `SolWaterManager.waterLevel`, only if your game changes water level at runtime

Restore time and date with:

```csharp
timeOfDay.RestoreTimeSnapshot(
    savedTime,
    savedDay,
    savedMonth,
    savedYear,
    savedWorldDayIndex,
    savedPlayerDaysElapsed,
    this);
weather.RestoreSnapshot(savedWeatherSnapshot);
```

`RestoreSnapshot` returns false without changing anything when the snapshot's
`SnapshotVersion` does not match, and resolves its target by profile name so reordering a
scene's selection list cannot silently retarget a save. It restores the sequencer position,
so the restored world replays the same future weather rather than a fresh roll.

`weather.SetWeather(savedWeatherName, instant: true)` remains as the lossy fallback: it puts
the right weather on screen but starts a new random sequence from there.

The legacy five-value restore overload remains only as an obsolete compatibility API. There is currently no project save system, so no legacy save migration is implemented by this sprint.

## Common Integration Mistakes

- Do not write `TimeOfDay.CurrentTime` every frame from gameplay code. Use the request/mutation methods.
- Do not query water height from `SolWaterManager.waterLevel` when a `WaterVolume` exists; that skips animated waves.
- Do not let two systems fight over weather-owned water fields. Disable `driveWind` or `driveWaves` if needed.
- Do not forget to assign `WaterRippleManager.simShader` for builds.
- Do not edit `SolWaterWaves.hlsl` without updating `SolWaterSurfaceSampler`.
- Do not use fake screenshots in public docs. Capture the actual demo scene.
- Sol shader globals remain scene-wide. Per-volume water fade/surface state and per-camera underwater state are deferred architecture work.
- Generated demo water tiles remain intentionally deferred until an authored prefab/asset workflow is selected.
# Water 2 / environment authority

The replacement environment API is documented in `Documentation/Water2 Overhaul.md`. Its principal public contracts are:

- `Sol.Environment.SolEnvironmentWorld`, `SolEnvironmentState`, `SolEnvironmentCommand`, and `SolEnvironmentSnapshot`.
- `Sol.Water.SolWaterProfile` exposes physical refraction strength/distance/dispersion and maximum viewport offset, clarity, projected-caustic texture/strength/scale, world-space foam texture scale/contrast/brightness, dynamic sky-reflection blending plus fallback/horizon energy controls, and optional logical-world shoreline depth/distance data with shallow-wave attenuation, contact controls, and shore-directed breaker strength/width/wavelength/speed/choppiness/foam alongside the existing optics, waves, shoreline, and underwater controls.
- `Sol.Water.SolWaterQualityProfile` exposes SSR traversal distance/thickness/edge fade, binary refinement, temporal history weight, depth/normal validation tolerances, and maximum reflected luminance.
- `Sol.Water.Rendering.SolWaterDebugMode` selects raw SSR, validated SSR, confidence, dynamic-sky fallback, pre-texture foam confidence, accepted refraction, or projected-caustic visualization on `SolWaterRendererFeature`.
- `Sol.Environment.SolEnvironmentCameraRegistry` and `SolWorldOriginService`.
- `Sol.Water.SolWaterWorld`, `SolWaterBody`, `SolWaterBodyId`, `SolWaterCommand`, and `SolWaterSnapshot`.
- `Sol.Water.ISolWaterQueryService` and `SolWaterSurfaceSample`.
- `Sol.Water.ISolWaterGeometry` and `SolWaterGeometrySample` are the finite-body bridge used by `SolRiverGeometry`, `SolLakeGeometry`, and `SolWaterfallGeometry`. Generated geometry supplies bounded mesh rendering plus matching position, normal, depth, flow, and foam data to the existing query service.
- `Sol.Hydrology.SolHydrologyAsset`, `SolHydrologyWorld`, `SolHydrologyCommand`, and `SolHydrologySnapshot`.
- `Sol.Streaming.IEnvironmentCellProvider` and `SolEnvironmentCellId`.

These APIs are a clean break. The one-way editor converter is the migration boundary; no runtime compatibility facade is provided.

The active-terrain shoreline baker is available from `Tools > Sol Environment > Water 2 > Bake Active Terrain Shoreline Data`. It creates an `RGHalf` Water 2 asset whose red channel is signed vertical water depth and green channel is signed horizontal distance from shore, then assigns its logical-world mapping to the active ocean profile.
