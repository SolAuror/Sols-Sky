using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// One-shot deterministic migration from the inline WeatherProfile YAML used before
/// Phase 5 to reusable SolWeatherProfileAsset objects plus scene-local selection weights.
/// The parser reads the source YAML before Unity discards fields no longer present on the
/// runtime selection type, so no presentation value is reconstructed from defaults.
/// </summary>
public static class SolWeatherProfileMigration
{
    public const string ProfileFolder = "Assets/Earth-Sky-Water/Weather Profiles";

    static readonly string[] ScenePaths =
    {
        "Assets/Scenes/Sc_Sols_FiniteBodies.unity",
        "Assets/Scenes/Sc_Sols_Landscape.unity",
        "Assets/Scenes/Sols_Water2_Demo.unity",
    };

    const string WeatherManagerScriptGuid = "73f599d83cb25b048a81f2549fd3490a";

    sealed class LegacyProfile
    {
        internal string Name;
        internal float Cloudiness;
        internal float CloudErosion;
        internal float RainIntensity;
        internal float WindStrength;
        internal float FogBoost;
        internal float Mistiness;
        internal float SkyObscuration;
        internal float LightScattering;
        internal float Dim;
        internal float WaveSpeedMultiplier;
        internal float WaterTurbulence;
        internal bool Lightning;
        internal float LightningIntensity;
        internal float Weight = 1f;
        internal float SpringWeight = 1f;
        internal float SummerWeight = 1f;
        internal float AutumnWeight = 1f;
        internal float WinterWeight = 1f;
    }

    [MenuItem("Sol/Weather/Migrate Inline Profiles To Assets")]
    public static void MigrateInlineProfilesToAssets()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        string restoreScene = SceneManager.GetActiveScene().path;
        MigrateAll();
        if (!string.IsNullOrEmpty(restoreScene) && File.Exists(restoreScene))
            EditorSceneManager.OpenScene(restoreScene, OpenSceneMode.Single);
    }

    /// <summary>Non-interactive entry point used by batch-mode verification.</summary>
    public static void MigrateFromCommandLine()
    {
        MigrateAll();
    }

    static void MigrateAll()
    {
        var legacyByScene = new Dictionary<string, List<LegacyProfile>>(StringComparer.Ordinal);
        List<LegacyProfile> canonical = null;

        foreach (string scenePath in ScenePaths)
        {
            List<LegacyProfile> parsed = ParseLegacyProfiles(scenePath);
            legacyByScene.Add(scenePath, parsed);
            if (parsed.Count == 0)
                continue;

            if (canonical == null)
                canonical = parsed;
            else
                RequireMatchingPresentation(canonical, parsed, scenePath);
        }

        EnsureFolder(ProfileFolder);
        SolWeatherProfileAsset[] assets = canonical != null
            ? CreateOrUpdateAssets(canonical)
            : LoadExistingAssets();
        if (assets.Length == 0)
            throw new InvalidOperationException(
                "No inline weather profiles or existing weather profile assets were found.");

        var assetsByName = new Dictionary<string, SolWeatherProfileAsset>(
            StringComparer.OrdinalIgnoreCase);
        foreach (SolWeatherProfileAsset asset in assets)
            assetsByName.Add(asset.name, asset);

        AssetDatabase.SaveAssets();
        foreach (string scenePath in ScenePaths)
            RewriteSceneYaml(scenePath, legacyByScene[scenePath], assetsByName);

        AssetDatabase.Refresh();
        Debug.Log($"[SolWeatherProfileMigration] Migrated {ScenePaths.Length} scenes and "
            + $"{assets.Length} reusable weather profiles.");
    }

    static SolWeatherProfileAsset[] CreateOrUpdateAssets(List<LegacyProfile> source)
    {
        var assets = new SolWeatherProfileAsset[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            LegacyProfile legacy = source[i];
            string assetPath = $"{ProfileFolder}/{legacy.Name}.asset";
            SolWeatherProfileAsset asset =
                AssetDatabase.LoadAssetAtPath<SolWeatherProfileAsset>(assetPath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<SolWeatherProfileAsset>();
                AssetDatabase.CreateAsset(asset, assetPath);
            }

            CopyPresentation(legacy, asset);
            EditorUtility.SetDirty(asset);
            assets[i] = asset;
        }
        return assets;
    }

    static SolWeatherProfileAsset[] LoadExistingAssets()
    {
        string[] guids = AssetDatabase.FindAssets("t:SolWeatherProfileAsset", new[] { ProfileFolder });
        var assets = new List<SolWeatherProfileAsset>(guids.Length);
        foreach (string guid in guids)
        {
            SolWeatherProfileAsset asset = AssetDatabase.LoadAssetAtPath<SolWeatherProfileAsset>(
                AssetDatabase.GUIDToAssetPath(guid));
            if (asset != null)
                assets.Add(asset);
        }
        assets.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        return assets.ToArray();
    }

    static void RewriteSceneYaml(
        string scenePath,
        List<LegacyProfile> legacy,
        Dictionary<string, SolWeatherProfileAsset> assetsByName)
    {
        // An already-migrated scene has no legacy entries. Leaving it byte-for-byte
        // untouched makes the command safe to rerun.
        if (legacy.Count == 0)
            return;

        string source = File.ReadAllText(scenePath);
        string newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        bool endsWithNewline = source.EndsWith("\n", StringComparison.Ordinal);
        string[] lines = source.Replace("\r\n", "\n").Split('\n');
        int profilesLine = FindProfilesLine(lines, scenePath);
        int blockEnd = profilesLine + 1;
        while (blockEnd < lines.Length
            && (lines[blockEnd].StartsWith("  - ", StringComparison.Ordinal)
                || lines[blockEnd].StartsWith("    ", StringComparison.Ordinal)))
        {
            blockEnd++;
        }

        var replacement = new List<string>(legacy.Count * 6);
        foreach (LegacyProfile old in legacy)
        {
            if (!assetsByName.TryGetValue(old.Name, out SolWeatherProfileAsset asset))
                throw new InvalidDataException(
                    $"No weather profile asset exists for '{old.Name}' in {scenePath}.");

            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset));
            replacement.Add($"  - profile: {{fileID: 11400000, guid: {guid}, type: 2}}");
            replacement.Add($"    weight: {Serialize(old.Weight)}");
            replacement.Add($"    springWeightMultiplier: {Serialize(old.SpringWeight)}");
            replacement.Add($"    summerWeightMultiplier: {Serialize(old.SummerWeight)}");
            replacement.Add($"    autumnWeightMultiplier: {Serialize(old.AutumnWeight)}");
            replacement.Add($"    winterWeightMultiplier: {Serialize(old.WinterWeight)}");
        }

        var output = new List<string>(lines.Length - (blockEnd - profilesLine - 1)
            + replacement.Count);
        for (int i = 0; i <= profilesLine; i++)
            output.Add(lines[i]);
        output.AddRange(replacement);
        for (int i = blockEnd; i < lines.Length; i++)
            output.Add(lines[i]);

        string migrated = string.Join(newline, output);
        if (endsWithNewline && !migrated.EndsWith(newline, StringComparison.Ordinal))
            migrated += newline;
        File.WriteAllText(scenePath, migrated);
        AssetDatabase.ImportAsset(scenePath, ImportAssetOptions.ForceUpdate);
    }

    static List<LegacyProfile> ParseLegacyProfiles(string assetPath)
    {
        string[] lines = File.ReadAllLines(assetPath);
        int profilesLine = FindProfilesLine(lines, assetPath);

        var result = new List<LegacyProfile>();
        LegacyProfile current = null;
        for (int i = profilesLine + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("  - name: ", StringComparison.Ordinal))
            {
                current = new LegacyProfile { Name = line.Substring("  - name: ".Length).Trim() };
                result.Add(current);
                continue;
            }
            if (!line.StartsWith("    ", StringComparison.Ordinal))
                break;
            if (current == null)
                continue;

            int colon = line.IndexOf(':', 4);
            if (colon < 0)
                continue;
            string key = line.Substring(4, colon - 4);
            string value = line.Substring(colon + 1).Trim();
            AssignLegacyValue(current, key, value);
        }
        return result;
    }

    static int FindProfilesLine(string[] lines, string assetPath)
    {
        int scriptLine = Array.FindIndex(lines,
            line => line.Contains($"guid: {WeatherManagerScriptGuid}", StringComparison.Ordinal));
        if (scriptLine < 0)
            throw new InvalidDataException($"SolWeatherManager was not found in {assetPath}.");

        int profilesLine = -1;
        for (int i = scriptLine + 1; i < lines.Length && !lines[i].StartsWith("--- !u!", StringComparison.Ordinal); i++)
        {
            if (lines[i] == "  profiles:")
            {
                profilesLine = i;
                break;
            }
        }
        if (profilesLine < 0)
            throw new InvalidDataException($"SolWeatherManager.profiles was not found in {assetPath}.");
        return profilesLine;
    }

    static string Serialize(float value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    static void AssignLegacyValue(LegacyProfile profile, string key, string serialized)
    {
        float number = 0f;
        if (key != "lightning")
            number = float.Parse(serialized, NumberStyles.Float, CultureInfo.InvariantCulture);

        switch (key)
        {
            case "cloudiness": profile.Cloudiness = number; break;
            case "cloudErosion": profile.CloudErosion = number; break;
            case "rainIntensity": profile.RainIntensity = number; break;
            case "windStrength": profile.WindStrength = number; break;
            case "fogBoost": profile.FogBoost = number; break;
            case "mistiness": profile.Mistiness = number; break;
            case "skyObscuration": profile.SkyObscuration = number; break;
            case "lightScattering": profile.LightScattering = number; break;
            case "dim": profile.Dim = number; break;
            case "waveSpeedMultiplier": profile.WaveSpeedMultiplier = number; break;
            case "waterTurbulence": profile.WaterTurbulence = number; break;
            case "lightning": profile.Lightning = serialized == "1" || bool.TryParse(serialized, out bool enabled) && enabled; break;
            case "lightningIntensity": profile.LightningIntensity = number; break;
            case "weight": profile.Weight = number; break;
            case "springWeightMultiplier": profile.SpringWeight = number; break;
            case "summerWeightMultiplier": profile.SummerWeight = number; break;
            case "autumnWeightMultiplier": profile.AutumnWeight = number; break;
            case "winterWeightMultiplier": profile.WinterWeight = number; break;
        }
    }

    static void CopyPresentation(LegacyProfile source, SolWeatherProfileAsset destination)
    {
        destination.cloudiness = source.Cloudiness;
        destination.cloudErosion = source.CloudErosion;
        destination.rainIntensity = source.RainIntensity;
        destination.windStrength = source.WindStrength;
        destination.fogBoost = source.FogBoost;
        destination.mistiness = source.Mistiness;
        destination.skyObscuration = source.SkyObscuration;
        destination.lightScattering = source.LightScattering;
        destination.dim = source.Dim;
        destination.waveSpeedMultiplier = source.WaveSpeedMultiplier;
        destination.waterTurbulence = source.WaterTurbulence;
        destination.lightning = source.Lightning;
        destination.lightningIntensity = source.LightningIntensity;
    }

    static void RequireMatchingPresentation(
        List<LegacyProfile> canonical,
        List<LegacyProfile> candidate,
        string scenePath)
    {
        if (canonical.Count != candidate.Count)
            throw new InvalidDataException($"Weather profile count differs in {scenePath}.");

        for (int i = 0; i < canonical.Count; i++)
        {
            LegacyProfile a = canonical[i];
            LegacyProfile b = candidate[i];
            if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)
                || !Approximately(a.Cloudiness, b.Cloudiness)
                || !Approximately(a.CloudErosion, b.CloudErosion)
                || !Approximately(a.RainIntensity, b.RainIntensity)
                || !Approximately(a.WindStrength, b.WindStrength)
                || !Approximately(a.FogBoost, b.FogBoost)
                || !Approximately(a.Mistiness, b.Mistiness)
                || !Approximately(a.SkyObscuration, b.SkyObscuration)
                || !Approximately(a.LightScattering, b.LightScattering)
                || !Approximately(a.Dim, b.Dim)
                || !Approximately(a.WaveSpeedMultiplier, b.WaveSpeedMultiplier)
                || !Approximately(a.WaterTurbulence, b.WaterTurbulence)
                || a.Lightning != b.Lightning
                || !Approximately(a.LightningIntensity, b.LightningIntensity))
            {
                throw new InvalidDataException(
                    $"Weather presentation '{a.Name}' differs in {scenePath}; migration refused.");
            }
        }
    }

    static bool Approximately(float a, float b) => Mathf.Abs(a - b) <= 0.000001f;

    static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder))
            return;

        string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
        string name = Path.GetFileName(folder);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
            throw new InvalidOperationException($"Invalid asset folder '{folder}'.");
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, name);
    }
}
