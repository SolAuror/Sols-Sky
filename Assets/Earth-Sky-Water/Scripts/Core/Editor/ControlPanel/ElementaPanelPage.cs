using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Sol.Environment.EditorTools
{
    /// <summary>A retained page. Only live values refresh; controls survive editor ticks.</summary>
    abstract class ElementaPanelPage : IDisposable
    {
        readonly List<Action> _live = new();
        readonly HashSet<UnityEngine.Object> _targets = new();
        public abstract string Title { get; }
        public string Id => Title.ToLowerInvariant();
        public virtual string Subtitle => null;
        public ElementaPanelContext Context { get; private set; }
        public VisualElement Root { get; private set; }
        public ElementaFieldCatalogue Catalogue { get; } = new();
        public VisualElement BuildContent(ElementaPanelContext context)
        {
            Dispose(); Context = context;
            Root = new VisualElement { name = Id + "-page" };
            Root.AddToClassList("elementa-page");
            Build(context, Root); Refresh(); return Root;
        }
        protected abstract void Build(ElementaPanelContext context, VisualElement root);
        public virtual int Badge(ElementaPanelContext context) => 0;
        public virtual void DrawSceneGui(ElementaPanelContext context, SceneView sceneView) { }
        public void Track(Action refresh) => _live.Add(refresh);
        public void Watch(UnityEngine.Object target)
        {
            if (target == null || !_targets.Add(target)) return;
            Context.Watch(target);
            var tracker = new VisualElement { name = "binding-watch" };
            tracker.style.display = DisplayStyle.None;
            Root.Add(tracker);
            tracker.TrackSerializedObjectValue(Context.Serialized(target), _ => Context.CheckEdits(immediate: true));
        }
        public void Refresh() { for (int i = 0; i < _live.Count; i++) _live[i](); }
        public virtual void Dispose()
        {
            Root?.Unbind(); Root?.RemoveFromHierarchy(); Root = null;
            _live.Clear(); _targets.Clear(); Catalogue.Clear();
        }
    }
}
