using System.Text;

/// <summary>A small glTF 2.0 model for the tests of scene3d's models (docs/graphics/models.md): a body (a blue box) with an arm (a red box, a child part) and two animations -
/// "wave" turns the arm around z (0, 90, 0 degrees in one second), "jump" lifts the body (0, 0.5, 0 m in half a second, step keys for the scale).</summary>
static class ModelTestData
{
    private static void Box(List<float> pos, List<float> nrm, List<ushort> idx, float sx, float sy, float sz, float oy)
    {
        // six faces, four corners each (flat normals), counter clockwise seen from outside
        float[,] n = { { 1, 0, 0 }, { -1, 0, 0 }, { 0, 1, 0 }, { 0, -1, 0 }, { 0, 0, 1 }, { 0, 0, -1 } };
        for (int f = 0; f < 6; f++)
        {
            float nx = n[f, 0], ny = n[f, 1], nz = n[f, 2];
            // two axes in the face: u, v with u x v = n
            float[] u = Math.Abs(ny) > 0.5f ? new float[] { 0, 0, ny } : new float[] { -nz, 0, nx };
            float[] v = { ny * u[2] - nz * u[1], nz * u[0] - nx * u[2], nx * u[1] - ny * u[0] };
            ushort b = (ushort)(pos.Count / 3);
            float[][] corners = { new[] { -1f, -1f }, new[] { 1f, -1f }, new[] { 1f, 1f }, new[] { -1f, 1f } };
            foreach (var c in corners)
            {
                pos.Add((nx + u[0] * c[0] + v[0] * c[1]) * sx / 2);
                pos.Add((ny + u[1] * c[0] + v[1] * c[1]) * sy / 2 + oy);
                pos.Add((nz + u[2] * c[0] + v[2] * c[1]) * sz / 2);
                nrm.Add(nx); nrm.Add(ny); nrm.Add(nz);
            }
            idx.AddRange(new ushort[] { b, (ushort)(b + 1), (ushort)(b + 2), b, (ushort)(b + 2), (ushort)(b + 3) });
        }
    }

    /// <summary>The robot as a .gltf text (one buffer as a data URI).</summary>
    public static string RobotGltf()
    {
        var bin = new MemoryStream();
        var w = new BinaryWriter(bin);
        var views = new List<string>();
        var accessors = new List<string>();
        int AddView(byte[] data)
        {
            while (bin.Length % 4 != 0) w.Write((byte)0);
            long offset = bin.Length;
            w.Write(data);
            views.Add($"{{\"buffer\":0,\"byteOffset\":{offset},\"byteLength\":{data.Length}}}");
            return views.Count - 1;
        }
        int AddFloats(float[] values, string type, int count, bool minmax)
        {
            var b = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, b, 0, b.Length);
            int view = AddView(b);
            string mm = "";
            if (minmax)
            {
                int k = type == "VEC3" ? 3 : 1;
                var lo = new float[k]; var hi = new float[k];
                for (int j = 0; j < k; j++) { lo[j] = float.MaxValue; hi[j] = float.MinValue; for (int i = j; i < values.Length; i += k) { lo[j] = Math.Min(lo[j], values[i]); hi[j] = Math.Max(hi[j], values[i]); } }
                mm = $",\"min\":[{string.Join(",", lo.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)))}],\"max\":[{string.Join(",", hi.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)))}]";
            }
            accessors.Add($"{{\"bufferView\":{view},\"componentType\":5126,\"count\":{count},\"type\":\"{type}\"{mm}}}");
            return accessors.Count - 1;
        }
        int AddIndices(ushort[] values)
        {
            var b = new byte[values.Length * 2];
            Buffer.BlockCopy(values, 0, b, 0, b.Length);
            int view = AddView(b);
            accessors.Add($"{{\"bufferView\":{view},\"componentType\":5123,\"count\":{values.Length},\"type\":\"SCALAR\"}}");
            return accessors.Count - 1;
        }
        string Mesh(float sx, float sy, float sz, float oy, int material)
        {
            var pos = new List<float>(); var nrm = new List<float>(); var idx = new List<ushort>();
            Box(pos, nrm, idx, sx, sy, sz, oy);
            int p = AddFloats(pos.ToArray(), "VEC3", pos.Count / 3, true), n = AddFloats(nrm.ToArray(), "VEC3", nrm.Count / 3, false), i = AddIndices(idx.ToArray());
            return $"{{\"primitives\":[{{\"attributes\":{{\"POSITION\":{p},\"NORMAL\":{n}}},\"indices\":{i},\"material\":{material}}}]}}";
        }
        string body = Mesh(1, 1, 1, 0.5f, 0), arm = Mesh(0.2f, 0.8f, 0.2f, -0.4f, 1);
        // wave: the arm around z; jump: the body up and down
        int waveT = AddFloats(new[] { 0f, 0.5f, 1f }, "SCALAR", 3, true);
        float s45 = (float)Math.Sin(Math.PI / 4), c45 = (float)Math.Cos(Math.PI / 4);
        int waveR = AddFloats(new[] { 0f, 0, 0, 1, 0, 0, s45, c45, 0, 0, 0, 1 }, "VEC4", 3, false);
        int jumpT = AddFloats(new[] { 0f, 0.25f, 0.5f }, "SCALAR", 3, true);
        int jumpP = AddFloats(new[] { 0f, 0, 0, 0, 0.5f, 0, 0, 0, 0 }, "VEC3", 3, false);
        int jumpS = AddFloats(new[] { 1f, 1, 1, 1.2f, 0.8f, 1.2f, 1, 1, 1 }, "VEC3", 3, false);
        string data = Convert.ToBase64String(bin.ToArray());
        var sb = new StringBuilder();
        sb.Append("{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],");
        sb.Append("\"nodes\":[{\"name\":\"body\",\"mesh\":0,\"children\":[1]},{\"name\":\"arm\",\"mesh\":1,\"translation\":[0.6,0.9,0]}],");
        sb.Append($"\"meshes\":[{body},{arm}],");
        sb.Append("\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.2,0.4,0.9,1]}},{\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.9,0.2,0.2,1]}}],");
        sb.Append("\"animations\":[");
        sb.Append($"{{\"name\":\"wave\",\"samplers\":[{{\"input\":{waveT},\"output\":{waveR}}}],\"channels\":[{{\"sampler\":0,\"target\":{{\"node\":1,\"path\":\"rotation\"}}}}]}},");
        sb.Append($"{{\"name\":\"jump\",\"samplers\":[{{\"input\":{jumpT},\"output\":{jumpP}}},{{\"input\":{jumpT},\"output\":{jumpS},\"interpolation\":\"STEP\"}}],\"channels\":[{{\"sampler\":0,\"target\":{{\"node\":0,\"path\":\"translation\"}}}},{{\"sampler\":1,\"target\":{{\"node\":0,\"path\":\"scale\"}}}}]}}");
        sb.Append("],");
        sb.Append($"\"accessors\":[{string.Join(",", accessors)}],\"bufferViews\":[{string.Join(",", views)}],");
        sb.Append($"\"buffers\":[{{\"byteLength\":{bin.Length},\"uri\":\"data:application/octet-stream;base64,{data}\"}}]}}");
        return sb.ToString();
    }

    /// <summary>The robot as an .f3d (through forge's converter).</summary>
    public static byte[] RobotF3d() => fire.Compiler.ModelConverter.Convert(Encoding.UTF8.GetBytes(RobotGltf()), _ => throw new IOException("no external buffers"));
}
