using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using Sol.Landscape;
using Sol.Landscape.Editor;
using Sol.Environment.EditorTools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Sol.Tests.Editor
{
    public sealed class SolLandscapeCreationTests
    {
        const string Folder = "Assets/LandscapeCreationAcceptance";
        Scene scene;
        [SetUp] public void SetUp()
        {
            Assert.That(AssetDatabase.IsValidFolder(Folder), Is.False, "Use an isolated test destination.");
            AssetDatabase.CreateFolder("Assets", "LandscapeCreationAcceptance"); scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, Folder + "/Creation.unity");
        }
        [TearDown] public void TearDown()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<SolLandscapeCreationWizard>()) window.Close();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Undo.ClearAll(); AssetDatabase.DeleteAsset(Folder);
        }
        SolLandscapeCreationSettings Settings() => new() { scene = scene, name = "Test landscape", tileSize = 64, heightResolution = 33, mapResolution = 64 };
        [Test] public void Creation_CancellationAfterFirstTileRollsBackOnlyItsTransaction()
        {
            var root = new GameObject("Existing empty root"); SceneManager.MoveGameObjectToScene(root, scene); var group = root.AddComponent<SolLandscapeGroup>();
            var unrelated = new GameObject("Authored object"); SceneManager.MoveGameObjectToScene(unrelated, scene);
            var settings = Settings(); settings.emptyRoot = root;
            int progress = 0; Assert.Throws<OperationCanceledException>(() => SolLandscapeCreation.Create(settings, (_, _) => ++progress > 1));
            Assert.That(root, Is.Not.Null); Assert.That(unrelated, Is.Not.Null); Assert.That(group.profile, Is.Null); Assert.That(root.transform.childCount, Is.Zero);
            Assert.That(AssetDatabase.IsValidFolder(SolLandscapeAssetLocations.SceneRoot(scene, settings.name)), Is.False);
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(Folder + "/Creation.unity"), Is.Not.Null);
        }
        [Test] public void Creation_DefaultsAndReadOnlyValidationRejectUnsafeInputs()
        {
            var settings = new SolLandscapeCreationSettings { scene = scene };
            Assert.That(settings.columns * settings.rows, Is.EqualTo(4)); Assert.That(settings.tileSize, Is.EqualTo(512)); Assert.That(settings.heightResolution, Is.EqualTo(257)); Assert.That(settings.mapResolution, Is.EqualTo(512));
            Assert.That(settings.LoweringReserve, Is.GreaterThan(0)); Assert.That(SolLandscapeCreation.Validate(settings), Is.Empty);
            Assert.That(AssetDatabase.IsValidFolder(SolLandscapeAssetLocations.SceneRoot(scene, settings.name)), Is.False);
            settings.name = "../wrong"; Assert.That(SolLandscapeCreation.Validate(settings), Is.Not.Empty); Assert.Throws<InvalidOperationException>(() => SolLandscapeCreation.Create(settings));
            settings.name = "Safe"; settings.heightResolution = 256; Assert.Throws<InvalidOperationException>(() => SolLandscapeCreation.Create(settings));
            settings = Settings(); settings.scene = default; Assert.Throws<InvalidOperationException>(() => SolLandscapeCreation.Create(settings));
            Assert.That(AssetDatabase.GetSubFolders(Folder), Is.Empty);
        }
        [Test] public void Creation_ReuseEmptyRootUndoRedoSaveReopenAndPaintLocations()
        {
            var root = new GameObject("Empty root"); SceneManager.MoveGameObjectToScene(root, scene); var empty = root.AddComponent<SolLandscapeGroup>();
            root.transform.position = new Vector3(40, 20, 60);
            var settings = Settings(); settings.emptyRoot = root; settings.origin = new Vector2(40, 60); settings.elevation = 5;
            var group = SolLandscapeCreation.Create(settings); Assert.That(group, Is.SameAs(empty)); Assert.That(group.tiles.Count, Is.EqualTo(4)); Assert.That(group.Validate(out string reason), Is.True, reason);
            var owner = group.assetOwner; string rootPath = SolLandscapeAssetLocations.Root(owner);
            foreach (var tile in group.tiles)
            {
                Assert.That(tile.terrain.GetComponent<TerrainCollider>().terrainData, Is.SameAs(tile.terrain.terrainData));
                Assert.That(tile.terrain.transform.position.y + tile.terrain.terrainData.GetHeight(0, 0), Is.EqualTo(5).Within(.01f));
                Assert.That(AssetDatabase.GetAssetPath(tile.terrain.terrainData), Does.StartWith(rootPath + "/TerrainData/"));
            }
            Assert.That(group.tiles[0].terrain.rightNeighbor, Is.SameAs(group.tiles[1].terrain)); Assert.That(group.tiles[0].terrain.topNeighbor, Is.SameAs(group.tiles[2].terrain));
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo(); Assert.That(root, Is.Not.Null); Assert.That(empty.profile, Is.Null); Assert.That(empty.tiles, Is.Empty); Assert.That(root.transform.position, Is.EqualTo(new Vector3(40, 20, 60)));
            Assert.That(AssetDatabase.LoadAssetAtPath<SolLandscapeAsset>(rootPath + "/Landscape.asset"), Is.Not.Null);
            Undo.PerformRedo(); Assert.That(empty.Validate(out reason), Is.True, reason); group = empty;
            Assert.That(SolLandscapeCreation.Validate(settings).Any(s => s.Contains("already exists")), Is.True);
            int path = group.profile.Layers.ToList().FindIndex(e => e.terrainLayer.name.Contains("Path")); Assert.That(path, Is.GreaterThanOrEqualTo(0));
            using (var stroke = new SolLandscapeStroke())
            {
                stroke.BeforeTileChange += tile => SolLandscapeAuthoring.EnsurePaintAsset(group, tile); stroke.Committed += EditorUtility.SetDirty;
                stroke.BeginStroke(group, SolLandscapePaintOperation.RemoveMaterial, path); stroke.ApplyDab(new Vector3(104, 5, 124), 12, 1); stroke.CommitStroke();
            }
            Assert.That(group.tiles.All(t => t.paint != null && t.paint.HasRemovals), Is.True);
            foreach (var tile in group.tiles) Assert.That(AssetDatabase.GetAssetPath(tile.paint), Does.StartWith(rootPath + "/Paint/"));
            var originalProfile = group.profile; var copy = SolLandscapeAssetLocations.DuplicateProfile(group); Assert.That(copy, Is.Not.SameAs(originalProfile)); Assert.That(copy.CSArray, Is.SameAs(originalProfile.CSArray)); Assert.That(AssetDatabase.GetAssetPath(copy), Does.StartWith(rootPath + "/Profiles/"));
            AssetDatabase.SaveAssets(); EditorSceneManager.SaveScene(scene); scene = EditorSceneManager.OpenScene(Folder + "/Creation.unity", OpenSceneMode.Single);
            group = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<SolLandscapeGroup>()).Single(); Assert.That(group.Validate(out reason), Is.True, reason); Assert.That(group.tiles.All(t => t.paint.HasRemovals), Is.True);
        }
        [UnityTest] public IEnumerator Wizard_UsesNativeBackCancelAndCreateControls()
        {
            var root = new GameObject("Empty root"); SceneManager.MoveGameObjectToScene(root, scene); root.AddComponent<SolLandscapeGroup>();
            var wizard = SolLandscapeCreationWizard.Open(root, 0, (_, _) => { }); yield return null;
            Assert.That(wizard.rootVisualElement.Query<IMGUIContainer>().ToList(), Is.Empty);
            wizard.Settings.name = "Wizard landscape"; wizard.Settings.tileSize = 64; wizard.Settings.heightResolution = 33; wizard.Settings.mapResolution = 64; wizard.Rebuild();
            void Click(string label) { var button = wizard.rootVisualElement.Query<Button>().ToList().Single(b => b.text == label); Assert.That(button.enabledInHierarchy, Is.True, label); Activate(button); }
            Click("Next"); Assert.That(wizard.Step, Is.EqualTo(1)); Click("Back"); Assert.That(wizard.Step, Is.Zero);
            Click("Next"); Click("Next"); Click("Next"); Assert.That(wizard.Step, Is.EqualTo(3));
            Assert.That(AssetDatabase.IsValidFolder(SolLandscapeAssetLocations.SceneRoot(scene, wizard.Settings.name)), Is.False);
            Click("Create landscape"); Assert.That(root.GetComponent<SolLandscapeGroup>().tiles.Count, Is.EqualTo(4));
            Assert.That(wizard.rootVisualElement.Query<Button>().ToList().Any(b => b.text == "Start sculpting"), Is.True); wizard.Close();
            var cancelled = SolLandscapeCreationWizard.Open(null, 0, (_, _) => { }); cancelled.Settings.scene = scene; cancelled.Settings.name = "Cancelled"; yield return null;
            Activate(cancelled.rootVisualElement.Query<Button>().ToList().Single(b => b.text == "Cancel"));
            Assert.That(AssetDatabase.IsValidFolder(SolLandscapeAssetLocations.SceneRoot(scene, "Cancelled")), Is.False);
        }
        static void Activate(Button button)
        {
            // Route through the real Clickable callback; batch windows do not receive OS focus/navigation events.
            typeof(Clickable).GetMethod("SimulateSingleClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(button.clickable, new object[] { null, 0 });
        }
        [Test] public void Organisation_ReviewedMovesPreserveGuidSharedArtworkAndReverse()
        {
            var group = SolLandscapeCreation.Create(Settings()); var owner = group.assetOwner;
            string originalRoot = SolLandscapeAssetLocations.Root(owner), libraryCS = AssetDatabase.GetAssetPath(group.profile.CSArray), terrainGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(group.tiles[0].terrain.terrainData));
            EditorSceneManager.SaveScene(scene, Folder + "/Renamed.unity");
            Assert.That(SolLandscapeAssetLocations.Root(owner), Is.EqualTo(originalRoot), "Save As must not silently move files.");
            var plan = SolLandscapeOrganisation.Preview(group, true); Assert.That(plan.moves.Count, Is.GreaterThan(0)); Assert.That(plan.retained, Does.Contain(libraryCS));
            SolLandscapeOrganisation.Apply(plan); Assert.That(AssetDatabase.GUIDToAssetPath(terrainGuid), Does.StartWith(plan.destination + "/TerrainData/")); Assert.That(AssetDatabase.GetAssetPath(group.profile.CSArray), Is.EqualTo(libraryCS));
            SolLandscapeOrganisation.ReverseMoves(owner); Assert.That(AssetDatabase.GUIDToAssetPath(terrainGuid), Does.StartWith(originalRoot + "/TerrainData/")); Assert.That(group.Validate(out var reason), Is.True, reason);
        }
        [Test] public void PaletteRepair_UsesCopiesAndPreservesSourceThroughUndoRedo()
        {
            var group = SolLandscapeCreation.Create(Settings()); var tile = group.tiles[0]; var source = tile.terrain.terrainData;
            var layer = new TerrainLayer(); AssetDatabase.CreateAsset(layer, Folder + "/Unmatched.terrainlayer"); source.terrainLayers = new[] { layer };
            Assert.That(group.Validate(out _), Is.False); SolLandscapeAuthoring.RepairTilePalettes(group);
            var repaired = tile.terrain.terrainData; Assert.That(repaired, Is.Not.SameAs(source)); Assert.That(source.terrainLayers, Is.EqualTo(new[] { layer }));
            Assert.That(group.Validate(out string reason), Is.True, reason);
            Assert.That(repaired.GetAlphamaps(0, 0, 1, 1)[0, 0, group.profile.FallbackIndex], Is.EqualTo(1));
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo(); Assert.That(tile.terrain.terrainData, Is.EqualTo(source));
            Undo.PerformRedo(); Assert.That(tile.terrain.terrainData, Is.EqualTo(repaired)); Assert.That(tile.terrain.GetComponent<TerrainCollider>().terrainData, Is.EqualTo(repaired));
        }
        [Test] public void ArtworkBuilds_AreVersionedAndFailureKeepsPreviousPair()
        {
            var group = SolLandscapeCreation.Create(Settings()); var profile = group.profile; profile.artworkResolution = 64;
            SolLandscapeArrayBaker.BakeTarget(profile); var cs = profile.CSArray; var noh = profile.NOHArray;
            string first = AssetDatabase.GetAssetPath(cs); Assert.That(first, Does.Contain("/Generated/Bakes/0001/"));
            SolLandscapeArrayBaker.BakeTarget(profile); Assert.That(AssetDatabase.GetAssetPath(profile.CSArray), Does.Contain("/Generated/Bakes/0002/"));
            Assert.That(AssetDatabase.LoadAssetAtPath<Texture2DArray>(first), Is.EqualTo(cs)); Assert.That(profile.NOHArray, Is.Not.EqualTo(noh));
            cs = profile.CSArray; noh = profile.NOHArray;
            var broken = UnityEngine.Object.Instantiate(profile.Layers[0].terrainLayer); broken.diffuseTexture = null; AssetDatabase.CreateAsset(broken, Folder + "/Broken artwork.terrainlayer"); profile.Layers[0].terrainLayer = broken;
            Assert.Throws<InvalidOperationException>(() => SolLandscapeArrayBaker.BakeTarget(profile)); Assert.That(profile.CSArray, Is.EqualTo(cs)); Assert.That(profile.NOHArray, Is.EqualTo(noh));
        }
        [UnityTest] public IEnumerator DesignerAcceptance_DefaultGridAndEditorBrushHandlers()
        {
            var group=SolLandscapeCreation.Create(new SolLandscapeCreationSettings {scene=scene,name="Designer exercise"});
            var window=ScriptableObject.CreateInstance<ElementaControlPanel>();
            try
            {
                window.Show();window.Context.State.landscapeGroup=group;window.Context.State.landscapeActivity="Sculpt";window.ShowPage("Landscape");
                for(int i=0;i<30&&window.ActivePage?.Title!="Landscape";i++)yield return null;
                void Choose(string tool)
                {
                    window.ActivePage.Root.Query<DropdownField>().ToList().Single(f=>f.label=="Tool").value=tool;
                    window.Context.State.landscapeBrushSize=100;window.Context.State.landscapeBrushStrength=1;window.Context.State.landscapeBrushHardness=.9f;
                }
                void Dab(bool finish=true)
                {
                    Physics.SyncTransforms();var ray=new Ray(new Vector3(512,500,512),Vector3.down);RaycastHit hit=default;
                    var terrain=group.tiles.Select(t=>t.terrain).First(t=>t.GetComponent<TerrainCollider>().Raycast(ray,out hit,1000));
                    var page=(ElementaLandscapePage)window.ActivePage;page.BeginEditorStroke(terrain,hit.point);page.ApplyEditorStroke(terrain,hit.point);if(finish)page.Finish();
                }
                Choose("Raise");Dab();float ridge=group.tiles[0].terrain.terrainData.GetHeight(256,256);Assert.That(ridge,Is.GreaterThan(32.1f));
                float[] corners={ridge,group.tiles[1].terrain.terrainData.GetHeight(0,256),group.tiles[2].terrain.terrainData.GetHeight(256,0),group.tiles[3].terrain.terrainData.GetHeight(0,0)};
                Assert.That(corners.All(h=>Mathf.Abs(h-ridge)<.02f),Is.True);
                Activate(window.ActivePage.Root.Query<Button>().ToList().Single(b=>b.text=="Start painting"));
                for(int i=0;i<30&&!window.ActivePage.Root.Query<Button>().ToList().Any(b=>b.text=="Remove material");i++)yield return null;
                int path=group.profile.Layers.ToList().FindIndex(e=>e.terrainLayer.name.Contains("Path"));window.Context.State.landscapeLayer=path;
                Choose("Paint material");Dab();Assert.That(group.tiles.All(t=>t.paint!=null&&t.paint.HasOverrides),Is.True);
                Choose("Remove material");Dab(false);AssetDatabase.SaveAssets();Assert.That(group.tiles.All(t=>t.paint.HasRemovals),Is.True);
                int[] edge={512*512-1,511*512,511,0};
                for(int i=0;i<4;i++)Assert.That(group.tiles[i].paint.Bytes(4+path/4)[edge[i]*4+path%4],Is.GreaterThan(240));
                Choose("Paint material");Dab();Undo.FlushUndoRecordObjects();Undo.PerformUndo();
                for(int i=0;i<4;i++)Assert.That(group.tiles[i].paint.Bytes(4+path/4)[edge[i]*4+path%4],Is.GreaterThan(240));
                Undo.PerformRedo();for(int i=0;i<4;i++)Assert.That(group.tiles[i].paint.Bytes(4+path/4)[edge[i]*4+path%4],Is.LessThan(15));
                AssetDatabase.SaveAssets();EditorSceneManager.SaveScene(scene);window.Close();scene=EditorSceneManager.OpenScene(Folder+"/Creation.unity",OpenSceneMode.Single);
                group=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<SolLandscapeGroup>()).Single();Assert.That(group.Validate(out var reason),Is.True,reason);
                Assert.That(group.tiles.All(t=>t.paint.HasOverrides&&t.paint.HasRemovals),Is.True);Assert.That(group.assetOwner,Is.Not.Null);
            }
            finally {if(window!=null)window.Close();}
        }
    }
}
