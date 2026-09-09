using UnityEditor;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// One page of the Elementa control panel.
    ///
    /// Pages are constructed by the window and hold no serialized state of their own -
    /// anything that has to survive a domain reload lives in <see cref="ElementaPanelState"/>
    /// - so a page can be added, removed or reordered without a migration.
    /// </summary>
    abstract class ElementaPanelPage
    {
        /// <summary>Navigation label. Kept short: it has to fit the sidebar.</summary>
        public abstract string Title { get; }

        /// <summary>One line under the page heading saying what this page owns.</summary>
        public virtual string Subtitle => null;

        /// <summary>Drawn inside the window's scroll view, after the page heading.</summary>
        public abstract void Draw(ElementaPanelContext context);

        /// <summary>
        /// Scene view overlay for this page only. Called for the visible page, so a page can
        /// draw handles without every other page's gizmos arriving with them.
        /// </summary>
        public virtual void DrawSceneGui(ElementaPanelContext context, SceneView sceneView)
        {
        }

        /// <summary>Count shown as a badge beside the navigation entry. 0 draws nothing.</summary>
        public virtual int Badge(ElementaPanelContext context) => 0;

        public virtual void OnWindowEnable(ElementaPanelContext context)
        {
        }

        public virtual void OnWindowDisable(ElementaPanelContext context)
        {
        }
    }
}
