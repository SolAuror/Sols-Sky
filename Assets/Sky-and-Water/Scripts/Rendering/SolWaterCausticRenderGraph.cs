using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace Sol.Water.Rendering
{
    /// <summary>
    /// Renders the caustic pattern the FFT surface actually projects, into a Tex2DArray
    /// with one slice per near cascade. A dense grid is displaced by the horizontal
    /// spectral displacement and the resulting area compression is accumulated
    /// additively, so caustic cells track individual wave crests instead of scrolling
    /// an authored texture.
    /// </summary>
    internal static class SolWaterCausticRenderGraph
    {
        static readonly int DisplacementId =
            Shader.PropertyToID("_SolWaterCausticDisplacement");
        static readonly int ParamsId = Shader.PropertyToID("_SolWaterCausticParams");
        static readonly int CausticArrayId =
            Shader.PropertyToID("_SolWaterCausticArray");
        static readonly int CausticArrayParamsId =
            Shader.PropertyToID("_SolWaterCausticArrayParams");

        /// <summary>
        /// Only the two shortest cascades carry wavelengths that focus usefully at
        /// swimmable depth; the long swells bend light over hundreds of metres and
        /// contribute an almost flat field. Matching the donor's two-cascade budget.
        /// </summary>
        internal const int CausticCascadeCount = 2;

        /// <summary>
        /// Cascade domain size in metres, delegated to the one CPU-side definition rather
        /// than restated here.
        ///
        /// This used to carry its own table of 32 / 128 / 512 / 2048 while claiming to
        /// mirror SolWaterWaves2.hlsl, which had already moved to 5 / 20 / 100 / 600 along
        /// with SolWaterFFT.compute and SolWaterFftReadback. The caustic pass was
        /// therefore dividing displacement by a domain about 6.4x too large and sampling
        /// the result back over that same wrong domain, so the projected pattern sat at
        /// the wrong scale relative to the waves that produced it. Delegating removes the
        /// fourth copy that made the drift possible.
        /// </summary>
        internal static float CascadeDomainSize(int cascade) =>
            SolWaterFftReadback.CascadeSize(cascade);

        sealed class PassData
        {
            public Material Material;
            public Mesh Grid;
            public TextureHandle Displacement;
            public int Cascade;
            public Vector4 Params;
        }

        static Mesh _grid;
        static int _gridDensity;
        static int _gridMarginQuads;

        /// <summary>
        /// How far past the cascade domain the grid extends, in UV, on every side.
        ///
        /// This is what keeps the domain seamless. Rasterization does not wrap, so a quad
        /// displaced past the edge is clipped and leaves the strip it vacated at zero,
        /// which the surface reads as darkening along a world-locked line. Beginning the
        /// grid outside the domain means the quads that displace inward are rasterized
        /// instead. Quads that stay outside are clipped before they cost a fragment, so
        /// this covers the same wrap region as replicating the grid over the eight
        /// neighbouring tiles at a fraction of the vertex count.
        ///
        /// Must exceed the largest horizontal displacement in UV, which is
        /// `displacement / domain * choppiness`. The domain shrinks with the cascade but
        /// so does the displacement it carries, so one margin covers both.
        /// </summary>
        const float GridMargin = 0.25f;

        /// <summary>
        /// Quads per unit of cascade UV. Deliberately finer than the FFT texture: area
        /// compression is a derivative, and a grid at texture resolution measures it with
        /// the crudest one-texel finite difference available.
        ///
        /// Density does not affect how much energy lands on the target — see the note on
        /// fragment energy in <see cref="Record"/> — so this trades vertex cost for a
        /// better conditioned derivative and nothing else.
        /// </summary>
        static int GridDensity(int resolution) =>
            Mathf.Clamp(Mathf.RoundToInt(resolution * 1.5f), 64, 384);

        /// <summary>
        /// A regular grid over the cascade domain in UV space, extended by
        /// <see cref="GridMargin"/> on every side. Density drives how finely the
        /// area-compression derivative is sampled, which is what resolves individual
        /// caustic cells rather than a smooth wash.
        /// </summary>
        static Mesh GetGrid(int density, float margin)
        {
            int marginQuads = Mathf.CeilToInt(density * margin);
            if (_grid != null && _gridDensity == density
                && _gridMarginQuads == marginQuads)
                return _grid;

            if (_grid != null)
                CoreUtils.Destroy(_grid);

            // The step stays exactly one quad of the interior grid, and the margin is a
            // whole number of those quads, so the covered region is [-margin, 1+margin]
            // with the interior still landing on the same lattice it always did.
            int spanQuads = density + 2 * marginQuads;
            float step = 1f / density;
            float origin2D = -marginQuads * step;

            int verticesPerSide = spanQuads + 1;
            Vector3[] positions = new Vector3[verticesPerSide * verticesPerSide];
            Vector2[] uvs = new Vector2[positions.Length];
            for (int y = 0; y <= spanQuads; y++)
            {
                for (int x = 0; x <= spanQuads; x++)
                {
                    int index = y * verticesPerSide + x;
                    // UVs run outside [0,1] in the margin. The displacement texture is
                    // sampled with Repeat, so a margin vertex reads the wrapped source
                    // it stands in for.
                    Vector2 uv = new(origin2D + x * step, origin2D + y * step);
                    uvs[index] = uv;
                    positions[index] = new Vector3(uv.x, uv.y, 0f);
                }
            }

            int[] indices = new int[spanQuads * spanQuads * 6];
            int cursor = 0;
            for (int y = 0; y < spanQuads; y++)
            {
                for (int x = 0; x < spanQuads; x++)
                {
                    int origin = y * verticesPerSide + x;
                    indices[cursor++] = origin;
                    indices[cursor++] = origin + verticesPerSide;
                    indices[cursor++] = origin + 1;
                    indices[cursor++] = origin + 1;
                    indices[cursor++] = origin + verticesPerSide;
                    indices[cursor++] = origin + verticesPerSide + 1;
                }
            }

            _grid = new Mesh
            {
                name = "Sol Water Caustic Grid",
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = positions.Length > 65535
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16,
            };
            _grid.SetVertices(positions);
            _grid.SetUVs(0, uvs);
            _grid.SetIndices(indices, MeshTopology.Triangles, 0, false);
            // The grid is drawn with an identity clip transform, so real bounds would
            // only invite culling.
            _grid.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
            _gridDensity = density;
            _gridMarginQuads = marginQuads;
            return _grid;
        }

        internal static void Release()
        {
            if (_grid != null)
                CoreUtils.Destroy(_grid);
            _grid = null;
            _gridDensity = 0;
            _gridMarginQuads = 0;
        }

        /// <summary>
        /// Records one additive draw per cascade and publishes the array globally.
        /// Returns an invalid handle when caustics are disabled or the FFT produced
        /// nothing, in which case the surface falls back to the authored texture.
        /// </summary>
        internal static TextureHandle Record(
            RenderGraph renderGraph,
            Material causticMaterial,
            TextureHandle displacement,
            int resolution,
            int cascadeCount,
            float displacementGain)
        {
            if (causticMaterial == null || !displacement.IsValid() || cascadeCount <= 0)
                return default;

            int slices = Mathf.Min(CausticCascadeCount, cascadeCount);
            TextureDesc desc = new(resolution, resolution)
            {
                colorFormat = GraphicsFormat.R16_SFloat,
                slices = slices,
                dimension = TextureDimension.Tex2DArray,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat,
                clearBuffer = true,
                clearColor = Color.clear,
                name = "_SolWaterCausticArray",
            };
            TextureHandle caustic = renderGraph.CreateTexture(desc);

            Mesh grid = GetGrid(GridDensity(resolution), GridMargin);

            for (int cascade = 0; cascade < slices; cascade++)
            {
                using IRasterRenderGraphBuilder builder =
                    renderGraph.AddRasterRenderPass<PassData>(
                        $"Sol Water Caustic Cascade {cascade}", out PassData passData);

                passData.Material = causticMaterial;
                passData.Grid = grid;
                passData.Displacement = displacement;
                passData.Cascade = cascade;
                // Energy each fragment deposits, and it is deliberately 1.0 rather than a
                // function of grid density.
                //
                // A quad writes flatArea/displacedArea to every texel it covers, so it
                // lays down textureResolution^2 * flatArea * energy in total. Summed over
                // the density^2 quads, each of flat area 1/density^2, the mean over the
                // target is exactly `energy` whatever the density. Grid density therefore
                // changes how well the derivative is resolved and nothing about the
                // field's level, which is what makes GridDensity a free knob.
                //
                // The previous expression multiplied a 1/gridResolution scale straight
                // back out to this same 1.0 and read as though it were compensating for
                // something, which is what made the coupling look fragile.
                const float fragmentEnergy = 1f;
                passData.Params = new Vector4(
                    cascade,
                    CascadeDomainSize(cascade),
                    displacementGain,
                    fragmentEnergy);

                builder.UseTexture(displacement, AccessFlags.Read);
                builder.SetRenderAttachment(caustic, 0, AccessFlags.Write, 0, cascade);
                builder.AllowPassCulling(false);
                // Publish on the last cascade, once every slice has been accumulated.
                if (cascade == slices - 1)
                    builder.SetGlobalTextureAfterPass(caustic, CausticArrayId);
                builder.SetRenderFunc(static (PassData data, RasterGraphContext context) =>
                {
                    data.Material.SetTexture(DisplacementId, data.Displacement);
                    data.Material.SetVector(ParamsId, data.Params);
                    // A single draw: the grid already extends past the domain by
                    // GridMargin on every side, so the quads that wrap in are part of
                    // this mesh and the ones that stay outside are clipped for free.
                    context.cmd.DrawMesh(data.Grid, Matrix4x4.identity, data.Material, 0, 0);
                });
            }

            return caustic;
        }

        /// <summary>
        /// Cascade domains and validity, so the surface and underwater passes sample the
        /// array over exactly the domain each slice was rendered across.
        /// </summary>
        internal static Vector4 ArrayParams(int cascadeCount, bool valid) => new(
            CascadeDomainSize(0),
            CascadeDomainSize(1),
            Mathf.Min(CausticCascadeCount, Mathf.Max(0, cascadeCount)),
            valid ? 1f : 0f);

        internal static int ArrayParamsId => CausticArrayParamsId;
    }
}
