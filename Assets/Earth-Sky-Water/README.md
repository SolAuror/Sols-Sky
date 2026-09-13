# Sol Sky & Water System

Unity 6 / URP 17 sky, time-of-day, weather, and water framework.

```
SolSkyProfile -> pure SolSkyResolver -> immutable SolSkyFrame
        |                    | shared directional sky radiance
        |--------------------|------------------------------|
        v                    v                              v
TimeOfDay              Atmosphere / Clouds          Water reflections
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
SolWaterWorld
  - registered SolWaterBody surfaces (ocean, lake, river, pool, waterfall)
  - SolWaterProfile look, SolWaterQualityProfile budget
  - SolWaterRendererFeature: FFT spectrum, clipmap ocean, SSR, caustics,
    volumetrics and underwater composition
  - SolWaterQueryService for gameplay sampling, buoyancy and interaction
```

## Scene Setup

1. Add `TimeOfDay`, `Calendar`, and `SolEnvironmentCoordinator` to one scene-owned system-manager GameObject. Assign the sun/moon prefabs and set the authored `Sol/Skybox` material in Lighting.
2. Add `SolEnvironmentWorld`. It publishes the canonical tick that water, clouds and fog all read, including the wave clock and the lagged wind responses.
3. Add `SolWaterWorld` and assign a `SolWaterProfile` and a `SolWaterQualityProfile`. It adds its own query service, planar reflection renderer, wetness authority and terrain shoreline builder.
4. Mark each water surface with a `SolWaterBody`. Ocean is infinite and clipmap-rendered; lake, river, pool and waterfall are bounded and take their surface from a spline geometry component or a Renderer on the same object.
5. Add `SolWeatherManager`. References auto-resolve if the managers are in the scene.
6. Assign a `SolSkyProfile` to `TimeOfDay`; the shipped `Sol_Sky_Grounded` profile is the default and `Sol_Sky_Legacy` is the compatibility translation. Add `SolAtmosphereController` and `SolRainVfxController` to the same scene-owned manager. `SolAtmosphereProfile` remains a fallback only when no sky profile is assigned.
7. Add `SolAtmosphereRendererFeature`, `SolCloudRendererFeature` and `SolWaterRendererFeature` to the URP Renderer asset. Keep existing features such as SSAO. Underwater composition is part of the water feature; there is no separate overlay feature or controller.
8. Enable Depth Texture and Opaque Texture on the URP asset. The atmosphere, clouds and the water prepass read depth; water refraction and the underwater composition read the opaque copy.
9. Tag the gameplay camera as `MainCamera`, or assign cameras explicitly on the relevant controllers.

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

## Celestial Lighting

Sun, moon, and configured additional bodies emit independently whenever they rise above
the horizon. Each keeps its own direction, color, intensity, and weather attenuation;
moonlight also follows lunar illumination. Moonlight does not wait for sunset. Tune the
sun and moon light colors and minimum/maximum intensities on `TimeOfDay`, including when
a sky profile is assigned. Sky profiles author the appearance of the discs and sky.

For another emitter, add a prefab and `CelestialBodyConfig` to Tertiary Planets, enable
`hasLight`, and set its light color/intensity. A missing directional light is created on
the instance. Clouds, fog, and water consume up to eight active celestial emitters from
the lighting director. Cloud occlusion is traced per emitter; the first two use the
quality tier's light steps and further emitters use at most two steps. Dark bodies incur
no light march. Surface lights remain managed by URP Forward+.

URP supplies one directional shadow map, assigned to the dominant emitter. Its shadow
strength fades around a change of ownership so shadows do not snap; emitted light is
unaffected. Cloud world shadows use that same owner. Cloud self-shadowing is independent
for every emitter. With no visible emitters, directional radiance is zero and ambient
twilight remains.

## Public API

See `../../Documentation/PublicAPI.md` for code examples and the full scripting guide

Common entry points:

| Class | Main use |
|---|---|
| `Sol.ToD.TimeOfDay` | world clock, sky authority, time skip/mutation events |
| `Sol.ToD.SolSkyProfile` | reusable authored sky, atmosphere, celestial, cloud-baseline, and source-asset settings |
| `Sol.ToD.SolSkyFrame` | immutable resolved sky presentation consumed by atmosphere, lighting, clouds, and water |
| `Sol.ToD.Calendar` | day/month/year state and season events |
| `SolLightingDirector` | unified sun, moon, ambient, shader-global, and quality authority |
| `SolLightingQualityProfile` | deterministic Low/Medium/High lighting budgets |
| `SolEnvironmentLight` | opt-in local-light activation, shadow, and volumetric budgeting |
| `SolReflectionProbeAnchor` | ranked, time-sliced realtime environment-probe scheduling |
| `SolWeatherManager` | weather profiles and transitions |
| `SolWeatherState` | immutable effective cloud/rain/wind/fog/wave/lightning state |
| `SolAtmosphereController` | analytic fog plus High-quality directional volumetric scattering and quality state |
| `SolRainVfxController` | camera-following rain, shelter, and exposure |
| `Sol.Water.SolWaterWorld` | water authority: registered bodies, shared profile, quality policy |
| `Sol.Water.SolWaterBody` | marks an object as water; identity, type, extent, flow and level |
| `Sol.Water.SolWaterProfile` | authored colour, optics, spectrum, shoreline, foam and caustics |
| `Sol.Water.SolWaterQualityProfile` | tier plus clipmap, SSR, planar, volumetric and underwater budgets |
| `Sol.Water.ISolWaterQueryService` | gameplay surface sampling, immediate or batched |
| `Sol.Water.SolWaterBuoyancy` | Rigidbody floating against the sampled surface |
| `Sol.Water.SolWaterInteractor` | wake and disturbance left by a moving object |
| `Sol.Water.SolWaterInteractionZone` | ripple simulation region covering a body |
| `Sol.Water.SolWaterWetness` | terrain wetness from rain and shoreline proximity |
| `Sol.Water.SolTerrainShoreline` | realtime shoreline field built from terrain heightmaps |
| `Sol.Water.Rendering.SolWaterRendererFeature` | ocean clipmap, FFT, SSR, caustics, volumetrics, underwater |

## Wave Sync Contract

Gameplay and the GPU read the same surface rather than two mirrored formulas. The ocean
spectrum is simulated by `SolWaterFFT.compute` and read back on the CPU by
`SolWaterFftReadback`, so a query resolves against the displacement that was actually
rendered. `SolWaterWaveEvaluator` evaluates the profile's authored Gerstner waves for
bodies that carry them and for the Low quality tier, which disables the spectrum entirely.

`ISolWaterQueryService.TrySampleImmediate(position, out sample)` is the gameplay entry
point; `RequestBatch` amortises many samples across a frame. Horizontal displacement is
inverted by `SolWaterDisplacementInversion`, so a query at a world position finds the
surface that ended up there rather than the one that started there.

`SolWaterWorld.TrySampleApproximate` is the cheap path for callers that only need a
plausible height, and reports its own confidence.

## Shader Globals

| Global | Set by | Meaning |
|---|---|---|
| `_Sol_SunDirection`, `_Sol_SunColor` | `SolLightingDirector` | dominant water lighting compatibility feed |
| `_Sol_DayFactor` | `SolLightingDirector` | night-to-day blend |
| `_Sol_EclipseFactor` | `SolLightingDirector` | solar eclipse strength |
| `_SolWaterWind`, `_SolWaterWeather` | `SolWaterRendererFeature` | wind and weather forcing on the surface |
| `_SolWaterWaveTime`, `_SolWaterSpectralParams` | `SolWaterRendererFeature` | wave clock and FFT cascade parameters |
| `_SolWaterOptics`, `_SolWaterAbsorption` | `SolWaterRendererFeature` | scattering, absorption and depth colour |
| `_SolWaterShoreline*` | `SolTerrainShoreline` | live shoreline distance, depth and breakers |
| `_Sol_RainIntensity`, `_Sol_SurfaceWetness` | `SolWaterWetness` | rain response and terrain wetness |
| `_Sol_GlobalWaterLevel` | `SolWaterWetness` | shoreline reference height for terrain wetness |
| `_Sol_LightningFlash` | `SolLightingDirector` | reflection and surface strike illumination |
| `_SolAtmosphere*` | `SolAtmosphereController` | analytic fog, noise, sun/moon scattering, High volumetric settings, quality, and lightning |
| `_SolSky*` | `TimeOfDay` / `SolSkyFrame` | shared normalized radiance, directional twilight, altitude response, horizon, and stellar backdrop |
| `_SolWaterInteraction*` | `SolWaterInteractionZone` | GPU ripple simulation region |
| `_SolOceanPatchData` | `SolOceanClipmap` | per-patch instancing data for the ocean rings |
| `_UnderwaterFactor`, `_UnderwaterDepth` | `SolWaterRendererFeature` | submersion contract, read by atmosphere and rain |

## Solar eclipses

The primary moon always renders in front of the sun. Its spawned orbit distance is capped at 95% of the sun's distance if a config would put it farther away; authored assets are preserved. Apparent disc sizes remain independently adjustable through the sky profile.

Solar coverage uses those displayed angular diameters, refraction, and horizon flattening. The moon's opaque silhouette blocks the photosphere and corona directly; its opacity never depends on the eclipse strength. Direct sunlight and the solar aureole scale with the uncovered solar area, reaching zero at totality. Ambient eclipse colours and the corona remain separately authored, and other celestial lights retain their own illumination. The threshold, phase window, and apex bias controls now apply only to lunar eclipses.

The CPU rejects separated discs early, uses analytic circle overlap away from the horizon, and integrates overlapping horizon ellipses with cached sampling nodes and no per-frame allocations. The sky shader reuses the two existing disc masks instead of constructing additional eclipse masks.

## Tuning

- Time speed: `TimeOfDay.CycleDuration` and `TimeOfDay.TimeScale`.
- Lighting quality: call `SolLightingDirector.SetTier` to switch the shared Low/Medium/High shadow, reflection-probe, cloud, atmosphere, and water budgets. The shipping default is the Medium `Sol_LightingQuality` resource profile.
- Managed local lights: add `SolEnvironmentLight` beside a point or spot light. Choose night-only, always-on, or a custom day-factor curve, then set its priority, shadow eligibility, and volumetric scattering.
- Dynamic probes: add `SolReflectionProbeAnchor` beside each realtime probe. Captures are ranked by camera visibility/distance, water relevance, and staleness; only one individual-face capture runs at once.
- APV authoring: use `Tools > Sol Environment > Configure Adaptive Probe Volumes` after adding an environment scene, then add that scene to `SolApvSetupUtility.EnvironmentScenePaths` so membership remains testable.
- Environment authoring and diagnostics: open `Tools > Elementa > Control Panel`. Its Overview, Time, Sky, Weather, Clouds, Water, Landscape, Lighting and Diagnostics pages edit the same scene authorities and profile assets used at runtime; it owns no preview simulation state. Overview runs the scene and project health checks, and every page exposes the full serialized surface of the components it owns, so the inspector is not needed for authoring.
- Visual validation: open `Tools > Elementa > Visual Validation > Capture Matrix` in Play Mode. One run captures exactly 432 converged states for a selected profile/version and writes raw images beside the Unity repository in `SolSkyVisualPass_Raw`.
- Environment clocks: consume `WorldDeltaSeconds` for continuous motion, `WorldDeltaHours` for chronology, and `PresentationDeltaSeconds` only for bounded transitions/transient envelopes. All freeze when Sol time is not moving forward; presentation time deliberately ignores the 10x/100x Sol multiplier.
- Day length: `TimeOfDay.DayRatio`; the four-season annual curve modifies `EffectiveDayRatio` and exposes `SunriseClockHour`/`SunsetClockHour`.
- Climate: `SolWeatherManager` derives `DailyFogTarget` deterministically from `Calendar.WorldDayIndex`, applies dawn-biased `FogDiurnalFactor`, and smooths `CurrentDailyFog` using presentation time. Tune the seasonal fog ranges, `climateSeed`, and `ClimateTransitionDurationSeconds`.
- Weather pacing: `SolWeatherManager.durationHoursRange`, `transitionDurationSeconds`, profile `weight`, and the four seasonal weight multipliers. Seasonal weights affect automatic selection only; `transitionHours` is obsolete. Selection is also weighted by `SolWeatherAdjacency`, so a change steps through neighbouring states rather than jumping anywhere in the set.
- Default weather identities: Clear is an open sky that varies from cloudless to lightly clouded; Fair holds the scattered-cumulus look Clear used to have; Fog is calm, bright and ground-hugging; Overcast is a dry deck; Drizzle, Rain and Storm escalate precipitation, wind and mist; Snow and Blizzard are the frozen branch, with Blizzard obscuring brightly rather than darkening.
- Cloud cover varies per world day. Each profile's `cloudCoverageVariance` scales `SolWeatherManager.DailyCoverageOffset`, a stable hash of the date crossfaded across the day boundary, so the same weather never renders identically twice.
- Profile tuning: use `cloudiness` for coverage, `cloudErosion` for edge breakup, `fogBoost` for extinction, `mistiness` for low height distribution, and `skyObscuration` only for additional sky-wide masking. `waterTurbulence` controls geometric disorder, foam, roughness, and drift.
- Water look: edit the `SolWaterProfile` assigned to the water world or to an individual body. Colour, surface optics, refraction, sun glitter, reflections, the wave spectrum, shoreline response, foam and caustics are all authored there.
- Water cost: set the `SolWaterQualityProfile` tier. It fixes the FFT resolution and cascade count; Low disables the spectrum entirely and drives the surface from the profile's authored Gerstner waves. Clipmap resolution, ring count and horizon distance are the biggest ocean vertex costs.
- Storm water polish: strong-wind steering saturates before wave headings collapse, and fixed wave-family phase offsets plus restrained crest foam keep side views from resolving into uniform white rows.
- Lunar water response: `TimeOfDay.LunarTideFactor` peaks at new and full moon.
- Sea state: the developed sea follows wind on a twenty-minute lag, the slowest response in the stack. After a wind change the surface is still building; the control panel's Water page reports how far along it is.
- Ripples: add a `SolWaterInteractionZone` over a body and a `SolWaterInteractor` on anything that should leave a wake.
- Floating objects: add `SolWaterBuoyancy` beside a Rigidbody.

## Gotchas

- The ocean material is authored as an asset with GPU instancing enabled, rather than created from a shader at runtime. Built-in shader stripping keeps the instancing variant only for shaders a material asset enables it on, and without it the clipmap collapses to nothing in a player build, silently and only in builds.
- `TimeOfDay` writes to a hidden runtime skybox clone and restores the authored material when the scene authority releases.
- Edit mode does not drive RenderSettings or skybox properties; use Play mode for the live environment preview so scene saves cannot serialize a temporary clone or preview state.
- Where bodies overlap, the highest `priority` containing a query position wins; the ocean is always the last resort. Only one body may be typed Ocean.
- `SolWeatherManager` owns weather-driven sky fields while enabled. Water reads the resulting state through `SolEnvironmentWorld` rather than being written to directly.
- Shoreline data is rebuilt from the live terrain heightmaps whenever either side moves. Nothing about it is baked, so there is no shoreline bake step to forget.
- New materials may need to be selected once in the Inspector so Unity syncs shader keywords.
- Environment shader globals are still scene-wide; true per-volume water state and per-camera underwater state are deferred.
- APV placement is authored, but probe coefficients are not shipped until **Bake Probe Volumes** is run against final static geometry. The one-time runtime warning is expected before that bake.
- Surface atmosphere and underwater rendering still use one active camera/global state. True multi-camera and local-volume scoping remain deferred.
