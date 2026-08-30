# Water2 Terrain Safety, Regression, and Cleanup Pass

## Summary

Repair live terrain-texture protection without rewriting existing alphamaps, correct bake staleness
detection, retire obsolete tooling and assets, and improve validation. Preserve current shader
appearance and all divergent/manual texture work.

`Sols_Water2_Demo.unity` is **already** the only scene on disk and the only enabled build entry.
Making it "the sole production scene" is not migration work; it becomes a regression assertion in the
validator.

## Correction to the original premise

`BuildProceduralWeights`
([SolLandscapeLiveAlphamapUpdater.cs:552](../Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeLiveAlphamapUpdater.cs#L552))
copies preserved layers verbatim from the current alphamap and scales only generated layers into the
remaining budget. **Path is protected structurally by `preservePaintedWeightDuringGeneration`, not by
the protection mask.** `InferProtectionMask` correspondingly tests divergence only over generated
indices, so a Path-only difference can never mark a texel protected — and never needs to.

Every requirement below that previously read "preserve Path via the mask" is restated as a test that
Path survives regeneration bit-exactly, which is a property of the generator, not the mask.

## Commit 1 — Mask storage migration (pure format change)

Replace the text-serialized `Texture2D` mask with a binary-serialized editor asset.

- Current mask is 8,389,667 bytes of hex-encoded YAML for 2048² single-byte texels; it rewrites in
  full on every mask save and is versioned in git.
- New storage: a `[PreferBinarySerialization]` `SolLandscapeProtectionAsset` ScriptableObject with a
  format version, TerrainData GUID, width, height, and 4,194,304-byte mask payload. This remains a
  Unity object so `Undo`, dirty tracking, and `SaveAssetIfDirty` operate on the actual stored data.
- Validate version, TerrainData GUID, dimensions, and payload length against `TerrainData` at load,
  replacing the `mask.width/height/format` check at
  [:445](../Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeLiveAlphamapUpdater.cs#L445).
- Relocate out of `Scripts/Landscape/Editor/` to `Assets/Sky-and-Water/Landscape/`, beside the config.
- **Cost:** `Selection.activeObject = mask`
  ([:129](../Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeLiveAlphamapUpdater.cs#L129))
  no longer yields a visual preview. Mitigate with an on-demand "Preview Protection Mask" action that
  builds a throwaway `Texture2D` in memory — not a saved asset.
- Fail closed: a missing, corrupt, wrong-version, wrong-GUID, wrong-resolution, or wrong-length mask
  disables live regeneration and presents Analyze / Repair actions. It must never silently substitute
  an empty mask or regenerate production alphamaps.
- No semantic change. No `TerrainData` writes.

**Verify:** read the pre-migration texture payload and assert byte-equality with the new binary asset.
Record and compare exact payload hashes and protected-texel counts; do not assume the count from the
66.255% baseline. Exercise every fail-closed validation case without touching `TerrainData`.

## Commit 2 — Per-texel protection and compaction

Replace rectangle-wide marking at
[:457](../Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeLiveAlphamapUpdater.cs#L457)
with per-texel comparison over the callback rect, reusing the predicate already implemented in
`InferProtectionMask`
([:614](../Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeLiveAlphamapUpdater.cs#L614))
— scoped to the rect, not the full terrain.

### Byte-exact classification, measured rather than predicted

This is the substantive design change:

- Do **not** predict Unity's quantization. Alphamap bytes are produced inside `SetAlphamaps`, not by
  any code in this repo: with six layers Unity writes two alphamap textures and normalizes weights
  across layers on write. `FloatToByte` is a private helper in
  [SolLandscapeArrayBaker.cs:1305](../Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeArrayBaker.cs#L1305)
  and is not used by the alphamap generator, which performs no quantization at all — its only
  rounding is coordinate math at
  [SolLandscapeAlphamapGenerator.cs:340](../Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeAlphamapGenerator.cs#L340).
  Reusing it here would encode an assumption about engine internals as though it were a fact.
- Obtain ground truth instead: write procedural output to an unsaved clone `TerrainData` via
  `SetAlphamaps`, read it back with `GetAlphamaps`, and compare those read-back bytes against
  production's read-back bytes. Both sides then travel the identical engine path, and no assumption
  about rounding or renormalization is required.
- **Marking** protection occurs on any generated-layer byte inequality. A one-LSB manual edit is real
  work and must not be left exposed to the next sculpt.
- **Clearing** protection requires every generated-layer byte to be identical. Nothing is unprotected
  on a tolerance.

This closes both data-loss vectors: existing near-matches cannot be unprotected on a tolerance, and
new one- or two-byte manual edits cannot escape protection.

**Texels with no procedural result.** `BuildProceduralWeights` throws when no rule claims a coordinate.
That throw previously could not reach the paint callback, which only ever set mask bits; routing the
callback through the classifier puts it on that path. An unclaimed texel is therefore treated as
divergent and protected, rather than erroring out and protecting nothing: when the procedural result
cannot be computed, the stored weights must be assumed to be manual work.

**Why the removed tolerance may have existed.** `InferenceTolerance = 2f/255f`
([:22](../Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeLiveAlphamapUpdater.cs#L22))
is being dropped for marking, and nothing records why it was chosen. A plausible reason is that the
write→read round-trip is not perfectly stable. The idempotence test below settles it. If that test
fails, byte-exact **marking** is not viable and the tolerance returns for marking only; byte-exact
**clearing** stands either way, being the conservative direction.

### Compact Redundant Protection

Do not add a second menu item. The existing **Protect Current Texture Work**
([:95](../Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeLiveAlphamapUpdater.cs#L95))
already clears and re-infers the whole mask; compaction is that operation under the stricter clearing
predicate. Extend it rather than duplicating it, and add a separate read-only **Analyze Protection**
action reporting total / divergent / protected / redundant counts with bounded diagnostic samples.

Constraints, unchanged from the original plan:

- Never call `SetAlphamaps` or otherwise alter production `TerrainData`.
- Preview counts before committing; `SaveAssetIfDirty` on the mask only.
- Keep `suppressTextureCallback` so regeneration cannot classify its own writes as painting.
- `Undo.RegisterCompleteObjectUndo` on the mask.

**Create and verify a recoverable pre-compaction checkpoint** outside the repo, recording its payload
hash and dimensions. Do not assume permission to create a git commit. Delete the external checkpoint
only after clone verification and the final production hash gate pass.

### Verify (non-circular)

- **Idempotence — run this first; it gates the whole design change.** Regenerate a region, re-run
  inference, and assert **zero** newly protected texels. Repeat the cycle twice. If the write→read
  round-trip is not stable, byte-exact marking makes every regenerate protect its own output and
  protection ratchets toward 100%, silently defeating the feature. A failure here sends marking back
  to the tolerance per the note above; it does not get worked around.
- Snapshot the mask. Compact. On an **unsaved clone**, clear protection exactly where compaction
  cleared it, run a full `RegenerateRegion`, and assert the resulting alphamap is byte-identical to
  production. This is the property that matters; a count-based check is not.
- Assert Path (and every `preservePaintedWeightDuringGeneration` layer) is bit-exact after a full
  regenerate on the clone — do not assume the `GetAlphamaps`→`SetAlphamaps` float round-trip is
  lossless, prove it.
- Large synthetic callback rect: assert only genuinely divergent texels become protected.
- Assert the post-compaction mask payload hash and protected count match the analyzed result.
- Production `TerrainData` alphamap hash byte-identical before and after the whole commit.

## Commit 3 — Bake fingerprint versioning

`BuildFingerprint`
([SolLandscapeArrayBaker.cs:1291](../Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeArrayBaker.cs#L1291))
resolves to `AssetPathToGUID` and nothing more, so in-place texture edits and importer changes leave
arrays reported fresh.

- Replace with a versioned combined hash. Use `AssetDatabase.GetAssetDependencyHash` per slice over
  the `.terrainlayer` and its diffuse/normal/mask textures — it already covers asset content *and*
  importer settings, so no hand-rolled importer introspection is needed.
- Treat a missing or older-versioned fingerprint as "needs migration," not as fresh.
- Refactor baking into a non-persisting preview path that builds transient arrays and never calls
  `PersistArray` or modifies the config/material. The existing per-slice `FNV1a64` calculation
  ([:732](../Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeArrayBaker.cs#L732)) compares
  generated payloads with the external Phase 1 baseline; it does **not** compare with the loaded
  production arrays and cannot be reused as-is for migration.
- Compare preview and production output one slice/mip at a time using GPU readback to RGBA32, hashing
  the decoded bytes so production arrays do not need Read/Write enabled. Release each readback before
  advancing to bound editor memory. A mismatch reports array, slice, mip, and both hashes.
  Decoded-pixel hashing answers *did the inputs change*, not *are these byte-identical assets* — two
  block-compression encodings can decode near-identically. That is the correct question for staleness
  detection; do not cite the result as asset equality.
- **Retire the Phase 1 baseline mechanism along with it.**
  `BakeTarget(requireTrustedHashBaseline: true)`
  ([:73](../Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeArrayBaker.cs#L73)) backs a menu
  entry; `AssertExpectedMovement` hardcodes the Phase 1 expectation
  `label == "CS" || label == "NOH" && slice == 0`
  ([:753](../Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeArrayBaker.cs#L753)); and
  `AssertComplete` throws unless every baseline key is matched. Left in place once fingerprinting
  supersedes it, that path throws on any legitimate rebake. Remove the baseline loader, its menu
  entry, and `Phase1LandscapeBakeHashBaseline.txt` — or state explicitly why it is kept.
- Store the verified per-slice output hashes in the versioned fingerprint for future checks. Any
  optional scratch report belongs outside the repo and `Assets/` so Unity cannot import it.
- Record the new fingerprint without replacing arrays when output matches; leave arrays untouched and
  report differing slices when it does not.
- Replace the broad `AssetDatabase.SaveAssets()` calls in production landscape tools with scoped
  `SetDirty`/`SaveAssetIfDirty` — 8 sites, of which the production ones are
  `SolLandscapeAlphamapGenerator.cs:80,369`, `SolLandscapeArrayBaker.cs:177,1054,1060`, and
  `SolLandscapeLiveAlphamapUpdater.cs:174`.

**Verify:** an in-place texture edit and an importer-setting change each mark arrays stale; unchanged
inputs stay fresh; preview comparison identifies a deliberately changed slice; production arrays and
material remain unchanged except fingerprint metadata.

**Note on running the migration.** `Migrate Bake Fingerprints` compares production arrays through
`AsyncGPUReadback`, because they are baked with `makeNoLongerReadable` and have no CPU copy. It
therefore needs a real graphics device and cannot run under `-nographics`; it checks
`SystemInfo.supportsAsyncGPUReadback` and refuses rather than silently skipping the comparison. Every
other check in this pass runs headlessly.

## Commit 4 — Validator and editor status surface

Upgrade `SolLandscapeWater2Validator` into the canonical production validator.

- Require exactly one Terrain, landscape driver, WaterWorld, infinite ocean, and wetness integration.
- Assert `Sols_Water2_Demo` is the sole enabled build scene — a regression guard on an already-correct
  state.
- Report offending alphamap/world coordinates, slope, altitude, protection state, dominant layer, and
  all layer weights.
- Unprotected non-Path steep failures → errors. Protected or Path-dominant steep cases → warnings,
  reported and never modified.
- Compact status surface: Water2 wiring, live-update state, mask health and compaction, array
  freshness, shader support/errors/warnings.
- Group existing landscape settings with labels/tooltips; show layer mapping and inactive feature
  flags. **No artistic values change.**
- Console output by default; persistent reports only with an explicit output path. Note that
  `RunLiveSculptVerification` currently writes `G1_LiveSculptProtection_Evidence.txt` unconditionally
  ([:314](../Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeLiveAlphamapUpdater.cs#L314))
  — make that opt-in as part of this commit.

Shader work in scope here, appearance-neutral: keep the six-layer all-layer sampling path and height
blending, do not restore top-K, do not enable stochastic sampling; compile-check lit /
basemap-generation / basemap under Water2's active feature set; remove only phase/diagnostic shader
code proven unused by reference and variant checks.

## Commit 5 — Asset migration and deletion (last, and alone)

Isolated because Unity GUID moves plus bulk deletion produce a diff that cannot be meaningfully
reviewed alongside logic changes.

- Clear the stale `LightingData.asset` reference from the Water2 scene (GUID
  `833e0027fe60af44d9b0f1429fb26639`, one hit) and validate realtime-only lighting without generating
  a bake.
- Move with `AssetDatabase.MoveAsset` so GUIDs stay stable. **Destination changed from the original
  plan:** not `Assets/Scenes/Sols_Water2_Demo/` — Unity reserves `<SceneName>/` beside
  `<SceneName>.unity` as that scene's generated-data folder and manages its contents on lighting
  operations. Use the folder that already holds the config:
  - `DemoTerrain.asset` → `Assets/Sky-and-Water/Landscape/Water2Terrain.asset`
  - `WeatherDemoLightingSettings.lighting` → `Assets/Sky-and-Water/Landscape/Water2LightingSettings.lighting`
  - `M_Metal.mat` → `Assets/Sky-and-Water/Landscape/Materials/M_Metal.mat`
- Hardcoded legacy paths: deleting the 5B–5G diagnostics and `SolLandscapePhase0SpikeSetup` removes 13
  of the 14 sites outright. Only
  [SolLandscapeArrayBaker.cs:30](../Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeArrayBaker.cs#L30)
  needs a real fix — route it through `config.TerrainData`, already the source of truth at
  [:236](../Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeArrayBaker.cs#L236).
- After serialized-GUID, path/name, Addressables/Resources, and `AssetDatabase` dependency checks,
  delete the allowlist: stale
  `LightingData.asset`, `ReflectionProbe-0/1.exr`, `SolsWeather_Demo Baking Set.asset`,
  `M_Metal 1–4.mat`, `PreviewCamera Profile.asset`, `TerrainLayer_Snow.terrainlayer` and its three
  textures, and the `Shaders/Terrain/Phase0` assets. Evaluate the allowlist as a deletion closure:
  references between candidates do not block cleanup, but any inbound dependency from outside the
  candidate set retains the referenced asset and every candidate it still requires. Report every
  retained dependency rather than deleting it.
- Remove the 5B–5G validators/capture tools and Phase0 setup. Retain focused Water2 validation, shader
  compilation, memory, and clone-based sculpt regression coverage.
- Remove the emptied `Assets/Scenes/SolsWeather_Demo/`, `Assets/Scenes/SolsWeather_Demo_Profiles/`, and
  `Assets/Sky-and-Water/Shaders/Terrain/Phase0/`. Restore no currently deleted file.

## Interfaces and Data

- Serializable versioned bake fingerprint: dependency hash + output metadata.
- Protection-analysis result: texel counts + bounded diagnostic samples.
- Analyze / Compact exposed through editor-only APIs and menu UI. No runtime API or shader property
  contract changes.
- Six terrain-layer mappings and all material/config values unchanged.

## How verification runs

The project has **no test infrastructure**: zero `.asmdef` files, no test assemblies, and no
`[Test]`/`[UnityTest]` attributes anywhere under `Assets`. `com.unity.test-framework` is listed in
`Packages/manifest.json` but nothing uses it; all scripts compile into the default `Assembly-CSharp`
and `Assembly-CSharp-Editor`.

Every check below is therefore an **editor menu action** following the existing
`VerifyLiveUpdateFromCommandLine`
([:186](../Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeLiveAlphamapUpdater.cs#L186))
pattern: run the scenario on an unsaved clone, assert, log PASS/FAIL with the measured numbers.
Introducing the project's first asmdefs to host a test assembly is a structural change with
compile-order risk and is deliberately out of scope for a safety pass.

Compile checking is available headlessly — Unity 6000.3.9f1 matches `ProjectVersion.txt` and
`Library/ScriptAssemblies` is already built. Six `InitializeOnLoad` sites lack `isBatchMode` guards,
but `SolLandscapeLiveAlphamapUpdater` is guarded
([:36](../Assets/Sky-and-Water/Scripts/Landscape/Editor/SolLandscapeLiveAlphamapUpdater.cs#L36)),
so headless compilation cannot trigger terrain writes. Do not run bake or regenerate menu actions
headlessly.

## Verification gate

- Runtime and editor C# projects compile with **zero errors and no new warnings attributable to
  changed files**. A blanket zero-warning gate is not achievable across the donor asset-store code and
  is not a useful signal here.
- Clone-only sculpt checks: texture updates synchronous, localized, Path/manual texels bit-exact.
- Protection classification is idempotent: regenerate → re-infer yields zero newly protected texels
  across two cycles. Byte-exact marking does not ship without this.
- Missing/corrupt/mismatched protection storage disables regeneration and cannot create an empty mask.
- Production `TerrainData` alphamap hash byte-identical across commits 1–4.
- Pre/post migration and compaction mask hashes, counts, dimensions, and TerrainData GUID recorded and
  verified; the external recovery checkpoint is removed only after this gate passes.
- Shader support/compile checks re-run; active keywords, material properties, and array bindings
  compared against baseline.
- Final dependency scan, clean compile, no generated evidence or build artifacts, and a git diff review
  excluding the existing Documentation changes.

## Assumptions

- Compaction is safe **for the current config**. Texels unprotected because the generator reproduces
  them byte-identically will follow the rules if those rules later change — that is the intent of
  compaction, not a regression.
- Every divergent texel is intentional work unless manually reviewed.
- No appearance tuning, layer rebalancing, normal-strength changes, texture resizing, or array
  replacement.
- The ~106.7 MB terrain texture footprint is documented as a future optimization, not touched here.
