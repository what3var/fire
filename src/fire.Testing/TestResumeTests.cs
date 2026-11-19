/// <summary>FIRE_TESTS_ONLY=resume: the parts of FIRE_TESTS_RESUME / FIRE_TESTS_SHARD that need no build (TestResume.cs) - the key, the shards, the records
/// and how they are read back, the folder; the cleanup of a build (NativeTestBuild.Clean) and the free space (TestDisk). A second; FIRE_TESTS_FILTER narrows.</summary>
static class TestResumeTests
{
    public static int Run()
    {
        Console.WriteLine("=== Fortsetzen und Teilen (FIRE_TESTS_RESUME, FIRE_TESTS_SHARD) ===");
        int failures = 0;
        void Check(string title, bool ok, string detail = "")
        {
            title = "Fortsetzen: " + title;
            if (!Selected(title)) return;
            if (!ok) failures++;
            Console.WriteLine(ok ? $"OK: {title}" : $"FEHLER: {title}\n  {detail}");
        }

        // ---- the key
        string g = TestResume.Hash("build");
        Check("Schluessel: gleiche Teile geben den gleichen Schluessel", TestResume.KeyWith(g, "src", "out") == TestResume.KeyWith(g, "src", "out"));
        Check("Schluessel: ein anderer Teil (die .expected) gibt einen anderen", TestResume.KeyWith(g, "src", "out") != TestResume.KeyWith(g, "src", "out2"));
        Check("Schluessel: ein anderer Build gibt einen anderen", TestResume.KeyWith(g, "src", "out") != TestResume.KeyWith(TestResume.Hash("build2"), "src", "out"));
        Check("Schluessel: die Grenzen der Teile zaehlen (\"ab\"+\"c\" ist nicht \"a\"+\"bc\")", TestResume.KeyWith(g, "ab", "c") != TestResume.KeyWith(g, "a", "bc"));
        Check("Schluessel: null ist nicht der leere Text, die Zahl der Teile zaehlt",
            TestResume.KeyWith(g, null, "x") != TestResume.KeyWith(g, "", "x") && TestResume.KeyWith(g, "x") != TestResume.KeyWith(g, "x", null));
        Check("Schluessel: der Build-Schluessel ist berechnet und bleibt im Lauf gleich", TestResume.Global.Length == 32 && TestResume.Global == TestResume.Global, TestResume.Global);
        {
            string dir = Path.Combine(Path.GetTempPath(), "fire-resume-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "a.txt"), "eins");
            File.WriteAllText(Path.Combine(dir, "sub", "b.txt"), "zwei");
            string h1 = TestResume.HashTree(dir, _ => true);
            File.WriteAllText(Path.Combine(dir, "sub", "b.txt"), "drei");
            string h2 = TestResume.HashTree(dir, _ => true);
            File.Move(Path.Combine(dir, "sub", "b.txt"), Path.Combine(dir, "sub", "c.txt"));
            string h3 = TestResume.HashTree(dir, _ => true);
            string h4 = TestResume.HashTree(dir, f => !f.EndsWith("c.txt"));
            File.Delete(Path.Combine(dir, "sub", "c.txt"));
            string h5 = TestResume.HashTree(dir, _ => true);
            Directory.Delete(dir, true);
            Check("Schluessel: der Hash eines Ordners aendert sich mit dem Inhalt und dem Namen einer Datei, nicht mit einer weggelassenen",
                h1 != h2 && h2 != h3 && h3 != h4 && h4 == h5 && TestResume.HashTree(dir, _ => true) == "-", $"{h1} {h2} {h3} {h4} {h5}");
        }

        // ---- shards
        Check("Teilen: FIRE_TESTS_SHARD wird gelesen (i/n mit 1 <= i <= n, sonst alles)",
            TestResume.ParseShard("1/2") == (1, 2) && TestResume.ParseShard(" 3/3 ") == (3, 3) && TestResume.ParseShard("3/2") == (1, 1) && TestResume.ParseShard("0/2") == (1, 1)
            && TestResume.ParseShard("x") == (1, 1) && TestResume.ParseShard(null) == (1, 1) && TestResume.ParseShard("2") == (1, 1));
        {
            var names = Enumerable.Range(0, 1000).Select(i => $"Native == VM: Fall {i}").ToList();
            var counts = Enumerable.Range(1, 4).Select(k => names.Count(n => TestResume.ShardOf(n, 4) == k)).ToArray();
            bool stable = names.All(n => TestResume.ShardOf(n, 4) == TestResume.ShardOf(n, 4)) && names.All(n => TestResume.ShardOf(n, 1) == 1);
            Check("Teilen: jeder Titel gehoert zu genau einem Teil, immer demselben, und die Teile sind etwa gleich gross", stable && counts.Sum() == 1000 && counts.All(c => c > 180), string.Join(" ", counts));
            // (a hash that does not change between processes: string.GetHashCode would)
            Check("Teilen: der Teil eines Titels haengt nicht vom Prozess ab", TestResume.ShardOf("abc", 1000) == (int)(BitConverter.ToUInt64(System.Security.Cryptography.SHA256.HashData("abc"u8.ToArray()), 0) % 1000) + 1);
        }

        // ---- records
        static string R(string run, long t, string unit, string key, bool ok, string title) => TestResume.Format(run, t, unit, key, ok, title);
        static string D(string run, long t, string unit, string key, bool ok, int n) => TestResume.Format(run, t, unit, key, ok, doneCount: n);
        {
            var lines = new List<string>
            {
                R("r1", 1, "A", "k", true, "VM: A: die erwartete Ausgabe"), R("r1", 2, "A", "k", true, "Native == VM: A"), D("r1", 3, "A", "k", true, 2),
                R("r1", 4, "B", "k", false, "Native == VM: B"), D("r1", 5, "B", "k", false, 1),
                R("r1", 6, "C", "k", true, "Native == VM: C"),                                   // (killed before C finished)
                R("r1", 7, "D", "k", true, "D1"), D("r1", 8, "D", "k", true, 2),                   // (fewer checks than the unit says)
                R("r1", 9, "", "", true, "outside"),
                "{\"run\":\"r1\",\"t\":10,\"unit\":\"E\",\"key\":\"k\",\"ok\":tr",                 // (the line a killed run did not finish)
                "kein JSON", "",
            };
            var resumable = TestResume.Parse(lines);
            Check("Aufzeichnung: eine Einheit mit nur OK und Ende wird fortgesetzt, mit ihren Pruefungen in ihrer Reihenfolge",
                resumable.TryGetValue(("A", "k"), out var a) && a.Select(c => c.Title).SequenceEqual(new[] { "VM: A: die erwartete Ausgabe", "Native == VM: A" }), string.Join(", ", resumable.Keys));
            Check("Aufzeichnung: eine fehlgeschlagene, eine unfertige, eine mit fehlenden Pruefungen und eine angeschnittene Zeile laufen wieder",
                !resumable.ContainsKey(("B", "k")) && !resumable.ContainsKey(("C", "k")) && !resumable.ContainsKey(("D", "k")) && !resumable.ContainsKey(("E", "k")) && resumable.Count == 1,
                string.Join(", ", resumable.Keys));
            Check("Aufzeichnung: ein anderer Schluessel (eine geaenderte .expected) findet nichts", !resumable.ContainsKey(("A", "k2")));
            Check("Aufzeichnung: Pruefungen ausserhalb von Einheiten werden nie fortgesetzt", !resumable.ContainsKey(("", "")));
        }
        {
            // the last finished run decides (also across files: the order of the lines does not matter, the time does)
            var lines = new List<string>
            {
                R("r2", 20, "A", "k", false, "Native == VM: A"), D("r2", 21, "A", "k", false, 1),
                R("r1", 10, "A", "k", true, "Native == VM: A"), D("r1", 11, "A", "k", true, 1),
                R("r1", 12, "B", "k", false, "Native == VM: B"), D("r1", 13, "B", "k", false, 1),
                R("r3", 30, "B", "k", true, "Native == VM: B"), D("r3", 31, "B", "k", true, 1),
                R("r4", 40, "B", "k", true, "Native == VM: B (unfertig)"),
            };
            var resumable = TestResume.Parse(lines);
            Check("Aufzeichnung: der letzte beendete Lauf entscheidet (ein spaeterer Fehler laesst wieder laufen, ein spaeteres OK setzt fort; ein unfertiger zaehlt nicht)",
                !resumable.ContainsKey(("A", "k")) && resumable.TryGetValue(("B", "k"), out var b) && b.Single().Title == "Native == VM: B", string.Join(", ", resumable.Keys));
        }
        {
            string line = TestResume.Format("r", 5, "Fenster: \"x\"\nzwei", "k", false, "Titel mit ä und \"", "Zeile 1\nZeile 2");
            bool oneLine = !line.Contains('\n');
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var root = doc.RootElement;
            Check("Aufzeichnung: ein Satz ist eine Zeile JSON, auch mit Zeilenumbruch und Anfuehrungszeichen im Titel und in der Meldung",
                oneLine && root.GetProperty("unit").GetString() == "Fenster: \"x\"\nzwei" && root.GetProperty("title").GetString() == "Titel mit ä und \"" && root.GetProperty("detail").GetString() == "Zeile 1\nZeile 2"
                && !root.GetProperty("ok").GetBoolean(), line);
        }
        Check("Ordner: FIRE_TESTS_RESUME=1 ist ~/.cache/fire-tests/resume, 0 und leer schalten aus, sonst der Ordner",
            TestResume.FolderFrom("1")!.EndsWith(Path.Combine(".cache", "fire-tests", "resume")) && TestResume.FolderFrom("0") == null && TestResume.FolderFrom("") == null && TestResume.FolderFrom(null) == null
            && TestResume.FolderFrom("/tmp/x") == "/tmp/x");

        // ---- the cleanup of a build and the free space
        {
            string dir = Path.Combine(Path.GetTempPath(), "fire-resume-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string[] mine = { "case7.bin", "case7.c", "case7.o", "case7_natives.o", "case7_natives.cpp", "case7_fire_unit1.o" };
            string[] others = { "case70.bin", "case70.o", "fire_rt.h", "case8.bin" };
            foreach (var f in mine.Concat(others)) File.WriteAllText(Path.Combine(dir, f), "x");
            Directory.CreateDirectory(Path.Combine(dir, "case7.split"));
            NativeTestBuild.Clean(dir, "case7", Path.Combine(dir, "case7.bin"));
            var left = Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList();
            Directory.Delete(dir, true);
            var want = NativeTestBuild.Keep ? mine.Concat(others).Append("case7.split").OrderBy(f => f, StringComparer.Ordinal).ToList() : others.OrderBy(f => f, StringComparer.Ordinal).ToList();
            Check("Aufraeumen: Clean loescht Programm, Objekte und Quellen eines Builds - nicht die eines anderen (case70) und nichts mit FIRE_TESTS_KEEP=1",
                left.SequenceEqual(want), string.Join(" ", left));
        }
        Check("Platz: der freie Platz des temporaeren Ordners ist bekannt", TestDisk.Free(Path.GetTempPath()) > 0, TestDisk.Free(Path.GetTempPath()).ToString());

        Console.WriteLine(failures == 0 ? "Alle Fortsetzen-Pruefungen bestanden." : $"FEHLER: {failures} Fortsetzen-Pruefung(en) fehlgeschlagen.");
        return failures;
    }

    static bool Selected(string name)
    {
        string? filter = Environment.GetEnvironmentVariable("FIRE_TESTS_FILTER");
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return filter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));
    }
}
