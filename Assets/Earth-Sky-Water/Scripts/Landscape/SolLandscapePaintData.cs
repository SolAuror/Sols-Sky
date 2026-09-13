using System;
using UnityEngine;

namespace Sol.Landscape
{
    [PreferBinarySerialization]
    public sealed class SolLandscapePaintData : ScriptableObject
    {
        [SerializeField] int version = 2;
        [SerializeField] int resolution = 512;
        [SerializeField, HideInInspector] byte[] overrides0, overrides1, exclusions0, exclusions1, removals0, removals1;
        [NonSerialized] RenderTexture[] working = new RenderTexture[6];
        [NonSerialized] public int Revision;
        public int Resolution => resolution;
        public int Version => version;
        public bool HasOverrides => overrides0 != null && overrides0.Length > 0;
        public bool HasExclusions => exclusions0 != null && exclusions0.Length > 0;
        public bool HasRemovals => removals0 != null && removals0.Length > 0;
        public long StorageBytes => (HasOverrides ? 8L * resolution * resolution : 0) + (HasExclusions ? 8L * resolution * resolution : 0) + (HasRemovals ? 8L * resolution * resolution : 0);
        public byte[] Bytes(int i) => i == 0 ? overrides0 : i == 1 ? overrides1 : i == 2 ? exclusions0 : i == 3 ? exclusions1 : i == 4 ? removals0 : i == 5 ? removals1 : throw new ArgumentOutOfRangeException(nameof(i));
        void SetBytes(int i, byte[] data) { if (i == 0) overrides0 = data; else if (i == 1) overrides1 = data; else if (i == 2) exclusions0 = data; else if (i == 3) exclusions1 = data; else if (i == 4) removals0 = data; else if (i == 5) removals1 = data; else throw new ArgumentOutOfRangeException(nameof(i)); }
        public void Initialize(int size) { if (HasOverrides || HasExclusions || HasRemovals) throw new InvalidOperationException("Use Resize to retain painted data."); resolution = Mathf.Clamp(size, 64, 2048); }
        public Texture GetTexture(int i) => Bytes(i)?.Length > 0 ? GetWorking(i) : Texture2D.blackTexture;
        public RenderTexture GetWorking(int i)
        {
            working ??= new RenderTexture[6];
            if (working[i] != null)
            {
                if (!working[i].IsCreated()) { working[i].Create(); Upload(i); }
                return working[i];
            }
            working[i] = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                { name = name + " paint " + i, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            working[i].Create(); Upload(i); return working[i];
        }
        public void Allocate(bool exclusion) => AllocateChannels(exclusion ? 1 : 0);
        public void AllocateChannels(int kind)
        {
            if (kind < 0 || kind > 2) throw new ArgumentOutOfRangeException(nameof(kind));
            version = 2;
            int first = kind * 2;
            for (int i = first; i < first + 2; i++) if (Bytes(i) == null || Bytes(i).Length == 0) SetBytes(i, new byte[resolution * resolution * 4]);
            Revision++;
        }
        void Upload(int i)
        {
            var pixels = Bytes(i);
            if (pixels == null || pixels.Length == 0) { var old = RenderTexture.active; RenderTexture.active = working[i]; GL.Clear(false, true, Color.clear); RenderTexture.active = old; return; }
            var texture = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false, true);
            var previous = RenderTexture.active;
            try { texture.LoadRawTextureData(pixels); texture.Apply(false, false); Graphics.Blit(texture, working[i]); }
            finally { RenderTexture.active = previous; DestroyTexture(texture); }
        }
        public void CommitRegion(int i, RectInt rect)
        {
            if (rect.width <= 0 || rect.height <= 0) return;
            var texture = new Texture2D(rect.width, rect.height, TextureFormat.RGBA32, false, true);
            var old = RenderTexture.active;
            try
            {
                RenderTexture.active = GetWorking(i); texture.ReadPixels(new Rect(rect.x, rect.y, rect.width, rect.height), 0, 0, false);
                var raw = texture.GetRawTextureData<byte>(); var target = Bytes(i);
                for (int y = 0; y < rect.height; y++) for (int x = 0; x < rect.width * 4; x++)
                    target[((rect.y + y) * resolution + rect.x) * 4 + x] = raw[y * rect.width * 4 + x];
            }
            finally { RenderTexture.active = old; DestroyTexture(texture); }
            Revision++;
        }
        public byte[][] Snapshot()
        {
            var result = new byte[6][]; for (int i = 0; i < 6; i++) result[i] = Bytes(i) == null ? null : (byte[])Bytes(i).Clone(); return result;
        }
        public void Restore(byte[][] snapshot)
        { for (int i = 0; i < 6; i++) SetBytes(i, i >= snapshot.Length || snapshot[i] == null ? null : (byte[])snapshot[i].Clone()); RefreshTextures(); }
        public void RefreshTextures() { Release(); Revision++; }
        public void Resize(int size)
        {
            size = Mathf.Clamp(size, 64, 2048); if (size == resolution) return;
            int old = resolution;
            for (int i = 0; i < 6; i++)
            {
                var source = Bytes(i); if (source == null || source.Length == 0) continue;
                var dest = new byte[size * size * 4];
                for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
                {
                    int sx = Mathf.RoundToInt(x * (old - 1f) / (size - 1)); int sy = Mathf.RoundToInt(y * (old - 1f) / (size - 1));
                    Array.Copy(source, (sy * old + sx) * 4, dest, (y * size + x) * 4, 4);
                }
                SetBytes(i, dest);
            }
            resolution = size; RefreshTextures();
        }
        public void Remap(int[] newToOld)
        {
            var source = Snapshot();
            for (int kind = 0; kind < 3; kind++)
            {
                if (source[kind * 2] == null || source[kind * 2].Length == 0) continue;
                for (int i = kind * 2; i < kind * 2 + 2; i++) SetBytes(i, new byte[resolution * resolution * 4]);
                for (int layer = 0; layer < newToOld.Length; layer++)
                {
                    int old = newToOld[layer]; if (old < 0) continue;
                    var from = source[kind * 2 + old / 4]; var to = Bytes(kind * 2 + layer / 4);
                    for (int p = 0; p < resolution * resolution; p++) to[p * 4 + layer % 4] = from[p * 4 + old % 4];
                }
            }
            RefreshTextures();
        }
        void OnDisable() => Release();
        void Release() { if (working == null) return; for (int i = 0; i < 6; i++) { if (working[i] != null) { if(RenderTexture.active==working[i])RenderTexture.active=null;working[i].Release(); DestroyTexture(working[i]); } working[i] = null; } }
        internal static void DestroyTexture(UnityEngine.Object obj) { if (Application.isPlaying) Destroy(obj); else DestroyImmediate(obj); }
    }
}
