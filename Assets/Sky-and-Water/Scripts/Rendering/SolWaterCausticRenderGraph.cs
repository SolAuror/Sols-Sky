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

        /// <summary>Cascade domain sizes, mirroring the table in SolWaterWaves2.hlsl.</summary>
        internal static float CascadeDomainSize(int cascade) => cascade switch
        {
            0 => 32f,
            1 => 128f,
            2 => 512f,
            _ => 2048f,
        };

        sealed class PassData
        {
            public Material Material;
            public Mesh Grid;
            public TextureHandle Displacement;
            public int Cascade;
            public Vector4 Params;
        }

        static Mesh _grid;
        static int _gridResolution;

        /// <summary>
        /// A regular grid over the cascade domain in UV space. Resolution drives how
        /// finely the area-compression derivative is sampled, which is what resolves
        /// individual caustic cells rather than a smooth wash.
        /// </summary>
        static Mesh GetGrid(int resolution)
        {
            if (_grid != null && _gridResolution == resolution)
                return _grid;

            if (_grid != null)
                CoreUtils.Destroy(_grid);

            int verticesPerSide = resolution + 1;
            Vector3[] positions = new Vector3[verticesPerSide * verticesPerSide];
            Vector2[] uvs = new Vector2[positions.Length];
            for (int y = 0; y <= resolution; y++)
            {
                for (int x = 0; x <= resolution; x++)
                {
                    int index = y * verticesPerSide + x;
                    Vector2 uv = new((float)x / resolution, (float)y / resolution);
                    uvs[index] = uv;
                    positions[index] = new Vector3(uv.x, uv.y, 0f);
                }
            }

            int[] indices = new int[resolution * resolution * 6];
            int cursor = 0;
            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
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
            _gridResolution = resolution;
            return _grid;
        }

        internal static void Release()
        {
            if (_grid != null)
                CoreUtils.Destroy(_grid);
            _grid = null;
            _gridResolution = 0;
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

            // The grid is deliberately denser than the FFT texture: area compression is
            // a derivative, so it needs more samples than the signal it differentiates.
            Mesh grid = GetGrid(Mathf.Clamp(resolution, 64, 256));

            for (int cascade = 0; cascade < slices; cascade++)
            {
                using IRasterRenderGraphBuilder builder =
                    renderGraph.AddRasterRenderPass<PassData>(
                        $"Sol Water Caustic Cascade {cascade}", out PassData passData);

                passData.Material = causticMaterial;
                passData.Grid = grid;
                passData.Displacement = displacement;
                passData.Cascade = cascade;
                // The intensity scale compensates for every grid quad contributing
                // additively; without it the accumulated field scales with grid density.
                float intensityScale = 1f / Mathf.Max(1, _gridResolution);
                passData.Params = new Vector4(
                    cascade,
                    CascadeDomainSize(cascade),
                    displacementGain,
                    intensityScale * _gridResolution);

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
