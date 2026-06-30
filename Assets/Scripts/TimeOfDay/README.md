# Sol Time of Day System

**Full documentation:** [Documentation/TimeOfDay.md](../../Documentation/TimeOfDay.md)

---

## Quick Summary

Drives day/night cycle, weather, and celestial bodies for a Unity 6 / URP scene.
- **`TimeOfDay`** — master clock; exposes `DayFactor`, `Hour`, `SunDirection`, `MoonDirection`, calendar events.
- **`EnvironmentManager`** — applies lighting, skybox, fog, and reflection probe blends per time of day.
- **`WeatherManager`** — state machine (Clear ? Cloudy ? Rain ? Storm ? Snow); raises C# events on transition.
- **`CelestialBody`** — orbit solver for sun and moon transforms.

## Scene Requirements

- One `TimeOfDay` component in the scene
- `EnvironmentManager` for lighting/fog blends
- Optional: `WeatherManager`, `CelestialBody` (sun/moon)

## Quick Setup

1. Add **TimeOfDay** component ? set `DayDuration` (seconds per full day cycle)
2. Add **EnvironmentManager** ? assign `TimeOfDay` reference ? configure Day/Night lighting curves
3. Optionally add **WeatherManager** ? assign `TimeOfDay` ? configure state transition thresholds
4. Optionally add **CelestialBody** components for sun/moon transforms

## Key Files

```
Assets/Sol_ToD/
+-- TimeOfDay.cs             Master clock — DayFactor, Hour, calendar events
+-- EnvironmentManager.cs    Lighting, skybox, fog, reflection blends
+-- WeatherManager.cs        Weather state machine, C# transition events
+-- CelestialBody.cs         Orbit solver for sun/moon
+-- README_TimeOfDay.md      Original setup notes
```
