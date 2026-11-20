/// <summary>The completion of the editor (fire.Editor CompletionEngine on ScriptSymbolIndex): what `x.` and a name being typed offer - only what the
/// code at the cursor may use (`private` in the class itself, `protected` also in derived classes, docs/language/08-classes.md), static members only
/// on the class and instance members only on objects, the receiver's type also when it comes from `var`, a global variable or a lambda - and the order
/// (the program's own names and exact prefixes first, the internals of the libraries last). Alone: FIRE_TESTS_ONLY=completion.</summary>
static class CompletionTests
{
    private static int _failures;

    private static int _document;

    /// <summary>The suggestions at `|` in `source`, the best first (as the editor sorts them); `projectFiles` are the other files of the project. Over the protocol
    /// (`forge serve`, docs/spec/FORGE_SERVE.md) unless FIRE_SPARK_LANG=inproc; then in this process (ScriptSymbolIndex, CompletionEngine).</summary>
    private static List<fire.Editor.CompletionItem> Items(string source, IEnumerable<(string, string)>? projectFiles = null)
    {
        int cursor = source.IndexOf('|');
        source = source.Remove(cursor, 1);
        if (!fire.Editor.ServeClient.InProcess) return Served(source, cursor, projectFiles);
        return InProcess(source, cursor, projectFiles);
    }

    private static List<fire.Editor.CompletionItem> InProcess(string source, int cursor, IEnumerable<(string, string)>? projectFiles)
    {
        var index = fire.Editor.ScriptSymbolIndex.Build(source, Array.Empty<string>(), projectFiles ?? Array.Empty<(string, string)>());
        return fire.Editor.CompletionEngine.GetSuggestions(source, cursor, index).OrderByDescending(i => i.Score).ToList();
    }

    private static List<fire.Editor.CompletionItem> Served(string source, int cursor, IEnumerable<(string, string)>? projectFiles)
    {
        var serve = fire.Editor.ServeClient.Shared
            ?? throw new InvalidOperationException("forge serve: " + (fire.Editor.ServeClient.FindExecutable(out var why) == null ? why : "did not start"));
        string path = Path.Combine(Path.GetTempPath(), $"fire-completion-{Environment.ProcessId}", $"case{++_document}.fire");
        serve.Open(path, 1, source, includes: (projectFiles ?? Array.Empty<(string, string)>()).Select(f => new fire.Editor.ServeFile(f.Item1, f.Item2)));
        try
        {
            var items = serve.Completion(path, cursor, out _);
            // the port is 1:1: the same items (text, kind, score, detail) in the same order as in this process
            var own = InProcess(source, cursor, projectFiles);
            static string Show(fire.Editor.CompletionItem i) => $"{i.Text}|{i.Kind}|{i.Score:R}|{i.Detail}";
            if (!items.Select(Show).SequenceEqual(own.Select(Show)))
            {
                _differences++;
                int at = 0;
                while (at < items.Count && at < own.Count && Show(items[at]) == Show(own[at])) at++;
                Console.WriteLine($"FEHLER: Vervollstaendigung: Protokoll und eigener Prozess verschieden ({items.Count} / {own.Count} Vorschlaege), ab {at}:"
                    + $"\n  Protokoll:        {string.Join(", ", items.Skip(at).Take(6).Select(Show))}\n  eigener Prozess: {string.Join(", ", own.Skip(at).Take(6).Select(Show))}");
            }
            return items;
        }
        finally { serve.Close(path); }
    }

    private static int _differences;

    /// <summary>`expected` must be suggested, `forbidden` must not (comma-separated); `exact`: nothing else.</summary>
    private static void Check(string title, string source, string expected, string forbidden = "", bool exact = false, IEnumerable<(string, string)>? projectFiles = null)
    {
        var names = Items(source, projectFiles).Select(i => i.Text).ToList();
        var want = expected.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var deny = forbidden.Split(',', StringSplitOptions.RemoveEmptyEntries);
        bool ok = want.All(names.Contains) && !deny.Any(names.Contains) && (!exact || names.Count == want.Length);
        Report(title, ok, ok ? null : $"  bekommen ({names.Count}): {string.Join(", ", names.Take(40))}"
            + $"\n  fehlt: {string.Join(", ", want.Where(w => !names.Contains(w)))}  zu viel: {string.Join(", ", deny.Where(names.Contains))}");
    }

    /// <summary>`first` comes before `second` in the list (both must be there).</summary>
    private static void CheckOrder(string title, string source, string first, string second, IEnumerable<(string, string)>? projectFiles = null)
    {
        var names = Items(source, projectFiles).Select(i => i.Text).ToList();
        int a = names.IndexOf(first), b = names.IndexOf(second);
        Report(title, a >= 0 && b >= 0 && a < b, $"  {first}: {a}, {second}: {b} in {string.Join(", ", names.Take(30))}");
    }

    private static void Report(string title, bool ok, string? detail)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Vervollstaendigung: {title}" : $"FEHLER: Vervollstaendigung: {title}\n{detail}");
    }

    private const string Bank = """
        class Account {
            private int balance = 0
            protected string owner = "nobody"
            static int Count = 0
            undefined Deposit(int amount) { this.balance += amount }
            int Balance() { return this.balance }
            static Account Make() { return new Account() }
            bool Same(Account other) { return other.| == this.balance }
        }
        class Savings : Account {
            float rate = 0.01
            undefined Grow() { this.| }
        }

        """;

    public static void Run()
    {
        Console.WriteLine();
        Console.WriteLine("=== Editor: Vervollstaendigung (Sichtbarkeit, Empfaengertyp, Reihenfolge) ===");
        Console.WriteLine(fire.Editor.ServeClient.InProcess ? "(im eigenen Prozess: FIRE_SPARK_LANG=inproc)" : "(ueber das Protokoll: forge serve, verglichen mit dem eigenen Prozess)");
        _failures = 0;
        _differences = 0;
        string bank = Bank.Replace("other.|", "other.balance").Replace("this.| }", "this.rate }");

        // ---- private / protected: only where the code may use them ----
        Check("private/protected ausserhalb jeder Klasse versteckt", bank + "var acc = new Account()\nacc.|", "Deposit,Balance,Same", "balance,owner,Count,Make");
        Check("private/protected mit Praefix ausserhalb versteckt", bank + "var acc = new Account()\nacc.b|", "", "balance", exact: false);
        Check("in der Klasse selbst sichtbar (this.)", bank.Replace("this.balance += amount", "this.|"), "balance,owner,Deposit,Count");
        Check("in der Klasse: private eines anderen Objekts derselben Klasse", Bank.Replace("this.| }", "this.rate }"), "balance,owner,Deposit");
        Check("protected in der Ableitung sichtbar, private der Basis nicht", Bank.Replace("other.|", "other.balance"), "owner,rate,Deposit,Grow", "balance");
        Check("protected ausserhalb auch an der Ableitung versteckt", bank + "var s = new Savings()\ns.|", "Deposit,Grow,rate", "owner,balance");
        Check("private in einer fremden Klasse versteckt", bank + "class Other { M(Account a) { a.| } }", "Deposit", "balance,owner");
        Check("protected in einer fremden Klasse versteckt", bank + "class Other { M(Savings a) { a.| } }", "Deposit,rate", "balance,owner");
        Check("Lambda in einer Methode zaehlt zur Klasse", bank.Replace("this.balance += amount", "var f = func () => { this.| }"), "balance,owner");
        Check("Lambda auf oberster Ebene zaehlt zu keiner Klasse", bank + "var acc = new Account()\nvar f = func () => { acc.| }", "Deposit", "balance,owner,Grow");
        Check("Name in der Klasse: private sichtbar", bank.Replace("this.balance += amount", "bal|"), "balance,Balance");
        Check("Name ausserhalb: keine Mitglieder", bank + "bal|", "", "balance,Balance");
        Check("private Felder mit 'defined', Generics, Lambda-Typ und Liste versteckt",
            "class Box {\n private defined int size = 1\n private List<int> items\n private int lambda clock\n private int a, b\n Open() { }\n}\nvar x = new Box()\nx.|",
            "Open", "size,items,clock,a,b", exact: true);
        Check("dieselben Felder in der Klasse sichtbar",
            "class Box {\n private defined int size = 1\n private List<int> items\n private int lambda clock\n private int a, b\n Open() { this.| }\n}",
            "Open,size,items,clock,a,b", exact: true);
        Check("benannte Argumente: private Methode bietet ihre Parameter nicht an", "class W { private static F(int size) { } }\nW.F(|", "", "size: ");

        // ---- inherited and the receiver's type ----
        Check("geerbte oeffentliche Mitglieder", bank + "var s = new Savings()\ns.|", "Deposit,Balance,Same,Grow,rate");
        Check("var aus anderer Variable", bank + "var a = new Account()\nvar b = a\nb.|", "Deposit", "balance");
        Check("var aus statischer Methode", bank + "var a = Account.Make()\na.|", "Deposit", "Make");
        Check("var aus Methodenkette", "class A { B Next() { return new B() } }\nclass B { Z() { } }\nvar a = new A()\nvar b = a.Next()\nb.|", "Z", exact: true);
        Check("globale Variable in einer Methode", bank + "var acc = new Account()\nclass User { M() { acc.| } }", "Deposit", "balance,owner,Grow");
        Check("globale Variable in einem Lambda", bank + "var acc = new Account()\nvar f = func () => { acc.| }", "Deposit", "Grow");
        Check("globale Variable nach der Methode deklariert", bank + "class User { M() { acc.| } }\nvar acc = new Account()", "Deposit", "Grow");
        Check("lokale Variable der Methode im Lambda", bank + "class User { M() {\n var a = new Account()\n var f = func () => { a.| }\n } }", "Deposit", "Grow");
        Check("Parameter der Methode im Lambda", bank + "class User { M(Account a) {\n var f = func () => { a.| }\n } }", "Deposit", "Grow");
        Check("Lambda mit Block ohne func", bank + "var f = (int n) => {\n var a = new Account()\n a.|\n}", "Deposit", "Grow,balance");
        Check("Parameter eines Lambdas mit Block", bank + "var f = (Account a) => {\n a.|\n}", "Deposit", "Grow");
        Check("Feld der Klasse geht der globalen Variablen vor", bank + "var acc = new Savings()\nclass User {\n Account acc\n M() { acc.| } }", "Deposit", "Grow");
        Check("Variable einer anderen Methode bleibt unsichtbar", "class A { M() { var q = new B() }\n N() { var r = 1\n | } }\nclass B { }", "r", "q");
        Check("Variable einer Methode auf oberster Ebene unsichtbar", "class A { M() { var q = 1 } }\nvar top = 2\nq|", "", "q");
        Check("globale Variable als Name in einer Methode", "var counter = 1\nclass A { M() { cou| } }", "counter");
        Check("generischer Typ einer Variablen", "class P { Name() { } }\nList<P> people\npeople.|", "Add,Count");
        Check("Feld mit generischem Typ", "class A { List<string> lines\n M() { this.lines.| } }", "Add,Count");
        Check("Feld mit 'defined'", "class P { Go() { } }\nclass A { defined P p = new P()\n M() { this.p.| } }", "Go", exact: true);

        // ---- static vs. instance ----
        Check("Klasse.: nur statische Mitglieder", bank + "Account.|", "Count,Make", "Deposit,Balance,balance", exact: true);
        Check("Objekt.: keine statischen Mitglieder", bank + "var a = new Account()\na.|", "Deposit", "Count,Make");
        Check("this.: auch statische Mitglieder", bank.Replace("this.balance += amount", "this.|"), "Count,Make,Deposit");
        Check("statische Methode: nur statische Mitglieder ohne this", "class A { int n\n static int total\n static Sum() { | }\n Get() { } }", "total,Sum", "n,Get");
        Check("Instanzmethode: alle Mitglieder", "class A { int n\n static int total\n M() { | } }", "n,total,M");
        Check("statische Klasse der Bibliothek", "#import \"windows\"\nWindowManager.|", "Default", "Attach,windows");
        Check("inline c ist statisch", "class N { inline c int Add(int a, int b) { return a + b; }\n Go() { } }\nN.|", "Add", "Go");

        // ---- what the index collects (no junk members) ----
        Check("keine Scheinmitglieder aus Typen (defined, List, lambda)",
            "class A {\n defined int Left\n List<string> Lines(int n) { return undefined }\n int lambda clock\n lambda<int, int> OnMove\n Dictionary<string, int> map\n}\nvar a = new A()\na.|",
            "Left,Lines,clock,OnMove,map", exact: true);
        Check("Attribute sind keine Mitglieder", "class T {\n [Test \"Adds\"]\n undefined Adds() { }\n [Colorize 0xFF0000]\n int Port\n}\nvar t = new T()\nt.|", "Adds,Port", exact: true);
        Check("mehrere Felder in einer Zeile", "class A { int x = 1, y\n float z }\nvar a = new A()\na.|", "x,y,z", exact: true);

        // ---- libraries: the API first, no junk, no internals of the runtime ----
        Check("UI.Button: API da, keine Scheinmitglieder", "#import \"ui\"\nvar b = new UI.Button(\"x\")\nb.|", "Text,OnClick,Width,Click,Visible", "defined,List,lambda");
        CheckOrder("UI.Button: API vor den Interna der Bibliothek", "#import \"ui\"\nvar b = new UI.Button(\"x\")\nb.|", "Width", "cw");
        CheckOrder("UI.Button: eigene vor geerbten", "#import \"ui\"\nvar b = new UI.Button(\"x\")\nb.|", "Text", "Background");
        CheckOrder("genaue Gross-/Kleinschreibung zuerst", "#import \"ui\"\nvar b = new UI.Button(\"x\")\nb.T|", "Text", "templateRoot");
        Check("UI.Root aus var", "#import \"ui\"\nvar fb = new Framebuffer(10, 10)\nvar win = new Window(fb, \"x\")\nvar ui = new UI.Root(fb, win)\nui.|", "Add,Run", "defined");
        Check("Window aus var", "#import \"ui\"\nvar fb = new Framebuffer(10, 10)\nvar win = new Window(fb, \"x\")\nwin.|", "Close,Show,Run", "defined");
        Check("UI-Element in einem Lambda (Ereignis)", "#import \"ui\"\nvar answer = new UI.Label(\"x\")\nvar greet = new UI.Button(\"Greet\")\ngreet.OnClick = func () => { answer.| }", "Text", "OnClick");
        Check("Klassen der Laufzeit (__Await) nicht angeboten", "__A|", "", "__Await");
        Check("unbekannter Typ: Mitglieder der eigenen Klassen", "#import \"ui\"\nclass Mine { Go() { } }\nvar q = something()\nq.|", "Go", "Text,Width,Add,cw");
        Check("unbekannter Typ ohne eigene Klassen: nichts ohne Praefix", "#import \"ui\"\nvar q = something()\nq.|", "", "Text,Width,Add", exact: true);
        Check("unbekannter Typ ohne eigene Klassen: API der Bibliothek mit Praefix", "#import \"ui\"\nvar q = something()\nq.Te|", "Text", "templateRoot,templateParts");
        Check("unbekannter Typ: private bleibt versteckt", bank + "var q = something()\nq.|", "Deposit", "balance,owner,Count");

        // ---- the order of names being typed ----
        CheckOrder("lokale Variable vor Schluesselwort", "var count = 1\nco|", "count", "continue");
        CheckOrder("eigene Klasse vor gleich gut passender Bibliotheksklasse", "class Bolt { }\nvar x = new B|", "Bolt", "Bits");
        CheckOrder("Mitglied der Klasse vor Schluesselwort", "class A { int retries\n M() { re| } }", "retries", "return");

        // ---- markup: the code-behind sees the generated class and the library it imports ----
        string markup = """
            <Window Class="Greeter" Title="Greeter" Width="360" Height="200">
              <Resources>
                <Converter Key="Upper" Type="UpperConverter"/>
              </Resources>
              <StackPanel Margin="16" Spacing="8">
                <TextBox Name="nameBox" OnEnter="Greet" Text="{Binding Name, Mode=TwoWay}"/>
                <Button Text="Greet" OnClick="Greet"/>
                <Label Name="answer" Text="Type a name"/>
              </StackPanel>
            </Window>
            """;
        string generated = fire.UI.Markup.MarkupFiles.GenerateScript("Greeter.fxml", markup);
        var project = new[] { ("Greeter.fxml", generated), ("Upper.fire", "class UpperConverter : UI.Converter { override any Convert(any value) { return value } }") };
        string codeBehind = "class GreeterWindow : GreeterBase {\n    override undefined Greet(any sender) { this.| }\n}\n";
        Check("Markup: die Felder und Methoden der erzeugten Klasse", codeBehind, "nameBox,answer,Greet,Run,Show,UIRoot,window", "fxBindAll,fxConv_Upper,fxHandles0,fxBusy0", projectFiles: project);
        Check("Markup: Elemente ohne eigenes #import (das erzeugte Skript importiert ui)", codeBehind.Replace("this.|", "this.nameBox.|"), "Text,OnEnter", "nameBox,Greet", projectFiles: project);
        Check("Markup: Wurzel des Fensters", codeBehind.Replace("this.|", "this.UIRoot.|"), "Add,Run", "defined", projectFiles: project);
        Check("Markup: von aussen", codeBehind.Replace("this.|", "") + "var w = new GreeterWindow()\nw.|", "Run,Show,nameBox", "fxBindAll", projectFiles: project);

        _failures += _differences;
        Console.WriteLine(_failures == 0 ? "Alle Pruefungen der Vervollstaendigung bestanden." : $"FEHLER: {_failures} Pruefung(en) der Vervollstaendigung fehlgeschlagen.");
    }
}
