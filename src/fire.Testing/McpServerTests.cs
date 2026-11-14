using System.Text.Json;
using System.Text.Json.Nodes;
using fire.Compiler;
using fire.Projects.Mcp;
using fire.Runtime;
using fire.Values;

/// <summary>The MCP server of C# (fire.Project/Mcp/McpServer.cs - the one of spark, docs/tools/spark-mcp.md) with the client of the fire package `mcp` in the VM: Streamable
/// HTTP, the token, sessions, tools, resources, a subscription and the stream of notifications (GET), errors.</summary>
static class McpServerTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: MCP-Server: {title}" : $"FEHLER: MCP-Server: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    private static string Vm(string source)
    {
        var lines = new List<string>();
        var session = RuntimeSession.Build(new[] { source }, VmExecutionMode.Release, args => { lines.Add(args[0].ToString()!); return Value.MakeUndefined(); });
        session.Run();
        Value.SingleFloats = false; Value.Ints32 = false; Value.Ints16 = false;
        return string.Join("\n", lines);
    }

    public static void Run()
    {
        Console.WriteLine("=== MCP server (C#) with the fire client ===");
        _failures = 0;
        using var server = new McpServer("fire-test-mcp", "0.1") { Instructions = "A test server." };
        server.AddTool("add", "Adds two numbers", "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"integer\"},\"b\":{\"type\":\"integer\"}},\"required\":[\"a\",\"b\"]}",
            args => Task.FromResult(McpToolResult.Ok((args.GetProperty("a").GetInt32() + args.GetProperty("b").GetInt32()).ToString())));
        server.AddTool("fail", "Fails", "", _ => Task.FromResult(McpToolResult.Error("it failed")));
        server.AddTool("boom", "Throws", "", _ => throw new InvalidOperationException("boom!"));
        string state = "one";
        server.AddResource("test://state", "state", "A state", "text/plain", () => Task.FromResult(state));
        server.Start();
        Check("startet und schreibt die Verbindungsdatei (URL, Token)", server.IsRunning && File.Exists(server.ConnectionFile) && File.ReadAllText(server.ConnectionFile).Contains(server.Token));

        // the client of the fire package in the VM: the token from the connection file; a notification while it listens
        var notifier = Task.Run(async () =>
        {
            for (int i = 0; i < 100 && !server.Clients.Contains("vm-client"); i++) await Task.Delay(50);
            await Task.Delay(500);
            state = "two";
            server.ResourceUpdated("test://state");
            server.Log("info", JsonValue.Create("hello")!, "test");
        });
        string script = """
            #import "mcp"
            #import "json"
            #import "time"
            var conn = Json.Parse(IO.File.ReadAllText("{FILE}"))
            var t = new Mcp.HttpTransport(conn.Get("url"))
            t.Headers.Set("Authorization", "Bearer " + conn.Get("token"))
            var client = new Mcp.Client(t)
            client.Initialize("vm-client", "1.0")
            print("server " + client.ServerName + " " + client.ProtocolVersion + " " + client.Instructions)
            foreach (x in client.Tools()) { Mcp.Tool tool = x; print("tool " + tool.Name) }
            print("add " + client.CallTool("add", "{\"a\":40,\"b\":2}").Text)
            var f = client.CallTool("fail")
            print("fail " + f.IsError + " " + f.Text)
            var b = client.CallTool("boom")
            print("boom " + b.IsError + " " + b.Text)
            try { client.CallTool("nope") } catch (Mcp.McpException e) { print("nope " + e.code) }
            print("read " + client.ReadResource("test://state"))
            try { client.ReadResource("test://none") } catch (Mcp.McpException e) { print("noread " + e.code) }
            client.Subscribe("test://state")
            print("listen " + t.Listen())
            var got = ""
            for (var i = 0; i < 400 && got.IndexOf("message") < 0; i++) {
                foreach (n in client.Notifications()) { Mcp.Notification note = n; got += note.Method + " " }
                Sleep(10ms)
            }
            print("notes " + got.Trim())
            print("read2 " + client.ReadResource("test://state"))
            client.Close()
            // without the token: refused
            var bad = new Mcp.Client(new Mcp.HttpTransport(conn.Get("url")))
            try { bad.Initialize("bad") } catch (Mcp.McpException e) { print("no token " + (e.message.IndexOf("401") >= 0)) }
            """.Replace("{FILE}", server.ConnectionFile.Replace("\\", "/"));
        string output;
        try { output = Vm(script); }
        catch (Exception e) { output = "EXCEPTION " + e.Message; }
        notifier.Wait(10000);
        string expected = string.Join("\n",
            "server fire-test-mcp 2025-11-25 A test server.",
            "tool add", "tool fail", "tool boom",
            "add 42", "fail True it failed", "boom True boom!", "nope -32602",
            "read one", "noread -32002",
            "listen True",
            "notes notifications/resources/updated notifications/message",
            "read2 two",
            "no token True");
        Check("der fire-Client: Initialize, Tools, Fehler, Ressourcen, Abo und der Strom der Benachrichtigungen, ohne Token 401", output == expected,
            $"--- erwartet ---\n{expected}\n--- bekommen ---\n{output}");
        Check("der Client ist nach DELETE weg", WaitFor(() => !server.Clients.Contains("vm-client")));

        // the protocol itself
        var (answer, _) = server.HandleAsync("not json", null).Result;
        Check("kein JSON: -32700", answer != null && answer.Contains("-32700"));
        (answer, _) = server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"nothing\"}", null).Result;
        Check("unbekannte Methode: -32601 mit der id", answer != null && answer.Contains("-32601") && answer.Contains("\"id\":7"));
        (answer, var session) = server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":\"a\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\"}}", null).Result;
        Check("eine aeltere Version wird angenommen, eine Sitzung entsteht", answer != null && answer.Contains("\"protocolVersion\":\"2025-03-26\"") && session != null);
        (answer, _) = server.HandleAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", session).Result;
        Check("eine Benachrichtigung hat keine Antwort", answer == null);

        // the documentation and the samples (the folders of the repository)
        string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (string? d = start; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                if (Directory.Exists(Path.Combine(d, "docs", "language")) && Directory.Exists(Path.Combine(d, "src", "fire.Project", "Samples"))) { repo = d; goto found; }
        found:
        KnowledgeTools.Register(server, Path.Combine(repo, "docs"), Path.Combine(repo, "src", "fire.Project", "Samples"));
        string Call(string tool, string args)
        {
            var (a, _) = server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + args + "}}", session).Result;
            var node = JsonNode.Parse(a!)!;
            return (node["result"]?["isError"]?.GetValue<bool>() == true ? "ERROR " : "") + (node["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? a!);
        }
        string found = Call("search_docs", "{\"query\":\"ReadAllLines file\"}");
        Check("search_docs: ReadAllLines findet die Seite der Dateien, drei Treffer", found.Contains("stdlib/filesystem.md") && found.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 3, found);
        string toc = Call("read_doc", "{\"path\":\"stdlib/filesystem.md\"}");
        Check("read_doc ohne Ueberschrift: das Inhaltsverzeichnis", toc.Contains("`IO.File`") && toc.Length < 2000, toc);
        string section = Call("read_doc", "{\"path\":\"stdlib/filesystem.md\",\"heading\":\"IO.File\"}");
        Check("read_doc mit Ueberschrift: der Abschnitt (begrenzt)", section.Contains("ReadAllLines") && section.Length <= 4100, section.Substring(0, Math.Min(300, section.Length)));
        Check("read_doc: eine fehlende Seite nennt aehnliche", Call("read_doc", "{\"path\":\"filesystem.md\"}").StartsWith("ERROR") );
        string samples = Call("list_samples", "{}");
        Check("list_samples nennt HelloWorld", samples.Contains("HelloWorld:"), samples);
        string files = Call("list_sample_files", "{\"name\":\"HelloWorld\"}");
        Check("list_sample_files: main.fire, ohne Projekt- und sample.json", files.Contains("main.fire") && !files.Contains("sample.json"), files);
        Check("read_sample_file: die Hauptdatei", Call("read_sample_file", "{\"name\":\"HelloWorld\"}").Contains("print("));
        Check("read_sample_file: kein Weg aus dem Ordner", Call("read_sample_file", "{\"name\":\"HelloWorld\",\"file\":\"../../../../etc/passwd\"}").StartsWith("ERROR"));

        server.Stop();
        Check("Stop entfernt die Verbindungsdatei", !File.Exists(server.ConnectionFile) && !server.IsRunning);
        Console.WriteLine(_failures == 0 ? "Alle MCP-Server-Pruefungen bestanden." : $"FEHLER: {_failures} MCP-Server-Pruefung(en) fehlgeschlagen.");
    }

    private static bool WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++) { if (condition()) return true; Thread.Sleep(50); }
        return condition();
    }
}
