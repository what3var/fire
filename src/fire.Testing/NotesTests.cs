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
