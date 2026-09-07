# Environment Integration and Visual Polish Checklist — August 2026

This checklist tracks the follow-up sprint to the stability work in `Systems Checklist aug 2026.md`. The original systems report remains unchanged.

## Engineering tasks

| ID | Priority | Task | Depends on | Acceptance | Automated coverage | Status |
|---|---:|---|---|---|---|---|
| POL-001 | P0 | Re-establish the stability baseline | — | Runtime and Editor assemblies compile; Editor, Runtime, and Player suites pass; authored Daybox survives Play mode | Existing environment suites | [x] Automated baseline green; visual smoke pending |
| POL-002 | P0 | Add the canonical environment frame clock | POL-001 | World seconds and hours include Unity scale, Sol scale, and pause exactly once | Clock scaling and pause tests | [x] |
| POL-003 | P0 | Expand time-change results and weather skip handling | POL-002 | Positive skips advance weather chronology; rewinds and corrections do not | Skip/rewind/correction tests | [x] |
| POL-004 | P0 | Publish immutable blended weather state | POL-002 | Sky, water, atmosphere, rain, and lightning can consume one effective state | Weather state and lifecycle tests | [x] |
| POL-005 | P0 | Synchronize water, ripple, cloud, and lightning clocks | POL-002 | Environment motion freezes and scales together; extreme deltas do not create unbounded work | 1x/10x/100x tests | [x] |
| POL-006 | P1 | Add camera-following rain VFX | POL-004 | Rain responds to weather, wind, daylight, lightning, pause, and camera changes | Rain controller tests | [x] |
| POL-007 | P1 | Add shelter and underwater rain exposure | POL-006 | Roof probes and gameplay exposure fade rain; underwater suppresses it | Exposure and underwater tests | [x] |
| POL-008 | P0 | Add the clean-room Sol atmosphere renderer | POL-004 | Distance/height fog, sky haze, noise, scattering, weather, and lightning work in URP RenderGraph | Controller/global/renderer tests | [x] |
| POL-009 | P0 | Integrate transparent Sol water with atmosphere | POL-008 | Sol water uses shared atmosphere math and is not double-fogged | Shader contract tests | [x] |
| POL-010 | P1 | Add pseudo-volume cloud quality tiers | POL-004 | Low/Medium/High tiers provide one/two/three shells and shared wind/time/lightning | Shader/default-sync tests | [x] |
| POL-011 | P1 | Wire renderer, manager, and demo controls | POL-006, POL-008, POL-010 | Demo exposes weather, time scale, exposure, and visual quality without losing existing renderer features | Scene smoke tests | [x] Implementation complete; visual smoke pending |
| POL-012 | P0 | Complete documentation and release validation | POL-011 | API guide, setup guide, screenshots, logs, tests, and performance checks are complete | Full test matrix | [~] Documentation/tests complete; screenshots and performance capture pending |
| VOL-001 | P0 | Replace approximate fog with analytic optical depth | POL-008 | Exponential-height extinction is finite for horizontal, vertical, crossing, and long rays | Numerical Editor tests | [x] |
| VOL-002 | P1 | Add High directional volumetric atmosphere | VOL-001 | Half-resolution 32-step raymarch, main-light shadows, early exit, and bilateral composite work in RenderGraph | Shader contracts and runtime render smoke | [x] |
| VOL-003 | P0 | Coordinate sun/moon directional-light ownership | VOL-001 | Dominant light uses hysteresis, drives `RenderSettings.sun`, and restores the authored light | Dominant-light and coordinator tests | [x] |
| VOL-004 | P0 | Complete volumetric documentation and release validation | VOL-002, VOL-003 | API, attribution, Editor, Runtime, Player, logs, and profiling are complete | Full test matrix | [~] Documentation and automated validation complete; visual profiling pending |
| BAL-001 | P0 | Remove duplicate weather fog density | VOL-001 | Atmosphere consumes the already weather-adjusted RenderSettings density exactly once | Runtime density test | [x] |
| BAL-002 | P0 | Restore sky/horizon balance | BAL-001 | Zenith clouds remain readable and horizon sky matches distant surface extinction without a flat band | Sky shaping numerical and visual tests | [x] Implementation; visual smoke pending |
| BAL-003 | P1 | Bound High sampling noise | VOL-002 | Reduced jitter and transient depth-aware filtering remove the repeating grain without temporal history | Shader/descriptor contracts | [x] Implementation; visual smoke pending |
| BAL-004 | P0 | Split weather chronology from presentation | POL-003, POL-004 | Skips retarget logical weather while every visible channel shares a bounded three-second blend | Skip and transition tests | [x] |
| BAL-005 | P0 | Repair rain pause/resume lifecycle | BAL-004 | Pause/rewind retain live drops, forward time resumes them, and dry weather does not freeze stale particles | Runtime particle lifecycle test | [x] |
| BAL-006 | P1 | Retune storm, lightning, wind, and waves | BAL-004 | Storm is dark, optional sky whiteout is bounded, and wind/waves transition together | State/default tests and visual matrix | [x] Implementation; visual smoke pending |
| TUNE-001 | P1 | Give Clear an explicit fair-weather identity | BAL-002, BAL-006 | Clear preserves authored clouds while removing weather fog, obscuration, dimming, rain, and lightning; wind and waves read calm | Profile default/prefab synchronization test | [x] Implementation; visual smoke pending |
| TUNE-002 | P1 | Separate Overcast from Rain | TUNE-001 | Overcast is a soft dry deck; Rain adds textured breakup, precipitation, visibility reduction, wind, and waves | Ordered profile test | [x] Implementation; visual smoke pending |
| TUNE-003 | P1 | Replace Storm whiteout dependence with dark turbulence | TUNE-002 | Storm remains the strongest state but retains cloud contrast through bounded fog, sky obscuration, scattering, and lightning | Ordered/bounded profile test | [x] Implementation; visual smoke pending |
| CLIM-001 | P0 | Add the four-season calendar model | POL-003 | Months 1/4/7/10 begin Spring/Summer/Autumn/Winter; progress uses authored month lengths; legacy half-year APIs remain compatible | Calendar boundary/progress tests | [x] |
| CLIM-002 | P0 | Add deterministic daily and diurnal climate | CLIM-001, BAL-004 | World dates reproduce a low-biased fog tendency; dawn exceeds noon; presentation freezes with forward world time | Hash/distribution/diurnal tests | [x] Implementation; runtime visual smoke pending |
| CLIM-003 | P1 | Apply moderate seasonal weather weighting | CLIM-001 | Automatic selection and `NextWeather` use seasonal multipliers while direct selection stays exact | Effective-weight/manual-selection tests | [x] |
| MIST-001 | P0 | Separate low mist shape from extinction | BAL-002, CLIM-002 | Rain/Storm become ground-hugging; Overcast retains distance; authored height settings blend to the low-mist profile | Profile/default/atmosphere tests | [x] Implementation; visual smoke pending |
| WATER-001 | P0 | Add coordinated storm turbulence | TUNE-003 | Turbulence increases geometry, detail, steepness, swell, foam, roughness, and drift without changing wave phase | C#/HLSL contract and restoration tests | [x] Implementation; visual smoke pending |
| WATER-002 | P1 | Add visual lunar spring/neap response | WATER-001 | New/full moon peak, quarter moons trough, and mean water height remains unchanged | Lunar-factor and shader-contract tests | [x] Implementation; visual smoke pending |
| POLISH-001 | P0 | Balance storm twilight atmosphere | MIST-001 | Ambient scattering, horizon warmth, haze, and sun glow remain coherent at dawn/dusk without a flat bright band | Atmosphere/sky contract tests | [x] Implementation; supplied-view recreation pending |
| POLISH-002 | P0 | Remove storm wave and whitecap repetition | WATER-001 | Strong wind retains crossing headings; wave families are phase-offset; whitecaps are narrower and less uniformly bright | C#/HLSL and canonical-clock tests | [x] Implementation; side-view smoke pending |

## Automated validation — 11 August 2026

- Last full pre-climate baseline: Editor 16/16, Runtime/PlayMode 10/10, and Standalone Windows Player 10/10 passed.
- Current climate pass: runtime, editor tooling, Editor-test, and Runtime-test C# assemblies compile with 0 warnings and 0 errors.
- Current Unity Test Runner matrix contains 25 Editor tests and 12 Runtime tests; Editor/Runtime/Player execution is pending the next Unity asset refresh because the project is currently open with focus-only refresh.
- Runtime render smoke: the High half-resolution raymarch and bilateral composite passed through a URP render request using a 2× MSAA, dynamic-scale target.
- No Sol C# compiler warnings, shader errors, RenderGraph validation errors, cleanup warnings, or leaked weak pointers were reported.
- Remaining infrastructure messages are external to Sol: Unity licensing could not refresh an access token in batch mode, Cinemachine's HDRP sample asmref has no target in this URP project, and the test-player build reports an existing unsupported `System.Windows.Forms` reference. No `System.Windows.Forms` reference exists under `Assets`, `Packages`, or `ProjectSettings` source text.

## Manual visual matrix

- [ ] Clear, overcast, rain, and storm at dawn, noon, sunset, and night.
- [ ] Pause and resume at 1x, 10x, and 100x.
- [ ] Switch the active camera while rain and clouds are moving.
- [ ] Walk under cover and adjust the gameplay rain-exposure override.
- [ ] Enter and leave water while rain and surface fog are active.
- [ ] Disable and re-enable time, weather, water, ripple, rain, and atmosphere authorities.
- [ ] Stop and restart Play mode; confirm no stale skybox, fog, rain, water, ripple, or underwater state.
- [ ] Confirm no authored material or skybox asset is dirtied by runtime control.
- [ ] Check Medium quality at 1920x1080 for stable 60-fps presentation and no catch-up spikes.
- [ ] Capture representative clear, overcast, rain, and storm screenshots.
- [ ] Compare the same clear date at Summer and Winter dawn; verify deterministic seasonal mist without forced heavy nights.
- [ ] Step and rewind dates; confirm each date returns to the same fog tendency.
- [ ] Compare new, quarter, and full moon water under identical weather and clock conditions.
- [ ] Test buoyancy and swimming during maximum Storm turbulence.

## Weather tuning reference

| Profile | Clouds | Rain | Wind | Fog | Mist | Sky mask | Scatter | Dim | Waves | Turbulence | Lightning |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Clear | 0.00 / erosion 0.52 | 0.00 | 0.35 | 0.00 | 0.00 | 0.00 | 0.55 | 0.00 | 0.85 | 0.05 | off |
| Overcast | 0.68 / erosion 0.22 | 0.00 | 0.75 | 0.03 | 0.06 | 0.04 | 0.30 | 0.28 | 1.00 | 0.18 | off |
| Rain | 0.84 / erosion 0.46 | 0.65 | 1.35 | 0.12 | 0.65 | 0.12 | 0.24 | 0.44 | 1.25 | 0.48 | off |
| Storm | 0.97 / erosion 0.64 | 0.95 | 2.65 | 0.30 | 0.82 | 0.36 | 0.16 | 0.78 | 1.85 | 1.00 | 0.28 |

Seasonal automatic-selection multipliers are Clear `1.05/1.45/0.85/0.65`, Overcast `0.95/0.70/1.25/1.35`, Rain `1.35/0.75/1.05/1.25`, and Storm `0.75/0.55/1.35/1.15` for Spring/Summer/Autumn/Winter respectively. Direct weather selection ignores these multipliers.

Visual review should use the same camera and clock time for all four states. At minimum, capture noon and sunset comparisons before changing these values again; time-of-day lighting otherwise makes weather adjustments difficult to compare reliably.

## Superseded

- **TUNE-001** gave Clear an identity that preserved the authored cloud deck. Clear is now the
  state that actively clears the sky, and the preserve-the-authored-deck identity moved to the
  new Fair profile. The change was needed because `cloudCoverageBias` -- documented as
  independent of `cloudiness` -- was being scaled by it, so at `cloudiness: 0` no profile could
  thin the authored deck and no weather could produce a clear sky.
- The **weather tuning reference** table above predates the nine-profile set, the metres-per-second
  wind unit, and the effective-cover model. `Documentation/PublicAPI.md` carries the current table.

## Deferred

- Full raymarched volumetric clouds.
- Local atmosphere volumes and true multi-camera atmosphere/underwater state.
- Rewindable weather history.
- Physical tides, mean water-level changes, atmospheric pressure, temperature, snow, and ice.
- Audio assets and thunder-delay simulation.
- Mobile-first rendering presets.
- Licensed Better Fog package integration or source reuse.
- Temporal atmosphere reprojection and quarter-resolution checkerboarding.
- Point/spot-light volumetrics and localized density volumes.
- Horizon-style volumetric cloud modeling; the current cloud shells remain a separate system.

## Directional volumetric atmosphere notes

- Low uses analytic extinction without procedural noise.
- Medium remains the default and uses analytic extinction with weather-driven noise.
- High renders directional in-scattering and transmittance into a transient half-resolution HDR texture, applies a transient depth-aware spatial filter, then performs a four-tap depth-aware composite before transparents.
- High is bounded to 32 steps and exits once transmittance reaches 0.01. It has no temporal history, camera-cut state, or persistent render textures.
- Water and rain continue to use the shared analytic atmosphere include and are rendered after the opaque atmosphere pass, so they receive fog exactly once.
- Sun and moon are the only participating shadowed lights. Local lights remain deferred.

The implementation is original Sol code informed by the general extinction, phase-scattering, early-exit, and reduced-resolution principles described in Guerrilla Games' [Horizon Zero Dawn cloud presentation](https://advances.realtimerendering.com/s2015/The%20Real-time%20Volumetric%20Cloudscapes%20of%20Horizon%20-%20Zero%20Dawn%20-%20ARTR.pdf). [Cristian Qiu's MIT URP volumetric-light project](https://github.com/CristianQiu/Unity-URP-Volumetric-Light) was used only as a public compatibility and feature reference; its package and source are not imported, copied, or distributed by Sol.
