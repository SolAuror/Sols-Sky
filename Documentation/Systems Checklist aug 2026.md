# Environment Systems Stability Sprint Checklist

This is the actionable companion to [Systems Report aug 2026.md](Systems%20Report%20aug%202026.md). The report remains the external assessment; this file tracks implementation and verification.

Status legend: `[x]` implemented, `[ ]` outstanding or awaiting verification. Priorities use P0 (correctness/lifecycle), P1 (public behavior and regression coverage), and P2 (documentation/manual validation).

## Delivery gates

| ID | Pri | Task | Depends on | Acceptance criteria | Coverage | Status |
|---|---:|---|---|---|---|---|
| ENV-001 | P0 | Separate rewindable world date from monotonic player-time | — | Calendar exposes signed `WorldDayIndex` and fractional `PlayerDaysElapsed`; new games start both at zero | `Calendar_UsesIndependentWorldAndPlayerClocks` | [x] |
| ENV-002 | P0 | Apply player-time accounting rules | ENV-001 | Natural ticks and positive gameplay skips add time; rewinds and arbitrary clock corrections do not | `TimeMutations_CountOnlyForwardGameplayTime`, `NaturalTicking_AddsFractionalPlayerTime` | [x] |
| ENV-003 | P0 | Migrate astronomy and aurora to world date | ENV-001 | Lunar orbit/eclipses and deterministic aurora selection use `WorldDayIndex` | `AstronomyAndAurora_UseWorldDayIndex` plus demo smoke test | [x] |
| ENV-004 | P1 | Expand time snapshot restore | ENV-001 | Restore sets normalized time, date, signed world index, and player-time independently | `TimeMutations_CountOnlyForwardGameplayTime` | [x] |
| ENV-005 | P1 | Preserve obsolete time compatibility APIs | ENV-001 | `TotalDaysElapsed`, elapsed-day `DayChanged`, legacy `SetDate`, and legacy restore overload compile with obsolete guidance | Editor compilation | [x] |
| ENV-006 | P0 | Add scene-owned environment coordinator | — | Existing `Sols System Manager` prefab owns `SolEnvironmentCoordinator`; no `DontDestroyOnLoad` is introduced | Prefab inspection | [x] |
| ENV-007 | P0 | Centralize environment registration and final cleanup | ENV-006 | Time, water, weather, ripple, grid, volume, and underwater authorities register; captured RenderSettings and known Sol globals restore after final release; an active newer authority prevents early restoration | `Coordinator_RestoresRenderSettingsAndKnownShaderGlobals`, `Coordinator_DoesNotRestoreWhileANewerAuthorityRemainsRegistered` | [x] |
| ENV-008 | P0 | Isolate skybox writes | ENV-006 | Active time authority writes to a hidden runtime material clone; authored skybox is restored and clone destroyed on release | `Coordinator_UsesRuntimeSkyboxCloneWithoutMutatingAuthoredMaterial` | [x] |
| ENV-009 | P0 | Refresh generated volume wave data | — | Assigning a grid material immediately updates the managed volume's CPU wave settings | `WaterVolume_MaterialRefreshReadsWaveParametersImmediately` | [x] |
| ENV-010 | P0 | Remove generated volume on grid disable | ENV-009 | Disabled/destroyed grid leaves no `_WaterVolume` in the static registry or water queries | `ManagedGridVolume_IsRemovedWhenGridDisables` | [x] |
| ENV-011 | P0 | Restore weather-owned water fields | ENV-006 | Disable restores rain; restores wind and wave speed only when their drive toggles are enabled; transient weather state returns neutral | `WeatherDisable_RestoresOwnedWaterFields` | [x] |
| ENV-012 | P1 | Wire exposed rain tuning controls | — | Manager publishes roughness, normal, and reflection tuning globals; shader has no hardcoded equivalents | `RainControls_ShaderAndManagerDefaultsStayInSync` | [x] |
| ENV-013 | P1 | Refresh celestial camera dynamically | — | Each refresh resolves the current main camera and updates orbit position/billboarding after a handoff | `CelestialBody_TracksChangedMainCamera` | [x] |
| ENV-014 | P1 | Add dedicated test assemblies | ENV-001–013 | Editor and Runtime asmdefs import under `Assets/Tests` and tests appear in Unity Test Runner | Unity import/Test Runner | [x] |
| ENV-015 | P1 | Complete Unity compilation and automated test run | ENV-014 | Zero C# or shader compile errors; all Environment EditorTests and RuntimeTests pass | Unity `6000.3.9f1`, URP `17.3.0` | [ ] |
| ENV-016 | P2 | Run manual demo smoke checklist | ENV-015 | All manual checks below pass with no persistent authored-state changes | Manual | [ ] |

## Implemented API and lifecycle checklist

- [x] `Calendar.Day`, `Month`, and `Year` remain the rewindable world date.
- [x] `WorldDayIndex` is signed and relative to the configured starting date.
- [x] `PlayerDaysElapsed` is a monotonic `double` under normal gameplay mutation APIs.
- [x] Re-enable does not reset either clock; `StartNewGame()` is the explicit reset path.
- [x] `PlayerTimeChanged` is separate from `Calendar.OnNewDay`, `OnNewMonth`, and `OnNewYear`.
- [x] `TotalDaysElapsed` and elapsed-day `DayChanged` remain obsolete compatibility surfaces.
- [x] `RestoreTimeSnapshot` restores both clocks; the old overload remains obsolete.
- [x] Water manager, grid, volume, ripple manager, weather manager, time authority, and underwater controller share scene-scoped cleanup ownership.
- [x] Managed water volume material synchronization is explicit.
- [x] Weather restores rain, wind direction, wind strength, and wave speed according to ownership toggles.
- [x] Rain roughness, normal, and reflection dampening are data-driven shader globals.
- [x] Celestial bodies no longer permanently cache `Camera.main`.

## Automated coverage map

| Behavior | Assembly | Test |
|---|---|---|
| Configured calendar start, signed rewind, symmetric date motion, independent player-time | Editor | `Calendar_UsesIndependentWorldAndPlayerClocks` |
| Direct date assignment computes a signed offset | Editor | `Calendar_SetDateComputesSignedWorldOffset` |
| Astronomy/aurora source contract uses world-date time | Editor | `AstronomyAndAurora_UseWorldDayIndex` |
| Positive skips, rewinds, corrections, and independent snapshot restore | Runtime | `TimeMutations_CountOnlyForwardGameplayTime` |
| Natural fractional player-time | Runtime | `NaturalTicking_AddsFractionalPlayerTime` |
| Runtime material-to-volume wave synchronization | Editor | `WaterVolume_MaterialRefreshReadsWaveParametersImmediately` |
| Managed volume registry cleanup | Runtime | `ManagedGridVolume_IsRemovedWhenGridDisables` |
| Weather-owned water restoration | Runtime | `WeatherDisable_RestoresOwnedWaterFields` |
| RenderSettings and known global restoration | Editor | `Coordinator_RestoresRenderSettingsAndKnownShaderGlobals` |
| Authority overlap prevents premature restore | Editor | `Coordinator_DoesNotRestoreWhileANewerAuthorityRemainsRegistered` |
| Runtime skybox clone leaves authored material unchanged | Editor | `Coordinator_UsesRuntimeSkyboxCloneWithoutMutatingAuthoredMaterial` |
| Rain control/default contract | Editor | `RainControls_ShaderAndManagerDefaultsStayInSync` |
| Main-camera handoff | Runtime | `CelestialBody_TracksChangedMainCamera` |

Astronomy/aurora migration and runtime skybox cloning also require the manual smoke pass because their final behavior is visual and scene-integrated.

## Manual demo smoke checklist

- [ ] Open and play `Assets/Scenes/SolsWeather_Demo.unity`.
- [ ] Confirm day/night, lunar, weather, rain, ripples, buoyancy, and underwater behavior.
- [ ] Change weather profiles and disable/re-enable the weather manager; confirm the authored water values return.
- [ ] Disable/re-enable the water grid; confirm no stale managed volume remains.
- [ ] Switch the `MainCamera` tag between cameras; confirm celestial positions and billboarding follow it.
- [ ] Stop and restart Play mode; confirm no stale sky, fog, water, ripple, or underwater state remains.
- [ ] Confirm the authored skybox material has no unexpected changes.
- [ ] Run Editor and PlayMode tests on Unity `6000.3.9f1` / URP `17.3.0`.

## Deferred follow-up phase

- [ ] Replace last-writer-wins water surface and wave-fade globals with per-volume state.
- [ ] Introduce per-camera underwater renderer state.
- [ ] Extend celestial rendering from active-camera handoff to simultaneous multi-camera support.
- [ ] Formalize generated water tiles as an authored prefab/asset workflow; current demo-generated content is intentionally retained.
- [ ] Replace the binary half-year season sign with four authored seasons and season-specific weather rules.
- [ ] Implement a save system and any legacy save migration only when persistent saves exist.

These limitations are intentionally outside this stability sprint. The coordinator restores scene-global state but does not make Unity shader globals per-camera or per-volume.
