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
        w.U32("qwen2.context_length", 256);
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
        var lines = new List<string>();
        var session = RuntimeSession.Build(new[] { source }, VmExecutionMode.Release, args => { lines.Add(args[0].ToString()!); return Value.MakeUndefined(); });
        session.Run();
        Value.SingleFloats = false; Value.Ints32 = false; Value.Ints16 = false;
        return string.Join("\n", lines) + (lines.Count > 0 ? "\n" : "");
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

    public static string Script => ScriptText.Replace("{MODEL}", ModelFile()).Replace("{BROKEN}", BrokenFile()) + RealModel();

    /// <summary>With FIRE_TEST_AI_MODEL (a Qwen 2.5 instruct model, e.g. qwen2.5-0.5b-instruct-q4_0.gguf): it answers a simple question and picks the right option.</summary>
    private static string RealModel()
    {
        if (Environment.GetEnvironmentVariable("FIRE_TEST_AI_MODEL") is not { Length: > 0 } path) return "";
        return """

            var real = new AI.Model("{PATH}")
            var rs = new AI.Session(real, 1024)
            var answer = rs.Ask("You are a helpful assistant.", "What is the capital of France? Answer in one sentence.", AI.Settings.Exact(40))
            print("real " + (answer.IndexOf("Paris") >= 0) + " " + real.Architecture)
            print("real choice " + rs.Choose(AI.Chat.Question("You are a helpful assistant.", "Which of these is a fruit? Answer with one word."), ["Hammer", "Banana", "Car"]))
            rs.Close()
            real.Close()
            """.Replace("{PATH}", path.Replace("\\", "/"));
    }

    private const string ScriptText = """
        #import "ai"

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
