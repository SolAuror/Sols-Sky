using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Sol.Landscape.Editor
{
    public static class SolLandscapeAssetInventory
    {
        public static void ReadOnlyAuditExisting()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Use the batch validation project for this audit.");
            var report = new StringBuilder(Report()).AppendLine("\n# Recorded moves and reopened references\n");
            foreach (string guid in AssetDatabase.FindAssets("t:SolLandscapeAsset"))
            {
                var owner = AssetDatabase.LoadAssetAtPath<SolLandscapeAsset>(AssetDatabase.GUIDToAssetPath(guid));
                foreach (var move in owner.moves)
                {
                    string current = AssetDatabase.GUIDToAssetPath(move.guid);
                    report.AppendLine(move.before + " → " + move.after + " · GUID " + move.guid + " · " + (current == move.after ? "verified" : "CURRENT PATH: " + current));
                    if (current != move.after) throw new InvalidOperationException("Move manifest mismatch: " + move.before);
                }
            }
            foreach (string path in AssetDatabase.FindAssets("t:Scene").Select(AssetDatabase.GUIDToAssetPath).Where(p => p.StartsWith("Assets/")))
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                foreach (var group in scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<SolLandscapeGroup>(true)))
                {
                    report.AppendLine(path + " / " + group.name + ": " + (group.Validate(out var reason) ? "valid" : reason));
                    foreach (var tile in group.tiles)
                    {
                        if (tile.terrain == null || tile.terrain.terrainData == null) throw new InvalidOperationException("Missing terrain reference in " + path);
                        report.AppendLine("  TerrainData " + AssetDatabase.GetAssetPath(tile.terrain.terrainData) + " · GUID " + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(tile.terrain.terrainData)));
                        if (tile.paint != null) report.AppendLine("  Paint " + AssetDatabase.GetAssetPath(tile.paint));
                    }
                }
            }
            File.WriteAllText(Path.GetFullPath("../landscape-organisation-evidence.md"), report.ToString());
            EditorApplication.Exit(0);
        }
        public static string Report()
        {
            var roots = AssetDatabase.FindAssets("t:Scene t:Prefab").Select(AssetDatabase.GUIDToAssetPath).Where(p => p.StartsWith("Assets/")).ToArray();
            var dependencies = roots.ToDictionary(p => p, p => AssetDatabase.GetDependencies(p, true).ToHashSet());
            var candidates = new HashSet<string>(AssetDatabase.FindAssets("t:TerrainData t:SolLandscapeConfig t:SolLandscapePaintData t:SolLandscapeAsset").Select(AssetDatabase.GUIDToAssetPath));
            foreach (string path in candidates.ToArray()) if (AssetDatabase.LoadMainAssetAtPath(path) is SolLandscapeConfig profile)
            { if (profile.CSArray != null) candidates.Add(AssetDatabase.GetAssetPath(profile.CSArray)); if (profile.NOHArray != null) candidates.Add(AssetDatabase.GetAssetPath(profile.NOHArray)); }
            var report = new StringBuilder("# Landscape asset inventory\n\nOwnership is inferred from typed references, scenes and prefabs, or recorded explicitly in Landscape.asset. Unreferenced files are not assumed to belong to a deleted landscape.\n\n");
            foreach (string path in candidates.OrderBy(p => p))
            {
                var users = roots.Where(p => dependencies[p].Contains(path)).ToArray();
                report.AppendLine(path).AppendLine("  Type: " + AssetDatabase.GetMainAssetTypeAtPath(path)?.Name)
                    .AppendLine("  Consumers: " + (users.Length == 0 ? "none — ownership assignment required" : string.Join(", ", users)))
                    .AppendLine("  GUID: " + AssetDatabase.AssetPathToGUID(path));
            }
            return report.ToString();
        }
        // An explicit batch entry point for this change's reference-verified organisation pass.
        // It does not run on import, scene save, or domain reload.
        public static void OrganiseExisting()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Use Setup → Organise assets for an interactive, reviewed move.");
            string output = Path.GetFullPath("../landscape-organisation-evidence.md");
            var report = new StringBuilder(Report()).AppendLine("\n# Organisation pass\n");
            foreach (string path in AssetDatabase.FindAssets("t:Scene").Select(AssetDatabase.GUIDToAssetPath).Where(p => p.StartsWith("Assets/")).ToArray())
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single); bool changed = false;
                var originalIssues = new Dictionary<string, string>();
                foreach (var group in scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<SolLandscapeGroup>(true)))
                {
                    if (group.profile == null || group.tiles.Count == 0) { report.AppendLine(path + " / " + group.name + ": empty; left untouched."); continue; }
                    if (!group.Validate(out string existingIssue)) { originalIssues[group.name] = existingIssue; report.AppendLine(path + " / " + group.name + ": existing setup issue: " + existingIssue); }
                    var plan = SolLandscapeOrganisation.Preview(group); report.AppendLine("\n## " + path + " / " + group.name + "\n" + plan.Report);
                    if (group.assetOwner != null) foreach (var move in group.assetOwner.moves) report.AppendLine("Recorded move: " + move.before + " → " + move.after + " · " + move.guid);
                    if (plan.moves.Count == 0) continue;
                    var references = group.tiles.Select(t => new[] { AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(t.terrain.terrainData)), AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(t.paint)) }).ToArray();
                    SolLandscapeOrganisation.Apply(plan); changed = true;
                    for (int i = 0; i < group.tiles.Count; i++)
                    {
                        if (AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(group.tiles[i].terrain.terrainData)) != references[i][0]
                            || AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(group.tiles[i].paint)) != references[i][1]) throw new InvalidOperationException("A terrain/paint reference changed while organising " + path);
                    }
                }
                if (!changed) continue;
                EditorSceneManager.SaveScene(scene); scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                foreach (var group in scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<SolLandscapeGroup>(true)))
                    if (group.profile != null && group.tiles.Count > 0 && !group.Validate(out string reason) && (!originalIssues.TryGetValue(group.name, out var previous) || reason != previous)) throw new InvalidOperationException(path + ": reopened validation changed: " + reason);
                report.AppendLine("Reopened scene: validation state unchanged; terrain and paint GUIDs retained.");
            }
            report.AppendLine("\n# After\n" + Report()); File.WriteAllText(output, report.ToString());
            EditorApplication.Exit(0);
        }
    }
}
