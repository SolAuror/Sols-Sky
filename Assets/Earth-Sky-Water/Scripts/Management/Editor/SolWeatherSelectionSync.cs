using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Appends a default selection entry to every serialized SolWeatherManager for any shared
/// profile asset a container does not yet reference.
///
/// The selection list is duplicated across three scenes and a prefab, and
/// WeatherManagers_UseSharedProfileAssets is the only thing keeping those four copies in
/// agreement. Adding a profile by hand therefore means four identical edits, and a missed
/// one only surfaces as a failing test later.
///
/// Deliberately a separate tool rather than an extension of SolWeatherProfileMigration:
/// that converter returns early on an already-migrated container so it stays safe to rerun,
/// which is exactly the behaviour that would have to be broken to reuse it here.
///
/// Rewrites the YAML as text rather than loading the scenes. Opening and saving a scene
/// re-serializes every object in it, which would bury a six-line addition in an unreviewable
/// diff.
/// </summary>
public static class SolWeatherSelectionSync
{
    const string ProfilesKey = "  profiles:";

    [MenuItem("Sol/Weather/Sync Selection Lists")]
    public static void SyncSelectionLists()
    {
        int changed = Sync(out string report);
        Debug.Log($"[SolWeatherSelectionSync] {report}");
        if (changed > 0)
            AssetDatabase.Refresh();
    }

    /// <summary>Batch-mode entry point, mirroring SolWeatherProfileMigration.</summary>
    public static void SyncFromCommandLine()
    {
        try
        {
            Sync(out string report);
            Debug.Log($"[SolWeatherSelectionSync] {report}");
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogError($"[SolWeatherSelectionSync] {exception}");
            EditorApplication.Exit(1);
        }
    }

    /// <summary>Returns the number of containers rewritten.</summary>
    public static int Sync(out string report)
    {
        List<string> profileGuids = new();
        foreach (string guid in AssetDatabase.FindAssets(
            "t:SolWeatherProfileAsset", new[] { SolWeatherProfileMigration.ProfileFolder }))
        {
            profileGuids.Add(guid);
        }
        profileGuids.Sort(StringComparer.Ordinal);

        int changed = 0;
        List<string> notes = new();
        foreach (string path in SolWeatherProfileMigration.SerializedAssetPaths)
        {
            int added = SyncContainer(path, profileGuids);
            if (added > 0)
            {
                changed++;
                notes.Add($"{Path.GetFileName(path)} +{added}");
            }
        }

        report = changed == 0
            ? $"All {SolWeatherProfileMigration.SerializedAssetPaths.Length} containers already "
                + $"reference every one of the {profileGuids.Count} shared profiles."
            : $"Added missing selections: {string.Join(", ", notes)}.";
        return changed;
    }

    static int SyncContainer(string path, List<string> profileGuids)
    {
        string source = File.ReadAllText(path);
        string newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        bool endsWithNewline = source.EndsWith("\n", StringComparison.Ordinal);
        List<string> lines = new(source.Replace("\r\n", "\n").Split('\n'));

        int start = lines.IndexOf(ProfilesKey);
        if (start < 0)
            throw new InvalidDataException($"No SolWeatherManager profiles list in {path}.");
        if (lines.IndexOf(ProfilesKey, start + 1) >= 0)
            throw new InvalidDataException($"More than one profiles list in {path}.");

        int end = start + 1;
        while (end < lines.Count
            && (lines[end].StartsWith("  - ", StringComparison.Ordinal)
                || lines[end].StartsWith("    ", StringComparison.Ordinal)))
        {
            end++;
        }

        string existing = string.Join("\n", lines.GetRange(start, end - start));
        List<string> additions = new();
        foreach (string guid in profileGuids)
        {
            if (existing.Contains($"guid: {guid}", StringComparison.Ordinal))
                continue;

            // A new entry is authored neutral on purpose. Frequency is a scene decision,
            // and guessing one here would look deliberate without anyone having chosen it.
            additions.Add($"  - profile: {{fileID: 11400000, guid: {guid}, type: 2}}");
            additions.Add("    weight: 1");
            additions.Add("    springWeightMultiplier: 1");
            additions.Add("    summerWeightMultiplier: 1");
            additions.Add("    autumnWeightMultiplier: 1");
            additions.Add("    winterWeightMultiplier: 1");
        }

        if (additions.Count == 0)
            return 0;

        lines.InsertRange(end, additions);
        string merged = string.Join(newline, lines);
        if (endsWithNewline && !merged.EndsWith(newline, StringComparison.Ordinal))
            merged += newline;
        File.WriteAllText(path, merged);
        return additions.Count / 6;
    }
}
