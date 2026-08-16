# Sol Water 2 and Environment World

Water 2 is a Sol-integrated implementation that selectively ports and adapts proven WaterFX techniques where they materially improve correctness. Ported behavior is wrapped in Sol's renderer, world-origin, weather, profile, and testing contracts; direct donor assets or kernels may be retained during parity work and distinguished during the later cleanup pass.

## First-run entry points

- Open `Assets/Scenes/Ocean Laboratory.unity` for the isolated High-tier ocean slice.
- Open `Assets/Scenes/Sols_Water2_Demo.unity` for the standalone Water 2 demo.
- `Assets/Scenes/SolsWeather_Water2_Regression.unity` is the one-way migrated weather-demo copy. It is generated on demand by `Tools > Sol Environment > Water 2 > Create Regression Demo Copy` and is not committed. The original demo is unchanged.
- Use `Tools > Sol Environment > Water 2 > Install Renderer Feature` if another renderer data asset needs Water 2.
- Use `Tools > Sol Environment > Water 2 > Convert Open Scene (One Way)` only on a scene copy. The converter disables legacy water authorities and adds the new world/body model; it does not install a compatibility bridge.
- The Sol renderer is configured to always use an intermediate color target. Water 2 requires this for scene-color refraction and SSR in Game and Scene views.
- Planar reflections are currently experimental and disabled in the shipped quality profile. Calling a nested URP camera render from `endCameraRendering` conflicts with Unity 6 Forward+ jobs; SSR and reflection-probe/sky fallback remain active while planar rendering is moved to a safe scheduled path.

## Runtime ownership

`SolEnvironmentWorld` publishes one immutable deterministic environment state. It currently adapts the proven Sol time/weather producers while exposing the clean contract consumed by replacement systems.

`SolWaterWorld` is the authority and registry for stable `SolWaterBodyId` values. It owns water commands/snapshots and the query service. Each ocean, lake, river, pool, or waterfall is a `SolWaterBody` with its own optics, level, flow, reflection policy, bounds, interaction zone, and hydrology binding.

`SolEnvironmentCameraRegistry` isolates camera matrices, cut detection, resolution, atmosphere history, SSR color/validation history, underwater body, and planar-reflection budget. SSR history is invalidated on cuts, resolution changes, floating-origin shifts, body-set changes, quality changes, and SSR disable/reenable transitions. Split-screen and reflection work must never store these resources in static shader state alone.

`SolWorldOriginService` stores the double-precision logical origin. Gerstner and FFT phases use logical coordinates, so a local transform rebase does not restart waves.

## Rendering tiers

| Tier | Ocean waves | Reflections | Atmosphere |
|---|---|---|---|
| Low | Deterministic CPU/HLSL Gerstner | Reduced SSR setting, probe/sky fallback | Full-resolution analytic Beer-Lambert height/distance fog |
| Medium | 2 x 128-squared inverse-FFT cascades | Half-resolution SSR, optional planar, probe/sky fallback | Half-resolution 16-step directional volumetrics with bilateral reconstruction |
| High | 4 x 256-squared inverse-FFT cascades, spectral chop, pixel-sampled normals, and crest foam | Optional per-camera planar, SSR, probe/sky fallback | Half-resolution 32-step temporal volumetrics, local density/exclusion volumes, point/spot lights |

The ocean renderer is a camera-relative instanced projected-density quadtree. Four camera-local roots are recursively selected by projected patch size, distance, frustum, horizon, and a bounded leaf budget. Each leaf carries neighbour availability and seam flags; odd fine-edge vertices collapse to the coarse endpoint before wave displacement. Bounded crack-hiding skirts, a deep horizon skirt, and logical-origin phase continuity remain active. The resolved water prepass packs stable body hash, device depth, and an octahedral normal into one texture. It depth-tests explicitly against camera depth, avoiding Unity 6 Scene View's incompatible resolved-MRT/MSAA-depth native-render-pass combinations.

The forward water material uses Beer-Lambert absorption, scattering, physical IOR Fresnel, WaterFX-style world-ray refraction with mirrored screen edges and depth leak rejection, optional sub-pixel RGB dispersion, main-light highlights/shadows, weather roughness, crest and shoreline foam, and Sol's shared atmosphere include. Refraction travel and normalized viewport displacement are bounded independently from clarity, while the accepted refracted world distance drives absorption and shallow-to-deep scattering. Surface extinction is kept consistent with the active body's underwater density so the surface cannot remain implausibly clear while submerged visibility is short. Crest and shoreline confidence are domain-warped with three derivative-filtered, incommensurate logical-world foam samples, then lit by the current sky and main light instead of being composited as unlit white. The logical origin is included before sampling, so foam remains stable through camera motion and floating-origin shifts. Water applies transparent atmosphere once; the opaque atmosphere pass runs before water.

Planar reflections are opt-in per body and budgeted to one selected body per source camera per frame. Reflection cameras do not recursively render Water 2. The base reflection mirrors the live Sol zenith/horizon/nadir gradient, including sun-relative dawn/dusk warmth; ambient trilight is used only for a non-Sol skybox. Profiles independently bound fallback reflection energy and grazing-horizon strength. Raw SSR uses adaptive screen-space traversal and binary hit refinement, rejecting far/sky depth, water self-hits, submerged geometry, back faces, discontinuities, and screen-edge escapes. Medium/High half-resolution hits are reconstructed at full resolution with body/depth/normal-aware filtering and validated per-camera temporal history. Fresnel, roughness, confidence, edge fading, an explicit SSR-valid bit, and an HDR luminance ceiling bound SSR before a valid planar result receives highest priority.

`SolWaterRendererFeature` exposes Raw SSR, Validated SSR, Reflection Confidence, Fallback Sky, Foam Confidence, Refraction, and Caustics debug modes on the renderer feature asset.

Underwater composition resolves the active body separately for every camera and uses that body's depth, RGB extinction, haze, and waterline transition. Distortion is a subtle continuous world-space field; the former screen-space sine bands were removed. Surface and underwater views project the same caustic field in logical world space and take the same branch: when the live FFT caustic array is available both sample it through `SolWaterSampleCausticArray`, and on Low tier or with caustics disabled both fall back to the authored texture using two rotated incommensurate samples. The surface additionally uses live FFT original-versus-focused area compression as restrained focusing modulation, which the submerged path does not need because the array sample already carries that convergence. Caustics are derivative-filtered, depth-windowed, foam-suppressed, shadow/cloud-rejected, and disabled by the quality profile, preventing the former pixel-grid pattern while keeping the field continuous across waterline transitions.

## WaterFX-informed calculation parity

WaterFX is both a behavioral reference and an implementation donor during parity work. Sol keeps the surrounding ownership and API contracts while porting or adapting the following proven calculations:

- deterministic Gaussian spectral seeds rather than a linear index phase;
- Pierson-Moskowitz wind energy in angular-frequency space with deep-water dispersion;
- WaterFX-compatible positive/negative frequency directional lobes, per-cascade turbulence floors, and height-scale treatment;
- independent positive and negative frequency evolution, preserving a real spatial surface;
- disjoint, smoothly complementary cascade bands so energy is not counted repeatedly;
- vertical and horizontal inverse-FFT displacement;
- normals reconstructed from neighboring fully displaced positions;
- persistent spectrum/displacement/normal-foam resources with per-camera previous normal/foam history and camera-cut invalidation;
- crest breaking from the horizontal displacement Jacobian, gated by wind and confidence;
- continuous distance and geometry-footprint filtering of short cascades, independent of clipmap ring boundaries;
- explicit fine-to-coarse edge stitching with a two-row morph band, eliminating T-junction triangle fans;
- odd seam vertices collapse onto actual coarse-grid endpoints before displacement;
- Medium/High use spectral geometry without layering coherent Gerstner bands; Gerstner remains the deterministic Low-tier fallback;
- spectral normal and foam textures are sampled per pixel, decoupling specular detail from clipmap triangles;
- spectral normal/foam sampling uses an anisotropic screen-footprint filter and derivative-driven cascade fade, preventing short-wave comb aliasing at grazing angles without moving the geometry;
- inverse-FFT checkerboard phase correction is applied before displacement/normal reconstruction, preventing the dotted lattice artifact;
- the frequency grid is centred before inverse FFT, matching the checkerboard correction instead of injecting an alternating spatial lattice;
- cubic cascade handoff distances follow the source visible-area schedule (40/160/800/4800 m);
- shallow water-shaded LOD skirts instead of visible vertical water cliffs;
- geometry-depth-gated shoreline foam, preventing sky/far-depth foam saturation;
- an optional baked shoreline data texture containing signed vertical depth and signed horizontal distance to land, generated from the active terrain by `Tools > Sol Environment > Water 2 > Bake Active Terrain Shoreline Data`;
- shallow-water attenuation of spectral displacement, velocity, and normals before local interaction waves, with the same attenuation applied by immediate CPU water queries;
- signed-distance shoreline breakers follow the baked coast gradient, travel landward, add horizontal crest motion, and feed the same foam confidence and CPU query contracts;
- signed-distance contact fading and derivative-antialiased shoreline foam instead of a hard scene-depth intersection band;
- an authored shoreline mask/detail contract with explicit strength, foam width, wetness, and unmasked-body fallback;
- immutable weather response for cloud-shadow, lightning, rain roughness, and rain normal detail.
- per-camera temporal SSR color and body/depth/normal validation history with explicit disocclusion rejection;
- dynamic Sol-sky reflection fallback, preserving directional horizon warmth when screen-space rays miss.
- physical IOR refraction ray projection with mirrored viewport overflow, displaced-depth leak rejection, RGB dispersion, and refracted-depth absorption;
- texture-shaped projected caustics shared by surface and underwater composition, with FFT focusing modulation plus depth, foam, shadow, weather, and quality gating.

`SolOceanSpectrumMath` exposes the CPU-side distribution and cascade partition for validation. Profile controls include spectral strength, choppiness, breaking threshold, and foam gain. `SolWaterProfile.shorelineDataMapping` stores logical-world XZ center and size, so the bake remains stable through floating-origin shifts; finite bodies and the ocean use the same shader contract.

## Finite water and interaction

Finite bodies use an authored or generated mesh through `SolWaterBody.AuthoredSurface`. The renderer feature issues the prepass and forward draw with per-body material data; the authored renderer's ordinary draw is temporarily suppressed to avoid double rendering and restored on disable.

Phase 5 adds three Unity Splines authoring components. Put each component on the same object as its `SplineContainer` and `SolWaterBody`; required mesh and body components are added automatically.

- `SolRiverGeometry` samples an open spline into a bounded channel mesh. Width and depth curves vary along the reach, bank blend/drop hides terrain contacts, lateral and longitudinal density are bounded, and optional terrain raycasts place each cross-section above authored terrain. Mesh tangents align finite Gerstner waves and foam drift with downstream flow. Rebuilds are deterministic and occur only after authoring changes.
- `SolLakeGeometry` ear-clips a closed spline boundary, including concave boundaries, into a finite lake surface. It provides point-in-polygon queries, configured depth, and boundary foam without using the ocean shoreline bake.
- `SolWaterfallGeometry` builds deterministic separated ribbons along a falling spline and can append a radial plunge pool. Vertex foam grows from lip to impact and the plunge-pool centre supplies a bounded impact zone. Ribbon breakup is local geometry, so it does not change with the camera or logical-origin shifts.

All three implement `ISolWaterGeometry`. `SolWaterBody` discovers that contract and uses it for bounds, containment, base surface, normal, channel depth, local flow, and authored foam. `ISolWaterQueryService` therefore returns the same body ID and sample layout for ocean, river, lake, and waterfall bodies. Generated finite bodies deliberately use bounded Gerstner displacement instead of the ocean FFT clipmap and ignore ocean shoreline data; they retain the shared Water 2 optics, SSR/refraction, foam, interaction-zone, weather, and origin contracts.

For streamed scenes, author and serialize a unique `SolWaterBodyId` in every cell. Enabling or disabling the cell registers or unregisters the body with `SolWaterWorld`; generated meshes and sample caches stay owned by that scene object and are released with it.

`SolWaterInteractionZone` is a bounded, fixed-step GPU shallow-water presentation layer. Zones can be static, camera/target-following, or prebaked. They simulate ripple height, velocity, foam, wetness, impulses, obstacle rejection, and texture-preserving recentering. They never authoritatively change stored water volume.

Add `SolWaterInteractor` to a moving object to deposit velocity-scaled wakes. Add `SolWaterBuoyancy` for allocation-free multi-point buoyancy, flow-relative drag, angular stabilization, and water-body transitions.

Immediate water queries return the deterministic approximate surface. Batched queries complete asynchronously behind `ISolWaterQueryService`; the contract already carries sample age and confidence so a future GPU readback implementation does not change gameplay callers.

## Fog authoring

Add `SolAtmosphereVolume` to Unity Volume Profiles for global or local camera blending. Add `SolAtmosphereDensityVolume` for local density, `SolAtmosphereExclusionVolume` to suppress fog, and `SolVolumetricLight` beside point or spot lights. Local volume bounds currently use the collider's world-space AABB.

Transparent Sol shaders should include `Assets/Sky-and-Water/Water/Shaders/SolAtmosphere.hlsl`. `SolTransparentAtmosphereBinding` sets the per-renderer opt-in value without cloning or mutating shared materials; the shader calls `SolApplyAtmosphereOptIn`.

The environment coordinator captures and restores legacy `RenderSettings.fog` and all atmosphere globals when the final Sol authority releases ownership.

## Hydrology and streaming

`SolHydrologyAsset` contains stable nodes and directed/bidirectional edges for oceans, lakes, reservoirs, river reaches, and waterfall pools. `SolHydrologyWorld` applies rainfall, snow storage/melt, evaporation, capacity-limited head flow, level changes, snapshots, and commands using a deterministic conservative CPU solver.

Edge flux is two-phase and mass-conserving. Node state persists independently of scene-cell lifetime. Bound `SolWaterBody` levels update without rebuilding rendering resources.

`IEnvironmentCellProvider` is backend-neutral. `SolAdditiveSceneCellProvider` is the default additive-scene implementation and publishes stable load/unload/failure/origin-shift events.

## Optics model

Water volume optics follow WaterFX's model, shared by the surface and the submerged
composition through `Shaders/Water2/SolWaterOptics.hlsl` so the waterline stays
continuous.

Transmittance is `max(floor, exp2(-K * rayLength^1.5 * absorptionColor * multiplier))`
with a per-channel floor of `(0.0005, 0.001, 0.025)`, so deep water keeps a blue tail
instead of collapsing to black. A separate saturating extinction term decides how much
of the transmitted scene has been replaced by volume scattering, and scattering
*replaces* the absorbed scene by that extinction rather than being added on top.

Two consequences worth knowing when authoring:

- `absorption` is a per-channel weight normalised by its red component, not a per-metre
  coefficient. Clear water is the ratio `(1, 0.12, 0.02)`.
- `clarityDistance` runs against intuition for open water. The extinction integral
  coefficient grows quadratically with clarity while the sampled path only grows
  linearly, so raising it makes deep water *darker*: extinction is 0.75 at 10, 0.46 at
  18, 0.21 at 30. Deep clear ocean reading near-black is correct, but this is the main
  brightness control for open sea.

`refractionMaximumDistance` bounds the screen-space refraction offset only. It must
never bound the optical path; doing so caps open water at a few metres of absorption and
transmits the sky straight through the surface.

Sun glitter is a GGX lobe with Smith joint visibility, added to the composed colour
*after* the Fresnel lerp. Folding it into the reflection branch attenuates it by Fresnel,
which is 0.02-0.15 at the shallow angles where glitter actually lives. Sub-pixel normal
variance widens the lobe (geometric specular antialiasing), converting detail the
spectral footprint filter discards into lobe width rather than a saturated disc.
`sunSpecularStrength` is the dispersal control: the GGX peak is far above the HDR ceiling,
so high values clamp across the whole lobe and fill it in solid.

Foam is stochastically tiled on a triangle grid (three hash-offset samples blended by
barycentric weights) and advected along the flow, with two half-period-offset copies
crossfaded on `sqrt(sin(phase * pi))` so each copy reaches zero exactly at its own reset.

## Wind units

`WeatherProfile.windStrength` is a 0-3 normalised multiplier. `SolEnvironmentWorld`
converts it to metres per second via `windStrengthToMetresPerSecond` before publishing
`SolEnvironmentWindState.Speed`, because the ocean spectrum derives its
Pierson-Moskowitz peak frequency from a real speed. Passing the multiplier through
unconverted puts the peak wavelength near two metres, which reads as flat water, gates
off crest foam (`smoothstep(3, 6, windSpeed)`) and leaves the anisotropic reflection
smear inert.

## Validation status

- EditMode: 62 passing. Covers the spectral distribution and cascade partition, the
  volume optics curve, displacement inversion, weather wind units, flood extent
  generation, hydrology mass conservation and cell-boundary handoff, quality tier
  degradation, profile validation and serialization, shoreline baking and CPU
  attenuation, stable IDs, wave-origin continuity, per-camera state, SSR signature
  invalidation, and the Phase 5 spline geometry.
- Graphics-enabled PlayMode on D3D11: 22 passing, one `[Explicit]` capture test skipped,
  zero failures. Covers High-tier RenderGraph execution, four-heading SSR parity,
  far-plane and submerged-terrain rejection, localized above-water reflections, and
  half-resolution reconstruction.
- A `-nographics` PlayMode run cannot create URP's own mandatory default render textures;
  use a graphics-enabled batch run for render tests.
- Note that any test which logs an unexpected error poisons every test after it: the
  duplicate-world guards fail the remainder of the run. When reading a failing suite,
  the first failure is usually the only real one.

## Known limitations

Honest statement of what is implemented but unproven, and what is deliberately absent.

**Implemented, not visually verified.** FFT-rendered caustics, anisotropic reflections,
volumetric water lighting, glitter dispersal, advected foam, flood extents and the depth
write all compile and pass their suites, but no test asserts on their appearance. They
have not been reviewed on screen.

**Volumetric lighting is directional only.** Point and spot lights would need the donor's
additional-light loop. The pass also uses the resolved body's optics as uniforms rather
than a per-body lookup, so a scene showing an ocean and a differently-tinted lake at once
will light both with the ocean's turbidity.

**Caustic array has no mips.** Array-slice mip generation is unreliable, so the array is
sampled with explicit gradients and the existing derivative filtering instead.

**FFT and caustics are recorded per camera.** Both are camera-independent, so a second
simultaneous camera duplicates the work. Hoisting them to frame scope requires moving the
FFT normal/foam history off the per-camera registry first.

**Water queries are CPU-side.** The displacement inversion runs on the CPU. The
`ISolWaterQueryService` contract already carries sample age and confidence, so a GPU
readback backend can replace `ProcessBatch` without touching gameplay callers.

**Displacement inversion degrades near breaking.** The fixed point contracts by roughly
the wave steepness per step. Residual is under a centimetre up to steepness 0.5 and rises
to ~0.035 m near breaking, where query confidence drops to report it rather than lying.

**Finite bodies get no FFT.** Rivers, lakes, waterfalls and flood extents are
Gerstner-only regardless of tier, so caustics fall back to the authored texture there and
volumetrics read flatter.

## Deliberately later parity work

- GPU foam and splash particle renderers, and terrain-independent shoreline authoring.
- GPU readback-backed query batches and voxel-volume rather than authored-point buoyancy.
- Full cloud/forecast/climate/audio work informed later by Enviro 3 and UniStorm.

These additions should extend the current contracts rather than restore global managers or
direct module mutation.
