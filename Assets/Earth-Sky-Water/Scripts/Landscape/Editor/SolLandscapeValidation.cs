using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Sol.Landscape.Editor
{
    /// <summary>Reproducible content and visual fixture, never edits the authored demo.</summary>
    public static class SolLandscapeValidation
    {
        public const string Folder = "Assets/Earth-Sky-Water/Landscape/Examples";
        public static void RunContentAudit()
        {
            if(!Application.isBatchMode)throw new InvalidOperationException("Run this audit in the separate batch validation project.");
            BuildExample();BuildMaterialPreview();ValidateMigration();SolLandscapeBenchmark.Start();
        }
        public static void ValidateMigration()
        {
            if(!Application.isBatchMode||!Application.dataPath.Contains("LandscapeDesignerValidation"))throw new InvalidOperationException("Migration audit requires the separate validation project.");
            const string destination="Assets/LandscapeDemoValidation";Directory.CreateDirectory(destination);AssetDatabase.Refresh();
            EditorSceneManager.OpenScene("Assets/Scenes/Elementa_Demo.unity");
            var legacy=UnityEngine.Object.FindFirstObjectByType<SolLandscapeDriver>();if(legacy==null)throw new InvalidOperationException("Demo driver missing.");
            var terrain=legacy.landscapeTerrain!=null?legacy.landscapeTerrain:Terrain.activeTerrain;var original=terrain.terrainData;var config=legacy.config;
            string originalConfig=EditorJsonUtility.ToJson(config);var originalAlpha=original.GetAlphamaps(0,0,original.alphamapWidth,original.alphamapHeight);
            var camera=new GameObject("Migration Comparison Camera").AddComponent<Camera>();camera.orthographic=true;camera.orthographicSize=original.size.x*.3f;
            camera.transform.position=terrain.transform.position+new Vector3(original.size.x*.5f,original.size.y+100,original.size.z*.5f);camera.transform.rotation=Quaternion.Euler(90,0,0);camera.farClipPlane=original.size.y+500;
            float level=Shader.GetGlobalFloat("_Sol_GlobalWaterLevel"),wetness=Shader.GetGlobalFloat("_Sol_SurfaceWetness");Shader.SetGlobalFloat("_Sol_GlobalWaterLevel",-10000);Shader.SetGlobalFloat("_Sol_SurfaceWetness",0);
            legacy.Publish();Capture(camera,Path.GetFullPath("../landscape-migration-before.png"));
            var group=SolLandscapeAuthoring.Migrate(legacy,destination+"/Migrated Profile.asset",false);group.Publish();
            if(!group.Validate(out string reason))throw new InvalidOperationException(reason);
            Capture(camera,Path.GetFullPath("../landscape-migration-after.png"));
            if(EditorJsonUtility.ToJson(config)!=originalConfig)throw new InvalidOperationException("Migration changed the original config.");
            var migrated=terrain.terrainData.GetAlphamaps(0,0,original.alphamapWidth,original.alphamapHeight);
            float error=originalAlpha.Cast<float>().Zip(migrated.Cast<float>(),(a,b)=>Mathf.Abs(a-b)).Max();
            if(error>1f/255+.00001f)throw new InvalidOperationException("Migration changed alpha artwork beyond native control precision: "+error);
            group.SuspendBindings();int normalized=0;
            foreach(var neighbor in Terrain.activeTerrains.Where(t=>t!=terrain).ToArray())
            {
                var data=UnityEngine.Object.Instantiate(neighbor.terrainData);var old=data.terrainLayers;var palette=group.profile.Layers.Select(e=>e.terrainLayer).ToArray();
                if(!old.SequenceEqual(palette))
                {
                    normalized++;var input=data.GetAlphamaps(0,0,data.alphamapWidth,data.alphamapHeight);var output=new float[data.alphamapHeight,data.alphamapWidth,palette.Length];
                    for(int y=0;y<data.alphamapHeight;y++)for(int x=0;x<data.alphamapWidth;x++)for(int i=0;i<old.Length;i++)
                    {int index=Array.FindIndex(palette,p=>p==old[i]||p.diffuseTexture==old[i].diffuseTexture);output[y,x,index<0?group.profile.FallbackIndex:index]+=input[y,x,i];}
                    data.terrainLayers=palette;data.SetAlphamaps(0,0,output);
                }
                AssetDatabase.CreateAsset(data,AssetDatabase.GenerateUniqueAssetPath(destination+"/"+neighbor.name+" Clone.asset"));neighbor.terrainData=data;neighbor.GetComponent<TerrainCollider>().terrainData=data;group.tiles.Add(new SolLandscapeTile{terrain=neighbor});
            }
            SolLandscapeAuthoring.Connect(group);if(!group.Validate(out reason))throw new InvalidOperationException(reason);group.Publish();
            EditorSceneManager.SaveScene(group.gameObject.scene,destination+"/Migrated Demo.unity");
            File.WriteAllText(Path.GetFullPath("../landscape-migration-report.txt"),$"Primary migration: original config unchanged, maximum alpha difference {error:R}, copied terrain/collider data. Separate connected validation copy: {group.tiles.Count} tiles; {normalized} neighboring palettes deliberately normalized by texture identity with unmatched contributions assigned to fallback. Group validation passed. Compare retained before/after primary dry reference captures.\n");
            Shader.SetGlobalFloat("_Sol_GlobalWaterLevel",level);Shader.SetGlobalFloat("_Sol_SurfaceWetness",wetness);
        }
        [MenuItem("Tools/Elementa/Landscape/Build designer example")]
        public static void BuildExample()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            SolLandscapeLibrary.BuildLibrary();
            foreach(string name in new[]{"Temperate","Volcanic Coast"})
            {
                var profile=AssetDatabase.LoadAssetAtPath<SolLandscapeProfile>("Assets/Earth-Sky-Water/Landscape/Library/Profiles/"+name+".asset");
                if(SolLandscapeArrayBaker.GetStaleness(profile).IsStale)SolLandscapeArrayBaker.BakeTarget(profile);
            }
            Directory.CreateDirectory(Folder);AssetDatabase.Refresh();
            bool exists=File.Exists(Folder+"/Landscape Designer.unity");
            var scene=exists?EditorSceneManager.OpenScene(Folder+"/Landscape Designer.unity"):EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            Selection.activeObject=null;
            if(!exists)EditorSceneManager.SaveScene(scene,Folder+"/Landscape Designer.unity");
            var group=exists?UnityEngine.Object.FindFirstObjectByType<SolLandscapeGroup>():SolLandscapeCreation.Create(new SolLandscapeCreationSettings {scene=scene,name="Example Landscape"});group.paintResolution=512;
            SolLandscapeLibrary.BindRuntimeShaders(group.profile);
            group.profile.CopyFrom(AssetDatabase.LoadAssetAtPath<SolLandscapeProfile>("Assets/Earth-Sky-Water/Landscape/Library/Profiles/Temperate.asset"));
            group.profile.assetOwner=group.assetOwner;
            SolLandscapeLibrary.ConfigureStarterRules(group.profile,"Temperate");
            int tileIndex=0;
            foreach(var tile in group.tiles)
            {
                tile.terrain.transform.position=new Vector3((tileIndex%2)*128,0,(tileIndex/2)*128);tileIndex++;
                var data=tile.terrain.terrainData;data.size = new Vector3(128,128,128);tile.terrain.drawInstanced=true;
                var heights=new float[data.heightmapResolution,data.heightmapResolution];
                for(int y=0;y<data.heightmapResolution;y++)for(int x=0;x<data.heightmapResolution;x++)
                {
                    float wx=(tile.terrain.transform.position.x+x*data.size.x/(data.heightmapResolution-1))*4;float wz=(tile.terrain.transform.position.z+y*data.size.z/(data.heightmapResolution-1))*4;
                    heights[y,x]=.15f+.34f*Mathf.Exp(-((wx-510)*(wx-510)+(wz-570)*(wz-570))/60000f)+.04f*Mathf.Sin(wx*.018f)*Mathf.Sin(wz*.013f);
                }
                data.SetHeights(0,0,heights);SolLandscapeAuthoring.EnsurePaintAsset(group,tile);tile.paint.Restore(new byte[4][]);EditorUtility.SetDirty(data);
            }
            using(var stroke=new SolLandscapeStroke())
            {
                stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,6);
                for(int i=0;i<180;i++)stroke.ApplyDab(new Vector3(40+i,0,112.5f+Mathf.Sin(i*.035f)*12.5f),4,.6f,.5f);
                stroke.CommitStroke();foreach(var tile in group.tiles)EditorUtility.SetDirty(tile.paint);
            }
            var sun=UnityEngine.Object.FindFirstObjectByType<Light>()??new GameObject("Sun").AddComponent<Light>();sun.type=LightType.Directional;sun.intensity=2;sun.transform.rotation=Quaternion.Euler(45,-35,0);
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.35f,.4f,.5f);
            var camera=UnityEngine.Object.FindFirstObjectByType<Camera>()??new GameObject("Landscape Camera").AddComponent<Camera>();camera.transform.position=new Vector3(128,190,-160);camera.transform.LookAt(new Vector3(128,25,128));camera.farClipPlane=3000;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.45f,.6f,.72f);
            group.Publish();EditorSceneManager.SaveScene(scene,Folder+"/Landscape Designer.unity");AssetDatabase.SaveAssets();
            var saved=group.tiles.Select(t=>t.paint.Snapshot()).ToArray();
            EditorSceneManager.OpenScene(Folder+"/Landscape Designer.unity");group=UnityEngine.Object.FindFirstObjectByType<SolLandscapeGroup>();
            for(int i=0;i<saved.Length;i++)for(int channel=0;channel<4;channel++)
                if(!(saved[i][channel]??Array.Empty<byte>()).SequenceEqual(group.tiles[i].paint.Bytes(channel)??Array.Empty<byte>()))throw new InvalidOperationException("Paint did not survive scene reopening.");
            if(!group.Validate(out string reason))throw new InvalidOperationException(reason);
            group.Publish();camera=UnityEngine.Object.FindFirstObjectByType<Camera>();
            Capture(camera,Path.GetFullPath("../landscape-designer-preview.png"));
            Debug.Log("Landscape designer example and starter profiles built.");
        }
        public static void Capture(Camera camera,string destination)
        {
            var target=new RenderTexture(1280,800,24);var pixels=new Texture2D(1280,800,TextureFormat.RGB24,false);
            var prior=camera.targetTexture;var active=RenderTexture.active;
            try{camera.targetTexture=target;camera.Render();RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,1280,800),0,0);File.WriteAllBytes(destination,pixels.EncodeToPNG());}
            finally{camera.targetTexture=prior;RenderTexture.active=active;UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(pixels);}
        }
        [MenuItem("Tools/Elementa/Landscape/Build material preview")]
        public static void BuildMaterialPreview()
        {
            if(!Application.isBatchMode&&!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())return;
            Directory.CreateDirectory(Folder+"/Material Preview");AssetDatabase.Refresh();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene,Folder+"/Material Preview.unity");
            var temperate=AssetDatabase.LoadAssetAtPath<SolLandscapeProfile>("Assets/Earth-Sky-Water/Landscape/Library/Profiles/Temperate.asset");
            var volcanic=AssetDatabase.LoadAssetAtPath<SolLandscapeProfile>("Assets/Earth-Sky-Water/Landscape/Library/Profiles/Volcanic Coast.asset");
            var extra=AssetDatabase.LoadAssetAtPath<SolLandscapeProfile>(Folder+"/Material Preview/Additional Artwork.asset");
            if(extra==null)
            {
                extra=ScriptableObject.CreateInstance<SolLandscapeProfile>();extra.artworkResolution=1024;
                extra.SetLayers(new[]{"Stone_A","Cliff_Mossy_A"}.Select(name=>SolLandscapeLibrary.EntryFor(AssetDatabase.LoadAssetAtPath<TerrainLayer>("Assets/Earth-Sky-Water/Landscape/Library/Layers/"+name+".terrainlayer"))));
                extra.EnsureIds();SolLandscapeLibrary.BindRuntimeShaders(extra);AssetDatabase.CreateAsset(extra,Folder+"/Material Preview/Additional Artwork.asset");
            }
            if(SolLandscapeArrayBaker.GetStaleness(extra).IsStale)SolLandscapeArrayBaker.BakeTarget(extra);
            var entries=temperate.Layers.Select(e=>(profile:temperate,entry:e)).Concat(volcanic.Layers.Take(5).Select(e=>(profile:volcanic,entry:e))).Concat(extra.Layers.Select(e=>(profile:extra,entry:e))).ToArray();
            for(int index=0;index<entries.Length;index++)
            {
                var item=entries[index];string destination=SolLandscapeAssetLocations.SceneRoot(scene,item.entry.terrainLayer.name);
                var owner=AssetDatabase.LoadAssetAtPath<SolLandscapeAsset>(destination+"/Landscape.asset")??SolLandscapeAssetLocations.Create(destination,scene,item.entry.terrainLayer.name);
                string path=destination+"/Profiles/"+item.entry.terrainLayer.name;
                var profile=AssetDatabase.LoadAssetAtPath<SolLandscapeProfile>(path+".asset");
                if(profile==null){profile=UnityEngine.Object.Instantiate(item.profile);AssetDatabase.CreateAsset(profile,path+".asset");}
                profile.CopyFrom(item.profile);profile.assetOwner=owner;SolLandscapeAssetLocations.Register(owner,profile);
                profile.SetLayers(new[]{JsonUtility.FromJson<SolLandscapeLayerEntry>(JsonUtility.ToJson(item.entry))});profile.fallbackMaterialId=profile.Layers[0].materialId;profile.preserveLegacyFallback=false;
                profile.Layers[0].mode=SolLandscapeLayerMode.Auto;profile.Layers[0].autoWeight=1;profile.Layers[0].useAltitudeRange=false;profile.Layers[0].slopeRange=new Vector2(0,90);
                profile.Layers[0].weatherSnowSusceptibility=profile.Layers[0].permanentSnowSusceptibility=0;SolLandscapeLibrary.BindRuntimeShaders(profile);
                int sourceIndex=item.profile.Layers.ToList().IndexOf(item.entry);
                profile.RecordBake(null,new[]{item.entry.terrainLayer},item.profile.CSArray,item.profile.NOHArray,"",new[]{item.profile.BakeFingerprints[sourceIndex]},item.profile.BakedUtc,item.profile.CSStorageBytes,item.profile.NOHStorageBytes,"Shared library artwork preview");
                string terrainPath=destination+"/TerrainData/"+item.entry.terrainLayer.name+" Terrain.asset";
                var data=AssetDatabase.LoadAssetAtPath<TerrainData>(terrainPath);if(data==null){data=new TerrainData();AssetDatabase.CreateAsset(data,terrainPath);}SolLandscapeAssetLocations.Register(owner,data);
                data.heightmapResolution=65;data.alphamapResolution=32;data.size=new Vector3(28,16,28);data.terrainLayers=new[]{item.entry.terrainLayer};
                var heights=new float[65,65];for(int y=0;y<65;y++)for(int x=0;x<65;x++){float dx=(x-32)/32f,dy=(y-32)/32f;heights[y,x]=.15f+.4f*Mathf.Exp(-4*(dx*dx+dy*dy));}data.SetHeights(0,0,heights);
                var alpha=new float[32,32,1];for(int y=0;y<32;y++)for(int x=0;x<32;x++)alpha[y,x,0]=1;data.SetAlphamaps(0,0,alpha);
                var tile=Terrain.CreateTerrainGameObject(data);tile.name=item.entry.terrainLayer.name;tile.transform.position=new Vector3(index%5*32,0,index/5*32);
                var group=tile.AddComponent<SolLandscapeGroup>();group.assetOwner=owner;group.profile=profile;group.tiles.Add(new SolLandscapeTile{terrain=tile.GetComponent<Terrain>()});group.Publish();EditorUtility.SetDirty(data);EditorUtility.SetDirty(profile);
            }
            var light=new GameObject("Preview Sun").AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.5f;light.transform.rotation=Quaternion.Euler(50,-35,0);
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=Color.gray*.6f;
            var camera=new GameObject("Material Preview Camera").AddComponent<Camera>();camera.transform.position=new Vector3(78,155,-85);camera.transform.LookAt(new Vector3(78,0,46));camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.15f,.17f,.19f);
            EditorSceneManager.SaveScene(scene,Folder+"/Material Preview.unity");AssetDatabase.SaveAssets();Capture(camera,Path.GetFullPath("../landscape-material-library.png"));
            camera.transform.position=new Vector3(14,23,-10);camera.transform.LookAt(new Vector3(14,4,14));Capture(camera,Path.GetFullPath("../landscape-grass-closeup.png"));
        }
    }
}

