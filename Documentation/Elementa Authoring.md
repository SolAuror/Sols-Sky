# Elementa authoring window

Open **Tools → Elementa → Control Panel**. Elementa uses native UI Toolkit on Unity 6000.3.9f1 / URP 17.3; no additional UI package is required. The ten pages share a persistent clock and weather-preview toolbar. Below 620 points the navigation becomes a dropdown; larger windows have a 144-point sidebar. Minimum size is 460 × 580.

## Editing and saving

Sections identify the object they change:

- **Profile asset** changes are shared by every scene or component using that asset.
- **Scene setting** changes belong to the named component, including prefab instance overrides.
- **Live control** runs an environment command, such as setting the clock or previewing weather.

Authored fields use Unity serialized binding and Undo. Save scenes and assets normally. **Make a copy** duplicates an asset to a project path and assigns it to the displayed slot. Cancelling changes nothing. Undo restores the assignment; the new asset remains in the project. Copying a weather condition replaces its scene selection entry's profile without changing its weights.

## Authoring workflows

**Clouds:** choose **Sky baseline** or **Weather condition** before editing. The baseline's **Cloud coverage** is absolute coverage; a condition's **Weather influence** pushes that baseline toward overcast. Formation-derived shape controls remain disabled until **Custom layer shape** is enabled. Daily variation remains independently editable. Weather and Clouds use the same cloud editor. The shared rendering profile controls appearance; its budgets and debug views live on Quality.

**Weather:** select a configured condition to edit its asset. A/B choices and navigation do not start a preview. Use **Start weather preview** or **Preview condition** explicitly. While previewing, A/B choices, blend, wind heading and profile edits update the preview. **End weather preview** restores the previous weather presentation; it keeps authored profile edits, clock changes and independent quality selections. The window releases its preview when closed, before assembly reload, before scene closure, on manager replacement and across Play Mode transitions. Remembered choices never resume preview automatically. Another tool's preview is not taken over.

**Water:** choose the world default or a specific body. A body without an override displays its inherited profile without opening that asset for editing. **Edit shared default** and **Make a body copy** are explicit choices. Body profile assignment and level settings stay separate from profile appearance.

**Landscape:** use Create landscape… for the guided scene-adjacent 2×2 setup, then Setup, Sculpt, Materials, Rules and Preview. Paint material, Remove material and Restore removed material work in Scene view. Remove material also removes base-painted paths; Clear painted overrides under Advanced only clears explicit strokes. Protected automatic rock remains visible. Setup provides repair actions, profile duplication, Reveal assets and reviewed asset organisation. Legacy configs have an explicit copy-based migration. See [Landscape Designer](Landscape%20Designer.md) for palette identity, save/undo, conversion and runtime contracts.

**Quality:** authored defaults and active results are displayed separately. Live tier controls do not overwrite quality assets. Without a lighting quality profile a scene director's fallback tier is a saved scene setting. A generated editor-preview director is labelled separately; add a scene director to retain a different profile assignment. Edits to its shared quality profile still persist. A director can enforce other subsystem tiers; consult each subsystem's active result. Renderer debug settings and pipeline requirements are persistent asset edits.

**Overview / Diagnostics:** inspect exact resolved objects, assigned assets and actionable issues. Additive scenes retain the runtime's existing resolution rules; picking a clock does not transfer other subsystems into its scene. Diagnostics also retains frame-budget logging, migration, validation and baking tools.

## Implementation

The shell is `ControlPanel/UI/ElementaPanel.uxml`, styled by `ElementaPanel.uss`, loaded relative to the window's editor script. Pages compose retained native elements, bind independent serialized targets and release their bindings when replaced. The Scene View wind hook is retained. Separate tool windows and component inspectors are outside this migration.

`ElementaFieldCatalogue` records each actual control's target/property path, whether it is curated, advanced or read-only, and explicit routes to other pages. New serialized fields remain reachable through named, searchable advanced groups. Coverage tests validate registrations and route destinations against the demo scene.

Live readouts update at up to 10 Hz; idle readouts at 2 Hz. Resolution runs at 0.5-second intervals and health checks at 1.5-second intervals. Native edit notifications and Undo use a shared refresh path. Clock progression and serialized diagnostic outputs are excluded from authored-change detection. A property-value edit updates existing controls; target or collection changes rebuild the page. Scroll and section state survive navigation.

## Validation

Run `SolControlPanelCoverageTests` and `SolControlPanelUIToolkitTests` with a graphics device: the focus test opens a real editor panel and cannot run with `-nographics`. Use an isolated validation project for the full environment, sky, lighting and landscape EditMode suites. Keep logs, temporary copied assets and validation projects outside repository folders.

Visual acceptance covers all ten pages at 460, 620 and 900 points, both Unity themes, and 100% / 150% scaling. Check expanded groups, native arrays, long asset names, wrapping actions, focus during live updates, and horizontal scrolling confined to the weather matrix. Landscape now includes Scene View brushes and diagnostic previews. Whole-world Apply/Discard sessions and isolated-baseline weather preview remain deferred.

### Migration review — 11 September 2026

Validated in an isolated copy using Unity 6000.3.9f1, URP 17.3 and Direct3D 11.

- Final EditMode regression: **229 passed, 0 failed**. Includes native control/catalogue coverage, serialized editing, Undo, profile copying, preview restoration and focus retention.
- Inspected all ten primary pages in Unity. Additional visual checks covered the compact dropdown, the 620-point sidebar breakpoint, expanded source sections, HDR fields, quality ownership, and the weather A/B workflow in both themes and at 100% / 150% scaling.
- Native layout audit: **120 expanded page/width/theme/scale combinations**, with no registered field or button extending outside its page bounds. This geometry check supplements the visual inspection; it does not compare pixels to a mockup or test every nested popup.
- Entered and exited Play Mode with domain reload enabled and disabled. Owned weather preview was released in both cases. Original editor scaling, theme and Play Mode options were restored.
- Removed a focus-disrupting rebuild caused by an unrelated lighting preview authority appearing. Forced scene resolution now retains serialized bindings; target and structure changes rebuild only affected visible controls.
- Corrected the generated lighting director's scope label and prevented assigning a copied profile to a transient editor-preview slot.

The demo emitted a terrain shader importer consistency warning during startup. It did not fail the control-panel tests; terrain rendering remains outside this UI migration review. Validation logs, result XML, layout reports and the temporary review harness remain outside the repository in the project root's validation area.
