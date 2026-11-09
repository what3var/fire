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
    ///   "blocks": [[x, y, z, colour], [x, y, z, colour, part], ...],     part 0 when it is left out
    ///   "animations": [{"name": "wave", "length": 1, "loop": true, "keys": [
    ///     {"part": 1, "time": 0.5, "rotate": [0, 0, 90], "move": [0, 0, 0], "scale": [1, 1, 1]}, ...]}]   degrees (x, y, z), cells, factors
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

        /// <summary>A key of an animation: where a part is at a time - turned around its pivot (degrees around x, y, z: R = Ry * Rx * Rz, as scene3d turns objects),
        /// moved (cells) and scaled, relative to its rest pose. Between two keys of a part the numbers go linearly; before its first key and after its last a part
        /// keeps them (a looping animation goes from its last key to its first).</summary>
        public sealed class Key
        {
            public int Part;
            public double Time;
            public double[] Rotate = { 0, 0, 0 }, Move = { 0, 0, 0 }, Scale = { 1, 1, 1 };
            public Key Clone() => new() { Part = Part, Time = Time, Rotate = (double[])Rotate.Clone(), Move = (double[])Move.Clone(), Scale = (double[])Scale.Clone() };
        }

        public sealed class Animation
        {
            public string Name = "animation";
            public double Length = 1;
            public bool Loop = true;
            public List<Key> Keys = new();
        }

        public int SizeX = 16, SizeY = 16, SizeZ = 16;
        public List<Animation> Animations = new();
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
            foreach (var a in Animations) { a.Keys.RemoveAll(k => k.Part == index); foreach (var k in a.Keys) if (k.Part > index) k.Part--; }
            if (parent > index) parent--;
            foreach (var key in _blocks.Keys.ToList())
            {
                var b = _blocks[key];
                if (b.Part == index) _blocks[key] = (b.Color, parent);
                else if (b.Part > index) _blocks[key] = (b.Color, b.Part - 1);
            }
        }

        // ---- regions ----

        /// <summary>A box of cells (both corners inside).</summary>
        public readonly record struct Box(int X0, int Y0, int Z0, int X1, int Y1, int Z1)
        {
            public static Box Of(int xa, int ya, int za, int xb, int yb, int zb) => new(Math.Min(xa, xb), Math.Min(ya, yb), Math.Min(za, zb), Math.Max(xa, xb), Math.Max(ya, yb), Math.Max(za, zb));
            public bool Contains(int x, int y, int z) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1 && z >= Z0 && z <= Z1;
            public int Width => X1 - X0 + 1;
            public int Height => Y1 - Y0 + 1;
            public int Depth => Z1 - Z0 + 1;
        }

        /// <summary>Blocks taken out of a model (copied): their places relative to the corner of the box they came from.</summary>
        public sealed class Clip
        {
            public int Width, Height, Depth;
            public List<(int X, int Y, int Z, int Color, int Part)> Blocks = new();
        }

        public Clip Copy(Box box)
        {
            var clip = new Clip { Width = box.Width, Height = box.Height, Depth = box.Depth };
            foreach (var (k, b) in _blocks.OrderBy(b => b.Key.Y).ThenBy(b => b.Key.Z).ThenBy(b => b.Key.X))
                if (box.Contains(k.X, k.Y, k.Z)) clip.Blocks.Add((k.X - box.X0, k.Y - box.Y0, k.Z - box.Z0, b.Color, b.Part));
            return clip;
        }

        /// <summary>Takes the blocks of a box away; how many.</summary>
        public int Delete(Box box)
        {
            var keys = _blocks.Keys.Where(k => box.Contains(k.X, k.Y, k.Z)).ToList();
            foreach (var k in keys) _blocks.Remove(k);
            return keys.Count;
        }

        /// <summary>Puts copied blocks with their corner at (x, y, z) (what falls outside the grid is left out); `part` >= 0: they all belong to that part, else they
        /// keep theirs (a part that the model does not have becomes part 0); how many.</summary>
        public int Paste(Clip clip, int x, int y, int z, int part = -1)
        {
            int n = 0;
            foreach (var b in clip.Blocks)
                if (Set(x + b.X, y + b.Y, z + b.Z, b.Color, part >= 0 ? part : b.Part < Parts.Count ? b.Part : 0)) n++;
            return n;
        }

        /// <summary>Fills a box with blocks of a colour and a part; how many cells.</summary>
        public int FillBox(Box box, int color, int part)
        {
            int n = 0;
            for (int y = box.Y0; y <= box.Y1; y++) for (int z = box.Z0; z <= box.Z1; z++) for (int x = box.X0; x <= box.X1; x++) if (Set(x, y, z, color, part)) n++;
            return n;
        }

        /// <summary>Fills from a cell across its layer: every cell reached through sides that holds what the start cell holds (the same colour and part, or
        /// nothing) becomes a block of the colour and part; how many cells changed.</summary>
        public int FloodFillLayer(int x, int y, int z, int color, int part)
        {
            if (!Inside(x, y, z)) return 0;
            bool full = _blocks.TryGetValue((x, y, z), out var start);
            if (full && start.Color == color && start.Part == part) return 0;
            var seen = new HashSet<(int, int)>();
            var todo = new Stack<(int X, int Z)>();
            todo.Push((x, z));
            int n = 0;
            while (todo.Count > 0)
            {
                var (cx, cz) = todo.Pop();
                if (cx < 0 || cz < 0 || cx >= SizeX || cz >= SizeZ || !seen.Add((cx, cz))) continue;
                bool here = _blocks.TryGetValue((cx, y, cz), out var b);
                if (here != full || (full && b != start)) continue;
                _blocks[(cx, y, cz)] = (color, part);
                n++;
                todo.Push((cx + 1, cz)); todo.Push((cx - 1, cz)); todo.Push((cx, cz + 1)); todo.Push((cx, cz - 1));
            }
            return n;
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
            if (root.TryGetProperty("animations", out var anims))
                foreach (var a in anims.EnumerateArray())
                {
                    var anim = new Animation
                    {
                        Name = a.TryGetProperty("name", out var an) ? an.GetString() ?? "animation" : "animation",
                        Length = a.TryGetProperty("length", out var al) && al.GetDouble() > 0 ? al.GetDouble() : 1,
                        Loop = !a.TryGetProperty("loop", out var lp) || lp.GetBoolean(),
                    };
                    double[] Three(JsonElement k, string name, double def) =>
                        k.TryGetProperty(name, out var v) ? v.EnumerateArray().Select(e => e.GetDouble()).Concat(new[] { def, def, def }).Take(3).ToArray() : new[] { def, def, def };
                    if (a.TryGetProperty("keys", out var keys))
                        foreach (var k in keys.EnumerateArray())
                            anim.Keys.Add(new Key
                            {
                                Part = k.TryGetProperty("part", out var kp) ? kp.GetInt32() : 0,
                                Time = k.TryGetProperty("time", out var kt) ? Math.Max(0, kt.GetDouble()) : 0,
                                Rotate = Three(k, "rotate", 0), Move = Three(k, "move", 0), Scale = Three(k, "scale", 1),
                            });
                    m.Animations.Add(anim);
                }
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
            sb.Append(ordered.Count > 0 ? "\n  ]" : "]");
            if (Animations.Count > 0)
            {
                string N(double v) => v.ToString("R", inv);
                string V(double[] v) => "[" + string.Join(", ", v.Select(N)) + "]";
                sb.Append(",\n  \"animations\": [\n");
                for (int i = 0; i < Animations.Count; i++)
                {
                    var a = Animations[i];
                    sb.Append("    {\"name\": ").Append(JsonSerializer.Serialize(a.Name)).Append(", \"length\": ").Append(N(a.Length)).Append(", \"loop\": ").Append(a.Loop ? "true" : "false").Append(", \"keys\": [");
                    var keys = a.Keys.OrderBy(k => k.Part).ThenBy(k => k.Time).ToList();
                    for (int k = 0; k < keys.Count; k++)
                    {
                        var key = keys[k];
                        sb.Append(k == 0 ? "\n      " : ",\n      ");
                        sb.Append($"{{\"part\": {key.Part}, \"time\": {N(key.Time)}, \"rotate\": {V(key.Rotate)}, \"move\": {V(key.Move)}, \"scale\": {V(key.Scale)}}}");
                    }
                    sb.Append(keys.Count > 0 ? "\n    ]}" : "]}").Append(i + 1 < Animations.Count ? ",\n" : "\n");
                }
                sb.Append("  ]");
            }
            sb.Append("\n}\n");
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
            // the clips: every frame the place of every part of the file (the parts of the colours stay where their part is)
            int fps = ModelConverter.FramesPerSecond;
            var clips = new List<(string, int, double[][][], bool)>();
            foreach (var anim in Animations)
            {
                int frames = Math.Max(1, (int)Math.Round(anim.Length * fps) + (anim.Loop ? 0 : 1));
                var poses = new double[frames][][];
                for (int f = 0; f < frames; f++)
                {
                    poses[f] = new double[parts.Count][];
                    for (int k = 0; k < parts.Count; k++) poses[f][k] = new double[] { 0, 0, 0, 0, 0, 0, 1, 1, 1, 1 };
                    for (int i = 0; i < Parts.Count; i++)
                    {
                        var (rot, move, scale) = PartPose(anim, i, (double)f / fps);
                        var p = Parts[i];
                        double[] parentPivot = p.Parent >= 0 ? Parts[p.Parent].Pivot : new[] { ox, 0, oz };
                        var q = ModelConverter.QuatOf(Rotation(rot));
                        poses[f][indexOf[i]] = new[]
                        {
                            (p.Pivot[0] - parentPivot[0] + move[0]) * s, (p.Pivot[1] - parentPivot[1] + move[1]) * s, (p.Pivot[2] - parentPivot[2] + move[2]) * s,
                            q[0], q[1], q[2], q[3], scale[0], scale[1], scale[2],
                        };
                    }
                }
                clips.Add((anim.Name.Length > 31 ? anim.Name[..31] : anim.Name, frames, poses, anim.Loop));
            }
            return ModelConverter.Write(parts, clips, fps);
        }

        // ---- poses ----

        /// <summary>The rotation (row major 4x4) of angles in degrees around x, y, z: Ry * Rx * Rz (as scene3d turns objects).</summary>
        public static double[] Rotation(double[] deg)
        {
            double ax = deg[0] * Math.PI / 180, ay = deg[1] * Math.PI / 180, az = deg[2] * Math.PI / 180;
            double cx = Math.Cos(ax), sx = Math.Sin(ax), cy = Math.Cos(ay), sy = Math.Sin(ay), cz = Math.Cos(az), sz = Math.Sin(az);
            double[] rx = { 1, 0, 0, 0, 0, cx, -sx, 0, 0, sx, cx, 0, 0, 0, 0, 1 };
            double[] ry = { cy, 0, sy, 0, 0, 1, 0, 0, -sy, 0, cy, 0, 0, 0, 0, 1 };
            double[] rz = { cz, -sz, 0, 0, sz, cz, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            return Mul(ry, Mul(rx, rz));
        }

        public static double[] Mul(double[] a, double[] b)
        {
            var r = new double[16];
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) { double v = 0; for (int k = 0; k < 4; k++) v += a[i * 4 + k] * b[k * 4 + j]; r[i * 4 + j] = v; }
            return r;
        }

        private static double[] Translation(double x, double y, double z) => new double[] { 1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1 };
        private static double[] Scaling(double[] v) => new double[] { v[0], 0, 0, 0, 0, v[1], 0, 0, 0, 0, v[2], 0, 0, 0, 0, 1 };
        public static readonly double[] Identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        /// <summary>Where a part is at a time of an animation (relative to its rest pose): rotation (degrees), movement (cells), scale - between its keys linearly.</summary>
        public (double[] Rotate, double[] Move, double[] Scale) PartPose(Animation? anim, int part, double time)
        {
            var keys = anim?.Keys.Where(k => k.Part == part).OrderBy(k => k.Time).ToList();
            if (anim == null || keys == null || keys.Count == 0) return (new double[3], new double[3], new double[] { 1, 1, 1 });
            double len = anim.Length > 0 ? anim.Length : 1;
            double t = anim.Loop ? time - Math.Floor(time / len) * len : Math.Clamp(time, 0, len);
            (double[], double[], double[]) Of(Key k) => ((double[])k.Rotate.Clone(), (double[])k.Move.Clone(), (double[])k.Scale.Clone());
            (double[], double[], double[]) Lerp(Key a, Key b, double ta, double tb)
            {
                double f = tb > ta ? (t - ta) / (tb - ta) : 0;
                double[] L(double[] p, double[] q) => new[] { p[0] + (q[0] - p[0]) * f, p[1] + (q[1] - p[1]) * f, p[2] + (q[2] - p[2]) * f };
                return (L(a.Rotate, b.Rotate), L(a.Move, b.Move), L(a.Scale, b.Scale));
            }
            if (keys.Count == 1) return Of(keys[0]);
            if (t < keys[0].Time) return anim.Loop ? Lerp(keys[^1], keys[0], keys[^1].Time - len, keys[0].Time) : Of(keys[0]);
            for (int i = 0; i + 1 < keys.Count; i++)
                if (t < keys[i + 1].Time) return Lerp(keys[i], keys[i + 1], keys[i].Time, keys[i + 1].Time);
            return anim.Loop ? Lerp(keys[^1], keys[0], keys[^1].Time, keys[0].Time + len) : Of(keys[^1]);
        }

        /// <summary>For every part the matrix (row major 4x4, cells) that takes a point of its rest pose to where it is at the time of the animation (null: the rest pose).</summary>
        public double[][] PoseMatrices(Animation? anim, double time)
        {
            var result = new double[Parts.Count][];
            for (int i = 0; i < Parts.Count; i++)
            {
                var (rot, move, scale) = PartPose(anim, i, time);
                var pv = Parts[i].Pivot;
                var local = Mul(Translation(pv[0] + move[0], pv[1] + move[1], pv[2] + move[2]), Mul(Rotation(rot), Mul(Scaling(scale), Translation(-pv[0], -pv[1], -pv[2]))));
                int parent = Parts[i].Parent;
                result[i] = parent >= 0 && parent < i ? Mul(result[parent], local) : local;
            }
            return result;
        }

        /// <summary>A key of a part at a time (exactly that time), or null.</summary>
        public static Key? KeyAt(Animation anim, int part, double time) => anim.Keys.FirstOrDefault(k => k.Part == part && Math.Abs(k.Time - time) < 1e-6);

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
