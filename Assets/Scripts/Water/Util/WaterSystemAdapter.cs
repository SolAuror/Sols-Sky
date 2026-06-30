using UnityEngine;
using Shared.Water;

/// <summary>
/// Adapter that implements IWaterSystem, delegating to WaterVolume (if present) or SolWaterManager (global fallback).
/// Use this for all gameplay water queries (surface, underwater, etc.).
/// </summary>
public class WaterSystemAdapter : MonoBehaviour, IWaterSystem
{
    public float GetWaterline(Vector3 position)
    {
        var volume = WaterVolume.FindVolumeXZ(position);
        if (volume != null)
            return volume.GetSurfaceHeight(position);
        if (SolWaterManager.Instance != null)
            return SolWaterManager.Instance.waterLevel;
        return 0f;
    }

    public bool IsUnderwater(Vector3 position)
    {
        // Use XZ lookup so local water bodies remain authoritative even when the
        // query point is above/below the animated surface at this instant.
        var volume = WaterVolume.FindVolumeXZ(position);
        if (volume != null)
            return volume.IsUnderwater(position);
        if (SolWaterManager.Instance != null)
            return position.y < SolWaterManager.Instance.waterLevel;
        return false;
    }
}



