using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Sol.Landscape.Editor
{
    public static class SolLandscapeAssetLocations
    {
        public static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) && name == name.Trim()
            && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name != "." && name != ".." && !name.EndsWith(".");
        public static string SceneRoot(Scene scene, string name)
        {
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path)) throw new InvalidOperationException("Save the target scene before creating landscape assets.");
            if (!ValidName(name)) throw new InvalidOperationException("Enter a landscape name without slashes or filename punctuation.");
            return Path.GetDirectoryName(scene.path).Replace('\\', '/') + "/" + Path.GetFileNameWithoutExtension(scene.path) + "/Landscapes/" + name;
        }
        public static string Root(SolLandscapeAsset asset) => Path.GetDirectoryName(AssetDatabase.GetAssetPath(asset)).Replace('\\', '/');
        public static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            if (!path.StartsWith("Assets/", StringComparison.Ordinal)) throw new InvalidOperationException("Landscape destinations must be inside Assets.");
            EnsureFolder(parent);
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(parent, Path.GetFileName(path)))) throw new IOException("Could not create " + path);
        }
        public static SolLandscapeAsset Create(string root, Scene scene, string name)
        {
            EnsureFolder(root);
            if (AssetDatabase.LoadMainAssetAtPath(root + "/Landscape.asset") != null) throw new InvalidOperationException("This landscape destination is already in use.");
            var asset = ScriptableObject.CreateInstance<SolLandscapeAsset>();
            asset.landscapeId = Guid.NewGuid().ToString("N"); asset.sceneGuid = AssetDatabase.AssetPathToGUID(scene.path); asset.landscapeName = name;
            AssetDatabase.CreateAsset(asset, root + "/Landscape.asset");
            foreach (var folder in new[] { "Profiles", "TerrainData", "Paint", "Generated/Bakes", "Archive" }) EnsureFolder(root + "/" + folder);
            return asset;
        }
        public static void Register(SolLandscapeAsset owner, UnityEngine.Object asset)
        {
            if (owner == null || asset == null || owner.ownedAssets.Contains(asset)) return;
            owner.ownedAssets.Add(asset); EditorUtility.SetDirty(owner);
        }
        public static string PathFor(SolLandscapeGroup group, string folder, string name)
        {
            // Old standalone profiles remain supported until the explicit organisation action.
            string root = group.assetOwner != null ? Root(group.assetOwner) + "/" + folder : SolLandscapeAuthoring.Folder(group.profile);
            EnsureFolder(root); return AssetDatabase.GenerateUniqueAssetPath(root + "/" + name + ".asset");
        }
        public static string NewBakeFolder(SolLandscapeProfile profile)
        {
            string root = profile.assetOwner != null ? Root(profile.assetOwner) : SolLandscapeAuthoring.Folder(profile);
            string builds = root + "/Generated/Bakes"; EnsureFolder(builds);
            int build = 1; while (AssetDatabase.IsValidFolder(builds + "/" + build.ToString("D4"))) build++;
            string result = builds + "/" + build.ToString("D4"); EnsureFolder(result); return result;
        }
        public static SolLandscapeProfile DuplicateProfile(SolLandscapeGroup group)
        {
            SolLandscapeLifecycle.FlushForAssetSave(Array.Empty<string>());
            var copy = UnityEngine.Object.Instantiate(group.profile); copy.name = group.profile.name + " Copy"; copy.assetOwner = group.assetOwner;
            AssetDatabase.CreateAsset(copy, PathFor(group, "Profiles", copy.name)); Register(group.assetOwner, copy);
            Undo.RecordObject(group, "Duplicate landscape profile"); group.profile = copy; EditorUtility.SetDirty(group); group.Invalidate(); AssetDatabase.SaveAssets(); return copy;
        }
    }
}
