using System.Net;
using System.Security.Cryptography;
using fire.Tooling;

/// <summary>The package ai.models (docs/ai/models.md) without the internet: the catalog, the folder (FIRE_MODELS), the choice, and the installer against a small
/// HTTP server of C# as the mirror - a download that goes on from a ".part" file (Range), the SHA-256 check, a wrong file that is removed.</summary>
static class AiModelsTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: ai.models: {title}" : $"FEHLER: ai.models: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    private static string Vm(string source)
    {
        // (in the VM in C: docs/ROADMAP_C_PORT.md, M1)
        return CVmTest.Output(source, ended: false);
    }

    public static void Run()
    {
        Console.WriteLine("=== ai.models: catalog, choice, installer ===");
        _failures = 0;
        string folder = Path.Combine(Path.GetTempPath(), "fire-models-test-" + Environment.ProcessId);
        Directory.CreateDirectory(folder);
        var data = new byte[3_000_000];
        new Random(7).NextBytes(data);
        string sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        int ranges = 0;

        using var listener = new HttpListener();
        int port = 0;
        for (int p = 47311; p < 47400; p++)
        {
            try { listener.Prefixes.Clear(); listener.Prefixes.Add($"http://127.0.0.1:{p}/"); listener.Start(); port = p; break; }
            catch (HttpListenerException) { }
        }
        var serving = Task.Run(() =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch { break; }
                try
                {
                    // /<repo>/resolve/main/<file>: "good.bin" the bytes, "bad.bin" other bytes of the same size
                    byte[] body = ctx.Request.Url!.AbsolutePath.EndsWith("/good.bin") ? data : ctx.Request.Url.AbsolutePath.EndsWith("/bad.bin") ? data.Reverse().ToArray() : Array.Empty<byte>();
                    if (body.Length == 0) { ctx.Response.StatusCode = 404; ctx.Response.Close(); continue; }
                    int from = 0;
                    string? range = ctx.Request.Headers["Range"];
                    if (range != null && range.StartsWith("bytes="))
                    {
                        from = int.Parse(range.Substring(6).TrimEnd('-').Split('-')[0]);
                        ranges++;
                        ctx.Response.StatusCode = 206;
                        ctx.Response.Headers["Content-Range"] = $"bytes {from}-{body.Length - 1}/{body.Length}";
                    }
                    ctx.Response.ContentLength64 = body.Length - from;
                    ctx.Response.OutputStream.Write(body, from, body.Length - from);
                    ctx.Response.Close();
                }
                catch { }
            }
        });
        // half of the good file is there already
        File.WriteAllBytes(Path.Combine(folder, "good.bin.part"), data.AsSpan(0, 1_200_000).ToArray());

        string script = """
            #import "ai.models"
            AI.Models.Mirror = "http://127.0.0.1:{PORT}"
            print("folder " + AI.Models.Folder())
            print("catalog " + AI.Models.Catalog().Count + " first " + AI.Models.Recommended().Id)
            AI.ModelInfo q = AI.Models.Find("Qwen3.5-0.8B-Q4_0.gguf")
            print("find " + q.Id + " " + q.SizeText() + " " + q.Url().StartsWith("http://127.0.0.1:{PORT}/unsloth/Qwen3.5-0.8B-GGUF/resolve/main/"))
            print("none " + (AI.Models.Find("nothing") == undefined) + " pick '" + AI.Models.Pick() + "'")
            var good = new AI.ModelInfo("good", "Good", "", "test/repo", "good.bin", 3000000, "{SHA}", "MIT", "", 10, 10)
            var setup = new AI.ModelInstaller(good)
            int steps = 0
            int checking = 0
            while (setup.Step()) {
                steps++
                if (setup.State == AI.InstallState.Checking) { checking++ }
            }
            print("good " + setup.Ok() + " '" + setup.Error + "' " + setup.Percent() + "% installed " + good.Installed() + " steps " + (steps > 1) + " checked " + (checking > 0))
            var bad = new AI.ModelInfo("bad", "Bad", "", "test/repo", "bad.bin", 3000000, "{SHA}", "MIT", "", 10, 10)
            var wrong = new AI.ModelInstaller(bad)
            print("bad " + wrong.Run() + " " + (wrong.Error.IndexOf("SHA-256") >= 0) + " removed " + !IO.File.Exists(bad.Path()))
            var missing = new AI.ModelInstaller(new AI.ModelInfo("gone", "Gone", "", "test/repo", "gone.bin", 5, "00", "MIT", "", 10, 10))
            print("missing " + missing.Run() + " " + (missing.Error != ""))
            print("pick " + IO.Path.GetFileName(AI.Models.Pick(good.Path())))
            """.Replace("{PORT}", port.ToString()).Replace("{SHA}", sha);
        string? old = Environment.GetEnvironmentVariable("FIRE_MODELS");
        Environment.SetEnvironmentVariable("FIRE_MODELS", folder);
        string output;
        try { output = Vm(script); }
        catch (Exception e) { output = "EXCEPTION " + e.Message; }
        finally { Environment.SetEnvironmentVariable("FIRE_MODELS", old); }
        listener.Stop();
        string expected = string.Join("\n",
            "folder " + folder,
            "catalog 4 first " + "qwen3.5-0.8b",
            "find qwen3.5-0.8b 507 MB True",
            "none True pick ''",
            "good True '' 100% installed True steps True checked True",
            "bad False True removed True",
            "missing False True",
            "pick good.bin");
        // (a machine with less than 3 GB memory gets the smallest model)
        if (output.Contains("first qwen2.5-0.5b")) expected = expected.Replace("first qwen3.5-0.8b", "first qwen2.5-0.5b");
        Check("Katalog, Ordner, Auswahl; der Installer setzt fort, prueft die SHA-256 und entfernt eine falsche Datei", output == expected,
            $"--- erwartet ---\n{expected}\n--- bekommen ---\n{output}");
        Check("der Download ging mit Range weiter", ranges >= 1);
        Check("die Datei ist da, die .part-Datei nicht mehr", File.Exists(Path.Combine(folder, "good.bin")) && !File.Exists(Path.Combine(folder, "good.bin.part")));
        try { Directory.Delete(folder, true); } catch { }
        Console.WriteLine(_failures == 0 ? "Alle ai.models-Pruefungen bestanden." : $"FEHLER: {_failures} ai.models-Pruefung(en) fehlgeschlagen.");
    }
}
