namespace Shared.Water
{
    /// <summary>
    /// Interface for querying water system (waterline, buoyancy, etc.).
    /// </summary>
    public interface IWaterSystem
    {
        /// <summary>
        /// Returns the water surface height at a given position.
        /// </summary>
        float GetWaterline(UnityEngine.Vector3 position);

        /// <summary>
        /// Returns true if the position is underwater.
        /// </summary>
        bool IsUnderwater(UnityEngine.Vector3 position);
    }
}
