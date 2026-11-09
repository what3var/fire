using System.Globalization;
using System.Text;
using System.Text.Json;

namespace fire.Compiler
{
    /// <summary>
    /// A model of blocks (`.fblocks`, docs/graphics/models.md): what the block editor of spark draws and forge turns into fire's model file (.f3d). A grid of
    /// SizeX x SizeY x SizeZ cells, each empty or a block of a colour of the palette, belonging to a part; parts form a tree and turn around their pivot (the
    /// rigid parts of the model file - what an animation moves). The text is JSON:
    /// <code>
    /// {
    ///   "fblocks": 1,
    ///   "size": [16, 16, 16],          cells in x, y (up), z
    ///   "block": 0.1,                  the edge of a block in meters
    ///   "palette": ["#C83232", ...],   the colours
    ///   "parts": [{"name": "body", "parent": -1, "pivot": [8, 0, 8]}],   pivot in cells (fractions allowed); a parent comes before its children
    ///   "blocks": [[x, y, z, colour], [x, y, z, colour, part], ...]      part 0 when it is left out
    /// }
    /// </code>
    /// The model file gets only what can be seen: a face between two blocks of the same part is left out (between two parts it stays - they move apart),
    /// and neighbouring faces of the same colour in one plane become one rectangle (greedy). The origin of the model is the middle of the bottom of the grid.
    /// </summary>
    public sealed class BlockModel
    {
        public const string Extension = ".fblocks";

        public sealed class Part
        {
            public string Name = "part";
            public int Parent = -1;
            public double[] Pivot = { 0, 0, 0 };
        }

        public int SizeX = 16, SizeY = 16, SizeZ = 16;
        public double BlockSize = 0.1;
        public List<uint> Palette = new();   // 0xRRGGBB
        public List<Part> Parts = new();
        private readonly Dictionary<(int X, int Y, int Z), (int Color, int Part)> _blocks = new();

        public int Count => _blocks.Count;
        public IEnumerable<KeyValuePair<(int X, int Y, int Z), (int Color, int Part)>> Blocks => _blocks;

        public static BlockModel CreateDefault(int sx = 16, int sy = 16, int sz = 16)
        {
            var m = new BlockModel { SizeX = sx, SizeY = sy, SizeZ = sz };
            m.Palette.AddRange(DefaultPalette);
            m.Parts.Add(new Part { Name = "body", Parent = -1, Pivot = new double[] { sx / 2.0, 0, sz / 2.0 } });
            return m;
        }

        /// <summary>16 colours to start with: black, greys, white, and the bright and the dark of six hues.</summary>
        public static readonly uint[] DefaultPalette =
        {
            0x1A1A1A, 0x6E6E6E, 0xBDBDBD, 0xF5F5F5,
            0xC83232, 0xE8873A, 0xF2D13D, 0x5DAE4B,
            0x2E7D4F, 0x3A9AD9, 0x2F4FA8, 0x8A4FB5,
            0xD970A6, 0x8B5A2B, 0x5C3A1E, 0xE8C99A,
        };

        public bool Inside(int x, int y, int z) => x >= 0 && y >= 0 && z >= 0 && x < SizeX && y < SizeY && z < SizeZ;

        public bool TryGet(int x, int y, int z, out int color, out int part)
        {
            if (_blocks.TryGetValue((x, y, z), out var b)) { color = b.Color; part = b.Part; return true; }
            color = part = -1;
            return false;
        }

        /// <summary>Puts a block (or changes one); false outside the grid.</summary>
        public bool Set(int x, int y, int z, int color, int part = 0)
        {
            if (!Inside(x, y, z)) return false;
            _blocks[(x, y, z)] = (Math.Clamp(color, 0, Math.Max(0, Palette.Count - 1)), Math.Clamp(part, 0, Math.Max(0, Parts.Count - 1)));
            return true;
        }

        public bool Remove(int x, int y, int z) => _blocks.Remove((x, y, z));

        public void Clear() => _blocks.Clear();

        /// <summary>A new size: what lies outside goes.</summary>
        public void Resize(int sx, int sy, int sz)
        {
            SizeX = Math.Clamp(sx, 1, 256); SizeY = Math.Clamp(sy, 1, 256); SizeZ = Math.Clamp(sz, 1, 256);
            foreach (var key in _blocks.Keys.Where(k => !Inside(k.X, k.Y, k.Z)).ToList()) _blocks.Remove(key);
        }

        /// <summary>A colour of the palette was taken out: its blocks take colour 0, the colours after it move down.</summary>
        public void RemoveColor(int index)
        {
            if (index < 0 || index >= Palette.Count || Palette.Count <= 1) return;
            Palette.RemoveAt(index);
            foreach (var key in _blocks.Keys.ToList())
            {
                var b = _blocks[key];
                if (b.Color == index) _blocks[key] = (0, b.Part);
                else if (b.Color > index) _blocks[key] = (b.Color - 1, b.Part);
            }
        }

        /// <summary>A part was taken out: its blocks go to its parent (or part 0), its children hang from its parent.</summary>
        public void RemovePart(int index)
        {
            if (index <= 0 || index >= Parts.Count) return;
            int parent = Math.Max(0, Parts[index].Parent);
            foreach (var p in Parts) if (p.Parent == index) p.Parent = parent;
            foreach (var p in Parts) if (p.Parent > index) p.Parent--;
            Parts.RemoveAt(index);
            if (parent > index) parent--;
            foreach (var key in _blocks.Keys.ToList())
            {
                var b = _blocks[key];
                if (b.Part == index) _blocks[key] = (b.Color, parent);
                else if (b.Part > index) _blocks[key] = (b.Color, b.Part - 1);
            }
        }

        // ---- text ----

        public static BlockModel Parse(string text)
        {
            var m = new BlockModel();
            if (string.IsNullOrWhiteSpace(text)) return CreateDefault();
            JsonElement root;
            try { root = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }).RootElement; }
            catch (JsonException e) { throw new InvalidDataException("The block model is no valid JSON: " + e.Message); }
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("fblocks", out var v) || v.GetInt32() != 1) throw new InvalidDataException("Not a block model (\"fblocks\": 1 is missing).");
            if (root.TryGetProperty("size", out var size))
            {
                var s = size.EnumerateArray().Select(e => e.GetInt32()).ToArray();
                if (s.Length != 3) throw new InvalidDataException("\"size\" has three numbers.");
                m.SizeX = Math.Clamp(s[0], 1, 256); m.SizeY = Math.Clamp(s[1], 1, 256); m.SizeZ = Math.Clamp(s[2], 1, 256);
            }
            if (root.TryGetProperty("block", out var bs)) m.BlockSize = bs.GetDouble() > 0 ? bs.GetDouble() : 0.1;
            if (root.TryGetProperty("palette", out var pal))
                foreach (var c in pal.EnumerateArray()) m.Palette.Add(ParseColor(c.GetString() ?? "#FFFFFF"));
            if (m.Palette.Count == 0) m.Palette.AddRange(DefaultPalette);
            if (root.TryGetProperty("parts", out var parts))
                foreach (var p in parts.EnumerateArray())
                {
                    var part = new Part
                    {
                        Name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "part" : "part",
                        Parent = p.TryGetProperty("parent", out var pa) ? pa.GetInt32() : -1,
                        Pivot = p.TryGetProperty("pivot", out var pv) ? pv.EnumerateArray().Select(e => e.GetDouble()).Concat(new double[3]).Take(3).ToArray() : new double[3],
                    };
                    if (part.Parent >= m.Parts.Count) throw new InvalidDataException($"The part '{part.Name}' names a parent that comes after it.");
                    m.Parts.Add(part);
                }
            if (m.Parts.Count == 0) m.Parts.Add(new Part { Name = "body", Pivot = new double[] { m.SizeX / 2.0, 0, m.SizeZ / 2.0 } });
            if (root.TryGetProperty("blocks", out var blocks))
                foreach (var b in blocks.EnumerateArray())
                {
                    var a = b.EnumerateArray().Select(e => e.GetInt32()).ToArray();
                    if (a.Length < 4) throw new InvalidDataException("A block is [x, y, z, colour] or [x, y, z, colour, part].");
                    m.Set(a[0], a[1], a[2], a[3], a.Length > 4 ? a[4] : 0);
                }
            return m;
        }

        private static uint ParseColor(string s)
        {
            s = s.Trim().TrimStart('#');
            return s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v) ? v : 0xFFFFFF;
        }

        public static string ColorText(uint c) => "#" + (c & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

        /// <summary>The text of the model: stable (blocks by y, z, x), one block a line - small diffs in git.</summary>
        public string ToJson()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("{\n  \"fblocks\": 1,\n");
            sb.Append($"  \"size\": [{SizeX}, {SizeY}, {SizeZ}],\n");
            sb.Append("  \"block\": ").Append(BlockSize.ToString("R", inv)).Append(",\n");
            sb.Append("  \"palette\": [").Append(string.Join(", ", Palette.Select(c => "\"" + ColorText(c) + "\""))).Append("],\n");
            sb.Append("  \"parts\": [\n");
            for (int i = 0; i < Parts.Count; i++)
            {
                var p = Parts[i];
                sb.Append("    {\"name\": ").Append(JsonSerializer.Serialize(p.Name)).Append($", \"parent\": {p.Parent}, \"pivot\": [")
                  .Append(string.Join(", ", p.Pivot.Select(v => v.ToString("R", inv)))).Append("]}").Append(i + 1 < Parts.Count ? ",\n" : "\n");
            }
            sb.Append("  ],\n  \"blocks\": [");
            var ordered = _blocks.OrderBy(b => b.Key.Y).ThenBy(b => b.Key.Z).ThenBy(b => b.Key.X).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var (k, b) = (ordered[i].Key, ordered[i].Value);
                sb.Append(i == 0 ? "\n    " : ",\n    ");
                sb.Append($"[{k.X}, {k.Y}, {k.Z}, {b.Color}").Append(b.Part != 0 ? $", {b.Part}]" : "]");
            }
            sb.Append(ordered.Count > 0 ? "\n  ]\n}\n" : "]\n}\n");
            return sb.ToString();
        }

        // ---- faces ----

        /// <summary>The six directions: +x, -x, +y, -y, +z, -z.</summary>
        public static readonly (int X, int Y, int Z)[] Directions = { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) };

        /// <summary>Is the face of the block (x, y, z) towards `dir` seen? Not when a block of the same part is there.</summary>
        public bool FaceVisible(int x, int y, int z, int dir, int part)
        {
            var d = Directions[dir];
            return !_blocks.TryGetValue((x + d.X, y + d.Y, z + d.Z), out var n) || n.Part != part;
        }

        /// <summary>Every visible face of every block (for a view): the cell, the direction, the colour, the part.</summary>
        public IEnumerable<(int X, int Y, int Z, int Dir, int Color, int Part)> VisibleFaces()
        {
            foreach (var (k, b) in _blocks)
                for (int d = 0; d < 6; d++)
                    if (FaceVisible(k.X, k.Y, k.Z, d, b.Part)) yield return (k.X, k.Y, k.Z, d, b.Color, b.Part);
        }

        /// <summary>A rectangle of faces in cell units: the four corners counter clockwise seen from outside, the direction.</summary>
        public readonly record struct Quad(int Dir, double[] A, double[] B, double[] C, double[] D, int Color, int Part);

        /// <summary>The visible faces merged greedily: per part, colour, direction and layer the rectangles that cover them.</summary>
        public List<Quad> Quads()
        {
            var quads = new List<Quad>();
            int[] size = { SizeX, SizeY, SizeZ };
            for (int dir = 0; dir < 6; dir++)
            {
                int axis = dir / 2, sign = dir % 2 == 0 ? 1 : -1;
                int ua = (axis + 1) % 3, va = (axis + 2) % 3;   // the two axes of the plane
                for (int layer = 0; layer < size[axis]; layer++)
                {
                    // the mask: per cell of the plane the (colour, part) of a visible face, or -1
                    var mask = new (int Color, int Part)[size[ua], size[va]];
                    bool any = false;
                    for (int u = 0; u < size[ua]; u++)
                        for (int v = 0; v < size[va]; v++)
                        {
                            var c = new int[3]; c[axis] = layer; c[ua] = u; c[va] = v;
                            mask[u, v] = (-1, -1);
                            if (_blocks.TryGetValue((c[0], c[1], c[2]), out var b) && FaceVisible(c[0], c[1], c[2], dir, b.Part)) { mask[u, v] = (b.Color, b.Part); any = true; }
                        }
                    if (!any) continue;
                    for (int v = 0; v < size[va]; v++)
                        for (int u = 0; u < size[ua]; )
                        {
                            var m = mask[u, v];
                            if (m.Color < 0) { u++; continue; }
                            int w = 1;
                            while (u + w < size[ua] && mask[u + w, v] == m) w++;
                            int h = 1;
                            for (bool ok = true; ok && v + h < size[va]; )
                            {
                                for (int k = 0; k < w; k++) if (mask[u + k, v + h] != m) { ok = false; break; }
                                if (ok) h++;
                            }
                            for (int dv = 0; dv < h; dv++) for (int k = 0; k < w; k++) mask[u + k, v + dv] = (-1, -1);
                            double plane = layer + (sign > 0 ? 1 : 0);
                            double[] P(double pu, double pv) { var p = new double[3]; p[axis] = plane; p[ua] = pu; p[va] = pv; return p; }
                            // (u, v, axis) is right handed: u x v = +axis; counter clockwise seen from +axis is (u0,v0) (u1,v0) (u1,v1) (u0,v1)
                            if (sign > 0) quads.Add(new Quad(dir, P(u, v), P(u + w, v), P(u + w, v + h), P(u, v + h), m.Color, m.Part));
                            else quads.Add(new Quad(dir, P(u, v), P(u, v + h), P(u + w, v + h), P(u + w, v), m.Color, m.Part));
                            u += w;
                        }
                }
            }
            return quads;
        }

        // ---- the model file ----

        /// <summary>fire's model file: a part of the model file for each part (at its pivot), under it one part for each colour it uses (the triangles).</summary>
        public byte[] ToF3d()
        {
            var parts = new List<ModelConverter.Part>();
            var quads = Quads();
            double s = BlockSize, ox = SizeX / 2.0, oz = SizeZ / 2.0, longest = Math.Max(SizeX, Math.Max(SizeY, SizeZ));
            var indexOf = new int[Parts.Count];
            for (int i = 0; i < Parts.Count; i++)
            {
                var p = Parts[i];
                double[] parentPivot = p.Parent >= 0 ? Parts[p.Parent].Pivot : new[] { ox, 0, oz };
                indexOf[i] = parts.Count;
                parts.Add(new ModelConverter.Part
                {
                    Name = p.Name.Length > 31 ? p.Name[..31] : p.Name,
                    Parent = p.Parent >= 0 ? indexOf[p.Parent] : -1,
                    T = new[] { (p.Pivot[0] - parentPivot[0]) * s, (p.Pivot[1] - parentPivot[1]) * s, (p.Pivot[2] - parentPivot[2]) * s },
                });
                foreach (var color in quads.Where(q => q.Part == i).Select(q => q.Color).Distinct().OrderBy(c => c))
                {
                    var mesh = new ModelConverter.Part { Name = p.Name.Length > 24 ? p.Name[..24] : p.Name, Parent = indexOf[i], HasUv = true };
                    uint rgb = Palette[Math.Clamp(color, 0, Palette.Count - 1)];
                    mesh.Color = new[] { (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, (byte)255 };
                    mesh.Name += "#" + color;
                    foreach (var q in quads.Where(q => q.Part == i && q.Color == color))
                    {
                        var d = Directions[q.Dir];
                        int b = mesh.Positions.Count;
                        foreach (var c in new[] { q.A, q.B, q.C, q.D })
                        {
                            mesh.Positions.Add(new[] { (c[0] - p.Pivot[0]) * s, (c[1] - p.Pivot[1]) * s, (c[2] - p.Pivot[2]) * s });
                            mesh.Normals.Add(new double[] { d.X, d.Y, d.Z });
                            // texture coordinates on the plane: one copy of a texture over the longest side of the grid
                            int axis = q.Dir / 2;
                            mesh.Uvs.Add(new[] { c[(axis + 1) % 3] / longest, c[(axis + 2) % 3] / longest });
                        }
                        mesh.Indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                        if (mesh.Positions.Count > 65531) throw new InvalidDataException($"The part '{p.Name}' has too many faces of one colour for one mesh.");
                    }
                    parts.Add(mesh);
                }
            }
            if (parts.Count > 32767) throw new InvalidDataException("Too many parts.");
            return ModelConverter.Write(parts, new List<(string, int, double[][][])>(), ModelConverter.FramesPerSecond);
        }

        // ---- picking ----

        /// <summary>The first block a ray hits (origin and direction in cells): the cell and the direction of the face it entered through (to put a block next to it);
        /// a ray that hits no block but the floor (y = 0) inside the grid: the cell below the floor (y = -1) and the face up. False: nothing.</summary>
        public bool Raycast(double[] o, double[] d, out (int X, int Y, int Z) cell, out int face)
        {
            cell = default; face = -1;
            // enter the box of the grid
            double tMin = 0, tMax = 1e9;
            int[] size = { SizeX, SizeY, SizeZ };
            int enterAxis = -1;
            for (int a = 0; a < 3; a++)
            {
                if (Math.Abs(d[a]) < 1e-12) { if (o[a] < 0 || o[a] > size[a]) return FloorHit(o, d, out cell, out face); continue; }
                double t1 = (0 - o[a]) / d[a], t2 = (size[a] - o[a]) / d[a];
                if (t1 > t2) (t1, t2) = (t2, t1);
                if (t1 > tMin) { tMin = t1; enterAxis = a; }
                tMax = Math.Min(tMax, t2);
            }
            if (tMin > tMax) return FloorHit(o, d, out cell, out face);
            double[] p = { o[0] + d[0] * (tMin + 1e-9), o[1] + d[1] * (tMin + 1e-9), o[2] + d[2] * (tMin + 1e-9) };
            int[] c = { Math.Clamp((int)Math.Floor(p[0]), 0, SizeX - 1), Math.Clamp((int)Math.Floor(p[1]), 0, SizeY - 1), Math.Clamp((int)Math.Floor(p[2]), 0, SizeZ - 1) };
            int[] step = new int[3];
            double[] tNext = new double[3], tDelta = new double[3];
            for (int a = 0; a < 3; a++)
            {
                step[a] = d[a] > 0 ? 1 : -1;
                tDelta[a] = Math.Abs(d[a]) < 1e-12 ? 1e30 : Math.Abs(1 / d[a]);
                double boundary = c[a] + (d[a] > 0 ? 1 : 0);
                tNext[a] = Math.Abs(d[a]) < 1e-12 ? 1e30 : tMin + (boundary - p[a]) / d[a];
            }
            int lastAxis = enterAxis;
            while (c[0] >= 0 && c[1] >= 0 && c[2] >= 0 && c[0] < SizeX && c[1] < SizeY && c[2] < SizeZ)
            {
                if (_blocks.ContainsKey((c[0], c[1], c[2])))
                {
                    cell = (c[0], c[1], c[2]);
                    face = lastAxis < 0 ? 2 : lastAxis * 2 + (d[lastAxis] > 0 ? 1 : 0);   // entered moving +a: through the -a face
                    return true;
                }
                int m = tNext[0] < tNext[1] ? (tNext[0] < tNext[2] ? 0 : 2) : (tNext[1] < tNext[2] ? 1 : 2);
                c[m] += step[m]; tNext[m] += tDelta[m]; lastAxis = m;
            }
            return FloorHit(o, d, out cell, out face);
        }

        private bool FloorHit(double[] o, double[] d, out (int X, int Y, int Z) cell, out int face)
        {
            cell = default; face = -1;
            if (d[1] >= -1e-12 || o[1] < 0) return false;
            double t = -o[1] / d[1];
            double x = o[0] + d[0] * t, z = o[2] + d[2] * t;
            if (x < 0 || z < 0 || x >= SizeX || z >= SizeZ) return false;
            cell = ((int)Math.Floor(x), -1, (int)Math.Floor(z));
            face = 2;
            return true;
        }
    }
}
