using System.Text;
using fire.Compiler;
using fire.Runtime;
using fire.Values;

/// <summary>
/// The package ai (docs/ai/README.md): a tiny model with random weights, written here as a GGUF file (a qwen2 model of 2 layers, every kind of weights the engine
/// reads: F32, F16, Q8_0, Q4_0; a byte-level vocabulary with a few merges and the ChatML control tokens). Its answers are nonsense, but the same in the VM
/// and in the native build - the tokenizer, the cache, the stop texts and the choice run through the same code as with a real model. With
/// FIRE_TEST_AI_MODEL=path/to/qwen2.5-0.5b-instruct-q4_0.gguf a real model answers a question, too (Program.cs).
/// </summary>
static class AiTests
{
    public const string Name = "AI: GGUF lesen, Tokenizer, Cache, Generieren, Stop-Texte, Auswahl, Fehler - nativ == VM";
    public const string McpName = "MCP: Server und Client (Tools, Ressourcen, Prompts, Benachrichtigungen, Fehler), JSON-RPC, SSE - nativ == VM";

    /// <summary>The package mcp within one program (the direct transport): what a client asks and the server answers, the errors of JSON-RPC, the events of
    /// Streamable HTTP. (stdio and HTTP against the official Python SDK of MCP are checked by hand: docs/networking/mcp.md.)</summary>
    public const string McpScript = """
        #import "mcp"
        #import "json"

        var server = new Mcp.Server("fire-test", "0.1")
        server.Instructions = "Tools for tests."
        server.Tool("add", "Adds two numbers", "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"number\"},\"b\":{\"type\":\"number\"}},\"required\":[\"a\",\"b\"]}",
            (JsonObject args) => Mcp.Result.Text("" + (args.Get("a") + args.Get("b"))))
        server.Tool("fail", "Always fails", "", (JsonObject args) => Mcp.Result.Error("it failed"))
        server.Tool("boom", "Throws", "", (JsonObject args) => { throw new Exception("boom!") })
        server.Resource("file:///notes.txt", "notes", "The notes", "text/plain", () => "Buy milk.")
        server.Prompt("greet", "A greeting", ["name"], (JsonObject a) => "Say hello to " + a.Get("name"))
        var client = new Mcp.Client(new Mcp.DirectTransport(server))
        client.Initialize("tester", "1.0")
        print("server " + client.ServerName + " " + client.ServerVersion + " " + client.ProtocolVersion + " " + client.Instructions + " " + (client.Capabilities.IndexOf("listChanged") >= 0))
        foreach (t in client.Tools()) { Mcp.Tool tool = t; print("tool " + tool.Name + ": " + tool.Description + " " + tool.Schema) }
        print("add " + client.CallTool("add", "{\"a\":2,\"b\":3}").Text)
        var args = new JsonObject()
        args.Set("a", 40)
        args.Set("b", 2)
        print("add2 " + client.CallTool("add", args).Text)
        var r = client.CallTool("fail")
        print("fail " + r.IsError + " " + r.Text)
        var b = client.CallTool("boom")
        print("boom " + b.IsError + " " + b.Text)
        try { client.CallTool("nope") } catch (Mcp.McpException e) { print("nope " + e.code + " " + e.message) }
        foreach (x in client.Resources()) { Mcp.Resource res = x; print("res " + res.Uri + " " + res.Name + " " + res.MimeType) }
        print("read " + client.ReadResource("file:///notes.txt"))
        try { client.ReadResource("file:///x") } catch (Mcp.McpException e) { print("noread " + e.code) }
        foreach (x in client.Prompts()) { Mcp.Prompt pr = x; print("prompt " + pr.Name + " " + pr.Arguments[0]) }
        print("get " + client.GetPrompt("greet", "{\"name\":\"Ann\"}"))
        client.Subscribe("file:///notes.txt")
        server.ResourceUpdated("file:///notes.txt")
        server.ResourceUpdated("file:///other.txt")
        server.Tool("later", "added later", "", (JsonObject a) => Mcp.Result.Text("x"))
        server.Log("info", "hello")
        foreach (x in client.Notifications()) { Mcp.Notification n = x; print("note " + n.Method + " " + n.Params) }
        client.Ping()
        print("raw " + server.Handle("not json").Substring(0, 64))
        print("raw2 " + server.Handle("{\"jsonrpc\":\"2.0\",\"id\":\"x7\",\"method\":\"nothing\"}"))
        print("raw3 " + server.Handle("[1,2]").Substring(0, 64))
        print("raw4 [" + server.Handle("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\"}") + "]")
        print("raw5 " + server.Handle("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"1999-01-01\"}}").Substring(0, 70))
        var events = new List()
        Mcp.HttpTransport.Events("event: message\ndata: {\"a\":1}\n\n: comment\nevent: message\r\ndata: {\"b\":\r\ndata: 2}\r\n\r\n", events)
        print("events " + events.Count + " " + events[0] + " " + events[1])
        print("hex " + Mcp.HttpTransport.Hex("1a\r") + " " + Mcp.HttpTransport.Hex("FF"))
        client.Close()
        """;

    // ---- the GGUF writer ----
    private sealed class Writer
    {
        private readonly MemoryStream _kv = new();
        private readonly List<(string Name, long[] Shape, int Type, byte[] Data)> _tensors = new();
        private int _kvCount;
        private readonly Random _random = new(1234);

        private static void Str(BinaryWriter w, string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }
        private BinaryWriter Key(string key, uint type) { var w = new BinaryWriter(_kv, Encoding.UTF8, true); Str(w, key); w.Write(type); _kvCount++; return w; }
        public void U32(string key, uint v) { using var w = Key(key, 4); w.Write(v); }
        public void F32(string key, float v) { using var w = Key(key, 6); w.Write(v); }
        public void Text(string key, string v) { using var w = Key(key, 8); Str(w, v); }
        public void Bool(string key, bool v) { using var w = Key(key, 7); w.Write((byte)(v ? 1 : 0)); }
        public void Texts(string key, IReadOnlyList<string> v) { using var w = Key(key, 9); w.Write(8u); w.Write((ulong)v.Count); foreach (var s in v) Str(w, s); }
        public void Ints(string key, IReadOnlyList<int> v) { using var w = Key(key, 9); w.Write(5u); w.Write((ulong)v.Count); foreach (var i in v) w.Write(i); }
        public void Bools(string key, IReadOnlyList<bool> v) { using var w = Key(key, 9); w.Write(7u); w.Write((ulong)v.Count); foreach (var b in v) w.Write((byte)(b ? 1 : 0)); }

        private float[] Values(long count, float scale)
        {
            var v = new float[count];
            for (long i = 0; i < count; i++) v[i] = (float)((_random.NextDouble() * 2 - 1) * scale);
            return v;
        }

        /// <summary>A tensor of `shape` (shape[0]: the length of a row) with random values (or `values`), stored as `type` (0 F32, 1 F16, 2 Q4_0, 8 Q8_0).</summary>
        public void Tensor(string name, long[] shape, int type, float scale = 0.2f, float[]? values = null)
        {
            long count = shape.Aggregate(1L, (a, b) => a * b);
            var v = values ?? Values(count, scale);
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
            {
                switch (type)
                {
                    case 0: foreach (var f in v) w.Write(f); break;
                    case 1: foreach (var f in v) w.Write(BitConverter.HalfToUInt16Bits((Half)f)); break;
                    case 8:
                        for (long b = 0; b < count / 32; b++)
                        {
                            float amax = 0;
                            for (int i = 0; i < 32; i++) amax = Math.Max(amax, Math.Abs(v[b * 32 + i]));
                            float d = amax / 127f, id = d != 0 ? 1 / d : 0;
                            w.Write(BitConverter.HalfToUInt16Bits((Half)d));
                            for (int i = 0; i < 32; i++) w.Write((sbyte)Math.Round(v[b * 32 + i] * id));
                        }
                        break;
                    case 2:
                        for (long b = 0; b < count / 32; b++)
                        {
                            float amax = 0, max = 0;
                            for (int i = 0; i < 32; i++) { float x = v[b * 32 + i]; if (Math.Abs(x) > amax) { amax = Math.Abs(x); max = x; } }
                            float d = max / -8f, id = d != 0 ? 1 / d : 0;
                            w.Write(BitConverter.HalfToUInt16Bits((Half)d));
                            for (int i = 0; i < 16; i++)
                            {
                                int lo = Math.Min(15, (int)(v[b * 32 + i] * id + 8.5f)), hi = Math.Min(15, (int)(v[b * 32 + i + 16] * id + 8.5f));
                                w.Write((byte)(lo | (hi << 4)));
                            }
                        }
                        break;
                }
            }
            _tensors.Add((name, shape, type, ms.ToArray()));
        }

        public byte[] Bytes()
        {
            var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("GGUF"));
            w.Write(3u);
            w.Write((ulong)_tensors.Count);
            w.Write((ulong)_kvCount);
            w.Write(_kv.ToArray());
            ulong offset = 0;
            foreach (var t in _tensors)
            {
                Str(w, t.Name);
                w.Write((uint)t.Shape.Length);
                foreach (var d in t.Shape) w.Write((ulong)d);
                w.Write((uint)t.Type);
                w.Write(offset);
                offset += ((ulong)t.Data.Length + 31) / 32 * 32;
            }
            while (ms.Length % 32 != 0) w.Write((byte)0);
            foreach (var t in _tensors)
            {
                w.Write(t.Data);
                for (int i = t.Data.Length; i % 32 != 0; i++) w.Write((byte)0);
            }
            w.Flush();
            return ms.ToArray();
        }
    }

    /// <summary>The characters of the bytes in a byte-level BPE vocabulary (gpt2): the printable ones themselves, the others moved past 255.</summary>
    private static string[] ByteChars()
    {
        var map = new string[256];
        int n = 0;
        for (int b = 0; b < 256; b++)
        {
            bool printable = (b >= '!' && b <= '~') || (b >= 0xA1 && b <= 0xAC) || (b >= 0xAE && b <= 0xFF);
            map[b] = char.ConvertFromUtf32(printable ? b : 256 + n++);
        }
        return map;
    }

    /// <summary>The tiny model as GGUF bytes.</summary>
    public static byte[] TinyModel()
    {
        const int embd = 64, heads = 4, kvHeads = 2, ff = 128, layers = 2;
        var w = new Writer();
        var chars = ByteChars();
        var tokens = new List<string>(chars);
        var types = Enumerable.Repeat(1, 256).ToList();
        var merges = new List<string>();
        void Merge(string a, string b) { merges.Add(a + " " + b); if (!tokens.Contains(a + b)) { tokens.Add(a + b); types.Add(1); } }
        Merge("h", "e"); Merge("l", "l"); Merge("ll", "o"); Merge("he", "llo"); Merge("Ġ", "w"); Merge("o", "r"); Merge("Ġw", "or"); Merge("l", "d"); Merge("Ġwor", "ld");
        foreach (var special in new[] { "<|endoftext|>", "<|im_start|>", "<|im_end|>" }) { tokens.Add(special); types.Add(3); }
        while (tokens.Count % 32 != 0) { tokens.Add("<|extra_" + tokens.Count + "|>"); types.Add(3); }
        int vocab = tokens.Count;
        w.Text("general.architecture", "qwen2");
        w.Text("general.name", "fire-tiny");
        w.U32("general.alignment", 32);
        w.U32("qwen2.block_count", layers);
        w.U32("qwen2.context_length", 4096);
        w.U32("qwen2.embedding_length", embd);
        w.U32("qwen2.feed_forward_length", ff);
        w.U32("qwen2.attention.head_count", heads);
        w.U32("qwen2.attention.head_count_kv", kvHeads);
        w.F32("qwen2.rope.freq_base", 10000f);
        w.F32("qwen2.attention.layer_norm_rms_epsilon", 1e-6f);
        w.Text("tokenizer.ggml.model", "gpt2");
        w.Text("tokenizer.ggml.pre", "qwen2");
        w.Texts("tokenizer.ggml.tokens", tokens);
        w.Ints("tokenizer.ggml.token_type", types);
        w.Texts("tokenizer.ggml.merges", merges);
        w.U32("tokenizer.ggml.eos_token_id", (uint)tokens.IndexOf("<|im_end|>"));
        w.U32("tokenizer.ggml.bos_token_id", (uint)tokens.IndexOf("<|endoftext|>"));
        w.Bool("tokenizer.ggml.add_bos_token", false);
        w.Text("tokenizer.chat_template", "{% for m in messages %}<|im_start|>{{ m.role }}\n{{ m.content }}<|im_end|>\n{% endfor %}");
        int kvDim = embd / heads * kvHeads;
        w.Tensor("token_embd.weight", new long[] { embd, vocab }, 8, 1.0f);
        w.Tensor("output_norm.weight", new long[] { embd }, 0, values: Enumerable.Repeat(1f, embd).ToArray());
        w.Tensor("output.weight", new long[] { embd, vocab }, 0, 0.3f);
        for (int l = 0; l < layers; l++)
        {
            string p = "blk." + l + ".";
            w.Tensor(p + "attn_norm.weight", new long[] { embd }, 0, values: Enumerable.Repeat(1f, embd).ToArray());
            w.Tensor(p + "attn_q.weight", new long[] { embd, embd }, 2);
            w.Tensor(p + "attn_k.weight", new long[] { embd, kvDim }, 2);
            w.Tensor(p + "attn_v.weight", new long[] { embd, kvDim }, 8);
            w.Tensor(p + "attn_q.bias", new long[] { embd }, 0, 0.05f);
            w.Tensor(p + "attn_k.bias", new long[] { kvDim }, 0, 0.05f);
            w.Tensor(p + "attn_v.bias", new long[] { kvDim }, 0, 0.05f);
            w.Tensor(p + "attn_output.weight", new long[] { embd, embd }, 1);
            w.Tensor(p + "ffn_norm.weight", new long[] { embd }, 0, values: Enumerable.Repeat(1f, embd).ToArray());
            w.Tensor(p + "ffn_gate.weight", new long[] { embd, ff }, 2);
            w.Tensor(p + "ffn_up.weight", new long[] { embd, ff }, 8);
            w.Tensor(p + "ffn_down.weight", new long[] { ff, embd }, 0);
        }
        return w.Bytes();
    }

    /// <summary>The vocabulary of the tiny models (the bytes, a few merges, the control tokens), to a multiple of 32.</summary>
    private static (List<string> Tokens, List<int> Types, List<string> Merges) TinyVocab(params string[] specials)
    {
        var tokens = new List<string>(ByteChars());
        var types = Enumerable.Repeat(1, 256).ToList();
        var merges = new List<string>();
        void Merge(string a, string b) { merges.Add(a + " " + b); if (!tokens.Contains(a + b)) { tokens.Add(a + b); types.Add(1); } }
        Merge("h", "e"); Merge("l", "l"); Merge("ll", "o"); Merge("he", "llo"); Merge("\u0120", "w"); Merge("o", "r"); Merge("\u0120w", "or"); Merge("l", "d"); Merge("\u0120wor", "ld");
        foreach (var special in specials) { tokens.Add(special); types.Add(3); }
        while (tokens.Count % 32 != 0) { tokens.Add("<|extra_" + tokens.Count + "|>"); types.Add(3); }
        return (tokens, types, merges);
    }

    private static float[] Ones(int n) => Enumerable.Repeat(1f, n).ToArray();

    /// <summary>A tiny Qwen 3.5: three layers of the gated delta net (the convolution, the state of each head) and one of gated attention with the norms of q and k and
    /// rotary positions on a part of the head.</summary>
    public static byte[] TinyQwen35()
    {
        const int embd = 64, heads = 2, kvHeads = 1, headDim = 32, ff = 128, layers = 4, S = 16, Hk = 2, Hv = 4, convK = 4;
        int keyDim = Hk * S, valueDim = Hv * S, convDim = 2 * keyDim + valueDim;
        var w = new Writer();
        var (tokens, types, merges) = TinyVocab("<|endoftext|>", "<|im_start|>", "<|im_end|>");
        int vocab = tokens.Count;
        w.Text("general.architecture", "qwen35");
        w.Text("general.name", "fire-tiny-qwen35");
        w.U32("qwen35.block_count", layers);
        w.U32("qwen35.context_length", 256);
        w.U32("qwen35.embedding_length", embd);
        w.U32("qwen35.feed_forward_length", ff);
        w.U32("qwen35.attention.head_count", heads);
        w.U32("qwen35.attention.head_count_kv", kvHeads);
        w.U32("qwen35.attention.key_length", headDim);
        w.U32("qwen35.attention.value_length", headDim);
        w.U32("qwen35.rope.dimension_count", 8);
        w.F32("qwen35.rope.freq_base", 10000000f);
        w.F32("qwen35.attention.layer_norm_rms_epsilon", 1e-6f);
        w.U32("qwen35.ssm.conv_kernel", convK);
        w.U32("qwen35.ssm.state_size", S);
        w.U32("qwen35.ssm.group_count", Hk);
        w.U32("qwen35.ssm.time_step_rank", Hv);
        w.U32("qwen35.ssm.inner_size", (uint)valueDim);
        w.U32("qwen35.full_attention_interval", 4);
        w.Text("tokenizer.ggml.model", "gpt2");
        w.Text("tokenizer.ggml.pre", "qwen35");
        w.Texts("tokenizer.ggml.tokens", tokens);
        w.Ints("tokenizer.ggml.token_type", types);
        w.Texts("tokenizer.ggml.merges", merges);
        w.U32("tokenizer.ggml.eos_token_id", (uint)tokens.IndexOf("<|im_end|>"));
        w.Tensor("token_embd.weight", new long[] { embd, vocab }, 8, 1.0f);
        w.Tensor("output_norm.weight", new long[] { embd }, 0, values: Ones(embd));
        for (int l = 0; l < layers; l++)
        {
            string p = "blk." + l + ".";
            w.Tensor(p + "attn_norm.weight", new long[] { embd }, 0, values: Ones(embd));
            w.Tensor(p + "post_attention_norm.weight", new long[] { embd }, 0, values: Ones(embd));
            if ((l + 1) % 4 != 0)
            {
                w.Tensor(p + "attn_qkv.weight", new long[] { embd, convDim }, 8);
                w.Tensor(p + "attn_gate.weight", new long[] { embd, valueDim }, 2);
                w.Tensor(p + "ssm_conv1d.weight", new long[] { convK, convDim }, 0, 0.5f);
                w.Tensor(p + "ssm_dt.bias", new long[] { Hv }, 0, 0.5f);
                w.Tensor(p + "ssm_a", new long[] { Hv }, 0, values: new[] { -0.5f, -1f, -2f, -0.1f });
                w.Tensor(p + "ssm_beta.weight", new long[] { embd, Hv }, 8);
                w.Tensor(p + "ssm_alpha.weight", new long[] { embd, Hv }, 0);
                w.Tensor(p + "ssm_norm.weight", new long[] { S }, 0, values: Ones(S));
                w.Tensor(p + "ssm_out.weight", new long[] { valueDim, embd }, 2);
            }
            else
            {
                w.Tensor(p + "attn_q.weight", new long[] { embd, heads * headDim * 2 }, 2);
                w.Tensor(p + "attn_k.weight", new long[] { embd, kvHeads * headDim }, 8);
                w.Tensor(p + "attn_v.weight", new long[] { embd, kvHeads * headDim }, 1);
                w.Tensor(p + "attn_output.weight", new long[] { heads * headDim, embd }, 2);
                w.Tensor(p + "attn_q_norm.weight", new long[] { headDim }, 0, values: Ones(headDim));
                w.Tensor(p + "attn_k_norm.weight", new long[] { headDim }, 0, values: Ones(headDim));
            }
            w.Tensor(p + "ffn_gate.weight", new long[] { embd, ff }, 2);
            w.Tensor(p + "ffn_up.weight", new long[] { embd, ff }, 8);
            w.Tensor(p + "ffn_down.weight", new long[] { ff, embd }, 0);
        }
        return w.Bytes();
    }

    /// <summary>A tiny LFM 3: short convolutions, attention over all positions (with the frequency factors of the rotary positions) and over a window of 4, the
    /// gate of each head, the norms after the attention and the feed forward, the start token.</summary>
    public static byte[] TinyLfm3()
    {
        const int embd = 64, heads = 4, kvHeads = 2, headDim = 16, ff = 128, layers = 4, convL = 3;
        var w = new Writer();
        var (tokens, types, merges) = TinyVocab("<|startoftext|>", "<|im_start|>", "<|im_end|>");
        int vocab = tokens.Count;
        w.Text("general.architecture", "lfm3");
        w.Text("general.name", "fire-tiny-lfm3");
        w.U32("lfm3.block_count", layers);
        w.U32("lfm3.context_length", 256);
        w.U32("lfm3.embedding_length", embd);
        w.U32("lfm3.feed_forward_length", ff);
        w.U32("lfm3.attention.head_count", heads);
        w.Ints("lfm3.attention.head_count_kv", new[] { 0, kvHeads, 0, kvHeads });
        w.Bools("lfm3.attention.sliding_window_pattern", new[] { false, false, false, true });
        w.U32("lfm3.attention.sliding_window", 4);
        w.U32("lfm3.attention.key_length", headDim);
        w.U32("lfm3.rope.dimension_count", headDim);
        w.F32("lfm3.rope.freq_base", 1000000f);
        w.F32("lfm3.rope.freq_base_swa", 10000f);
        w.F32("lfm3.attention.layer_norm_rms_epsilon", 1e-5f);
        w.U32("lfm3.shortconv.l_cache", convL);
        w.Text("tokenizer.ggml.model", "gpt2");
        w.Text("tokenizer.ggml.pre", "lfm2");
        w.Texts("tokenizer.ggml.tokens", tokens);
        w.Ints("tokenizer.ggml.token_type", types);
        w.Texts("tokenizer.ggml.merges", merges);
        w.U32("tokenizer.ggml.bos_token_id", (uint)tokens.IndexOf("<|startoftext|>"));
        w.U32("tokenizer.ggml.eos_token_id", (uint)tokens.IndexOf("<|im_end|>"));
        w.Bool("tokenizer.ggml.add_bos_token", false);
        w.Tensor("token_embd.weight", new long[] { embd, vocab }, 8, 1.0f);
        w.Tensor("output_norm.weight", new long[] { embd }, 0, values: Ones(embd));
        w.Tensor("rope_freqs.weight", new long[] { headDim / 2 }, 0, values: Enumerable.Range(0, headDim / 2).Select(i => 1f + i * 0.5f).ToArray());
        for (int l = 0; l < layers; l++)
        {
            string p = "blk." + l + ".";
            w.Tensor(p + "attn_norm.weight", new long[] { embd }, 0, values: Ones(embd));
            w.Tensor(p + "ffn_norm.weight", new long[] { embd }, 0, values: Ones(embd));
            w.Tensor(p + "post_attention_norm.weight", new long[] { embd }, 0, values: Ones(embd));
            w.Tensor(p + "post_ffw_norm.weight", new long[] { embd }, 0, values: Ones(embd));
            if (l % 2 == 0)
            {
                w.Tensor(p + "shortconv.conv.weight", new long[] { convL, embd }, 0, 0.5f);
                w.Tensor(p + "shortconv.in_proj.weight", new long[] { embd, 3 * embd }, 8);
                w.Tensor(p + "shortconv.out_proj.weight", new long[] { embd, embd }, 2);
            }
            else
            {
                w.Tensor(p + "attn_q.weight", new long[] { embd, heads * headDim }, 2);
                w.Tensor(p + "attn_k.weight", new long[] { embd, kvHeads * headDim }, 8);
                w.Tensor(p + "attn_v.weight", new long[] { embd, kvHeads * headDim }, 8);
                w.Tensor(p + "attn_output.weight", new long[] { heads * headDim, embd }, 2);
                w.Tensor(p + "attn_q_norm.weight", new long[] { headDim }, 0, values: Ones(headDim));
                w.Tensor(p + "attn_k_norm.weight", new long[] { headDim }, 0, values: Ones(headDim));
                w.Tensor(p + "attn_gate.weight", new long[] { embd, heads }, 0);
            }
            w.Tensor(p + "ffn_gate.weight", new long[] { embd, ff }, 2);
            w.Tensor(p + "ffn_up.weight", new long[] { embd, ff }, 8);
            w.Tensor(p + "ffn_down.weight", new long[] { ff, embd }, 1);
        }
        return w.Bytes();
    }

    private static string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), name);
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) File.WriteAllBytes(path, bytes);
        return path.Replace("\\", "/");
    }

    private static string ModelFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "fire-ai-tiny.gguf");
        var bytes = TinyModel();
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) File.WriteAllBytes(path, bytes);
        return path.Replace("\\", "/");
    }

    private static string BrokenFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "fire-ai-broken.gguf");
        File.WriteAllBytes(path, TinyModel().Take(200).ToArray());
        return path.Replace("\\", "/");
    }

    private static string Vm(string source)
    {
        // (in the VM in C: docs/ROADMAP_C_PORT.md, M1)
        return CVmTest.Output(source, ended: true);
    }

    /// <summary>A quick run while working on the package: FIRE_TESTS_ONLY=ai prints what the VM prints and what the native build prints (FIRE_AI_FILE=script: only
    /// that script in the VM).</summary>
    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("FIRE_AI_FILE") is { Length: > 0 } file) { Console.WriteLine(Vm(File.ReadAllText(file))); return; }
        string vm = Vm(Script);
        Console.WriteLine("--- VM ---\n" + vm);
        string work = Path.Combine(Path.GetTempPath(), "fire-ai-" + Guid.NewGuid().ToString("N"));
        fire.Native.NativeRuntimeFiles.WriteTo(work);
        var generated = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { Script }, null, null, VmExecutionMode.Release));
        string build = NativeTestBuild.Build(NativeTestBuild.In(work), "g++", generated, work, "case", "-pthread -O2 -Wall -Wextra", Path.Combine(work, "case.bin"), out _);
        if (build.Length > 0) Console.WriteLine("--- build ---\n" + build);
        string native = "(no binary)";
        if (File.Exists(Path.Combine(work, "case.bin")))
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(work, "case.bin")) { RedirectStandardOutput = true, UseShellExecute = false, WorkingDirectory = work })!;
            native = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
        }
        Console.WriteLine("--- native ---\n" + native);
        Console.WriteLine(vm == native ? "SAME" : "DIFFERENT");
    }

    public static string Script => ScriptText.Replace("{MODEL}", ModelFile()).Replace("{BROKEN}", BrokenFile())
        + RecurrentText.Replace("{MODEL}", Write("fire-ai-tiny-qwen35.gguf", TinyQwen35())).Replace("{NAME}", "qwen35")
        + RecurrentText.Replace("{MODEL}", Write("fire-ai-tiny-lfm3.gguf", TinyLfm3())).Replace("{NAME}", "lfm3")
        + AgentText.Replace("{MODEL}", ModelFile())
        + RealModel();

    /// <summary>The package ai.agent on the tiny model: the tools in the system message, the calls the models write (XML and JSON), asking, the conversation, choosing.</summary>
    private const string AgentText = """

        var agent = new AI.Agent("{MODEL}", "Be brief.", 2048)
        agent.Settings = AI.Settings.Exact(12)
        agent.Tool("read_file", "Reads a file", ["path: the file"], (JsonObject args) => "content of " + args.Get("path"))
        agent.ToolWithSchema("add", "Adds two numbers", "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"number\"},\"b\":{\"type\":\"number\"}}}", (JsonObject args) => "" + (args.Get("a") + args.Get("b")))
        var sys = agent.SystemPrompt
        print("agent schema " + (sys.IndexOf("\"name\": \"add\"") >= 0) + " " + (sys.IndexOf("\"a\": {\"type\": \"number\"}") >= 0))
        print("agent tools " + agent.ToolCount + " " + (sys.IndexOf("<tools>") >= 0) + " " + (sys.IndexOf("\"name\": \"read_file\"") >= 0) + " " + (sys.IndexOf("\"required\": [\"path\"]") >= 0) + " " + sys.EndsWith("Be brief."))
        var x = AI.Agent.ParseCall("<function=read_file>\n<parameter=path>\nnotes.txt\n</parameter>\n</function>")
        print("xml call " + x.Name + " " + x.Arguments.Get("path"))
        var j = AI.Agent.ParseCall("{\"name\": \"add\", \"arguments\": {\"a\": 2, \"b\": 3}}")
        print("json call " + j.Name + " " + j.Arguments.Get("a") + " " + j.Arguments.Get("b"))
        print("no call " + (AI.Agent.ParseCall("just text") == undefined))
        var sloppy = AI.Agent.ParseCall("<function=open_file>\n<parameter=path>\nmain.fire\n\n<parameter=line>\n12\n</parameter>\n</function>")
        print("sloppy call " + sloppy.Name + " [" + sloppy.Arguments.Get("path") + "] [" + sloppy.Arguments.Get("line") + "]")
        var a1 = agent.Ask("hello")
        var a2 = agent.Ask("world")
        print("agent asked " + agent.HistoryCount + " " + agent.HistoryRole(0) + " " + agent.HistoryText(0) + " " + agent.HistoryRole(1) + " " + (agent.HistoryText(1) == a1) + " " + agent.Running)
        agent.Start("hello")
        var streamed = ""
        while (agent.Running) { streamed += agent.Update() }
        print("agent streamed " + agent.HistoryCount + " " + (streamed.Trim() == agent.HistoryText(5)))
        var pick = agent.Choose("hello?", ["world", "or", "ld"])
        print("agent choice " + (pick >= 0 && pick < 3) + " " + agent.HistoryCount)
        agent.Forget()
        print("agent forgot " + agent.HistoryCount)
        agent.RemoveTool("add")
        print("agent removed " + agent.ToolCount)
        agent.Close()
        """;

    /// <summary>The models with recurrent layers: generating, the cache (the checkpoint after a prompt), the choice (each option from the checkpoint) - against a fresh
    /// session, which has no checkpoint; a prompt longer than the window of 4.</summary>
    private const string RecurrentText = """

        var m{NAME} = new AI.Model("{MODEL}")
        var s{NAME} = new AI.Session(m{NAME}, 256, 2)
        var p{NAME} = AI.Chat.Question("Be brief.", "hello world, hello world", "", m{NAME})
        var a{NAME} = s{NAME}.Complete(p{NAME}, AI.Settings.Exact(20))
        var b{NAME} = s{NAME}.Complete(p{NAME}, AI.Settings.Exact(20))
        print("{NAME} " + m{NAME}.Architecture + " " + m{NAME}.Layers + " " + s{NAME}.Generated + " " + H.Sum(a{NAME}) + " " + (a{NAME} == b{NAME}) + " " + m{NAME}.CountTokens(p{NAME}))
        var c{NAME} = s{NAME}.Choose(p{NAME}, ["world", "or", "hello"])
        var ok{NAME} = true
        for (var i = 0; i < 3; i++) {
            var fresh = new AI.Session(m{NAME}, 256, 2)
            var one = ["world", "or", "hello"]
            fresh.Choose(p{NAME}, [one[i]])
            if (fresh.Score(0) != s{NAME}.Score(i)) { ok{NAME} = false }
            fresh.Close()
        }
        // after the choice the answer is the same again (back to the checkpoint)
        var d{NAME} = s{NAME}.Complete(p{NAME}, AI.Settings.Exact(20))
        print("{NAME} choice " + c{NAME} + " " + ok{NAME} + " " + (d{NAME} == a{NAME}))
        // a chat that goes on: the next prompt starts with the answer before
        var chat{NAME} = new AI.Chat()
        chat{NAME}.For(m{NAME}).User("hello")
        var r1{NAME} = s{NAME}.Complete(chat{NAME}.Prompt(), AI.Settings.Exact(8))
        chat{NAME}.Assistant(r1{NAME}).User("world")
        var r2{NAME} = s{NAME}.Complete(chat{NAME}.Prompt(), AI.Settings.Exact(8))
        var fresh2{NAME} = new AI.Session(m{NAME}, 256, 2)
        print("{NAME} chat " + (fresh2{NAME}.Complete(chat{NAME}.Prompt(), AI.Settings.Exact(8)) == r2{NAME}))
        fresh2{NAME}.Close()
        // the kept start (Session.Keep): another question after the same instructions does not compute them again, and answers as a fresh session does
        var sys{NAME} = "<|im_start|>system\nBe brief.<|im_end|>\n"
        s{NAME}.Keep(sys{NAME})
        s{NAME}.Complete(AI.Chat.Question("Be brief.", "hello", "", m{NAME}), AI.Settings.Exact(4))
        var q{NAME} = AI.Chat.Question("Be brief.", "world, world", "", m{NAME})
        var k{NAME} = s{NAME}.Complete(q{NAME}, AI.Settings.Exact(6))
        var reused{NAME} = s{NAME}.PromptReused
        var fresh3{NAME} = new AI.Session(m{NAME}, 256, 2)
        print("{NAME} kept " + (reused{NAME} >= m{NAME}.CountTokens(sys{NAME})) + " " + (reused{NAME} < s{NAME}.PromptTokens) + " " + (fresh3{NAME}.Complete(q{NAME}, AI.Settings.Exact(6)) == k{NAME}) + " " + s{NAME}.EndReason + " " + (s{NAME}.PromptDone == s{NAME}.PromptTokens))
        fresh3{NAME}.Close()
        s{NAME}.Close()
        m{NAME}.Close()
        """;

    /// <summary>With FIRE_TEST_AI_MODEL (an instruct model: Qwen3.5-0.8B-Q4_0.gguf, LFM3-1.7B-QAD-Q4_0.gguf, qwen2.5-0.5b-instruct-q4_0.gguf): it answers a simple question
    /// and picks the right option.</summary>
    private static string RealModel()
    {
        if (Environment.GetEnvironmentVariable("FIRE_TEST_AI_MODEL") is not { Length: > 0 } path) return "";
        return """

            var real = new AI.Model("{PATH}")
            var rs = new AI.Session(real, 1024)
            var answer = rs.Ask("You are a helpful assistant.", "What is the capital of France? Answer in one sentence.", AI.Settings.Exact(40))
            print("real " + (answer.IndexOf("Paris") >= 0) + " " + real.Architecture)
            print("real choice " + rs.Choose(AI.Chat.Question("You answer questions correctly and briefly.", "Which of these is a fruit?\n\nOptions:\n- Hammer\n- Banana\n- Car\n\nAnswer with one of the options.", "Answer:", real), [" Hammer", " Banana", " Car"], true))
            rs.Close()
            real.Close()
            """.Replace("{PATH}", path.Replace("\\", "/"));
    }

    private const string ScriptText = """
        #import "ai"
        #import "ai.agent"
        #import "json"

        // (a checksum of a text: the answers of random weights are no words)
        class H {
            static int Sum(string s) {
                var h = 0
                for (var i = 0; i < s.Length; i++) { h = (h * 31 + s[i].ToInt()) % 1000003 }
                return h
            }
        }

        var model = new AI.Model("{MODEL}")
        print("model " + model.Name + " " + model.Architecture + " " + model.Layers + " " + model.VocabularySize + " " + model.ContextLength + " " + model.Parameters)
        print("template " + (model.ChatTemplate.IndexOf("im_start") >= 0))
        print("tokens " + model.CountTokens("hello world") + " " + model.CountTokens("<|im_start|>hello") + " " + model.CountTokens("Grüße, 2026!"))

        var session = new AI.Session(model, 256, 2)
        var exact = AI.Settings.Exact(24)
        var a = session.Complete(AI.Chat.Question("Be brief.", "hello world"), exact)
        print("generated " + session.Generated + " " + H.Sum(a) + " " + session.PromptTokens + " " + session.State)
        // the same prompt again: the same answer (the cache holds the prompt)
        var b = session.Complete(AI.Chat.Question("Be brief.", "hello world"), exact)
        print("again " + (a == b) + " " + session.Generated)
        // streaming: Start, then Take until it is done
        session.Start(AI.Chat.Question("Be brief.", "hello"), exact)
        var parts = ""
        while (session.Busy) { parts += session.Take() }
        parts += session.Take()
        print("streamed " + session.State + " " + session.Generated + " " + (parts.Length > 0))

        // sampling with a seed: the same seed gives the same text
        var s = new AI.Settings()
        s.Seed = 42
        s.MaxTokens = 16
        var x1 = session.Complete("hello", s)
        var x2 = session.Complete("hello", s)
        print("seeded " + (x1 == x2) + " " + H.Sum(x1))

        // a stop text ends the answer before it
        var full = session.Complete("hello", AI.Settings.Exact(30))
        var stop = AI.Settings.Exact(30)
        // (an ASCII part of it as the stop text: random weights also give bytes that are no UTF-8, which become U+FFFD in a string)
        var at = 1
        while (at + 2 < full.Length && (full[at].ToInt() > 126 || full[at].ToInt() < 33 || full[at + 1].ToInt() > 126 || full[at + 1].ToInt() < 33)) { at++ }
        var cut = full.Substring(at, 2)
        stop.Stops.Add(cut)
        var stopped = session.Complete("hello", stop)
        print("stop " + (stopped.Length < full.Length) + " " + (stopped.IndexOf(cut) < 0) + " " + full.StartsWith(stopped))

        // the choice: the scores are log-probabilities, the choice the highest
        var c = session.Choose("hello", ["world", "or", "ld"])
        var best = 0
        for (var i = 1; i < 3; i++) { if (session.Score(i) > session.Score(best)) { best = i } }
        print("choice " + c + " " + (c == best) + " " + (session.Score(0) < 0.0))
        var cp = session.Choose("hello", ["world", "or", "ld"], true)
        print("pertoken " + (cp >= 0 && cp < 3))

        // cancelling a long answer
        session.Start("hello", AI.Settings.Exact(200))
        session.Cancel()
        print("cancelled " + (session.State == AI.State.Cancelled || session.State == AI.State.Done))

        // errors: a prompt longer than the context, a file that is not a model, a broken one
        var long = ""
        for (var i = 0; i < 300; i++) { long += "<|im_start|>" }
        try { session.Complete(long, exact) } catch (AI.AIException e) { print("toolong " + (e.message.IndexOf("context") >= 0)) }
        try { new AI.Model("/nonexistent/model.gguf") } catch (AI.AIException e) { print("missing " + (e.message.IndexOf("cannot be opened") >= 0)) }
        try { new AI.Model("{BROKEN}") } catch (AI.AIException e) { print("broken " + e.message.Length) }
        session.Close()
        model.Close()
        print("closed " + __AiOpenCount())
        """;
}
