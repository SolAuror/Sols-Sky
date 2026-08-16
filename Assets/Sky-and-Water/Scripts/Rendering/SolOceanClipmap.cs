using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sol.Water.Rendering
{
    /// <summary>Builds reusable patch geometry and allocation-free per-camera clipmap transforms.</summary>
    internal sealed class SolOceanClipmap : IDisposable
    {
        internal sealed class DrawSet
        {
            internal readonly Matrix4x4[] Matrices = new Matrix4x4[256];
            internal readonly Vector4[] PatchData = new Vector4[256];
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
        Mesh _patchMesh;
        int _patchResolution;
        float _patchSkirtDepth;

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

            float maximumAmplitude = SolWaterWaveEvaluator.EstimateMaximumAmplitude(ocean.Profile);
            // Keep the skirt deep enough to cover the residual displacement delta
            // between adjacent FFT LODs, but bounded so it cannot read as a water
            // cliff at the shoreline.
            float skirtDepth = Mathf.Clamp(maximumAmplitude * 0.04f, 0.08f, 0.18f);
            EnsureMesh(quality.clipmapPatchResolution, skirtDepth);

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
            float verticalExtent = Mathf.Max(8f,
                maximumAmplitude * 4f);
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

            PrepareProperties(set, ocean);
            Prune();
            return set;
        }

        void BuildQuadtree(float anchorX, float anchorZ, int maxDepth, float minimumSize,
            float horizon, Vector3 cameraPosition, float verticalExtent, Camera camera, float surfaceLevel)
        {
            _roots.Clear();
            _leaves.Clear();
            int rootSizeInt = Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Max(minimumSize, horizon)));
            float rootSize = rootSizeInt;
            float half = rootSize * 0.5f;
            _roots.Add(new QuadNode(new Vector3(anchorX - half, surfaceLevel, anchorZ - half), rootSize, 0));
            _roots.Add(new QuadNode(new Vector3(anchorX + half, surfaceLevel, anchorZ - half), rootSize, 0));
            _roots.Add(new QuadNode(new Vector3(anchorX - half, surfaceLevel, anchorZ + half), rootSize, 0));
            _roots.Add(new QuadNode(new Vector3(anchorX + half, surfaceLevel, anchorZ + half), rootSize, 0));
            for (int i = 0; i < _roots.Count; i++)
                SelectNode(_roots[i], maxDepth, minimumSize, horizon,
                    cameraPosition, verticalExtent, camera);
            BalanceQuadtree(maxDepth, minimumSize);
        }

        void BalanceQuadtree(int maxDepth, float minimumSize)
        {
            // Enforce a 2:1 neighbour rule after projected-density selection. This
            // guarantees that a seam collapse only ever bridges one coarse edge,
            // matching the reusable patch's odd-vertex stitching contract.
            for (int iteration = 0; iteration < 64; iteration++)
            {
                bool changed = false;
                for (int a = 0; a < _leaves.Count && !changed; a++)
                {
                    QuadNode first = _leaves[a];
                    for (int b = 0; b < _leaves.Count; b++)
                    {
                        if (a == b)
                            continue;
                        QuadNode second = _leaves[b];
                        if (second.Size <= first.Size * 2.001f)
                            continue;
                        float xGap = Mathf.Abs(first.Center.x - second.Center.x)
                            - (first.Size + second.Size) * 0.5f;
                        float zGap = Mathf.Abs(first.Center.z - second.Center.z)
                            - (first.Size + second.Size) * 0.5f;
                        bool touching = (Mathf.Abs(xGap) < 0.01f
                                && Mathf.Abs(first.Center.z - second.Center.z)
                                    < (first.Size + second.Size) * 0.5f)
                            || (Mathf.Abs(zGap) < 0.01f
                                && Mathf.Abs(first.Center.x - second.Center.x)
                                    < (first.Size + second.Size) * 0.5f);
                        if (!touching || second.Level >= maxDepth
                            || second.Size * 0.5f <= minimumSize
                            || _leaves.Count + 3 > 220)
                            continue;
                        SplitLeaf(b);
                        changed = true;
                        break;
                    }
                }
                if (!changed)
                    break;
            }
        }

        void SplitLeaf(int index)
        {
            QuadNode node = _leaves[index];
            float childSize = node.Size * 0.5f;
            int nextLevel = node.Level + 1;
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
            float distance = Vector2.Distance(
                new Vector2(cameraPosition.x, cameraPosition.z),
                new Vector2(node.Center.x, node.Center.z));
            float projectedTarget = Mathf.Max(minimumSize,
                Mathf.Lerp(minimumSize, node.Size * 0.25f,
                    Mathf.Clamp01(distance / Mathf.Max(1f, horizon))));
            bool split = node.Level < maxDepth && node.Size * 0.5f > minimumSize
                && node.Size > projectedTarget * 2.2f;
            if (!split)
            {
                _leaves.Add(node);
                return;
            }
            if (_leaves.Count >= 220)
            {
                _leaves.Add(node);
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

        bool HasCoarseNeighbour(QuadNode node, Vector2 direction)
        {
            Vector2 sample = new Vector2(node.Center.x, node.Center.z)
                + direction * (node.Size * 0.75f);
            for (int i = 0; i < _leaves.Count; i++)
            {
                QuadNode candidate = _leaves[i];
                if (candidate.Size <= node.Size * 1.5f)
                    continue;
                float half = candidate.Size * 0.5f;
                if (sample.x >= candidate.Center.x - half && sample.x <= candidate.Center.x + half
                    && sample.y >= candidate.Center.z - half && sample.y <= candidate.Center.z + half)
                    return true;
            }
            return false;
        }

        void PrepareProperties(DrawSet set, SolWaterBody ocean)
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
            set.Properties.SetVector(SolWaterShaderIds.GeometryParams, Vector4.zero);
            set.Properties.SetVector(SolWaterShaderIds.BodyFlow, Vector4.zero);
            SolWaterInteractionZone zone = ocean.InteractionZone;
            if (zone != null && zone.IsReady)
            {
                set.Properties.SetTexture(SolWaterShaderIds.InteractionTexture, zone.StateTexture);
                set.Properties.SetVector(SolWaterShaderIds.InteractionMapping, zone.GetShaderMapping());
                set.Properties.SetVector(SolWaterShaderIds.InteractionTexel,
                    new Vector4(1f / Mathf.Max(1, zone.StateTexture.width),
                        1f / Mathf.Max(1, zone.StateTexture.height), 0f, 0f));
                set.Properties.SetFloat(SolWaterShaderIds.InteractionStrength, zone.HeightStrength);
            }
            else
            {
                set.Properties.SetFloat(SolWaterShaderIds.InteractionStrength, 0f);
            }
        }

        void EnsureMesh(int resolution, float skirtDepth)
        {
            resolution = Mathf.Clamp(resolution, 16, 128);
            resolution += resolution & 1;
            if (_patchMesh != null && _patchResolution == resolution
                && Mathf.Abs(_patchSkirtDepth - skirtDepth) < 0.001f)
                return;

            CoreUtils.Destroy(_patchMesh);
            _patchResolution = resolution;
            _patchSkirtDepth = skirtDepth;
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
                vertices[surfaceVertexCount + i] = vertices[source] + Vector3.down * skirtDepth;
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
            _patchMesh.bounds = new Bounds(Vector3.zero, new Vector3(1f, skirtDepth * 2f + 8f, 1f));
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
            Vector3 sunDirection = RenderSettings.sun != null
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

        internal void Apply(Material material)
        {
            material.SetColor(SolWaterShaderIds.ReflectionSkyColor, Zenith);
            material.SetColor(SolWaterShaderIds.ReflectionEquatorColor, Horizon);
            material.SetColor(SolWaterShaderIds.ReflectionGroundColor, Nadir);
            material.SetColor(SolWaterShaderIds.ReflectionWarmColor, WarmHorizon);
            material.SetVector(SolWaterShaderIds.ReflectionSkyParams, GradientParams);
            material.SetVector(SolWaterShaderIds.ReflectionSunDirection, SunDirection);
        }

        internal void Apply(MaterialPropertyBlock properties)
        {
            properties.SetColor(SolWaterShaderIds.ReflectionSkyColor, Zenith);
            properties.SetColor(SolWaterShaderIds.ReflectionEquatorColor, Horizon);
            properties.SetColor(SolWaterShaderIds.ReflectionGroundColor, Nadir);
            properties.SetColor(SolWaterShaderIds.ReflectionWarmColor, WarmHorizon);
            properties.SetVector(SolWaterShaderIds.ReflectionSkyParams, GradientParams);
            properties.SetVector(SolWaterShaderIds.ReflectionSunDirection, SunDirection);
        }
    }

    internal static class SolWaterShaderIds
    {
        internal static readonly int WaveDataA = Shader.PropertyToID("_SolWaterWaveDataA");
        internal static readonly int WaveDataB = Shader.PropertyToID("_SolWaterWaveDataB");
        internal static readonly int PatchData = Shader.PropertyToID("_SolOceanPatchData");
        internal static readonly int WaveCount = Shader.PropertyToID("_SolWaterWaveCount");
        internal static readonly int BodyHash = Shader.PropertyToID("_SolWaterBodyHash");
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
