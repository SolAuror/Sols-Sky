using System.Collections.Generic;
using Sol.Water;
using Sol.Water.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UI = Sol.Environment.EditorTools.ElementaPanelGui;

namespace Sol.Environment.EditorTools
{
    sealed class ElementaWaterPage : ElementaPanelPage
    {
        public override string Title => "Water";
        public override string Subtitle => "Choose a water body and shape its appearance.";
        protected override void Build(ElementaPanelContext context, VisualElement root)
        {
            if (context.Water == null) { UI.Help(root, "Add a water world using Overview to begin authoring water."); return; }
            var bodies = new List<SolWaterBody>();
            if (context.Water.Bodies != null) foreach (var item in context.Water.Bodies) if (item != null) bodies.Add(item);
            var body = context.State.selectedWaterBody;
            if (body != null && !bodies.Contains(body)) context.State.selectedWaterBody = body = null;
            var choices = new List<string> { "World default" };
            for (int i = 0; i < bodies.Count; i++) choices.Add(bodies[i].name + "  [" + (i + 1) + "]");
            var picker = new DropdownField("Editing", choices, body != null ? bodies.IndexOf(body) + 1 : 0);
            picker.AddToClassList("elementa-field"); root.Add(picker);
            picker.RegisterValueChangedCallback(e => { int index = choices.IndexOf(e.newValue); context.State.selectedWaterBody = index > 0 ? bodies[index - 1] : null; context.RefreshLayout(); });
            SolWaterProfile profile;
            if (body == null)
            {
                UI.Source(this, root, context.Water, "defaultProfile", "Default water profile");
                profile = context.Water.DefaultProfile;
            }
            else
            {
                UI.Source(this, root, body, "profile", "Body profile");
                profile = context.Serialized(body).FindProperty("profile").objectReferenceValue as SolWaterProfile;
                if (profile == null)
                {
                    UI.Help(root, "This body inherits the world default. Choose an explicit editing action to change its appearance.");
                    UI.ObjectRow(root, "Inherited profile", context.Water.DefaultProfile);
                    var actions = UI.Row(root);
                    UI.Button(actions, "Edit shared default", () => { context.State.selectedWaterBody = null; context.RefreshLayout(); });
                    UI.Button(actions, "Make a body copy", () => MakeBodyCopy(context, body), enabled: context.Water.DefaultProfile != null);
                }
                var level = UI.Foldout(this, root, "water/body/settings", "Body settings", note: "Scene settings · runtime water systems can override authored levels.");
                UI.ObjectRow(level, "Body", body);
                UI.Fields(this, level, body, "useTransformYAsLevel|Use transform height", "levelOffset|Level offset (m)", "priority|Overlap priority", "authoredFlow|Current (m/s)", "enablePlanarReflection|Planar reflections");
                UI.Metric(this, level, "Effective level", () => body != null ? body.SurfaceLevel.ToString("0.00") + " m" : "Body removed");
            }
            if (profile != null)
            {
                var appearance = UI.Section(root, "Appearance", "Profile asset · " + profile.name);
                UI.Fields(this, appearance, profile, "shallowScattering|Shallow colour", "deepScattering|Deep colour", "clarityDistance|Clarity (m)", "smoothness|Surface smoothness");
                UI.Field(this, appearance, profile, "spectralStrength", "Ocean wave strength", "Ocean spectrum strength on Medium/High. Finite bodies and Low quality use the authored wave train.");
                if (body != null && body.BodyType != SolWaterBodyType.Ocean)
                    appearance.Q("spectralStrength")?.SetEnabled(false);
                var waves = UI.Foldout(this, root, "water/waves", "Authored wave train", note: "Used by finite bodies and the ocean on Low quality. The first eight entries are used.");
                UI.Fields(this, waves, profile, "gerstnerWaves|Waves");
                var detail = UI.Foldout(this, root, "water/appearance", "Detailed water appearance");
                UI.GroupedProperties(this, detail, "water/body", profile);
            }
            var list = UI.Foldout(this, root, "water/bodies", "Resolved water bodies (" + bodies.Count + ")");
            foreach (var item in bodies)
            {
                var row = new VisualElement(); row.AddToClassList("elementa-list-row"); list.Add(row);
                var label = new Label(); row.Add(label);
                Track(() => label.text = item != null ? $"{UI.Describe(item)} · {item.BodyType} · {item.SurfaceLevel:0.00} m · priority {item.Priority}" : "Body removed");
                UI.Button(row, "Edit", () => { context.State.selectedWaterBody = item; context.RefreshLayout(); });
                UI.Button(row, "Select", () => { Selection.activeGameObject = item.gameObject; EditorGUIUtility.PingObject(item); });
            }
            UI.Button(root, "Water quality and debug views", () => context.Window.ShowPage("Quality", "quality/water"));
            UI.Advanced(this, root, "water/component/world", "Advanced · water world", context.Water, "defaultProfile", "qualityProfile");
            UI.Advanced(this, root, "water/component/shoreline", "Advanced · terrain shoreline", context.Shoreline);
            UI.Advanced(this, root, "water/component/planar", "Advanced · planar reflections", context.PlanarReflections);
            UI.Advanced(this, root, "water/component/feature", "Advanced · water renderer", ElementaPanelDoctor.ResolveFeature<SolWaterRendererFeature>(), "renderInSceneView", "debugMode", "debugLog");
        }
        static void MakeBodyCopy(ElementaPanelContext context, SolWaterBody body)
        {
            var source = context.Water.DefaultProfile;
            if (source == null || body == null) return;
            string path = EditorUtility.SaveFilePanelInProject("Make a body profile", body.name + " Water", "asset", "Choose where the body's editable profile is saved.");
            if (string.IsNullOrEmpty(path)) return;
            var copy = Object.Instantiate(source); copy.name = System.IO.Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(copy, AssetDatabase.GenerateUniqueAssetPath(path));
            var serialized = context.Serialized(body); serialized.Update(); serialized.FindProperty("profile").objectReferenceValue = copy;
            serialized.ApplyModifiedProperties(); context.RefreshLayout();
        }
    }
}
