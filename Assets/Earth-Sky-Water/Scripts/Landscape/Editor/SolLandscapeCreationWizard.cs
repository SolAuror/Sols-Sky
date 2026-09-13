using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Sol.Landscape.Editor
{
    public sealed class SolLandscapeCreationWizard : EditorWindow
    {
        public SolLandscapeCreationSettings Settings { get; private set; }
        public int Step { get; private set; }
        Action<SolLandscapeGroup, bool> completed;
        SolLandscapeGroup result;
        string error;
        public static SolLandscapeCreationWizard Open(GameObject emptyRoot, float elevation, Action<SolLandscapeGroup, bool> completed)
        {
            var wizard = CreateInstance<SolLandscapeCreationWizard>();
            wizard.Settings = new SolLandscapeCreationSettings { scene = emptyRoot != null ? emptyRoot.scene : SceneManager.GetActiveScene(), emptyRoot = emptyRoot, elevation = elevation };
            if (emptyRoot != null) wizard.Settings.origin = new Vector2(emptyRoot.transform.position.x, emptyRoot.transform.position.z);
            wizard.completed = completed; wizard.titleContent = new GUIContent("Create landscape"); wizard.minSize = new Vector2(490, 540); wizard.Show(); return wizard;
        }
        void OnEnable() => SceneView.duringSceneGui += DrawFootprint;
        void OnDisable() { SceneView.duringSceneGui -= DrawFootprint; SceneView.RepaintAll(); }
        public void CreateGUI()
        {
            Settings ??= new SolLandscapeCreationSettings { scene = SceneManager.GetActiveScene() };
            Rebuild();
        }
        void Note(VisualElement parent, string text) => parent.Add(new HelpBox(text, HelpBoxMessageType.Info));
        void Button(VisualElement parent, string text, Action action, bool enabled = true)
        { var button = new Button(action) { text = text }; button.SetEnabled(enabled); parent.Add(button); }
        public void Rebuild()
        {
            var root = rootVisualElement; root.Clear(); root.style.paddingLeft = root.style.paddingRight = 14; root.style.paddingTop = 12;
            if (result != null)
            {
                Note(root, "Your landscape is ready. Sculpt and paint by dragging in Scene view. Escape cancels a stroke; Alt navigates the camera.");
                Note(root, SolLandscapeAssetLocations.Root(result.assetOwner));
                Button(root, "Start sculpting", () => { completed?.Invoke(result, true); Close(); });
                Button(root, "Start painting", () => { completed?.Invoke(result, false); Close(); });
                Button(root, "Reveal assets", () => EditorGUIUtility.PingObject(result.assetOwner)); return;
            }
            string[] headings = { "Name and destination", "Terrain", "Surface", "Review and create" };
            root.Add(new Label($"{Step + 1} of 4 · {headings[Step]}") { style = { fontSize = 20, marginBottom = 12 } });
            var body = new ScrollView(); body.style.flexGrow = 1; root.Add(body);
            if (Step == 0)
            {
                var name = new TextField("Landscape name") { value = Settings.name }; body.Add(name);
                var scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Where(s => s.isLoaded).ToArray();
                var names = scenes.Select(s => string.IsNullOrEmpty(s.path) ? "Unsaved scene · " + s.handle : s.path).ToList();
                var scene = new DropdownField("Target scene", names, Mathf.Max(0, Array.IndexOf(scenes, Settings.scene))); body.Add(scene);
                scene.RegisterValueChangedCallback(e => { Settings.scene = scenes[names.IndexOf(e.newValue)]; if (Settings.emptyRoot != null && Settings.emptyRoot.scene != Settings.scene) Settings.emptyRoot = null; Rebuild(); });
                if (string.IsNullOrEmpty(Settings.scene.path)) Button(body, "Save target scene…", () => { EditorSceneManager.SaveScene(Settings.scene); Rebuild(); });
                var destination = new HelpBox("", HelpBoxMessageType.Info); body.Add(destination);
                void UpdateDestination() { try { destination.text = SolLandscapeAssetLocations.SceneRoot(Settings.scene, Settings.name); } catch (Exception e) { destination.text = e.Message; } }
                name.RegisterValueChangedCallback(e => { Settings.name = e.newValue; UpdateDestination(); root.Query<Button>().ToList().FirstOrDefault(b => b.text == "Next")?.SetEnabled(!string.IsNullOrEmpty(Settings.scene.path) && SolLandscapeAssetLocations.ValidName(Settings.name)); });
                UpdateDestination();
                Note(body, Settings.emptyRoot == null ? "A new root will be created in this scene." : "Reuse empty root: " + Settings.emptyRoot.name);
                Note(body, "Each landscape gets its own editable profile and terrain data. Shared material artwork stays in the library.");
            }
            if (Step == 1)
            {
                var grids = new List<string> { "1 × 1", "2 × 2", "3 × 3", "Custom" };
                var grid = new DropdownField("Grid", grids, Settings.columns == Settings.rows && Settings.columns <= 3 ? Settings.columns - 1 : 3); body.Add(grid);
                var custom = new VisualElement();
                grid.RegisterValueChangedCallback(e => { int i = grids.IndexOf(e.newValue); if (i < 3) { Settings.columns = Settings.rows = i + 1; Rebuild(); } else custom.style.display = DisplayStyle.Flex; SceneView.RepaintAll(); });
                body.Add(custom); custom.style.display = grid.index == 3 ? DisplayStyle.Flex : DisplayStyle.None;
                Int(custom, "Columns", Settings.columns, v => Settings.columns = v); Int(custom, "Rows", Settings.rows, v => Settings.rows = v);
                Float(body, "Tile size (m)", Settings.tileSize, v => Settings.tileSize = v); Float(body, "Vertical range (m)", Settings.verticalRange, v => Settings.verticalRange = v);
                Float(body, "Initial surface height (world m)", Settings.elevation, v => Settings.elevation = v);
                var origin = new Vector2Field("Southwest corner (world X/Z)") { value = Settings.origin }; body.Add(origin); origin.RegisterValueChangedCallback(e => { Settings.origin = e.newValue; SceneView.RepaintAll(); });
                var summary = new HelpBox("", HelpBoxMessageType.Info); body.Add(summary);
                summary.schedule.Execute(() => summary.text = $"{Settings.columns * Settings.rows} tiles · {Settings.columns * Settings.tileSize:0} × {Settings.rows * Settings.tileSize:0} m. Flat ground, with {Settings.LoweringReserve:0} m available below the initial surface.").Every(200);
                Button(body, "Frame footprint in Scene view", () => SceneView.lastActiveSceneView?.Frame(new Bounds(Settings.Footprint.center, Settings.Footprint.size + Vector3.up * Settings.verticalRange), false));
                var advanced = new Foldout { text = "Advanced · sample resolutions" }; body.Add(advanced);
                Choice(advanced, "Height samples", new[] { 33, 65, 129, 257, 513, 1025, 2049, 4097 }, Settings.heightResolution, v => Settings.heightResolution = v);
                Choice(advanced, "Control and paint maps", new[] { 64, 128, 256, 512, 1024, 2048 }, Settings.mapResolution, v => Settings.mapResolution = v);
            }
            if (Step == 2)
            {
                foreach (string preset in new[] { "Temperate", "Volcanic Coast" })
                {
                    var profile = SolLandscapeCreation.Preset(preset); var row = new VisualElement { style = { flexDirection = FlexDirection.Row } }; body.Add(row);
                    foreach (var entry in profile != null ? profile.Layers.Take(5) : Enumerable.Empty<SolLandscapeLayerEntry>()) row.Add(new Image { image = entry.terrainLayer?.diffuseTexture, scaleMode = ScaleMode.ScaleToFit, style = { width = 75, height = 75 } });
                    Button(body, (Settings.preset == preset ? "● " : "") + preset, () => { Settings.preset = preset; Rebuild(); }, profile != null);
                }
                Note(body, "Automatic ground responds as you sculpt. Protected rock stays visible on slopes. Path is paint-only: select it in Materials, then drag to paint or remove it locally.");
            }
            if (Step == 3)
            {
                Note(body, $"{Settings.name} · {Settings.preset}\n{Settings.columns * Settings.rows} terrains · {Settings.columns * Settings.tileSize:0} × {Settings.rows * Settings.tileSize:0} m\nSurface {Settings.elevation:0.0} m · vertical range {Settings.verticalRange:0} m\nTerrain/control data estimate: {Settings.EstimatedBytes / 1048576f:0.0} MiB before Unity overhead. Paint is allocated on first use: up to {Settings.columns * Settings.rows * 24L * Settings.mapResolution * Settings.mapResolution / 1048576f:0.0} MiB plus GPU copies and undo. Existing starter arrays are shared.");
                try { Note(body, SolLandscapeAssetLocations.SceneRoot(Settings.scene, Settings.name)); } catch (Exception e) { Note(body, e.Message); }
                foreach (string issue in SolLandscapeCreation.Validate(Settings)) body.Add(new HelpBox(issue, HelpBoxMessageType.Error));
            }
            if (!string.IsNullOrEmpty(error)) body.Add(new HelpBox(error, HelpBoxMessageType.Error));
            var navigation = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 12, marginBottom = 12 } }; root.Add(navigation);
            Button(navigation, "Cancel", Close); Button(navigation, "Back", () => { Step--; error = null; Rebuild(); }, Step > 0);
            if (Step < 3) Button(navigation, "Next", () => { Step++; error = null; Rebuild(); }, Step != 0 || (!string.IsNullOrEmpty(Settings.scene.path) && SolLandscapeAssetLocations.ValidName(Settings.name)));
            else Button(navigation, "Create landscape", () => { try { result = SolLandscapeCreation.Create(Settings); completed?.Invoke(result, true); error = null; } catch (Exception e) { error = e.Message; } Rebuild(); }, SolLandscapeCreation.Validate(Settings).Count == 0);
        }
        void Int(VisualElement root, string title, int value, Action<int> update) { var field = new IntegerField(title) { value = value }; root.Add(field); field.RegisterValueChangedCallback(e => { update(e.newValue); SceneView.RepaintAll(); }); }
        void Float(VisualElement root, string title, float value, Action<float> update) { var field = new FloatField(title) { value = value }; root.Add(field); field.RegisterValueChangedCallback(e => { update(e.newValue); SceneView.RepaintAll(); }); }
        void Choice(VisualElement root, string title, int[] values, int value, Action<int> update) { var field = new DropdownField(title, values.Select(v => v.ToString()).ToList(), value.ToString()); root.Add(field); field.RegisterValueChangedCallback(e => update(int.Parse(e.newValue))); }
        void DrawFootprint(SceneView view)
        {
            if (Settings == null || result != null || !Settings.scene.IsValid()) return;
            Handles.color = new Color(.35f, .9f, .55f, .8f); float size = Settings.tileSize;
            for (int z = 0; z < Mathf.Clamp(Settings.rows, 1, 16); z++) for (int x = 0; x < Mathf.Clamp(Settings.columns, 1, 16); x++)
                Handles.DrawWireCube(new Vector3(Settings.origin.x + (x + .5f) * size, Settings.elevation, Settings.origin.y + (z + .5f) * size), new Vector3(size, 0, size));
        }
    }
}
