using System.Globalization;
using System.Text;
using System.Text.Json;

namespace fire.Compiler
{
    /// <summary>
    /// A model of polygons (`.fmesh`, docs/graphics/models.md): what the CAD editor of spark draws and forge turns into fire's model file (.f3d). Corners
    /// (vertices, in meters, y up) and faces between them (polygons of three or more corners, counter clockwise seen from outside - the side they are seen
    /// from), each of a colour of the palette and belonging to a part; parts and animations as in the block models (BlockModel: a tree of parts that turn around
    /// their pivot - here in meters). The text is JSON:
    /// <code>
    /// {
    ///   "fmesh": 1,
    ///   "palette": ["#C83232", ...],
    ///   "parts": [{"name": "body", "parent": -1, "pivot": [0, 0, 0]}],
    ///   "vertices": [[x, y, z], ...],
    ///   "faces": [[colour, part, v0, v1, v2, ...], ...],
    ///   "animations": [...]                            as in .fblocks, moves in meters
    /// }
    /// </code>
    /// The model file gets flat faces (a normal for each face; polygons as fans of triangles), a part of the file for each part (at its pivot) and under it one
    /// for each colour. The origin of the model is the origin of the mesh.
    /// </summary>
    public sealed class MeshModel
    {
        public const string Extension = ".fmesh";
        private const double Eps = 1e-6;

        public sealed class Face
        {
            public List<int> V = new();
            public int Color, Part;
            public Face Clone() => new() { V = new List<int>(V), Color = Color, Part = Part };
        }

        public List<double[]> Vertices = new();
        public List<Face> Faces = new();
        public List<uint> Palette = new();
        public List<BlockModel.Part> Parts = new();
        public List<BlockModel.Animation> Animations = new();

        /// <summary>A new model: a box of a meter on the floor (its bottom in the middle of the origin) of the first bright colour.</summary>
        public static MeshModel CreateDefault()
        {
            var m = CreateEmpty();
            m.AddBox(new[] { -0.5, 0, -0.5 }, new[] { 0.5, 1, 0.5 }, 9, 0);
            return m;
        }

        public static MeshModel CreateEmpty()
        {
            var m = new MeshModel();
            m.Palette.AddRange(BlockModel.DefaultPalette);
            m.Parts.Add(new BlockModel.Part { Name = "body", Parent = -1, Pivot = new double[3] });
            return m;
        }

        public MeshModel Clone() => Parse(ToJson());

        // ---- vectors ----

        public static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        public static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        public static double[] Mul(double[] a, double s) => new[] { a[0] * s, a[1] * s, a[2] * s };
        public static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        public static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        public static double Length(double[] a) => Math.Sqrt(Dot(a, a));
        public static double[] Normalize(double[] a) { double l = Length(a); return l > 1e-12 ? Mul(a, 1 / l) : new double[3]; }

        // ---- faces ----

        /// <summary>The normal of a face (unit length; Newell's - right for polygons that are not quite flat, zero for a face without area).</summary>
        public double[] Normal(int face) => Normal(Faces[face].V);

        public double[] Normal(IReadOnlyList<int> loop)
        {
            var n = new double[3];
            for (int i = 0; i < loop.Count; i++)
            {
                var a = Vertices[loop[i]]; var b = Vertices[loop[(i + 1) % loop.Count]];
                n[0] += (a[1] - b[1]) * (a[2] + b[2]);
                n[1] += (a[2] - b[2]) * (a[0] + b[0]);
                n[2] += (a[0] - b[0]) * (a[1] + b[1]);
            }
            return Normalize(n);
        }

        /// <summary>The middle of a face (the mean of its corners).</summary>
        public double[] Center(int face) => Center(Faces[face].V);

        public double[] Center(IEnumerable<int> vertices)
        {
            var c = new double[3]; int n = 0;
            foreach (int v in vertices) { c[0] += Vertices[v][0]; c[1] += Vertices[v][1]; c[2] += Vertices[v][2]; n++; }
            return n > 0 ? Mul(c, 1.0 / n) : c;
        }

        /// <summary>Every edge once (the smaller corner first), in the order the faces have them.</summary>
        public List<(int A, int B)> Edges()
        {
            var seen = new HashSet<(int, int)>();
            var list = new List<(int, int)>();
            foreach (var f in Faces)
                for (int i = 0; i < f.V.Count; i++)
                {
                    var e = Edge(f.V[i], f.V[(i + 1) % f.V.Count]);
                    if (seen.Add(e)) list.Add(e);
                }
            return list;
        }

        public static (int A, int B) Edge(int a, int b) => a < b ? (a, b) : (b, a);

        /// <summary>The corners of faces (each once).</summary>
        public SortedSet<int> VerticesOf(IEnumerable<int> faces)
        {
            var set = new SortedSet<int>();
            foreach (int f in faces) set.UnionWith(Faces[f].V);
            return set;
        }

        /// <summary>The faces whose corners are all in a set (what a selection of corners or edges holds).</summary>
        public List<int> FacesWithin(ICollection<int> vertices)
        {
            var list = new List<int>();
            for (int f = 0; f < Faces.Count; f++) if (Faces[f].V.All(vertices.Contains)) list.Add(f);
            return list;
        }

        public int TriangleCount => Faces.Sum(f => Math.Max(0, f.V.Count - 2));

        public (double[] Min, double[] Max) Bounds()
        {
            if (Vertices.Count == 0) return (new double[3], new double[3]);
            var min = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
            var max = new[] { double.MinValue, double.MinValue, double.MinValue };
            foreach (var v in Vertices) for (int a = 0; a < 3; a++) { min[a] = Math.Min(min[a], v[a]); max[a] = Math.Max(max[a], v[a]); }
            return (min, max);
        }

        // ---- building ----

        public int AddVertex(double x, double y, double z) { Vertices.Add(new[] { x, y, z }); return Vertices.Count - 1; }

        public int AddFace(IEnumerable<int> loop, int color, int part)
        {
            var f = new Face { V = loop.ToList(), Color = color, Part = part };
            if (f.V.Count < 3 || f.V.Distinct().Count() != f.V.Count || f.V.Any(v => v < 0 || v >= Vertices.Count)) throw new ArgumentException("A face has three or more different corners.");
            Faces.Add(f);
            return Faces.Count - 1;
        }

        /// <summary>A box between two corners; its faces.</summary>
        public List<int> AddBox(double[] min, double[] max, int color, int part)
        {
            int b = Vertices.Count;
            for (int i = 0; i < 8; i++) AddVertex((i & 1) != 0 ? max[0] : min[0], (i & 2) != 0 ? max[1] : min[1], (i & 4) != 0 ? max[2] : min[2]);
            // corner i: x = bit 0, y = bit 1, z = bit 2; each face counter clockwise seen from outside
            int[][] quads = { new[] { 1, 3, 7, 5 }, new[] { 0, 4, 6, 2 }, new[] { 2, 6, 7, 3 }, new[] { 0, 1, 5, 4 }, new[] { 4, 5, 7, 6 }, new[] { 0, 2, 3, 1 } };
            return quads.Select(q => AddFace(q.Select(i => b + i), color, part)).ToList();
        }

        /// <summary>A flat rectangle on y = center[1] (seen from above); its face.</summary>
        public List<int> AddPlane(double[] center, double width, double depth, int color, int part)
        {
            double x0 = center[0] - width / 2, x1 = center[0] + width / 2, z0 = center[2] - depth / 2, z1 = center[2] + depth / 2, y = center[1];
            int a = AddVertex(x0, y, z0), b = AddVertex(x0, y, z1), c = AddVertex(x1, y, z1), d = AddVertex(x1, y, z0);
            return new List<int> { AddFace(new[] { a, b, c, d }, color, part) };
        }

        /// <summary>A cylinder standing on `bottom` (the middle of its bottom); `segments` sides; its faces.</summary>
        public List<int> AddCylinder(double[] bottom, double radius, double height, int segments, int color, int part)
        {
            segments = Math.Clamp(segments, 3, 128);
            int b = Vertices.Count;
            for (int i = 0; i < segments; i++)
            {
                double a = 2 * Math.PI * i / segments;
                double x = bottom[0] + Math.Cos(a) * radius, z = bottom[2] - Math.Sin(a) * radius;
                AddVertex(x, bottom[1], z); AddVertex(x, bottom[1] + height, z);
            }
            var faces = new List<int>();
            for (int i = 0; i < segments; i++)
            {
                int j = (i + 1) % segments;
                faces.Add(AddFace(new[] { b + 2 * i, b + 2 * j, b + 2 * j + 1, b + 2 * i + 1 }, color, part));
            }
            faces.Add(AddFace(Enumerable.Range(0, segments).Select(i => b + 2 * i + 1), color, part));
            faces.Add(AddFace(Enumerable.Range(0, segments).Reverse().Select(i => b + 2 * i), color, part));
            return faces;
        }

        /// <summary>A sphere (rings of quads, triangles at the poles) around `center`; its faces.</summary>
        public List<int> AddSphere(double[] center, double radius, int segments, int rings, int color, int part)
        {
            segments = Math.Clamp(segments, 3, 128); rings = Math.Clamp(rings, 2, 64);
            int top = AddVertex(center[0], center[1] + radius, center[2]);
            int first = Vertices.Count;
            for (int r = 1; r < rings; r++)
            {
                double phi = Math.PI * r / rings;
                for (int i = 0; i < segments; i++)
                {
                    double a = 2 * Math.PI * i / segments;
                    AddVertex(center[0] + Math.Sin(phi) * Math.Cos(a) * radius, center[1] + Math.Cos(phi) * radius, center[2] - Math.Sin(phi) * Math.Sin(a) * radius);
                }
            }
            int bottom = AddVertex(center[0], center[1] - radius, center[2]);
            int V(int r, int i) => first + (r - 1) * segments + (i % segments);
            var faces = new List<int>();
            for (int i = 0; i < segments; i++) faces.Add(AddFace(new[] { top, V(1, i), V(1, i + 1) }, color, part));
            for (int r = 1; r + 1 < rings; r++)
                for (int i = 0; i < segments; i++) faces.Add(AddFace(new[] { V(r, i), V(r + 1, i), V(r + 1, i + 1), V(r, i + 1) }, color, part));
            for (int i = 0; i < segments; i++) faces.Add(AddFace(new[] { bottom, V(rings - 1, i + 1), V(rings - 1, i) }, color, part));
            return faces;
        }

        /// <summary>A face through corners in the order given, turned so that it faces away from the middle of the model (a face drawn to close a hole).</summary>
        public int FaceFromVertices(IReadOnlyList<int> loop, int color, int part)
        {
            var list = loop.ToList();
            var others = Faces.SelectMany(f => f.V).Distinct().Except(list).ToList();
            if (others.Count > 0)
            {
                var toFace = Sub(Center(list), Center(others));
                if (Dot(Normal(list), toFace) < 0) list.Reverse();
            }
            else if (Faces.Count == 0 && Normal(list)[1] < -Eps) list.Reverse();
            return AddFace(list, color, part);
        }

        // ---- changing ----

        public void Move(IEnumerable<int> vertices, double[] by)
        {
            foreach (int v in vertices) Vertices[v] = Add(Vertices[v], by);
        }

        public void Scale(IEnumerable<int> vertices, double[] center, double[] factors)
        {
            foreach (int v in vertices)
            {
                var d = Sub(Vertices[v], center);
                Vertices[v] = new[] { center[0] + d[0] * factors[0], center[1] + d[1] * factors[1], center[2] + d[2] * factors[2] };
            }
        }

        /// <summary>Turns corners around an axis (0 x, 1 y, 2 z) through `center` by degrees (counter clockwise looking down the axis).</summary>
        public void Rotate(IEnumerable<int> vertices, double[] center, int axis, double degrees)
        {
            double a = degrees * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            int i = (axis + 1) % 3, j = (axis + 2) % 3;
            foreach (int v in vertices)
            {
                var d = Sub(Vertices[v], center);
                var r = (double[])d.Clone();
                r[i] = d[i] * c - d[j] * s; r[j] = d[i] * s + d[j] * c;
                Vertices[v] = Add(center, r);
            }
        }

        /// <summary>Extrudes faces: their corners are copied and moved `distance` out along the faces (every face of the region moves by the distance, so
        /// a box corner moves diagonally), walls join the old border to the new one. A region with a free border (a face on its own, a flat shape)
        /// keeps a copy of itself turned round as its bottom - a closed body. The new faces in place of the old ones (the same indices), then the walls.</summary>
        public List<int> Extrude(IReadOnlyCollection<int> faces, double distance)
        {
            var sel = new HashSet<int>(faces.Where(f => f >= 0 && f < Faces.Count));
            if (sel.Count == 0) return new List<int>();
            // the directed edges of the region; its border: an edge whose reverse is not in the region
            var directed = new HashSet<(int, int)>();
            foreach (int f in sel) { var l = Faces[f].V; for (int i = 0; i < l.Count; i++) directed.Add((l[i], l[(i + 1) % l.Count])); }
            var border = new List<(int A, int B, int Face)>();
            foreach (int f in sel.OrderBy(f => f))
            {
                var l = Faces[f].V;
                for (int i = 0; i < l.Count; i++) { int a = l[i], b = l[(i + 1) % l.Count]; if (!directed.Contains((b, a))) border.Add((a, b, f)); }
            }
            // is the border free (no other face on it)?
            var otherEdges = new HashSet<(int, int)>();
            for (int f = 0; f < Faces.Count; f++)
                if (!sel.Contains(f)) { var l = Faces[f].V; for (int i = 0; i < l.Count; i++) otherEdges.Add(Edge(l[i], l[(i + 1) % l.Count])); }
            bool free = border.All(e => !otherEdges.Contains(Edge(e.A, e.B)));
            var bottom = free ? sel.OrderBy(f => f).Select(f => Faces[f].Clone()).ToList() : new List<Face>();
            // where each corner goes: d * a / |a|^2 with a the mean of the normals of its faces (each of those faces then moves by d)
            var sum = new Dictionary<int, (double[] N, int Count)>();
            foreach (int f in sel)
            {
                var n = Normal(f);
                foreach (int v in Faces[f].V) sum[v] = sum.TryGetValue(v, out var s) ? (Add(s.N, n), s.Count + 1) : (n, 1);
            }
            var copy = new Dictionary<int, int>();
            foreach (var (v, (n, count)) in sum.OrderBy(p => p.Key))
            {
                var a = Mul(n, 1.0 / count);
                double aa = Dot(a, a);
                var offset = aa > 0.2 ? Mul(a, distance / aa) : Mul(Normalize(a), distance);
                copy[v] = AddVertex(Vertices[v][0] + offset[0], Vertices[v][1] + offset[1], Vertices[v][2] + offset[2]);
            }
            foreach (int f in sel) Faces[f].V = Faces[f].V.Select(v => copy[v]).ToList();
            var added = new List<int>();
            foreach (var (a, b, f) in border)
                added.Add(AddFace(new[] { a, b, copy[b], copy[a] }, Faces[f].Color, Faces[f].Part));
            foreach (var face in bottom)
            {
                face.V.Reverse();
                Faces.Add(face);
                added.Add(Faces.Count - 1);
            }
            return sel.OrderBy(f => f).Concat(added).ToList();
        }

        /// <summary>Mirrors faces at the plane `axis` = `plane` (0 x, 1 y, 2 z): copies them (copy) or moves them (no copy). The copies share the corners that
        /// lie on the plane (the halves stay joined); a face that lies in the plane is not copied. The faces mirrored (the copies, or the faces themselves).</summary>
        public List<int> Mirror(IReadOnlyCollection<int> faces, int axis, double plane, bool copy = true)
        {
            var sel = faces.Where(f => f >= 0 && f < Faces.Count).Distinct().OrderBy(f => f).ToList();
            var result = new List<int>();
            if (!copy)
            {
                foreach (int v in VerticesOf(sel)) Vertices[v][axis] = 2 * plane - Vertices[v][axis];
                foreach (int f in sel) { Faces[f].V.Reverse(); result.Add(f); }
                return result;
            }
            var map = new Dictionary<int, int>();
            int MirrorOf(int v)
            {
                if (map.TryGetValue(v, out int m)) return m;
                var p = Vertices[v];
                if (Math.Abs(p[axis] - plane) < Eps) m = v;
                else { var q = (double[])p.Clone(); q[axis] = 2 * plane - p[axis]; Vertices.Add(q); m = Vertices.Count - 1; }
                map[v] = m;
                return m;
            }
            foreach (int f in sel)
            {
                var face = Faces[f];
                if (face.V.All(v => Math.Abs(Vertices[v][axis] - plane) < Eps)) continue;
                var loop = face.V.Select(MirrorOf).Reverse().ToList();
                Faces.Add(new Face { V = loop, Color = face.Color, Part = face.Part });
                result.Add(Faces.Count - 1);
            }
            return result;
        }

        /// <summary>Turns faces round (the other side is seen).</summary>
        public void Flip(IEnumerable<int> faces) { foreach (int f in faces) Faces[f].V.Reverse(); }

        /// <summary>Takes faces away, and the corners no face uses any more.</summary>
        public void DeleteFaces(IEnumerable<int> faces)
        {
            var set = new HashSet<int>(faces);
            var keep = new List<Face>();
            for (int f = 0; f < Faces.Count; f++) if (!set.Contains(f)) keep.Add(Faces[f]);
            Faces = keep;
            RemoveUnused();
        }

        /// <summary>Takes corners away with every face at them.</summary>
        public void DeleteVertices(IEnumerable<int> vertices)
        {
            var set = new HashSet<int>(vertices);
            Faces.RemoveAll(f => f.V.Any(set.Contains));
            foreach (int v in set.OrderByDescending(v => v)) if (v >= 0 && v < Vertices.Count) RemoveVertexAt(v);
        }

        /// <summary>Takes the faces at edges away (and the corners left without faces).</summary>
        public void DeleteEdges(IEnumerable<(int A, int B)> edges)
        {
            var set = new HashSet<(int, int)>(edges.Select(e => Edge(e.A, e.B)));
            var gone = new List<int>();
            for (int f = 0; f < Faces.Count; f++)
            {
                var l = Faces[f].V;
                for (int i = 0; i < l.Count; i++) if (set.Contains(Edge(l[i], l[(i + 1) % l.Count]))) { gone.Add(f); break; }
            }
            DeleteFaces(gone);
        }

        private void RemoveVertexAt(int v)
        {
            Vertices.RemoveAt(v);
            foreach (var f in Faces) for (int i = 0; i < f.V.Count; i++) if (f.V[i] > v) f.V[i]--;
        }

        /// <summary>Takes away the corners no face uses; the new index of every old corner (-1: gone).</summary>
        public int[] RemoveUnused()
        {
            var used = new bool[Vertices.Count];
            foreach (var f in Faces) foreach (int v in f.V) used[v] = true;
            var map = new int[Vertices.Count];
            var keep = new List<double[]>();
            for (int v = 0; v < Vertices.Count; v++) { map[v] = used[v] ? keep.Count : -1; if (used[v]) keep.Add(Vertices[v]); }
            Vertices = keep;
            foreach (var f in Faces) for (int i = 0; i < f.V.Count; i++) f.V[i] = map[f.V[i]];
            return map;
        }

        /// <summary>Joins corners closer than `distance` (of the set, or all) into one; faces that lose their area go. How many corners went.</summary>
        public int Weld(IEnumerable<int>? vertices = null, double distance = 1e-4)
        {
            var list = (vertices ?? Enumerable.Range(0, Vertices.Count)).Distinct().OrderBy(v => v).ToList();
            var target = Enumerable.Range(0, Vertices.Count).ToArray();
            int joined = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (target[list[i]] != list[i]) continue;
                for (int j = i + 1; j < list.Count; j++)
                    if (target[list[j]] == list[j] && Length(Sub(Vertices[list[i]], Vertices[list[j]])) <= distance) { target[list[j]] = list[i]; joined++; }
            }
            if (joined == 0) return 0;
            Remap(target);
            return joined;
        }

        /// <summary>Puts corners together at their middle (one corner).</summary>
        public void Merge(IEnumerable<int> vertices)
        {
            var list = vertices.Distinct().OrderBy(v => v).ToList();
            if (list.Count < 2) return;
            Vertices[list[0]] = Center(list);
            var target = Enumerable.Range(0, Vertices.Count).ToArray();
            foreach (int v in list) target[v] = list[0];
            Remap(target);
        }

        private void Remap(int[] target)
        {
            foreach (var f in Faces)
            {
                var l = new List<int>();
                foreach (int v in f.V.Select(v => target[v])) if (l.Count == 0 || l[^1] != v) l.Add(v);
                while (l.Count > 1 && l[0] == l[^1]) l.RemoveAt(l.Count - 1);
                f.V = l;
            }
            Faces.RemoveAll(f => f.V.Count < 3 || f.V.Distinct().Count() != f.V.Count);
            RemoveUnused();
        }

        /// <summary>A new corner in the middle of an edge, in every face at the edge; its index.</summary>
        public int SplitEdge(int a, int b, double at = 0.5)
        {
            var pa = Vertices[a]; var pb = Vertices[b];
            int m = AddVertex(pa[0] + (pb[0] - pa[0]) * at, pa[1] + (pb[1] - pa[1]) * at, pa[2] + (pb[2] - pa[2]) * at);
            foreach (var f in Faces)
                for (int i = 0; i < f.V.Count; i++)
                {
                    int p = f.V[i], q = f.V[(i + 1) % f.V.Count];
                    if ((p == a && q == b) || (p == b && q == a)) { f.V.Insert(i + 1, m); break; }
                }
            return m;
        }

        /// <summary>Cuts a face in two along the line between two of its corners (not neighbours); the new face, or -1.</summary>
        public int SplitFace(int face, int a, int b)
        {
            var l = Faces[face].V;
            int i = l.IndexOf(a), j = l.IndexOf(b);
            if (i < 0 || j < 0 || i == j || (i + 1) % l.Count == j || (j + 1) % l.Count == i) return -1;
            if (i > j) (i, j) = (j, i);
            var first = l.Take(i + 1).Concat(l.Skip(j)).ToList();
            var second = l.Skip(i).Take(j - i + 1).ToList();
            Faces[face].V = first;
            Faces.Add(new Face { V = second, Color = Faces[face].Color, Part = Faces[face].Part });
            return Faces.Count - 1;
        }

        /// <summary>A colour of the palette was taken out: its faces take colour 0, the colours after it move down.</summary>
        public void RemoveColor(int index)
        {
            if (index < 0 || index >= Palette.Count || Palette.Count <= 1) return;
            Palette.RemoveAt(index);
            foreach (var f in Faces) f.Color = f.Color == index ? 0 : f.Color > index ? f.Color - 1 : f.Color;
        }

        /// <summary>A part was taken out: its faces go to its parent (or part 0), its children hang from its parent, its keys go.</summary>
        public void RemovePart(int index)
        {
            if (index <= 0 || index >= Parts.Count) return;
            int parent = Math.Max(0, Parts[index].Parent);
            foreach (var p in Parts) if (p.Parent == index) p.Parent = parent;
            foreach (var p in Parts) if (p.Parent > index) p.Parent--;
            Parts.RemoveAt(index);
            foreach (var a in Animations) { a.Keys.RemoveAll(k => k.Part == index); foreach (var k in a.Keys) if (k.Part > index) k.Part--; }
            if (parent > index) parent--;
            foreach (var f in Faces) f.Part = f.Part == index ? parent : f.Part > index ? f.Part - 1 : f.Part;
        }

        // ---- picking ----

        /// <summary>The nearest face a ray hits (either side; the faces as fans of triangles): the face and the distance along the ray (in lengths of `d`).</summary>
        public bool Raycast(double[] o, double[] d, out int face, out double t)
        {
            face = -1; t = double.MaxValue;
            for (int f = 0; f < Faces.Count; f++)
            {
                var l = Faces[f].V;
                for (int i = 1; i + 1 < l.Count; i++)
                    if (RayTriangle(o, d, Vertices[l[0]], Vertices[l[i]], Vertices[l[i + 1]], out double h) && h < t) { t = h; face = f; }
            }
            return face >= 0;
        }

        private static bool RayTriangle(double[] o, double[] d, double[] a, double[] b, double[] c, out double t)
        {
            t = 0;
            var e1 = Sub(b, a); var e2 = Sub(c, a);
            var p = Cross(d, e2);
            double det = Dot(e1, p);
            if (Math.Abs(det) < 1e-12) return false;
            var s = Sub(o, a);
            double u = Dot(s, p) / det;
            if (u < 0 || u > 1) return false;
            var q = Cross(s, e1);
            double v = Dot(d, q) / det;
            if (v < 0 || u + v > 1) return false;
            t = Dot(e2, q) / det;
            return t > 1e-9;
        }

        // ---- text ----

        public static MeshModel Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return CreateDefault();
            JsonElement root;
            try { root = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }).RootElement; }
            catch (JsonException e) { throw new InvalidDataException("The mesh model is no valid JSON: " + e.Message); }
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("fmesh", out var v) || v.GetInt32() != 1) throw new InvalidDataException("Not a mesh model (\"fmesh\": 1 is missing).");
            var m = new MeshModel();
            if (root.TryGetProperty("palette", out var pal))
                foreach (var c in pal.EnumerateArray()) m.Palette.Add(BlockModel.ParseColor(c.GetString() ?? "#FFFFFF"));
            if (m.Palette.Count == 0) m.Palette.AddRange(BlockModel.DefaultPalette);
            m.Parts.AddRange(BlockModel.ParseParts(root));
            if (m.Parts.Count == 0) m.Parts.Add(new BlockModel.Part { Name = "body", Pivot = new double[3] });
            m.Animations.AddRange(BlockModel.ParseAnimations(root));
            if (root.TryGetProperty("vertices", out var verts))
                foreach (var p in verts.EnumerateArray())
                {
                    var a = p.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    if (a.Length != 3) throw new InvalidDataException("A corner is [x, y, z].");
                    m.Vertices.Add(a);
                }
            if (root.TryGetProperty("faces", out var faces))
                foreach (var f in faces.EnumerateArray())
                {
                    var a = f.EnumerateArray().Select(e => e.GetInt32()).ToArray();
                    if (a.Length < 5) throw new InvalidDataException("A face is [colour, part, v0, v1, v2, ...].");
                    var loop = a.Skip(2).ToList();
                    if (loop.Any(i => i < 0 || i >= m.Vertices.Count)) throw new InvalidDataException("A face names a corner that is not there.");
                    m.Faces.Add(new Face { Color = Math.Clamp(a[0], 0, m.Palette.Count - 1), Part = Math.Clamp(a[1], 0, m.Parts.Count - 1), V = loop });
                }
            return m;
        }

        /// <summary>The text of the model: a corner a line, a face a line (numbers rounded to a micrometer) - small diffs in git.</summary>
        public string ToJson()
        {
            var inv = CultureInfo.InvariantCulture;
            string N(double x) { double r = Math.Round(x, 6); return (r == 0 ? 0 : r).ToString("R", inv); }
            var sb = new StringBuilder();
            sb.Append("{\n  \"fmesh\": 1,\n");
            sb.Append("  \"palette\": [").Append(string.Join(", ", Palette.Select(c => "\"" + BlockModel.ColorText(c) + "\""))).Append("],\n");
            BlockModel.AppendParts(sb, Parts);
            sb.Append(",\n  \"vertices\": [");
            for (int i = 0; i < Vertices.Count; i++)
                sb.Append(i == 0 ? "\n    " : ",\n    ").Append('[').Append(N(Vertices[i][0])).Append(", ").Append(N(Vertices[i][1])).Append(", ").Append(N(Vertices[i][2])).Append(']');
            sb.Append(Vertices.Count > 0 ? "\n  ]" : "]");
            sb.Append(",\n  \"faces\": [");
            for (int i = 0; i < Faces.Count; i++)
                sb.Append(i == 0 ? "\n    " : ",\n    ").Append('[').Append(Faces[i].Color).Append(", ").Append(Faces[i].Part).Append(", ").Append(string.Join(", ", Faces[i].V)).Append(']');
            sb.Append(Faces.Count > 0 ? "\n  ]" : "]");
            BlockModel.AppendAnimations(sb, Animations);
            sb.Append("\n}\n");
            return sb.ToString();
        }

        // ---- the model file ----

        /// <summary>fire's model file: a part of the file for each part (at its pivot), under it one for each colour it uses (flat triangles).</summary>
        public byte[] ToF3d()
        {
            var parts = new List<ModelConverter.Part>();
            var indexOf = new int[Parts.Count];
            for (int i = 0; i < Parts.Count; i++)
            {
                var p = Parts[i];
                double[] parentPivot = p.Parent >= 0 ? Parts[p.Parent].Pivot : new double[3];
                indexOf[i] = parts.Count;
                parts.Add(new ModelConverter.Part
                {
                    Name = p.Name.Length > 31 ? p.Name[..31] : p.Name,
                    Parent = p.Parent >= 0 ? indexOf[p.Parent] : -1,
                    T = Sub(p.Pivot, parentPivot),
                });
                foreach (int color in Faces.Where(f => f.Part == i).Select(f => f.Color).Distinct().OrderBy(c => c))
                {
                    var mesh = new ModelConverter.Part { Name = (p.Name.Length > 24 ? p.Name[..24] : p.Name) + "#" + color, Parent = indexOf[i], HasUv = true };
                    uint rgb = Palette[Math.Clamp(color, 0, Palette.Count - 1)];
                    mesh.Color = new[] { (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, (byte)255 };
                    foreach (var f in Faces.Where(f => f.Part == i && f.Color == color))
                    {
                        var n = Normal(f.V);
                        if (Length(n) < 0.5) continue;
                        // texture coordinates on the plane the face is most turned to (a copy of a texture a meter)
                        int axis = Math.Abs(n[0]) > Math.Abs(n[1]) ? (Math.Abs(n[0]) > Math.Abs(n[2]) ? 0 : 2) : (Math.Abs(n[1]) > Math.Abs(n[2]) ? 1 : 2);
                        int b = mesh.Positions.Count;
                        foreach (int v in f.V)
                        {
                            var c = Vertices[v];
                            mesh.Positions.Add(Sub(c, p.Pivot));
                            mesh.Normals.Add(n);
                            mesh.Uvs.Add(new[] { c[(axis + 1) % 3], c[(axis + 2) % 3] });
                        }
                        for (int k = 1; k + 1 < f.V.Count; k++) mesh.Indices.AddRange(new[] { b, b + k, b + k + 1 });
                        if (mesh.Positions.Count > 65531) throw new InvalidDataException($"The part '{p.Name}' has too many faces of one colour for one mesh.");
                    }
                    parts.Add(mesh);
                }
            }
            if (parts.Count > 32767) throw new InvalidDataException("Too many parts.");
            int fps = ModelConverter.FramesPerSecond;
            var clips = new List<(string, int, double[][][], bool)>();
            foreach (var anim in Animations)
            {
                int frames = Math.Max(1, (int)Math.Round(anim.Length * fps) + (anim.Loop ? 0 : 1));
                var poses = new double[frames][][];
                for (int fr = 0; fr < frames; fr++)
                {
                    poses[fr] = new double[parts.Count][];
                    for (int k = 0; k < parts.Count; k++) poses[fr][k] = new double[] { 0, 0, 0, 0, 0, 0, 1, 1, 1, 1 };
                    for (int i = 0; i < Parts.Count; i++)
                    {
                        var (rot, move, scale) = BlockModel.PoseOf(anim, i, (double)fr / fps);
                        var p = Parts[i];
                        double[] parentPivot = p.Parent >= 0 ? Parts[p.Parent].Pivot : new double[3];
                        var q = ModelConverter.QuatOf(BlockModel.Rotation(rot));
                        poses[fr][indexOf[i]] = new[]
                        {
                            p.Pivot[0] - parentPivot[0] + move[0], p.Pivot[1] - parentPivot[1] + move[1], p.Pivot[2] - parentPivot[2] + move[2],
                            q[0], q[1], q[2], q[3], scale[0], scale[1], scale[2],
                        };
                    }
                }
                clips.Add((anim.Name.Length > 31 ? anim.Name[..31] : anim.Name, frames, poses, anim.Loop));
            }
            return ModelConverter.Write(parts, clips, fps);
        }
    }
}
