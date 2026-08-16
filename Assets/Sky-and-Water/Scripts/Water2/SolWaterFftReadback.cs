using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Sol.Water
{
    /// <summary>
    /// CPU mirror of the spectral displacement field, refreshed by asynchronous GPU
    /// readback.
    ///
    /// Water queries run on the CPU. Without this the CPU evaluates the Gerstner
    /// fallback while Medium and High tiers render the spectrum, so a hull floats on a
    /// different sea than the one it is drawn on — worst exactly where the ocean looks
    /// best. Rather than re-run the inverse FFT on the CPU, the render pass box-filters
    /// the displacement array down to a small grid and this reads it back.
    ///
    /// The mirror is deliberately frame-global rather than per-camera: the spectrum
    /// itself is camera-independent, and gameplay queries have no camera. Its staleness
    /// is real and is reported through <see cref="SolWaterSurfaceSample.SampleAgeSeconds"/>
    /// rather than hidden.
    /// </summary>
    public static class SolWaterFftReadback
    {
        /// <summary>
        /// Readback grid size per cascade. The shortest cascade spans 32 m, so 64 gives
        /// half-metre resolution — finer than any hull that needs buoyancy, and two
        /// orders of magnitude cheaper than reading back the full simulation.
        /// </summary>
        public const int Resolution = 64;

        const int MaximumCascades = 4;

        static RenderTexture _target;
        static RTHandle _targetHandle;
        static int _cascades;
        static bool _requestInFlight;
        static bool _hasRecordedDownsample;
        static double _recordedWaveTime;

        static Vector4[] _current;
        static Vector4[] _previous;
        static double _currentWaveTime;
        static double _previousWaveTime;
        static float _currentRealtime;
        static bool _hasCurrent;
        static bool _hasPrevious;

        /// <summary>True once at least one readback has landed.</summary>
        public static bool HasData => _hasCurrent && _cascades > 0;

        /// <summary>Cascades present in the mirror.</summary>
        public static int CascadeCount => _cascades;

        /// <summary>Seconds since the mirrored frame was captured.</summary>
        public static float SampleAgeSeconds =>
            _hasCurrent ? Mathf.Max(0f, Time.realtimeSinceStartup - _currentRealtime) : 0f;

        /// <summary>Domain size in metres of a cascade. Mirrors CascadeSize in SolWaterFFT.compute.</summary>
        public static float CascadeSize(int cascade) => cascade == 0 ? 32f
            : cascade == 1 ? 128f : cascade == 2 ? 512f : 2048f;

        /// <summary>
        /// The render target the downsample pass writes into. The RTHandle wrapper is
        /// cached with it: allocating one per frame to hand to ImportTexture leaks a
        /// handle every frame the ocean is on screen.
        /// </summary>
        internal static RTHandle EnsureTarget(int cascades)
        {
            cascades = Mathf.Clamp(cascades, 1, MaximumCascades);
            if (_target != null && _cascades == cascades)
                return _targetHandle;

            ReleaseTarget();
            // The replacement target is uninitialized until something writes it, and the
            // mirrored data belongs to a different cascade layout.
            _hasRecordedDownsample = false;
            _hasCurrent = false;
            _hasPrevious = false;
            _cascades = cascades;
            _target = new RenderTexture(new RenderTextureDescriptor(Resolution, Resolution)
            {
                graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat,
                depthBufferBits = 0,
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = cascades,
                enableRandomWrite = true,
                msaaSamples = 1,
                useMipMap = false,
            })
            {
                name = "_SolWaterFftReadback",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat,
            };
            _target.Create();
            _targetHandle = RTHandles.Alloc(_target, transferOwnership: false);
            return _targetHandle;
        }

        /// <summary>
        /// Issues a readback of whatever the target currently holds. Called before this
        /// frame's downsample is recorded, so it captures the previous frame's result —
        /// which is what keeps the request non-blocking. One request is in flight at a
        /// time; dropping frames is preferable to queueing latency behind a stall.
        ///
        /// Gated on a downsample having actually been recorded: a freshly created
        /// RenderTexture has undefined contents, and reading that back as displacement
        /// would launch anything floating on it.
        /// </summary>
        internal static void RequestReadback()
        {
            if (_target == null || !_hasRecordedDownsample || _requestInFlight
                || !SystemInfo.supportsAsyncGPUReadback)
                return;

            _requestInFlight = true;
            // The target holds the frame recorded last, not the one being recorded now,
            // so the sample carries that frame's wave time. Using the current time here
            // would bias every velocity difference by one frame of dt.
            double capturedWaveTime = _recordedWaveTime;
            // A quality change between request and completion reallocates the target with
            // a different slice count. The in-flight result then describes a layout that
            // no longer exists, so it has to be recognised and dropped.
            int capturedCascades = _cascades;
            AsyncGPUReadback.Request(_target, 0, 0, Resolution, 0, Resolution, 0, _cascades,
                request => OnReadbackComplete(request, capturedWaveTime, capturedCascades));
        }

        /// <summary>Records that a downsample pass was queued for the given wave time.</summary>
        internal static void NotifyDownsampleRecorded(double waveTime)
        {
            _recordedWaveTime = waveTime;
            _hasRecordedDownsample = true;
        }

        static void OnReadbackComplete(AsyncGPUReadbackRequest request, double waveTime,
            int cascades)
        {
            _requestInFlight = false;
            if (request.hasError || _target == null || cascades != _cascades)
                return;

            int texelsPerSlice = Resolution * Resolution;
            int total = texelsPerSlice * cascades;

            // Validate every slice before touching the mirror. Bailing halfway through
            // would leave _current holding a mix of two frames while still reporting
            // itself as valid, which is worse than having no spectral data at all.
            for (int slice = 0; slice < cascades; slice++)
            {
                if (request.GetData<Vector4>(slice).Length < texelsPerSlice)
                    return;
            }

            if (_current == null || _current.Length != total)
            {
                _current = new Vector4[total];
                _previous = new Vector4[total];
                _hasPrevious = false;
                _hasCurrent = false;
            }

            // Keep the last frame so velocity can be differenced. The spectrum has no
            // closed-form velocity the way Gerstner does, and differencing the mirror is
            // both cheaper and consistent with whatever the surface actually did.
            (_current, _previous) = (_previous, _current);
            _hasPrevious = _hasCurrent;
            _previousWaveTime = _currentWaveTime;

            for (int slice = 0; slice < cascades; slice++)
            {
                NativeArray<Vector4>.Copy(request.GetData<Vector4>(slice), 0,
                    _current, slice * texelsPerSlice, texelsPerSlice);
            }

            _currentWaveTime = waveTime;
            _currentRealtime = Time.realtimeSinceStartup;
            _hasCurrent = true;
        }

        /// <summary>
        /// Samples the mirrored spectral displacement at a logical world position.
        ///
        /// Every cascade contributes at full weight. The shader fades short cascades by
        /// camera distance, but that is a rendering LOD that exists to stop texels going
        /// sub-pixel — it is not part of the physical surface, and a query has no camera.
        /// </summary>
        public static bool TrySampleDisplacement(Vector2 logicalXZ, out Vector3 displacement,
            out Vector3 velocity)
        {
            displacement = Vector3.zero;
            velocity = Vector3.zero;
            if (!HasData || _current == null)
                return false;

            double waveTimeDelta = _currentWaveTime - _previousWaveTime;
            bool canDifference = _hasPrevious && _previous != null
                && waveTimeDelta > 1e-4;

            for (int cascade = 0; cascade < _cascades; cascade++)
            {
                Vector2 uv = CascadeUv(logicalXZ, CascadeSize(cascade), cascade);
                Vector4 sample = SampleBilinear(_current, cascade, uv);
                Vector2 unrotated = UnrotateCascade(new Vector2(sample.x, sample.z), cascade);
                displacement += new Vector3(unrotated.x, sample.y, unrotated.y);

                if (!canDifference)
                    continue;
                Vector4 previous = SampleBilinear(_previous, cascade, uv);
                Vector2 previousUnrotated = UnrotateCascade(
                    new Vector2(previous.x, previous.z), cascade);
                Vector3 delta = new(unrotated.x - previousUnrotated.x,
                    sample.y - previous.y, unrotated.y - previousUnrotated.y);
                velocity += delta / (float)waveTimeDelta;
            }
            return true;
        }

        /// <summary>
        /// Surface normal at a logical world position, from central differences of the
        /// displaced surface.
        ///
        /// The GPU reads its normal from the spectral normal texture, which this does not
        /// mirror — reading back a second array would double the bandwidth for data that
        /// is itself derived from neighbouring displaced positions in the Finalize kernel.
        /// Differencing the displacement reproduces the same quantity at the mirror's
        /// resolution, which is what buoyancy torque needs.
        /// </summary>
        public static bool TrySampleNormal(Vector2 logicalXZ, out Vector3 normal)
        {
            normal = Vector3.up;
            if (!HasData)
                return false;

            // One texel of the shortest cascade: finer than this only resolves the box
            // filter applied during downsample.
            float delta = CascadeSize(0) / Resolution;
            if (!TrySampleDisplacement(logicalXZ - new Vector2(delta, 0f),
                    out Vector3 left, out _)
                || !TrySampleDisplacement(logicalXZ + new Vector2(delta, 0f),
                    out Vector3 right, out _)
                || !TrySampleDisplacement(logicalXZ - new Vector2(0f, delta),
                    out Vector3 back, out _)
                || !TrySampleDisplacement(logicalXZ + new Vector2(0f, delta),
                    out Vector3 forward, out _))
                return false;

            Vector3 tangentX = new(2f * delta + right.x - left.x,
                right.y - left.y, right.z - left.z);
            Vector3 tangentZ = new(forward.x - back.x,
                forward.y - back.y, 2f * delta + forward.z - back.z);
            Vector3 result = Vector3.Cross(tangentZ, tangentX);
            if (result.sqrMagnitude < 1e-8f)
                return false;
            normal = result.normalized;
            if (normal.y < 0f)
                normal = -normal;
            return true;
        }

        static Vector4 SampleBilinear(Vector4[] buffer, int cascade, Vector2 uv)
        {
            float x = uv.x * Resolution - 0.5f;
            float y = uv.y * Resolution - 0.5f;
            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            float fx = x - x0;
            float fy = y - y0;
            int offset = cascade * Resolution * Resolution;

            Vector4 s00 = buffer[offset + Wrap(y0) * Resolution + Wrap(x0)];
            Vector4 s10 = buffer[offset + Wrap(y0) * Resolution + Wrap(x0 + 1)];
            Vector4 s01 = buffer[offset + Wrap(y0 + 1) * Resolution + Wrap(x0)];
            Vector4 s11 = buffer[offset + Wrap(y0 + 1) * Resolution + Wrap(x0 + 1)];
            return Vector4.Lerp(Vector4.Lerp(s00, s10, fx), Vector4.Lerp(s01, s11, fx), fy);
        }

        // The FFT domain is periodic, so the mirror wraps rather than clamps.
        static int Wrap(int value) => ((value % Resolution) + Resolution) % Resolution;

        /// <summary>Mirrors SolWaterCascadeUv in SolWaterWaves2.hlsl.</summary>
        internal static Vector2 CascadeUv(Vector2 logicalXZ, float mapSize, int cascade)
        {
            Vector2 offset = cascade == 0 ? new Vector2(0f, 0f)
                : cascade == 1 ? new Vector2(0.371f, 0.173f)
                : cascade == 2 ? new Vector2(0.619f, 0.427f)
                : new Vector2(0.211f, 0.793f);
            Vector2 coordinate = RotateCascade(logicalXZ, cascade) / Mathf.Max(0.001f, mapSize)
                + offset;
            return new Vector2(Frac(coordinate.x), Frac(coordinate.y));
        }

        static float Frac(float value) => value - Mathf.Floor(value);

        static Vector2 CascadeBasis(int cascade) => cascade == 0 ? new Vector2(1f, 0f)
            : cascade == 1 ? new Vector2(0.992546f, 0.121869f)
            : cascade == 2 ? new Vector2(0.981627f, -0.190809f)
            : new Vector2(0.997564f, 0.069756f);

        /// <summary>Mirrors SolWaterRotateCascade in SolWaterWaves2.hlsl.</summary>
        internal static Vector2 RotateCascade(Vector2 value, int cascade)
        {
            Vector2 cs = CascadeBasis(cascade);
            return new Vector2(cs.x * value.x - cs.y * value.y,
                cs.y * value.x + cs.x * value.y);
        }

        /// <summary>Mirrors SolWaterUnrotateCascade in SolWaterWaves2.hlsl.</summary>
        internal static Vector2 UnrotateCascade(Vector2 value, int cascade)
        {
            Vector2 cs = CascadeBasis(cascade);
            return new Vector2(cs.x * value.x + cs.y * value.y,
                -cs.y * value.x + cs.x * value.y);
        }

        /// <summary>
        /// Marks the mirror as carrying no usable data without freeing it.
        ///
        /// Call whenever the FFT does not run for a frame — dropping to the Low tier, or
        /// the ocean going off screen. Without this the CPU keeps answering queries from
        /// the last spectral frame while the GPU has gone back to Gerstner, which is the
        /// same surface disagreement this class exists to remove, only inverted.
        /// </summary>
        public static void Invalidate()
        {
            _hasCurrent = false;
            _hasPrevious = false;
            _hasRecordedDownsample = false;
        }

        /// <summary>Drops the mirror. Call when the water renderer feature is disposed.</summary>
        public static void Release()
        {
            ReleaseTarget();
            _current = null;
            _previous = null;
            _hasCurrent = false;
            _hasPrevious = false;
            _hasRecordedDownsample = false;
            _cascades = 0;
        }

        static void ReleaseTarget()
        {
            if (_targetHandle != null)
            {
                RTHandles.Release(_targetHandle);
                _targetHandle = null;
            }
            if (_target == null)
                return;
            _target.Release();
            UnityEngine.Object.DestroyImmediate(_target);
            _target = null;
        }
    }
}
