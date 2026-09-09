using UnityEngine;

namespace Sol.Environment.EditorTools
{
    /// <summary>
    /// Presentation of Elementa's live values as the things an author is actually judging:
    /// a wall-clock time, a Beaufort force, a significant wave height, a moon phase name.
    ///
    /// These are read-only derivations of published state. None of them is a second copy of
    /// simulation - the panel must never be able to disagree with the runtime about what the
    /// weather is doing - so anything here is either a unit conversion or a label.
    /// </summary>
    static class ElementaPanelFormat
    {
        /// <summary>
        /// Pierson-Moskowitz significant wave height for a fully developed sea, in metres
        /// per (m/s) squared. Matches the factor the sea-state readout has always used.
        /// </summary>
        public const float SignificantWaveHeightFactor = 0.007f;

        static readonly float[] BeaufortThresholds =
        {
            0.5f, 1.6f, 3.4f, 5.5f, 8f, 10.8f, 13.9f,
            17.2f, 20.8f, 24.5f, 28.5f, 32.7f,
        };

        static readonly string[] BeaufortNames =
        {
            "calm", "light air", "light breeze", "gentle breeze",
            "moderate breeze", "fresh breeze", "strong breeze",
            "near gale", "gale", "strong gale", "storm",
            "violent storm", "hurricane force",
        };

        static readonly string[] LunarPhaseNames =
        {
            "new moon", "waxing crescent", "first quarter", "waxing gibbous",
            "full moon", "waning gibbous", "last quarter", "waning crescent",
        };

        public static string Clock(float hour)
        {
            int totalMinutes = Mathf.RoundToInt(Mathf.Repeat(hour, 24f) * 60f) % (24 * 60);
            return $"{totalMinutes / 60:00}:{totalMinutes % 60:00}";
        }

        /// <summary>Hours as a duration rather than a time of day, for day length and skips.</summary>
        public static string Duration(float hours)
        {
            int totalMinutes = Mathf.RoundToInt(Mathf.Max(0f, hours) * 60f);
            return $"{totalMinutes / 60}h {totalMinutes % 60:00}m";
        }

        public static int BeaufortNumber(float speedMetresPerSecond)
        {
            for (int i = 0; i < BeaufortThresholds.Length; i++)
                if (speedMetresPerSecond < BeaufortThresholds[i])
                    return i;
            return 12;
        }

        public static string BeaufortName(float speedMetresPerSecond)
            => BeaufortNames[BeaufortNumber(speedMetresPerSecond)];

        public static string Wind(float speedMetresPerSecond)
            => $"{speedMetresPerSecond:0.0} m/s  ·  Beaufort {BeaufortNumber(speedMetresPerSecond)} "
             + $"({BeaufortName(speedMetresPerSecond)})";

        public static float SignificantWaveHeight(float windSpeedMetresPerSecond)
            => SignificantWaveHeightFactor * windSpeedMetresPerSecond * windSpeedMetresPerSecond;

        /// <summary>
        /// Where the sea is relative to where this wind would eventually take it. The
        /// twenty-minute developed-sea lag is the slowest response in the stack, so a wind
        /// change that looks instant in the readout takes a long while to reach the surface;
        /// saying so is the difference between "the waves are wrong" and "the waves are
        /// still building".
        /// </summary>
        public static string SeaState(in SolEnvironmentWindState wind)
        {
            float targetSpeed = Mathf.Max(0f, wind.Speed);
            float seaSpeed = Mathf.Max(0f, wind.SeaStateSpeed);
            float targetHeight = SignificantWaveHeight(targetSpeed);
            float currentHeight = SignificantWaveHeight(seaSpeed);
            if (targetSpeed < 0.05f && seaSpeed < 0.05f)
                return "calm · Hs 0.0 m";

            bool building = seaSpeed <= targetSpeed;
            float progress = building
                ? targetSpeed > 0.001f ? seaSpeed / targetSpeed : 1f
                : seaSpeed > 0.001f ? targetSpeed / seaSpeed : 1f;
            string phase = building ? "building" : "settling";
            string relation = building ? "of" : "toward";
            return $"{phase} {Mathf.Clamp01(progress):P0}  →  "
                 + $"Hs {currentHeight:0.0} m {relation} {targetHeight:0.0} m";
        }

        public static string LunarPhase(float phase01)
        {
            float wrapped = Mathf.Repeat(phase01, 1f);
            int index = Mathf.RoundToInt(wrapped * 8f) % 8;
            return $"{wrapped:0.00} ({LunarPhaseNames[index]})";
        }

        /// <summary>
        /// A horizontal direction as the same X/Z angle the weather manager authors wind in,
        /// where 0 is +X and the angle turns toward +Z. Deliberately not a compass bearing:
        /// the dial, the manager and this readout have to mean the same number.
        /// </summary>
        public static float WindDegrees(Vector3 direction)
        {
            Vector2 horizontal = new(direction.x, direction.z);
            if (horizontal.sqrMagnitude < 0.000001f)
                return 0f;
            return Mathf.Repeat(Mathf.Atan2(horizontal.y, horizontal.x) * Mathf.Rad2Deg, 360f);
        }

        /// <summary>Elevation of a unit direction above the horizon, in degrees.</summary>
        public static float Elevation(Vector3 direction)
            => Mathf.Asin(Mathf.Clamp(direction.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
    }
}
