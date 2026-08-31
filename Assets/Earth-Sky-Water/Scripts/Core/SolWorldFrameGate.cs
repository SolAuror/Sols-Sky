namespace Sol.Environment
{
    /// <summary>
    /// Admits one caller per frame and turns the rest away.
    ///
    /// The environment stack records several chains that are world-level facts but are
    /// reached from per-camera code: the ocean spectrum, the caustic array, TimeOfDay's
    /// RenderSettings push. Each was previously guarded, where it was guarded at all, by an
    /// inline <c>if (_frame == Time.frameCount)</c>. Pulling that into a type makes the
    /// semantics testable and keeps the several copies from drifting.
    ///
    /// Deliberately a struct with no sentinel field: <c>default</c> must behave as "no frame
    /// seen yet", which is why the stamp is paired with a validity flag rather than
    /// initialised to a magic number. A struct initialised to <c>_frame = 0</c> would
    /// silently refuse frame zero, which is the frame a fresh domain reload starts on.
    /// </summary>
    public struct SolWorldFrameGate
    {
        int _frame;
        bool _hasFrame;

        /// <summary>The frame this gate last admitted, or 0 if it has admitted none.</summary>
        public readonly int CurrentFrame => _frame;

        /// <summary>
        /// True for the first call with a given frame number, false for every later call
        /// with that same number.
        ///
        /// The comparison is inequality, not <c>&gt;</c>. Unity's frame counter restarts on a
        /// domain reload and does not advance monotonically across a play-mode exit, so a
        /// greater-than test would latch at the highest frame ever seen and refuse every
        /// caller for the remainder of the session.
        /// </summary>
        public bool TryBeginFrame(int frame)
        {
            if (_hasFrame && _frame == frame)
                return false;

            _frame = frame;
            _hasFrame = true;
            return true;
        }

        /// <summary>Forgets the current frame, so the next <see cref="TryBeginFrame"/> for
        /// that same frame is admitted again. Used where a later, better-informed pass must
        /// legitimately redo work already done this frame.</summary>
        public void Invalidate() => _hasFrame = false;
    }
}
