using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Sol.Landscape.Editor
{
    [Serializable]
    public sealed class SolLandscapeCreationSettings
    {
        public string name = "Landscape", preset = "Temperate";
        public Scene scene;
        public GameObject emptyRoot;
        public Vector2 origin;
        public int columns = 2, rows = 2, heightResolution = 257, mapResolution = 512;
        public float tileSize = 512, verticalRange = 128, elevation;
        public float LoweringReserve => verticalRange * .25f;
        public Bounds Footprint => new(new Vector3(origin.x + columns * tileSize * .5f, elevation, origin.y + rows * tileSize * .5f), new Vector3(columns * tileSize, 0, rows * tileSize));
        public long EstimatedBytes => columns * (long)rows * (heightResolution * (long)heightResolution * 2 + mapResolution * (long)mapResolution * 8);
    }
    public static class SolLandscapeCreation
    {
        public static SolLandscapeProfile Preset(string name) => AssetDatabase.LoadAssetAtPath<SolLandscapeProfile>(SolLandscapeLibrary.Root + "/Profiles/" + name + ".asset");
        public static List<string> Validate(SolLandscapeCreationSettings settings)
        {
            var issues = new List<string>();
            if (!settings.scene.IsValid() || !settings.scene.isLoaded || string.IsNullOrEmpty(settings.scene.path)) issues.Add("Save and load the target scene first.");
            if (!SolLandscapeAssetLocations.ValidName(settings.name)) issues.Add("Enter a valid landscape name.");
            else if (settings.scene.IsValid() && !string.IsNullOrEmpty(settings.scene.path) && AssetDatabase.IsValidFolder(SolLandscapeAssetLocations.SceneRoot(settings.scene, settings.name))) issues.Add("This destination already exists. Choose a different landscape name.");
            if (settings.columns < 1 || settings.rows < 1 || settings.columns > 16 || settings.rows > 16) issues.Add("Use between 1 and 16 tiles on each axis.");
            if (!Finite(settings.tileSize) || settings.tileSize < 16 || settings.tileSize > 8192 || !Finite(settings.verticalRange) || settings.verticalRange < 16 || settings.verticalRange > 8192) issues.Add("Tile size and height range must be between 16 and 8192 metres.");
            if (!Finite(settings.elevation) || !Finite(settings.origin.x) || !Finite(settings.origin.y)) issues.Add("Use finite placement and elevation values.");
            if (!new[] { 33, 65, 129, 257, 513, 1025, 2049, 4097 }.Contains(settings.heightResolution)) issues.Add("Choose a supported height resolution.");
            if (!new[] { 64, 128, 256, 512, 1024, 2048 }.Contains(settings.mapResolution)) issues.Add("Choose a supported control/paint resolution.");
            if (Preset(settings.preset) == null) issues.Add("The selected starter palette is missing. Rebuild the Landscape library before creating.");
            var root = settings.emptyRoot;
            if (root != null && (root.scene != settings.scene || root.transform.childCount != 0 || root.transform.rotation != Quaternion.identity || root.transform.lossyScale != Vector3.one || root.GetComponent<Terrain>() != null)) issues.Add("Reuse only an empty, unscaled and unrotated root in the target scene.");
            if (root != null && root.GetComponents<Component>().Any(c => !(c is Transform || c is SolLandscapeGroup))) issues.Add("The selected root has other components. Use a new landscape root to keep those objects intact.");
            if (root != null && root.TryGetComponent<SolLandscapeGroup>(out var group) && (group.tiles.Count != 0 || group.profile != null)) issues.Add("The selected root already owns a landscape. Choose a new root.");
            return issues;
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public static SolLandscapeGroup Create(SolLandscapeCreationSettings settings, Func<string, float, bool> cancelProgress = null)
        {
            var issues = Validate(settings); if (issues.Count > 0) throw new InvalidOperationException(string.Join("\n", issues));
            string destination = SolLandscapeAssetLocations.SceneRoot(settings.scene, settings.name);
            // Track exactly which folders did not exist before this transaction, including scene scaffolding.
            var newFolders = new List<string>(); string cursor = destination;
            while (!AssetDatabase.IsValidFolder(cursor)) { newFolders.Add(cursor); cursor = System.IO.Path.GetDirectoryName(cursor).Replace('\\', '/'); }
            Undo.IncrementCurrentGroup(); int transaction = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Create landscape");
            try
            {
                var owner = SolLandscapeAssetLocations.Create(destination, settings.scene, settings.name);
                var profile = UnityEngine.Object.Instantiate(Preset(settings.preset)); profile.name = settings.name + " Profile"; profile.assetOwner = owner; SolLandscapeLibrary.BindRuntimeShaders(profile);
                AssetDatabase.CreateAsset(profile, destination + "/Profiles/" + profile.name + ".asset"); SolLandscapeAssetLocations.Register(owner, profile);
                if (profile.CSArray == null || profile.NOHArray == null || profile.CSArray.depth < profile.Layers.Count || profile.NOHArray.depth < profile.Layers.Count)
                {
                    EditorUtility.DisplayProgressBar("Create landscape", "Building missing palette artwork", .05f);
                    SolLandscapeArrayBaker.BakeTarget(profile);
                }
                var root = settings.emptyRoot;
                if (root == null) { root = new GameObject(settings.name); SceneManager.MoveGameObjectToScene(root, settings.scene); Undo.RegisterCreatedObjectUndo(root, "Create landscape"); }
                else Undo.RegisterCompleteObjectUndo(root.transform, "Place landscape");
                var group = root.GetComponent<SolLandscapeGroup>() ?? Undo.AddComponent<SolLandscapeGroup>(root);
                Undo.RegisterCompleteObjectUndo(group, "Create landscape");
                Undo.RegisterCompleteObjectUndo(root.transform, "Place landscape root");
                root.transform.position = new Vector3(settings.origin.x, settings.elevation - settings.LoweringReserve, settings.origin.y);
                group.assetOwner = owner; group.profile = profile; group.paintResolution = settings.mapResolution; group.projectionOrigin = settings.origin;
                int n = settings.heightResolution, m = settings.mapResolution;
                var heights = new float[n, n]; for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) heights[y, x] = .25f;
                var weights = new float[m, m, profile.Layers.Count]; for (int y = 0; y < m; y++) for (int x = 0; x < m; x++) weights[y, x, profile.FallbackIndex] = 1;
                var createdTiles = new List<SolLandscapeTile>();
                for (int z = 0; z < settings.rows; z++) for (int x = 0; x < settings.columns; x++)
                {
                    string progress = $"Creating tile {x}, {z}"; float fraction = .1f + .85f * (z * settings.columns + x) / (settings.rows * settings.columns);
                    if (cancelProgress != null ? cancelProgress(progress, fraction) : EditorUtility.DisplayCancelableProgressBar("Create landscape", progress, fraction)) throw new OperationCanceledException("Landscape creation cancelled. New objects and assets were rolled back.");
                    var data = new TerrainData { name = $"Tile_{x:D2}_{z:D2}", heightmapResolution = n, alphamapResolution = m, size = new Vector3(settings.tileSize, settings.verticalRange, settings.tileSize), terrainLayers = profile.Layers.Select(e => e.terrainLayer).ToArray() };
                    data.SetHeights(0, 0, heights); data.SetAlphamaps(0, 0, weights);
                    AssetDatabase.CreateAsset(data, destination + "/TerrainData/" + data.name + ".asset"); SolLandscapeAssetLocations.Register(owner, data);
                    var tile = Terrain.CreateTerrainGameObject(data); tile.name = data.name; SceneManager.MoveGameObjectToScene(tile, settings.scene); Undo.RegisterCreatedObjectUndo(tile, "Create landscape");
                    Undo.SetTransformParent(tile.transform, root.transform, "Create landscape");
                    Undo.RegisterCompleteObjectUndo(tile.transform, "Place landscape tile"); tile.transform.localPosition = new Vector3(x * settings.tileSize, 0, z * settings.tileSize);
                    createdTiles.Add(new SolLandscapeTile { terrain = tile.GetComponent<Terrain>() }); EditorUtility.SetDirty(data);
                }
                // RegisterCreatedObjectUndo flushes existing records. Assign the final collection only
                // after every child exists, so redo captures the complete group instead of an empty list.
                Undo.RegisterCompleteObjectUndo(group, "Assign landscape tiles"); group.tiles = createdTiles;
                SolLandscapeAuthoring.Connect(group);
                if (!group.Validate(out string reason)) throw new InvalidOperationException(reason);
                EditorUtility.SetDirty(group); EditorUtility.SetDirty(owner); EditorSceneManager.MarkSceneDirty(settings.scene);
                AssetDatabase.SaveAssets(); group.Invalidate(); group.Publish(); Undo.CollapseUndoOperations(transaction);
                Selection.activeGameObject = root;
                SceneView.lastActiveSceneView?.Frame(new Bounds(settings.Footprint.center, settings.Footprint.size + Vector3.up * settings.verticalRange), false);
                return group;
            }
            catch
            {
                Undo.RevertAllDownToGroup(transaction);
                if (AssetDatabase.IsValidFolder(destination)) AssetDatabase.DeleteAsset(destination);
                foreach (string folder in newFolders.Skip(1)) if (AssetDatabase.IsValidFolder(folder) && !System.IO.Directory.EnumerateFileSystemEntries(folder).Any()) AssetDatabase.DeleteAsset(folder);
                throw;
            }
            finally { EditorUtility.ClearProgressBar(); }
        }
    }
}
