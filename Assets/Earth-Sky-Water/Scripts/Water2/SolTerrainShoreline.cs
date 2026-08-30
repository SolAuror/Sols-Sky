using System;
using System.Collections.Generic;
using Sol.Environment;
using UnityEngine;
using UnityEngine.TerrainTools;

namespace Sol.Water
{
    /// <summary>
    /// Builds the ocean's shoreline data from the live terrain instead of a baked texture.
    ///
    /// The shoreline contract -- signed water depth and signed distance to the waterline
    /// over a rectangle of logical world XZ -- has always been what the surface shader and
    /// the CPU wave evaluator consume. What was missing was anything that produced it: the
    /// baker the profile's tooltip still credits does not exist in this project, so
    /// <c>shorelineData</c> could only ever be an imported texture that no longer tracked
    /// the terrain it was made from. Anything that moved the ground or the sea -- sculpting
    /// a bay, a tide, a flood, a terrain dropped into the scene -- silently desynchronised
    /// the two, and there was no way to resynchronise them.
    ///
    /// This derives the field from <c>TerrainData</c> every time either side moves, so
    /// there is nothing to keep in step by hand. It covers the ocean only, which is also
    /// the only body the shoreline path has ever run for: <see cref="SolWaterWaveEvaluator"/>
    /// evaluates shorelines on the infinite path and never on a finite body, because the
    /// field is measured against one water level and a lake sits at its own.
    ///
    /// The profile's baked texture stays supported as a fallback. A scene with no terrain,
    /// or with this component absent, behaves exactly as it did.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    // After SolWaterWorld (-800) so the ocean body is registered, and before the renderer
    // feature reads the field for the frame.
    [DefaultExecutionOrder(-780)]
    public sealed class SolTerrainShoreline : MonoBehaviour
    {
        /// <summary>
        /// Upper bound on either grid axis. A square kilometre at one texel per metre is
        /// already a million texels and about 4 MB of GPU memory; this stops a large
        /// terrain group from quietly asking for a hundred times that.
        /// </summary>
        const int ResolutionCeiling = 4096;

        [Header("Terrain Sources")]
        [Tooltip("Terrains the shoreline is measured against. Leave empty to use every "
            + "active terrain in the scene, which is what a single-terrain scene wants.")]
        [SerializeField] Terrain[] terrains = Array.Empty<Terrain>();

        [Tooltip("Rebuild when a terrain is sculpted. Costs a rebuild per brush stroke, "
            + "debounced by the interval below, so the waterline follows the brush.")]
        [SerializeField] bool trackTerrainEdits = true;

        [Header("Resolution")]
        [Tooltip("World metres covered by one texel of the field. Lower resolves a "
            + "crisper waterline and costs quadratically more memory and build time. "
            + "One metre per texel matches the resolution the shipped bake used.")]
        [Min(0.05f)] [SerializeField] float metresPerTexel = 1f;

        [Tooltip("Hard cap on either axis of the generated field. The metres-per-texel "
            + "figure is relaxed rather than the terrain being cropped.")]
        [Range(64, ResolutionCeiling)] [SerializeField] int maximumResolution = 2048;

        [Header("Rebuilds")]
        [Tooltip("Water level movement, in metres, that forces a rebuild. A tide crossing "
            + "this redraws the waterline; anything under it is absorbed by the depth "
            + "channel's own precision.")]
        [Min(0.001f)] [SerializeField] float waterLevelTolerance = 0.05f;

        [Tooltip("Shortest time between rebuilds, in seconds. This is what keeps a "
            + "continuously rising tide or a held sculpt brush from queueing a build per "
            + "frame; the field is never more than this far behind.")]
        [Min(0f)] [SerializeField] float minimumRebuildInterval = 0.25f;

        readonly List<Terrain> _resolvedTerrains = new(8);
        readonly List<Terrain> _activeTerrains = new(8);
        readonly SolShorelineFieldBuilder _builder = new();
        SolShorelineField _field;

        // Snapshot of everything the last scheduled build was derived from. A rebuild is
        // requested when the live values stop matching it.
        Terrain[] _builtTerrains = Array.Empty<Terrain>();
        TerrainPlacement[] _builtPlacements = Array.Empty<TerrainPlacement>();
        Vector2 _builtLocalCentre;
        float _builtWaterLevel = float.NaN;
        float _builtMetresPerTexel = float.NaN;
        int _builtMaximumResolution;
        float _builtDepthRange = float.NaN;
        float _builtDistanceRange = float.NaN;
        bool _rebuildRequested = true;
        float _lastBuildStartTime = float.NegativeInfinity;
        string _lastStatus = "No field has been built yet.";

        /// <summary>
        /// The live shoreline authority, or null when none is publishing. Consumers read
        /// <see cref="OceanField"/> rather than this.
        /// </summary>
        public static SolTerrainShoreline Active { get; private set; }

        /// <summary>
        /// The ocean's live shoreline field, or null when there is none. Null is the
        /// signal to fall back to the profile's baked texture, never an error.
        /// </summary>
        public static SolShorelineField OceanField
        {
            get
            {
                SolTerrainShoreline active = Active;
                if (active == null)
                    return null;
                SolShorelineField field = active._field;
                return field != null && field.IsValid ? field : null;
            }
        }

        /// <summary>Human-readable result of the last build, for the inspector.</summary>
        public string LastStatus => _lastStatus;

        public bool IsBuilding => _builder.IsRunning;

        void OnEnable()
        {
            if (Active != null && Active != this)
            {
                Debug.LogError("[SolTerrainShoreline] Only one shoreline authority is allowed.", this);
                enabled = false;
                return;
            }
            Active = this;
            _field ??= new SolShorelineField();
            TerrainCallbacks.heightmapChanged += OnHeightmapChanged;
            _rebuildRequested = true;
        }

        void OnDisable()
        {
            TerrainCallbacks.heightmapChanged -= OnHeightmapChanged;
            if (Active == this)
                Active = null;
            // The jobs hold the arrays they are writing, so tearing down has to wait for
            // them whether or not the result is wanted.
            _builder.Abort();
            _field?.Dispose();
            _field = null;
        }

        void OnValidate()
        {
            maximumResolution = Mathf.Clamp(Mathf.ClosestPowerOfTwo(maximumResolution), 64, ResolutionCeiling);
            _rebuildRequested = true;
        }

        void LateUpdate()
        {
            if (_builder.IsRunning)
            {
                if (!_builder.IsComplete)
                    return;
                _builder.Finish(_field);
                ReportStatus();
                return;
            }

            if (!TryResolveInputs(out float waterLevel, out float depthRange, out float distanceRange))
                return;

            // The mapping is expressed in logical coordinates and the terrain is not, so a
            // world-origin shift moves the rectangle without changing a single texel.
            RebaseMapping();

            if (!NeedsRebuild(waterLevel, depthRange, distanceRange))
                return;

            float now = Time.realtimeSinceStartup;
            if (now - _lastBuildStartTime < minimumRebuildInterval)
                return;

            BeginBuild(waterLevel, depthRange, distanceRange, now);
        }

        /// <summary>Forces a rebuild on the next update, ignoring the dirty checks.</summary>
        public void Rebuild() => _rebuildRequested = true;

        void ReportStatus()
            => _lastStatus = _field != null && _field.IsValid
                ? $"{_field.Width} x {_field.Height} texels over "
                    + $"{_field.Mapping.z:0} x {_field.Mapping.w:0} m, "
                    + $"{_resolvedTerrains.Count} terrain(s)"
                : "The last build produced no field.";

        /// <summary>
        /// Rebuilds and waits for the result. Only for editor actions that need the field
        /// in hand when they return; the normal path never blocks.
        /// </summary>
        public bool RebuildImmediate()
        {
            _builder.Abort();
            _rebuildRequested = true;
            if (!TryResolveInputs(out float waterLevel, out float depthRange, out float distanceRange))
                return false;
            if (!BeginBuild(waterLevel, depthRange, distanceRange, Time.realtimeSinceStartup))
                return false;
            _builder.Finish(_field);
            ReportStatus();
            return _field != null && _field.IsValid;
        }

        bool BeginBuild(float waterLevel, float depthRange, float distanceRange, float now)
        {
            if (!TryPlanGrid(out Vector2 localCentre, out int width, out int height,
                    out float texelSize))
            {
                _lastStatus = "The resolved terrains cover no area.";
                _rebuildRequested = false;
                return false;
            }

            SolDouble3 origin = SolWorldOriginService.Active?.LogicalOrigin ?? default;
            Vector4 mapping = new(
                localCentre.x + (float)origin.X,
                localCentre.y + (float)origin.Z,
                width * texelSize,
                height * texelSize);

            Terrain[] sources = _resolvedTerrains.ToArray();
            if (!_builder.Begin(sources, sources.Length, waterLevel, mapping, localCentre,
                    width, height, texelSize, depthRange, distanceRange, ResolveFormat()))
            {
                _lastStatus = "No terrain supplied a readable heightmap.";
                _rebuildRequested = false;
                return false;
            }

            _builtTerrains = sources;
            _builtPlacements = CapturePlacements(sources);
            _builtLocalCentre = localCentre;
            _builtWaterLevel = waterLevel;
            _builtMetresPerTexel = metresPerTexel;
            _builtMaximumResolution = maximumResolution;
            _builtDepthRange = depthRange;
            _builtDistanceRange = distanceRange;
            _rebuildRequested = false;
            _lastBuildStartTime = now;
            return true;
        }

        /// <summary>
        /// Resolves the ocean's level and the ranges its profile encodes the field with.
        /// The ranges stay authored on the profile rather than duplicated here, because
        /// the same two numbers are what the shader decodes with -- keeping one copy is
        /// what stops the two drifting the way the caustic cascade table once did.
        /// </summary>
        bool TryResolveInputs(out float waterLevel, out float depthRange, out float distanceRange)
        {
            waterLevel = 0f;
            depthRange = 1f;
            distanceRange = 1f;

            SolWaterWorld world = SolWaterWorld.Active;
            if (world == null || !world.TryGetOcean(out SolWaterBody ocean))
                return false;

            SolWaterProfile profile = ocean.Profile;
            if (profile == null)
                return false;

            ResolveTerrains();
            if (_resolvedTerrains.Count == 0)
                return false;

            waterLevel = ocean.SurfaceLevel;
            depthRange = Mathf.Max(0.01f, profile.shorelineDepthRange);
            distanceRange = Mathf.Max(0.01f, profile.shorelineDistanceRange);
            return true;
        }

        void ResolveTerrains()
        {
            _resolvedTerrains.Clear();
            if (terrains != null && terrains.Length > 0)
            {
                for (int i = 0; i < terrains.Length; i++)
                {
                    Terrain terrain = terrains[i];
                    if (terrain != null && terrain.isActiveAndEnabled && terrain.terrainData != null)
                        _resolvedTerrains.Add(terrain);
                }
                return;
            }

            // GetActiveTerrains rather than the activeTerrains property: this runs every
            // frame as part of the dirty check, and the property allocates a fresh array
            // on each read.
            _activeTerrains.Clear();
            Terrain.GetActiveTerrains(_activeTerrains);
            for (int i = 0; i < _activeTerrains.Count; i++)
            {
                Terrain terrain = _activeTerrains[i];
                if (terrain != null && terrain.isActiveAndEnabled && terrain.terrainData != null)
                    _resolvedTerrains.Add(terrain);
            }
        }

        /// <summary>
        /// Sizes the grid over the union of the resolved terrains. Texels stay square: the
        /// distance transform measures diagonals in metres, so a stretched texel would
        /// bias every distance by the aspect ratio.
        /// </summary>
        bool TryPlanGrid(out Vector2 localCentre, out int width, out int height, out float texelSize)
        {
            localCentre = Vector2.zero;
            width = 0;
            height = 0;
            texelSize = 1f;

            float minX = float.MaxValue;
            float minZ = float.MaxValue;
            float maxX = float.MinValue;
            float maxZ = float.MinValue;
            for (int i = 0; i < _resolvedTerrains.Count; i++)
            {
                Terrain terrain = _resolvedTerrains[i];
                Vector3 origin = terrain.transform.position;
                Vector3 size = terrain.terrainData.size;
                minX = Mathf.Min(minX, origin.x);
                minZ = Mathf.Min(minZ, origin.z);
                maxX = Mathf.Max(maxX, origin.x + size.x);
                maxZ = Mathf.Max(maxZ, origin.z + size.z);
            }

            float spanX = maxX - minX;
            float spanZ = maxZ - minZ;
            if (spanX <= 0.001f || spanZ <= 0.001f)
                return false;

            texelSize = Mathf.Max(0.05f, metresPerTexel);
            int cap = Mathf.Clamp(maximumResolution, 64, ResolutionCeiling);
            // Relax the texel size rather than cropping the terrain: a field that stops
            // short of the coast reads as open ocean there and loses the shoreline
            // outright, which is far worse than a coarser one that still covers it.
            texelSize = Mathf.Max(texelSize, Mathf.Max(spanX, spanZ) / cap);

            width = Mathf.Clamp(Mathf.CeilToInt(spanX / texelSize), 2, cap);
            height = Mathf.Clamp(Mathf.CeilToInt(spanZ / texelSize), 2, cap);
            localCentre = new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
            return true;
        }

        void RebaseMapping()
        {
            if (_field == null || !_field.IsValid)
                return;

            // Against the centre the field was actually built around, not the terrain's
            // current one. A terrain that has since moved triggers a rebuild of its own;
            // recentring the existing texels on it would misplace them until then.
            SolDouble3 origin = SolWorldOriginService.Active?.LogicalOrigin ?? default;
            Vector4 mapping = _field.Mapping;
            _field.Rebase(new Vector4(
                _builtLocalCentre.x + (float)origin.X,
                _builtLocalCentre.y + (float)origin.Z,
                mapping.z,
                mapping.w));
        }

        bool NeedsRebuild(float waterLevel, float depthRange, float distanceRange)
        {
            if (_rebuildRequested || _field == null || !_field.IsValid)
                return true;
            if (Mathf.Abs(waterLevel - _builtWaterLevel) > Mathf.Max(0.001f, waterLevelTolerance))
                return true;
            if (!Mathf.Approximately(depthRange, _builtDepthRange)
                || !Mathf.Approximately(distanceRange, _builtDistanceRange))
                return true;
            if (!Mathf.Approximately(metresPerTexel, _builtMetresPerTexel)
                || maximumResolution != _builtMaximumResolution)
                return true;
            if (_builtTerrains.Length != _resolvedTerrains.Count)
                return true;

            for (int i = 0; i < _builtTerrains.Length; i++)
            {
                Terrain terrain = _resolvedTerrains[i];
                if (_builtTerrains[i] != terrain || terrain == null || terrain.terrainData == null)
                    return true;
                if (!_builtPlacements[i].Equals(new TerrainPlacement(terrain)))
                    return true;
            }
            return false;
        }

        static TerrainPlacement[] CapturePlacements(Terrain[] sources)
        {
            var placements = new TerrainPlacement[sources.Length];
            for (int i = 0; i < sources.Length; i++)
                placements[i] = new TerrainPlacement(sources[i]);
            return placements;
        }

        /// <summary>
        /// Everything about a terrain that a built field depends on, short of the
        /// heightmap samples themselves -- those arrive through
        /// <see cref="TerrainCallbacks.heightmapChanged"/> instead of being hashed here
        /// every frame. The vertical origin and size are in: moving or rescaling a terrain
        /// in Y changes every depth in the field.
        /// </summary>
        readonly struct TerrainPlacement : IEquatable<TerrainPlacement>
        {
            readonly Vector3 _origin;
            readonly Vector3 _size;
            readonly int _heightmapResolution;

            internal TerrainPlacement(Terrain terrain)
            {
                TerrainData data = terrain != null ? terrain.terrainData : null;
                _origin = terrain != null ? terrain.transform.position : Vector3.zero;
                _size = data != null ? data.size : Vector3.zero;
                _heightmapResolution = data != null ? data.heightmapResolution : 0;
            }

            public bool Equals(TerrainPlacement other)
                => _origin == other._origin
                    && _size == other._size
                    && _heightmapResolution == other._heightmapResolution;

            public override bool Equals(object obj)
                => obj is TerrainPlacement other && Equals(other);

            public override int GetHashCode()
                => HashCode.Combine(_origin, _size, _heightmapResolution);
        }

        /// <summary>
        /// RGHalf where it is available, which is everywhere this renderer runs. Eight
        /// bits per channel would quantise the depth channel to about a quarter of a metre
        /// over its 60 m range, and the contact fade the waterline is drawn with is under
        /// half a metre wide -- the whole fade would land inside two quantisation steps.
        /// Both formats are four bytes per texel, so the precision is free.
        /// </summary>
        static TextureFormat ResolveFormat()
            => SystemInfo.SupportsTextureFormat(TextureFormat.RGHalf)
                ? TextureFormat.RGHalf
                : TextureFormat.RGBA32;

        void OnHeightmapChanged(Terrain terrain, RectInt region, bool synched)
        {
            if (!trackTerrainEdits || terrain == null)
                return;
            for (int i = 0; i < _resolvedTerrains.Count; i++)
            {
                if (_resolvedTerrains[i] == terrain)
                {
                    _rebuildRequested = true;
                    return;
                }
            }
        }
    }
}
