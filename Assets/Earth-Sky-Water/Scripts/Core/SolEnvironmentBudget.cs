using System.Diagnostics;
using Unity.Profiling;
using UnityEngine;

namespace Sol.Environment
{
    /// <summary>
    /// Per-frame tallies of the environment stack's repeated work.
    ///
    /// This exists because the numbers that justify the optimisation roadmap were all
    /// analytic. "The FFT runs once per camera" and "the atmosphere copies the camera colour
    /// twice" are both true, but neither is visible without opening a frame capture, and
    /// neither stays fixed once someone adds a camera. These counters make the claims
    /// falsifiable in the editor and give the environment window something to display.
    ///
    /// Counting happens at *record* time rather than execute time, deliberately. The
    /// RenderGraph passes recorded here may be culled before they execute, and the question
    /// this answers is "how many times did we ask for this work", which is the thing a
    /// refactor is trying to change. It also means the counts behave identically in edit
    /// mode, where the Scene view camera is the one doing the duplicating.
    ///
    /// Every mutator is [Conditional], so call sites stay free of #if and compile away
    /// entirely in a release player.
    /// </summary>
    public static class SolEnvironmentBudget
    {
        /// <summary>One frame's worth of tallies. Copied out wholesale so a reader never
        /// observes a half-updated frame.</summary>
        public struct Counters
        {
            /// <summary>Compute dispatches recorded by the ocean spectrum chain.</summary>
            public int FftDispatches;

            /// <summary>Full-resolution camera-colour copies recorded, across all features.</summary>
            public int FullResColorCopies;

            /// <summary>Calls to TimeOfDay's RenderSettings + skybox material push.</summary>
            public int EnvironmentUpdates;

            /// <summary>Calls to the atmosphere controller's full shader-global push.</summary>
            public int AtmosphereGlobalPushes;

            /// <summary>Live per-camera contexts held by the camera registry. A gauge, not a
            /// tally -- it is set rather than accumulated.</summary>
            public int CameraContexts;

            /// <summary>Bytes held by persistent SSR history handles. Also a gauge.</summary>
            public long SsrHistoryBytes;
        }

        // Markers for the two chains worth attributing in a real capture. The counters say
        // how much work was asked for; these say what it cost.
        public static readonly ProfilerMarker FftMarker = new("Sol.Water.SpectrumRecord");
        public static readonly ProfilerMarker AtmosphereMarker = new("Sol.Atmosphere.Record");

        static SolWorldFrameGate _gate;
        static Counters _current;
        static Counters _previous;

        /// <summary>Tallies for the frame currently being recorded.</summary>
        public static Counters Current
        {
            get
            {
                SyncFrame();
                return _current;
            }
        }

        /// <summary>The last completed frame. Preferred for display: reading
        /// <see cref="Current"/> mid-frame shows whatever has been recorded so far, which
        /// flickers in an inspector that repaints at an unrelated rate.</summary>
        public static Counters Previous
        {
            get
            {
                SyncFrame();
                return _previous;
            }
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void AddFftDispatches(int count)
        {
            SyncFrame();
            _current.FftDispatches += count;
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void AddFullResColorCopy()
        {
            SyncFrame();
            _current.FullResColorCopies++;
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void AddEnvironmentUpdate()
        {
            SyncFrame();
            _current.EnvironmentUpdates++;
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void AddAtmosphereGlobalPush()
        {
            SyncFrame();
            _current.AtmosphereGlobalPushes++;
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void SetCameraContexts(int count)
        {
            SyncFrame();
            _current.CameraContexts = count;
        }

        [Conditional("UNITY_EDITOR"), Conditional("DEVELOPMENT_BUILD")]
        public static void SetSsrHistoryBytes(long bytes)
        {
            SyncFrame();
            _current.SsrHistoryBytes = bytes;
        }

        /// <summary>Rolls the tallies over when the frame stamp moves. The frame-regression
        /// handling lives in <see cref="SolWorldFrameGate"/>.</summary>
        static void SyncFrame()
        {
            if (!_gate.TryBeginFrame(Time.frameCount))
                return;

            _previous = _current;
            _current = default;
        }

        /// <summary>Drops all state. Domain-reload safety for the statics above, matching
        /// the SolEnvironmentCameraRegistry.ResetStatics precedent.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            _gate = default;
            _current = default;
            _previous = default;
        }
    }
}
