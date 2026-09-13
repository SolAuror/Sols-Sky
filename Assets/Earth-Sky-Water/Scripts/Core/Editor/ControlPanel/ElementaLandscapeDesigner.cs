using System;
using System.Linq;
using System.Collections.Generic;
using Sol.Landscape;
using Sol.Landscape.Editor;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TerrainTools;
using UnityEngine.UIElements;
using UI = Sol.Environment.EditorTools.ElementaPanelGui;

namespace Sol.Environment.EditorTools
{
    sealed partial class ElementaLandscapePage
    {
        SolLandscapeGroup designerGroup;
        SolLandscapeStroke stroke;
        readonly HashSet<SolLandscapePaintData> undoPaint = new();
        int undoGroup = -1;
        float flattenHeight;
        Vector3 lastDab;
        Material sculptMaterial;
        bool hooked;
        static readonly string[] tools = { "Inspect", "Paint material", "Clear painted overrides", "Exclude material", "Restore excluded", "Raise", "Lower", "Smooth", "Flatten", "Stamp", "Remove material", "Restore removed material" };
        bool BuildDesigner(ElementaPanelContext context, VisualElement root)
        {
            var state=context.State;
            var picker=new ObjectField("Landscape group") { objectType=typeof(SolLandscapeGroup), value=state.landscapeGroup, allowSceneObjects=true };
            root.Add(picker);picker.RegisterValueChangedCallback(e=> { Finish(); designerGroup?.ClearPreview(this);state.landscapeGroup=e.newValue as SolLandscapeGroup;context.RefreshLayout(); });
            designerGroup=state.landscapeGroup;
            if(designerGroup==null)
            {
                UI.Help(root,"Create a connected landscape to start sculpting and painting, or migrate existing terrain below. Remove material works on base paint, automatic rules and brush overrides.");
                UI.Button(root,"Create landscape…",()=>OpenCreation(context));
                if(context.Landscape!=null)
                {
                    bool standard=SolLandscapeAuthoring.CanMigrate(context.Landscape,true,false,out string reason);
                    var feedback=UI.Help(root,standard?"Migration creates editable copies of terrain data and keeps the source assets.":reason);
                    void Migrate(bool primaryBase)
                    {
                        if(string.IsNullOrEmpty(context.Landscape.gameObject.scene.path) && !EditorSceneManager.SaveScene(context.Landscape.gameObject.scene))return;
                        try {state.landscapeGroup=SolLandscapeAuthoring.MigrateBesideScene(context.Landscape,primaryBase);state.landscapeActivity="Materials";state.landscapeTool=1;context.RefreshLayout();}
                        catch(Exception error){feedback.text=error.Message;}
                    }
                    UI.Button(root,"Migrate terrain and connected neighbors",()=>Migrate(false),enabled:standard);
                    if(!standard)
                    {
                        bool shared=SolLandscapeAuthoring.CanMigrate(context.Landscape,true,true,out string sharedReason);
                        UI.Note(root,"Shared-material migration copies the primary terrain's palette and base paint onto every connected tile's editable copy. Original terrain assets remain intact. Texture alignment becomes shared across tiles.");
                        UI.Button(root,"Migrate shared material using primary base",()=>Migrate(true),enabled:shared);
                        if(!shared)UI.Help(root,sharedReason);
                    }
                }
                return false;
            }
            if(designerGroup.profile==null || designerGroup.tiles.Count==0)
            {
                UI.Help(root,"This landscape is empty or incomplete. Create a new landscape here to set up terrain, materials, colliders and connections automatically.");
                UI.Button(root,"Create landscape…",()=>OpenCreation(context));
                var repair=UI.Foldout(this,root,"landscape/empty/repair","Repair existing assignments");
                UI.Source(this,repair,designerGroup,"profile","Shared profile");UI.Field(this,repair,designerGroup,"tiles","Terrain tiles");
                return true;
            }
            hooked=true; Undo.undoRedoPerformed+=OnDesignerUndo; AssemblyReloadEvents.beforeAssemblyReload+=Finish;
            SolLandscapeLifecycle.BeforeAssetSave+=Finish;
            EditorSceneManager.sceneSaving+=OnSaving;EditorSceneManager.sceneClosing+=OnClosing;EditorApplication.playModeStateChanged+=OnPlay;
            Watch(designerGroup); if(designerGroup.profile!=null) Watch(designerGroup.profile);
            var activities=new DropdownField("Activity",new List<string>{"Setup","Sculpt","Materials","Rules","Preview"},state.landscapeActivity);
            root.Add(activities);activities.RegisterValueChangedCallback(e=>{state.landscapeActivity=e.newValue;state.landscapeTool=0;context.RefreshLayout();});
            var status=UI.Help(root,"");Track(()=>{bool valid=designerGroup.Validate(out string reason);status.text=valid?"Connected landscape ready. Profile changes affect the group; brush strokes affect local terrain.":reason;});
            BuildReadiness(root);
            if(state.landscapeActivity=="Setup") { BuildSetup(root);return true; }
            var profile=designerGroup.profile;if(profile==null)return true;
            if(state.landscapeActivity=="Sculpt") { BuildBrush(root,true);return true; }
            if(state.landscapeActivity=="Preview") { BuildPreview(root);return true; }
            if(profile.Layers.Count==0)return true;
            state.landscapeLayer=Mathf.Clamp(state.landscapeLayer,0,profile.Layers.Count-1);
            var cards=UI.Row(root);cards.style.flexWrap=Wrap.Wrap;
            for(int i=0;i<profile.Layers.Count;i++)
            {
                int selected=i;var entry=profile.Layers[i];var card=new VisualElement();card.style.width=100;cards.Add(card);
                var image=new Image { image=entry.terrainLayer?.diffuseTexture,scaleMode=ScaleMode.ScaleToFit };image.style.height=64;card.Add(image);
                UI.Note(card,entry.ProtectAutomaticCoverage?"Automatic · Protected":entry.mode==SolLandscapeLayerMode.Auto?"Automatic":"Base-painted");
                UI.Button(card,(i==state.landscapeLayer?"● ":"")+(entry.terrainLayer!=null?entry.terrainLayer.name:"Unassigned"),()=>{state.landscapeLayer=selected;context.RefreshLayout();});
            }
            string prefix="layers.Array.data["+state.landscapeLayer+"].";
            if(state.landscapeActivity=="Materials")
            {
                BuildBrush(root,false);
                var appearance=UI.Section(root,"Appearance","Shared profile · "+profile.name);
                UI.Field(this,appearance,profile,prefix+"paintProtection","Paint protection");
                foreach(var spec in new[]{"textureSize|Texture size (m)","normalStrength|Normal strength","tint|Tint","tintStrength|Tint strength","adjustSmoothness|Adjust smoothness","smoothnessRemap|Smoothness range","adjustOcclusion|Adjust occlusion","occlusionStrength|Occlusion strength","macroVariation|Broad colour variation","mesoVariation|Local colour variation","sandResponse|Sand wetness response"})
                {var split=spec.Split('|');UI.Field(this,appearance,profile,prefix+split[0],split[1]);}
                var source=UI.Foldout(this,appearance,"landscape/designer/artwork","Artwork conversion · rebuild required");
                UI.Field(this,source,profile,prefix+"maskConvention","Source packing");UI.Field(this,source,profile,prefix+"heightTexture","Separate height (R)");
                UI.Note(source,"HDRP masks without a separate height use neutral 0.5. Blue detail masks are not height.");
            }
            else
            {
                var rules=UI.Section(root,"Automatic distribution","Shared profile · protected automatic coverage remains visible beneath local painting");
                UI.Field(this,rules,profile,prefix+"paintProtection","Paint protection");
                var presets=UI.Row(rules);foreach(string name in new[]{"Gentle ground","Exposed cliff","Shoreline"})UI.Button(presets,name,()=>{SolLandscapeAuthoring.ApplyRule(profile,state.landscapeLayer,name);designerGroup.Invalidate();context.RefreshLayout();});
                foreach(var field in new[]{"mode","ruleModel","autoWeight","slopeRange","slopeFeather","useAltitudeRange","altitudeReference","heightRange","altitudeFeather"}) UI.Field(this,rules,profile,prefix+field);
                var curve=new VisualElement();curve.style.height=85;curve.generateVisualContent+=ctx=>DrawCurve(ctx,profile.Layers[state.landscapeLayer]);rules.Add(curve);Track(()=>curve.MarkDirtyRepaint());
                var advanced=UI.Foldout(this,root,"landscape/designer/advanced","Advanced · legacy curves and projection");
                foreach(var field in new[]{"slopeCenter","slopeContrast","slopeInfluence","slopeBias","slopeCeiling","slopeCeilingFeather","heightBias","heightInfluence","cavityScale","cavityInfluence","stochasticTiling","triplanarProjection","weatherSnowSusceptibility","permanentSnowSusceptibility"})UI.Field(this,advanced,profile,prefix+field);
            }
            return true;
        }
        void OpenCreation(ElementaPanelContext context)
        {
            Finish();
            GameObject empty=designerGroup!=null && designerGroup.tiles.Count==0 && designerGroup.profile==null && designerGroup.transform.childCount==0 ? designerGroup.gameObject : null;
            if(empty==null && Selection.activeGameObject!=null && Selection.activeGameObject.transform.childCount==0 && Selection.activeGameObject.GetComponents<Component>().All(c=>c is Transform || c is SolLandscapeGroup))empty=Selection.activeGameObject;
            float elevation=context.State.selectedWaterBody!=null?context.State.selectedWaterBody.SurfaceLevel+5:0;
            SolLandscapeCreationWizard.Open(empty,elevation,(group,sculpt)=>
            {
                context.State.landscapeGroup=group;context.State.landscapeActivity=sculpt?"Sculpt":"Materials";context.State.landscapeTool=sculpt?5:1;
                context.State.landscapeLayer=0;context.State.landscapeBrush=null;context.State.landscapeBrushSize=20;context.State.landscapeBrushStrength=.2f;context.State.landscapeBrushHardness=.5f;
                context.RefreshLayout();
            });
        }
        void BuildReadiness(VisualElement root)
        {
            bool missing=designerGroup.tiles.Any(t=>t.terrain==null || t.terrain.terrainData==null);
            if(missing)UI.Help(root,"A tile reference is missing. In Setup, assign its Terrain and TerrainData before painting or reconnecting.");
            bool colliders=designerGroup.tiles.Any(t=>t.terrain!=null && (t.terrain.GetComponent<TerrainCollider>()==null || t.terrain.GetComponent<TerrainCollider>().terrainData!=t.terrain.terrainData));
            if(colliders)UI.Button(root,"Repair terrain colliders",()=>
            {
                Undo.IncrementCurrentGroup();int undo=Undo.GetCurrentGroup();
                foreach(var tile in designerGroup.tiles)if(tile.terrain!=null){var collider=tile.terrain.GetComponent<TerrainCollider>()??Undo.AddComponent<TerrainCollider>(tile.terrain.gameObject);Undo.RecordObject(collider,"Repair terrain collider");collider.terrainData=tile.terrain.terrainData;EditorUtility.SetDirty(collider);}
                Undo.CollapseUndoOperations(undo);Context.RefreshLayout();
            });
            if(!missing)
            {
                if(designerGroup.tiles.Any(t=>!t.terrain.terrainData.terrainLayers.SequenceEqual(designerGroup.profile.Layers.Select(e=>e.terrainLayer))))
                {
                    UI.Help(root,"Tile palettes do not match the profile. Repair creates terrain copies, preserves matching base-painted materials and sends unmatched coverage to the fallback. Original assets remain available for Undo.");
                    UI.Button(root,"Repair palette assignments",()=>{Finish();SolLandscapeAuthoring.RepairTilePalettes(designerGroup);Context.RefreshLayout();});
                }
                if(AssetDatabase.GetAssetPath(designerGroup.profile).StartsWith(SolLandscapeLibrary.Root+"/"))
                {
                    UI.Help(root,"This group uses a shared starter profile. Make an editable copy before changing its materials or rules.");
                    UI.Button(root,"Make profile independent",()=>{Finish();SolLandscapeAssetLocations.DuplicateProfile(designerGroup);Context.RefreshLayout();});
                }
                UI.Button(root,"Repair tile connections",()=>{foreach(var tile in designerGroup.tiles)Undo.RecordObject(tile.terrain,"Connect landscape tiles");SolLandscapeAuthoring.Connect(designerGroup);Context.RefreshLayout();});
                var actions=UI.Row(root);
                UI.Button(actions,"Start sculpting",()=>{Context.State.landscapeActivity="Sculpt";Context.State.landscapeTool=5;Context.RefreshLayout();},enabled:!colliders);
                UI.Button(actions,"Start painting",()=>{Context.State.landscapeActivity="Materials";Context.State.landscapeTool=1;Context.RefreshLayout();},enabled:!colliders);
            }
            if(designerGroup.profile.CSArray==null || designerGroup.profile.NOHArray==null)UI.Button(root,"Build missing artwork",()=>{Finish();SolLandscapeArrayBaker.BakeTarget(designerGroup.profile);designerGroup.Invalidate();Context.RefreshLayout();});
        }
        void BuildSetup(VisualElement root)
        {
            UI.Button(root,"Create landscape…",()=>OpenCreation(Context));
            UI.Button(root,"Reveal assets",()=>EditorGUIUtility.PingObject(designerGroup.assetOwner!=null?(UnityEngine.Object)designerGroup.assetOwner:designerGroup.profile));
            UI.Button(root,"Organise assets",()=>{Finish();SolLandscapeOrganisationWindow.Open(designerGroup);});
            if(designerGroup.assetOwner!=null)UI.Note(root,SolLandscapeAssetLocations.Root(designerGroup.assetOwner));
            UI.Source(this,root,designerGroup,"profile","Shared palette and rules");
            UI.Field(this,root,designerGroup,"tiles","Connected terrain tiles");
            UI.Field(this,root,designerGroup,"projectionOrigin","Shared texture origin");
            UI.Button(root,"Connect assigned tiles",()=>SolLandscapeAuthoring.Connect(designerGroup));
            var profile=designerGroup.profile;if(profile==null)return;
            UI.Button(root,"Duplicate profile for this group",()=>
            {
                Finish();SolLandscapeAssetLocations.DuplicateProfile(designerGroup);Context.RefreshLayout();
            });
            var preset=new ObjectField("Rule / appearance preset") { objectType=typeof(SolLandscapeProfile),allowSceneObjects=false };root.Add(preset);
            UI.Button(root,"Apply matching material settings",()=>
            {
                if(!(preset.value is SolLandscapeProfile source))return;Finish();Undo.RecordObject(profile,"Apply landscape settings preset");
                foreach(var entry in profile.Layers)
                {
                    var match=source.Layers.FirstOrDefault(e=>e.terrainLayer==entry.terrainLayer);if(match==null)continue;
                    string id=entry.materialId;var packing=entry.maskConvention;var height=entry.heightTexture;
                    JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(match),entry);entry.materialId=id;entry.maskConvention=packing;entry.heightTexture=height;
                }
                EditorUtility.SetDirty(profile);designerGroup.Invalidate();Context.RefreshLayout();
            });
            UI.Note(root,"Settings presets affect matching materials. Palette identities and local paint stay intact.");
            if(profile.Layers.Count>0)
            {
                var names=profile.Layers.Select(e=>e.terrainLayer!=null?e.terrainLayer.name:"Unassigned").ToList();
                var fallback=new DropdownField("Automatic fallback",names,profile.FallbackIndex);root.Add(fallback);
                fallback.RegisterValueChangedCallback(e=>{Undo.RecordObject(profile,"Change fallback material");var entry=profile.Layers[names.IndexOf(e.newValue)];profile.fallbackMaterialId=entry.materialId;entry.mode=SolLandscapeLayerMode.Auto;profile.preserveLegacyFallback=false;EditorUtility.SetDirty(profile);designerGroup.Invalidate();Context.RefreshLayout();});
                UI.Note(root,"Fallback ground cannot be removed or excluded. Choose another fallback here before using Remove material on it.");
            }
            UI.Field(this,root,profile,"colourVariation","Enable colour variation");UI.Field(this,root,profile,"macroScale","Broad variation size (m)");UI.Field(this,root,profile,"mesoScale","Local variation size (m)");
            UI.Field(this,root,profile,"stochasticTiling");UI.Field(this,root,profile,"triplanarProjection");UI.Field(this,root,profile,"heightBlend");
            UI.Field(this,root,profile,"heightTransition");UI.Field(this,root,profile,"artworkResolution");
            var rebuild=UI.Section(root,"Artwork");UI.Metric(this,rebuild,"Status",()=>SolLandscapeArrayBaker.GetStaleness(profile).Message);
            UI.Button(rebuild,"Rebuild terrain textures",()=>{Finish();SolLandscapeArrayBaker.BakeTarget(profile);designerGroup.Invalidate();Context.RefreshLayout();});
            UI.Note(root,"Paint storage: two RGBA maps per enabled channel set. At 512², overrides, exclusions and removals use up to 6 MiB per tile, plus GPU working copies and undo.");
            var sizes=new List<string>{"64","128","256","512","1024","2048"};if(!sizes.Contains(designerGroup.paintResolution.ToString()))sizes.Add(designerGroup.paintResolution.ToString());
            var resolution=new DropdownField("Paint resolution",sizes,designerGroup.paintResolution.ToString());root.Add(resolution);
            resolution.RegisterValueChangedCallback(e=>{Finish();Undo.RecordObject(designerGroup,"Resize paint maps");designerGroup.paintResolution=int.Parse(e.newValue);foreach(var tile in designerGroup.tiles)if(tile.paint!=null){Undo.RegisterCompleteObjectUndo(tile.paint,"Resize paint maps");tile.paint.Resize(designerGroup.paintResolution);EditorUtility.SetDirty(tile.paint);}designerGroup.Invalidate();});
            var palette=UI.Section(root,"Palette order");
            for(int i=0;i<profile.Layers.Count;i++)
            {int index=i;var row=UI.Row(palette);UI.Note(row,profile.Layers[i].terrainLayer!=null?profile.Layers[i].terrainLayer.name:"Unassigned");
                UI.Button(row,"↑",()=>{var entries=profile.Layers.ToList();(entries[index-1],entries[index])=(entries[index],entries[index-1]);SolLandscapeAuthoring.ChangePalette(designerGroup,entries);Context.RefreshLayout();},enabled:i>0);
                UI.Button(row,"Remove",()=>{var entries=profile.Layers.ToList();entries.RemoveAt(index);SolLandscapeAuthoring.ChangePalette(designerGroup,entries);Context.RefreshLayout();},enabled:profile.Layers.Count>1&&i!=profile.FallbackIndex);
            }
            var add=new ObjectField("Add library material") { objectType=typeof(TerrainLayer),allowSceneObjects=false };palette.Add(add);
            add.RegisterValueChangedCallback(e=>{if(e.newValue is TerrainLayer layer && profile.Layers.Count<8){var entries=profile.Layers.ToList();entries.Add(SolLandscapeLibrary.EntryFor(layer));SolLandscapeAuthoring.ChangePalette(designerGroup,entries);Context.RefreshLayout();}});
        }
        void BuildBrush(VisualElement root,bool sculpt)
        {
            var state=Context.State;var section=UI.Section(root,sculpt?"Sculpt terrain":"Local material editing");
            state.landscapeTool=Mathf.Clamp(state.landscapeTool,0,tools.Length-1);
            var choices=(sculpt?new[]{0,5,6,7,8,9}:new[]{0,1,10,11,2,3,4}).Select(i=>tools[i]).ToList();
            var tool=new DropdownField("Tool",choices,choices.Contains(tools[state.landscapeTool])?tools[state.landscapeTool]:"Inspect");section.Add(tool);
            tool.RegisterValueChangedCallback(e=>{Finish();state.landscapeTool=Array.IndexOf(tools,e.newValue);SceneView.RepaintAll();});
            if(!sculpt)
            {
                var protectedNotice=UI.Help(section,"");
                Track(()=>protectedNotice.text=designerGroup.profile.Layers[state.landscapeLayer].ProtectAutomaticCoverage
                    ?"This automatic material is protected. Its rule coverage cannot be painted over or excluded. Select grass/soil to paint around it, or set Paint protection to Off to unlock it."
                    :"Painting respects protected automatic rock coverage. On rocky slopes, your brush fills only the remaining coverage.");
                var actions=UI.Row(section);actions.style.flexWrap=Wrap.Wrap;
                foreach(int index in new[]{1,10,11})
                {
                    int selected=index;var action=UI.Button(actions,tools[index],()=>tool.value=tools[selected]);
                    if(selected==1||selected==10)Track(()=>action.SetEnabled(!designerGroup.profile.Layers[state.landscapeLayer].ProtectAutomaticCoverage && (selected!=10||state.landscapeLayer!=designerGroup.profile.FallbackIndex)));
                }
                var advanced=UI.Foldout(this,section,"landscape/brush/advanced","Advanced · overrides and automatic exclusions");
                foreach(int index in new[]{2,3,4}){int selected=index;UI.Button(advanced,tools[index],()=>tool.value=tools[selected]);}
                var instruction=UI.Help(section,"");
                Track(()=>instruction.text=state.landscapeTool==10
                    ?(state.landscapeLayer==designerGroup.profile.FallbackIndex?"Fallback ground cannot be removed. Choose another Automatic fallback in Setup first.":"Remove material: drag in Scene view to suppress this material from base terrain paint, automatic rules and overrides. Protected rock remains visible. This preserves the original data.")
                    :state.landscapeTool==11?"Restore removed material: drag to reveal this material's previous coverage. Painting it also clears removal under your brush."
                    :state.landscapeTool==2
                    ?"Clear painted overrides: drag in Scene view to remove local overrides. This does not remove base terrain paint. To remove a path from any source, select Path and use Remove material."
                    :state.landscapeTool==3?"Exclude material: suppress this material's automatic rule locally. Explicit paint still wins; the fallback cannot be excluded."
                    :state.landscapeTool==4?"Restore excluded: drag to remove the selected material's exclusion mask."
                    :"Paint material: select a material card, choose Paint material, then drag in Scene view. Works for unlocked Auto and Manual materials; protected rock coverage remains visible.");
            }
            Slider("Brush diameter (m)",.5f,200,state.landscapeBrushSize,v=>state.landscapeBrushSize=v,section);
            Slider("Strength",.01f,1,state.landscapeBrushStrength,v=>state.landscapeBrushStrength=v,section);
            Slider("Hardness",0,.99f,state.landscapeBrushHardness,v=>state.landscapeBrushHardness=v,section);
            Slider("Rotation",0,360,state.landscapeBrushRotation,v=>state.landscapeBrushRotation=v,section);
            UI.Note(section,"Drag in Scene view. Escape cancels the stroke. Flatten uses the height at stroke start. Alt keeps camera navigation available.");
            UI.Button(section,"Soft round brush",()=>{state.landscapeBrush=null;state.landscapeBrushHardness=.5f;state.landscapeBrushRotation=0;Context.RefreshLayout();});
            var search=new TextField("Search brushes");section.Add(search);var category=new DropdownField("Category",new List<string>{"All","Mountain","Ridge","Slope","Plateau","Canyon","Utility"},0);section.Add(category);
            var gallery=UI.Row(section);gallery.style.flexWrap=Wrap.Wrap;
            void Populate()
            {
                gallery.Clear();
                foreach(var guid in AssetDatabase.FindAssets("t:Brush",new[]{"Assets/Earth-Sky-Water/Landscape/Editor/Brushes"}))
                {
                    var asset=AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(AssetDatabase.GUIDToAssetPath(guid));string name=asset.name;
                    if(!string.IsNullOrEmpty(search.value)&&name.IndexOf(search.value,StringComparison.OrdinalIgnoreCase)<0)continue;
                    bool known=new[]{"Mountain","Ridge","Slope","Plateau","Canyon"}.Any(c=>name.IndexOf(c,StringComparison.OrdinalIgnoreCase)>=0);
                    if(category.value!="All"&&(category.value=="Utility"?known:name.IndexOf(category.value,StringComparison.OrdinalIgnoreCase)<0))continue;
                    var button=new Button(()=>{state.landscapeBrush=asset;}){tooltip=name};button.style.width=60;button.style.height=60;
                    var image=new Image { image=BrushTexture(asset),scaleMode=ScaleMode.ScaleToFit };image.style.flexGrow=1;button.Add(image);gallery.Add(button);
                }
            }
            search.RegisterValueChangedCallback(_=>Populate());category.RegisterValueChangedCallback(_=>Populate());Populate();
        }
        static Texture BrushTexture(UnityEngine.Object asset) => asset==null?Texture2D.whiteTexture:new SerializedObject(asset).FindProperty("m_Mask")?.objectReferenceValue as Texture ?? Texture2D.whiteTexture;
        static void Slider(string title,float min,float max,float value,Action<float> change,VisualElement root)
        {var slider=new Slider(title,min,max){value=value,showInputField=true};root.Add(slider);slider.RegisterValueChangedCallback(e=>change(e.newValue));}
        void BuildPreview(VisualElement root)
        {
            if(designerGroup.profile.Layers.Count==0){UI.Help(root,"Add a material in Setup before previewing coverage.");return;}
            var names=designerGroup.profile.Layers.Select(e=>e.terrainLayer!=null?e.terrainLayer.name:"Unassigned").ToList();
            var selected=new DropdownField("Material",names,Mathf.Clamp(Context.State.landscapeLayer,0,names.Count-1));root.Add(selected);
            selected.RegisterValueChangedCallback(e=>{Context.State.landscapeLayer=names.IndexOf(e.newValue);designerGroup.SetPreview(this,designerGroup.PreviewSnow,designerGroup.PreviewWetness,designerGroup.DebugMode,Context.State.landscapeLayer);designerGroup.Publish();});
            var options=new List<string>{"Off","Final material weight","Manual / automatic","Procedural weight","Snow coverage","Automatic material","Painted override","Local exclusion"};
            var debug=new DropdownField("View",options,0);root.Add(debug);debug.RegisterValueChangedCallback(e=>{designerGroup.SetPreview(this,designerGroup.PreviewSnow,designerGroup.PreviewWetness,options.IndexOf(e.newValue),Context.State.landscapeLayer);designerGroup.Publish();});
            Slider("Snow cover",0,1,0,v=>{designerGroup.SetPreview(this,v,designerGroup.PreviewWetness,designerGroup.DebugMode,Context.State.landscapeLayer);designerGroup.Publish();},root);
            Slider("Ground wetness",0,1,0,v=>{designerGroup.SetPreview(this,designerGroup.PreviewSnow,v,designerGroup.DebugMode,Context.State.landscapeLayer);designerGroup.Publish();},root);
            UI.Button(root,"End landscape preview",()=>{designerGroup.ClearPreview(this);designerGroup.Publish();});
            UI.Note(root,"Temporary preview for this landscape group. Environment simulation and saved profiles remain authoritative when preview ends.");
        }
        static void DrawCurve(MeshGenerationContext context,SolLandscapeLayerEntry entry)
        {
            var painter=context.painter2D;var rect=context.visualElement.contentRect;painter.strokeColor=new Color(.3f,.75f,.45f);painter.lineWidth=2;painter.BeginPath();
            for(int i=0;i<=90;i++)
            {float response=entry.ruleModel==SolLandscapeRuleModel.Ranges?SolLandscapeProfile.RangeResponse(i,entry.slopeRange,entry.slopeFeather):LegacyCurve(i,entry);
                var point=new Vector2(i/90f*rect.width,(1-response)*(rect.height-8)+4);if(i==0)painter.MoveTo(point);else painter.LineTo(point);}
            painter.Stroke();
        }
        static float LegacyCurve(float degrees,SolLandscapeLayerEntry e)
        {float t=Mathf.SmoothStep(0,1,(degrees-e.slopeCenter)/Mathf.Max(.01f,e.slopeContrast)+.5f);t=Mathf.Clamp01(t+e.slopeBias*t*(1-t));return Mathf.Lerp(1,e.slopeInfluence>=0?t:1-t,Mathf.Abs(e.slopeInfluence));}
        public override void DrawSceneGui(ElementaPanelContext context,SceneView view)
        {
            if(designerGroup==null||Application.isPlaying)return;var state=context.State;var evt=Event.current;
            if(evt.type==EventType.KeyDown&&(evt.control||evt.command)&&evt.keyCode==KeyCode.Z){Finish();return;}
            if(evt.type==EventType.KeyDown&&evt.keyCode==KeyCode.Escape){Cancel();evt.Use();return;}
            int control=GUIUtility.GetControlID("Elementa Landscape brush".GetHashCode(),FocusType.Passive);
            if(evt.rawType==EventType.MouseUp&&evt.button==0&&undoGroup>=0){Finish();evt.Use();return;}
            if(evt.type==EventType.ValidateCommand && evt.commandName=="UndoRedoPerformed") { Finish(); return; }
            Ray ray=HandleUtility.GUIPointToWorldRay(evt.mousePosition);RaycastHit hit=default;Terrain terrain=null;float distance=float.MaxValue;
            foreach(var tile in designerGroup.tiles){var collider=tile.terrain!=null?tile.terrain.GetComponent<TerrainCollider>():null;if(collider!=null&&collider.Raycast(ray,out var candidate,100000)&&candidate.distance<distance){hit=candidate;terrain=tile.terrain;distance=hit.distance;}}
            if(terrain==null)return;
            if(state.landscapeProbe)Handles.Label(hit.point+Vector3.up,$"{terrain.name} · {hit.point.y:F1} m · {Vector3.Angle(hit.normal,Vector3.up):F1}°");
            if(state.landscapeTool==0||evt.alt)return;
            if(evt.type==EventType.Layout)HandleUtility.AddDefaultControl(control);
            Handles.color=new Color(.4f,.85f,.5f,.9f);Handles.DrawWireDisc(hit.point,hit.normal,state.landscapeBrushSize*.5f);view.Repaint();
            if(evt.button!=0||(evt.type!=EventType.MouseDown&&evt.type!=EventType.MouseDrag))return;
            if(!designerGroup.Validate(out string reason)){view.ShowNotification(new GUIContent(reason));return;}
            if(undoGroup<0)
            {
                if(evt.type!=EventType.MouseDown)return;
                try{BeginEditorStroke(terrain,hit.point);GUIUtility.hotControl=control;}catch(Exception error){Cancel();view.ShowNotification(new GUIContent(error.Message));return;}
            }
            if(evt.type==EventType.MouseDrag&&Vector3.Distance(lastDab,hit.point)<state.landscapeBrushSize*.05f)return;
            try {ApplyEditorStroke(terrain,hit.point);}catch(Exception error){Cancel();view.ShowNotification(new GUIContent(error.Message));evt.Use();return;}
            evt.Use();EditorApplication.QueuePlayerLoopUpdate();SceneView.RepaintAll();
        }
        // Shared editor brush operations serve both Scene-view input and deterministic acceptance checks.
        internal void BeginEditorStroke(Terrain terrain,Vector3 point)
        {
            if(undoGroup>=0)throw new InvalidOperationException("Finish the current stroke first.");
            if(designerGroup==null || !designerGroup.Validate(out _))throw new InvalidOperationException("Repair the landscape setup before editing.");
            var state=Context.State;
                Undo.IncrementCurrentGroup();undoGroup=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Landscape stroke");flattenHeight=point.y;lastDab=point;undoPaint.Clear();
                if(state.landscapeTool<=4||state.landscapeTool>=10)
                {
                    stroke=new SolLandscapeStroke();stroke.BeforeTileChange+=tile=>{SolLandscapeAuthoring.EnsurePaintAsset(designerGroup,tile);if(undoPaint.Add(tile.paint)){Undo.RegisterCompleteObjectUndo(tile.paint,"Landscape stroke");EditorUtility.SetDirty(tile.paint);}};
                    stroke.Committed+=data=>EditorUtility.SetDirty(data);
                    stroke.BeginStroke(designerGroup,(state.landscapeTool>=10?(SolLandscapePaintOperation)(state.landscapeTool-6):(SolLandscapePaintOperation)(state.landscapeTool-1)),state.landscapeLayer);
                }
                else foreach(var tile in designerGroup.tiles)Undo.RegisterCompleteObjectUndo(tile.terrain.terrainData,"Landscape stroke");
        }
        internal void ApplyEditorStroke(Terrain terrain,Vector3 point)
        {
            if(undoGroup<0)throw new InvalidOperationException("Begin the editor stroke first.");
            var state=Context.State;
            int steps=Mathf.Max(1,Mathf.CeilToInt(Vector3.Distance(lastDab,point)/Mathf.Max(.025f,state.landscapeBrushSize*.05f)));
            for(int step=1;step<=steps;step++)
            {
                var dab=Vector3.Lerp(lastDab,point,(float)step/steps);
                if(stroke!=null)stroke.ApplyDab(dab,state.landscapeBrushSize*.5f,state.landscapeBrushStrength,state.landscapeBrushHardness,BrushTexture(state.landscapeBrush),state.landscapeBrushRotation);
                else Sculpt(terrain,dab);
            }
            lastDab=point;
        }
        void Sculpt(Terrain terrain,Vector3 point)
        {
            var s=Context.State;var size=terrain.terrainData.size;var delta=point-terrain.transform.position;
            var transform=TerrainPaintUtility.CalculateBrushTransform(terrain,new Vector2(delta.x/size.x,delta.z/size.z),s.landscapeBrushSize,s.landscapeBrushRotation);
            var paint=TerrainPaintUtility.BeginPaintHeightmap(terrain,transform.GetBrushXYBounds(),1);
            sculptMaterial??=new Material(Shader.Find("Hidden/Sol/Landscape Sculpt")){hideFlags=HideFlags.HideAndDontSave};
            TerrainPaintUtility.SetupTerrainToolMaterialProperties(paint,transform,sculptMaterial);sculptMaterial.SetTexture("_BrushTex",BrushTexture(s.landscapeBrush));
            float strength=s.landscapeBrushStrength;if(s.landscapeTool==5||s.landscapeTool==6||s.landscapeTool==9)strength*=s.landscapeBrushSize/size.y*.05f;
            if(s.landscapeTool==6)strength=-strength;
            sculptMaterial.SetVector("_Sculpt",new Vector4(strength,(flattenHeight-paint.heightWorldSpaceMin)/paint.heightWorldSpaceSize*.5f,s.landscapeTool<=6?0:s.landscapeTool==7?1:s.landscapeTool==8?2:3,s.landscapeBrushHardness));
            Graphics.Blit(paint.sourceRenderTexture,paint.destinationRenderTexture,sculptMaterial);TerrainPaintUtility.EndPaintHeightmap(paint,"Landscape stroke");
        }
        internal void Finish()
        {
            var pending=stroke;stroke=null;int transaction=undoGroup;undoGroup=-1;
            try {pending?.CommitStroke();}
            finally {pending?.Dispose();PaintContext.ApplyDelayedActions();if(transaction>=0){GUIUtility.hotControl=0;Undo.CollapseUndoOperations(transaction);}}
        }
        void Cancel()
        {
            var pending=stroke;stroke=null;int transaction=undoGroup;undoGroup=-1;
            try {pending?.CancelStroke();}
            finally {pending?.Dispose();PaintContext.ApplyDelayedActions();if(transaction>=0){GUIUtility.hotControl=0;Undo.RevertAllDownToGroup(transaction);}}
        }
        void OnDesignerUndo()
        {
            var pending=stroke;stroke=null;if(undoGroup>=0)GUIUtility.hotControl=0;undoGroup=-1;
            pending?.DiscardAfterExternalRestore();pending?.Dispose();
            if(designerGroup==null)return;foreach(var tile in designerGroup.tiles)tile.paint?.RefreshTextures();designerGroup.Invalidate();designerGroup.Publish();SceneView.RepaintAll();
        }
        void OnSaving(Scene scene,string path)=>Finish();
        void OnClosing(Scene scene,bool removing){Finish();designerGroup?.ClearPreview(this);}
        void OnPlay(PlayModeStateChange state){Finish();designerGroup?.ClearPreview(this);}
        public override void Dispose()
        {
            Finish();designerGroup?.ClearPreview(this);
            if(hooked){Undo.undoRedoPerformed-=OnDesignerUndo;AssemblyReloadEvents.beforeAssemblyReload-=Finish;EditorSceneManager.sceneSaving-=OnSaving;EditorSceneManager.sceneClosing-=OnClosing;EditorApplication.playModeStateChanged-=OnPlay;hooked=false;}
            SolLandscapeLifecycle.BeforeAssetSave-=Finish;
            if(sculptMaterial!=null)UnityEngine.Object.DestroyImmediate(sculptMaterial);sculptMaterial=null;designerGroup=null;base.Dispose();
        }
    }
}

