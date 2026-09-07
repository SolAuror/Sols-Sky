using System;
using System.Collections.Generic;
using Sol.Environment;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sol.Water.Rendering
{
    /// <summary>Builds reusable patch geometry and allocation-free per-camera clipmap transforms.</summary>
    internal sealed class SolOceanClipmap : IDisposable
    {
        /// <summary>
        /// Hard ceiling on leaves, bounded by the instanced draw arrays below. A split
        /// adds three, so the guard leaves room for one more.
        /// </summary>
        const int MaximumLeaves = 380;

        /// <summary>
        /// Ceiling for projected-density selection alone. The gap to
        /// <see cref="MaximumLeaves"/> is the balance pass's working room, and it has to
        /// stay generous: raising this without raising the ceiling starves balancing and
        /// the cracks come straight back, which is exactly what happened when the two
        /// were pushed together.
        /// </summary>
        const int SelectionLeafBudget = 220;

        /// <summary>
        /// A patch subdivides while it is larger than this fraction of its distance from
        /// the camera. Smaller values mean finer geometry and more leaves: the count per
        /// detail ring is roughly 2*pi/ratio, so 0.35 gives about eighteen patches per
        /// ring and around a hundred and sixty leaves across the horizon before culling.
        /// Raise it for cheaper, flatter water; lower it for more geometric wave relief.
        /// </summary>
        const float PatchSizeToDistanceRatio = 0.35f;

        /// <summary>
        /// Object-space Y extent of the patch mesh's bounds.
        ///
        /// The patch matrix scales X and Z by the node size and leaves Y at 1, so this is
        /// metres. It only has to cover the skirt, which is centimetres; the rest is
        /// headroom for wave displacement and is deliberately generous, because these
        /// bounds do not drive culling -- `CommandBuffer.DrawMeshInstanced` does not cull,
        /// and the clipmap does its own frustum test per leaf.
        /// </summary>
        const float PatchBoundsHeight = 10f;

        /// <summary>
        /// How far past the quadtree the horizon ring reaches, as a multiple of the root
        /// size. The tree covers +/- rootSize about the camera -- 8192 m with the shipped
        /// 8 km horizon distance -- and past that there is simply no ocean, which from
        /// altitude, or in a scene view whose far plane reaches that far, reads as the sea
        /// stopping along a straight line. Eight puts the outer edge near 65 km, past any
        /// practical far plane, for four flat instanced quads.
        /// </summary>
        const float HorizonRingExtent = 8f;

        internal sealed class DrawSet
        {
            // Sized above MaximumLeaves so the ceiling, not the array, is what bounds the
            // tree. DrawMeshInstanced allows up to 1023 per call.
            internal readonly Matrix4x4[] Matrices = new Matrix4x4[384];
            internal readonly Vector4[] PatchData = new Vector4[384];
            internal readonly MaterialPropertyBlock Properties = new();
            internal readonly Vector4[] WaveDataA = new Vector4[8];
            internal readonly Vector4[] WaveDataB = new Vector4[8];
            internal int Count;
            internal int LastFrame;
        }

        readonly Dictionary<int, DrawSet> _cameraDrawSets = new(8);
        readonly List<int> _stale = new(8);
        readonly List<QuadNode> _roots = new(4);
        readonly List<QuadNode> _leaves = new(512);
        readonly Plane[] _frustumPlanes = new Plane[6];
        // Occupancy of the anchored quadtree lattice, as packed (level, i, j). Leaves
        // partition the plane, so at most one leaf contains any point, which is what turns
        // the neighbour queries below from a scan of every leaf into a walk of the at most
        // nine possible levels. Rebuilt once the leaf set is final.
        readonly HashSet<long> _leafCells = new(512);
        // Lattice the leaf cells are indexed against; see CellIndex.
        float _anchorX;
        float _anchorZ;
        float _rootSize;
        // Smallest leaf currently in the tree. Only ever shrinks within a build, since the
        // only mutation is splitting. Lets NeedsBalanceSplit reject a candidate outright.
        float _minimumLeafSize;
        Mesh _patchMesh;
        int _patchResolution;

        internal Mesh PatchMesh => _patchMesh;

        struct QuadNode
        {
            internal Vector3 Center;
            internal float Size;
            internal int Level;

            internal QuadNode(Vector3 center, float size, int level)
            {
                Center = center;
                Size = size;
                Level = level;
            }
        }

        internal DrawSet Build(Camera camera, SolWaterBody ocean, SolWaterQualityProfile quality)
        {
            if (camera == null || ocean == null || quality == null)
                return null;

            // The ocean is issued through DrawMeshInstanced, which bypasses layer culling
            // entirely, so a camera that deliberately masks out the water layer still got
            // the full ocean drawn into it. SolFiniteWaterDrawSet already filters this way
            // for finite bodies; the clipmap was the one that did not.
            if ((camera.cullingMask & (1 << ocean.gameObject.layer)) == 0)
                return null;

            // The spectrum, not the authored Gerstner set, is what the surface is built
            // from on Medium and High, so the reach has to be estimated from the wind
            // driving it. Everything below is sized from this value.
            bool spectral = quality.FftCascadeCount > 0 && quality.FftResolution > 0;
            float windSpeed = SolEnvironmentWorld.ResolveState().Wind.Speed;
            float maximumAmplitude = SolWaterWaveEvaluator.EstimateMaximumAmplitude(
                ocean.Profile, windSpeed, spectral);
            // Deep enough to cover the residual displacement delta between adjacent
            // detail levels, but bounded so it cannot read as a water cliff. Scaling this
            // with the sea state instead made the skirts plainly visible as vertical
            // walls: a skirt is only ever meant to plug a crack it sits behind, so the
            // fix for a visible gap is to remove the gap, not to hang a deeper wall in
            // it. The corrected amplitude now feeds this, which lifts it off the old
            // 8 cm floor without approaching the cap.
            //
            // Published as a shader uniform, not baked into the mesh. The patch mesh
            // authors its skirt one unit down and SolOcean.shader scales that by this
            // value, so the depth can track the wind continuously and the mesh depends
            // on resolution alone. While it was baked, EnsureMesh rebuilt on a 0.001
            // change against a value derived from continuously moving wind, which
            // destroyed and regenerated a ~17k vertex mesh -- RecalculateNormals and
            // UploadMeshData included -- on most frames of every weather transition.
            float skirtDepth = Mathf.Clamp(maximumAmplitude * 0.04f, 0.08f, 0.18f);
            EnsureMesh(quality.clipmapPatchResolution);

            int cameraId = camera.GetInstanceID();
            if (!_cameraDrawSets.TryGetValue(cameraId, out DrawSet set))
            {
                set = new DrawSet();
                _cameraDrawSets.Add(cameraId, set);
            }

            set.Count = 0;
            set.LastFrame = Time.frameCount;
            Vector3 cameraPosition = camera.transform.position;
            float baseSize = Mathf.Max(1f, quality.clipmapBasePatchSize);
            int horizonRings = Mathf.CeilToInt(Mathf.Log(
                Mathf.Max(1f, quality.oceanHorizonDistance / (baseSize * 2f)), 2f)) + 1;
            horizonRings = Mathf.Clamp(Mathf.Max(quality.clipmapRingCount, horizonRings), 3, 9);
            // Culling AABB half-height. Generous on purpose: a patch wrongly culled is a
            // hole in the ocean, while a patch wrongly kept costs one instanced draw.
            float verticalExtent = Mathf.Max(8f, maximumAmplitude * 6f);
            GeometryUtility.CalculateFrustumPlanes(camera, _frustumPlanes);

            float anchorX = Mathf.Floor(cameraPosition.x / baseSize) * baseSize;
            float anchorZ = Mathf.Floor(cameraPosition.z / baseSize) * baseSize;
            BuildQuadtree(anchorX, anchorZ, horizonRings, baseSize,
                Mathf.Max(1f, quality.oceanHorizonDistance), cameraPosition,
                verticalExtent, camera, ocean.SurfaceLevel);
            for (int i = 0; i < _leaves.Count && set.Count < set.Matrices.Length; i++)
            {
                QuadNode node = _leaves[i];
                int edgeMask = GetCoarseNeighbourMask(node);
                Vector3 position = new(node.Center.x, ocean.SurfaceLevel, node.Center.z);
                int instance = set.Count++;
                set.Matrices[instance] = Matrix4x4.TRS(position, Quaternion.identity,
                    new Vector3(node.Size, 1f, node.Size));
                set.PatchData[instance] = new Vector4(
                    node.Size, quality.clipmapPatchResolution, edgeMask, node.Level);
            }

            AppendHorizonRing(set, quality, anchorX, anchorZ, ocean.SurfaceLevel);
            PrepareProperties(set, ocean, skirtDepth);
            Prune();
            return set;
        }

        /// <summary>
        /// Four flat quads forming an annulus immediately outside the quadtree, so the
        /// clipmap's extent is not a visible edge. They are ordinary patch instances in the
        /// same draw set, so they inherit the prepass, the forward pass and the depth-write
        /// pass without a change at any draw site, and they resolve against the rest of the
        /// water through the same nearest-surface prepass. A level of -1 marks them for
        /// SolOcean.shader, which drops waves, edge morphing, foam and the skirt.
        ///
        /// The four rectangles tile the annulus exactly: north and south span the full outer
        /// width, east and west fill only the inner height between them. No overlap, so the
        /// prepass never has two coplanar water surfaces to choose between, and no gap.
        /// </summary>
        void AppendHorizonRing(DrawSet set, SolWaterQualityProfile quality,
            float anchorX, float anchorZ, float surfaceLevel)
        {
            if (_rootSize <= 0f || set.Count + 4 > set.Matrices.Length)
                return;
            float inner = _rootSize;
            float outer = inner * HorizonRingExtent;
            float band = outer - inner;
            float middle = (inner + outer) * 0.5f;
            AddHorizonQuad(set, quality,
                new Vector3(anchorX, surfaceLevel, anchorZ + middle), outer * 2f, band);
            AddHorizonQuad(set, quality,
                new Vector3(anchorX, surfaceLevel, anchorZ - middle), outer * 2f, band);
            AddHorizonQuad(set, quality,
                new Vector3(anchorX + middle, surfaceLevel, anchorZ), band, inner * 2f);
            AddHorizonQuad(set, quality,
                new Vector3(anchorX - middle, surfaceLevel, anchorZ), band, inner * 2f);
        }

        void AddHorizonQuad(DrawSet set, SolWaterQualityProfile quality,
            Vector3 center, float sizeX, float sizeZ)
        {
            int instance = set.Count++;
            set.Matrices[instance] = Matrix4x4.TRS(center, Quaternion.identity,
                new Vector3(sizeX, 1f, sizeZ));
            // Edge mask 0: a flat ring has no coarser neighbour to stitch to, and the shader
            // reads that as "no morph, no skirt". Level -1 is the horizon marker itself.
            set.PatchData[instance] = new Vector4(Mathf.Max(sizeX, sizeZ),
                quality.clipmapPatchResolution, 0f, -1f);
        }

        void BuildQuadtree(float anchorX, float anchorZ, int maxDepth, float minimumSize,
            float horizon, Vector3 cameraPosition, float verticalExtent, Camera camera, float surfaceLevel)
        {
            _roots.Clear();
            _leaves.Clear();
            int rootSizeInt = Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Max(minimumSize, horizon)));
            float rootSize = rootSizeInt;
            float half = rootSize * 0.5f;
            _anchorX = anchorX;
            _anchorZ = anchorZ;
            _rootSize = rootSize;
            _minimumLeafSize = rootSize;
            _roots.Add(new QuadNode(new Vector3(anchorX - half, surfaceLevel, anchorZ - half), rootSize, 0));
            _roots.Add(new QuadNode(new Vector3(anchorX + half, surfaceLevel, anchorZ - half), rootSize, 0));
            _roots.Add(new QuadNode(new Vector3(anchorX - half, surfaceLevel, anchorZ + half), rootSize, 0));
            _roots.Add(new QuadNode(new Vector3(anchorX + half, surfaceLevel, anchorZ + half), rootSize, 0));
            for (int i = 0; i < _roots.Count; i++)
                SelectNode(_roots[i], maxDepth, minimumSize, horizon,
                    cameraPosition, verticalExtent, camera);
            BalanceQuadtree(maxDepth, minimumSize);
            RebuildLeafCells();
        }

        /// <summary>
        /// Every quadtree cell is anchored: at level L the cell size is rootSize / 2^L and
        /// centres sit on anchor + (i + 0.5) * size. That follows from the roots being
        /// placed half a root either side of the anchor and every split halving about the
        /// centre, and it means a point maps to its containing cell at any level in
        /// constant time with no search.
        /// </summary>
        int CellIndex(float coordinate, float anchor, float size)
            => Mathf.FloorToInt((coordinate - anchor) / size);

        /// <summary>Packs a lattice cell into a key. Disjoint bit fields, so no collisions.</summary>
        static long CellKey(int level, int i, int j)
            => ((long)(level & 0xF) << 56)
                | ((long)(i & 0xFFFFFFF) << 28)
                | (long)(j & 0xFFFFFFF);

        void RebuildLeafCells()
        {
            _leafCells.Clear();
            for (int index = 0; index < _leaves.Count; index++)
            {
                QuadNode leaf = _leaves[index];
                _leafCells.Add(CellKey(leaf.Level,
                    CellIndex(leaf.Center.x, _anchorX, leaf.Size),
                    CellIndex(leaf.Center.z, _anchorZ, leaf.Size)));
            }
        }

        /// <summary>Appends a leaf, keeping the minimum-size bound current.</summary>
        void AddLeaf(QuadNode node)
        {
            if (node.Size < _minimumLeafSize)
                _minimumLeafSize = node.Size;
            _leaves.Add(node);
        }

        void BalanceQuadtree(int maxDepth, float minimumSize)
        {
            // Enforce a 2:1 neighbour rule after projected-density selection. This
            // guarantees that a seam collapse only ever bridges one coarse edge,
            // matching the reusable patch's odd-vertex stitching contract.
            //
            // Every violating leaf is split each pass. The previous version set a
            // `changed` flag and broke out of its scan on the first split, so a pass
            // performed exactly one — capping the entire balance at 64 splits per frame.
            // A horizon-scale tree needs far more than that, so the 2:1 rule quietly
            // failed wherever the budget of splits ran out, and the vertex stitching
            // (which only ever bridges one level) left real T-junctions behind. Those
            // are the cracks visible across the ocean.
            for (int iteration = 0; iteration < 64; iteration++)
            {
                bool changed = false;
                // Backwards, so the children appended by a split are not rescanned
                // until the next pass.
                for (int index = _leaves.Count - 1; index >= 0; index--)
                {
                    if (_leaves.Count + 3 > MaximumLeaves)
                        break;
                    if (!NeedsBalanceSplit(_leaves[index], maxDepth, minimumSize))
                        continue;
                    SplitLeaf(index);
                    changed = true;
                }
                if (!changed)
                    break;
            }
        }

        /// <summary>
        /// True when this leaf is more than twice the size of a leaf it touches, and can
        /// still be subdivided. The oversized leaf is the one that has to split: the
        /// stitching contract is written from the fine side collapsing onto a grid exactly
        /// one level coarser.
        /// </summary>
        bool NeedsBalanceSplit(QuadNode candidate, int maxDepth, float minimumSize)
        {
            if (candidate.Level >= maxDepth || candidate.Size * 0.5f <= minimumSize)
                return false;
            // The scan below skips every neighbour failing candidate.Size > neighbour.Size
            // * 2.001, so if even the smallest leaf in the tree fails that, no neighbour
            // can pass and the whole scan is dead work. This is the exact negation of the
            // loop's own skip test applied to the best possible candidate, so the result is
            // unchanged -- it just stops a leaf already at or near the finest level from
            // comparing itself against all several hundred others, which in a balanced tree
            // is almost every leaf.
            if (candidate.Size <= _minimumLeafSize * 2.001f)
                return false;
            for (int i = 0; i < _leaves.Count; i++)
            {
                QuadNode neighbour = _leaves[i];
                if (candidate.Size <= neighbour.Size * 2.001f)
                    continue;
                if (AreTouching(candidate, neighbour))
                    return true;
            }
            return false;
        }

        static bool AreTouching(QuadNode first, QuadNode second)
        {
            float halfSpan = (first.Size + second.Size) * 0.5f;
            float xGap = Mathf.Abs(first.Center.x - second.Center.x) - halfSpan;
            float zGap = Mathf.Abs(first.Center.z - second.Center.z) - halfSpan;
            return (Mathf.Abs(xGap) < 0.01f
                    && Mathf.Abs(first.Center.z - second.Center.z) < halfSpan)
                || (Mathf.Abs(zGap) < 0.01f
                    && Mathf.Abs(first.Center.x - second.Center.x) < halfSpan);
        }

        void SplitLeaf(int index)
        {
            QuadNode node = _leaves[index];
            float childSize = node.Size * 0.5f;
            int nextLevel = node.Level + 1;
            if (childSize < _minimumLeafSize)
                _minimumLeafSize = childSize;
            _leaves[index] = new QuadNode(
                node.Center + new Vector3(-childSize * 0.5f, 0f, -childSize * 0.5f),
                childSize, nextLevel);
            _leaves.Add(new QuadNode(
                node.Center + new Vector3(childSize * 0.5f, 0f, -childSize * 0.5f),
                childSize, nextLevel));
            _leaves.Add(new QuadNode(
                node.Center + new Vector3(-childSize * 0.5f, 0f, childSize * 0.5f),
                childSize, nextLevel));
            _leaves.Add(new QuadNode(
                node.Center + new Vector3(childSize * 0.5f, 0f, childSize * 0.5f),
                childSize, nextLevel));
        }

        void SelectNode(QuadNode node, int maxDepth, float minimumSize, float horizon,
            Vector3 cameraPosition, float verticalExtent, Camera camera)
        {
            Bounds bounds = new(node.Center, new Vector3(node.Size, verticalExtent, node.Size));
            if (!GeometryUtility.TestPlanesAABB(_frustumPlanes, bounds))
                return;
            // Full 3D distance, including how far the camera sits above the surface.
            // Measuring in XZ alone made a camera hovering high over the ocean report a
            // distance near zero for the patch directly beneath it, so that patch
            // subdivided to maximum depth despite being hundreds of metres away and the
            // leaf budget was gone before the rest of the view was considered. A camera
            // near the waterline hides this, which is why Game view looked correct while
            // the elevated Scene view camera did not.
            float distance = Vector3.Distance(cameraPosition, node.Center);
            // Distance to the node's nearest corner, not its centre. A large node the
            // camera is standing inside otherwise reports a big centre distance and
            // refuses to subdivide.
            float nearDistance = Mathf.Max(1f, distance - node.Size * 0.7071f);
            // Split purely on distance. The previous test derived its own threshold from
            // node.Size, so two adjacent nodes at the same distance but different sizes
            // applied different thresholds and could settle many levels apart — the tree
            // came out unbalanced by construction and the balance pass then needed more
            // splits than the leaf ceiling could ever supply, which is exactly what the
            // budgetBlocked diagnostic reported. With the threshold depending only on
            // position, neighbours differ by at most one level almost everywhere and the
            // balance pass has very little left to do.
            bool split = node.Level < maxDepth && node.Size * 0.5f >= minimumSize
                && node.Size > nearDistance * PatchSizeToDistanceRatio;
            if (!split)
            {
                AddLeaf(node);
                return;
            }
            if (_leaves.Count >= SelectionLeafBudget)
            {
                AddLeaf(node);
                return;
            }
            float childSize = node.Size * 0.5f;
            int next = node.Level + 1;
            SelectNode(new QuadNode(node.Center + new Vector3(-childSize * 0.5f, 0, -childSize * 0.5f), childSize, next),
                maxDepth, minimumSize, horizon, cameraPosition, verticalExtent, camera);
            SelectNode(new QuadNode(node.Center + new Vector3(childSize * 0.5f, 0, -childSize * 0.5f), childSize, next),
                maxDepth, minimumSize, horizon, cameraPosition, verticalExtent, camera);
            SelectNode(new QuadNode(node.Center + new Vector3(-childSize * 0.5f, 0, childSize * 0.5f), childSize, next),
                maxDepth, minimumSize, horizon, cameraPosition, verticalExtent, camera);
            SelectNode(new QuadNode(node.Center + new Vector3(childSize * 0.5f, 0, childSize * 0.5f), childSize, next),
                maxDepth, minimumSize, horizon, cameraPosition, verticalExtent, camera);
        }

        int GetCoarseNeighbourMask(QuadNode node)
        {
            int mask = 0;
            if (HasCoarseNeighbour(node, Vector2.left)) mask |= 1;
            if (HasCoarseNeighbour(node, Vector2.right)) mask |= 2;
            if (HasCoarseNeighbour(node, Vector2.down)) mask |= 4;
            if (HasCoarseNeighbour(node, Vector2.up)) mask |= 8;
            return mask;
        }

        /// <summary>
        /// True when the leaf just across this edge is coarser than the given node.
        ///
        /// Sizes are exact powers of two of the root, so candidate.Size > node.Size * 1.5
        /// holds for a candidate at any level shallower than this node's and for no other:
        /// the size test and a level test are the same test. Combined with leaves
        /// partitioning the plane, "is there a coarser leaf covering this point" becomes
        /// "is any cell occupied at a shallower level", which is at most nine hashed
        /// lookups rather than a scan of every leaf in the tree.
        ///
        /// This runs four times per leaf, so at the leaf ceiling the scan it replaces was
        /// over half a million box tests per camera per frame, on the main thread.
        /// </summary>
        bool HasCoarseNeighbour(QuadNode node, Vector2 direction)
        {
            Vector2 sample = new Vector2(node.Center.x, node.Center.z)
                + direction * (node.Size * 0.75f);
            float size = _rootSize;
            for (int level = 0; level < node.Level; level++)
            {
                if (_leafCells.Contains(CellKey(level,
                        CellIndex(sample.x, _anchorX, size),
                        CellIndex(sample.y, _anchorZ, size))))
                    return true;
                size *= 0.5f;
            }
            return false;
        }

        void PrepareProperties(DrawSet set, SolWaterBody ocean, float skirtDepth)
        {
            SolWaterProfile profile = ocean.Profile;
            for (int i = 0; i < 8; i++)
            {
                if (profile != null && profile.gerstnerWaves != null && i < profile.gerstnerWaves.Length)
                {
                    SolGerstnerWave wave = profile.gerstnerWaves[i];
                    Vector2 direction = wave.direction.sqrMagnitude > 0.0001f
                        ? wave.direction.normalized
                        : Vector2.right;
                    set.WaveDataA[i] = new Vector4(direction.x, direction.y,
                        Mathf.Max(0.01f, wave.wavelength), Mathf.Max(0f, wave.amplitude));
                    set.WaveDataB[i] = new Vector4(Mathf.Clamp01(wave.steepness), wave.phaseOffset, 0f, 0f);
                }
                else
                {
                    set.WaveDataA[i] = Vector4.zero;
                    set.WaveDataB[i] = Vector4.zero;
                }
            }

            set.Properties.Clear();
            set.Properties.SetVectorArray(SolWaterShaderIds.WaveDataA, set.WaveDataA);
            set.Properties.SetVectorArray(SolWaterShaderIds.WaveDataB, set.WaveDataB);
            set.Properties.SetVectorArray(SolWaterShaderIds.PatchData, set.PatchData);
            set.Properties.SetInt(SolWaterShaderIds.WaveCount,
                profile?.gerstnerWaves == null ? 0 : Mathf.Min(8, profile.gerstnerWaves.Length));
            set.Properties.SetFloat(SolWaterShaderIds.BodyHash,
                ocean.PrepassHash / 16777215f);
            // Scales the patch mesh's unit skirt. Only the ocean draws skirted geometry,
            // so only this draw set publishes it.
            set.Properties.SetFloat(SolWaterShaderIds.SkirtDepth, skirtDepth);
            set.Properties.SetVector(SolWaterShaderIds.GeometryParams, Vector4.zero);
            set.Properties.SetVector(SolWaterShaderIds.BodyFlow, Vector4.zero);
            // The ocean's profile state is written to the shared material by
            // SolWaterRendererFeature.ApplyMaterialState, not here — this block carries
            // only what varies per patch and per draw.
            SolWaterMaterialState.ApplyInteractionZone(
                new SolWaterPropertyTarget(set.Properties), ocean.InteractionZone);
        }

        /// <summary>
        /// Builds the shared patch mesh. Resolution is the only thing it depends on, so
        /// it is rebuilt on a quality change and never again.
        ///
        /// The skirt is authored one unit down and scaled by `_SolWaterSkirtDepth` in the
        /// vertex shader. Baking the depth in instead tied this mesh to the wind speed,
        /// and regenerating ~17k vertices per frame through a weather transition is the
        /// one thing this cache exists to prevent.
        /// </summary>
        void EnsureMesh(int resolution)
        {
            resolution = Mathf.Clamp(resolution, 16, 128);
            resolution += resolution & 1;
            if (_patchMesh != null && _patchResolution == resolution)
                return;

            CoreUtils.Destroy(_patchMesh);
            _patchResolution = resolution;
            int side = resolution + 1;
            int surfaceVertexCount = side * side;
            int perimeterCount = resolution * 4;
            Vector3[] vertices = new Vector3[surfaceVertexCount + perimeterCount];
            Vector2[] uvs = new Vector2[vertices.Length];
            int[] perimeterSurfaceIndices = new int[perimeterCount];

            for (int z = 0; z <= resolution; z++)
            {
                for (int x = 0; x <= resolution; x++)
                {
                    int index = z * side + x;
                    float u = x / (float)resolution;
                    float v = z / (float)resolution;
                    vertices[index] = new Vector3(u - 0.5f, 0f, v - 0.5f);
                    uvs[index] = new Vector2(u, v);
                }
            }

            int perimeter = 0;
            for (int x = 0; x < resolution; x++) perimeterSurfaceIndices[perimeter++] = x;
            for (int z = 0; z < resolution; z++) perimeterSurfaceIndices[perimeter++] = z * side + resolution;
            for (int x = resolution; x > 0; x--) perimeterSurfaceIndices[perimeter++] = resolution * side + x;
            for (int z = resolution; z > 0; z--) perimeterSurfaceIndices[perimeter++] = z * side;

            for (int i = 0; i < perimeterCount; i++)
            {
                int source = perimeterSurfaceIndices[i];
                // One unit down, not the authored depth: the vertex shader scales this by
                // _SolWaterSkirtDepth. Keeping a real unit offset here rather than
                // collapsing onto the source vertex also keeps the skirt quads
                // non-degenerate, so RecalculateNormals below still produces meaningful
                // normals for them.
                vertices[surfaceVertexCount + i] = vertices[source] + Vector3.down;
                uvs[surfaceVertexCount + i] = uvs[source] + Vector2.one * 2f;
            }

            int surfaceIndexCount = resolution * resolution * 6;
            int skirtIndexCount = perimeterCount * 6;
            int[] indices = new int[surfaceIndexCount + skirtIndexCount];
            int cursor = 0;
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    int a = z * side + x;
                    int b = a + 1;
                    int c = a + side;
                    int d = c + 1;
                    indices[cursor++] = a; indices[cursor++] = c; indices[cursor++] = b;
                    indices[cursor++] = b; indices[cursor++] = c; indices[cursor++] = d;
                }
            }
            for (int i = 0; i < perimeterCount; i++)
            {
                int next = (i + 1) % perimeterCount;
                int topA = perimeterSurfaceIndices[i];
                int topB = perimeterSurfaceIndices[next];
                int bottomA = surfaceVertexCount + i;
                int bottomB = surfaceVertexCount + next;
                indices[cursor++] = topA; indices[cursor++] = bottomA; indices[cursor++] = topB;
                indices[cursor++] = topB; indices[cursor++] = bottomA; indices[cursor++] = bottomB;
            }

            _patchMesh = new Mesh
            {
                name = $"Sol Ocean Clipmap Patch {resolution}",
                indexFormat = vertices.Length > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };
            _patchMesh.SetVertices(vertices);
            _patchMesh.SetUVs(0, uvs);
            _patchMesh.SetIndices(indices, MeshTopology.Triangles, 0, true);
            _patchMesh.RecalculateNormals();
            _patchMesh.bounds = new Bounds(
                Vector3.zero, new Vector3(1f, PatchBoundsHeight, 1f));
            _patchMesh.UploadMeshData(true);
        }

        void Prune()
        {
            _stale.Clear();
            foreach (KeyValuePair<int, DrawSet> pair in _cameraDrawSets)
            {
                if (Time.frameCount - pair.Value.LastFrame > 16)
                    _stale.Add(pair.Key);
            }
            for (int i = 0; i < _stale.Count; i++)
                _cameraDrawSets.Remove(_stale[i]);
        }

        public void Dispose()
        {
            CoreUtils.Destroy(_patchMesh);
            _patchMesh = null;
            _cameraDrawSets.Clear();
        }
    }

    /// <summary>Extracts the live Sol sky gradient without coupling water to TimeOfDay.</summary>
    internal readonly struct SolWaterSkyReflectionState
    {
        static readonly int ZenithColorId = Shader.PropertyToID("_ZenithColor");
        static readonly int HorizonColorId = Shader.PropertyToID("_HorizonColor");
        static readonly int HorizonWarmColorId = Shader.PropertyToID("_HorizonWarmColor");
        static readonly int NadirColorId = Shader.PropertyToID("_NadirColor");
        static readonly int ZenithBlendId = Shader.PropertyToID("_ZenithBlend");
        static readonly int HorizonBlendId = Shader.PropertyToID("_HorizonBlend");
        static readonly int NadirBlendId = Shader.PropertyToID("_NadirBlend");
        static readonly int HorizonWarmthFalloffId = Shader.PropertyToID("_HorizonWarmthFalloff");
        static readonly int SunDirectionId = Shader.PropertyToID("_SunDirection");

        internal readonly Color Zenith;
        internal readonly Color Horizon;
        internal readonly Color Nadir;
        internal readonly Color WarmHorizon;
        internal readonly Vector4 GradientParams;
        internal readonly Vector4 SunDirection;

        SolWaterSkyReflectionState(
            Color zenith,
            Color horizon,
            Color nadir,
            Color warmHorizon,
            Vector4 gradientParams,
            Vector4 sunDirection)
        {
            Zenith = zenith;
            Horizon = horizon;
            Nadir = nadir;
            WarmHorizon = warmHorizon;
            GradientParams = gradientParams;
            SunDirection = sunDirection;
        }

        internal static SolWaterSkyReflectionState Resolve()
        {
            Color zenith = RenderSettings.ambientSkyColor;
            Color horizon = RenderSettings.ambientEquatorColor;
            Color nadir = RenderSettings.ambientGroundColor;
            Color warm = new(horizon.r, horizon.g, horizon.b, 0f);
            Vector4 gradient = new(1f, 1f, 1f, 4f);
            // This direction positions the warm horizon in the *reflected sky*, so it has
            // to be the actual sun -- the same thing the Sol skybox uses to place its own
            // gradient -- or the water reflects a sky that is not the one overhead.
            //
            // RenderSettings.sun is the *dominant* light, which TimeOfDay swaps to the moon
            // at night. Reading it here meant the horizon glow followed the sun when a Sol
            // skybox was assigned (the override below wins) and jumped to the moon when one
            // was not: the same scene behaved two different ways depending on an unrelated
            // setting. Prefer the environment's sun, and fall back to the scene light only
            // when there is no environment authority to ask.
            Vector3 sunDirection = SolEnvironmentWorld.Active != null
                ? SolEnvironmentWorld.Active.State.Lighting.SunDirection
                : RenderSettings.sun != null
                    ? -RenderSettings.sun.transform.forward
                    : Vector3.up;

            Material sky = RenderSettings.skybox;
            bool solGradient = sky != null
                && sky.HasProperty(ZenithColorId)
                && sky.HasProperty(HorizonColorId)
                && sky.HasProperty(NadirColorId);
            if (solGradient)
            {
                zenith = sky.GetColor(ZenithColorId);
                horizon = sky.GetColor(HorizonColorId);
                nadir = sky.GetColor(NadirColorId);
                if (sky.HasProperty(HorizonWarmColorId))
                    warm = sky.GetColor(HorizonWarmColorId);
                gradient = new Vector4(
                    sky.HasProperty(ZenithBlendId) ? Mathf.Max(0.0001f, sky.GetFloat(ZenithBlendId)) : 1f,
                    sky.HasProperty(NadirBlendId) ? Mathf.Max(0.0001f, sky.GetFloat(NadirBlendId)) : 1f,
                    sky.HasProperty(HorizonBlendId) ? Mathf.Max(0.0001f, sky.GetFloat(HorizonBlendId)) : 1f,
                    sky.HasProperty(HorizonWarmthFalloffId)
                        ? Mathf.Max(0.5f, sky.GetFloat(HorizonWarmthFalloffId))
                        : 4f);
                if (sky.HasProperty(SunDirectionId))
                {
                    Vector4 materialSun = sky.GetVector(SunDirectionId);
                    if (materialSun.sqrMagnitude > 0.0001f)
                        sunDirection = new Vector3(materialSun.x, materialSun.y, materialSun.z).normalized;
                }
            }

            return new SolWaterSkyReflectionState(
                zenith, horizon, nadir, warm, gradient,
                new Vector4(sunDirection.x, sunDirection.y, sunDirection.z,
                    solGradient ? 1f : 0f));
        }

        internal void Apply(SolWaterPropertyTarget target)
        {
            target.SetColor(SolWaterShaderIds.ReflectionSkyColor, Zenith);
            target.SetColor(SolWaterShaderIds.ReflectionEquatorColor, Horizon);
            target.SetColor(SolWaterShaderIds.ReflectionGroundColor, Nadir);
            target.SetColor(SolWaterShaderIds.ReflectionWarmColor, WarmHorizon);
            target.SetVector(SolWaterShaderIds.ReflectionSkyParams, GradientParams);
            target.SetVector(SolWaterShaderIds.ReflectionSunDirection, SunDirection);
        }
    }

    internal static class SolWaterShaderIds
    {
        internal static readonly int WaveDataA = Shader.PropertyToID("_SolWaterWaveDataA");
        internal static readonly int WaveDataB = Shader.PropertyToID("_SolWaterWaveDataB");
        internal static readonly int PatchData = Shader.PropertyToID("_SolOceanPatchData");
        internal static readonly int WaveCount = Shader.PropertyToID("_SolWaterWaveCount");
        internal static readonly int BodyHash = Shader.PropertyToID("_SolWaterBodyHash");
        internal static readonly int SkirtDepth = Shader.PropertyToID("_SolWaterSkirtDepth");
        internal static readonly int GeometryParams = Shader.PropertyToID("_SolWaterGeometryParams");
        internal static readonly int BodyFlow = Shader.PropertyToID("_SolWaterBodyFlow");
        internal static readonly int WaveTime = Shader.PropertyToID("_SolWaterWaveTime");
        internal static readonly int WorldOrigin = Shader.PropertyToID("_SolWaterWorldOrigin");
        internal static readonly int Wind = Shader.PropertyToID("_SolWaterWind");
        internal static readonly int Weather = Shader.PropertyToID("_SolWaterWeather");
        internal static readonly int ShallowColor = Shader.PropertyToID("_SolWaterShallowColor");
        internal static readonly int DeepColor = Shader.PropertyToID("_SolWaterDeepColor");
        internal static readonly int Absorption = Shader.PropertyToID("_SolWaterAbsorption");
        internal static readonly int Optics = Shader.PropertyToID("_SolWaterOptics");
        internal static readonly int SunParams = Shader.PropertyToID("_SolWaterSunParams");
        internal static readonly int AnisoParams = Shader.PropertyToID("_SolWaterAnisoParams");
        internal static readonly int RefractionParams = Shader.PropertyToID("_SolWaterRefractionParams");
        internal static readonly int VisibilityParams = Shader.PropertyToID("_SolWaterVisibilityParams");
        internal static readonly int Spectrum = Shader.PropertyToID("_SolWaterSpectrum");
        internal static readonly int SpectralParams = Shader.PropertyToID("_SolWaterSpectralParams");
        internal static readonly int FoamColor = Shader.PropertyToID("_SolWaterFoamColor");
        internal static readonly int FoamParams = Shader.PropertyToID("_SolWaterFoamParams");
        internal static readonly int FoamTexture = Shader.PropertyToID("_SolWaterFoamTexture");
        internal static readonly int FoamDetail = Shader.PropertyToID("_SolWaterFoamDetail");
        internal static readonly int CausticTexture = Shader.PropertyToID("_SolWaterCausticTexture");
        internal static readonly int ReflectionParams = Shader.PropertyToID("_SolWaterReflectionParams");
        internal static readonly int ReflectionSkyColor = Shader.PropertyToID("_SolWaterReflectionSkyColor");
        internal static readonly int ReflectionEquatorColor = Shader.PropertyToID("_SolWaterReflectionEquatorColor");
        internal static readonly int ReflectionGroundColor = Shader.PropertyToID("_SolWaterReflectionGroundColor");
        internal static readonly int ReflectionWarmColor = Shader.PropertyToID("_SolWaterReflectionWarmColor");
        internal static readonly int ReflectionSkyParams = Shader.PropertyToID("_SolWaterReflectionSkyParams");
        internal static readonly int ReflectionSunDirection = Shader.PropertyToID("_SolWaterReflectionSunDirection");
        internal static readonly int ShorelineMask = Shader.PropertyToID("_SolWaterShorelineMask");
        internal static readonly int ShorelineParams = Shader.PropertyToID("_SolWaterShorelineParams");
        internal static readonly int ShorelineDetail = Shader.PropertyToID("_SolWaterShorelineDetail");
        internal static readonly int ShorelineData = Shader.PropertyToID("_SolWaterShorelineData");
        internal static readonly int ShorelineDataMapping = Shader.PropertyToID("_SolWaterShorelineDataMapping");
        internal static readonly int ShorelineDataParams = Shader.PropertyToID("_SolWaterShorelineDataParams");
        internal static readonly int ShorelineSurfaceParams = Shader.PropertyToID("_SolWaterShorelineSurfaceParams");
        internal static readonly int ShorelineBreakerParams = Shader.PropertyToID("_SolWaterShorelineBreakerParams");
        internal static readonly int ShorelineBreakerDetail = Shader.PropertyToID("_SolWaterShorelineBreakerDetail");
        internal static readonly int WeatherExtended = Shader.PropertyToID("_SolWaterWeatherExtended");
        internal static readonly int InteractionTexture = Shader.PropertyToID("_SolWaterInteractionTexture");
        internal static readonly int InteractionMapping = Shader.PropertyToID("_SolWaterInteractionMapping");
        internal static readonly int InteractionTexel = Shader.PropertyToID("_SolWaterInteractionTexel");
        internal static readonly int InteractionStrength = Shader.PropertyToID("_SolWaterInteractionStrength");
        internal static readonly int UnderwaterColor = Shader.PropertyToID("_SolUnderwaterColor");
        internal static readonly int UnderwaterParams = Shader.PropertyToID("_SolUnderwaterParams");
        internal static readonly int PlanarTexture = Shader.PropertyToID("_SolWaterPlanarReflectionTexture");
        internal static readonly int PlanarViewProjection = Shader.PropertyToID("_SolWaterPlanarViewProjection");
        internal static readonly int PlanarParams = Shader.PropertyToID("_SolWaterPlanarParams");
    }
}
