using fire.Projects;

/// <summary>Notes of projects and solutions (docs/NOTES.md): the folder `notes/`, todo lists - reading them and writing back only what changed.</summary>
static class NotesTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Notes: {title}" : $"FEHLER: Notes: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    private static void Same(string title, string actual, string expected) =>
        Check(title, actual == expected, $"--- erwartet ---\n{expected.Replace("\r", "\\r")}--- bekommen ---\n{actual.Replace("\r", "\\r")}---");

    public static void Run()
    {
        Console.WriteLine("=== Notes: todo lists ===");
        _failures = 0;
        Reading();
        States();
        Texts();
        Removing();
        Adding();
        LineEndings();
        Folders();
        Preview();
        Overview();
        OverviewEdits();
        Console.WriteLine(_failures == 0 ? "Notes: alle Tests bestanden" : $"Notes: {_failures} FEHLER");
    }

    private const string Sample = """
        # Todo

        Notes for the whole list.

        - [ ] loose task

        ## Circuits
        - [ ] Show the probe values
        - [/] Falstad import
          - [x] Parser
          - [ ] Dialog

          A note below the Falstad task.
        - [#] Tube models
        - [X] RC sample
        - a list item without a mark

        ```
        - [ ] not a task: in a code block
        ```

        ## Docs
        1. [ ] CIRCUITS.md in English
        2) [x] Second

        """;

    private static void Reading()
    {
        var list = TodoList.Parse(Sample);
        Check("Titel", list.Title == "Todo", list.Title);
        Check("Gruppen in Reihenfolge", list.Groups.SequenceEqual(new[] { "Circuits", "Docs" }), string.Join(",", list.Groups));
        Check("Anzahl Aufgaben (nicht im Codeblock, nicht ohne Marke)", list.Items.Count == 9, string.Join(" | ", list.Items));
        Check("Aufgabe vor der ersten Gruppe hat keine Gruppe", list.Items[0].Text == "loose task" && list.Items[0].Group == null);
        Check("Zustaende [ ] [/] [x] [#] [X]",
            list.Items[1].State == TodoState.Open && list.Items[2].State == TodoState.InProgress && list.Items[3].State == TodoState.Done
            && list.Items[5].State == TodoState.Blocked && list.Items[6].State == TodoState.Done, string.Join(" | ", list.Items.Select(i => i.State)));
        var falstad = list.Items[2];
        Check("Unteraufgaben haengen an ihrer Aufgabe", falstad.Children.Count == 2 && list.Items[3].Parent == falstad && list.Items[3].Depth == 1);
        Check("nach der Notiz geht es auf oberster Ebene weiter", list.Items[5].Parent == null && list.Items[5].Text == "Tube models");
        Check("geordnete Listen mit . und )", list.Items[7].Bullet == "1." && list.Items[8].Bullet == "2)" && list.Items[8].Group == "Docs");
        Check("leere Datei: keine Aufgaben", TodoList.Parse("").Items.Count == 0 && !TodoList.Parse("").HasContent);
        Check("leerer Aufgabentext", TodoList.Parse("- [ ]\n").Items.Single().Text == "");
        Check("Schluss-# einer Ueberschrift gehoert nicht zum Namen", TodoList.Parse("## Group ##\n- [ ] a\n").Items[0].Group == "Group");
    }

    private static void States()
    {
        var list = TodoList.Parse(Sample);
        list.SetState(list.Items[1], TodoState.Blocked);
        Same("Zustand: nur das Zeichen in der Klammer aendert sich", list.ToString(), Sample.Replace("- [ ] Show the probe values", "- [#] Show the probe values"));
        list.SetState(list.Items[6], TodoState.Open);
        Check("[X] wird als [ ] geschrieben", list.ToString().Contains("- [ ] RC sample"));
        list.SetState(list.Items[3], TodoState.InProgress);
        Check("eingerueckte Unteraufgabe", list.ToString().Contains("  - [/] Parser"));
        Check("Klick: offen -> in Arbeit -> erledigt -> offen, blockiert -> offen",
            ProjectNotes.Next(TodoState.Open) == TodoState.InProgress && ProjectNotes.Next(TodoState.InProgress) == TodoState.Done
            && ProjectNotes.Next(TodoState.Done) == TodoState.Open && ProjectNotes.Next(TodoState.Blocked) == TodoState.Open);
        bool threw = false;
        var stale = list.Items[1];
        list.SetText(stale, "changed");
        try { list.SetState(stale, TodoState.Done); } catch (InvalidOperationException) { threw = true; }
        Check("eine veraltete Aufgabe wird nicht geaendert", threw);
    }

    private static void Texts()
    {
        var list = TodoList.Parse(Sample);
        list.SetText(list.Items[2], "Falstad import: group linked pots");
        Same("Text: nur der Text der Zeile aendert sich", list.ToString(), Sample.Replace("- [/] Falstad import\n", "- [/] Falstad import: group linked pots\n"));
        list.SetText(list.Items[0], "two\nlines");
        Check("Zeilenumbrueche werden Leerzeichen", list.Items[0].Text == "two lines");
        list.SetText(list.Items[0], "");
        Check("leerer Text", list.ToString().Contains("\n- [ ]\n"));
    }

    private static void Removing()
    {
        var list = TodoList.Parse(Sample);
        list.Remove(list.Items[2]);
        Same("Loeschen: mit Unteraufgaben und eingerueckter Notiz", list.ToString(), Sample.Replace("""
            - [/] Falstad import
              - [x] Parser
              - [ ] Dialog

              A note below the Falstad task.

            """, ""));
        list.Remove(list.Items.First(i => i.Text == "Second"));
        Check("letzte Aufgabe der Datei", list.ToString().EndsWith("## Docs\n1. [ ] CIRCUITS.md in English\n"), list.ToString());
    }

    private static void Adding()
    {
        var list = TodoList.Empty();
        list.Add("first");
        Same("erste Aufgabe: mit Titel", list.ToString(), "# Todo\n\n- [ ] first\n");
        list.Add("second");
        Same("zweite Aufgabe: dahinter", list.ToString(), "# Todo\n\n- [ ] first\n- [ ] second\n");
        list.Add("in a group", "Later");
        Same("neue Gruppe am Ende, mit Leerzeile", list.ToString(), "# Todo\n\n- [ ] first\n- [ ] second\n\n## Later\n- [ ] in a group\n");
        list.Add("ungrouped");
        Same("ohne Gruppe: hinter die Aufgaben ohne Gruppe", list.ToString(), "# Todo\n\n- [ ] first\n- [ ] second\n- [ ] ungrouped\n\n## Later\n- [ ] in a group\n");

        list = TodoList.Parse(Sample);
        var added = list.Add("Probe tooltip colours", "Circuits", TodoState.InProgress);
        Check("in vorhandene Gruppe: hinter die letzte Aufgabe (und ihre Notizen)",
            list.ToString().Contains("- [X] RC sample\n- [/] Probe tooltip colours\n- a list item without a mark") && added.Text == "Probe tooltip colours" && added.Group == "Circuits",
            list.ToString());
        list.Add("Third", "Docs");
        Check("geordnete Liste: naechste Nummer", list.ToString().Contains("2) [x] Second\n3) [ ] Third\n"), list.ToString());

        list = TodoList.Parse("# Todo\n\n## Empty\n\n## Full\n- [ ] a\n");
        list.Add("b", "Empty");
        Same("Gruppe ohne Aufgaben: direkt unter die Ueberschrift", list.ToString(), "# Todo\n\n## Empty\n- [ ] b\n\n## Full\n- [ ] a\n");

        list = TodoList.Parse("# Todo\n\nSome text.\n\n## Group\n- [ ] a\n");
        list.Add("loose");
        Same("ohne Gruppe, wenn es Gruppen gibt: vor die erste Gruppe", list.ToString(), "# Todo\n\nSome text.\n\n- [ ] loose\n\n## Group\n- [ ] a\n");

        list = TodoList.Parse("# Todo\n\nJust text.\n");
        list.Add("x");
        Same("nach einem Absatz: mit Leerzeile", list.ToString(), "# Todo\n\nJust text.\n\n- [ ] x\n");

        list = TodoList.Parse("* [ ] star\n");
        list.Add("next");
        Same("Aufzaehlungszeichen der Nachbarn", list.ToString(), "* [ ] star\n* [ ] next\n");
    }

    private static void LineEndings()
    {
        var list = TodoList.Parse("# Todo\r\n\r\n- [ ] a\r\n- [ ] b");
        list.SetState(list.Items[1], TodoState.Done);
        list.Add("c");
        Same("CRLF und fehlender Zeilenumbruch am Ende bleiben", list.ToString(), "# Todo\r\n\r\n- [ ] a\r\n- [x] b\r\n- [ ] c");
    }

    private static void Preview()
    {
        var list = fire.Editor.MarkdownParser.Parse("- [ ] a\n- [/] b\n- [#] c\n- [x] d\n- [X] e\n- f\n").OfType<fire.Editor.MdList>().Single();
        var states = list.Items.Select(i => i.Task).ToList();
        Check("Markdown-Vorschau: vier Zustaende, Punkt ohne Marke ist keine Aufgabe",
            states.SequenceEqual(new fire.Editor.MdTaskState?[] { fire.Editor.MdTaskState.Open, fire.Editor.MdTaskState.InProgress, fire.Editor.MdTaskState.Blocked, fire.Editor.MdTaskState.Done, fire.Editor.MdTaskState.Done, null }),
            string.Join(",", states));
        Check("Markdown-Vorschau: Checked bleibt (erledigt oder nicht)", list.Items[3].Checked == true && list.Items[1].Checked == false && list.Items[5].Checked == null);
    }

    private const string OverviewSample = """
        # Fuzz pedal

        Goal: a distortion.

        [container]: <x=420 y=180 w=300 src="todo.md"> "todos"

        [container]: <x=40 y=180 w=340 h=200> "signal"
        ## Signal path
        Input → Out

        [container]: <x=16 y=110 w=260> 'inner'
        Nested text

        [/]: <>

        [sketch]: <>
        ```
        [rect]: <0 90 300 60 stroke=#147AD0>
        [path]: <M 20 120 L 280 120 stroke=#ff4070 width=2 arrow=end>
        [text]: <10 10 size=14> "Clipper"
        ```
        [/sketch]: <>
        [/container]: <>

        [line]: <from=signal to=todos arrow=end> "open"

        [other]: <https://example.com> "a link of the author"
        """;

    private static void Overview()
    {
        Check("Uebersicht erkannt", fire.Editor.OverviewParser.IsOverview(OverviewSample) && !fire.Editor.OverviewParser.IsOverview(Sample));
        var doc = fire.Editor.OverviewParser.Parse(OverviewSample);
        Check("keine Probleme", doc.Problems.Count == 0, string.Join("\n", doc.Problems));
        Check("Text der Uebersicht ohne Definitionen", doc.Markdown.StartsWith("# Fuzz pedal") && doc.Markdown.Contains("Goal: a distortion.") && !doc.Markdown.Contains("[container]")
            && !doc.Markdown.Contains("Signal path") && doc.Markdown.Contains("[other]: <https://example.com>"), doc.Markdown);
        var todos = doc.Children[0] as fire.Editor.OvContainer;
        Check("Container mit src: ohne Inhalt, ohne Ende", todos is { Name: "todos", Src: "todo.md", X: 420, Y: 180, W: 300, H: null, EndLine: -1 });
        var signal = doc.Children[1] as fire.Editor.OvContainer;
        Check("Container: Position, Groesse, Name", signal is { Name: "signal", X: 40, Y: 180, W: 340, H: 200 });
        Check("Container: sein Text", signal!.Markdown.Contains("## Signal path") && signal.Markdown.Contains("Input → Out") && !signal.Markdown.Contains("Nested"), signal.Markdown);
        var inner = signal.Children.OfType<fire.Editor.OvContainer>().Single();
        Check("verschachtelt, Name in '...'", inner.Name == "inner" && inner.Markdown == "Nested text" && inner.X == 16);
        var sketch = signal.Children.OfType<fire.Editor.OvSketch>().Single();
        Check("Sketch im Container, Zaun ``` wird uebersprungen", sketch.Shapes.Select(s => s.Kind).SequenceEqual(new[] { "rect", "path", "text" }), string.Join(",", sketch.Shapes.Select(s => s.Kind)));
        Check("Form: Zahlen ohne Schluessel, Attribute", sketch.Shapes[0].Attributes.Number("x", 0) == 0 && sketch.Shapes[0].Attributes.Number("h", 3) == 60 && sketch.Shapes[0].Attributes.Get("stroke") == "#147AD0");
        Check("Pfad: die Woerter ohne Schluessel sind die Pfaddaten", string.Join(" ", sketch.Shapes[1].Attributes.Positional) == "M 20 120 L 280 120" && sketch.Shapes[1].Attributes.Get("arrow") == "end");
        Check("Text: der Titel ist der Text", sketch.Shapes[2].Name == "Clipper" && sketch.Shapes[2].Attributes.Number("size") == 14);
        var line = doc.Lines.Single();
        Check("Linie", line is { From: "signal", To: "todos", Arrow: "end", Name: "open" });
        Check("alle Container", doc.AllContainers().Select(c => c.Name).SequenceEqual(new[] { "todos", "signal", "inner" }));

        var attrs = fire.Editor.OvAttributes.Parse("x=1 label=\"two words\" 3 4 key='q'");
        Check("Attribute: Anfuehrungszeichen, Woerter ohne Schluessel", attrs.Get("label") == "two words" && attrs.Get("key") == "q" && attrs.Positional.SequenceEqual(new[] { "3", "4" }) && attrs.Number("x") == 1);

        var bad = fire.Editor.OverviewParser.Parse("Text\n[container]: <x=1 y=2> \"a\"\nInhalt\n\n[line]: <from=a to=b>\n\n[/]: <>\n[/]: <>\n");
        Check("Probleme: klebt am Absatz, keine Breite, unbekanntes Ziel, [/] zu viel",
            bad.Problems.Count == 4 && bad.Problems.Any(p => p.StartsWith("2:") && p.Contains("directly follows text")) && bad.Problems.Any(p => p.Contains("no width"))
            && bad.Problems.Any(p => p.Contains("'b'")) && bad.Problems.Any(p => p.StartsWith("8:") && p.Contains("ends nothing")), string.Join("\n", bad.Problems));
        var open = fire.Editor.OverviewParser.Parse("[container]: <x=0 y=0 w=10>\ntext\n");
        Check("Problem: nicht beendet", open.Problems.Single().Contains("not ended") && ((fire.Editor.OvContainer)open.Children[0]).Markdown == "text");
    }

    private static string Apply(string[] lines, (int First, int Count, List<string> Lines) change)
    {
        var list = lines.ToList();
        list.RemoveRange(change.First, change.Count);
        list.InsertRange(change.First, change.Lines);
        return string.Join("\n", list);
    }

    private static void OverviewEdits()
    {
        Check("Attribute: Wert an seiner Stelle", fire.Editor.OverviewSource.SetAttributes("[container]: <x=40 y=180 w=340> \"signal\"", new[] { ("x", "50"), ("y", "200") })
            == "[container]: <x=50 y=200 w=340> \"signal\"");
        Check("Attribute: neues hinten, Anfuehrungszeichen bleiben", fire.Editor.OverviewSource.SetAttributes("[container]: <x=1 src=\"a b.md\" w=3>", new[] { ("h", "120"), ("w", "40") })
            == "[container]: <x=1 src=\"a b.md\" w=40 h=120>");
        Check("Attribute: leeres <>", fire.Editor.OverviewSource.SetAttributes("[sketch]: <>", new[] { ("x", "1") }) == "[sketch]: <x=1>");
        Check("Zahl", fire.Editor.OverviewSource.Number(12.3456) == "12.35" && fire.Editor.OverviewSource.Number(40) == "40");

        string[] lines = OverviewSample.Replace("\r\n", "\n").Split('\n');
        var doc = fire.Editor.OverviewParser.Parse(OverviewSample);
        var signal = (fire.Editor.OvContainer)doc.Children[1];
        Same("eigener Text eines Containers", fire.Editor.OverviewSource.OwnText(lines, doc, signal), "## Signal path\nInput → Out");
        Same("eigener Text der Uebersicht", fire.Editor.OverviewSource.OwnText(lines, doc, null), "# Fuzz pedal\n\nGoal: a distortion.\n\n[other]: <https://example.com> \"a link of the author\"");

        string changed = Apply(lines, fire.Editor.OverviewSource.ReplaceOwnText(lines, doc, signal, "## New title\n\nMore text."));
        var again = fire.Editor.OverviewParser.Parse(changed);
        var signal2 = (fire.Editor.OvContainer)again.Children[1];
        Check("Text ersetzen: neuer Text, innere Elemente bleiben, keine Probleme",
            signal2.Markdown == "## New title\n\nMore text." && signal2.Children.Count == 2 && signal2.Children.OfType<fire.Editor.OvContainer>().Single().Markdown == "Nested text"
            && again.Problems.Count == 0 && again.Lines.Count == 1, changed + "\n" + string.Join("\n", again.Problems));
        Check("Text ersetzen: alles ausserhalb bleibt", changed.StartsWith("# Fuzz pedal\n\nGoal: a distortion.\n\n[container]: <x=420 y=180 w=300 src=\"todo.md\"> \"todos\"\n\n[container]: <x=40 y=180 w=340 h=200> \"signal\"\n## New title")
            && changed.EndsWith("[/container]: <>\n\n[line]: <from=signal to=todos arrow=end> \"open\"\n\n[other]: <https://example.com> \"a link of the author\""), changed);

        string root = Apply(lines, fire.Editor.OverviewSource.ReplaceOwnText(lines, doc, null, "# Pedal\n\nShort."));
        var rootDoc = fire.Editor.OverviewParser.Parse(root);
        Check("Text der Uebersicht ersetzen: Text zuerst, dann die Container, dann die Linien",
            rootDoc.Markdown == "# Pedal\n\nShort." && rootDoc.Children.Count == 2 && rootDoc.Lines.Count == 1 && rootDoc.Problems.Count == 0 && root.TrimEnd().EndsWith("[line]: <from=signal to=todos arrow=end> \"open\""),
            root + "\n" + string.Join("\n", rootDoc.Problems));
    }

    private static void Folders()
    {
        string root = Path.Combine(Path.GetTempPath(), "fire-notes-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = new FireProject { Name = "App" };
            project.Save(Path.Combine(root, "App", "App.fireproj"));
            Check("Ordner: notes/ neben dem Projekt", ProjectNotes.Folder(project) == Path.Combine(root, "App", "notes"));
            Check("Todo-Datei", ProjectNotes.TodoPath(project) == Path.Combine(root, "App", "notes", "todo.md"));
            Check("ohne Einstellung wird nichts geschrieben", !File.ReadAllText(project.FilePath!).Contains("notes"));

            project.Notes = "docs/notes";
            project.Save();
            var loaded = FireProject.Load(project.FilePath!);
            Check("\"notes\" im Projekt: gelesen und geschrieben", loaded.Notes == "docs/notes" && ProjectNotes.Folder(loaded) == Path.Combine(root, "App", "docs", "notes"));

            var solution = new FireSolution { Name = "All", Notes = "planning" };
            solution.Save(Path.Combine(root, "All.firesln"));
            var loadedSolution = FireSolution.Load(solution.FilePath!);
            Check("\"notes\" in der Mappe", loadedSolution.Notes == "planning" && ProjectNotes.TodoPath(loadedSolution) == Path.Combine(root, "planning", "todo.md"));

            string todo = ProjectNotes.TodoPath(loaded);
            var list = TodoList.Load(todo);
            Check("fehlende Datei: leere Liste, kein Ordner", !list.HasContent && !Directory.Exists(Path.GetDirectoryName(todo)));
            list.Add("first");
            list.Save(todo);
            Check("Speichern legt den Ordner an", File.ReadAllText(todo) == "# Todo\n\n- [ ] first\n");
            Check("Laden", TodoList.Load(todo).Items.Single().Text == "first");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
