using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Sol.Landscape
{
    public enum SolLandscapeDebugView { Off, FinalWeight, ManualAutoSplit, ProceduralWeight, SnowCoverage, Automatic, Overrides, Exclusions }
    [CreateAssetMenu(menuName = "Sol/Landscape/Shared Profile")]
    public sealed class SolLandscapeProfile : SolLandscapeConfig
    {
        public const int Capacity = 8;
        public int version = 1;
        public SolLandscapeAsset assetOwner;
        public string fallbackMaterialId;
        public bool preserveLegacyFallback;
        public bool stochasticTiling = true;
        public bool triplanarProjection = true;
        public bool heightBlend = true;
        public bool colourVariation;
        [Min(1)] public float macroScale = 120;
        [Min(1)] public float mesoScale = 20;
        public int artworkResolution = 2048;
        [HideInInspector] public Shader terrainShader;
        [HideInInspector] public Shader paintShader;
        [SerializeField, HideInInspector] List<string> bakedMaterialIds = new();
        public int SliceFor(int index) => bakedMaterialIds.Count == 0 ? index : bakedMaterialIds.IndexOf(Layers[index].materialId);
        public void RecordPaletteBake() => bakedMaterialIds = Layers.Select(e => e.materialId).ToList();
        public int FallbackIndex
        {
            get { for (int i = 0; i < Layers.Count; i++) if (Layers[i].materialId == fallbackMaterialId) return i; return 0; }
        }
        public void EnsureIds()
        {
            foreach (var layer in Layers)
                if (string.IsNullOrEmpty(layer.materialId)) layer.materialId = Guid.NewGuid().ToString("N");
            if (string.IsNullOrEmpty(fallbackMaterialId) && Layers.Count > 0) fallbackMaterialId = Layers[0].materialId;
            if (bakedMaterialIds.Count == 0 && CSArray != null) RecordPaletteBake();
        }
        public static float RangeResponse(float value, Vector2 range, float feather)
        {
            feather = Mathf.Max(feather, .01f);
            return Mathf.SmoothStep(0, 1, (value - range.x + feather) / feather)
                * (1 - Mathf.SmoothStep(0, 1, (value - range.y) / feather));
        }
    }
}
