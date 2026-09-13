using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Sol.Landscape.Editor
{
    public static class SolLandscapeAuthoring
    {
        public static string Folder(UnityEngine.Object asset) => Path.GetDirectoryName(AssetDatabase.GetAssetPath(asset)).Replace('\\','/');
        public static Terrain[] MigrationTiles(SolLandscapeDriver legacy, bool neighbors)
        {
            if (legacy == null || legacy.config == null) throw new InvalidOperationException("Assign an existing landscape driver and config.");
            var first = legacy.landscapeTerrain != null ? legacy.landscapeTerrain : Terrain.activeTerrain;
            if (first == null || first.terrainData == null) throw new InvalidOperationException("Assign the primary terrain and its TerrainData.");
            var selected = new HashSet<Terrain> { first };
            if (neighbors)
            {
                bool added;
                do
                {
                    added=false;
                    foreach(var terrain in Terrain.activeTerrains)
                        if(terrain.gameObject.scene==first.gameObject.scene && !selected.Contains(terrain) && selected.Any(t=>Adjacent(t,terrain))) { selected.Add(terrain); added=true; }
                } while(added);
            }
            return new[]{first}.Concat(selected.Where(t=>t!=first).OrderBy(t=>t.name)).ToArray();
        }
        public static bool CanMigrate(SolLandscapeDriver legacy, bool neighbors, bool usePrimaryBase, out string reason)
        {
            try {ValidateMigration(legacy,MigrationTiles(legacy,neighbors),usePrimaryBase);reason=null;return true;}
            catch(InvalidOperationException error){reason=error.Message;return false;}
        }
        static void ValidateMigration(SolLandscapeDriver legacy, Terrain[] selected, bool usePrimaryBase)
        {
            var first=selected[0];
            if(!legacy.TryValidateContract(out string contract))throw new InvalidOperationException(contract);
            foreach(var terrain in selected)
            {
                var original=terrain.terrainData;
                if((!usePrimaryBase || terrain==first) && !original.terrainLayers.SequenceEqual(legacy.config.Layers.Select(e=>e.terrainLayer)))
                    throw new InvalidOperationException(terrain.name+": saved palette differs from the primary terrain. Use 'Migrate shared material using primary base' to copy the visible legacy base onto editable terrain copies.");
                if(usePrimaryBase && terrain.materialTemplate!=first.materialTemplate)
                    throw new InvalidOperationException(terrain.name+": uses a different material. Shared-material migration cannot replace it.");
                if(SolLandscapeGroup.Owns(terrain)) throw new InvalidOperationException(terrain.name+": already belongs to a landscape group.");
                if(terrain.transform.rotation!=Quaternion.identity || terrain.transform.lossyScale!=Vector3.one || terrain.terrainData.heightmapResolution!=first.terrainData.heightmapResolution || terrain.terrainData.alphamapResolution!=first.terrainData.alphamapResolution)
                    throw new InvalidOperationException(terrain.name+": match sample resolutions and reset rotation/scale before migration.");
            }
            if(usePrimaryBase && UnityEngine.Object.FindObjectsByType<SolLandscapeDriver>(FindObjectsSortMode.None).Any(d=>d!=legacy && d.isActiveAndEnabled && selected.Contains(d.landscapeTerrain)))
                throw new InvalidOperationException("Another landscape driver owns a selected tile. Migrate that landscape separately.");
        }
        public static SolLandscapeGroup Migrate(SolLandscapeDriver legacy, string destination, bool neighbors, bool usePrimaryBase = false)
        {
            var selected=MigrationTiles(legacy,neighbors);ValidateMigration(legacy,selected,usePrimaryBase);var first=selected[0];
            Undo.IncrementCurrentGroup();int transaction=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Migrate landscape");
            var profile = ScriptableObject.CreateInstance<SolLandscapeProfile>(); profile.CopyFrom(legacy.config); profile.preserveLegacyFallback = true;
            foreach (var e in profile.Layers) { e.liveSurfaceSettings = true; e.textureSize = e.terrainLayer.tileSize; e.textureOffset = e.terrainLayer.tileOffset; e.normalStrength = e.terrainLayer.normalScale; e.sandResponse = e.terrainLayer.name.IndexOf("sand", StringComparison.OrdinalIgnoreCase) >= 0; }
            var fallback = profile.Layers.FirstOrDefault(e => e.mode == SolLandscapeLayerMode.Auto && e.terrainLayer.name.IndexOf("dirt",StringComparison.OrdinalIgnoreCase)>=0)
                ?? profile.Layers.FirstOrDefault(e => e.mode == SolLandscapeLayerMode.Auto);
            profile.fallbackMaterialId = fallback?.materialId; profile.EnsureIds();
            profile.stochasticTiling = first.materialTemplate != null && first.materialTemplate.IsKeywordEnabled("_SOL_LANDSCAPE_STOCHASTIC");
            profile.triplanarProjection = first.materialTemplate != null && first.materialTemplate.IsKeywordEnabled("_SOL_LANDSCAPE_TRIPLANAR");
            profile.heightBlend = first.materialTemplate != null && first.materialTemplate.IsKeywordEnabled("_SOL_LANDSCAPE_BLEND_HEIGHT");
            SolLandscapeLibrary.BindRuntimeShaders(profile);
            destination = AssetDatabase.GenerateUniqueAssetPath(destination); AssetDatabase.CreateAsset(profile,destination);
            var go = new GameObject("Elementa Landscape");UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go,first.gameObject.scene);Undo.RegisterCreatedObjectUndo(go,"Migrate landscape");
            var group = Undo.AddComponent<SolLandscapeGroup>(go); group.profile = profile; group.materialTemplate = first.materialTemplate;
            group.projectionOrigin = new Vector2(first.transform.position.x,first.transform.position.z);
            foreach(var terrain in selected)
            {
                string copyPath=AssetDatabase.GenerateUniqueAssetPath(Folder(profile)+"/"+terrain.name+" Designer.asset");
                string sourcePath=AssetDatabase.GetAssetPath(terrain.terrainData);TerrainData data;
                if(!string.IsNullOrEmpty(sourcePath)&&!EditorUtility.IsDirty(terrain.terrainData))
                {
                    if(!AssetDatabase.CopyAsset(sourcePath,copyPath))throw new InvalidOperationException("Could not copy "+sourcePath);
                    data=AssetDatabase.LoadAssetAtPath<TerrainData>(copyPath);
                }
                else {data=UnityEngine.Object.Instantiate(terrain.terrainData);AssetDatabase.CreateAsset(data,copyPath);}
                if(usePrimaryBase && terrain!=first)
                {
                    data.terrainLayers=first.terrainData.terrainLayers;
                    data.SetAlphamaps(0,0,first.terrainData.GetAlphamaps(0,0,first.terrainData.alphamapWidth,first.terrainData.alphamapHeight));
                    EditorUtility.SetDirty(data);
                }
                Undo.RecordObject(terrain,"Migrate landscape"); terrain.terrainData=data;
                var collider=terrain.GetComponent<TerrainCollider>(); if(collider!=null) { Undo.RecordObject(collider,"Migrate landscape"); collider.terrainData=data; }
                group.tiles.Add(new SolLandscapeTile { terrain=terrain });
            }
            Undo.RecordObject(legacy,"Migrate landscape"); legacy.enabled=false;
            Connect(group); EditorUtility.SetDirty(group); AssetDatabase.SaveAssets(); group.Invalidate(); Undo.CollapseUndoOperations(transaction); return group;
        }
        public static SolLandscapeGroup MigrateBesideScene(SolLandscapeDriver legacy, bool usePrimaryBase = false)
        {
            var terrain = MigrationTiles(legacy, true)[0];
            string name = "Migrated Landscape", root = SolLandscapeAssetLocations.SceneRoot(terrain.gameObject.scene, name);
            int suffix = 2; while (AssetDatabase.IsValidFolder(root)) { name = "Migrated Landscape " + suffix++; root = SolLandscapeAssetLocations.SceneRoot(terrain.gameObject.scene, name); }
            Undo.IncrementCurrentGroup(); int transaction = Undo.GetCurrentGroup();
            try
            {
                var owner = SolLandscapeAssetLocations.Create(root, terrain.gameObject.scene, name);
                var group = Migrate(legacy, root + "/Profiles/Profile.asset", true, usePrimaryBase);
                Undo.RecordObject(group, "Assign landscape assets"); group.assetOwner = owner; group.profile.assetOwner = owner;
                SolLandscapeAssetLocations.Register(owner, group.profile);
                foreach (var tile in group.tiles)
                {
                    var data = tile.terrain.terrainData;
                    string before = AssetDatabase.GetAssetPath(data), after = SolLandscapeAssetLocations.PathFor(group, "TerrainData", tile.terrain.name);
                    string error = AssetDatabase.MoveAsset(before, after); if (!string.IsNullOrEmpty(error)) throw new IOException(error);
                    SolLandscapeAssetLocations.Register(owner, data);
                }
                EditorUtility.SetDirty(group); EditorUtility.SetDirty(group.profile); AssetDatabase.SaveAssets(); Undo.CollapseUndoOperations(transaction); return group;
            }
            catch { Undo.RevertAllDownToGroup(transaction); if (AssetDatabase.IsValidFolder(root)) AssetDatabase.DeleteAsset(root); throw; }
        }
        static bool Adjacent(Terrain a,Terrain b)
        {
            if(a.terrainData==null || b.terrainData==null || a.terrainData.size!=b.terrainData.size) return false;
            var d=b.transform.position-a.transform.position; var size=a.terrainData.size;
            return Mathf.Abs(d.y)<.01f && ((Mathf.Abs(Mathf.Abs(d.x)-size.x)<.01f && Mathf.Abs(d.z)<.01f) || (Mathf.Abs(Mathf.Abs(d.z)-size.z)<.01f && Mathf.Abs(d.x)<.01f));
        }
        public static void Connect(SolLandscapeGroup group)
        {
            foreach(var tile in group.tiles.Where(t=>t.terrain!=null && t.terrain.terrainData!=null))
            {
                var t=tile.terrain; var p=t.transform.position; var s=t.terrainData.size;
                Terrain At(Vector3 offset) => group.tiles.FirstOrDefault(x => x.terrain!=null && (x.terrain.transform.position-p-offset).sqrMagnitude<.0001f)?.terrain;
                t.SetNeighbors(At(new Vector3(-s.x,0,0)),At(new Vector3(0,0,s.z)),At(new Vector3(s.x,0,0)),At(new Vector3(0,0,-s.z)));
            }
        }
        public static void EnsurePaintAsset(SolLandscapeGroup group, SolLandscapeTile tile)
        {
            if(tile.paint!=null) return;
            if(string.IsNullOrEmpty(AssetDatabase.GetAssetPath(group.profile)))
                throw new InvalidOperationException("Save the landscape profile before painting so tile edits have an asset destination.");
            Undo.RecordObject(group,"Create landscape paint data");
            tile.paint=ScriptableObject.CreateInstance<SolLandscapePaintData>(); tile.paint.Initialize(group.paintResolution);
            SolLandscapeLifecycle.CreatingPaintAsset++;
            try {AssetDatabase.CreateAsset(tile.paint,SolLandscapeAssetLocations.PathFor(group,"Paint",tile.terrain.name+" Paint"));}
            finally {SolLandscapeLifecycle.CreatingPaintAsset--;}
            // Persistent asset creation cannot be recreated reliably by RegisterCreatedObjectUndo.
            // Undo the tile reference and pixel edits, keeping the asset alive for redo.
            SolLandscapeAssetLocations.Register(group.assetOwner,tile.paint);
            EditorUtility.SetDirty(group);
        }
        public static void RepairTilePalettes(SolLandscapeGroup group)
        {
            if (group.profile == null || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(group.profile))) throw new InvalidOperationException("Assign a saved profile first.");
            SolLandscapeLifecycle.FlushForAssetSave(Array.Empty<string>());
            var palette = group.profile.Layers.Select(e => e.terrainLayer).ToArray();
            Undo.IncrementCurrentGroup(); int transaction = Undo.GetCurrentGroup(); var copies = new List<string>();
            try
            {
                foreach (var tile in group.tiles.Where(t => t.terrain != null && t.terrain.terrainData != null && !t.terrain.terrainData.terrainLayers.SequenceEqual(palette)))
                {
                    var source = tile.terrain.terrainData; var old = source.terrainLayers;
                    var input = source.GetAlphamaps(0, 0, source.alphamapWidth, source.alphamapHeight);
                    var output = new float[source.alphamapHeight, source.alphamapWidth, palette.Length];
                    for (int y = 0; y < source.alphamapHeight; y++) for (int x = 0; x < source.alphamapWidth; x++)
                    {
                        float total = 0;
                        for (int i = 0; i < old.Length; i++) { int slot = Array.IndexOf(palette, old[i]); float weight = input[y, x, i]; output[y, x, slot < 0 ? group.profile.FallbackIndex : slot] += weight; total += weight; }
                        if (total <= .000001f) output[y, x, group.profile.FallbackIndex] = 1;
                    }
                    var data = UnityEngine.Object.Instantiate(source); string destination = SolLandscapeAssetLocations.PathFor(group, "TerrainData", tile.terrain.name + " Palette repair");
                    // Persist the clone's native subassets before replacing its alphamap layout.
                    AssetDatabase.CreateAsset(data, destination); copies.Add(destination); data.terrainLayers = palette; data.SetAlphamaps(0, 0, output);
                    EditorUtility.SetDirty(data); AssetDatabase.SaveAssetIfDirty(data);
                    Undo.RecordObject(tile.terrain, "Repair terrain palette"); tile.terrain.terrainData = data;
                    var collider = tile.terrain.GetComponent<TerrainCollider>(); if (collider != null) { Undo.RecordObject(collider, "Repair terrain palette"); collider.terrainData = data; }
                    SolLandscapeAssetLocations.Register(group.assetOwner, data); EditorUtility.SetDirty(data);
                }
                Undo.CollapseUndoOperations(transaction); group.Invalidate(); AssetDatabase.SaveAssets();
            }
            catch { Undo.RevertAllDownToGroup(transaction); foreach (string copy in copies) AssetDatabase.DeleteAsset(copy); throw; }
        }
        public static void ChangePalette(SolLandscapeGroup group, IList<SolLandscapeLayerEntry> entries)
        {
            if(entries.Count<1 || entries.Count>8) throw new InvalidOperationException("Keep 1–8 materials.");
            SolLandscapeLifecycle.FlushForAssetSave(Array.Empty<string>());
            group.profile.EnsureIds();
            var old=group.profile.Layers.ToArray(); var map=entries.Select(e=>Array.FindIndex(old,x=>x.materialId==e.materialId)).ToArray();
            var affected=UnityEngine.Object.FindObjectsByType<SolLandscapeGroup>(FindObjectsInactive.Include,FindObjectsSortMode.None)
                .Where(g=>g.profile==group.profile).Append(group).Distinct().ToArray();
            var changedTerrain=new HashSet<TerrainData>();var changedPaint=new HashSet<SolLandscapePaintData>();
            Undo.IncrementCurrentGroup();
            int undo=Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Change landscape palette"); Undo.RecordObject(group.profile,"Change landscape palette");
            foreach(var tile in affected.SelectMany(g=>g.tiles))
            {
                var data=tile.terrain.terrainData;
                if(changedTerrain.Add(data))
                {
                    Undo.RegisterCompleteObjectUndo(data,"Change landscape palette");
                    var source=data.GetAlphamaps(0,0,data.alphamapWidth,data.alphamapHeight);
                    var result=new float[data.alphamapHeight,data.alphamapWidth,entries.Count];
                    for(int y=0;y<data.alphamapHeight;y++) for(int x=0;x<data.alphamapWidth;x++)
                    {
                        float sum=0; for(int i=0;i<map.Length;i++) if(map[i]>=0) { result[y,x,i]=source[y,x,map[i]];sum+=result[y,x,i]; }
                        if(sum<=.000001f) result[y,x,0]=1; else for(int i=0;i<map.Length;i++) result[y,x,i]/=sum;
                    }
                    data.terrainLayers=entries.Select(e=>e.terrainLayer).ToArray();data.SetAlphamaps(0,0,result);
                    EditorUtility.SetDirty(data);
                }
                if(tile.paint!=null && changedPaint.Add(tile.paint)) { Undo.RegisterCompleteObjectUndo(tile.paint,"Change landscape palette");tile.paint.Remap(map);EditorUtility.SetDirty(tile.paint); }
            }
            group.profile.SetLayers(entries);group.profile.EnsureIds();
            if(!entries.Any(e=>e.materialId==group.profile.fallbackMaterialId)) group.profile.fallbackMaterialId=entries[0].materialId;
            EditorUtility.SetDirty(group.profile);Undo.CollapseUndoOperations(undo);foreach(var owner in affected)owner.Invalidate();
        }
        public static void ApplyRule(SolLandscapeProfile profile,int index,string preset)
        {
            Undo.RecordObject(profile,"Apply landscape rule");var e=profile.Layers[index];e.ruleModel=SolLandscapeRuleModel.Ranges;e.mode=SolLandscapeLayerMode.Auto;
            e.slopeRange=preset=="Exposed cliff"?new Vector2(35,90):new Vector2(0,35);e.slopeFeather=8;
            e.useAltitudeRange=preset=="Shoreline"; if(e.useAltitudeRange) { e.altitudeReference=SolLandscapeAltitudeReference.RelativeToWaterLevel;e.heightRange=new Vector2(-3,3);e.altitudeFeather=2; }
            EditorUtility.SetDirty(profile);
        }
    }
}
