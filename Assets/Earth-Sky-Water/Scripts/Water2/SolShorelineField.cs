using System;
using Unity.Collections;
using UnityEngine;

namespace Sol.Water
{
    /// <summary>
    /// A live shoreline field: signed water depth and signed distance to the waterline,
    /// over a rectangle of logical world XZ.
    ///
    /// This is the same two-channel contract the baked <c>shorelineData</c> texture
    /// carried -- R is depth in metres, positive where the ground is submerged, and G is
    /// distance to the waterline in metres, positive on the water side -- so nothing
    /// downstream of <see cref="Sol.Water.Rendering.SolWaterMaterialState"/> or
    /// <see cref="SolWaterWaveEvaluator"/> had to change shape to consume it.
    ///
    /// It keeps two representations of the same data on purpose. The texture is what the
    /// surface shader samples; the <see cref="NativeArray{T}"/> pair is what gameplay
    /// samples, and it holds full float precision rather than the texture's quantised
    /// encoding. The CPU path used to go through <c>Texture2D.GetPixelBilinear</c>, which
    /// forced the texture to be readable and cost a managed call per tap -- five of them
    /// per breaker query.
    ///
    /// A field only ever holds finished data. <see cref="SolShorelineFieldBuilder"/> works
    /// in its own arrays and hands them over through <see cref="Adopt"/>, so a rebuild
    /// neither races a gameplay query against a running job nor blinks the shoreline off
    /// for the frames the build is in flight.
    /// </summary>
    public sealed class SolShorelineField : IDisposable
    {
        NativeArray<float> _depth;
        NativeArray<float> _distance;
        Texture2D _texture;
        int _width;
        int _height;
        Vector4 _mapping;
        float _depthRange = 1f;
        float _distanceRange = 1f;

        public int Width => _width;
        public int Height => _height;

        /// <summary>Logical world XZ centre and size covered by the field.</summary>
        public Vector4 Mapping => _mapping;

        /// <summary>Metres the R channel's full range encodes. Mirrors the profile field of the same name.</summary>
        public float DepthRange => _depthRange;

        /// <summary>Metres the G channel's full range encodes.</summary>
        public float DistanceRange => _distanceRange;

        public Texture2D Texture => _texture;

        public bool IsValid => _texture != null && _depth.IsCreated && _distance.IsCreated;

        /// <summary>
        /// Takes ownership of a finished build. The previous arrays are released here and
        /// nowhere else, so the builder never has to reason about which generation of the
        /// field it is replacing.
        /// </summary>
        internal void Adopt(
            NativeArray<float> depth,
            NativeArray<float> distance,
            int width,
            int height,
            Vector4 mapping,
            float depthRange,
            float distanceRange)
        {
            if (_depth.IsCreated)
                _depth.Dispose();
            if (_distance.IsCreated)
                _distance.Dispose();
            _depth = depth;
            _distance = distance;
            _width = width;
            _height = height;
            _mapping = mapping;
            _depthRange = Mathf.Max(0.01f, depthRange);
            _distanceRange = Mathf.Max(0.01f, distanceRange);
        }

        /// <summary>
        /// Returns the texture to upload into, creating or resizing it as needed. Reusing
        /// the existing object whenever its shape is unchanged matters: the ocean material
        /// holds this reference, and a fresh texture every rebuild would leave it bound to
        /// a stale one until the next profile write.
        /// </summary>
        internal Texture2D AcquireTexture(int width, int height, TextureFormat format)
        {
            if (_texture != null && (_texture.width != width || _texture.height != height
                || _texture.format != format))
            {
                DestroyTexture();
            }

            if (_texture == null)
            {
                _texture = new Texture2D(width, height, format, false, true)
                {
                    name = "Sol Shoreline Field",
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    anisoLevel = 0,
                };
            }
            return _texture;
        }

        /// <summary>
        /// Moves the field's rectangle without touching its contents. A world-origin shift
        /// leaves the terrain exactly where it was relative to the water, so only the
        /// logical coordinates the mapping is expressed in have moved -- rebuilding the
        /// whole field for that would be pure waste.
        /// </summary>
        internal void Rebase(Vector4 mapping) => _mapping = mapping;

        /// <summary>
        /// Bilinear lookup in the same texel-centre convention as a clamped GPU sampler,
        /// so a CPU query and the surface shader agree on the waterline.
        /// </summary>
        public bool TrySample(Vector2 logicalXZ, out float depth, out float shoreDistance)
        {
            depth = 0f;
            shoreDistance = 0f;
            if (!IsValid)
                return false;

            float u = (logicalXZ.x - _mapping.x) / Mathf.Max(0.001f, _mapping.z) + 0.5f;
            float v = (logicalXZ.y - _mapping.y) / Mathf.Max(0.001f, _mapping.w) + 0.5f;
            if (u < 0f || u > 1f || v < 0f || v > 1f)
                return false;

            float x = u * _width - 0.5f;
            float y = v * _height - 0.5f;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(x), 0, _width - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(y), 0, _height - 1);
            int x1 = Mathf.Min(x0 + 1, _width - 1);
            int y1 = Mathf.Min(y0 + 1, _height - 1);
            float tx = Mathf.Clamp01(x - x0);
            float ty = Mathf.Clamp01(y - y0);

            int row0 = y0 * _width;
            int row1 = y1 * _width;
            depth = Bilinear(_depth, row0 + x0, row0 + x1, row1 + x0, row1 + x1, tx, ty);
            shoreDistance = Bilinear(_distance, row0 + x0, row0 + x1, row1 + x0, row1 + x1, tx, ty);
            return true;
        }

        static float Bilinear(NativeArray<float> source, int i00, int i10, int i01, int i11,
            float tx, float ty)
        {
            float lower = Mathf.Lerp(source[i00], source[i10], tx);
            float upper = Mathf.Lerp(source[i01], source[i11], tx);
            return Mathf.Lerp(lower, upper, ty);
        }

        public void Dispose()
        {
            if (_depth.IsCreated)
                _depth.Dispose();
            if (_distance.IsCreated)
                _distance.Dispose();
            DestroyTexture();
            _width = 0;
            _height = 0;
        }

        // Destroy is illegal in edit mode and this whole stack is [ExecuteAlways], which
        // is how SolWaterInteractionZone previously leaked a render target pair on every
        // OnDisable. Same branch, same reason.
        void DestroyTexture()
        {
            if (_texture == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(_texture);
            else
                UnityEngine.Object.DestroyImmediate(_texture);
            _texture = null;
        }
    }
}
