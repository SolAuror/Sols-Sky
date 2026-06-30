using UnityEngine;

namespace Sol.ToD
{
    public enum CelestialBodyType { Sun, Moon, Planet }

    /// <summary>
    /// ScriptableObject defining the orbital, visual, light, and eclipse
    /// parameters of any celestial body (sun, moon, or planet).
    /// </summary>
    [CreateAssetMenu(fileName = "New CelestialBody", menuName = "Sol/Celestial Body Config")]
    public class CelestialBodyConfig : ScriptableObject
    {
        [Header("-- Identity ---------------------")]
        [Tooltip("Display name shown in hierarchy when spawned.")]
        public string bodyName = "Planet";

        [Tooltip("What kind of body this is. Affects default behaviors.")]
        public CelestialBodyType bodyType = CelestialBodyType.Planet;

        // -- Orbit --------------------------------

        [Header("-- Orbit -----------------------")]
        [Tooltip("Orbital period in game days. Sun = 1 (daily cycle), planets can be anything.")]
        [Min(0.1f)]
        public float orbitalPeriodDays = 30f;

        [Tooltip("Orbital inclination in degrees relative to the ecliptic.")]
        [Range(-90f, 90f)]
        public float orbitalTiltDegrees = 0f;

 [Tooltip("Starting orbital phase (0-1). Controls where in its orbit the body begins.")]
        [Range(0f, 1f)]
        public float initialPhase = 0f;

        [Tooltip("Distance from the camera the body orbits at (in world units).")]
        [Min(10f)]
        public float orbitDistance = 800f;

        // -- Visual -------------------------------

        [Header("-- Visual ----------------------")]
        [Tooltip("Base color of the lit (sun-facing) hemisphere.")]
        public Color baseColor = Color.white;

        [Tooltip("Emission color and intensity. Use HDR values for self-luminous bodies (sun).")]
        [ColorUsage(true, true)]
        public Color emissionColor = Color.black;

        [Tooltip("Color of the dark (shadowed) hemisphere.")]
        public Color darkSideColor = new Color(0.02f, 0.02f, 0.04f);

        [Tooltip("How sharp the terminator (day/night boundary) is. 1 = very soft, 10 = razor sharp.")]
        [Range(1f, 10f)]
        public float terminatorSharpness = 3f;

        // -- Light --------------------------------

        [Header("-- Light -----------------------")]
        [Tooltip("Does this body emit a directional light?")]
        public bool hasLight;

        [Tooltip("Maximum light intensity when directly overhead.")]
        [Min(0f)]
        public float maxLightIntensity = 1.5f;

        [Tooltip("Minimum light intensity at the horizon.")]
        [Min(0f)]
        public float minLightIntensity = 0.05f;

        [Tooltip("Primary light color.")]
        public Color lightColor = Color.white;

        [Tooltip("Cast shadows from this light.")]
        public bool castShadows = true;

        // -- Eclipse ------------------------------

        [Header("-- Eclipse ---------------------")]
        [Tooltip("Color tint applied during an eclipse.")]
        public Color eclipseTintColor = new Color(0.6f, 0.15f, 0.1f);

        [Tooltip("How much to dim intensity during eclipse (0 = none, 1 = full blackout).")]
        [Range(0f, 1f)]
        public float eclipseDimFactor = 0.95f;
    }
}
