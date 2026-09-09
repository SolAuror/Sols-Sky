using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// Drawing primitives shared by every control panel page.
    ///
    /// The grouped property editor is the reason this exists. Elementa's authoring assets
    /// carry between seven and sixteen [Header] groups each, and a flat NextVisible dump of
    /// one -- which is what the panel used to show -- is unreadable past the first screen.
    /// Reading the headers back off the type reconstructs the grouping the asset already
    /// declares, so a field added to a profile lands in the right section with no change on
    /// this side.
    /// </summary>
    static class ElementaPanelGui
    {
        static readonly Dictionary<Type, Dictionary<string, string>> HeaderCache = new();
        static GUIStyle _sectionHeader;
        static GUIStyle _pageTitle;
        static GUIStyle _wrappedMini;
        static GUIStyle _value;

        static GUIStyle SectionHeaderStyle => _sectionHeader ??= new GUIStyle(EditorStyles.foldout)
        {
            fontStyle = FontStyle.Bold,
        };

        static GUIStyle PageTitleStyle => _pageTitle ??= new GUIStyle(EditorStyles.largeLabel)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 14,
        };

        static GUIStyle WrappedMiniStyle => _wrappedMini ??= new GUIStyle(EditorStyles.miniLabel)
        {
            wordWrap = true,
        };

        /// <summary>
        /// Metric values are sentences as often as they are numbers - a Beaufort description, a
        /// sea state, a base/thickness pair - so the value column takes whatever width is left
        /// and wraps rather than clipping the end off.
        /// </summary>
        static GUIStyle ValueStyle => _value ??= new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.UpperRight,
            wordWrap = true,
        };

        // -- Structure ---------------------------------------------------------------

        public static void PageTitle(string title, string subtitle)
        {
            EditorGUILayout.LabelField(title, PageTitleStyle);
            if (!string.IsNullOrEmpty(subtitle))
                EditorGUILayout.LabelField(subtitle, WrappedMiniStyle);
            Rule();
        }

        public static bool SectionHeader(
            ElementaPanelContext context, string key, string label, bool defaultOpen)
        {
            EditorGUILayout.Space(6f);
            bool open = context.State.IsSectionOpen(key, defaultOpen);
            bool next = EditorGUILayout.Foldout(open, label, true, SectionHeaderStyle);
            if (next == open)
                return next;

            // Opening a section adds controls the cached layout does not have, and closing one
            // removes controls it does. Either way this pass is drawing a different page than
            // the one that was laid out, so end it and repaint.
            context.State.SetSectionOpen(key, next);
            context.RefreshLayout();
            return next;
        }

        public static void Rule()
        {
            Rect rect = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
            rect.y += 2f;
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.25f));
            EditorGUILayout.Space(3f);
        }

        public static void Note(string text)
            => EditorGUILayout.LabelField(text, WrappedMiniStyle);

        // -- Readouts ----------------------------------------------------------------

        public static void Metric(string label, string value, string tooltip = null)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent(label, tooltip),
                    GUILayout.Width(168f));
                EditorGUILayout.LabelField(value, ValueStyle,
                    GUILayout.MinWidth(90f), GUILayout.ExpandWidth(true));
            }
        }

        /// <summary>
        /// A normalised channel with its own bar. Weather is nine simultaneous scalars, and
        /// as nine formatted numbers it hides which one is actually moving.
        /// </summary>
        public static void MetricBar(string label, float value01, string value = null)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(150f));
                Rect rect = GUILayoutUtility.GetRect(60f, 14f, GUILayout.ExpandWidth(true));
                rect.y += 2f;
                rect.height = 10f;
                EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.22f));
                Rect fill = rect;
                fill.width = Mathf.Max(0f, rect.width * Mathf.Clamp01(value01));
                EditorGUI.DrawRect(fill, Heat(Mathf.Clamp01(value01)));
                EditorGUILayout.LabelField(
                    value ?? value01.ToString("0.00"), GUILayout.Width(60f));
            }
        }

        public static Color Heat(float t)
            => Color.Lerp(new Color(0.24f, 0.38f, 0.52f), new Color(0.30f, 0.82f, 1f), t);

        /// <summary>Read-only reference row with a click-through to the object itself.</summary>
        public static void ObjectRow(
            string label, UnityEngine.Object value, string missingHint = null)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    // Explicit single-line height: an ObjectField given a tall rect switches to
                    // its large-preview style, which is what turned the landscape array rows
                    // into thumbnails with a second Select button drawn over them.
                    EditorGUILayout.ObjectField(label, value,
                        value != null ? value.GetType() : typeof(UnityEngine.Object), true,
                        GUILayout.Height(EditorGUIUtility.singleLineHeight));
                }
                using (new EditorGUI.DisabledScope(value == null))
                {
                    if (GUILayout.Button("Select", GUILayout.Width(56f)))
                    {
                        Selection.activeObject = value;
                        EditorGUIUtility.PingObject(value);
                    }
                }
            }

            if (value == null && !string.IsNullOrEmpty(missingHint))
                Note(missingHint);
        }

        public static bool ActionButton(
            string label, string tooltip, bool enabled = true, float width = 0f)
        {
            using (new EditorGUI.DisabledScope(!enabled))
            {
                return width > 0f
                    ? GUILayout.Button(new GUIContent(label, tooltip), GUILayout.Width(width))
                    : GUILayout.Button(new GUIContent(label, tooltip));
            }
        }

        // -- Property editing --------------------------------------------------------

        public static string SearchField(string filter)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                string next = EditorGUILayout.TextField("Find field", filter ?? string.Empty);
                if (GUILayout.Button("Clear", GUILayout.Width(50f)))
                    next = string.Empty;
                return next;
            }
        }

        /// <summary>
        /// Draws a serialized object as collapsible [Header] groups with a field filter.
        /// While a filter is active every matching field is shown regardless of which groups
        /// are collapsed, so finding one knob never means remembering which of sixteen
        /// sections it lives in.
        /// </summary>
        /// <returns>True when the author changed something, so the caller can republish.</returns>
        /// <summary>
        /// Draws a serialized object as collapsible [Header] groups with a field filter.
        /// While a filter is active every matching field is shown regardless of which groups
        /// are collapsed, so finding one knob never means remembering which of sixteen
        /// sections it lives in.
        /// </summary>
        /// <returns>True when the author changed something, so the caller can republish.</returns>
        public static bool DrawGroupedProperties(
            ElementaPanelContext context,
            string keyPrefix,
            SerializedObject serialized,
            ref string filter)
        {
            if (serialized == null || serialized.targetObject == null)
                return false;

            serialized.Update();
            filter = SearchField(filter);
            bool filtering = !string.IsNullOrWhiteSpace(filter);

            EditorGUI.BeginChangeCheck();
            if (filtering)
                DrawFiltered(serialized, filter);
            else
                DrawGroups(context, keyPrefix, serialized);

            if (!EditorGUI.EndChangeCheck())
                return false;

            serialized.ApplyModifiedProperties();
            return true;
        }

        static void DrawFiltered(SerializedObject serialized, string filter)
        {
            int shown = 0;
            SerializedProperty property = serialized.GetIterator();
            bool enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (property.propertyPath == "m_Script" || !Matches(property, filter))
                    continue;

                shown++;
                using (new EditorGUI.IndentLevelScope())
                    EditorGUILayout.PropertyField(property, true);
            }

            if (shown == 0)
                EditorGUILayout.HelpBox("No field matches that name.", MessageType.None);
        }

        /// <summary>
        /// One collapsible section per [Header] group.
        ///
        /// Unity draws the HeaderAttribute decorator itself on the group's first field and
        /// there is no supported way to suppress it, so an open group does not print a title
        /// of its own - it indents the body to clear the gutter and puts the fold arrow back
        /// onto the decorator's own row, at the same half-line offset HeaderDrawer uses. A
        /// closed group has no field drawn and therefore no decorator, so there the foldout
        /// carries the title itself. Either way the title appears exactly once, on the row
        /// with the arrow.
        /// </summary>
        static void DrawGroups(
            ElementaPanelContext context, string keyPrefix, SerializedObject serialized)
        {
            List<PropertyGroup> groups = BuildGroups(serialized);
            bool firstGroup = true;

            for (int i = 0; i < groups.Count; i++)
            {
                PropertyGroup group = groups[i];
                if (group.Header == null)
                {
                    // Fields declared before any header. Rare, and never a section.
                    for (int p = 0; p < group.Properties.Count; p++)
                        using (new EditorGUI.IndentLevelScope())
                            EditorGUILayout.PropertyField(group.Properties[p], true);
                    continue;
                }

                string key = keyPrefix + "/" + group.Header;

                // The first group opens so the editor is never a wall of closed rows; the
                // rest stay shut, because sixteen open groups is the flat dump again.
                bool open = context.State.IsSectionOpen(key, firstGroup);
                firstGroup = false;

                EditorGUILayout.Space(6f);
                if (!open)
                {
                    bool reopened = EditorGUILayout.Foldout(
                        false, group.Header, true, SectionHeaderStyle);
                    if (reopened)
                    {
                        context.State.SetSectionOpen(key, true);
                        context.RefreshLayout();
                    }

                    continue;
                }

                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(group.Properties[0], true);
                Rect headerRow = GUILayoutUtility.GetLastRect();
                for (int p = 1; p < group.Properties.Count; p++)
                    EditorGUILayout.PropertyField(group.Properties[p], true);
                EditorGUI.indentLevel--;

                // HeaderDrawer insets its label by half a line before drawing it, so matching
                // that expression puts the arrow on the title's line rather than above it.
                headerRow.y += EditorGUIUtility.singleLineHeight * 0.5f;
                headerRow.height = EditorGUIUtility.singleLineHeight;
                if (EditorGUI.Foldout(headerRow, true, GUIContent.none, true, SectionHeaderStyle))
                    continue;

                context.State.SetSectionOpen(key, false);
                context.RefreshLayout();
            }
        }

        sealed class PropertyGroup
        {
            public PropertyGroup(string header)
            {
                Header = header;
            }

            public string Header { get; }

            public List<SerializedProperty> Properties { get; } = new();
        }

        /// <summary>
        /// Splits the object into its authored header groups. The properties are copied
        /// because a SerializedObject iterator is a single moving cursor - keeping references
        /// to it would leave every group pointing at the last field.
        /// </summary>
        static List<PropertyGroup> BuildGroups(SerializedObject serialized)
        {
            Dictionary<string, string> headers = HeaderMap(serialized.targetObject.GetType());
            List<PropertyGroup> groups = new();
            PropertyGroup current = new(null);

            SerializedProperty property = serialized.GetIterator();
            bool enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (property.propertyPath == "m_Script")
                    continue;

                if (headers.TryGetValue(property.name, out string header))
                {
                    if (current.Properties.Count > 0)
                        groups.Add(current);
                    current = new PropertyGroup(header);
                }

                current.Properties.Add(property.Copy());
            }

            if (current.Properties.Count > 0)
                groups.Add(current);
            return groups;
        }

        static bool Matches(SerializedProperty property, string filter)
            => property.displayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
            || property.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        static Dictionary<string, string> HeaderMap(Type type)
        {
            if (HeaderCache.TryGetValue(type, out Dictionary<string, string> cached))
                return cached;

            Dictionary<string, string> map = new();
            for (Type current = type;
                 current != null && current != typeof(UnityEngine.Object);
                 current = current.BaseType)
            {
                FieldInfo[] fields = current.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly);
                foreach (FieldInfo field in fields)
                {
                    HeaderAttribute header = field.GetCustomAttribute<HeaderAttribute>();
                    if (header != null && !map.ContainsKey(field.Name))
                        map[field.Name] = header.header;
                }
            }

            HeaderCache[type] = map;
            return map;
        }

        // -- Bespoke controls --------------------------------------------------------

        /// <summary>
        /// Compass dial for a world-space XZ heading, with the lagged cloud heading behind
        /// it. A slider alone cannot show that 359 and 1 are adjacent, which is exactly the
        /// edit an author makes when nudging a front around.
        /// </summary>
        public static float WindDial(float degrees, float cloudDegrees, float radius = 38f)
        {
            Rect rect = GUILayoutUtility.GetRect(
                80f, radius * 2f + 24f, GUILayout.ExpandWidth(true));
            Vector2 center = new(rect.x + rect.width * 0.5f, rect.y + radius + 8f);
            int controlId = GUIUtility.GetControlID(
                "ElementaWindDial".GetHashCode(), FocusType.Passive);
            Event current = Event.current;

            switch (current.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    if (current.button == 0
                        && Vector2.Distance(current.mousePosition, center) <= radius + 8f)
                    {
                        GUIUtility.hotControl = controlId;
                        degrees = DirectionFromPointer(center, current.mousePosition);
                        current.Use();
                        GUI.changed = true;
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == controlId)
                    {
                        degrees = DirectionFromPointer(center, current.mousePosition);
                        current.Use();
                        GUI.changed = true;
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        current.Use();
                    }
                    break;
                case EventType.Repaint:
                    Handles.BeginGUI();
                    Color old = Handles.color;
                    Handles.color = new Color(0.55f, 0.6f, 0.68f, 1f);
                    Handles.DrawWireDisc(center, Vector3.forward, radius);
                    Handles.DrawWireDisc(center, Vector3.forward, radius * 0.5f);
                    Handles.color = new Color(0.75f, 0.55f, 1f, 1f);
                    Handles.DrawAAPolyLine(2f, center, center + Bearing(cloudDegrees) * radius);
                    Handles.color = new Color(0.2f, 0.85f, 1f, 1f);
                    Handles.DrawAAPolyLine(3f, center, center + Bearing(degrees) * radius);
                    Handles.color = old;
                    Handles.EndGUI();
                    GUI.Label(new Rect(center.x - 8f, rect.y - 4f, 22f, 18f), "Z+");
                    GUI.Label(new Rect(center.x + radius + 2f, center.y - 9f, 24f, 18f), "X+");
                    break;
            }

            return Mathf.Repeat(degrees, 360f);
        }

        static Vector2 Bearing(float degrees)
            => new(Mathf.Cos(degrees * Mathf.Deg2Rad), -Mathf.Sin(degrees * Mathf.Deg2Rad));

        static float DirectionFromPointer(Vector2 center, Vector2 pointer)
        {
            Vector2 delta = pointer - center;
            return Mathf.Repeat(Mathf.Atan2(-delta.y, delta.x) * Mathf.Rad2Deg, 360f);
        }

        /// <summary>
        /// One channel's window inside a shared transition, drawn against the whole
        /// transition width. The ordering is the entire point of the timings asset and it is
        /// not readable as fourteen delay/span numbers.
        /// </summary>
        public static void TimelineRow(
            string label, float delay, float span, Color color, float master = -1f)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(96f));
                Rect rect = GUILayoutUtility.GetRect(80f, 16f, GUILayout.ExpandWidth(true));
                rect.y += 2f;
                rect.height = 12f;
                EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.22f));

                float start = Mathf.Clamp(delay, 0f, 0.95f);
                float end = Mathf.Min(1f, start + Mathf.Max(0.05f, span));
                Rect bar = rect;
                bar.x = rect.x + rect.width * start;
                bar.width = Mathf.Max(2f, rect.width * (end - start));
                EditorGUI.DrawRect(bar, color);

                if (master >= 0f)
                {
                    Rect head = rect;
                    head.x = rect.x + rect.width * Mathf.Clamp01(master);
                    head.width = 2f;
                    EditorGUI.DrawRect(head, Color.white);
                }

                EditorGUILayout.LabelField($"{start:0.00} - {end:0.00}", GUILayout.Width(78f));
            }
        }
    }
}
