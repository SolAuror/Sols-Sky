# Elementa Landscape Designer

Unity 6000.3.9f1 · URP 17.3 · Terrain Tools 5.3.3

## Start designing

Open **Tools → Elementa → Control Panel → Landscape → Create landscape…**. Creation starts fresh and never restores deleted demo terrain. The currently saved demo has one existing terrain with a palette mismatch; keep it, use the explicit repair action, or create a separate new landscape.

The UI Toolkit wizard guides you through name/destination, terrain dimensions, surface preset and a final review. Save an untitled target scene first. The default is a flat **2×2 grid of 512 m tiles**, with 128 m vertical range, 257² height samples and 512² control/paint maps. One quarter of the height range is reserved below the initial surface for lowering. The suggested surface is five metres above the selected water body, or world height zero without one. Change the initial elevation and southwest corner in Terrain; the Scene-view grid previews placement before creation. Advanced contains sample resolutions.

Temperate is selected by default; Volcanic Coast is also available. Creation makes an independent editable profile, reuses valid library arrays, assigns colliders, terrain layers, neighbours and material bindings, then selects and frames the group. Choose **Start sculpting** or **Start painting**. A soft round brush is ready with visible diameter and strength. Back and Cancel create no files; cancellation or failure during creation rolls back its new objects and files. Successful creation is one Undo operation. Assets are retained for reliable Redo.

To adapt existing terrain, use **Migrate terrain and connected neighbors**. If connected terrains display the legacy primary terrain's shared material but have different saved palettes, **Migrate shared material using primary base** copies that visible primary palette/base onto new editable TerrainData. Original terrain assets remain intact. The editable copies are stored beside the scene. Migration preserves heights, holes and Manual/Auto rule models.

To paint, select a material card in **Materials**, choose **Paint material**, then drag in Scene view. To remove a path, select **Path → Remove material** and drag over it. This works on base terrain paint, automatic rules and brush overrides together. **Restore removed material** reveals the original coverage; painting the material deliberately clears its removal mask under the brush. Protected automatic rock remains visible on slopes, and the fallback ground cannot be removed. Choose a different fallback in Setup if necessary.

**Advanced → Clear painted overrides** is the renamed Erase to automatic operation. It only clears brush overrides; it does not remove a path stored in base terrain paint. Advanced also retains Exclude material and Restore excluded for automatic rules only.

1. **Setup:** choose the group, inspect its explicit tile list and shared profile, resolve validation messages using collider, connection or palette repair actions, and duplicate a profile before making an independent variant. Tiles must be axis-aligned, unscaled, edge-connected, and have matching dimensions and sample resolutions. Materials use stable IDs; use the palette actions here to reorder or remove them rather than editing channel order directly in an Inspector.
2. **Sculpt:** select Raise, Lower, Smooth, Flatten, or Stamp; choose a brush and drag in Scene view. Diameter is in metres. Flatten samples the height at stroke start. Escape cancels the entire stroke; Alt retains camera navigation. Connected PaintContext gathering/scattering handles neighboring heightmaps.
3. **Materials:** select a thumbnail, adjust scale, normal strength, tint, smoothness or AO, and choose a local editing operation. Brush strokes can cross tile boundaries. Appearance changes affect the shared profile; brush marks belong to the touched tiles.
4. **Rules:** choose Gentle ground, Exposed cliff, or Shoreline, then tune slope, altitude, feather widths and strength. Water-relative ranges follow the existing global water level. The graph shows the selected slope rule before competition; use GPU coverage views to inspect the actual result. Legacy polynomial controls remain under Advanced. Switching the rule model is an intentional edit.
5. **Preview:** select a material and inspect final coverage, automatic coverage, override or exclusion masks. Snow and wetness sliders are temporary and scoped to this group. End preview restores simulation-driven rendering. The probe reports the terrain collider's position, height and slope, not exact GPU cavity or weights.

Save normally. Paint commits at stroke completion and editor lifecycle boundaries. Closing the page, changing groups, reloading assemblies, closing the scene or changing Play Mode releases previews. Terrain material instances and forced basemap distances are suspended before scene serialization and restored for rendering afterward.

Asset saves also finish pending editor strokes. Save a new profile before its first paint stroke. Newly created paint assets remain available for redo when undo restores a tile's previous reference; unused assets can remain after undo or cancellation. Empty erase/restore operations and zero-strength dabs do not create paint storage. Each tile needs its own paint asset, and only one runtime stroke may edit a group at a time.

Palette reorder/removal remaps all loaded groups sharing the profile as one undo operation. Open the affected scenes before changing a shared palette, or duplicate the profile for independently authored landscapes. Shortcut undo finishes the current stroke first; an external undo that has already restored serialized data aborts any remaining GPU transaction without overwriting the restored data.

## Painting semantics

| Operation | Result |
| --- | --- |
| Paint material | Increase the selected unlocked contribution while reducing other overrides proportionally and clearing its removal mask with the same brush footprint. Protected automatic coverage remains intact. |
| Remove material | Locally suppress this material in manual base paint, automatic claims and explicit overrides. Protected rock and fallback ground remain. |
| Restore removed material | Reduce only this material’s removal mask, revealing its previous contributions. |
| Clear painted overrides (Advanced) | Reduce all explicit contributions, revealing the current underlying result. Base terrain paint and removal masks remain. |
| Exclude material | Suppress an unlocked material's automatic claim. Protected materials and the fallback cannot be excluded. |
| Restore excluded | Erase this material's exclusion mask. |

**Paint protection** is available in Materials and Rules. Its default **Rock Materials** protects Auto layers whose TerrainLayer name contains stone, rock or cliff, including existing Stone2 artwork. **On** protects any Auto layer; **Off** unlocks it. Manual layers remain paintable. No rebake or asset migration is required. Protected automatic materials cannot be painted directly, removed or excluded; existing masks are retained but cannot override their protection.

For example, if the resolved base is 80% protected rock, a full grass stroke produces 80% rock and 20% grass. Sculpting and rule changes still update the rock coverage. Erase removes local overrides while keeping the current protected result. This applies to Elementa's non-destructive brushes; Unity's native Terrain brush still edits the legacy base alphamaps.

New profiles use an automatic soil fallback when no rule claims the ground. Legacy profiles retain their original Manual/Auto base resolution. Removal attenuates manual base contributions before the automatic budget is computed, automatic claims before normalization, and overrides before final composition. Fully Manual palettes normalize surviving contributions or use the fallback when none survive. Exclusions retain their automatic-only meaning. Base height blending precedes explicit paint. Without protected coverage:

`final = (1 - total override coverage) * base + override`

With protection, protected layers retain their resolved base weights. Let `P` be their summed coverage and `U` the summed unlocked override coverage (normalized if above one). Each unlocked layer resolves as `(1 - U) * base + (1 - P) * override`. Protected override channels do not consume the paint budget. Final weights remain normalized, including at soft slope transitions.

Final weights drive colour, normals, surface properties, snow susceptibility and grouped sand wetness response. Removing a palette entry returns its override coverage to automatic and removes its exclusions and removal masks; reorder and removal remap alphamaps and custom masks as one undo operation. Stable baked slice IDs keep retained materials correctly mapped until the next explicit rebuild. Adding new artwork requires rebuilding.

**Apply matching material settings** copies rules and appearance for matching TerrainLayer assets. It preserves palette IDs, artwork conversion settings and local paint. A new preset profile is an editable asset, not a runtime dependency on the donor project.

## Storage and runtime boundary

Each tile's `SolLandscapePaintData` stores versioned canonical byte arrays. Version 2 appends two linear RGBA8 removal maps to the existing override and exclusion pairs. Each pair is allocated independently on first use; version-one data and four-map snapshots remain readable. At 512² each pair is 2 MiB CPU plus 2 MiB GPU. All three kinds together use 6 MiB canonical plus 6 MiB GPU per tile, excluding Undo snapshots and temporary brush targets. Resolution controls resize existing maps while retaining endpoint samples. A four-tile stroke is one transaction.

`SolLandscapeStroke` exposes `BeginStroke`, `ApplyDab`, `CommitStroke`, `CancelStroke` and `Dispose` without UnityEditor references. `BeforeTileChange` and `Committed` let callers supply persistence and undo integration. Dabs run on GPU without synchronous readback; commit reads only each touched tile's accumulated dirty rectangle. Large commits can stall and must be measured for gameplay use. CPU bytes become authoritative at commit; cancel restores snapshots. Runtime callers must finish or cancel a stroke before saving or changing its palette/resolution. Use cloned TerrainData, matching collider data and cloned paint assets for gameplay edits.

Profiles retain direct terrain/paint Shader references so build dependency discovery includes the brush shader. The terrain's local projection/height/debug variants use multi-compile to survive runtime keyword changes. The editor UI and sculpt integration live in editor assemblies; an in-game interface is a follow-on project.

## Asset organisation

New landscapes use this scene-adjacent structure:

```text
<Scene folder>/<Scene name>/Landscapes/<Landscape name>/
    Landscape.asset
    Profiles/
    TerrainData/Tile_00_00.asset …
    Paint/
    Generated/Bakes/0001/ …
    Archive/
```

`Landscape.asset` stores identity, scene GUID, explicit ownership and a reversible move manifest. Profile duplication, paint allocation, migration and array rebuilding use the same asset-location service. Shared source textures, TerrainLayers and starter arrays remain in the library. **Setup → Reveal assets** selects the manifest in the Project browser.

**Organise assets** previews GUID-preserving Unity asset moves. Scene and prefab references, other loaded groups and typed asset references prevent shared dependencies from being moved. Orphan assets without recorded ownership stay in place and are listed for explicit ownership assignment. Clearly owned unused files go into Archive. Apply reviewed moves writes a before/after report beside the manifest. Reverse recorded moves restores the old paths when the destinations are free.

Renaming a scene or Save As does not silently move assets. Use **Organise assets → Preview relocation beside current scene**, inspect the destination, then apply the moves. The manifest keeps its landscape identity and references remain GUID-based.

## Material library and conversion

Twelve curated donor materials, 45 terrain brushes, 45 corresponding heightmaps and the brush importer preset live under Elementa's Landscape area. Brushes stay under `Editor`, outside `Resources`. `Library/DonorManifest.json` records source roots, working textures, packing and optional height sources. Original donor files are retained under `Library/Sources`; normalized PNG working inputs are separate. `Library/NormalizeSources.py` reproduces the conversion with Python, Pillow and NumPy. These TIFFs mark their fourth sample as unspecified data: the converter exposes it as alpha in memory without altering the source file. Separate unsigned 16-bit heights are scaled across the full 0–65535 range before 8-bit working output. Source TerrainLayer smoothness/AO remaps are retained in the manifest and baked once. No donor weather, water, fog or vegetation runtime framework was imported.

| Palette | Materials |
| --- | --- |
| Temperate | Grass A, Grass Soil A, Heather A, Pebbles B, Cliff Mossy E, existing Dirt, Path, Stone2 |
| Volcanic Coast | Black Sand A, Black Sand Rocks B, Grass Moss A, Rock Jagged B, Tidal Pools B, Pebbles B, existing Dirt and Path |
| Additional library | Stone A and Cliff Mossy A, both with separate height artwork |

The library can exceed eight entries; each active profile is limited to eight. Temperate starts with distinct gentle-ground and cliff rules, a restricted heather band, modest pebbles and a soil safety fallback. Volcanic Coast separates shoreline sand, exposed rocks and higher moss.

| Input convention | Smoothness | AO | Height |
| --- | --- | --- | --- |
| Legacy Sol | Source R passed through the authored W remap (existing inputs use roughness with an inverted remap) | G with authored remap | Separate height R if assigned, otherwise source B |
| HDRP mask | A with authored remap | G with authored remap | Separate height R if assigned; otherwise neutral 0.5 |

HDRP blue is a detail mask and is never treated as height. The output is CS (sRGB RGB colour + A smoothness) and NOH (linear RG normal XY + B AO + A height), with explicit mip chains and BC7 compression. Normal green flipping follows the working normal importer. Existing bake-time remaps remain bake-time operations; live tint/smoothness/AO are optional adjustments with identity defaults.

Artwork fingerprints include texture dependency hashes, channel convention, optional height, conversion version and array resolution. Painting, rule changes, tint and live normal strength do not invalidate artwork. Each managed profile publishes complete array pairs into numbered `Generated/Bakes/0001`, `0002`, … folders under its landscape. Both new arrays are built and validated before publishing the new pair; previous valid pairs are retained. Unused clearly owned pairs can be moved to Archive by Organise assets; no permanent deletion is part of this workflow.

## Rendering ownership and constraints

`SolLandscapeGroup` owns a transient material instance and per-tile bindings. `Terrain.SetSplatMaterialPropertyBlock` publishes arrays, local controls, rules and preview values while preserving unrelated entries. Release restores only owned entries into the current block. Multiple groups do not share palette, mask or diagnostic state. Group origin defines texture projection/variation; masks use tile-local UVs with matching edge sample positions. Idle publication skips unchanged values.

Unmigrated scenes retain the legacy shared-primary appearance: a single `SolLandscapeDriver` also binds unmanaged terrains in the same scene using the exact same material. These recipients consume the primary terrain's controls, palette and projection origin, as they did under the former global shader contract. Their authored TerrainData and material assignments remain unchanged. Multiple legacy drivers sharing that material are ambiguous, so automatic follower binding is disabled in that case. Explicit groups take ownership before publishing their independent tile controls.

Normal snow comes from `SolEnvironmentWorld`. Normal wetness remains published by `SolWaterWetness`; the landscape driver never writes `_Sol_SurfaceWetness`. The effective-wetness overload accepts a group preview value and final-weight sand response. Existing wetness consumers retain the original entry point. Snow cover remains saturated and the snow include has no temperature dependency.

ForwardLit and DepthNormals share weight, normal and snow resolution. Colour-only variation stays out of DepthNormals. Stock URP geometry, holes, shadows and instancing paths remain intact. Wetness is applied once before PBR; no MixFog, DBuffer or GBuffer integration is added.

Managed groups force full terrain rendering at all distances. This avoids stale basemaps but increases distant terrain cost. A material-consistent distant representation, measured sampling optimizations, stable cavity/puddles, wind-directed snow, foliage and in-game UI remain deferred.

## Verification

`SolLandscapeDesignerTests` covers GPU paint semantics, four-tile borders, cancellation, persistence round trips, undo/redo, sculpt PaintContext seams, stable palette remapping, property-block isolation/restoration, native UI activities, shader variants and Play Mode editing on cloned data. Run together with `SolLandscapeSnowRegressionTests`, lighting and control-panel regressions using a graphics device.

`SolLandscapeValidation.BuildMaterialPreview` creates a separate scene showing all twelve donors plus the three retained legacy materials. `SolLandscapeValidation.BuildExample` creates/rebuilds the separate example and starter artwork, saves/reopens it, compares paint bytes and captures a fixed camera. `SolLandscapeBenchmark.Start` is batch-only: it uses transient clones, tests six/eight layers with projection combinations and empty/painted/removal masks, and writes CSV to the parent project directory. CPU camera submission and GPU recorder timing are distinct measurements; an unavailable GPU counter is reported explicitly.

Designer acceptance: create a landscape; sculpt a ridge across a seam; paint a path; remove part of it; repaint; undo/redo; save and reopen; reveal the organised files. The example and automated checks support this exercise; artist approval is a separate visual judgment.
