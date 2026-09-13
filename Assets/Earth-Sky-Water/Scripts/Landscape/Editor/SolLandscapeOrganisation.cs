using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sol.Landscape.Editor
{
    public sealed class SolLandscapeOrganisationPlan
    {
        public SolLandscapeGroup group;
        public string destination;
        public readonly List<SolLandscapeAssetMove> moves = new();
        public readonly List<string> retained = new(), ambiguous = new();
        public string Report => "Destination: " + destination + "\n\nMoves (GUIDs preserved):\n" + string.Join("\n", moves.Select(m => m.before + " → " + m.after))
            + "\n\nShared dependencies retained:\n" + string.Join("\n", retained) + "\n\nOwnership needed (left in place):\n" + string.Join("\n", ambiguous);
    }
    public static class SolLandscapeOrganisation
    {
        static bool Library(string path) => path.StartsWith(SolLandscapeLibrary.Root + "/", StringComparison.Ordinal)
            || path.StartsWith("Assets/Earth-Sky-Water/Landscape/Resources/", StringComparison.Ordinal);
        public static SolLandscapeOrganisationPlan Preview(SolLandscapeGroup group, bool relocate = false)
        {
            if (group == null) throw new ArgumentNullException(nameof(group));
            string name = group.assetOwner != null ? group.assetOwner.landscapeName : group.name;
            var plan = new SolLandscapeOrganisationPlan { group = group, destination = group.assetOwner != null && !relocate ? SolLandscapeAssetLocations.Root(group.assetOwner) : SolLandscapeAssetLocations.SceneRoot(group.gameObject.scene, name) };
            var live = new HashSet<UnityEngine.Object>();
            if (group.profile != null) { live.Add(group.profile); live.Add(group.profile.CSArray); live.Add(group.profile.NOHArray); }
            foreach (var tile in group.tiles) { if (tile.terrain != null) live.Add(tile.terrain.terrainData); live.Add(tile.paint); }
            live.Remove(null);
            var candidates = new HashSet<UnityEngine.Object>(live);
            if (group.assetOwner != null) { candidates.UnionWith(group.assetOwner.ownedAssets.Where(a => a != null)); candidates.Add(group.assetOwner); }
            var candidatePaths = candidates.Select(AssetDatabase.GetAssetPath).Where(p => !string.IsNullOrEmpty(p)).ToHashSet();
            var reverse = new Dictionary<string, List<string>>();
            // Direct reference graph includes scenes, prefabs and assets. Traversal below follows indirect consumers.
            foreach (string path in AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith("Assets/") && !AssetDatabase.IsValidFolder(p)))
                foreach (string dependency in AssetDatabase.GetDependencies(path, false).Where(p => p != path))
                { if (!reverse.TryGetValue(dependency, out var users)) reverse[dependency] = users = new List<string>(); users.Add(path); }
            bool Shared(string path)
            {
                var pending = new Queue<string>(); pending.Enqueue(path); var visited = new HashSet<string>();
                while (pending.Count > 0)
                {
                    string current = pending.Dequeue(); if (!visited.Add(current) || !reverse.TryGetValue(current, out var users)) continue;
                    foreach (string user in users)
                    {
                        if (user == group.gameObject.scene.path) continue;
                        if (!candidatePaths.Contains(user)) return true;
                        pending.Enqueue(user);
                    }
                }
                // Unsaved loaded groups may not yet be represented by the on-disk scene graph.
                return UnityEngine.Object.FindObjectsByType<SolLandscapeGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(g => g != group &&
                    (AssetDatabase.GetAssetPath(g.profile) == path || (g.profile != null && (AssetDatabase.GetAssetPath(g.profile.CSArray) == path || AssetDatabase.GetAssetPath(g.profile.NOHArray) == path)) || g.tiles.Any(t => AssetDatabase.GetAssetPath(t.paint) == path || (t.terrain != null && AssetDatabase.GetAssetPath(t.terrain.terrainData) == path))));
            }
            foreach (var asset in candidates)
            {
                string before = AssetDatabase.GetAssetPath(asset); if (string.IsNullOrEmpty(before)) continue;
                if (Library(before) || Shared(before)) { plan.retained.Add(before); continue; }
                string folder = asset is SolLandscapeAsset ? "" : !live.Contains(asset) ? "Archive/" : asset is TerrainData ? "TerrainData/" : asset is SolLandscapePaintData ? "Paint/" : asset is SolLandscapeProfile ? "Profiles/" : "Generated/Bakes/Imported/";
                string after = plan.destination + "/" + folder + Path.GetFileName(before);
                if (before == after) continue;
                if (AssetDatabase.LoadMainAssetAtPath(after) != null || plan.moves.Any(m => m.after == after))
                    after = plan.destination + "/" + folder + Path.GetFileNameWithoutExtension(before) + "_" + AssetDatabase.AssetPathToGUID(before).Substring(0, 8) + Path.GetExtension(before);
                plan.moves.Add(new SolLandscapeAssetMove { guid = AssetDatabase.AssetPathToGUID(before), before = before, after = after, utc = DateTime.UtcNow.ToString("O") });
            }
            // No owner and no scene/prefab consumer is evidence of ambiguity, not permission to discard.
            foreach (string guid in AssetDatabase.FindAssets("t:TerrainData t:SolLandscapeProfile t:SolLandscapePaintData"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!candidatePaths.Contains(path) && !Library(path) && (!reverse.TryGetValue(path, out var users) || users.Count == 0)) plan.ambiguous.Add(path);
            }
            return plan;
        }
        public static void Apply(SolLandscapeOrganisationPlan plan)
        {
            SolLandscapeLifecycle.FlushForAssetSave(Array.Empty<string>());
            foreach (var move in plan.moves)
                if (AssetDatabase.GUIDToAssetPath(move.guid) != move.before || AssetDatabase.LoadMainAssetAtPath(move.after) != null)
                    throw new InvalidOperationException("The inventory changed. Preview again before moving " + move.before);
            var moved = new List<SolLandscapeAssetMove>(); var owner = plan.group.assetOwner; bool newOwner = owner == null;
            try
            {
                if (newOwner) owner = SolLandscapeAssetLocations.Create(plan.destination, plan.group.gameObject.scene, plan.group.name);
                foreach (var move in plan.moves)
                {
                    SolLandscapeAssetLocations.EnsureFolder(Path.GetDirectoryName(move.after).Replace('\\', '/'));
                    string failure = AssetDatabase.MoveAsset(move.before, move.after); if (!string.IsNullOrEmpty(failure)) throw new IOException(failure);
                    moved.Add(move); if (AssetDatabase.GUIDToAssetPath(move.guid) != move.after) throw new IOException("GUID verification failed: " + move.before);
                }
                Undo.RecordObject(plan.group, "Assign landscape asset ownership"); plan.group.assetOwner = owner;
                if (plan.group.profile != null && !plan.retained.Contains(AssetDatabase.GetAssetPath(plan.group.profile)))
                { plan.group.profile.assetOwner = owner; SolLandscapeAssetLocations.Register(owner, plan.group.profile); EditorUtility.SetDirty(plan.group.profile); }
                foreach (var move in moved) { SolLandscapeAssetLocations.Register(owner, AssetDatabase.LoadMainAssetAtPath(move.after)); owner.moves.Add(move); }
                owner.sceneGuid = AssetDatabase.AssetPathToGUID(plan.group.gameObject.scene.path);
                EditorUtility.SetDirty(owner); EditorUtility.SetDirty(plan.group); EditorSceneManager.MarkSceneDirty(plan.group.gameObject.scene); AssetDatabase.SaveAssets();
                string report = SolLandscapeAssetLocations.Root(owner) + "/Organisation report.txt";
                File.WriteAllText(report, plan.Report + "\n\nCompleted " + DateTime.UtcNow.ToString("O") + ". Reverse moves using Landscape.asset's move manifest."); AssetDatabase.ImportAsset(report);
            }
            catch
            {
                foreach (var move in moved.AsEnumerable().Reverse()) AssetDatabase.MoveAsset(move.after, move.before);
                if (newOwner && owner != null) AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(owner));
                throw;
            }
        }
        public static void ReverseMoves(SolLandscapeAsset owner)
        {
            var moves = owner.moves.AsEnumerable().Reverse().ToArray();
            // Multiple journals may contain the same GUID. Validate each step against a virtual path map.
            var paths = new Dictionary<string, string>();
            foreach (var move in moves)
            {
                if (!paths.TryGetValue(move.guid, out string current)) current = AssetDatabase.GUIDToAssetPath(move.guid);
                if (current != move.after || AssetDatabase.LoadMainAssetAtPath(move.before) != null) throw new InvalidOperationException("Cannot restore " + move.before + ". Resolve destination conflicts first.");
                paths[move.guid] = move.before;
            }
            var applied = new List<SolLandscapeAssetMove>();
            try
            {
                foreach (var move in moves)
                {
                    SolLandscapeAssetLocations.EnsureFolder(Path.GetDirectoryName(move.before).Replace('\\', '/'));
                    string failure = AssetDatabase.MoveAsset(move.after, move.before); if (!string.IsNullOrEmpty(failure)) throw new IOException(failure); applied.Add(move);
                }
                owner.moves.Clear(); EditorUtility.SetDirty(owner); AssetDatabase.SaveAssets();
            }
            catch { foreach (var move in applied.AsEnumerable().Reverse()) AssetDatabase.MoveAsset(move.before, move.after); throw; }
        }
    }
    public sealed class SolLandscapeOrganisationWindow : EditorWindow
    {
        SolLandscapeOrganisationPlan plan;
        public static void Open(SolLandscapeGroup group)
        {
            var window = CreateInstance<SolLandscapeOrganisationWindow>(); window.plan = SolLandscapeOrganisation.Preview(group); window.titleContent = new GUIContent("Organise landscape assets"); window.minSize = new Vector2(650, 480); window.Show(); window.Build();
        }
        void Build()
        {
            rootVisualElement.Clear(); var scroll = new ScrollView(); scroll.style.flexGrow = 1; rootVisualElement.Add(scroll);
            scroll.Add(new Label(plan.Report) { style = { whiteSpace = WhiteSpace.Normal } });
            var assignment = new ObjectField("Assign an ambiguous asset") { objectType = typeof(UnityEngine.Object), allowSceneObjects = false }; scroll.Add(assignment);
            scroll.Add(new HelpBox("Assign ownership only when you recognise the asset. Unused owned assets go to Archive; other scenes, prefabs and library consumers still prevent a move.", HelpBoxMessageType.Info));
            scroll.Add(new Button(() =>
            {
                var asset = assignment.value;
                if (!(asset is TerrainData || asset is SolLandscapeProfile || asset is SolLandscapePaintData || asset is Texture2DArray)) { scroll.Add(new HelpBox("Choose terrain data, a landscape profile, paint data or a generated array.", HelpBoxMessageType.Error)); return; }
                try
                {
                    if (plan.group.assetOwner == null) { plan.group.assetOwner = SolLandscapeAssetLocations.Create(plan.destination, plan.group.gameObject.scene, plan.group.name); EditorUtility.SetDirty(plan.group); }
                    SolLandscapeAssetLocations.Register(plan.group.assetOwner, asset); AssetDatabase.SaveAssets(); plan = SolLandscapeOrganisation.Preview(plan.group); Build();
                }
                catch (Exception e) { scroll.Add(new HelpBox(e.Message, HelpBoxMessageType.Error)); }
            }) { text = "Assign to this landscape and refresh preview" });
            rootVisualElement.Add(new Button(() => { try { SolLandscapeOrganisation.Apply(plan); Close(); } catch (Exception e) { scroll.Add(new HelpBox(e.Message, HelpBoxMessageType.Error)); } }) { text = "Apply reviewed moves" });
            rootVisualElement.Add(new Button(() => { plan = SolLandscapeOrganisation.Preview(plan.group, true); Build(); }) { text = "Preview relocation beside current scene" });
            if (plan.group.assetOwner != null && plan.group.assetOwner.moves.Count > 0) rootVisualElement.Add(new Button(() => { try { SolLandscapeOrganisation.ReverseMoves(plan.group.assetOwner); Close(); } catch (Exception e) { scroll.Add(new HelpBox(e.Message, HelpBoxMessageType.Error)); } }) { text = "Reverse recorded moves" });
        }
    }
}
