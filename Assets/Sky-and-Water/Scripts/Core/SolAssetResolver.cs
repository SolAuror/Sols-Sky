using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Sol.Environment
{
    /// <summary>
    /// Locates Sol's shaders and compute shaders without depending on them living in a
    /// <c>Resources</c> folder.
    ///
    /// These assets used to be under <c>Resources/</c> and were fetched with
    /// <see cref="Resources.Load"/>. They no longer are, so those calls returned null and
    /// every consumer silently fell back to doing nothing. Serialized references are the
    /// primary path now, because they are also what guarantees the asset survives into a
    /// build; the lookups here exist to repair a missing reference rather than to replace
    /// it.
    /// </summary>
    public static class SolAssetResolver
    {
        /// <summary>
        /// Returns <paramref name="serialized"/> when assigned, otherwise finds the shader
        /// by its declared name. <see cref="Shader.Find"/> works from any folder, but only
        /// for shaders already included in the build, so an editor-side asset search backs
        /// it up and lets the caller re-serialize the result.
        /// </summary>
        public static Shader ResolveShader(Shader serialized, string declaredName,
            string assetFileName = null)
        {
            if (serialized != null)
                return serialized;

            Shader found = string.IsNullOrEmpty(declaredName)
                ? null
                : Shader.Find(declaredName);
#if UNITY_EDITOR
            if (found == null && !string.IsNullOrEmpty(assetFileName))
                found = FindByName<Shader>(assetFileName);
#endif
            if (found == null)
                Debug.LogWarning($"[Sol] Could not resolve shader '{declaredName}'. " +
                    "Assign it explicitly so it is included in builds.");
            return found;
        }

        /// <summary>
        /// Compute shaders have no equivalent of <see cref="Shader.Find"/>, so outside the
        /// editor the serialized reference is the only option. The editor search exists so
        /// an unassigned field repairs itself and can be saved.
        /// </summary>
        public static ComputeShader ResolveCompute(ComputeShader serialized,
            string assetFileName)
        {
            if (serialized != null)
                return serialized;

            ComputeShader found = null;
#if UNITY_EDITOR
            found = FindByName<ComputeShader>(assetFileName);
#endif
            if (found == null)
                Debug.LogWarning($"[Sol] Could not resolve compute shader '{assetFileName}'. " +
                    "Assign it explicitly; compute shaders cannot be located by name at runtime.");
            return found;
        }

#if UNITY_EDITOR
        static T FindByName<T>(string assetFileName) where T : Object
        {
            string[] guids = AssetDatabase.FindAssets(
                $"{assetFileName} t:{typeof(T).Name}");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(path) != assetFileName)
                    continue;
                T asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (asset != null)
                    return asset;
            }
            return null;
        }
#endif
    }
}
