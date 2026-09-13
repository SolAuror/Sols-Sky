# Landscape Designer validation — updated 13 September 2026

Implementation: Unity 6000.3.9f1, URP 17.3, Terrain Tools 5.3.3. Validation ran in the separate `LandscapeDesignerValidation` project, on Direct3D 11 and an AMD Radeon RX 6600 XT. Validation scenes remain separate. The September 13 organisation pass changed asset locations and ownership records, as documented below; terrain artwork was preserved.

## Guided creation and unified removal — 13 September 2026

**289 passed, 0 failed, 0 skipped** in the complete scratch-project suite. [NUnit results](Guided%20Creation%20Tests.xml). A second run in a normal, hidden editor passed **8/8 creation/organisation checks**, including native asset persistence. [Normal-editor results](Guided%20Native%20Editor%20Tests.xml). C# compilation completed with zero warnings/errors.

The UI Toolkit wizard covers scene/name/destination, grid and elevation, palette thumbnails and review. Default creation makes four 512 m tiles with colliders, neighbours, independent profiles and organised destinations. Tests exercise actual wizard button callbacks, Back, Cancel, duplicate/invalid/unsaved destinations, cancellation after the first tile, reused empty roots, Undo/Redo, and reopening. Testing caught and fixed missing tile-list and transform records on Redo and TerrainData initialisation/import ordering.

Removal tests exercise all eight channels, lazy allocation, restore, deliberate repaint, cancellation, four-tile borders, resizing/remapping, version-one snapshots, fallback/protection guards and rendered manual/automatic/override/mixed distributions. The all-Manual validation guard now agrees with the shader's safe fallback path. ForwardLit and DepthNormals still share the weight implementation. Existing shader, snow, lighting, editor and runtime clone regressions pass.

The integrated designer test creates the default grid, chooses tools through Elementa's real controls, sculpts a ridge at the four-tile junction, paints Path, removes it, saves before mouse-up, repaints, undoes/redoes and reopens. It invokes the production editor stroke operations with terrain-collider hits. Native synthetic mouse dispatch was unreliable in the test host (events became Ignore); physical mouse routing and subjective brush feel still need the normal designer acceptance pass. The test does not claim to automate OS mouse input.

Artwork tests verify numbered bake destinations, retained previous pairs, and failure without replacing the valid pair. Palette repair creates copies and preserves original TerrainData through Undo/Redo. Scene Save As leaves folders in place until explicit relocation. Relocation tests verify unchanged GUIDs, shared artwork retention and reverse manifests.

### Existing asset organisation

[Reference inventory and move audit](Asset%20Organisation%20Audit.md) verifies **39 asset moves** through Unity AssetDatabase operations, with persistent before/after GUID manifests beside the affected scenes. Reopened example groups validate. No duplicate GUIDs or unresolved GUID references were found in organised assets. Shared starter profiles and CS/NOH arrays have identical before/after SHA-256 hashes.

The current canonical demo contained one `Demo_Landscape` terrain, rather than the earlier empty snapshot. Its TerrainData moved to `Assets/Scenes/Elementa_Demo/Landscapes/LandscapeRoot/TerrainData/Demo_Landscape.asset`; its SHA-256 is identical to the pre-move file. No heights, paint or deleted terrain were recovered. The group has a palette/profile mismatch: the existing TerrainData and unchanged shared profile do not agree. That setup issue remains visible; **Repair palette assignments** creates editable copies, while **Create landscape…** starts fresh.

The first organisation pass stopped when reopened validation encountered this demo mismatch. Automatic approval review rejected a second mutating pass. A separately approved **read-only audit** then reopened the scenes and verified every recorded GUID move, terrain reference and protected content hash. No further moves were attempted. Ambiguous unowned files remain in place for explicit ownership assignment; the individual move reports and manifests remain reversible.

### Removal cost

[32-scenario CSV](Removal%20Performance.csv): RX 6600 XT / D3D11, fixed 1280×800 camera, six/eight layers, stochastic/triplanar combinations and empty/override/removal/combined masks. Forty samples per scenario, twelve warmup frames. Measured GPU camera time was **0.338–0.794 ms**, CPU submission **0.520–0.620 ms**. Paired removal-enabled GPU deltas ranged from **−0.024 to +0.042 ms**, median **+0.008 ms**; this short run does not establish a statistically isolated terrain cost.

Removal allocates **2 MiB canonical + 2 MiB GPU per touched 512² tile**. Four-tile removal alone uses 8 MiB canonical; overrides plus removal use 16 MiB. All three mask sets use up to 6 MiB canonical + 6 MiB GPU per tile, excluding undo snapshots and temporary targets. Every scenario recorded **zero unchanged binding writes**.

First-use dab measurements include allocation/publication. The largest was **86.649 ms** for combined override/removal operations; commits reached **7.941 ms**. Combined rows sum the two operation timings. This is a real first-use latency risk, not a frame-rate guarantee. No synchronous readback occurs during dabs; commits remain synchronous. Basemaps remain disabled for managed groups.

## Earlier checkpoints

The sections below retain their original test counts and performance measurements as historical evidence.

## Results

- **246 passed, 0 failed, 0 skipped** in the complete EditMode suite. [NUnit results](Unity%20Test%20Results.xml) include the landscape Play Mode boundary test, existing snow/lighting/environment tests, native control-panel tests, GPU final-weight checks, four-tile painting and sculpting, undo/redo, cancellation, stable palette remapping, tile/group isolation, all-pass shader compilation, instanced/non-instanced variants and a rendered terrain-hole check.
- Final content audit: **zero GUID collisions** and **zero unresolved GUID references** in the curated assets/examples. Superseded test arrays were left in the scratch project.
- The separate four-tile example saves/reopens and compares canonical paint data successfully.
- Primary legacy migration preserved the original config, had **zero maximum alphamap difference**, and produced **pixel-identical** before/after dry reference captures (1280×800). Asset copying avoids the small native-cloning re-quantization observed in the initial check.
- The initial copied nine-tile demo validated after deliberately normalizing seven incompatible neighboring palettes in the scratch project. This was a scale fixture, not proof of the end-user migration path. The 12 September migration repair below adds an explicit shared-material conversion for this case.
- Source inspection and material renders caught and corrected omitted TIFF alpha data and clipped 16-bit heights. All twelve masks now retain their smoothness sample; all three separate heights span the normalized range. Source remaps are baked once. [Material comparison](Material%20Library.png) and [close view](Material%20Closeup.png) show the corrected inputs.

## Legacy demo regression correction

The initial 246-test run missed unmanaged legacy followers: per-terrain publication populated only the primary tile in the original nine-tile `Elementa_Demo`. All nine material assignments remained intact, but the other eight received no landscape contract. The normalized group fixture did not test this compatibility path.

The driver now publishes the original shared-primary contract to matching unmanaged material users in the same scene. Follow-up validation passed **21 landscape/snow tests, 0 failures**, including new checks against all nine original demo terrains, unchanged idle bindings, unrelated property preservation and legacy-to-group ownership transfer. [Follow-up results](Legacy%20Repair%20Tests.xml) and [nine-tile render](Legacy%20Nine%20Tiles.png) record the repair. The original scene and configuration hashes are unchanged. This restores legacy rendering; it does not migrate the demo or reconcile its differing terrain palettes.

## Painting repair verification

Final scratch-project suite: **260 passed, 0 failed, 0 skipped**. This includes 259 repository tests and one scratch-only native asset lifecycle test. [NUnit results](Painting%20Repair%20Tests.xml). The final C# editor build passed with zero warnings/errors. [Native fixture source](First%20Paint%20Asset%20Regression.cs.txt) is retained for reproducing the asset-creation check in a scratch project.

The painting audit reproduced five failures in six new initial tests: empty operations allocated maps, shared paint assets escaped validation, concurrent strokes could overwrite each other's transactions, cancellation left destroyed working textures bound until another update, and external undo left a stroke active. A further test reproduced palette reordering corrupting the interpretation of paint in another loaded group sharing the profile.

The fixes add allocation guards, tile paint ownership validation, one active stroke per group, immediate binding restoration, safe external-undo termination, and coordinated palette remapping/undo across loaded groups. Lost GPU textures rebuild from committed pixels. Asset saves flush editor strokes before serialization, and scene save/reload suspension occurs after that flush. No synchronous readback was added to brush movement.

Native first-use testing in the scratch project also caught a redo failure from registering persistent paint asset creation as an object-creation undo. Tile references and pixels now participate in undo while the persistent asset stays available for redo. The save callback excludes nested paint-asset creation so it cannot end ApplyDab reentrantly. Native verification creates two fresh paint assets, saves before mouse-up, loads the actual saved files, and checks undo/redo including restored edge pixels. Its fixture assets live only in the scratch project and are removed after the test.

New coverage also checks channel eight, independent exclusions/restoration, native serialized pixel round trips, multiple dabs along all four junction borders, shared-profile reorder undo/redo, and avoiding transient material serialization. This audit does not establish behavior for unopened scenes sharing a profile or measure interactive brush feel. No authored demo or terrain assets were changed during the repair.

## Migration and authoring repair — 12 September 2026

The actual editor error was `Terrain_(S): palette differs`. Migration now preflights inline and offers **Migrate shared material using primary base** for connected tiles sharing the legacy primary material. This deliberate conversion copies primary palette/alphamaps onto new TerrainData and preserves original assets. It is not an arbitrary palette merge; group texture alignment can differ from the repeated legacy projection. Successful migration selects the group and opens Materials with painting enabled. Paint/erase/exclude/restore buttons and explanations are above appearance controls.

Final verification: **262 passed, 0 failed, 0 skipped** ([results](Migration%20Repair%20Tests.xml)). The scratch native fixture migrates the actual nine-tile demo, checks all copied palette assignments and valid group bindings, undoes back to original TerrainData, redoes to a valid nine-tile group, paints afterward and renders it. An attached UI Toolkit test selects paint/erase for an Auto material and checks the visible explanation. [Migrated render](Migrated%20Nine%20Tiles.png), [scratch fixture source](Migration%20Regression.cs.txt). No canonical demo/terrain assets were modified by this validation.

## Protected automatic rock coverage

Final full suite: **265 passed, 0 failed, 0 skipped** ([results](Rock%20Protection%20Tests.xml)). The C# editor build also passed without warnings/errors.

Automatic Stone/Rock/Cliff materials now default to protected coverage through the per-entry Paint protection setting. Protection can be forced On for custom artwork or Off to unlock it, and does not apply to Manual layers. The shared ForwardLit/DepthNormals weight resolution retains protected base weights after height blending and applies local overrides only to the remaining budget. Protected channels are excluded from the override sum, and protected procedural claims ignore exclusion masks. No texture rebake or wetness publisher change is involved.

Regression cases render a terrain with flat ground and a rocky slope, then paint grass at full strength. They check retained rock coverage, full grass on flat ground, normalized remaining weights, terrain/rule edits without repainting, existing exclusion/override masks when locking, and both height-blend settings. Runtime guards and the Materials UI prevent direct painting/exclusion of locked materials. These rules concern Elementa's override/exclusion brushes, not editing the underlying Unity alphamaps.

The performance CSV below predates automatic coverage protection; this repair did not repeat GPU timing measurements.

## Performance measurement

[Raw CSV](Performance.csv): fixed 1280×800 camera on the four-tile example; six/eight active layers; all combinations of stochastic/triplanar projection; empty or painted overrides. Forty editor updates per case, twelve warmup updates. GPU values come from URP's camera profiling sampler with editor GPU profiling enabled, allowing its delayed results to settle. They cover the camera render, not an isolated terrain-only pass. CPU render values are camera submission duration and must not be interpreted as GPU time.

Across this short run: **0.544–0.924 ms GPU camera time**, **0.541–0.656 ms CPU submission**, **2.800–10.110 ms** for the measured first dab across four tiles, and **4.415–10.028 ms** for commit. All cases recorded **zero unchanged property publications**. Painted cases allocate **8 MiB canonical override data** across four 512² tiles, plus **8 MiB GPU working textures**. Exclusions allocate independently and can double those paint allocations. Each complete 2048² eight-layer CS/NOH BC7 pair contains approximately **85.3 MiB** of compressed mip data; native/driver overhead and old referenced pairs are additional.

These are short editor measurements, not a standalone-player frame-rate guarantee or a statistical comparison of projection algorithms. Initial runs using an external custom command-buffer marker did not report GPU samples; the retained CSV uses the actual URP camera marker. Per-dab synchronous readback is absent. Commits are synchronous and can stall, so sustained gameplay painting requires further latency profiling. Managed distant basemaps remain disabled, increasing far-view terrain work.

## Practical limits

Validation covers the implementation and reproducible fixtures. An artist should still perform the documented ridge/path/grass/exclusion/weather/undo/reopen exercise to approve brush feel and palette art direction. Standalone-player build size/variant coverage, sustained large-world GPU budgets, other graphics APIs, and in-game UI are not established by this editor run. No puddles, new stable cavity shading, foliage or directional snow were added.

Full logs, before/after migration images, scratch scenes and projects remain outside the repository in the root project directory. The canonical project contains the authoring guide, source manifest/converter, curated content, two example scenes, executable validation helpers and this report.
