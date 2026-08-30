using System;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Sol.Water
{
    /// <summary>One terrain's heightmap, flattened into the shared build arrays.</summary>
    struct SolShorelineTerrainFootprint
    {
        /// <summary>First index of this terrain's heightmap inside the shared heights array.</summary>
        public int HeightOffset;
        /// <summary>Heightmap resolution. Terrain heightmaps are square.</summary>
        public int Resolution;
        public float MinX;
        public float MinZ;
        public float SizeX;
        public float SizeZ;
        /// <summary>Local world Y of the terrain's base.</summary>
        public float BaseY;
        /// <summary>Metres the normalised heightmap's full range spans.</summary>
        public float HeightScale;
    }

    /// <summary>
    /// Turns terrain heightmaps and a water level into the shoreline field.
    ///
    /// The whole build runs on job worker threads, because it is not cheap: at one texel
    /// per metre a single square kilometre is a million texels, and the distance transform
    /// walks all of them twice. Nothing here blocks the main thread -- the owner polls
    /// <see cref="IsComplete"/> and finishes the build on whichever later frame the jobs
    /// land. Every allocation is <see cref="Allocator.Persistent"/> so a build spanning
    /// several frames does not trip the temp-allocator leak warning.
    ///
    /// The one thing that cannot move off the main thread is reading the heightmaps
    /// themselves: <c>TerrainData.GetHeights</c> is a main-thread engine call, so
    /// <see cref="Begin"/> gathers them and everything after that is pure array maths.
    /// </summary>
    sealed class SolShorelineFieldBuilder : IDisposable
    {
        /// <summary>
        /// Offset magnitude standing for "no waterline was reached from here". Large
        /// enough to lose every comparison in the sweep, small enough that its square
        /// stays far inside float range.
        /// </summary>
        const float UnreachedOffset = 1e9f;

        NativeArray<float> _heights;
        NativeArray<SolShorelineTerrainFootprint> _footprints;
        NativeArray<Vector2> _offsets;
        NativeArray<int> _queue;
        NativeArray<byte> _queued;
        NativeArray<byte> _texels;
        NativeArray<float> _depth;
        NativeArray<float> _distance;
        JobHandle _handle;
        bool _running;

        int _width;
        int _height;
        Vector4 _mapping;
        float _depthRange;
        float _distanceRange;
        TextureFormat _format;

        internal bool IsRunning => _running;
        internal bool IsComplete => _running && _handle.IsCompleted;

        /// <summary>
        /// Gathers heightmaps and schedules the build. Returns false when the request
        /// cannot produce a field, in which case nothing was scheduled and the caller
        /// should leave the previous field alone.
        /// </summary>
        internal bool Begin(
            Terrain[] terrains,
            int terrainCount,
            float waterLevel,
            Vector4 mapping,
            Vector2 localCentre,
            int width,
            int height,
            float texelSize,
            float depthRange,
            float distanceRange,
            TextureFormat format)
        {
            if (_running)
                throw new InvalidOperationException("A shoreline build is already in flight.");
            if (terrains == null || terrainCount <= 0 || width <= 0 || height <= 0)
                return false;

            if (!TryGatherHeights(terrains, terrainCount, out int gathered))
            {
                ReleaseBuildArrays();
                return false;
            }

            _width = width;
            _height = height;
            _mapping = mapping;
            _depthRange = Mathf.Max(0.01f, depthRange);
            _distanceRange = Mathf.Max(0.01f, distanceRange);
            _format = format;

            int texelCount = width * height;
            _depth = new NativeArray<float>(texelCount, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _distance = new NativeArray<float>(texelCount, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _offsets = new NativeArray<Vector2>(texelCount, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _queue = new NativeArray<int>(texelCount, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _queued = new NativeArray<byte>(texelCount, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            // RGHalf and RGBA32 are both four bytes per texel, so one buffer size serves
            // either encoding.
            _texels = new NativeArray<byte>(texelCount * 4, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);

            // Texel (0,0) covers the corner of the mapped rectangle, so its centre sits
            // half a texel in. The shader's UV convention is the same one.
            float originX = localCentre.x - width * texelSize * 0.5f;
            float originZ = localCentre.y - height * texelSize * 0.5f;

            var depthJob = new DepthJob
            {
                Heights = _heights,
                Footprints = _footprints,
                Depth = _depth,
                Width = width,
                OriginX = originX,
                OriginZ = originZ,
                TexelSize = texelSize,
                WaterLevel = waterLevel,
                DepthLimit = _depthRange,
                FootprintCount = gathered,
            };
            var seedJob = new SeedJob
            {
                Depth = _depth,
                Offsets = _offsets,
                Width = width,
                Height = height,
                TexelSize = texelSize,
            };
            var sweepJob = new SweepJob
            {
                Offsets = _offsets,
                Queue = _queue,
                Queued = _queued,
                Width = width,
                Height = height,
                TexelSize = texelSize,
                Limit = _distanceRange,
            };
            var resolveJob = new ResolveJob
            {
                Depth = _depth,
                Offsets = _offsets,
                Distance = _distance,
                Texels = _texels,
                DepthRange = _depthRange,
                DistanceRange = _distanceRange,
                Half = format == TextureFormat.RGHalf,
            };

            JobHandle handle = depthJob.Schedule(texelCount, 256);
            handle = seedJob.Schedule(texelCount, 256, handle);
            handle = sweepJob.Schedule(handle);
            _handle = resolveJob.Schedule(texelCount, 256, handle);
            // Nothing here will hit a sync point on its own -- the owner only ever polls
            // IsCompleted -- so the workers have to be kicked explicitly or the chain sits
            // queued until some unrelated Complete happens to flush it.
            JobHandle.ScheduleBatchedJobs();
            _running = true;
            return true;
        }

        /// <summary>
        /// Waits for the scheduled jobs, uploads the texture and publishes the mapping.
        /// Call only once <see cref="IsComplete"/> is true, or on teardown where blocking
        /// is the point.
        /// </summary>
        internal void Finish(SolShorelineField field)
        {
            if (!_running)
                return;
            _handle.Complete();
            _running = false;

            if (field != null)
            {
                Texture2D texture = field.AcquireTexture(_width, _height, _format);
                texture.SetPixelData(_texels, 0);
                texture.Apply(false, false);
                // Ownership of the result arrays moves to the field, which releases the
                // generation it was serving until now.
                field.Adopt(_depth, _distance, _width, _height, _mapping,
                    _depthRange, _distanceRange);
                _depth = default;
                _distance = default;
            }
            ReleaseBuildArrays();
        }

        /// <summary>Completes and discards an in-flight build without publishing it.</summary>
        internal void Abort()
        {
            if (_running)
            {
                _handle.Complete();
                _running = false;
            }
            ReleaseBuildArrays();
        }

        public void Dispose() => Abort();

        void ReleaseBuildArrays()
        {
            if (_heights.IsCreated)
                _heights.Dispose();
            if (_footprints.IsCreated)
                _footprints.Dispose();
            if (_offsets.IsCreated)
                _offsets.Dispose();
            if (_queue.IsCreated)
                _queue.Dispose();
            if (_queued.IsCreated)
                _queued.Dispose();
            if (_texels.IsCreated)
                _texels.Dispose();
            // Still created only when the build was aborted or the field refused the
            // hand-off; a published build has already given these away.
            if (_depth.IsCreated)
                _depth.Dispose();
            if (_distance.IsCreated)
                _distance.Dispose();
        }

        bool TryGatherHeights(Terrain[] terrains, int terrainCount, out int gathered)
        {
            gathered = 0;
            int total = 0;
            for (int i = 0; i < terrainCount; i++)
            {
                TerrainData data = terrains[i] != null ? terrains[i].terrainData : null;
                if (data == null)
                    continue;
                int resolution = data.heightmapResolution;
                if (resolution < 2)
                    continue;
                total += resolution * resolution;
            }
            if (total <= 0)
                return false;

            _heights = new NativeArray<float>(total, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _footprints = new NativeArray<SolShorelineTerrainFootprint>(terrainCount,
                Allocator.Persistent, NativeArrayOptions.ClearMemory);

            int cursor = 0;
            for (int i = 0; i < terrainCount; i++)
            {
                Terrain terrain = terrains[i];
                TerrainData data = terrain != null ? terrain.terrainData : null;
                if (data == null)
                    continue;
                int resolution = data.heightmapResolution;
                if (resolution < 2)
                    continue;

                float[,] heights = data.GetHeights(0, 0, resolution, resolution);
                for (int y = 0; y < resolution; y++)
                {
                    int row = cursor + y * resolution;
                    for (int x = 0; x < resolution; x++)
                        _heights[row + x] = heights[y, x];
                }

                Vector3 origin = terrain.transform.position;
                Vector3 size = data.size;
                // Footprints past `gathered` stay zeroed and are never read: the jobs
                // iterate FootprintCount, not the array length. A zeroed footprint would
                // otherwise read as a sea-level slab of land at the world origin and carve
                // a shoreline out of nothing.
                _footprints[gathered++] = new SolShorelineTerrainFootprint
                {
                    HeightOffset = cursor,
                    Resolution = resolution,
                    MinX = origin.x,
                    MinZ = origin.z,
                    SizeX = Mathf.Max(0.001f, size.x),
                    SizeZ = Mathf.Max(0.001f, size.z),
                    BaseY = origin.y,
                    HeightScale = size.y,
                };
                cursor += resolution * resolution;
            }

            return gathered > 0;
        }

        /// <summary>
        /// Signed water depth per texel: water level minus the highest ground covering it.
        ///
        /// Texels no terrain covers are open water at the full encoded depth rather than
        /// ground at zero height, so the edge of the terrain group does not read as a
        /// coastline running along nothing.
        /// </summary>
        struct DepthJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Heights;
            [ReadOnly] public NativeArray<SolShorelineTerrainFootprint> Footprints;
            [WriteOnly] public NativeArray<float> Depth;
            public int Width;
            public int FootprintCount;
            public float OriginX;
            public float OriginZ;
            public float TexelSize;
            public float WaterLevel;
            public float DepthLimit;

            public void Execute(int index)
            {
                int x = index % Width;
                int y = index / Width;
                float worldX = OriginX + (x + 0.5f) * TexelSize;
                float worldZ = OriginZ + (y + 0.5f) * TexelSize;

                bool covered = false;
                float ground = 0f;
                for (int i = 0; i < FootprintCount; i++)
                {
                    SolShorelineTerrainFootprint footprint = Footprints[i];
                    float u = (worldX - footprint.MinX) / footprint.SizeX;
                    float v = (worldZ - footprint.MinZ) / footprint.SizeZ;
                    if (u < 0f || u > 1f || v < 0f || v > 1f)
                        continue;
                    float sample = SampleHeight(footprint, u, v);
                    ground = covered ? Mathf.Max(ground, sample) : sample;
                    covered = true;
                }

                Depth[index] = covered
                    ? Mathf.Clamp(WaterLevel - ground, -DepthLimit, DepthLimit)
                    : DepthLimit;
            }

            float SampleHeight(in SolShorelineTerrainFootprint footprint, float u, float v)
            {
                int resolution = footprint.Resolution;
                int last = resolution - 1;
                float fx = Mathf.Clamp(u * last, 0f, last);
                float fy = Mathf.Clamp(v * last, 0f, last);
                int x0 = (int)fx;
                int y0 = (int)fy;
                int x1 = Mathf.Min(x0 + 1, last);
                int y1 = Mathf.Min(y0 + 1, last);
                float tx = fx - x0;
                float ty = fy - y0;

                int row0 = footprint.HeightOffset + y0 * resolution;
                int row1 = footprint.HeightOffset + y1 * resolution;
                float lower = Mathf.Lerp(Heights[row0 + x0], Heights[row0 + x1], tx);
                float upper = Mathf.Lerp(Heights[row1 + x0], Heights[row1 + x1], tx);
                return footprint.BaseY + Mathf.Lerp(lower, upper, ty) * footprint.HeightScale;
            }
        }

        /// <summary>
        /// Seeds the distance transform one texel either side of the waterline.
        ///
        /// The offset is taken from where the depth actually changes sign between two
        /// texel centres, not from the depth gradient. A gradient step is undefined on
        /// flat ground and wildly long on a gentle beach -- exactly the case a shoreline
        /// spends most of its length in -- whereas the crossing fraction between a wet and
        /// a dry neighbour is well conditioned everywhere and is already sub-texel
        /// accurate.
        /// </summary>
        struct SeedJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Depth;
            [WriteOnly] public NativeArray<Vector2> Offsets;
            public int Width;
            public int Height;
            public float TexelSize;

            public void Execute(int index)
            {
                int x = index % Width;
                int y = index / Width;
                float depth = Depth[index];
                float best = float.MaxValue;
                Vector2 offset = new(UnreachedOffset, UnreachedOffset);

                Consider(depth, x - 1, y, -1f, 0f, ref best, ref offset);
                Consider(depth, x + 1, y, 1f, 0f, ref best, ref offset);
                Consider(depth, x, y - 1, 0f, -1f, ref best, ref offset);
                Consider(depth, x, y + 1, 0f, 1f, ref best, ref offset);
                Offsets[index] = offset;
            }

            void Consider(float depth, int nx, int ny, float dirX, float dirZ,
                ref float best, ref Vector2 offset)
            {
                if (nx < 0 || nx >= Width || ny < 0 || ny >= Height)
                    return;
                float neighbour = Depth[ny * Width + nx];
                // Strictly opposite sides of the waterline. Comparing against zero on both
                // texels rather than multiplying signs keeps an exactly-zero depth on one
                // consistent side instead of seeding a whole flat plateau that grazes it.
                if (depth > 0f == neighbour > 0f)
                    return;

                float span = Mathf.Abs(depth) + Mathf.Abs(neighbour);
                float fraction = span > 1e-6f ? Mathf.Abs(depth) / span : 0f;
                float distance = fraction * TexelSize;
                if (distance >= best)
                    return;
                best = distance;
                offset = new Vector2(dirX * distance, dirZ * distance);
            }
        }

        /// <summary>
        /// Vector distance transform, propagated as a bounded wavefront out from the
        /// waterline. Each texel carries the offset to the nearest waterline point found
        /// so far; relaxing a texel offers that point to its eight neighbours, one step
        /// nearer or further.
        ///
        /// Bounded because the result is clamped to the distance range on the way out
        /// anyway -- a texel further from the waterline than that encodes to exactly the
        /// same saturated value whether it was resolved or left untouched. So the walk
        /// stops at the range, and the cost follows the length of the coastline rather
        /// than the area of the map. The raster form this replaces swept every texel twice
        /// regardless: on the demo's square kilometre that was ten million comparisons to
        /// resolve a band about thirty texels wide.
        ///
        /// Serial by nature -- a relaxation reads results other relaxations just wrote --
        /// which is why it is a plain <see cref="IJob"/> rather than a parallel one, and
        /// why the build is polled across frames instead of completed inline.
        ///
        /// The queue is a ring buffer over an array sized to the whole grid. That is
        /// always enough: <see cref="Queued"/> keeps a texel from being enqueued twice, so
        /// at most every texel is pending at once.
        /// </summary>
        struct SweepJob : IJob
        {
            public NativeArray<Vector2> Offsets;
            public NativeArray<int> Queue;
            public NativeArray<byte> Queued;
            public int Width;
            public int Height;
            public float TexelSize;
            /// <summary>Metres beyond which a distance is saturated and not worth resolving.</summary>
            public float Limit;

            int _head;
            int _tail;
            int _count;

            public void Execute()
            {
                float limitSquared = Limit * Limit;
                int texelCount = Width * Height;
                _head = 0;
                _tail = 0;
                _count = 0;

                for (int i = 0; i < texelCount; i++)
                {
                    Queued[i] = 0;
                    if (Offsets[i].sqrMagnitude <= limitSquared)
                        Enqueue(i);
                }

                while (_count > 0)
                {
                    int index = Queue[_head];
                    _head = _head + 1 == Queue.Length ? 0 : _head + 1;
                    _count--;
                    Queued[index] = 0;

                    Vector2 offset = Offsets[index];
                    int x = index % Width;
                    int y = index / Width;

                    Relax(x - 1, y, offset, -1, 0, limitSquared);
                    Relax(x + 1, y, offset, 1, 0, limitSquared);
                    Relax(x, y - 1, offset, 0, -1, limitSquared);
                    Relax(x, y + 1, offset, 0, 1, limitSquared);
                    Relax(x - 1, y - 1, offset, -1, -1, limitSquared);
                    Relax(x + 1, y - 1, offset, 1, -1, limitSquared);
                    Relax(x - 1, y + 1, offset, -1, 1, limitSquared);
                    Relax(x + 1, y + 1, offset, 1, 1, limitSquared);
                }
            }

            /// <summary>
            /// Offers this texel's waterline point to the neighbour at (dx, dy). The point
            /// sits at this centre plus this offset, so measured from the neighbour's
            /// centre it is one step back along (dx, dy).
            /// </summary>
            void Relax(int nx, int ny, Vector2 offset, int dx, int dy, float limitSquared)
            {
                if (nx < 0 || nx >= Width || ny < 0 || ny >= Height)
                    return;

                Vector2 candidate = offset - new Vector2(dx * TexelSize, dy * TexelSize);
                float candidateSquared = candidate.sqrMagnitude;
                // Past the limit is not worth carrying: it encodes to the saturated value
                // the untouched sentinel already produces. This is also what bounds the
                // walk -- without it the wavefront would cross the whole grid.
                if (candidateSquared > limitSquared)
                    return;

                int neighbour = ny * Width + nx;
                if (candidateSquared >= Offsets[neighbour].sqrMagnitude)
                    return;

                Offsets[neighbour] = candidate;
                if (Queued[neighbour] == 0)
                    Enqueue(neighbour);
            }

            void Enqueue(int index)
            {
                Queue[_tail] = index;
                _tail = _tail + 1 == Queue.Length ? 0 : _tail + 1;
                _count++;
                Queued[index] = 1;
            }
        }

        /// <summary>
        /// Converts offsets to signed metres and encodes both channels into the texture.
        ///
        /// The encoding matches <c>SolSampleShorelineData</c> exactly: each channel is a
        /// signed value normalised into 0..1 by its own range, which the shader undoes
        /// with <c>value * 2 - 1</c>.
        /// </summary>
        struct ResolveJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Depth;
            [ReadOnly] public NativeArray<Vector2> Offsets;
            [WriteOnly] public NativeArray<float> Distance;
            // Each execution writes only its own four bytes, but the index it writes is
            // four times its own, which the safety system cannot see through.
            [NativeDisableParallelForRestriction] public NativeArray<byte> Texels;
            public float DepthRange;
            public float DistanceRange;
            public bool Half;

            public void Execute(int index)
            {
                float depth = Depth[index];
                float magnitude = Offsets[index].magnitude;
                float distance = Mathf.Clamp(depth > 0f ? magnitude : -magnitude,
                    -DistanceRange, DistanceRange);
                Distance[index] = distance;

                float encodedDepth = Mathf.Clamp01(depth / DepthRange * 0.5f + 0.5f);
                float encodedDistance = Mathf.Clamp01(distance / DistanceRange * 0.5f + 0.5f);

                int offset = index * 4;
                if (Half)
                {
                    WriteHalf(offset, encodedDepth);
                    WriteHalf(offset + 2, encodedDistance);
                }
                else
                {
                    Texels[offset] = (byte)(encodedDepth * 255f + 0.5f);
                    Texels[offset + 1] = (byte)(encodedDistance * 255f + 0.5f);
                    Texels[offset + 2] = 0;
                    Texels[offset + 3] = 255;
                }
            }

            void WriteHalf(int offset, float value)
            {
                ushort bits = FloatToHalf(value);
                Texels[offset] = (byte)(bits & 0xFF);
                Texels[offset + 1] = (byte)(bits >> 8);
            }

            /// <summary>
            /// Managed IEEE 754 binary32 to binary16. <c>Mathf.FloatToHalf</c> would do
            /// this, but it is an engine call and this runs on a job worker thread. Both
            /// channels are normalised into 0..1 before they get here, so the overflow and
            /// subnormal branches exist only to keep the routine total.
            /// </summary>
            static ushort FloatToHalf(float value)
            {
                int bits = BitConverter.SingleToInt32Bits(value);
                int sign = (bits >> 16) & 0x8000;
                int exponent = ((bits >> 23) & 0xFF) - 127 + 15;
                int mantissa = bits & 0x007FFFFF;

                if (exponent >= 0x1F)
                    return (ushort)(sign | 0x7BFF);
                if (exponent <= 0)
                {
                    if (exponent < -10)
                        return (ushort)sign;
                    mantissa |= 0x00800000;
                    int shift = 14 - exponent;
                    return (ushort)(sign | (mantissa >> shift));
                }
                return (ushort)(sign | (exponent << 10) | (mantissa >> 13));
            }
        }
    }
}
