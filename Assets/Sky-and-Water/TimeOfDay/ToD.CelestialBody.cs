using UnityEngine;

namespace Sol.ToD
{
    /// <summary>
    /// Positions a camera-facing billboard quad on a sky orbit and drives
    /// its procedural material (UV-reconstructed sphere normals, eclipse tint).
    /// Attach to the root of a sun/moon/planet prefab that has a MeshFilter,
    /// MeshRenderer and optionally a Light.  Driven each frame by <see cref="TimeofDay"/>.
    /// </summary>
    public class CelestialBody : MonoBehaviour
    {
        #region Inspector Settings
        [Header("-- Config ------------------------")]
        [Tooltip("Optional ScriptableObject config. Values here override inspector defaults.")]
        [SerializeField] CelestialBodyConfig config;

        [Header("-- Fallback (used when config is null) --")]
        [Tooltip("Distance from the camera the body orbits at.")]
        [SerializeField] float orbitDistance = 800f;

        [Tooltip("Base lit-side color. Leave white to keep material default.")]
        [SerializeField] Color baseColor = Color.white;

        [Tooltip("Dark-side hemisphere color.")]
        [SerializeField] Color darkSideColor = new Color(0.02f, 0.02f, 0.04f);

        [Tooltip("Self-luminous emission (HDR). Use bright values for stars.")]
        [ColorUsage(true, true)]
        [SerializeField] Color emissionColor = Color.black;

        [Tooltip("Terminator sharpness (1 = soft, 10 = hard edge).")]
        [Range(1f, 10f)]
        [SerializeField] float terminatorSharpness = 3f;

        [Tooltip("Eclipse tint color.")]
        [SerializeField] Color eclipseTintColor = new Color(0.6f, 0.15f, 0.1f);

        [Tooltip("Optional full-disc surface texture (moon face). Leave null for a flat-colored disc.")]
        [SerializeField] Texture2D surfaceTexture;

        [Tooltip("Rotation of the surface texture on the disc, in degrees.")]
        [Range(0f, 360f)]
        [SerializeField] float surfaceRotation;
        #endregion

        // -- Public state (set by TimeofDay each frame) --

        /// <summary>Direction from camera toward this body.</summary>
        public Vector3 Direction { get; set; } = Vector3.up;

        /// <summary>0 = fully visible, 1 = fully eclipsed.</summary>
        public float EclipseFactor { get; set; }

        /// <summary>Per-frame color override. Null = use config/inspector baseColor.</summary>
        public Color? ColorOverride { get; set; }

        /// <summary>World-space direction toward the sun (for dark-side computation).</summary>
        public Vector3 SunDirection { get; set; } = Vector3.up;

        /// <summary>The Light found on this prefab (cached at Awake).</summary>
        public Light AttachedLight { get; private set; }

        /// <summary>The active config (if any).</summary>
        public CelestialBodyConfig Config => config;

        // -- Internals --

        Renderer rend;
        MaterialPropertyBlock propBlock;
        Camera cachedCamera;

        static Mesh sharedQuad;

        static readonly int BaseColorID    = Shader.PropertyToID("_BaseColor");
        static readonly int DarkColorID    = Shader.PropertyToID("_DarkColor");
        static readonly int EmissionID     = Shader.PropertyToID("_EmissionColor");
        static readonly int SunDirID       = Shader.PropertyToID("_SunDirection");
        static readonly int SharpnessID    = Shader.PropertyToID("_TerminatorSharpness");
        static readonly int EclipseFactID  = Shader.PropertyToID("_EclipseFactor");
        static readonly int EclipseTintID  = Shader.PropertyToID("_EclipseTint");
        static readonly int SurfaceTexID   = Shader.PropertyToID("_SurfaceTex");
        static readonly int SurfaceRotID   = Shader.PropertyToID("_SurfaceRotation");

        void Awake()
        {
            AttachedLight = GetComponentInChildren<Light>();
            rend = GetComponentInChildren<Renderer>();
            propBlock = new MaterialPropertyBlock();

            // Replace whatever mesh the prefab shipped with (sphere, etc.)
            // with a shared unit quad. The shader reconstructs a sphere via UVs.
            var mf = GetComponentInChildren<MeshFilter>();
            if (mf != null)
                mf.sharedMesh = GetQuadMesh();
        }

        /// <summary>Assign a config at runtime (called by TimeofDay after instantiation).</summary>
        public void Initialize(CelestialBodyConfig cfg) => config = cfg;

        /// <summary>Call once per frame (from TimeofDay) to position, billboard, and tint the body.</summary>
        public void Refresh()
        {
            if (cachedCamera == null) cachedCamera = Camera.main;
            Camera cam = cachedCamera;
            if (cam == null) return;

            bool visible = Direction.y > -0.05f;
            gameObject.SetActive(visible);
            if (!visible) return;

            // Resolve values: config wins if present, else use inspector fields
            float dist      = config != null ? config.orbitDistance         : orbitDistance;
            Color litCol    = ColorOverride ?? (config != null ? config.baseColor : baseColor);
            Color darkCol   = config != null ? config.darkSideColor        : darkSideColor;
            Color emit      = config != null ? config.emissionColor        : emissionColor;
            float sharp     = config != null ? config.terminatorSharpness  : terminatorSharpness;
            Color eclTint   = config != null ? config.eclipseTintColor     : eclipseTintColor;
            Texture2D surfTex = config != null ? config.surfaceTexture     : surfaceTexture;
            float surfRot   = config != null ? config.surfaceRotation      : surfaceRotation;

            // Position on orbit around camera
            transform.position = cam.transform.position + Direction.normalized * dist;

            // Billboard: match camera rotation so the quad always faces the viewer.
            transform.rotation = cam.transform.rotation;

            // Push all properties via MaterialPropertyBlock (no material clone).
            if (rend != null)
            {
                rend.GetPropertyBlock(propBlock);
                propBlock.SetColor(BaseColorID,   litCol);
                propBlock.SetColor(DarkColorID,   darkCol);
                propBlock.SetColor(EmissionID,    emit);
                propBlock.SetVector(SunDirID,     SunDirection);
                propBlock.SetFloat(SharpnessID,   sharp);
                propBlock.SetFloat(EclipseFactID, EclipseFactor);
                propBlock.SetColor(EclipseTintID, eclTint);
                // MaterialPropertyBlock.SetTexture rejects null; when no
                // texture is assigned the material's default (white) applies.
                if (surfTex != null)
                    propBlock.SetTexture(SurfaceTexID, surfTex);
                propBlock.SetFloat(SurfaceRotID, surfRot);
                rend.SetPropertyBlock(propBlock);
            }
        }

        /// <summary>
        /// Returns a shared unit quad mesh (4 verts, 2 tris) for all celestial bodies.
        /// </summary>
        static Mesh GetQuadMesh()
        {
            if (sharedQuad != null) return sharedQuad;

            sharedQuad = new Mesh { name = "CelestialBodyQuad" };
            sharedQuad.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f),
            };
            sharedQuad.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
            };
            sharedQuad.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            sharedQuad.bounds = new Bounds(Vector3.zero, Vector3.one);
            return sharedQuad;
        }
    }
}
