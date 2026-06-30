using System;
using UnityEngine;

namespace Sol.ToD
{
    /// <summary>Serializable entry for an extra planet in the sky.</summary>
    [Serializable]
    public class TertiaryPlanetEntry
    {
        [Tooltip("Prefab with a Renderer and CelestialBody component.")]
        public GameObject prefab;

        [Tooltip("ScriptableObject with orbital, visual, and light config.")]
        public CelestialBodyConfig config;
    }
}
