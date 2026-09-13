using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Sol.Landscape.Editor
{
    [InitializeOnLoad]
    static class SolLandscapeLifecycle
    {
        internal static event Action BeforeAssetSave;
        internal static int CreatingPaintAsset;
        internal static string[] FlushForAssetSave(string[] paths)
        {
            // CreateAsset also enters this callback. It is called inside BeforeTileChange,
            // before the GPU dab exists, so ending the stroke here would invalidate ApplyDab.
            if (CreatingPaintAsset > 0) return paths;
            BeforeAssetSave?.Invoke();
            // A stroke can make another tile dirty while the save's original path list is being gathered.
            return paths.Concat(Groups().SelectMany(g=>g.tiles).Where(t=>t.paint!=null && EditorUtility.IsDirty(t.paint))
                .Select(t=>AssetDatabase.GetAssetPath(t.paint)).Where(p=>!string.IsNullOrEmpty(p))).Distinct().ToArray();
        }
        static SolLandscapeLifecycle()
        {
            EditorSceneManager.sceneSaving += BeforeSave;
            EditorSceneManager.sceneSaved += AfterSave;
            AssemblyReloadEvents.beforeAssemblyReload += Release;
            EditorApplication.playModeStateChanged += _ => Release();
            Undo.undoRedoPerformed += Refresh;
        }
        static SolLandscapeGroup[] Groups() => UnityEngine.Object.FindObjectsByType<SolLandscapeGroup>(FindObjectsSortMode.None);
        static void BeforeSave(Scene scene,string path) { BeforeAssetSave?.Invoke();foreach(var group in Groups())if(group.gameObject.scene==scene)group.SuspendBindings(); }
        static void AfterSave(Scene scene) { foreach(var group in Groups())if(group.gameObject.scene==scene)group.Publish(); }
        static void Release() { BeforeAssetSave?.Invoke();foreach(var group in Groups()){group.ClearPreview();group.SuspendBindings();} }
        static void Refresh()
        {foreach(var group in Groups()){foreach(var tile in group.tiles)tile.paint?.RefreshTextures();group.Invalidate();}EditorApplication.QueuePlayerLoopUpdate();SceneView.RepaintAll();}
    }
    sealed class SolLandscapePaintSaveProcessor : AssetModificationProcessor
    {
        static string[] OnWillSaveAssets(string[] paths) => SolLandscapeLifecycle.FlushForAssetSave(paths);
    }
}
