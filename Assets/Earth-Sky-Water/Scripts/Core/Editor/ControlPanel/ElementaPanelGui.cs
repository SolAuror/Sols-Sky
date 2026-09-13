using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Sol.Environment.EditorTools
{
    /// <summary>Native, themeable controls shared by all Elementa authoring pages.</summary>
    static class ElementaPanelGui
    {
        public static VisualElement Row(VisualElement parent)
        {
            var row = new VisualElement(); row.AddToClassList("elementa-row"); parent.Add(row); return row;
        }
        public static Label Note(VisualElement parent, string text)
        {
            var label = new Label(text); label.AddToClassList("elementa-note"); parent.Add(label); return label;
        }
        public static VisualElement Section(VisualElement parent, string title, string scope = null)
        {
            var section = new VisualElement(); section.AddToClassList("elementa-section");
            var heading = new Label(title); heading.AddToClassList("elementa-section-title"); section.Add(heading);
            if (!string.IsNullOrEmpty(scope)) Note(section, scope);
            parent.Add(section); return section;
        }
        public static Foldout Foldout(ElementaPanelPage page, VisualElement parent, string key,
            string title, bool open = false, string note = null)
        {
            var foldout = new Foldout { text = title, name = key, value = page.Context.State.IsSectionOpen(key, open) };
            foldout.AddToClassList("elementa-foldout");
            foldout.RegisterValueChangedCallback(e => { if (e.target == foldout) page.Context.State.SetSectionOpen(key, e.newValue); });
            parent.Add(foldout); if (!string.IsNullOrEmpty(note)) Note(foldout, note); return foldout;
        }
        public static Button Button(VisualElement parent, string text, Action action, string tooltip = null, bool enabled = true)
        {
            var button = new Button(action) { text = text, tooltip = tooltip, name = text };
            button.SetEnabled(enabled); parent.Add(button); return button;
        }
        public static HelpBox Help(VisualElement parent, string message, HelpBoxMessageType severity = HelpBoxMessageType.Info)
        {
            var box = new HelpBox(message, severity); box.AddToClassList("elementa-help"); parent.Add(box); return box;
        }
        public static Label Metric(ElementaPanelPage page, VisualElement parent, string label,
            Func<string> value, string tooltip = null)
        {
            var row = Row(parent); row.AddToClassList("elementa-metric");
            var name = new Label(label) { tooltip = tooltip }; name.AddToClassList("elementa-metric-name"); row.Add(name);
            var output = new Label(); output.AddToClassList("elementa-metric-value"); row.Add(output);
            page.Track(() => { string next = value() ?? "—"; if (next != output.text) output.text = next; }); return output;
        }
        public static ProgressBar MetricBar(ElementaPanelPage page, VisualElement parent, string label,
            Func<float> value, Func<string> description = null)
        {
            var bar = new ProgressBar { lowValue = 0, highValue = 1 }; bar.AddToClassList("elementa-metric-bar"); parent.Add(bar);
            page.Track(() => { bar.value = Mathf.Clamp01(value()); bar.title = label + "  " + (description?.Invoke() ?? value().ToString("P0")); }); return bar;
        }
        public static string Describe(Object value)
        {
            if (value == null) return "None";
            return value is Component c ? value.name + " · " + (IsPreviewTarget(c) ? "Editor preview" : c.gameObject.scene.name) : value.name;
        }
        public static bool IsPreviewTarget(Object value) => value is Component component &&
            (!component.gameObject.scene.IsValid() || (component.hideFlags & HideFlags.DontSaveInEditor) != 0 ||
             (component.gameObject.hideFlags & HideFlags.DontSaveInEditor) != 0);
        public static VisualElement ObjectRow(VisualElement parent, string label, Object value)
        {
            var row = Row(parent); var text = new Label(label + "  ·  " + Describe(value));
            text.AddToClassList("elementa-object-label"); text.tooltip = value != null ? AssetDatabase.GetAssetPath(value) : "Not assigned";
            row.Add(text); Button(row, "Select", () => { Selection.activeObject = value; EditorGUIUtility.PingObject(value); }, enabled: value != null); return row;
        }
        public static VisualElement Source(ElementaPanelPage page, VisualElement parent, Object owner,
            string propertyPath, string label, bool copy = true)
        {
            var source = Section(parent, label, owner is ScriptableObject
                ? "Profile asset · changes affect every user of this asset" : (IsPreviewTarget(owner) ? "Live control · " : "Scene setting · ") + Describe(owner));
            Field(page, source, owner, propertyPath, label);
            var profile = page.Context.Serialized(owner)?.FindProperty(propertyPath)?.objectReferenceValue;
            if (profile is ScriptableObject && copy)
            {
                var row = Row(source); Note(row, "Shared asset · " + profile.name);
                Button(row, "Make a copy", () => ElementaPanelActions.CopyProfile(page.Context, owner, propertyPath), enabled: !IsPreviewTarget(owner));
                if (IsPreviewTarget(owner)) Note(source, "This director is generated for editor preview. Add a scene lighting director to retain a different profile assignment. Edits to the shared profile still persist.");
            }
            return source;
        }
        public static VisualElement Field(ElementaPanelPage page, VisualElement parent, Object target,
            string path, string label = null, string tooltip = null, ElementaFieldRole role = ElementaFieldRole.Curated)
        {
            var serialized = page.Context.Serialized(target); if (serialized == null) return null;
            serialized.UpdateIfRequiredOrScript(); var property = serialized.FindProperty(path);
            if (property == null) { Help(parent, "Unavailable field: " + path, HelpBoxMessageType.Warning); return null; }
            label ??= property.displayName; VisualElement field;
            // Typed controls avoid duplicate Header decorators on curated fields.
            switch (property.propertyType)
            {
                case SerializedPropertyType.Float:
                    if (property.numericType == SerializedPropertyNumericType.Double)
                    { var precise = new DoubleField(label); precise.BindProperty(property); field = precise; break; }
                    var range = Attribute<RangeAttribute>(target.GetType(), path);
                    if (range != null) { var slider = new Slider(label, range.min, range.max) { showInputField = true }; slider.BindProperty(property); field = slider; }
                    else
                    {
                        var number = new FloatField(label);
                        var min = Attribute<MinAttribute>(target.GetType(), path);
                        if (min != null) number.RegisterValueChangedCallback(e =>
                        { if (e.newValue < min.min) { e.StopImmediatePropagation(); number.value = min.min; } });
                        number.BindProperty(property); field = number;
                    } break;
                case SerializedPropertyType.Integer:
                    if (property.numericType == SerializedPropertyNumericType.Int64)
                    { var large = new LongField(label); large.BindProperty(property); field = large; break; }
                    var integerRange = Attribute<RangeAttribute>(target.GetType(), path);
                    if (integerRange != null)
                    { var slider = new SliderInt(label, (int)integerRange.min, (int)integerRange.max) { showInputField = true }; slider.BindProperty(property); field = slider; }
                    else
                    {
                        var integer = new IntegerField(label);
                        var min = Attribute<MinAttribute>(target.GetType(), path);
                        if (min != null) integer.RegisterValueChangedCallback(e =>
                        { if (e.newValue < min.min) { e.StopImmediatePropagation(); integer.value = Mathf.CeilToInt(min.min); } });
                        integer.BindProperty(property); field = integer;
                    } break;
                case SerializedPropertyType.Boolean:
                    var toggle = new Toggle(label); toggle.BindProperty(property); field = toggle; break;
                case SerializedPropertyType.Color:
                    var usage = Attribute<ColorUsageAttribute>(target.GetType(), path);
                    var color = new ColorField(label) { hdr = usage?.hdr ?? false, showAlpha = usage?.showAlpha ?? true };
                    color.BindProperty(property); field = color; break;
                case SerializedPropertyType.ObjectReference:
                    var reference = new ObjectField(label) { objectType = FieldType(target.GetType(), path) ?? typeof(Object), allowSceneObjects = !EditorUtility.IsPersistent(target) };
                    reference.BindProperty(property); field = reference; break;
                case SerializedPropertyType.Enum:
                    var type = FieldType(target.GetType(), path);
                    if (type != null && type.IsEnum) { var enumeration = new EnumField(label, (Enum)Enum.ToObject(type, property.intValue)); enumeration.BindProperty(property); field = enumeration; }
                    else { var fallback = new PropertyField(property, label); fallback.Bind(serialized); field = fallback; } break;
                default:
                    var native = new PropertyField(property, label); native.Bind(serialized); field = native; break;
            }
            field.name = path; field.tooltip = tooltip ?? property.tooltip;
            field.AddToClassList("elementa-field"); field.AddToClassList(BaseField<float>.alignedFieldUssClassName);
            if (target is Sol.ToD.TimeOfDay time && (path.StartsWith("debug", StringComparison.Ordinal) || path == "timeOfDay" ||
                time.SkyProfile != null && TimeOfDayEditor.ProfileOwned.Contains(path))) role = ElementaFieldRole.ReadOnly;
            if (target is Sol.ToD.Calendar && (path.StartsWith("current", StringComparison.Ordinal) || path == "worldDayIndex" || path == "playerDaysElapsed"))
                role = ElementaFieldRole.ReadOnly;
            field.SetEnabled(role != ElementaFieldRole.ReadOnly && !path.EndsWith("bodyId", StringComparison.Ordinal));
            parent.Add(field); page.Catalogue.Add(target, path, role, field); page.Watch(target); return field;
        }
        public static void Fields(ElementaPanelPage page, VisualElement parent, Object target, params string[] paths)
        {
            foreach (string spec in paths) { var pair = spec.Split('|'); Field(page, parent, target, pair[0], pair.Length > 1 ? pair[1] : null); }
        }
        public static void Route(ElementaPanelPage page, VisualElement parent, Object target, string destination, params string[] paths)
        { foreach (string path in paths) page.Catalogue.Add(target, path, ElementaFieldRole.Routed, parent, destination); }
        public static void Advanced(ElementaPanelPage page, VisualElement parent, string key, string title, Object target, params string[] skip)
        {
            if (target == null) return;
            var foldout = Foldout(page, parent, key, title);
            Note(foldout, (IsPreviewTarget(target) ? "Live control · " : target is Component ? "Scene setting · " : "Profile asset · ") + Describe(target));
            GroupedProperties(page, foldout, key, target, new HashSet<string>(skip));
        }
        public static void GroupedProperties(ElementaPanelPage page, VisualElement parent, string key,
            Object target, HashSet<string> skip = null, Func<string, bool> readOnly = null, Func<string, bool> include = null)
        {
            var serialized = page.Context.Serialized(target); if (serialized == null) return;
            serialized.UpdateIfRequiredOrScript();
            var filter = new ToolbarSearchField { value = page.Context.State.GetFilter(key), tooltip = "Find a field by label or serialized name", name = key + "-search" };
            parent.Add(filter);
            var groups = new List<(VisualElement Container, List<(VisualElement Field, string Name)> Fields)>();
            VisualElement current = null; List<(VisualElement Field, string Name)> entries = null;
            var property = serialized.GetIterator(); bool first = true;
            while (property.NextVisible(first))
            {
                first = false; string path = property.propertyPath; if (path == "m_Script") continue;
                var header = Attribute<HeaderAttribute>(target.GetType(), path);
                if (current == null || header != null)
                {
                    current = Foldout(page, parent, key + "/" + (header?.header ?? "settings"), header?.header ?? "Settings");
                    entries = new List<(VisualElement, string)>(); groups.Add((current, entries));
                }
                if ((skip?.Contains(path) ?? false) || page.Catalogue.Contains(target, path) || (include != null && !include(path))) continue;
                var field = Field(page, current, target, path, role: readOnly?.Invoke(path) == true ? ElementaFieldRole.ReadOnly : ElementaFieldRole.Advanced);
                entries.Add((field, property.displayName + " " + path));
            }
            void Filter(string text)
            {
                bool searching = !string.IsNullOrWhiteSpace(text);
                foreach (var group in groups)
                {
                    int visible = 0;
                    foreach (var entry in group.Fields)
                    {
                        bool match = !searching || entry.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
                        entry.Field.style.display = match ? DisplayStyle.Flex : DisplayStyle.None; if (match) visible++;
                    }
                    group.Container.style.display = visible > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                    if (group.Container is Foldout fold) fold.SetValueWithoutNotify(searching || page.Context.State.IsSectionOpen(fold.name, false));
                }
            }
            filter.RegisterValueChangedCallback(e => { page.Context.State.SetFilter(key, e.newValue); Filter(e.newValue); }); Filter(filter.value);
        }
        public static void Issues(ElementaPanelPage page, VisualElement parent, string subsystem = null)
        {
            var issuesRoot = new VisualElement { name = "issues" };
            parent.Add(issuesRoot);
            string signature = null;
            page.Track(() =>
            {
                var issues = page.Context.Issues.Where(i => subsystem == null || i.Subsystem == subsystem).ToArray();
                string next = string.Join("|", issues.Select(i => i.Id + i.Message)); if (signature == next) return;
                signature = next; issuesRoot.Clear();
                foreach (var issue in issues)
                {
                    var row = new VisualElement(); row.AddToClassList("elementa-issue"); issuesRoot.Add(row);
                    Help(row, issue.Symptom, issue.Severity == MessageType.Error ? HelpBoxMessageType.Error : issue.Severity == MessageType.Warning ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info);
                    var details = new Foldout { text = "Details", value = false }; Note(details, issue.Message); row.Add(details);
                    var actions = Row(row);
                    if (issue.Fix != null) Button(actions, issue.FixLabel, () => { issue.Fix(); page.Context.Resolve(true); page.Context.InvalidateIssues(); page.Context.RefreshLayout(); });
                    if (issue.Subsystem != page.Title) Button(actions, "Open " + issue.Subsystem, () => page.Context.Window.ShowPage(issue.Subsystem));
                }
            });
        }
        public static T Attribute<T>(Type type, string path) where T : Attribute => FindField(type, path)?.GetCustomAttribute<T>();
        static Type FieldType(Type type, string path) => FindField(type, path)?.FieldType;
        static FieldInfo FindField(Type type, string path)
        {
            FieldInfo field = null; string[] pieces = path.Split('.');
            for (int i = 0; i < pieces.Length; i++)
            {
                if (pieces[i] == "Array" && i + 1 < pieces.Length)
                { type = type.IsArray ? type.GetElementType() : type.GetGenericArguments().FirstOrDefault(); i++; continue; }
                field = null;
                for (Type t = type; t != null && field == null; t = t.BaseType)
                    field = t.GetField(pieces[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null) return null; type = field.FieldType;
            }
            return field;
        }
    }
}
