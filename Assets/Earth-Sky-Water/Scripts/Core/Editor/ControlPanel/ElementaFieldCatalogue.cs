using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sol.Environment.EditorTools
{
    enum ElementaFieldRole { Curated, Advanced, ReadOnly, Routed }
    /// <summary>Records actual bound controls and explicit routes, for coverage and navigation.</summary>
    sealed class ElementaFieldCatalogue
    {
        public readonly struct Entry
        {
            public readonly Object Target;
            public readonly string Path;
            public readonly ElementaFieldRole Role;
            public readonly string Route;
            public readonly VisualElement Control;
            public Entry(Object target, string path, ElementaFieldRole role, VisualElement control, string route)
                => (Target, Path, Role, Control, Route) = (target, path, role, control, route);
        }
        readonly List<Entry> _entries = new();
        public IReadOnlyList<Entry> Entries => _entries;
        public void Add(Object target, string path, ElementaFieldRole role, VisualElement control, string route = null)
            => _entries.Add(new Entry(target, path, role, control, route));
        public bool Contains(Object target, string path) => _entries.Exists(e => e.Target == target && e.Path == path);
        public void Clear() => _entries.Clear();
    }
}
