using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Collections;
using NUnit.Framework;
using Sol.Landscape;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Sol.Environment.EditorTools;
using UnityEngine.UIElements;
using UnityEngine.TerrainTools;
using Sol.Landscape.Editor;

namespace Sol.Tests.Editor
{
    public sealed partial class SolLandscapeDesignerTests
    {
        readonly List<UnityEngine.Object> cleanup = new();
        T Keep<T>(T obj) where T:UnityEngine.Object { cleanup.Add(obj);return obj; }
        [TearDown] public void TearDown() { foreach(var obj in cleanup)if(obj is GameObject go && go.TryGetComponent<Camera>(out var camera))camera.targetTexture=null;for(int i=cleanup.Count-1;i>=0;i--)if(cleanup[i]!=null)UnityEngine.Object.DestroyImmediate(cleanup[i]);cleanup.Clear(); }
        SolLandscapeGroup Fixture(int width=2,int height=2,int count=2)
        {
            var profile=Keep(ScriptableObject.CreateInstance<SolLandscapeProfile>());profile.stochasticTiling=profile.triplanarProjection=false;profile.heightBlend=true;
            var entries=new List<SolLandscapeLayerEntry>();
            for(int i=0;i<count;i++)entries.Add(new SolLandscapeLayerEntry(Keep(new TerrainLayer())){materialId="material-"+i,ruleModel=SolLandscapeRuleModel.Ranges,slopeRange=new Vector2(0,90)});
            profile.SetLayers(entries);profile.fallbackMaterialId=entries[0].materialId;
            var cs=Keep(new Texture2DArray(4,4,count,TextureFormat.RGBA32,false,false));var noh=Keep(new Texture2DArray(4,4,count,TextureFormat.RGBA32,false,true));
            for(int i=0;i<count;i++){cs.SetPixels(Enumerable.Repeat(i==0?Color.red:Color.green,16).ToArray(),i);noh.SetPixels(Enumerable.Repeat(new Color(.5f,.5f,1,.5f),16).ToArray(),i);}cs.Apply();noh.Apply();
            profile.RecordBake(null,entries.Select(e=>e.terrainLayer).ToArray(),cs,noh,"",Array.Empty<SolLandscapeBakeFingerprint>(),"",0,0,"");
            var go=Keep(new GameObject("Designer test group"));var group=go.AddComponent<SolLandscapeGroup>();group.profile=profile;group.paintResolution=64;
            for(int z=0;z<height;z++)for(int x=0;x<width;x++)
            {
                var data=Keep(new TerrainData{heightmapResolution=33,alphamapResolution=32,size=new Vector3(64,32,64),terrainLayers=entries.Select(e=>e.terrainLayer).ToArray()});
                var weights=new float[32,32,count];for(int yy=0;yy<32;yy++)for(int xx=0;xx<32;xx++)weights[yy,xx,0]=1;data.SetAlphamaps(0,0,weights);
                var terrainObject=Keep(Terrain.CreateTerrainGameObject(data));terrainObject.transform.position=new Vector3(x*64,0,z*64);
                var paint=Keep(ScriptableObject.CreateInstance<SolLandscapePaintData>());paint.Initialize(64);
                group.tiles.Add(new SolLandscapeTile{terrain=terrainObject.GetComponent<Terrain>(),paint=paint});
            }
            return group;
        }
        [Test] public void ProfilesAndTiles_ValidateOneThroughEightLayers()
        {var group=Fixture(1,1,8);Assert.That(group.Validate(out var reason),Is.True,reason);group.Publish();group.Publish();Assert.That(group.LastPublishWriteCount,Is.Zero);}
        [Test] public void DesignerActivities_BuildNativeControlsAndReleasePreview()
        {
            var group=Fixture(1,1);var window=Keep(ScriptableObject.CreateInstance<ElementaControlPanel>());window.Context.State.landscapeGroup=group;
            var page=window.Pages.Single(p=>p.Id=="landscape");
            foreach(string activity in new[]{"Setup","Sculpt","Materials","Rules","Preview"})
            {
                window.Context.State.landscapeActivity=activity;var root=page.BuildContent(window.Context);
                Assert.That(root.Query<IMGUIContainer>().ToList(),Is.Empty);
                Assert.That(root.Query<HelpBox>().ToList().Where(h=>h.text.StartsWith("Unavailable field:")),Is.Empty,activity);
                var before=root.Query<VisualElement>().ToList();page.Refresh();Assert.That(root.Query<VisualElement>().ToList(),Is.EqualTo(before));
                page.Dispose();Assert.That(group.PreviewSnow,Is.Null);
            }
        }
        [Test] public void MaterialEditing_ExposesPaintAndEraseForAutoLayers()
        {
            var group=Fixture(1,1);Assert.That(group.profile.Layers[1].mode,Is.EqualTo(SolLandscapeLayerMode.Auto));
            var window=Keep(ScriptableObject.CreateInstance<ElementaControlPanel>());window.Context.State.landscapeGroup=group;window.Context.State.landscapeActivity="Materials";
            window.Show();var page=window.Pages.Single(p=>p.Id=="landscape");var root=page.BuildContent(window.Context);window.rootVisualElement.Add(root);
            foreach(string title in new[]{"Paint material","Clear painted overrides","Exclude material","Restore excluded"})
                Assert.That(root.Query<Button>().ToList().Any(b=>b.text==title && b.enabledInHierarchy),Is.True,title);
            var tool=root.Query<DropdownField>().ToList().Single(f=>f.label=="Tool");tool.value="Paint material";Assert.That(window.Context.State.landscapeTool,Is.EqualTo(1));
            tool.value="Clear painted overrides";page.Refresh();Assert.That(window.Context.State.landscapeTool,Is.EqualTo(2));
            Assert.That(root.Query<HelpBox>().ToList().Any(h=>h.text.Contains("remove local overrides")),Is.True);
            group.profile.Layers[window.Context.State.landscapeLayer].terrainLayer.name="Stone2";page.Refresh();
            Assert.That(root.Query<Button>().ToList().Single(b=>b.text=="Paint material").enabledSelf,Is.False);
            Assert.That(root.Query<Button>().ToList().Single(b=>b.text=="Clear painted overrides").enabledSelf,Is.True);page.Dispose();
        }
        [Test] public void PerTileBindings_AreIndependentAndPreserveUnrelatedProperties()
        {
            var group=Fixture();var block=new MaterialPropertyBlock();block.SetFloat("_DesignerUnrelated",.73f);group.tiles[0].terrain.SetSplatMaterialPropertyBlock(block);
            group.Publish();group.tiles[0].terrain.GetSplatMaterialPropertyBlock(block);Assert.That(block.GetFloat("_DesignerUnrelated"),Is.EqualTo(.73f));
            var a=block.GetTexture("_Sol_LandscapeControl0");group.tiles[1].terrain.GetSplatMaterialPropertyBlock(block);Assert.That(block.GetTexture("_Sol_LandscapeControl0"),Is.Not.SameAs(a));
            Assert.That(block.GetVector("_Sol_LandscapeTerrainOriginSize").x,Is.EqualTo(64));
        }
        [Test] public void IndependentGroups_RetainTheirOwnPalettesAndPreview()
        {
            var first=Fixture(1,1,2);var second=Fixture(1,1,8);first.SetPreview(this,.7f,.9f,1,1);
            first.Publish();second.Publish();var block=new MaterialPropertyBlock();first.tiles[0].terrain.GetSplatMaterialPropertyBlock(block);var a=block.GetTexture("_Sol_LandscapeCS");
            second.tiles[0].terrain.GetSplatMaterialPropertyBlock(block);Assert.That(block.GetTexture("_Sol_LandscapeCS"),Is.Not.SameAs(a));Assert.That(block.GetVector("_Sol_LandscapeWetPreview").x,Is.Zero);
            second.Publish();first.Publish();first.tiles[0].terrain.GetSplatMaterialPropertyBlock(block);Assert.That(block.GetTexture("_Sol_LandscapeCS"),Is.SameAs(a));Assert.That(block.GetVector("_Sol_LandscapeWetPreview").y,Is.EqualTo(.9f));
        }
        [Test] public void LegacySharedMaterial_BindsFollowersAndRestoresUnrelatedProperties()
        {
            var fixture=Fixture(2,1);fixture.enabled=false;
            var primary=fixture.tiles[0].terrain;var follower=fixture.tiles[1].terrain;
            var material=Keep(new Material(Shader.Find("Sol/Terrain/Array Lit")));primary.materialTemplate=follower.materialTemplate=material;
            var block=new MaterialPropertyBlock();block.SetFloat("_UnrelatedLegacy",.42f);follower.SetSplatMaterialPropertyBlock(block);
            var driver=Keep(new GameObject("Legacy shared driver")).AddComponent<SolLandscapeDriver>();driver.landscapeTerrain=primary;driver.config=fixture.profile;
            driver.Publish();Assert.That(driver.LegacyRecipientCount,Is.EqualTo(1));
            follower.GetSplatMaterialPropertyBlock(block);Assert.That(block.GetTexture("_Sol_LandscapeControl0"),Is.SameAs(primary.terrainData.GetAlphamapTexture(0)));
            Assert.That(block.GetVector("_Sol_LandscapeTerrainOriginSize").x,Is.EqualTo(primary.transform.position.x));
            driver.Publish();Assert.That(driver.LastPublishWriteCount,Is.Zero);
            block.SetFloat("_UnrelatedLegacy",.84f);follower.SetSplatMaterialPropertyBlock(block);
            var group=Keep(new GameObject("Adopt follower")).AddComponent<SolLandscapeGroup>();group.profile=fixture.profile;group.tiles.Add(fixture.tiles[1]);group.paintResolution=64;group.Publish();
            driver.Publish();Assert.That(driver.LegacyRecipientCount,Is.Zero);
            follower.GetSplatMaterialPropertyBlock(block);Assert.That(block.GetTexture("_Sol_LandscapeControl0"),Is.SameAs(follower.terrainData.GetAlphamapTexture(0)));
            Assert.That(block.GetFloat("_UnrelatedLegacy"),Is.EqualTo(.84f));
            group.enabled=false;driver.Publish();Assert.That(driver.LegacyRecipientCount,Is.EqualTo(1));driver.enabled=false;
            follower.GetSplatMaterialPropertyBlock(block);Assert.That(block.GetFloat("_UnrelatedLegacy"),Is.EqualTo(.84f));
        }
        [Test] public void LegacyElementaDemo_AllNineTerrainsReceiveTheExistingMaterialContract()
        {
            var scene=EditorSceneManager.OpenScene("Assets/Scenes/Elementa_Demo.unity",OpenSceneMode.Additive);
            try
            {
                var terrains=scene.GetRootGameObjects().SelectMany(go=>go.GetComponentsInChildren<Terrain>(true)).ToArray();Assert.That(terrains.Length,Is.EqualTo(9));
                var driver=scene.GetRootGameObjects().SelectMany(go=>go.GetComponentsInChildren<SolLandscapeDriver>(true)).Single();
                var data=terrains.Select(t=>t.terrainData).ToArray();var materials=terrains.Select(t=>t.materialTemplate).ToArray();
                driver.Publish();Assert.That(driver.LastPublishRefused,Is.False,driver.LastRefusalReason);Assert.That(driver.LegacyRecipientCount,Is.EqualTo(8));
                var block=new MaterialPropertyBlock();
                for(int i=0;i<terrains.Length;i++)
                {
                    terrains[i].GetSplatMaterialPropertyBlock(block);
                    Assert.That(block.GetTexture("_Sol_LandscapeCS"),Is.SameAs(driver.config.CSArray),terrains[i].name);
                    Assert.That(block.GetTexture("_Sol_LandscapeNOH"),Is.SameAs(driver.config.NOHArray),terrains[i].name);
                    Assert.That(block.GetTexture("_Sol_LandscapeControl0"),Is.SameAs(driver.landscapeTerrain.terrainData.GetAlphamapTexture(0)),terrains[i].name);
                    Assert.That(terrains[i].terrainData,Is.SameAs(data[i]));Assert.That(terrains[i].materialTemplate,Is.SameAs(materials[i]));
                }
                driver.Publish();Assert.That(driver.LastPublishWriteCount,Is.Zero);
                var bounds=new Bounds(terrains[0].transform.position,Vector3.zero);foreach(var terrain in terrains){bounds.Encapsulate(terrain.transform.position);bounds.Encapsulate(terrain.transform.position+terrain.terrainData.size);}
                var camera=Keep(new GameObject("Legacy nine tile validation camera")).AddComponent<Camera>();camera.orthographic=true;camera.orthographicSize=bounds.size.z*.55f;camera.aspect=1.6f;camera.farClipPlane=bounds.size.y+10000;
                camera.transform.position=new Vector3(bounds.center.x,bounds.max.y+3000,bounds.center.z);camera.transform.rotation=Quaternion.Euler(90,0,0);
                SolLandscapeValidation.Capture(camera,Path.GetFullPath("../landscape-legacy-nine-tiles.png"));
            }
            finally {EditorSceneManager.CloseScene(scene,true);}
        }
        [Test] public void PaintingBug_EmptyOperationsDoNotAllocateOrNotify()
        {
            var group=Fixture(1,1);var tile=group.tiles[0];tile.paint=null;
            using var stroke=new SolLandscapeStroke();int touched=0;stroke.BeforeTileChange+=_=>touched++;
            foreach(var operation in new[]{SolLandscapePaintOperation.EraseToAutomatic,SolLandscapePaintOperation.RestoreExcluded})
            {stroke.BeginStroke(group,operation,1);stroke.ApplyDab(new Vector3(32,0,32),10,1);stroke.CommitStroke();}
            stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,1);
            stroke.ApplyDab(new Vector3(32,0,32),10,0);
            stroke.ApplyDab(new Vector3(-8,0,-8),10,1); // Bounding boxes overlap, brush circle does not.
            stroke.CommitStroke();Assert.That(tile.paint,Is.Null);Assert.That(touched,Is.Zero);
        }
        [Test] public void PaintingBug_SharedPaintAssetIsRejected()
        {
            var group=Fixture(2,1);group.tiles[1].paint=group.tiles[0].paint;
            Assert.That(group.Validate(out var reason),Is.False);Assert.That(reason,Does.Contain("paint"));
        }
        [Test] public void PaintingBug_ConcurrentStrokesCannotOverwriteCancellation()
        {
            var group=Fixture(1,1);using var first=new SolLandscapeStroke();using var second=new SolLandscapeStroke();
            first.BeginStroke(group,SolLandscapePaintOperation.Paint,1);first.ApplyDab(new Vector3(32,0,32),10,1);
            Assert.Throws<InvalidOperationException>(()=>second.BeginStroke(group,SolLandscapePaintOperation.Paint,0));
            first.CancelStroke();second.BeginStroke(group,SolLandscapePaintOperation.Paint,0);second.CommitStroke();
        }
        [Test] public void PaintingBug_CancelImmediatelyRebindsRestoredTextures()
        {
            var group=Fixture(1,1);var data=group.tiles[0].paint;using var stroke=new SolLandscapeStroke();
            stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,1);stroke.ApplyDab(new Vector3(32,0,32),10,1);stroke.CommitStroke();
            stroke.BeginStroke(group,SolLandscapePaintOperation.EraseToAutomatic,1);stroke.ApplyDab(new Vector3(32,0,32),10,1);stroke.CancelStroke();
            var block=new MaterialPropertyBlock();group.tiles[0].terrain.GetSplatMaterialPropertyBlock(block);
            Assert.That(block.GetTexture("_Sol_LandscapePaint0"),Is.SameAs(data.GetTexture(0)));
        }
        [Test] public void PaintingBug_EighthChannelAndExclusionsSurviveReloadAndRestore()
        {
            var group=Fixture(2,1,8);using var stroke=new SolLandscapeStroke();
            stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,7);stroke.ApplyDab(new Vector3(64,0,32),12,1,.9f);stroke.CommitStroke();
            stroke.BeginStroke(group,SolLandscapePaintOperation.Exclude,6);stroke.ApplyDab(new Vector3(64,0,32),12,1,.9f);stroke.CommitStroke();
            foreach(var tile in group.tiles)
            {
                var copy=Keep(ScriptableObject.CreateInstance<SolLandscapePaintData>());JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(tile.paint),copy);tile.paint=copy;
                int x=tile.terrain.transform.position.x==0?63:0,p=(32*64+x)*4;
                Assert.That(copy.Bytes(1)[p+3],Is.EqualTo(255));Assert.That(copy.Bytes(3)[p+2],Is.EqualTo(255));Assert.That(copy.Bytes(0).All(b=>b==0),Is.True);
            }
            stroke.BeginStroke(group,SolLandscapePaintOperation.RestoreExcluded,6);stroke.ApplyDab(new Vector3(64,0,32),12,1,.9f);stroke.CommitStroke();
            foreach(var tile in group.tiles){int x=tile.terrain.transform.position.x==0?63:0,p=(32*64+x)*4;Assert.That(tile.paint.Bytes(3)[p+2],Is.Zero);Assert.That(tile.paint.Bytes(1)[p+3],Is.EqualTo(255));}
        }
        [Test] public void PaintingBug_UndoDuringActiveEditorStrokeClosesTransaction()
        {
            var group=Fixture(1,1);var window=Keep(ScriptableObject.CreateInstance<ElementaControlPanel>());window.Context.State.landscapeGroup=group;
            var page=window.Pages.Single(p=>p.Id=="landscape");page.BuildContent(window.Context);
            Undo.IncrementCurrentGroup();int undo=Undo.GetCurrentGroup();Undo.RegisterCompleteObjectUndo(group.tiles[0].paint,"Active stroke");
            var stroke=new SolLandscapeStroke();stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,1);stroke.ApplyDab(new Vector3(32,0,32),12,1,.9f);
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            page.GetType().GetField("stroke",flags).SetValue(page,stroke);page.GetType().GetField("undoGroup",flags).SetValue(page,undo);
            try {Undo.PerformUndo();Assert.That(stroke.IsActive,Is.False);Assert.That(group.tiles[0].paint.HasOverrides,Is.False);}
            finally {page.Dispose();stroke.Dispose();}
        }
        [Test] public void PaintingBug_AssetSaveCommitsActiveStrokeAndNativeReloadRetainsPixels()
        {
            var group=Fixture(2,1);var window=Keep(ScriptableObject.CreateInstance<ElementaControlPanel>());window.Context.State.landscapeGroup=group;
            var page=window.Pages.Single(p=>p.Id=="landscape");page.BuildContent(window.Context);
            var stroke=new SolLandscapeStroke();stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,1);stroke.ApplyDab(new Vector3(64,0,32),12,1,.9f);
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;page.GetType().GetField("stroke",flags).SetValue(page,stroke);
            try
            {
                SolLandscapeLifecycle.FlushForAssetSave(Array.Empty<string>());Assert.That(stroke.IsActive,Is.False);
                string path=Path.GetFullPath("../landscape-painting-native-roundtrip.asset");
                UnityEditorInternal.InternalEditorUtility.SaveToSerializedFileAndForget(group.tiles.Select(t=>(UnityEngine.Object)t.paint).ToArray(),path,true);
                var loaded=UnityEditorInternal.InternalEditorUtility.LoadSerializedFileAndForget(path);
                Assert.That(loaded.Length,Is.EqualTo(2));
                for(int i=0;i<2;i++){var paint=Keep((SolLandscapePaintData)loaded[i]);int p=(32*64+(i==0?63:0))*4;Assert.That(paint.Bytes(0)[p+1],Is.EqualTo(255));Assert.That(paint.Bytes(0),Is.EqualTo(group.tiles[i].paint.Bytes(0)));}
            }
            finally{page.Dispose();stroke.Dispose();}
        }
        [Test] public void PaintingBug_LostGpuTextureRestoresCommittedData()
        {
            var group=Fixture(1,1);using var stroke=new SolLandscapeStroke();stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,1);stroke.ApplyDab(new Vector3(32,0,32),12,1,.9f);stroke.CommitStroke();
            var paint=group.tiles[0].paint;paint.GetWorking(0).Release();var texture=paint.GetWorking(0);Assert.That(texture.IsCreated(),Is.True);
            paint.CommitRegion(0,new RectInt(32,32,1,1));Assert.That(paint.Bytes(0)[(32*64+32)*4+1],Is.EqualTo(255));
        }
        [Test] public void PaintingBug_MultipleDabsKeepEveryBorderSampleContinuous()
        {
            var group=Fixture();using var stroke=new SolLandscapeStroke();stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,1);
            for(int i=0;i<31;i++)stroke.ApplyDab(new Vector3(48+i,0,49+i),9,.27f,.25f,null,37);stroke.CommitStroke();
            for(int i=0;i<64;i++)for(int c=0;c<4;c++)
            {
                Assert.That(group.tiles[0].paint.Bytes(0)[(i*64+63)*4+c],Is.EqualTo(group.tiles[1].paint.Bytes(0)[i*64*4+c]).Within(1));
                Assert.That(group.tiles[2].paint.Bytes(0)[(i*64+63)*4+c],Is.EqualTo(group.tiles[3].paint.Bytes(0)[i*64*4+c]).Within(1));
                Assert.That(group.tiles[0].paint.Bytes(0)[(63*64+i)*4+c],Is.EqualTo(group.tiles[2].paint.Bytes(0)[i*4+c]).Within(1));
                Assert.That(group.tiles[1].paint.Bytes(0)[(63*64+i)*4+c],Is.EqualTo(group.tiles[3].paint.Bytes(0)[i*4+c]).Within(1));
            }
        }
        [Test] public void PaintingBug_PaletteReorderRemapsEveryGroupSharingProfile()
        {
            var first=Fixture(1,1);var second=Fixture(1,1);second.profile=first.profile;
            second.tiles[0].terrain.terrainData.terrainLayers=first.tiles[0].terrain.terrainData.terrainLayers;
            first.profile.RecordPaletteBake();using var stroke=new SolLandscapeStroke();
            foreach(var group in new[]{first,second}){stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,1);stroke.ApplyDab(new Vector3(32,0,32),12,1,.9f);stroke.CommitStroke();}
            SolLandscapeAuthoring.ChangePalette(first,first.profile.Layers.Reverse().ToList());
            foreach(var group in new[]{first,second})
            {Assert.That(group.Validate(out var reason),Is.True,reason);Assert.That(group.tiles[0].paint.Bytes(0)[(32*64+32)*4],Is.EqualTo(255));}
            Undo.FlushUndoRecordObjects();Undo.PerformUndo();
            foreach(var group in new[]{first,second}){Assert.That(group.Validate(out var reason),Is.True,reason);Assert.That(group.tiles[0].paint.Bytes(0)[(32*64+32)*4+1],Is.EqualTo(255));}
            Undo.PerformRedo();
            foreach(var group in new[]{first,second}){Assert.That(group.Validate(out var reason),Is.True,reason);Assert.That(group.tiles[0].paint.Bytes(0)[(32*64+32)*4],Is.EqualTo(255));}
        }
        [Test] public void PaintingBug_SaveFlushDoesNotSerializeTransientMaterial()
        {
            var group=Fixture(1,1);var terrain=group.tiles[0].terrain;var original=terrain.materialTemplate;
            var window=Keep(ScriptableObject.CreateInstance<ElementaControlPanel>());window.Context.State.landscapeGroup=group;
            var page=window.Pages.Single(p=>p.Id=="landscape");page.BuildContent(window.Context);
            var stroke=new SolLandscapeStroke();stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,1);stroke.ApplyDab(new Vector3(32,0,32),12,1,.9f);
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;page.GetType().GetField("stroke",flags).SetValue(page,stroke);
            try
            {
                typeof(SolLandscapeLifecycle).GetMethod("BeforeSave",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)
                    .Invoke(null,new object[]{group.gameObject.scene,"Unused.unity"});
                page.GetType().GetMethod("OnSaving",flags).Invoke(page,new object[]{group.gameObject.scene,"Unused.unity"});
                Assert.That(stroke.IsActive,Is.False);Assert.That(terrain.materialTemplate,Is.SameAs(original));
                Assert.That(group.tiles[0].paint.Bytes(0)[(32*64+32)*4+1],Is.EqualTo(255));
            }
            finally{page.Dispose();stroke.Dispose();}
        }
        [Test] public void Stroke_PaintsAcrossFourTileJunction_AndEraseRestoresAutomatic()
        {
            var group=Fixture();using var stroke=new SolLandscapeStroke();stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,1);stroke.ApplyDab(new Vector3(64,0,64),12,1,.9f);stroke.CommitStroke();
            int[][] corner={new[]{63,63},new[]{0,63},new[]{63,0},new[]{0,0}};
            for(int i=0;i<4;i++){int pixel=(corner[i][1]*64+corner[i][0])*4;Assert.That(group.tiles[i].paint.Bytes(0)[pixel+1],Is.GreaterThan(250));Assert.That(group.tiles[i].paint.HasExclusions,Is.False);}
            stroke.BeginStroke(group,SolLandscapePaintOperation.EraseToAutomatic,1);stroke.ApplyDab(new Vector3(64,0,64),12,1,.9f);stroke.CommitStroke();
            for(int i=0;i<4;i++){int pixel=(corner[i][1]*64+corner[i][0])*4;Assert.That(group.tiles[i].paint.Bytes(0)[pixel+1],Is.LessThan(3));}
        }
        [Test] public void Cancel_RestoresUnallocatedMaps_AndExclusionDoesNotChangePaint()
        {
            var group=Fixture(1,1);var data=group.tiles[0].paint;
            using var stroke=new SolLandscapeStroke();stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,1);stroke.ApplyDab(new Vector3(32,0,32),10,1);stroke.CancelStroke();Assert.That(data.HasOverrides,Is.False);
            Assert.Throws<InvalidOperationException>(()=>stroke.BeginStroke(group,SolLandscapePaintOperation.Exclude,0));
            stroke.BeginStroke(group,SolLandscapePaintOperation.Exclude,1);stroke.ApplyDab(new Vector3(32,0,32),10,1);stroke.CommitStroke();Assert.That(data.HasOverrides,Is.False);Assert.That(data.HasExclusions,Is.True);
        }
        [Test] public void Paint_RemapPreservesMaterialIdentity_AndRemovalRevealsBase()
        {
            var group=Fixture(1,1);using var stroke=new SolLandscapeStroke();stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,1);stroke.ApplyDab(new Vector3(32,0,32),10,1,.9f);stroke.CommitStroke();
            var data=group.tiles[0].paint;int pixel=(32*64+32)*4;data.Remap(new[]{1,0});Assert.That(data.Bytes(0)[pixel],Is.GreaterThan(250));
            data.Remap(new[]{1});Assert.That(data.Bytes(0)[pixel],Is.Zero);
        }
        [Test] public void Preview_IsOwnerScoped_AndDoesNotWriteWorldWetness()
        {
            var group=Fixture(1,1);float original=Shader.GetGlobalFloat("_Sol_SurfaceWetness");var owner=new object();
            Assert.That(group.SetPreview(owner,.5f,.8f,1,0),Is.True);Assert.That(group.SetPreview(new object(),1,1,0,0),Is.False);group.Publish();
            Assert.That(Shader.GetGlobalFloat("_Sol_SurfaceWetness"),Is.EqualTo(original));group.ClearPreview(new object());Assert.That(group.PreviewSnow,Is.EqualTo(.5f));group.ClearPreview(owner);Assert.That(group.PreviewSnow,Is.Null);
        }
        [Test] public void Rules_HaveExplicitFeathersAndPreservedEndpoints()
        {Assert.That(SolLandscapeProfile.RangeResponse(20,new Vector2(20,40),10),Is.EqualTo(1));Assert.That(SolLandscapeProfile.RangeResponse(40,new Vector2(20,40),10),Is.EqualTo(1));Assert.That(SolLandscapeProfile.RangeResponse(10,new Vector2(20,40),10),Is.Zero);Assert.That(SolLandscapeProfile.RangeResponse(50,new Vector2(20,40),10),Is.Zero);}
        [Test] public void Paint_SerializesAndUndoRestoresBothTiles()
        {
            var group=Fixture(2,1);using var stroke=new SolLandscapeStroke();
            Undo.IncrementCurrentGroup();int undo=Undo.GetCurrentGroup();
            foreach(var tile in group.tiles)Undo.RegisterCompleteObjectUndo(tile.paint,"Test landscape stroke");
            stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,1);stroke.ApplyDab(new Vector3(64,0,32),10,1,.9f);stroke.CommitStroke();Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(undo);
            string path=Path.GetFullPath("../landscape-paint-roundtrip.json");File.WriteAllText(path,JsonUtility.ToJson(group.tiles[0].paint));
            var reloaded=Keep(ScriptableObject.CreateInstance<SolLandscapePaintData>());JsonUtility.FromJsonOverwrite(File.ReadAllText(path),reloaded);
            Assert.That(reloaded.Bytes(0),Is.EqualTo(group.tiles[0].paint.Bytes(0)));Assert.That(reloaded.Resolution,Is.EqualTo(64));
            Undo.PerformUndo();foreach(var tile in group.tiles)Assert.That(tile.paint.HasOverrides,Is.False);
            Undo.PerformRedo();foreach(var tile in group.tiles)Assert.That(tile.paint.HasOverrides,Is.True);
        }
        [Test] public void BindingRelease_PreservesPropertiesWrittenAfterPublication()
        {
            var group=Fixture(1,1);group.Publish();var terrain=group.tiles[0].terrain;var block=new MaterialPropertyBlock();terrain.GetSplatMaterialPropertyBlock(block);
            block.SetFloat("_AnotherSystem",.9f);terrain.SetSplatMaterialPropertyBlock(block);group.enabled=false;terrain.GetSplatMaterialPropertyBlock(block);Assert.That(block.GetFloat("_AnotherSystem"),Is.EqualTo(.9f));
        }
        [Test] public void Sculpt_PaintContextPreservesFourTileBordersAndUndo()
        {
            var group=Fixture();SolLandscapeAuthoring.Connect(group);
            Undo.IncrementCurrentGroup();int undo=Undo.GetCurrentGroup();foreach(var tile in group.tiles)Undo.RegisterCompleteObjectUndo(tile.terrain.terrainData,"Sculpt fixture");
            var terrain=group.tiles[0].terrain;var transform=TerrainPaintUtility.CalculateBrushTransform(terrain,Vector2.one,24,0);
            var paint=TerrainPaintUtility.BeginPaintHeightmap(terrain,transform.GetBrushXYBounds(),1);
            var material=Keep(new Material(Shader.Find("Hidden/Sol/Landscape Sculpt")));TerrainPaintUtility.SetupTerrainToolMaterialProperties(paint,transform,material);
            material.SetTexture("_BrushTex",Texture2D.whiteTexture);material.SetVector("_Sculpt",new Vector4(.1f,0,0,.9f));
            Graphics.Blit(paint.sourceRenderTexture,paint.destinationRenderTexture,material);TerrainPaintUtility.EndPaintHeightmap(paint,"Sculpt fixture");PaintContext.ApplyDelayedActions();
            float[] corners={group.tiles[0].terrain.terrainData.GetHeight(32,32),group.tiles[1].terrain.terrainData.GetHeight(0,32),group.tiles[2].terrain.terrainData.GetHeight(32,0),group.tiles[3].terrain.terrainData.GetHeight(0,0)};
            Assert.That(corners[0],Is.GreaterThan(1));foreach(float corner in corners)Assert.That(corner,Is.EqualTo(corners[0]).Within(.01f));
            Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(undo);Undo.PerformUndo();Assert.That(terrain.terrainData.GetHeight(32,32),Is.Zero.Within(.01f));
            Undo.PerformRedo();Assert.That(terrain.terrainData.GetHeight(32,32),Is.EqualTo(corners[0]).Within(.01f));
        }
        [Test] public void PaletteReorder_PreservesBakedSlicePaintAndAlphamaps()
        {
            var group=Fixture(1,1);group.profile.RecordPaletteBake();using var stroke=new SolLandscapeStroke();
            stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,1);stroke.ApplyDab(new Vector3(32,0,32),12,1);stroke.CommitStroke();
            SolLandscapeAuthoring.ChangePalette(group,group.profile.Layers.Reverse().ToList());
            Assert.That(group.profile.SliceFor(0),Is.EqualTo(1));Assert.That(group.profile.FallbackIndex,Is.EqualTo(1));
            Assert.That(group.tiles[0].paint.Bytes(0)[(32*64+32)*4],Is.GreaterThan(250));
            Assert.That(group.tiles[0].terrain.terrainData.GetAlphamaps(16,16,1,1)[0,0,1],Is.EqualTo(1));
        }
        [Test] public void GpuFinalWeights_RespectOverrideEraseAndExclusion()
        {
            var group=Fixture(1,1);group.SetPreview(this,null,null,1,1);group.Publish();
            var camera=Keep(new GameObject("Weight camera")).AddComponent<Camera>();camera.transform.position=new Vector3(32,60,32);camera.transform.rotation=Quaternion.Euler(90,0,0);camera.orthographic=true;camera.orthographicSize=32;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.magenta;
            var rt=Keep(new RenderTexture(64,64,24));camera.targetTexture=rt;var pixels=Keep(new Texture2D(64,64,TextureFormat.RGBA32,false,true));
            float Read(){group.Publish();camera.Render();var previous=RenderTexture.active;RenderTexture.active=rt;pixels.ReadPixels(new Rect(0,0,64,64),0,0);RenderTexture.active=previous;return pixels.GetPixel(32,32).g;}
            float baseline=Read();Assert.That(baseline,Is.InRange(.1f,.9f));
            using var stroke=new SolLandscapeStroke();stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,1);stroke.ApplyDab(new Vector3(32,0,32),12,1,.9f);stroke.CommitStroke();Assert.That(Read(),Is.GreaterThan(.95f));
            stroke.BeginStroke(group,SolLandscapePaintOperation.EraseToAutomatic,1);stroke.ApplyDab(new Vector3(32,0,32),12,1,.9f);stroke.CommitStroke();Assert.That(Read(),Is.EqualTo(baseline).Within(.02f));
            stroke.BeginStroke(group,SolLandscapePaintOperation.Exclude,1);stroke.ApplyDab(new Vector3(32,0,32),12,1,.9f);stroke.CommitStroke();Assert.That(Read(),Is.LessThan(.02f));
        }
        [Test] public void AutomaticRockProtection_DefaultsAndBrushGuards()
        {
            var group=Fixture(1,1);var rock=group.profile.Layers[1];rock.terrainLayer.name="Stone2";
            Assert.That(rock.ProtectAutomaticCoverage,Is.True);
            using var stroke=new SolLandscapeStroke();
            Assert.Throws<InvalidOperationException>(()=>stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,1));
            Assert.Throws<InvalidOperationException>(()=>stroke.BeginStroke(group,SolLandscapePaintOperation.Exclude,1));
            stroke.BeginStroke(group,SolLandscapePaintOperation.EraseToAutomatic,1);stroke.CommitStroke();
            rock.paintProtection=SolLandscapePaintProtection.Off;Assert.That(rock.ProtectAutomaticCoverage,Is.False);
            rock.paintProtection=SolLandscapePaintProtection.On;rock.mode=SolLandscapeLayerMode.Manual;Assert.That(rock.ProtectAutomaticCoverage,Is.False);
            rock.mode=SolLandscapeLayerMode.Auto;rock.terrainLayer.name="Custom material";Assert.That(rock.ProtectAutomaticCoverage,Is.True);
            rock.paintProtection=SolLandscapePaintProtection.RockMaterials;Assert.That(rock.ProtectAutomaticCoverage,Is.False);
        }
        [TestCase(false),TestCase(true)] public void AutomaticRockProtection_KeepsSlopesWhileGrassFillsRemainingCoverage(bool heightBlend)
        {
            var group=Fixture(1,1,3);group.profile.heightBlend=heightBlend;var rock=group.profile.Layers[2];rock.terrainLayer.name="Stone2";rock.slopeRange=new Vector2(35,90);rock.slopeFeather=5;
            var heights=new float[33,33];for(int z=0;z<33;z++)for(int x=0;x<33;x++)heights[z,x]=Mathf.Max(0,x-16)/16f*.95f;
            group.tiles[0].terrain.terrainData.SetHeights(0,0,heights);
            var camera=Keep(new GameObject("Protected rock camera")).AddComponent<Camera>();camera.transform.position=new Vector3(32,80,32);camera.transform.rotation=Quaternion.Euler(90,0,0);camera.orthographic=true;camera.orthographicSize=32;
            var rt=Keep(new RenderTexture(64,64,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear));camera.targetTexture=rt;
            var pixels=Keep(new Texture2D(64,64,TextureFormat.RGBA32,false,true));
            float Read(int material,int x){group.SetPreview(this,null,null,1,material);group.Publish();camera.Render();var previous=RenderTexture.active;RenderTexture.active=rt;pixels.ReadPixels(new Rect(0,0,64,64),0,0);RenderTexture.active=previous;return pixels.GetPixel(x,32).g;}
            float before=Read(2,48);Assert.That(before,Is.GreaterThan(.15f));Assert.That(Read(2,12),Is.LessThan(.02f));
            using var stroke=new SolLandscapeStroke();stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,1);stroke.ApplyDab(new Vector3(32,0,32),100,1,.99f);stroke.CommitStroke();
            Assert.That(Read(2,48),Is.EqualTo(before).Within(.02f));Assert.That(Read(1,12),Is.GreaterThan(.95f));Assert.That(Read(0,48),Is.LessThan(.02f));
            Assert.That(Read(1,48)+Read(2,48),Is.EqualTo(1).Within(.025f));
            group.tiles[0].terrain.terrainData.SetHeights(0,0,new float[33,33]);
            Assert.That(Read(2,48),Is.LessThan(.02f));Assert.That(Read(1,48),Is.GreaterThan(.95f));
            rock.slopeRange=new Vector2(0,90);Assert.That(Read(2,48),Is.GreaterThan(.15f));
            rock.paintProtection=SolLandscapePaintProtection.Off;
            stroke.BeginStroke(group,SolLandscapePaintOperation.Exclude,2);stroke.ApplyDab(new Vector3(32,0,32),100,1,.99f);stroke.CommitStroke();Assert.That(Read(2,48),Is.LessThan(.02f));
            rock.paintProtection=SolLandscapePaintProtection.On;Assert.That(Read(2,48),Is.GreaterThan(.15f),"Existing exclusions cannot suppress newly protected rock.");
            rock.paintProtection=SolLandscapePaintProtection.Off;
            stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,2);stroke.ApplyDab(new Vector3(32,0,32),100,1,.99f);stroke.CommitStroke();Assert.That(Read(2,48),Is.GreaterThan(.95f));
            rock.paintProtection=SolLandscapePaintProtection.On;Assert.That(Read(2,48),Is.LessThan(.8f),"Existing rock overrides cannot replace the automatic distribution after locking.");
        }
        [UnityTest] public IEnumerator RuntimeStroke_OperatesOnClonedData()
        {
            yield return new EnterPlayMode();
            var group=Fixture(1,1);var source=group.tiles[0].terrain.terrainData;
            var clone=Keep(UnityEngine.Object.Instantiate(source));group.tiles[0].terrain.terrainData=clone;group.tiles[0].terrain.GetComponent<TerrainCollider>().terrainData=clone;
            using(var stroke=new SolLandscapeStroke()){stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,1);stroke.ApplyDab(new Vector3(32,0,32),8,1);stroke.CommitStroke();}
            Assert.That(group.tiles[0].paint.HasOverrides,Is.True);Assert.That(source.GetAlphamaps(16,16,1,1)[0,0,0],Is.EqualTo(1));
            TearDown();yield return new ExitPlayMode();
        }
        [Test] public void Shaders_CompileAndRenderRequiredVariants()
        {
            var group=Fixture(1,1,8);var cameraObject=Keep(new GameObject("Landscape validation camera"));var camera=cameraObject.AddComponent<Camera>();
            camera.transform.position=new Vector3(32,60,32);camera.transform.rotation=Quaternion.Euler(90,0,0);camera.orthographic=true;camera.orthographicSize=32;
            var target=Keep(new RenderTexture(128,128,24));camera.targetTexture=target;
            foreach(bool stochastic in new[]{false,true})foreach(bool triplanar in new[]{false,true})
            {
                group.profile.stochasticTiling=stochastic;group.profile.triplanarProjection=triplanar;foreach(var e in group.profile.Layers){e.stochasticTiling=stochastic;e.triplanarProjection=triplanar;}group.Publish();
                foreach(bool instanced in new[]{false,true}){group.tiles[0].terrain.drawInstanced=instanced;camera.Render();}
                var material=group.tiles[0].terrain.materialTemplate;for(int pass=0;pass<material.passCount;pass++)ShaderUtil.CompilePass(material,pass,true);
            }
            foreach(string name in new[]{"Sol/Terrain/Array Lit","Hidden/Sol/Terrain/Array Basemap Gen","Hidden/Sol/Landscape Paint","Hidden/Sol/Landscape Sculpt"})
            {var shader=Shader.Find(name);Assert.That(shader,Is.Not.Null,name);Assert.That(ShaderUtil.ShaderHasError(shader),Is.False,string.Join("\n",ShaderUtil.GetShaderMessages(shader).Select(m=>m.message)));}
        }
        [Test] public void TerrainHoles_ClipTheRenderedSurface()
        {
            var group=Fixture(1,1);group.SetPreview(this,null,null,1,1);group.Publish();
            var terrain=group.tiles[0].terrain;terrain.drawInstanced=true;
            var camera=Keep(new GameObject("Hole camera")).AddComponent<Camera>();camera.transform.position=new Vector3(32,60,32);camera.transform.rotation=Quaternion.Euler(90,0,0);camera.orthographic=true;camera.orthographicSize=32;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.magenta;
            var rt=Keep(new RenderTexture(64,64,24));camera.targetTexture=rt;var pixels=Keep(new Texture2D(64,64,TextureFormat.RGBA32,false,true));
            Color Read(){camera.Render();var previous=RenderTexture.active;RenderTexture.active=rt;pixels.ReadPixels(new Rect(0,0,64,64),0,0);RenderTexture.active=previous;return pixels.GetPixel(32,32);}
            Assert.That(Read().g,Is.GreaterThan(.1f));var holes=new bool[32,32];for(int y=0;y<32;y++)for(int x=0;x<32;x++)holes[y,x]=!(x>=14&&x<=18&&y>=14&&y<=18);
            terrain.terrainData.SetHoles(0,0,holes);var pixel=Read();Assert.That(pixel.r,Is.GreaterThan(.95f));Assert.That(pixel.b,Is.GreaterThan(.95f));Assert.That(pixel.g,Is.LessThan(.02f));
        }
    }
}

