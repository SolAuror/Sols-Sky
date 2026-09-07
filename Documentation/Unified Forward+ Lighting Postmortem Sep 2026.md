# Unified Forward+ Environment Lighting Postmortem

**Date:** 8 September 2026
**Project:** Sol Sky & Water System
**Engine:** Unity 6.3 / URP 17.3
**Status:** Implementation complete; content bake and target-hardware acceptance pending

## Executive summary

The environment renderer already used URP Forward+, but lighting responsibility was
distributed across time-of-day, weather, water, atmosphere, Unity lights, and a
single reflection-probe path. Custom water shaders did not fully participate in
Forward+ clustered lighting, quality controls were not coordinated, and the project
had no authored Adaptive Probe Volume workflow.

The work replaced those disconnected paths with one canonical lighting frame and one
runtime authority. It added deterministic quality tiers, opt-in managed settlement
lights, clustered water-light evaluation, ranked and time-sliced reflection probes,
hybrid realtime APV sky occlusion, and visible development diagnostics.

The final automated result was **163 of 163 EditMode tests passing**. Unity completed
the suite with no C# compilation or tested-shader failures. The APV placement assets
are authored, but probe coefficients were intentionally not baked because that bake
must be generated from final static geometry. The planned visual matrix and
RTX 3060/RX 6600-class performance capture also remain manual acceptance work.

This was a modernization project rather than a production outage. The postmortem is
therefore focused on architectural risk, migration failures, and acceptance gaps.

## Original problem

The initial system had four material weaknesses:

1. Multiple systems could derive or publish overlapping lighting state.
2. Custom water shaders could miss Forward+ additional lights.
3. Reflection and local-light work was not governed by a shared, measurable budget.
4. Dynamic sky colour had no spatially occluded indirect-lighting solution.

The change also had to preserve existing uncommitted work in
`SolOceanClipmap.cs`, `SolOcean.shader`, and `Daybox.mat`.

## Delivered outcome

| Phase | Delivered result | Primary evidence |
|---|---|---|
| Canonical lighting | `SolLightingFrame` and `SolLightingDirector` resolve and publish sun, moon, dominant light, ambient, weather attenuation, cloud shadow, eclipse, and lightning from one authority. | `Scripts/Lighting/SolLightingFrame.cs`, `SolLightingDirector.cs` |
| Celestial handoff | Clouds and atmosphere consume one continuous radiance-weighted sun/moon blend. When neither body contributes, direct celestial lighting and cloud shadows fade to zero while ambient twilight remains. | `SolLightingFrame.cs`, `SolAtmosphereController.cs`, `SolCloudRendererFeature.cs` |
| Quality policy | Manual Low/Medium/High tiers synchronise URP shadows, water, clouds, atmosphere, local-light limits, volumetric limits, and reflection cadence. Medium is the shipped default. | `SolLightingQualityProfile.cs`, `Sol_LightingQuality.asset` |
| Managed local lights | `SolEnvironmentLight` provides opt-in activation, weather-driven early activation, deterministic ranking, hysteresis, fades, shadow-slice accounting, volumetric selection, and authored-state restoration. Ordinary Unity lights remain untouched. | `SolEnvironmentLight.cs` |
| Forward+ water and probes | Both water paths use a shared clustered-light loop. Realtime probes are ranked by visibility, distance, water relevance, and staleness; only one individual-face capture can be in flight. | `SolForwardPlusWaterLighting.hlsl`, `SolReflectionProbeAnchor.cs` |
| Adaptive Probe Volumes | Three environment scenes share a streamed APV baking set with sky occlusion and sky direction. Sun, moon, and managed settlement lights remain realtime; no day/night scenarios were introduced. | `Assets/Settings/SolEnvironmentAPV.asset`, three scene APV volumes |
| Diagnostics | The Environment Window and frame-budget logger expose tier, registered/selected lights, shadow slices, volumetric lights, GI requests, and probe requests/completions. Profiler markers cover resolve, apply, selection, Dynamic GI, and probe scheduling. | `SolEnvironmentBudget.cs`, `SolEnvironmentWindow.cs` |

## Important design decisions

### One writer, immutable output

The director owns runtime lighting commits. Upstream systems supply inputs; downstream
systems consume a resolved immutable frame. Compatibility properties remain available,
but they no longer establish a second authority. This makes ordering, restoration, and
testing explicit.

### Lightning is intentionally transient

Lightning affects direct shader lighting and atmosphere, but the scheduler compares
stable ambient state. It therefore does not invalidate GI or reflection captures by
itself. This avoids baking a short flash into slowly refreshed indirect lighting.

### Local-light management is opt-in

Only lights carrying `SolEnvironmentLight` enter the shared activation and shadow
budget. This prevents the migration from silently changing unrelated gameplay or art
lights. A development warning identifies unmanaged local shadow casters that bypass
the budget.

### Shadow capacity is measured in slices

A spot shadow costs one atlas slice and a point shadow costs six. The Low, Medium, and
High limits are 4, 12, and 24 slices respectively. This reflects URP atlas pressure more
accurately than counting shadow-casting Light components.

### APV uses one static occlusion bake with a realtime sky

The shared baking set has sky occlusion and sky shading direction enabled, with disk
and GPU streaming. It uses 2 m minimum probe spacing and three simplification levels.
Lighting scenarios are disabled: the canonical ambient probe supplies the live sky
contribution instead of maintaining separate day and night bakes.

`DynamicGI.UpdateEnvironment` runs at a one-second minimum cadence when asynchronous
GPU readback is supported and a four-second minimum on the synchronous path. Missing
APV data produces one warning and retains trilight ambient as the fallback.

## Quality-tier contract

| Tier | Main shadows | Main/punctual atlas | Managed lights | Shadow slices | Local volumetrics | Probe interval |
|---|---:|---:|---:|---:|---:|---:|
| Low | 35 m, 2 cascades | 1024 / 1024 | 48 | 4 | 0 | 8 s |
| Medium | 60 m, 3 cascades | 2048 / 2048 | 96 | 12 | 4 | 4 s |
| High | 100 m, 4 cascades | 4096 / 4096 | 160 | 24 | 8 | 2 s |

Tier changes are user-controlled and deterministic. Runtime overrides are restored
when the director releases authority, preventing play-mode quality changes from
becoming authored project state.

## What went well

- The work was divided into narrow phases with a full-suite test after each risky
  integration point.
- Existing uncommitted water, clipmap, and sky-material edits were preserved and
  reconciled instead of overwritten.
- Shared helpers removed duplicate clustered-light logic from the two water shaders.
- Snapshot-and-restore behaviour was implemented for directional lights, ambient
  settings, pipeline quality, local lights, and reflection-probe capture settings.
- The APV configuration is repeatable through
  `Tools > Sol Environment > Configure Adaptive Probe Volumes` and protected by
  bake-validation tests.
- Diagnostics compile out of non-development players, while profiler markers remain
  available for targeted captures.
- The completed automated suite contains 163 passing tests, including APV membership,
  streaming, realtime-light exclusion, quality contracts, Forward+ source contracts,
  restoration, probe ranking, and diagnostics.

## Problems encountered during implementation

### Unity cache access failed inside the filesystem sandbox

**Symptom:** The first batch Unity launch failed while opening its Curl request-cache
database outside the repository.
**Impact:** The initial APV migration did not execute. No project asset was partially
written by that attempt.
**Correction:** Unity batch operations were rerun with explicit permission for Unity's
local cache. Subsequent imports and tests completed normally.
**Prevention:** Treat Unity batch runs as cache-dependent operations and request the
required host access at the first launch.

### The APV baking-set object was unloaded during multi-scene migration

**Symptom:** After opening several scenes sequentially, the setup utility retained a
destroyed `ProbeVolumeBakingSet` reference and threw `MissingReferenceException`.
**Impact:** Scene volumes had been added, but the first migration run terminated before
its success path.
**Root cause:** Unity unloaded an unreferenced asset object while replacing the active
scene.
**Correction:** The utility stopped retaining the asset across scene loads and reloads
it where needed. Core RP's scene-bound and per-scene-data hooks are invoked explicitly.
**Prevention:** Do not retain Unity asset object references across scene replacement
unless they are guaranteed to remain loaded; retain the asset path or GUID instead.

### Saving scenes captured unrelated ExecuteAlways state

**Symptom:** The APV migration's first scene saves also serialized preview fog/ambient
values, changed wind state, emitted newly introduced default fields, and created an
unrelated shoreline helper.
**Impact:** The diff contained changes outside APV scope.
**Root cause:** Environment components execute during editor scene load, and saving the
scene persisted their transient or auto-generated state.
**Correction:** Every scene diff was audited. Transient RenderSettings and unrelated
component changes were removed while retaining only the APV placement volume and Core
RP per-scene data host.
**Prevention:** Editor migrations that save scenes should either suspend preview
authorities or snapshot and restore editor-global state before serialization. Generated
helpers should not dirty authored scenes unless the user explicitly requests creation.

### The APV test assumed an active scene existed in batch mode

**Symptom:** All APV assertions passed, but cleanup failed while restoring an empty
`SceneManagerSetup`.
**Impact:** The first APV-enabled suite finished 157/158.
**Correction:** Cleanup now restores the previous setup only when it contains a loaded
active scene; otherwise it creates a clean empty scene.
**Prevention:** Editor tests must handle both interactive-editor and headless batch
scene topology.

### Dawn's no-light interval injected synthetic direct lighting

**Symptom:** At the same time each morning, approximately 05:13-05:36 in the captured
Lithane 25 sequence, the overcast scene became unnaturally bright and grey before
falling dark again as sunrise approached.
**Impact:** The sun/moon transition was visibly discontinuous in atmosphere, clouds,
terrain, and water reflections.
**Root cause:** After the moon dropped below its eligibility threshold and before the
sun became eligible, the canonical frame correctly reported no dominant celestial
light. The cloud renderer substituted `Color.white`, while atmosphere retained an
authored directional-scattering fallback. A weak newly eligible sun then replaced
those synthetic values, creating the second luminance discontinuity.
**Correction:** The lighting resolver now publishes a continuous radiance-weighted
sun/moon blend. Clouds and atmosphere use that shared blend, the no-light interval is
ambient-only, and cloud shadows fade with celestial shadow strength. Three regression
tests cover moon-only, overlap, and no-eligible-light states.
**Prevention:** Downstream renderers must consume resolved celestial radiance and must
not invent a direct-light colour when the canonical frame reports no contributor.

### Scene registry drifted after the landscape scene replacement

**Symptom:** The full isolated test run referenced the deleted
`Sc_Sols_Landscape.unity`, and the shared APV asset still contained its obsolete scene
GUID.
**Impact:** APV membership validation and weather-profile migration could target a
scene that no longer existed.
**Correction:** `Sols_Lights.unity` is now the registered replacement in both editor
utilities, and the APV baking-set scene GUID was reconciled to its metadata.
**Prevention:** Treat scene replacement as a registry migration: update explicit path
lists, baking-set GUIDs, tests, and documentation in the same change.

## Validation performed

- Unity imported and compiled the runtime and editor assemblies.
- Full graphics-enabled EditMode suite: **163 total, 163 passed, 0 failed, 0 skipped**.
- APV validation confirms:
  - all three environment scenes belong to the shared baking set;
  - each scene has one auto-fitted scene volume and one per-scene data host;
  - sky occlusion and sky direction are enabled;
  - disk and GPU streaming are enabled;
  - lighting scenarios and scenario blending are disabled;
  - all lights in the environment scenes are authored Realtime.
- Forward+ water tests pin clustered local-light and additional-directional loops.
- Runtime render-graph smoke coverage completed as part of the existing suite.
- `git diff --check` completed without whitespace or patch-format errors.

## Validation not yet performed

The following planned acceptance work requires final content, target hardware, or
subjective visual review and was not represented as completed:

- Bake APV probe coefficients after static geometry and Contribute GI flags are final.
- Add local Probe Adjustment Volumes at actual caves, enclosed buildings, and difficult
  shoreline/interior transitions identified during the bake review.
- Capture the dawn, noon, storm, lightning, dusk, and moonlit-night visual matrix above
  and below water, inside and outside APV-occluded interiors.
- Exercise authored main-only, 32-light, and 96-light stress scenes rather than relying
  solely on source/import contracts.
- Profile Medium at 2560x1440 on RTX 3060/RX 6600-class hardware and verify:
  - sustained 60 FPS;
  - lighting-management CPU below 0.3 ms average;
  - zero managed per-frame GC after warm-up;
  - no reflection update exceeds the 16.67 ms frame budget;
  - shadow-atlas slice use matches the configured budget.

## Residual risks

| Risk | Consequence | Mitigation |
|---|---|---|
| APV coefficients are currently absent | Spatial indirect lighting falls back to trilight ambient. | Complete the final-content bake; the runtime warning makes this visible. |
| APV setup invokes internal Core RP editor hooks by reflection | A future render-pipeline update can rename the hooks. | Tests fail on missing hooks; update the utility during Unity/URP upgrades. |
| Unmanaged Unity lights remain legal | They can bypass managed light and shadow budgets. | Development warning plus art-review policy; add `SolEnvironmentLight` where budgeting is required. |
| One primary camera/global environment state is assumed | Multi-camera views may compete for global presentation state. | Keep reflection/secondary cameras on reduced paths; design scoped state before expanding camera support. |
| APV scene membership is an explicit path list | New environment scenes can be omitted. | Add new paths to `SolApvSetupUtility.EnvironmentScenePaths`; bake-validation tests then enforce membership. |
| Terrain wetness shader reports unsupported subshaders on the headless test platform | Batch logs contain warnings even though the target URP shader tests pass. | Verify the shader on the actual graphics API and tighten its renderer/platform guards separately. |

## Follow-up actions

### P0 — required before visual sign-off

1. Freeze static geometry and verify Contribute GI flags.
2. Bake `SolEnvironmentAPV` and commit the generated streaming data.
3. Review probe validity and add local adjustment volumes where leaking is visible.
4. Run the complete time/weather/underwater visual matrix.

### P1 — required before performance sign-off

1. Build or designate the 32-light and 96-light settlement stress scenes.
2. Capture Medium-tier CPU, GPU, allocation, light-complexity, and shadow-atlas data on
   target-class hardware.
3. Record measurements in a checked-in performance report with engine, GPU, resolution,
   graphics API, and build configuration.

### P2 — editor and maintenance hardening

1. Prevent ExecuteAlways helper generation from dirtying scenes implicitly.
2. Revalidate the APV setup reflection hooks whenever Unity or Core RP is upgraded.
3. Consider a build-time check that rejects environment scenes missing from the APV set.

## Final assessment

The central architectural objective was achieved: the renderer now has one coherent
environment-lighting layer rather than several loosely coordinated writers. The design
is deterministic, reversible, observable, and covered by automated regression tests.

The project is ready for content-dependent APV baking and hardware acceptance, but it
should not yet claim the target 1440p/60 performance result or final APV visual quality.
Those are measured gates, not conclusions that can be inferred from passing editor
tests.
