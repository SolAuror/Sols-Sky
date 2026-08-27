using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace Sol.Landscape.Diagnostics.Editor
{
    /// <summary>Ticket 5F.2 quick compile sanity check for _SOL_LANDSCAPE_STOCHASTIC before investing in the full capture/measurement pipeline.</summary>
    public static class SolLandscape5F2ShaderCheck
    {
        private const string ArrayMaterialPath = "Assets/Sky-and-Water/Landscape/M_SolLandscape.mat";
        private const string StochasticKeyword = "_SOL_LANDSCAPE_STOCHASTIC";

        public static void CheckFromCommandLine()
        {
            int exitCode = 1;
            try
            {
                Material production = AssetDatabase.LoadAssetAtPath<Material>(ArrayMaterialPath);
                if (production == null || production.shader == null)
                    throw new InvalidOperationException("The production array terrain material is missing.");

                Shader shader = production.shader;

                Material stochasticOnly = new Material(production) { hideFlags = HideFlags.HideAndDontSave };
                stochasticOnly.EnableKeyword(StochasticKeyword);

                ShaderMessage[] baseMessages = ShaderUtil.GetShaderMessages(shader);
                int errors = baseMessages.Count(m => m.severity == ShaderCompilerMessageSeverity.Error);
                int warnings = baseMessages.Count(m => m.severity == ShaderCompilerMessageSeverity.Warning);

                Debug.Log($"[Sol Landscape 5F.2] Shader.isSupported={shader.isSupported}; Errors={errors}; Warnings={warnings}");
                foreach (ShaderMessage message in baseMessages)
                    Debug.Log($"[Sol Landscape 5F.2] {message.severity}: {message.message} ({message.file}:{message.line})");

                UnityEngine.Object.DestroyImmediate(stochasticOnly);

                exitCode = (errors == 0 && shader.isSupported) ? 0 : 1;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                exitCode = 1;
            }
            finally
            {
                EditorApplication.Exit(exitCode);
            }
        }
    }
}
