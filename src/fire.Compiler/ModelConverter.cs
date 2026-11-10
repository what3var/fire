using System.Text;
using System.Text.Json;

namespace fire.Compiler
{
    /// <summary>
    /// Makes fire's model file (.f3d, docs/graphics/models.md; read by packages/scene3d/native/s3d_model.h) from glTF 2.0 (.gltf with its buffers - data URIs or files next to
    /// it - or .glb). A node becomes a part (in the order parent before child), its mesh the part's triangles (all primitives of triangles merged; their first material's
    /// base colour the part's colour), its translation / rotation / scale (or matrix) the rest pose. An animation becomes a clip: every channel is sampled at
    /// <see cref="FramesPerSecond"/> (linear, step and cubic spline - its values), and a frame holds the place of every part. Parts are rigid: skins are not read.
    /// Called for a resource "x.gltf" / "x.glb" (the program gets the .f3d) and by `forge model`.
    /// </summary>
    public static class ModelConverter
    {
        public const int FramesPerSecond = 30;

        public static bool IsModelSource(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".gltf" || ext == ".glb" || ext == BlockModel.Extension || ext == MeshModel.Extension;
        }

        /// <summary>The .f3d of a glTF file; InvalidDataException with the reason when it cannot be read.</summary>
        public static byte[] ConvertFile(string path, int fps = FramesPerSecond)
        {
            if (Path.GetExtension(path).Equals(BlockModel.Extension, StringComparison.OrdinalIgnoreCase)) return ConvertBlocks(File.ReadAllText(path));
            if (Path.GetExtension(path).Equals(MeshModel.Extension, StringComparison.OrdinalIgnoreCase)) return ConvertMesh(File.ReadAllText(path));
            byte[] data = File.ReadAllBytes(path);
            string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            return Convert(data, name => File.ReadAllBytes(Path.Combine(dir, Uri.UnescapeDataString(name))), fps);
        }

        /// <summary>A part as the writer takes it: the rest pose relative to the parent, the mesh in the part's space (normals of unit length), the colour (RGBA) or none.</summary>
        internal sealed class Part
        {
            public int Parent = -1;
            public string Name = "";
            public double[] T = { 0, 0, 0 }, R = { 0, 0, 0, 1 }, S = { 1, 1, 1 };
            public List<double[]> Positions = new(), Normals = new(), Uvs = new();
            public List<int> Indices = new();
            public byte[]? Color;
            public bool HasUv;
        }

        /// <summary>The .f3d of a block model (`.fblocks`, docs/graphics/models.md - the block editor of spark).</summary>
        public static byte[] ConvertBlocks(string text) => BlockModel.Parse(text).ToF3d();

        /// <summary>The .f3d of a mesh model (`.fmesh`, docs/graphics/models.md - the CAD editor of spark).</summary>
        public static byte[] ConvertMesh(string text) => MeshModel.Parse(text).ToF3d();

        public static byte[] Convert(byte[] file, Func<string, byte[]> readExternal, int fps = FramesPerSecond)
        {
            JsonElement root;
            byte[]? glbBin = null;
            if (file.Length >= 12 && BitConverter.ToUInt32(file, 0) == 0x46546C67)
            {
                // .glb: a header, then chunks (JSON, BIN)
                int at = 12;
                string? json = null;
                while (at + 8 <= file.Length)
                {
                    int len = BitConverter.ToInt32(file, at);
                    uint type = BitConverter.ToUInt32(file, at + 4);
                    if (at + 8 + len > file.Length) throw new InvalidDataException("The .glb file is cut off.");
                    if (type == 0x4E4F534A) json = Encoding.UTF8.GetString(file, at + 8, len);
                    else if (type == 0x004E4942) glbBin = file.AsSpan(at + 8, len).ToArray();
                    at += 8 + len;
                }
                if (json == null) throw new InvalidDataException("The .glb file has no JSON.");
                root = JsonDocument.Parse(json).RootElement;
            }
            else
            {
                try { root = JsonDocument.Parse(file).RootElement; }
                catch (JsonException e) { throw new InvalidDataException("Not glTF (" + e.Message + ")."); }
            }
            if (!root.TryGetProperty("asset", out var asset) || !asset.TryGetProperty("version", out var ver) || !(ver.GetString() ?? "").StartsWith("2"))
                throw new InvalidDataException("Only glTF 2.0 is read.");

            // buffers
            var buffers = new List<byte[]>();
            if (root.TryGetProperty("buffers", out var bufs))
                foreach (var b in bufs.EnumerateArray())
                {
                    if (b.TryGetProperty("uri", out var uriEl))
                    {
                        string uri = uriEl.GetString() ?? "";
                        if (uri.StartsWith("data:", StringComparison.Ordinal))
                        {
                            int comma = uri.IndexOf(',');
                            if (comma < 0 || !uri.Substring(0, comma).EndsWith(";base64")) throw new InvalidDataException("A data URI of a buffer that is not base64.");
                            buffers.Add(System.Convert.FromBase64String(uri.Substring(comma + 1)));
                        }
                        else buffers.Add(readExternal(uri));
                    }
                    else buffers.Add(glbBin ?? throw new InvalidDataException("A buffer without data."));
                }

            JsonElement Arr(string name) => root.TryGetProperty(name, out var a) ? a : default;
            var accessors = Arr("accessors");
            var views = Arr("bufferViews");

            double[][] Read(int index)
            {
                var acc = accessors[index];
                int count = acc.GetProperty("count").GetInt32();
                int comp = acc.GetProperty("componentType").GetInt32();
                string type = acc.GetProperty("type").GetString() ?? "SCALAR";
                bool normalized = acc.TryGetProperty("normalized", out var nz) && nz.GetBoolean();
                int n = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => throw new InvalidDataException("Accessor type " + type + " is not read.") };
                int size = comp switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw new InvalidDataException("Component type " + comp + " is not read.") };
                var result = new double[count][];
                if (!acc.TryGetProperty("bufferView", out var bvEl)) { for (int i = 0; i < count; i++) result[i] = new double[n]; return result; }
                var bv = views[bvEl.GetInt32()];
                byte[] buf = buffers[bv.GetProperty("buffer").GetInt32()];
                int start = (bv.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0) + (acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
                int stride = bv.TryGetProperty("byteStride", out var st) ? st.GetInt32() : n * size;
                for (int i = 0; i < count; i++)
                {
                    var v = new double[n];
                    for (int k = 0; k < n; k++)
                    {
                        int at = start + i * stride + k * size;
                        if (at + size > buf.Length) throw new InvalidDataException("An accessor reaches past its buffer.");
                        v[k] = comp switch
                        {
                            5126 => BitConverter.ToSingle(buf, at),
                            5125 => BitConverter.ToUInt32(buf, at),
                            5123 => normalized ? BitConverter.ToUInt16(buf, at) / 65535.0 : BitConverter.ToUInt16(buf, at),
                            5122 => normalized ? Math.Max(BitConverter.ToInt16(buf, at) / 32767.0, -1) : BitConverter.ToInt16(buf, at),
                            5121 => normalized ? buf[at] / 255.0 : buf[at],
                            _ => normalized ? Math.Max((sbyte)buf[at] / 127.0, -1) : (sbyte)buf[at],
                        };
                    }
                    result[i] = v;
                }
                return result;
            }

            // the nodes of the scene, parent before child
            var nodes = Arr("nodes");
            if (nodes.ValueKind != JsonValueKind.Array || nodes.GetArrayLength() == 0) throw new InvalidDataException("The glTF file has no nodes.");
            var roots = new List<int>();
            if (root.TryGetProperty("scenes", out var scenes) && scenes.GetArrayLength() > 0)
            {
                int si = root.TryGetProperty("scene", out var sc) ? sc.GetInt32() : 0;
                foreach (var r in scenes[si].GetProperty("nodes").EnumerateArray()) roots.Add(r.GetInt32());
            }
            else
            {
                var isChild = new HashSet<int>();
                foreach (var nd in nodes.EnumerateArray()) if (nd.TryGetProperty("children", out var ch)) foreach (var c in ch.EnumerateArray()) isChild.Add(c.GetInt32());
                for (int i = 0; i < nodes.GetArrayLength(); i++) if (!isChild.Contains(i)) roots.Add(i);
            }
            var parts = new List<Part>();
            var partOfNode = new Dictionary<int, int>();
            var materials = Arr("materials");
            var meshes = Arr("meshes");
            void Visit(int node, int parent)
            {
                if (partOfNode.ContainsKey(node)) throw new InvalidDataException("A node is in the tree twice.");
                var nd = nodes[node];
                var p = new Part { Parent = parent, Name = nd.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "node" + node };
                if (nd.TryGetProperty("matrix", out var mx)) Decompose(mx.EnumerateArray().Select(e => e.GetDouble()).ToArray(), p);
                if (nd.TryGetProperty("translation", out var t)) p.T = t.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                if (nd.TryGetProperty("rotation", out var r)) p.R = r.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                if (nd.TryGetProperty("scale", out var s)) p.S = s.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                if (nd.TryGetProperty("mesh", out var meshEl)) ReadMesh(meshes[meshEl.GetInt32()], p);
                int index = parts.Count;
                parts.Add(p);
                partOfNode[node] = index;
                if (parts.Count > 32767) throw new InvalidDataException("Too many parts.");
                if (nd.TryGetProperty("children", out var ch)) foreach (var c in ch.EnumerateArray()) Visit(c.GetInt32(), index);
            }
            void ReadMesh(JsonElement mesh, Part p)
            {
                foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
                {
                    if (prim.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4) continue;   // triangles only
                    var attrs = prim.GetProperty("attributes");
                    if (!attrs.TryGetProperty("POSITION", out var posEl)) continue;
                    var pos = Read(posEl.GetInt32());
                    var nrm = attrs.TryGetProperty("NORMAL", out var nEl) ? Read(nEl.GetInt32()) : null;
                    var uv = attrs.TryGetProperty("TEXCOORD_0", out var uEl) ? Read(uEl.GetInt32()) : null;
                    int baseIndex = p.Positions.Count;
                    var idx = prim.TryGetProperty("indices", out var iEl) ? Read(iEl.GetInt32()).Select(v => (int)v[0]).ToArray() : Enumerable.Range(0, pos.Length).ToArray();
                    p.Positions.AddRange(pos);
                    for (int i = 0; i < pos.Length; i++)
                    {
                        p.Normals.Add(nrm != null ? nrm[i] : new double[3]);
                        p.Uvs.Add(uv != null ? uv[i] : new double[2]);
                    }
                    if (uv != null) p.HasUv = true;
                    foreach (int i in idx) { if (i < 0 || i >= pos.Length) throw new InvalidDataException("An index past the vertices."); p.Indices.Add(baseIndex + i); }
                    if (nrm == null) SmoothNormals(p, baseIndex, idx);
                    if (p.Color == null && prim.TryGetProperty("material", out var matEl) && materials.ValueKind == JsonValueKind.Array)
                    {
                        var mat = materials[matEl.GetInt32()];
                        double[] c = { 1, 1, 1, 1 };
                        if (mat.TryGetProperty("pbrMetallicRoughness", out var pbr) && pbr.TryGetProperty("baseColorFactor", out var bc)) c = bc.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                        p.Color = c.Select(v => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255)).ToArray();
                    }
                }
                if (p.Positions.Count > 65535) throw new InvalidDataException($"The mesh of '{p.Name}' has {p.Positions.Count} corners (at most 65535 a part).");
            }
            foreach (int r in roots) Visit(r, -1);

            // the clips
            var clips = new List<(string Name, int Frames, double[][][] Poses, bool Loop)>();
            if (root.TryGetProperty("animations", out var anims))
            {
                int ai = 0;
                foreach (var anim in anims.EnumerateArray())
                {
                    string name = anim.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                    if (name.Length == 0) name = "animation" + ai;
                    ai++;
                    var samplers = anim.GetProperty("samplers");
                    var channels = new List<(int Part, string Path, double[] Times, double[][] Values, string Interp)>();
                    double duration = 0;
                    foreach (var ch in anim.GetProperty("channels").EnumerateArray())
                    {
                        var target = ch.GetProperty("target");
                        if (!target.TryGetProperty("node", out var tn) || !partOfNode.TryGetValue(tn.GetInt32(), out int part)) continue;
                        string path = target.GetProperty("path").GetString() ?? "";
                        if (path != "translation" && path != "rotation" && path != "scale") continue;
                        var smp = samplers[ch.GetProperty("sampler").GetInt32()];
                        double[] times = Read(smp.GetProperty("input").GetInt32()).Select(v => v[0]).ToArray();
                        double[][] values = Read(smp.GetProperty("output").GetInt32());
                        string interp = smp.TryGetProperty("interpolation", out var ip) ? ip.GetString() ?? "LINEAR" : "LINEAR";
                        if (times.Length == 0) continue;
                        duration = Math.Max(duration, times[^1]);
                        channels.Add((part, path, times, values, interp));
                    }
                    // a clip loops: its frames cover [0, duration) - the last key is the first again
                    int frames = Math.Max(1, (int)Math.Round(duration * fps));
                    var poses = new double[frames][][];
                    for (int f = 0; f < frames; f++)
                    {
                        double time = (double)f / fps;
                        poses[f] = new double[parts.Count][];
                        for (int k = 0; k < parts.Count; k++) poses[f][k] = parts[k].T.Concat(parts[k].R).Concat(parts[k].S).ToArray();
                        foreach (var c in channels)
                        {
                            double[] v = Sample(c.Times, c.Values, c.Interp, time, c.Path == "rotation");
                            int off = c.Path == "translation" ? 0 : c.Path == "rotation" ? 3 : 7;
                            for (int k = 0; k < v.Length; k++) poses[f][c.Part][off + k] = v[k];
                        }
                    }
                    if (name.Length > 31) name = name.Substring(0, 31);
                    clips.Add((name, frames, poses, true));
                }
                if (clips.Count > 65535) throw new InvalidDataException("Too many animations.");
            }

            return Write(parts, clips, fps);
        }

        /// <summary>The bytes of an .f3d: the parts (parent before child) and the clips (frames x parts of translation, rotation, scale - 10 numbers).</summary>
        internal static byte[] Write(List<Part> parts, List<(string Name, int Frames, double[][][] Poses, bool Loop)> clips, int fps)
        {
            // the box of the whole model in its rest pose
            var world = new double[parts.Count][];
            double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
            for (int i = 0; i < parts.Count; i++)
            {
                var m = PoseMatrix(parts[i].T, parts[i].R, parts[i].S);
                world[i] = parts[i].Parent >= 0 ? Mul(world[parts[i].Parent], m) : m;
                foreach (var p in parts[i].Positions)
                    for (int k = 0; k < 3; k++)
                    {
                        double v = world[i][k * 4] * p[0] + world[i][k * 4 + 1] * p[1] + world[i][k * 4 + 2] * p[2] + world[i][k * 4 + 3];
                        lo[k] = Math.Min(lo[k], v); hi[k] = Math.Max(hi[k], v);
                    }
            }
            if (lo[0] > hi[0]) { lo = new double[3]; hi = new double[3]; }

            var o = new MemoryStream();
            var w = new BinaryWriter(o);
            w.Write(Encoding.ASCII.GetBytes("F3D1"));
            w.Write((ushort)1); w.Write((ushort)parts.Count); w.Write((ushort)clips.Count); w.Write((ushort)0);
            foreach (double v in lo) w.Write((float)v);
            foreach (double v in hi) w.Write((float)v);
            foreach (var p in parts)
            {
                w.Write((short)p.Parent);
                w.Write((byte)((p.Color != null ? 1 : 0) | (p.HasUv ? 2 : 0)));
                WriteName(w, p.Name);
                foreach (double v in p.T) w.Write((float)v);
                WriteQuat(w, p.R);
                foreach (double v in p.S) w.Write((float)v);
                var c = p.Color ?? new byte[] { 255, 255, 255, 255 };
                w.Write(c[0]); w.Write(c[1]); w.Write(c[2]); w.Write(c.Length > 3 ? c[3] : (byte)255);
                w.Write((ushort)p.Positions.Count); w.Write((uint)p.Indices.Count);
                double[] plo = { 0, 0, 0 }, phi = { 0, 0, 0 };
                if (p.Positions.Count > 0)
                    for (int k = 0; k < 3; k++) { plo[k] = p.Positions.Min(v => v[k]); phi[k] = p.Positions.Max(v => v[k]); }
                foreach (double v in plo) w.Write((float)v);
                foreach (double v in phi) w.Write((float)v);
                for (int i = 0; i < p.Positions.Count; i++)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        // the decoder reads min + (q + 32768) / 65535 * size, with the box as floats
                        float flo = (float)plo[k], size = (float)phi[k] - flo;
                        double q = size > 0 ? Math.Round(((float)p.Positions[i][k] - flo) / size * 65535.0) - 32768 : -32768;
                        w.Write((short)Math.Clamp(q, -32768, 32767));
                    }
                    var (a, b) = OctNormal(p.Normals[i]);
                    w.Write(a); w.Write(b);
                    if (p.HasUv) { w.Write(Short(p.Uvs[i][0] * 1024)); w.Write(Short(p.Uvs[i][1] * 1024)); }
                }
                foreach (int i in p.Indices) w.Write((ushort)i);
            }
            foreach (var c in clips)
            {
                WriteName(w, c.Name);
                w.Write((byte)(c.Loop ? 1 : 0));
                w.Write((ushort)c.Frames);
                w.Write((float)fps);
                foreach (var frame in c.Poses)
                    foreach (var pose in frame)
                    {
                        for (int k = 0; k < 3; k++) w.Write((float)pose[k]);
                        WriteQuat(w, pose[3..7]);
                        for (int k = 7; k < 10; k++) w.Write(Short(pose[k] * 4096));
                    }
            }
            return o.ToArray();
        }

        private static short Short(double v) => (short)Math.Clamp(Math.Round(v), -32768, 32767);

        private static void WriteName(BinaryWriter w, string name)
        {
            byte[] b = Encoding.UTF8.GetBytes(name);
            if (b.Length > 31) b = b[..31];
            w.Write((byte)b.Length); w.Write(b);
        }

        private static void WriteQuat(BinaryWriter w, double[] q)
        {
            double l = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            if (l < 1e-12) { q = new double[] { 0, 0, 0, 1 }; l = 1; }
            for (int k = 0; k < 4; k++) w.Write(Short(q[k] / l * 32767));
        }

        /// <summary>A normal in two signed bytes (octahedral).</summary>
        private static (sbyte, sbyte) OctNormal(double[] n)
        {
            double x = n[0], y = n[1], z = n[2], l = Math.Abs(x) + Math.Abs(y) + Math.Abs(z);
            if (l < 1e-12) return (0, 127);
            x /= l; y /= l;
            if (z < 0) { double ox = x; x = (1 - Math.Abs(y)) * (ox >= 0 ? 1 : -1); y = (1 - Math.Abs(ox)) * (y >= 0 ? 1 : -1); }
            return ((sbyte)Math.Clamp(Math.Round(x * 127), -127, 127), (sbyte)Math.Clamp(Math.Round(y * 127), -127, 127));
        }

        private static void SmoothNormals(Part p, int baseIndex, int[] idx)
        {
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                var a = p.Positions[baseIndex + idx[t]]; var b = p.Positions[baseIndex + idx[t + 1]]; var c = p.Positions[baseIndex + idx[t + 2]];
                double ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2], vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                foreach (int k in new[] { idx[t], idx[t + 1], idx[t + 2] }) { var n = p.Normals[baseIndex + k]; n[0] += nx; n[1] += ny; n[2] += nz; }
            }
        }

        private static double[] Sample(double[] times, double[][] values, string interp, double time, bool rotation)
        {
            bool cubic = interp == "CUBICSPLINE";
            double[] Value(int i) => cubic ? values[i * 3 + 1] : values[i];
            if (time <= times[0]) return Value(0);
            if (time >= times[^1]) return Value(times.Length - 1);
            int k = Array.BinarySearch(times, time);
            if (k >= 0) return Value(k);
            k = ~k - 1;
            if (interp == "STEP") return Value(k);
            double f = (time - times[k]) / (times[k + 1] - times[k]);
            double[] a = Value(k), b = Value(k + 1), r = new double[a.Length];
            if (rotation)
            {
                double d = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3], s = d < 0 ? -1 : 1;
                for (int i = 0; i < 4; i++) r[i] = a[i] + (b[i] * s - a[i]) * f;
                double l = Math.Sqrt(r.Sum(v => v * v));
                if (l > 1e-12) for (int i = 0; i < 4; i++) r[i] /= l;
                return r;
            }
            for (int i = 0; i < a.Length; i++) r[i] = a[i] + (b[i] - a[i]) * f;
            return r;
        }

        /// <summary>The quaternion (x, y, z, w) of a rotation matrix (row major 3x3 in the first entries of a 4x4).</summary>
        internal static double[] QuatOf(double[] m)
        {
            double r00 = m[0], r01 = m[1], r02 = m[2], r10 = m[4], r11 = m[5], r12 = m[6], r20 = m[8], r21 = m[9], r22 = m[10], tr = r00 + r11 + r22;
            if (tr > 0) { double s = Math.Sqrt(tr + 1) * 2; return new[] { (r21 - r12) / s, (r02 - r20) / s, (r10 - r01) / s, s / 4 }; }
            if (r00 > r11 && r00 > r22) { double s = Math.Sqrt(1 + r00 - r11 - r22) * 2; return new[] { s / 4, (r01 + r10) / s, (r02 + r20) / s, (r21 - r12) / s }; }
            if (r11 > r22) { double s = Math.Sqrt(1 + r11 - r00 - r22) * 2; return new[] { (r01 + r10) / s, s / 4, (r12 + r21) / s, (r02 - r20) / s }; }
            { double s = Math.Sqrt(1 + r22 - r00 - r11) * 2; return new[] { (r02 + r20) / s, (r12 + r21) / s, s / 4, (r10 - r01) / s }; }
        }

        /// <summary>Row major 4x4 (applied to column vectors) of T * R(q) * S.</summary>
        private static double[] PoseMatrix(double[] t, double[] q, double[] s)
        {
            double l = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
            double x = q[0] / l, y = q[1] / l, z = q[2] / l, w = q[3] / l;
            return new[]
            {
                (1 - 2 * (y * y + z * z)) * s[0], 2 * (x * y - z * w) * s[1], 2 * (x * z + y * w) * s[2], t[0],
                2 * (x * y + z * w) * s[0], (1 - 2 * (x * x + z * z)) * s[1], 2 * (y * z - x * w) * s[2], t[1],
                2 * (x * z - y * w) * s[0], 2 * (y * z + x * w) * s[1], (1 - 2 * (x * x + y * y)) * s[2], t[2],
                0, 0, 0, 1,
            };
        }

        private static double[] Mul(double[] a, double[] b)
        {
            var r = new double[16];
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) { double s = 0; for (int k = 0; k < 4; k++) s += a[i * 4 + k] * b[k * 4 + j]; r[i * 4 + j] = s; }
            return r;
        }

        /// <summary>A glTF matrix (column major) into translation, rotation and scale (no shear).</summary>
        private static void Decompose(double[] m, Part p)
        {
            double[] c0 = { m[0], m[1], m[2] }, c1 = { m[4], m[5], m[6] }, c2 = { m[8], m[9], m[10] };
            double Len(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            double sx = Len(c0), sy = Len(c1), sz = Len(c2);
            if (c0[0] * (c1[1] * c2[2] - c1[2] * c2[1]) - c1[0] * (c0[1] * c2[2] - c0[2] * c2[1]) + c2[0] * (c0[1] * c1[2] - c0[2] * c1[1]) < 0) sx = -sx;
            p.T = new[] { m[12], m[13], m[14] };
            p.S = new[] { sx, sy, sz };
            double r00 = c0[0] / sx, r10 = c0[1] / sx, r20 = c0[2] / sx, r01 = c1[0] / sy, r11 = c1[1] / sy, r21 = c1[2] / sy, r02 = c2[0] / sz, r12 = c2[1] / sz, r22 = c2[2] / sz;
            double tr = r00 + r11 + r22;
            double qx, qy, qz, qw;
            if (tr > 0) { double s = Math.Sqrt(tr + 1) * 2; qw = s / 4; qx = (r21 - r12) / s; qy = (r02 - r20) / s; qz = (r10 - r01) / s; }
            else if (r00 > r11 && r00 > r22) { double s = Math.Sqrt(1 + r00 - r11 - r22) * 2; qw = (r21 - r12) / s; qx = s / 4; qy = (r01 + r10) / s; qz = (r02 + r20) / s; }
            else if (r11 > r22) { double s = Math.Sqrt(1 + r11 - r00 - r22) * 2; qw = (r02 - r20) / s; qx = (r01 + r10) / s; qy = s / 4; qz = (r12 + r21) / s; }
            else { double s = Math.Sqrt(1 + r22 - r00 - r11) * 2; qw = (r10 - r01) / s; qx = (r02 + r20) / s; qy = (r12 + r21) / s; qz = s / 4; }
            p.R = new[] { qx, qy, qz, qw };
        }
    }
}
