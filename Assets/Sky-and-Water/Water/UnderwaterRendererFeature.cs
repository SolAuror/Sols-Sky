using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

/// <summary>
/// ---------------------------------------------------------------------------
/// UNDERWATER RENDERER FEATURE  (URP 17 / Unity 6, Render Graph)
/// ---------------------------------------------------------------------------
///
/// Performs the fullscreen blit that applies Sol.UnderwaterOverlay to the
/// camera colour texture whenever _UnderwaterFactor > 0.
///
/// Uses the same copy-then-blit-back pattern as URP's built-in
/// FullScreenPassRendererFeature (proven RG-safe approach).
///
/// SETUP:
///   1.  Open your URP Renderer Asset (Project > Assets > Settings > ...).
///   2.  Add Renderer Feature > "Underwater Renderer Feature" from the list.
///   3.  Create a new Material using Sol/UnderwaterOverlay shader and assign
///       it to the "Overlay Material" field.
///   4.  Add UnderwaterVolumeController to your camera/player prefab.
/// ---------------------------------------------------------------------------
/// </summary>
[System.Serializable]
public class UnderwaterRendererFeature : ScriptableRendererFeature
{
    [Tooltip("Material created from Sol/UnderwaterOverlay shader.")]
    public Material overlayMaterial;

    [Tooltip("Print a debug message to the console when the blit executes.")]
    public bool debugLog = false;

    UnderwaterBlitPass _pass;

    // -----------------------------------------------------------------------
    //  ScriptableRendererFeature API
    // -----------------------------------------------------------------------

    public override void Create()
    {
        _pass = new UnderwaterBlitPass
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (overlayMaterial == null)
        {
            if (debugLog) Debug.LogWarning("[UnderwaterFeature] No overlay material assigned.");
            return;
        }

        _pass.requiresIntermediateTexture = true;
        _pass.SetMaterial(overlayMaterial, debugLog);
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        _pass = null;
    }

    // -----------------------------------------------------------------------
    //  Inner render pass - mirrors FullScreenPassRendererFeature's pattern
    // -----------------------------------------------------------------------

    class UnderwaterBlitPass : ScriptableRenderPass
    {
        static readonly int _SID_UnderwaterFactor = Shader.PropertyToID("_UnderwaterFactor");

        Material _material;
        bool _debugLog;

        public void SetMaterial(Material mat, bool debug)
        {
            _material = mat;
            _debugLog = debug;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_material == null) return;

            float factor = Shader.GetGlobalFloat(_SID_UnderwaterFactor);

            // Skip the blit entirely when above water for zero GPU cost.
            if (factor < 0.002f)
            {
                if (_debugLog) Debug.Log($"[UnderwaterFeature] Skipping blit: _UnderwaterFactor={factor:F4}");
                return;
            }

            var resourceData = frameData.Get<UniversalResourceData>();

            if (resourceData.isActiveTargetBackBuffer)
            {
                Debug.LogWarning("[UnderwaterFeature] Cannot blit: active target is back buffer.");
                return;
            }

            // ---- Step 1: Copy active colour into a temp texture ----
            TextureHandle activeColor = resourceData.activeColorTexture;
            var copyDesc = renderGraph.GetTextureDesc(activeColor);
            copyDesc.name        = "_UnderwaterColorCopy";
            copyDesc.clearBuffer = false;
            TextureHandle copiedColor = renderGraph.CreateTexture(copyDesc);

            renderGraph.AddBlitPass(activeColor, copiedColor, Vector2.one, Vector2.zero,
                passName: "Underwater Copy Color");

            // ---- Step 2: Blit from copy back into active colour with overlay material ----
            var blitParams = new RenderGraphUtils.BlitMaterialParameters(
                copiedColor, activeColor, _material, 0);
            renderGraph.AddBlitPass(blitParams, passName: "Underwater Overlay Blit");

            if (_debugLog) Debug.Log($"[UnderwaterFeature] Blit executed. factor={factor:F3}");
        }
    }
}
