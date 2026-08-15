# Sol Water 2 and Environment World

Water 2 is an original Sol implementation informed by the visible capabilities and architectural lessons of the reference packages. No reference-package source, shaders, compute kernels, profiles, or assets are part of this implementation.

## First-run entry points

- Open `Assets/Scenes/Ocean Laboratory.unity` for the isolated High-tier ocean slice.
- Open `Assets/Scenes/SolsWeather_Water2_Regression.unity` for the one-way migrated weather-demo copy. The original demo is unchanged.
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

The ocean renderer is a camera-relative instanced projected-density quadtree. Four camera-local roots are recursively selected by projected patch size, distance, frustum, horizon, and a bounded leaf budget. Each leaf carries neighbour availability and seam flags; odd fine-edge vertices collapse to the coarse endpoint before wave displacement. Crack-hiding skirts, a deep horizon skirt, and logical-origin phase continuity remain active. The water prepass stores stable body hash, face classification, device depth, normal, skirt classification, and foam.

The forward water material uses Beer-Lambert absorption, scattering, physical IOR Fresnel, thickness-aware refraction with screen-edge rejection, main-light highlights/shadows, weather roughness, crest and shoreline foam, and Sol's shared atmosphere include. Crest and shoreline confidence are domain-warped with three derivative-filtered, incommensurate logical-world foam samples, then lit by the current sky and main light instead of being composited as unlit white. The logical origin is included before sampling, so foam remains stable through camera motion and floating-origin shifts. Water applies transparent atmosphere once; the opaque atmosphere pass runs before water.

Planar reflections are opt-in per body and budgeted to one selected body per source camera per frame. Reflection cameras do not recursively render Water 2. The base reflection mirrors the live Sol zenith/horizon/nadir gradient, including sun-relative dawn/dusk warmth; ambient trilight is used only for a non-Sol skybox. Raw SSR uses adaptive screen-space traversal and binary hit refinement, rejecting far/sky depth, water self-hits, submerged geometry, back faces, discontinuities, and screen-edge escapes. Medium/High half-resolution hits are reconstructed at full resolution with body/depth/normal-aware filtering and validated per-camera temporal history. Fresnel, roughness, confidence, edge fading, and an HDR luminance ceiling bound SSR before a valid planar result receives highest priority.

`SolWaterRendererFeature` exposes Raw SSR, Validated SSR, Reflection Confidence, Fallback Sky, and Foam Confidence debug modes on the renderer feature asset.

Underwater composition resolves the active body separately for every camera and uses that body's depth, RGB extinction, haze, and waterline transition. Distortion is a subtle continuous world-space field; the former screen-space sine bands were removed. Water now adds shadow-rejected, light-space caustic modulation only where valid scene depth exists, keeping the pattern stable under camera movement and dynamic resolution.

## WaterFX-informed calculation parity

WaterFX remains a behavioral reference only. The corresponding Sol implementation is original and now follows the same physically meaningful stages rather than matching source code structure:

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
- cubic cascade handoff distances follow the source visible-area schedule (40/160/800/4800 m);
- shallow water-shaded LOD skirts instead of visible vertical water cliffs;
- geometry-depth-gated shoreline foam, preventing sky/far-depth foam saturation;
- an optional baked shoreline data texture containing signed vertical depth and signed horizontal distance to land, generated from the active terrain by `Tools > Sol Environment > Water 2 > Bake Active Terrain Shoreline Data`;
- shallow-water attenuation of spectral displacement, velocity, and normals before local interaction waves, with the same attenuation applied by immediate CPU water queries;
- signed-distance contact fading and derivative-antialiased shoreline foam instead of a hard scene-depth intersection band;
- an authored shoreline mask/detail contract with explicit strength, foam width, wetness, and unmasked-body fallback;
- immutable weather response for cloud-shadow, lightning, rain roughness, and rain normal detail.
- per-camera temporal SSR color and body/depth/normal validation history with explicit disocclusion rejection;
- dynamic Sol-sky reflection fallback, preserving directional horizon warmth when screen-space rays miss.

`SolOceanSpectrumMath` exposes the CPU-side distribution and cascade partition for validation. Profile controls include spectral strength, choppiness, breaking threshold, and foam gain. `SolWaterProfile.shorelineDataMapping` stores logical-world XZ center and size, so the bake remains stable through floating-origin shifts; finite bodies and the ocean use the same shader contract.

## Finite water and interaction

Finite bodies use an authored mesh through `SolWaterBody.AuthoredSurface`. The renderer feature issues the prepass and forward draw with per-body material data; the authored renderer's ordinary draw is temporarily suppressed to avoid double rendering and restored on disable.

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

## Validation status

- The focused Water 2 EditMode suite covers spectral distribution/energy partition, profile validation/serialization, shoreline signed-distance baking and CPU attenuation, stable IDs, wave-origin continuity, per-camera state, and SSR signature invalidation.
- The focused graphics-enabled Water 2 PlayMode suite covers High-tier RenderGraph execution, four-heading SSR parity, far-plane and submerged-terrain rejection, localized above-water reflections, and half-resolution reconstruction.
- On Unity 6000.3.9f1, the complete EditMode suite passes 41/41 and the regular D3D11 PlayMode suite passes 22/22. The explicit five-view demo capture test also passes independently, including its baked-shoreline close view.
- The project imports and compiles in Unity 6000.3.9f1 with URP 17.3.0.
- A `-nographics` PlayMode run cannot create URP's own mandatory default render textures; use a graphics-enabled batch run for render tests.

## Deliberately later parity work

The current milestone is the ocean vertical slice plus architecture for the following phases. These items remain future implementation work rather than placeholder claims:

- Unity Spline river mesh generation and waterfall ribbon/impact authoring.
- Advected river foam, GPU foam/splash particle renderers, terrain-independent shoreline authoring, and higher-fidelity projected world-space caustics.
- GPU readback-backed water query batches and voxel-volume rather than authored-point buoyancy generation.
- Hydrology flood-extent mesh generation and serialized cell-boundary seed handoff.
- Full cloud/forecast/climate/audio work informed later by Enviro 3 and UniStorm.

These additions should extend the current contracts rather than restore global managers or direct module mutation.
