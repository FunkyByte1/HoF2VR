using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HoF2VR
{
    /// (from the work/limbo2 prototype, docs/loading-v2-design.md §1) Reads the waiting-space asset pack that
    /// tools/extract_limbo_pack.py builds from the game's own bundles (read-only, at install time).
    /// Everything in it is plain data - meshes as arrays, textures as the game's own compressed blocks
    /// (DXT1/DXT5 with their mips), a text layout - so it can be turned into Unity objects on the very
    /// first frame of the boot, before the game has loaded a single bundle, and it never depends on (or
    /// interferes with) the game's AssetBundleManager.
    ///
    /// Cost: ~10 MB read + ~25 texture/mesh uploads, once per session (~30-80 ms; done in Plugin.Awake
    /// or spread over the first frames with LoadAsync). Missing/corrupt pack = null: the caller keeps the
    /// procedural waiting space (today's Limbo.cs) - never an error the player sees.
    internal sealed class LimboPack
    {
        public const string FileName = "hof2vr_limbo.pack";
        public readonly Dictionary<string, Mesh> Meshes = new Dictionary<string, Mesh>();
        public readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>();
        public readonly Dictionary<string, AudioClip> Sounds = new Dictionary<string, AudioClip>();
        public string Layout = "";
        public static LimboPack Loaded;

        /// The pack next to the plugin DLL (BepInEx/plugins/HoF2VR/hof2vr_limbo.pack).
        public static string DefaultPath
        {
            get
            {
                string dir = Path.GetDirectoryName(typeof(LimboPack).Assembly.Location) ?? ".";
                return Path.Combine(dir, FileName);
            }
        }

        public static LimboPack TryLoad(string path)
        {
            if (Loaded != null) return Loaded;
            if (!File.Exists(path)) { Plugin.L.LogInfo("limbo pack: none at " + path + " - procedural waiting space"); return null; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var p = new LimboPack();
                using (var br = new BinaryReader(File.OpenRead(path)))
                {
                    byte[] magic = br.ReadBytes(8);
                    if (System.Text.Encoding.ASCII.GetString(magic) != "HF2LIMBO") throw new Exception("not a limbo pack");
                    int version = br.ReadInt32();
                    if (version != 1) throw new Exception("pack version " + version);
                    int count = br.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        byte kind = br.ReadByte();
                        string name = br.ReadString();
                        if (kind == 1) p.Meshes[name] = ReadMesh(br, name);
                        else if (kind == 2) p.Textures[name] = ReadTexture(br, name);
                        else if (kind == 3) p.Layout = br.ReadString();
                        else if (kind == 4) p.Sounds[name] = ReadAudio(br, name);
                        else throw new Exception("entry kind " + kind);
                    }
                }
                Loaded = p;
                Plugin.L.LogInfo("limbo pack: " + p.Meshes.Count + " meshes, " + p.Textures.Count + " textures in " + sw.ElapsedMilliseconds + " ms");
                return p;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("limbo pack: could not read " + path + " (" + e.Message + ") - procedural waiting space");
                return null;
            }
        }

        static Mesh ReadMesh(BinaryReader br, string name)
        {
            int n = br.ReadInt32();
            byte flags = br.ReadByte();
            var pos = new Vector3[n];
            for (int i = 0; i < n; i++) pos[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            var m = new Mesh { name = "HoF2VR_" + name };
            m.vertices = pos;
            if ((flags & 1) != 0)
            {
                var nr = new Vector3[n];
                for (int i = 0; i < n; i++) nr[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                m.normals = nr;
            }
            if ((flags & 2) != 0)
            {
                var uv = new Vector2[n];
                for (int i = 0; i < n; i++) uv[i] = new Vector2(br.ReadSingle(), br.ReadSingle());
                m.uv = uv;
            }
            if ((flags & 4) != 0)
            {
                var c = new Color32[n];
                for (int i = 0; i < n; i++) c[i] = new Color32(br.ReadByte(), br.ReadByte(), br.ReadByte(), br.ReadByte());
                m.colors32 = c;
            }
            int subs = br.ReadInt32();
            if (n > 65535) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.subMeshCount = subs;
            for (int s = 0; s < subs; s++)
            {
                int k = br.ReadInt32();
                var idx = new int[k];
                for (int i = 0; i < k; i++) idx[i] = br.ReadInt32();
                m.SetTriangles(idx, s);
            }
            if ((flags & 1) == 0) m.RecalculateNormals();
            m.RecalculateBounds();
            // small meshes stay readable (the emissive overlays are copied and re-coloured), and the ground (a collider
            // for finding level ground under the player, FlatSpot); other big meshes keep no CPU copy
            m.UploadMeshData(n > 4000 && name != "mesh/ground");
            return m;
        }

        static Texture2D ReadTexture(BinaryReader br, string name)
        {
            int w = br.ReadInt32(), h = br.ReadInt32(), fmt = br.ReadInt32(), mips = br.ReadInt32();
            bool linear = br.ReadByte() != 0, clamp = br.ReadByte() != 0;
            int len = br.ReadInt32();
            byte[] data = br.ReadBytes(len);
            // Unity allocates the full mip chain (down to 1x1); a texture packed with fewer levels (the baked ground
            // map stops at 4x4) gets its missing tiny levels padded with copies of its last block / pixel
            if (mips > 1)
            {
                int need = ChainBytes(w, h, (TextureFormat)fmt);
                if (need > data.Length && need - data.Length <= 4096)
                {
                    int unit = BlockBytes((TextureFormat)fmt);
                    var full = new byte[need];
                    Buffer.BlockCopy(data, 0, full, 0, data.Length);
                    for (int o = data.Length; o < need; o++) full[o] = data[data.Length - unit + (o - data.Length) % unit];
                    data = full;
                }
            }
            // TextureFormat ids are the ones the bundles use: 10 DXT1, 12 DXT5, 3 RGB24, 4 RGBA32, 5 ARGB32
            var t = new Texture2D(w, h, (TextureFormat)fmt, mips > 1, linear)
            {
                name = "HoF2VR_" + name,
                wrapMode = clamp ? TextureWrapMode.Clamp : TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,   // mips + trilinear + aniso: no shimmer on the ground
                anisoLevel = 8,
            };
            t.LoadRawTextureData(data);
            t.Apply(false, true);   // keep no CPU copy
            return t;
        }

        /// Bytes per 4x4 block (DXT) or per pixel (uncompressed) of the pack's formats.
        static int BlockBytes(TextureFormat f)
        {
            switch (f)
            {
                case TextureFormat.DXT1: return 8;
                case TextureFormat.DXT5: return 16;
                case TextureFormat.RGB24: return 3;
                default: return 4;
            }
        }

        /// The size of a full mip chain (w x h down to 1x1) in the format.
        static int ChainBytes(int w, int h, TextureFormat f)
        {
            bool block = f == TextureFormat.DXT1 || f == TextureFormat.DXT5;
            int unit = BlockBytes(f), total = 0;
            while (true)
            {
                total += block ? Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * unit : w * h * unit;
                if (w == 1 && h == 1) break;
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
            }
            return total;
        }

        static AudioClip ReadAudio(BinaryReader br, string name)
        {
            int ch = br.ReadInt32(), rate = br.ReadInt32(), frames = br.ReadInt32();
            byte[] pcm = br.ReadBytes(frames * ch * 2);
            var data = new float[frames * ch];
            for (int i = 0; i < data.Length; i++) data[i] = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)) / 32768f;
            AudioClip c = AudioClip.Create("HoF2VR_" + name, frames, ch, rate, false);
            c.SetData(data, 0);
            return c;
        }

        public AudioClip Sound(string key) { AudioClip c; return Sounds.TryGetValue(key, out c) ? c : null; }

        public Mesh Mesh(string key) { Mesh m; return Meshes.TryGetValue(key, out m) ? m : null; }
        public Texture2D Tex(string key) { Texture2D t; return key != null && key != "-" && Textures.TryGetValue(key, out t) ? t : null; }

        // ---- layout ------------------------------------------------------------------------------------

        internal struct Prop
        {
            public string Name, Kind, Mesh, MainTex, EmisTex;
            public Vector3 Pos, Scale;
            public Quaternion Rot;
            public Vector2 Tiling;
            public Color Color;
            public float Emission;
        }

        internal struct Ground
        {
            public string Mesh, BaseTex, Control;
            public string[] Splat;
            public Vector2[] SplatTiling;
        }

        public readonly List<Prop> Props = new List<Prop>();
        public Ground Floor;
        public Vector3 TitleCam;   // the title camera relative to the fire's ground
        public float TitleYaw;
        /// Where the player stands (feet, relative to the fire's ground) and faces: "boot" = the title
        /// camera's spot (6.75 m from the fire, on a rise 0.64 m above it), "near" = 3.2 m from the fire.
        public readonly Dictionary<string, Vector4> Spots = new Dictionary<string, Vector4>();
        public Color FireColour = new Color(1f, 0.56f, 0.1f);
        public float FireRange = 2.5f, FireMin = 2.0f, FireMax = 2.6f, FireSpeed = 5f;

        static float F(string s) { return float.Parse(s, System.Globalization.CultureInfo.InvariantCulture); }
        static Vector3 V3(string s) { var a = s.Split(' '); return new Vector3(F(a[0]), F(a[1]), F(a[2])); }

        public void ParseLayout()
        {
            Props.Clear();
            foreach (string raw in Layout.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                int sp = line.IndexOf(' ');
                string what = sp > 0 ? line.Substring(0, sp) : line, rest = sp > 0 ? line.Substring(sp + 1) : "";
                try
                {
                    if (what == "prop")
                    {
                        var f = rest.Split('|');
                        var q = f[6].Split(' ');
                        var t = f[8].Split(' ');
                        var c = f[9].Split(' ');
                        Props.Add(new Prop
                        {
                            Name = f[0], Kind = f[1], Mesh = f[2], MainTex = f[3], EmisTex = f[4],
                            Pos = V3(f[5]), Rot = new Quaternion(F(q[0]), F(q[1]), F(q[2]), F(q[3])), Scale = V3(f[7]),
                            Tiling = new Vector2(F(t[0]), F(t[1])),
                            Color = new Color(F(c[0]), F(c[1]), F(c[2]), 1f), Emission = F(c[3]),
                        });
                    }
                    else if (what == "ground")
                    {
                        var f = rest.Split('|');
                        Floor.Mesh = f[0]; Floor.BaseTex = f[1]; Floor.Control = f[2];
                        int n = f.Length - 3;
                        Floor.Splat = new string[n];
                        Floor.SplatTiling = new Vector2[n];
                        for (int i = 0; i < n; i++)
                        {
                            var s = f[3 + i].Split(' ');
                            Floor.Splat[i] = s[0];
                            Floor.SplatTiling[i] = new Vector2(F(s[1]), F(s[2]));
                        }
                    }
                    else if (what == "titlecam")
                    {
                        var a = rest.Split(' ');
                        TitleCam = new Vector3(F(a[0]), F(a[1]), F(a[2]));
                        TitleYaw = F(a[3]);
                    }
                    else if (what == "spot")
                    {
                        var a = rest.Split(' ');
                        Spots[a[0]] = new Vector4(F(a[1]), F(a[2]), F(a[3]), F(a[4]));
                    }
                    else if (what == "firelight")
                    {
                        var a = rest.Split(' ');
                        FireColour = new Color(F(a[0]), F(a[1]), F(a[2]));
                        FireRange = F(a[3]); FireMin = F(a[4]); FireMax = F(a[5]); FireSpeed = F(a[6]);
                    }
                }
                catch (Exception e) { Plugin.L.LogWarning("limbo pack: bad layout line '" + line + "': " + e.Message); }
            }
        }
    }
}
