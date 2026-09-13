using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Sol.Landscape.Editor
{
    public static class SolLandscapeLibrary
    {
        public const string Root = "Assets/Earth-Sky-Water/Landscape/Library";
        [Serializable] public sealed class Source { public string name,source,convention,colour,normal,mask,height,conversionVersion; public Vector4 maskRemapMin; public Vector4 maskRemapMax=Vector4.one; }
        [Serializable] public sealed class Manifest { public Source[] materials; }
        static Manifest ReadManifest() => JsonUtility.FromJson<Manifest>(File.ReadAllText(Root+"/DonorManifest.json"));
        public static void BindRuntimeShaders(SolLandscapeProfile profile)
        {
            profile.terrainShader=Shader.Find("Sol/Terrain/Array Lit");
            profile.paintShader=Shader.Find("Hidden/Sol/Landscape Paint");
            EditorUtility.SetDirty(profile);
        }
        [MenuItem("Tools/Elementa/Landscape/Build curated library")]
        public static void BuildLibrary()
        {
            Directory.CreateDirectory(Root+"/Layers");Directory.CreateDirectory(Root+"/Profiles");AssetDatabase.Refresh();
            foreach(var source in ReadManifest().materials)
            {
                Configure(source.colour,true,false);Configure(source.normal,false,true);Configure(source.mask,false,false);Configure(source.height,false,false);
                string path=Root+"/Layers/"+source.name+".terrainlayer";
                var layer=AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
                if(layer==null) { layer=new TerrainLayer();AssetDatabase.CreateAsset(layer,path); }
                layer.diffuseTexture=AssetDatabase.LoadAssetAtPath<Texture2D>(source.colour);layer.normalMapTexture=AssetDatabase.LoadAssetAtPath<Texture2D>(source.normal);
                layer.maskMapTexture=AssetDatabase.LoadAssetAtPath<Texture2D>(source.mask);layer.tileSize=new Vector2(4,4);layer.maskMapRemapMin=source.maskRemapMin;layer.maskMapRemapMax=source.maskRemapMax;
                EditorUtility.SetDirty(layer);
            }
            CreateProfile("Temperate",new[]{"Grass_A","Grass_Soil_A","Heather_A","Pebbles_B","Cliff_Mossy_E","Dirt","Path","Stone2"});
            CreateProfile("Volcanic Coast",new[]{"Black_Sand_A","Black_Sand_Rocks_B","Grass_Moss_A","Rock_Jagged_B","Tidal_Pools_B","Pebbles_B","Dirt","Path"});
            AssetDatabase.SaveAssets();
        }
        static void Configure(string path,bool srgb,bool normal)
        {
            if(string.IsNullOrEmpty(path))return;
            var importer=AssetImporter.GetAtPath(path) as TextureImporter;
            if(importer==null)throw new InvalidOperationException("Missing working texture: "+path);
            if(importer.textureType==(normal?TextureImporterType.NormalMap:TextureImporterType.Default)&&importer.sRGBTexture==srgb&&importer.maxTextureSize==2048&&importer.textureCompression==TextureImporterCompression.CompressedHQ)return;
            importer.textureType=normal?TextureImporterType.NormalMap:TextureImporterType.Default;
            importer.sRGBTexture=srgb;importer.maxTextureSize=2048;importer.textureCompression=TextureImporterCompression.CompressedHQ;
            importer.SaveAndReimport();
        }
        public static SolLandscapeLayerEntry EntryFor(TerrainLayer layer)
        {
            var source=ReadManifest().materials.FirstOrDefault(s=>s.name==layer.name);
            var entry=new SolLandscapeLayerEntry(layer){materialId=Guid.NewGuid().ToString("N"),ruleModel=SolLandscapeRuleModel.Ranges,
                liveSurfaceSettings=true,textureSize=layer.tileSize,textureOffset=layer.tileOffset,normalStrength=layer.normalScale,
                maskConvention=source!=null?SolLandscapeMaskConvention.HdrpMaskMap:SolLandscapeMaskConvention.SolPacked,
                heightTexture=source!=null&&!string.IsNullOrEmpty(source.height)?AssetDatabase.LoadAssetAtPath<Texture2D>(source.height):null};
            bool cliff=layer.name.Contains("Cliff")||layer.name.Contains("Rock")||layer.name.Contains("Stone");
            entry.slopeRange=cliff?new Vector2(35,90):new Vector2(0,35);entry.triplanarProjection=cliff;entry.stochasticTiling=!cliff;
            entry.sandResponse=layer.name.Contains("Sand");
            if(entry.sandResponse||layer.name.Contains("Tidal")){entry.useAltitudeRange=true;entry.altitudeReference=SolLandscapeAltitudeReference.RelativeToWaterLevel;entry.heightRange=new Vector2(-5,5);entry.altitudeFeather=3;}
            if(layer.name.Contains("Path"))entry.mode=SolLandscapeLayerMode.Manual;
            return entry;
        }
        static void CreateProfile(string name,string[] names)
        {
            string path=Root+"/Profiles/"+name+".asset";var existing=AssetDatabase.LoadAssetAtPath<SolLandscapeProfile>(path);if(existing!=null){BindRuntimeShaders(existing);if(existing.version<2)ConfigureStarterRules(existing,name);return;}
            var profile=ScriptableObject.CreateInstance<SolLandscapeProfile>();
            var legacy=AssetDatabase.LoadAssetAtPath<SolLandscapeConfig>("Assets/Earth-Sky-Water/Landscape/Water2LandscapeConfig.asset");if(legacy!=null)profile.CopyFrom(legacy);
            var entries=new List<SolLandscapeLayerEntry>();
            foreach(string item in names)
            {
                var layer=AssetDatabase.LoadAssetAtPath<TerrainLayer>(Root+"/Layers/"+item+".terrainlayer")??AssetDatabase.LoadAssetAtPath<TerrainLayer>("Assets/Earth-Sky-Water/Landscape/Resources/TerrainLayer_"+item+".terrainlayer");
                if(layer==null)throw new InvalidOperationException("Missing library material "+item);entries.Add(EntryFor(layer));
            }
            profile.SetLayers(entries);profile.fallbackMaterialId=entries.First(e=>e.terrainLayer.name.Contains("Dirt")).materialId;profile.EnsureIds();
            // Newly built profiles need their own eight-slice artwork; never borrow the legacy six-slice pair.
            profile.RecordBake(null,entries.Select(e=>e.terrainLayer).ToArray(),null,null,"",Array.Empty<SolLandscapeBakeFingerprint>(),"",0,0,"Rebuild required");
            ConfigureStarterRules(profile,name);BindRuntimeShaders(profile);AssetDatabase.CreateAsset(profile,path);
        }
        public static void ConfigureStarterRules(SolLandscapeProfile profile,string name)
        {
            foreach(var entry in profile.Layers)
            {
                string material=entry.terrainLayer.name;
                if(material.Contains("Dirt"))entry.autoWeight=0; // The unconditional safety fallback still claims otherwise empty ground.
                if(material.Contains("Stone2"))entry.mode=SolLandscapeLayerMode.Manual;
                if(material=="Grass_A"){entry.slopeRange=new Vector2(0,25);entry.autoWeight=1;}
                if(material=="Grass_Soil_A"){entry.slopeRange=new Vector2(20,40);entry.autoWeight=.7f;}
                if(material=="Heather_A"){entry.useAltitudeRange=true;entry.heightRange=new Vector2(38,55);entry.altitudeFeather=8;entry.autoWeight=.4f;}
                if(material=="Pebbles_B"){entry.slopeRange=new Vector2(25,35);entry.autoWeight=.2f;}
                if(material.Contains("Cliff"))entry.slopeRange=new Vector2(35,90);
                if(name=="Volcanic Coast")
                {
                    if(material=="Black_Sand_A")entry.slopeRange=new Vector2(0,15);
                    if(material=="Black_Sand_Rocks_B")entry.slopeRange=new Vector2(12,40);
                    if(material=="Grass_Moss_A"){entry.useAltitudeRange=true;entry.heightRange=new Vector2(5,2000);entry.altitudeFeather=3;}
                    if(material=="Tidal_Pools_B"){entry.slopeRange=new Vector2(0,8);entry.heightRange=new Vector2(-2,1);entry.autoWeight=.5f;}
                }
            }
            profile.version=2;EditorUtility.SetDirty(profile);
        }
        [Obsolete("Use SolLandscapeCreation with explicit settings or the Create landscape wizard.")]
        public static SolLandscapeGroup CreateGroupFromSelection(string path,string preset="Temperate")
        {
            return SolLandscapeCreation.Create(new SolLandscapeCreationSettings {
                name=Path.GetFileNameWithoutExtension(path),preset=preset,scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene()
            });
        }
    }
}
