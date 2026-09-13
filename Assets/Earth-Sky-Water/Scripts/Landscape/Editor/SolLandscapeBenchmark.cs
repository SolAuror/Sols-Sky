using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace Sol.Landscape.Editor
{
    /// <summary>Batch-only measurement on transient clones of the shipped example.</summary>
    public static class SolLandscapeBenchmark
    {
        static SolLandscapeGroup group;
        static SolLandscapeLayerEntry[] layers;
        static Camera camera;
        static RenderTexture target;
        static Recorder recorder;
        static int scenario=-1,frame;
        static readonly List<double> cpu=new(),gpu=new();
        static readonly List<string> lines=new();
        static readonly List<UnityEngine.Object> clones=new();
        static double dabMs,commitMs;
        static bool previousProfiler,previousEditorProfiling,previousGpuProfiling;
        public static void Start()
        {
            if(!Application.isBatchMode)throw new InvalidOperationException("Run this benchmark in the separate batch validation project.");
            previousProfiler=Profiler.enabled;previousEditorProfiling=UnityEditorInternal.ProfilerDriver.profileEditor;
            previousGpuProfiling=Profiler.GetAreaEnabled(ProfilerArea.GPU);
            Profiler.enabled=true;
            UnityEditorInternal.ProfilerDriver.profileEditor=true;
            UnityEditorInternal.ProfilerDriver.SetAreaEnabled(ProfilerArea.GPU,true);
            EditorSceneManager.OpenScene(SolLandscapeValidation.Folder+"/Landscape Designer.unity");group=UnityEngine.Object.FindFirstObjectByType<SolLandscapeGroup>();group.SuspendBindings();
            group.profile=UnityEngine.Object.Instantiate(group.profile);clones.Add(group.profile);layers=group.profile.Layers.ToArray();
            foreach(var tile in group.tiles)
            {
                tile.terrain.terrainData=UnityEngine.Object.Instantiate(tile.terrain.terrainData);clones.Add(tile.terrain.terrainData);
                tile.terrain.GetComponent<TerrainCollider>().terrainData=tile.terrain.terrainData;
                tile.paint=ScriptableObject.CreateInstance<SolLandscapePaintData>();tile.paint.Initialize(group.paintResolution);clones.Add(tile.paint);
            }
            camera=UnityEngine.Object.FindFirstObjectByType<Camera>();target=new RenderTexture(1280,800,24);camera.targetTexture=target;
            lines.Clear();lines.Add("# "+SystemInfo.graphicsDeviceName+" | "+SystemInfo.graphicsDeviceVersion+" | GPU recorder support: "+SystemInfo.supportsGpuRecorder);
            lines.Add("layers,stochastic,triplanar,painted,removal,cpu_render_ms,gpu_camera_ms,dab_ms,commit_ms,paint_cpu_bytes,idle_writes");
            scenario=-1;Next();EditorApplication.update+=Tick;
        }
        static void Next()
        {
            scenario++;frame=0;cpu.Clear();gpu.Clear();if(scenario==32){Finish();return;}
            int count=(scenario&8)==0?6:8;group.profile.SetLayers(layers.Take(count));group.profile.stochasticTiling=(scenario&1)!=0;group.profile.triplanarProjection=(scenario&2)!=0;
            foreach(var entry in group.profile.Layers){entry.stochasticTiling=group.profile.stochasticTiling;entry.triplanarProjection=group.profile.triplanarProjection;}
            foreach(var tile in group.tiles)
            {
                var data=tile.terrain.terrainData;data.terrainLayers=group.profile.Layers.Select(e=>e.terrainLayer).ToArray();
                var weights=new float[data.alphamapHeight,data.alphamapWidth,count];for(int y=0;y<data.alphamapHeight;y++)for(int x=0;x<data.alphamapWidth;x++)weights[y,x,group.profile.FallbackIndex]=1;data.SetAlphamaps(0,0,weights);
                tile.paint.Restore(new byte[6][]);
            }
            dabMs=commitMs=0;
            if((scenario&4)!=0)
            {
                using var stroke=new SolLandscapeStroke();stroke.BeginStroke(group,SolLandscapePaintOperation.Paint,1);
                var watch=Stopwatch.StartNew();stroke.ApplyDab(new Vector3(128,0,128),20,.8f);watch.Stop();dabMs=watch.Elapsed.TotalMilliseconds;
                watch.Restart();stroke.CommitStroke();watch.Stop();commitMs=watch.Elapsed.TotalMilliseconds;
            }
            if((scenario&16)!=0)
            {
                using var stroke=new SolLandscapeStroke();stroke.BeginStroke(group,SolLandscapePaintOperation.RemoveMaterial,1);
                var watch=Stopwatch.StartNew();stroke.ApplyDab(new Vector3(128,0,128),20,.8f);watch.Stop();dabMs+=watch.Elapsed.TotalMilliseconds;
                watch.Restart();stroke.CommitStroke();watch.Stop();commitMs+=watch.Elapsed.TotalMilliseconds;
            }
            group.Invalidate();group.Publish();
        }
        static void Tick()
        {
            try
            {
                if(scenario>=32)return;
                if(recorder!=null&&recorder.isValid&&frame>12&&recorder.gpuElapsedNanoseconds>0)gpu.Add(recorder.gpuElapsedNanoseconds/1e6);
                var watch=Stopwatch.StartNew();camera.Render();watch.Stop();
                if(recorder==null||!recorder.isValid){recorder=Recorder.Get("UniversalRenderPipeline.RenderSingleCameraInternal: "+camera.name);recorder.enabled=true;}
                group.Publish();if(frame>12)cpu.Add(watch.Elapsed.TotalMilliseconds);
                frame++;if(frame<40)return;
                string F(double value)=>value.ToString("F3",CultureInfo.InvariantCulture);
                lines.Add($"{group.profile.Layers.Count},{group.profile.stochasticTiling},{group.profile.triplanarProjection},{(scenario&4)!=0},{(scenario&16)!=0},{F(cpu.Average())},{(gpu.Count>0?F(gpu.Average()):"unavailable")},{F(dabMs)},{F(commitMs)},{group.tiles.Sum(t=>t.paint.StorageBytes)},{group.LastPublishWriteCount}");Next();
            }
            catch(Exception error){File.WriteAllText(Path.GetFullPath("../landscape-benchmark-error.txt"),error.ToString());EditorApplication.update-=Tick;RestoreProfiler();EditorApplication.Exit(1);}
        }
        static void RestoreProfiler(){UnityEditorInternal.ProfilerDriver.SetAreaEnabled(ProfilerArea.GPU,previousGpuProfiling);UnityEditorInternal.ProfilerDriver.profileEditor=previousEditorProfiling;Profiler.enabled=previousProfiler;}
        static void Finish()
        {
            EditorApplication.update-=Tick;if(recorder!=null)recorder.enabled=false;
            File.WriteAllLines(Path.GetFullPath("../landscape-benchmark.csv"),lines);camera.targetTexture=null;UnityEngine.Object.DestroyImmediate(target);
            group.SuspendBindings();RestoreProfiler();EditorApplication.Exit(0);
        }
    }
}
