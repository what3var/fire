using fire.Bytecode;
using fire.Compiler;
using fire.Parsing;
using fire.Resolving;
using fire.Runtime;
using fire.Values;

/// <summary>The ComboBox of the UI library and the package `ui.docking` (docs/DOCKING.md) in the virtual machine: the scripts drive a UI.Root directly with mouse and
/// keys (ui.MouseDown/MouseMove/MouseUp/KeyDown) and print the state.</summary>
static class DockingTests
{
    private static int _failures;

    private const string Head = """
        // GetPixel returns the colour value signed (32 bit): compare the RGB part
        class Px { static int Get(console, int x, int y) { return console.GetPixel(x, y) & 16777215 } }
        var fb = new Framebuffer(400, 300)
        var win = new Window(fb, "Test")
        var ui = new UI.Root(fb, win)

        """;

    private static string StdPrelude(string import) => fire.Package.Manager.PackageStore.Default.FindImport(import)!.ReadPrelude()!;

    private static List<string> Run(string script, VmExecutionMode mode, bool docking)
    {
        var lines = new List<string>();
        var imports = new List<string> { "graphics", "windows", "reflection", "ui" };
        if (docking) imports.Add("ui.docking");
        var sources = new[] { fire.Standard.Prelude.Source }.Concat(imports.Select(StdPrelude)).Append(Head + script);
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var program = Parser.ParseMultiple(sources.Select(src => Preprocessor.Process(src, Directory.GetCurrentDirectory(), included)).ToList());
        var natives = new NativeRegistry();
        natives.Register("print", args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        natives.RegisterBaseTypeNatives();
        var fbManager = new fire.Terminal.FramebufferManager();
        var conManager = new fire.Terminal.RendererManager(fbManager, new fire.Terminal.IntegratedGlyphFont());
        var winManager = new fire.Terminal.Windows.WindowManager(fbManager, (l, v) => { }, () => new FakeRenderer());
        fire.Terminal.Bridge.GraphicsBridge.RegisterAll(natives, fbManager, conManager);
        fire.Windows.Bridge.WindowsBridge.RegisterAll(natives, winManager);
        ReflectionNatives.Register(natives);
        // __Shot(fb.id, "name"): with FIRE_DOCKING_SHOTS=<folder> the framebuffer is saved there as a PNG (to look at the layout)
        natives.Register("__Shot", args =>
        {
            string? folder = Environment.GetEnvironmentVariable("FIRE_DOCKING_SHOTS");
            if (folder == null || mode != VmExecutionMode.Debug) return Value.MakeUndefined();
            var framebuffer = fbManager.GetFramebuffer((int)args[0].AsInt());
            Directory.CreateDirectory(folder);
            var image = fire.Terminal.ImageData.CreateTruecolor(framebuffer.Width, framebuffer.Height, (uint[])framebuffer.Pixels.Clone(), "PNG");
            File.WriteAllBytes(Path.Combine(folder, args[1].AsString() + ".png"), fire.Terminal.ImageEncoder.EncodePng(image));
            return Value.MakeUndefined();
        });
        var resolved = Resolver.Resolve(program, natives.Names);
        var compiled = fire.Compiler.Compiler.Compile(program, resolved, natives);
        var vm = new VM(compiled.TopLevel, new Scope(null, isGlobal: true), natives, compiled.Classes, isMainThreadVm: true, executionMode: mode);
        vm.Run();
        if (vm.UnhandledException != null) lines.Add("UNHANDLED: " + new UncaughtScriptException(vm.UnhandledException).Message);
        winManager.StopManagers();
        return lines;
    }

    private static void Check(string title, string script, string[] expected, bool docking = true)
    {
        foreach (var mode in new[] { VmExecutionMode.Debug, VmExecutionMode.Release })
        {
            string[] actual;
            try { actual = Run(script, mode, docking).ToArray(); }
            catch (Exception ex) { actual = new[] { "AUSNAHME: " + CompileErrors.Describe(ex) }; }
            bool ok = actual.SequenceEqual(expected);
            if (!ok) _failures++;
            Console.WriteLine(ok ? $"OK: Docking: {title} [{mode}]" : $"FEHLER: Docking: {title} [{mode}]\n  erwartet: {string.Join(" | ", expected)}\n  erhalten: {string.Join(" | ", actual)}");
        }
    }

    public static void Run()
    {
        Console.WriteLine("=== ComboBox und ui.docking ===");
        const int Down = 1073741905, Up = 1073741906, F4 = 1073741885;

        Check("ComboBox: Klick oeffnet, Zeile waehlt, Tasten, Klick daneben schliesst", $$"""
            var combo = new UI.ComboBox(10, 10, 120, 24)
            combo.Add("Red")
            combo.Add("Green")
            combo.Add("Blue")
            var hits = 0
            combo.OnSelect = func () => { hits = hits + 1 }
            ui.Add(combo)
            ui.Draw()
            print("leer '" + combo.Text + "' " + combo.SelectedIndex)
            ui.MouseDown(1, 50, 20)
            ui.MouseUp(1, 50, 20)
            ui.Draw()
            print("offen " + combo.IsDropDownOpen() + " " + ui.popups.count + " " + combo.popupList.ay)
            var row = combo.popupList.RowHeight(ui)
            ui.MouseMove(50, 34 + row + row / 2)
            ui.Draw()
            print("hover " + combo.popupList.SelectedIndex + " " + combo.SelectedIndex)
            ui.MouseDown(1, 50, 34 + row + row / 2)
            ui.MouseUp(1, 50, 34 + row + row / 2)
            ui.Draw()
            print("gewaehlt " + combo.Text + " " + combo.IsDropDownOpen() + " " + hits + " " + combo.TakeChanged() + " fokus " + combo.Focused)
            ui.KeyDown({{Down}}, 0)
            print("runter " + combo.Text)
            ui.KeyDown({{Down}}, 0)
            print("unten bleibt " + combo.Text)
            ui.KeyDown({{Up}}, 0)
            print("hoch " + combo.Text)
            ui.KeyDown({{Down}}, 512)
            ui.Draw()
            print("Alt+Runter " + combo.IsDropDownOpen())
            ui.KeyDown({{Up}}, 0)
            ui.KeyDown(13, 0)
            ui.Draw()
            print("Enter " + combo.Text + " " + combo.IsDropDownOpen())
            ui.KeyDown({{F4}}, 0)
            ui.KeyDown(27, 0)
            ui.Draw()
            print("Escape " + combo.IsDropDownOpen() + " " + combo.Text)
            ui.MouseDown(1, 50, 20)
            ui.MouseUp(1, 50, 20)
            ui.MouseDown(1, 50, 20)
            ui.MouseUp(1, 50, 20)
            ui.Draw()
            print("zweiter Klick schliesst " + combo.IsDropDownOpen())
            ui.MouseDown(1, 50, 20)
            ui.MouseUp(1, 50, 20)
            ui.MouseDown(1, 300, 250)
            ui.MouseUp(1, 300, 250)
            ui.Draw()
            print("daneben " + combo.IsDropDownOpen() + " " + combo.Text)
            combo.SelectedIndex = 2
            ui.Draw()
            print("von aussen " + combo.Text + " " + hits)
            print("SelectText " + combo.SelectText("Red") + " " + combo.SelectedIndex + " " + combo.SelectText("Pink"))
            """, new[] { "leer '' -1", "offen True 1 34", "hover 1 -1", "gewaehlt Green False 1 True fokus True", "runter Blue", "unten bleibt Blue", "hoch Green",
                "Alt+Runter True", "Enter Red False", "Escape False Red", "zweiter Klick schliesst False", "daneben False Red", "von aussen Blue 5", "SelectText True 0 False" }, docking: false);

        Check("ComboBox: displayMember, itemsSource, Zeichnen", """
            class Fruit {
                string name
                construct(string n) { this.name = n }
            }
            var fruits = new List()
            fruits.Add(new Fruit("Apple"))
            fruits.Add(new Fruit("Pear"))
            var combo = new UI.ComboBox(10, 10, 120, 24)
            combo.DisplayMember = "name"
            combo.ItemsSource = fruits
            combo.SelectedIndex = 1
            ui.Add(combo)
            ui.Draw()
            print(combo.Text + " " + combo.count + " " + combo.selectedItem.name)
            // the arrow on the right is drawn in the colour of the text
            print(Px.Get(ui.renderer, 10 + 120 - 9, 10 + 12) == (ui.theme.text.Color & 16777215))
            var auto = new UI.ComboBox(10, 50, -1, -1)
            auto.Add("a")
            auto.Add("a much longer entry")
            ui.Add(auto)
            ui.Draw()
            print(auto.actualWidth > ui.cw * 19)
            """, new[] { "Pear 2 Pear", "True", "True" }, docking: false);

        Check("Leisten: Overflow-Menue, Always/Never, Klick im Menue, Federn in der Statusleiste", """
            var dm = new UI.Docking.DockManager()
            ui.Add(dm)
            var strip = new UI.Docking.ToolStrip("Main")
            var hits = 0
            var first = strip.AddButton("Button1")
            first.overflow = UI.Docking.Overflow.Never
            strip.AddButton("Button2")
            strip.AddSeparator()
            strip.AddButton("Button3")
            strip.AddButton("Button4")
            strip.AddButton("Button5")
            strip.AddButton("Button6")
            var last = strip.AddButton("Last", func () => { hits = hits + 1 })
            var always = strip.AddButton("Hidden")
            always.overflow = UI.Docking.Overflow.Always
            dm.AddToolStrip(strip)
            var sb = new UI.Docking.StatusBar()
            var ready = sb.AddLabel("Ready", true)
            var pos = sb.AddLabel("Ln 1")
            var bar = sb.AddProgressBar(80)
            bar.value = 50
            dm.SetStatusBar(sb)
            ui.Draw()
            print("strip " + strip.actualWidth + " shown " + strip.shown.count + " overflow " + strip.overflowItems.count)
            print("first " + strip.shown.IndexOf(first) + " last " + strip.overflowItems.IndexOf(last) + " always " + strip.overflowItems.IndexOf(always))
            print("spring " + (ready.actualWidth + pos.actualWidth + bar.actualWidth + 4 >= 390) + " " + ready.rx)
            ui.MouseDown(1, strip.ax + strip.actualWidth - 7, strip.ay + 10)
            ui.MouseUp(1, strip.ax + strip.actualWidth - 7, strip.ay + 10)
            ui.Draw()
            print("menu " + ui.popups.count + " " + last.inOverflow + " " + last.ax + "," + last.ay + " " + last.actualWidth)
            __Shot(fb.id, "overflow")
            ui.MouseMove(last.ax + 10, last.ay + 5)
            ui.MouseDown(1, last.ax + 10, last.ay + 5)
            ui.MouseUp(1, last.ax + 10, last.ay + 5)
            ui.Draw()
            print("clicked " + hits + " menu " + ui.popups.count)
            ui.MouseDown(1, strip.ax + strip.actualWidth - 7, strip.ay + 10)
            ui.MouseUp(1, strip.ax + strip.actualWidth - 7, strip.ay + 10)
            ui.MouseDown(1, strip.ax + strip.actualWidth - 7, strip.ay + 10)
            ui.MouseUp(1, strip.ax + strip.actualWidth - 7, strip.ay + 10)
            ui.Draw()
            print("toggle " + ui.popups.count)
            """, new[] { "strip 400 shown 6 overflow 3", "first 0 last 1 always 2", "spring True 2", "menu 1 True 282,50 116", "clicked 1 menu 0", "toggle 0" });

        Check("Leisten: Reihen, Ziehen am Griff, an andere Kanten, schwebend und zurueck", """
            var dm = new UI.Docking.DockManager()
            ui.Add(dm)
            var a = new UI.Docking.ToolStrip("A")
            a.AddButton("One")
            a.AddButton("Two")
            var b = new UI.Docking.ToolStrip("B")
            b.AddButton("Three")
            b.AddButton("Four")
            dm.AddToolStrip(a)
            dm.AddToolStrip(b)
            ui.Draw()
            print("row0 " + a.rx + " " + b.rx + "=" + a.actualWidth + " h " + dm.topStrips.actualHeight)
            var gx = b.ax + 4
            var gy = b.ay + 10
            ui.MouseDown(1, gx, gy)
            ui.MouseMove(gx + 30, gy)
            ui.MouseMove(gx + 60, gy)
            ui.Draw()
            print("along " + b.rx + " row " + b.row)
            ui.MouseMove(gx + 60, gy + 28)
            ui.Draw()
            print("row " + b.row + " rows " + dm.topStrips.RowCount() + " h " + dm.topStrips.actualHeight + " " + b.ry)
            ui.MouseMove(6, 150)
            ui.Draw()
            print("left " + dm.leftStrips.strips.count + " " + b.Horizontal + " " + b.actualWidth + "x" + b.actualHeight + " top " + dm.topStrips.strips.count + " h " + dm.topStrips.actualHeight)
            ui.MouseMove(200, 150)
            ui.Draw()
            var f = b.parent
            print("float " + b.floating + " " + dm.floats.count + " " + f.Left + "," + f.Top + " " + b.Horizontal)
            ui.MouseUp(1, 200, 150)
            ui.Draw()
            __Shot(fb.id, "floatstrip")
            ui.MouseDown(1, f.ax + 10, f.ay + 5)
            ui.MouseMove(f.ax + 40, f.ay + 5)
            ui.MouseMove(150, 8)
            ui.MouseUp(1, 150, 8)
            ui.Draw()
            print("docked " + b.floating + " " + dm.floats.count + " top " + dm.topStrips.strips.count + " row " + b.row + " " + b.rx)
            b.Visible = false
            ui.Draw()
            print("hidden " + dm.topStrips.RowStrips(0).count)
            """, new[] { "row0 0 84=84 h 26", "along 144 row 0", "row 1 rows 2 h 52 26", "left 1 False 54x60 top 1 h 26", "float True 1 194,144 True", "docked False 0 top 2 row 0 145", "hidden 1" });

        Check("Leisten: SplitButton, ComboBox und TextBox in der Leiste", """
            var dm = new UI.Docking.DockManager()
            ui.Add(dm)
            var strip = new UI.Docking.ToolStrip("Tools")
            var log = ""
            var split = strip.AddSplitButton("Run", func () => { log = log + "run;" })
            split.AddItem("Debug", func () => { log = log + "debug;" })
            split.AddItem("Release", func () => { log = log + "release;" })
            var combo = strip.AddComboBox(90)
            combo.Add("x86")
            combo.Add("x64")
            combo.SelectedIndex = 0
            var box = strip.AddTextBox("", 80)
            var check = strip.AddButton("Wrap")
            check.Checkable = true
            dm.AddToolStrip(strip)
            ui.Draw()
            ui.MouseDown(1, split.ax + 5, split.ay + 5)
            ui.MouseUp(1, split.ax + 5, split.ay + 5)
            ui.Draw()
            print("main " + log + " " + ui.popups.count)
            ui.MouseDown(1, split.ax + split.actualWidth - 4, split.ay + 5)
            ui.MouseUp(1, split.ax + split.actualWidth - 4, split.ay + 5)
            ui.Draw()
            var menu = ui.popups[0]
            print("arrow " + ui.popups.count + " " + menu.ay + "=" + (split.ay + split.actualHeight))
            var mx = menu.ax + 10
            var my = menu.RowTop(ui, 1) + 3
            ui.MouseDown(1, mx, my)
            ui.MouseUp(1, mx, my)
            ui.Draw()
            print("chosen " + log + " " + ui.popups.count)
            ui.MouseDown(1, combo.ax + 10, combo.ay + 5)
            ui.MouseUp(1, combo.ax + 10, combo.ay + 5)
            ui.KeyDown(1073741905, 0)
            ui.KeyDown(13, 0)
            ui.Draw()
            print("combo " + combo.Text)
            ui.MouseDown(1, box.ax + 5, box.ay + 5)
            ui.MouseUp(1, box.ax + 5, box.ay + 5)
            ui.TextInput("abc")
            ui.Draw()
            print("text " + box.Text + " " + box.Focused)
            ui.MouseDown(1, check.ax + 5, check.ay + 5)
            ui.MouseUp(1, check.ax + 5, check.ay + 5)
            print("check " + check.IsChecked + " " + check.TakeClicked())
            __Shot(fb.id, "items")
            """, new[] { "main run; 0", "arrow 1 24=24", "chosen run;release; 0", "combo x64", "text abc True", "check True True" });

        const string Tools = """
            var dm = new UI.Docking.DockManager()
            ui.Add(dm)
            dm.SetContent(new UI.Panel())
            var explorer = dm.Add(new UI.Docking.ToolWindow("Explorer", new UI.ListBox()), UI.Dock.Left)
            var classes = dm.Add(new UI.Docking.ToolWindow("Classes", new UI.TextBox("c")), UI.Dock.Left)
            var output = dm.Add(new UI.Docking.ToolWindow("Output", new UI.TextBox("log")), UI.Dock.Bottom)
            var props = dm.AddTabbed(new UI.Docking.ToolWindow("Properties", new UI.TextBox("p")), classes)
            ui.Draw()

            """;

        Check("Toolfenster: Anordnung, Tabs, Splitter an der Kante und zwischen Panes", Tools + """
            var pe = explorer.Pane()
            var pc = classes.Pane()
            print("left " + dm.leftPanes.count + " " + pe.rx + "," + pe.ry + " " + pe.actualWidth + "x" + pe.actualHeight + " / " + pc.ry + " " + pc.actualHeight)
            print("tabs " + pc.windows.count + " active " + pc.ActiveWindow().title + " same " + (props.Pane() == pc))
            print("center " + dm.centerX + "," + dm.centerY + " " + dm.centerW + "x" + dm.centerH + " content " + dm.content.actualWidth + "x" + dm.content.actualHeight)
            var tabY = pc.ay + pc.actualHeight - 5
            ui.MouseDown(1, pc.TabLeft(ui, 0) + 5, tabY)
            ui.MouseUp(1, pc.TabLeft(ui, 0) + 5, tabY)
            ui.Draw()
            print("tab " + pc.ActiveWindow().title + " focus " + classes.content.Focused + " active pane " + pc.focusInside)
            __Shot(fb.id, "tools")
            // the splitter between the left edge and the centre
            var sx = pe.ax + pe.actualWidth + 1
            ui.MouseDown(1, sx, 100)
            ui.MouseMove(sx + 40, 100)
            ui.MouseUp(1, sx + 40, 100)
            ui.Draw()
            print("edge " + dm.leftSize + " " + pe.actualWidth)
            // the splitter between the two left panes
            var py = pe.ay + pe.actualHeight + 1
            var before = pe.actualHeight
            ui.MouseDown(1, 50, py)
            ui.MouseMove(50, py + 30)
            ui.MouseUp(1, 50, py + 30)
            ui.Draw()
            print("panes " + (pe.actualHeight - before) + " " + (pe.weight + pc.weight))
            """, new[] { "left 2 0,0 200x148 / 152 148", "tabs 2 active Properties same True", "center 204,0 196x156 content 196x156", "tab Classes focus True active pane True", "edge 240 240", "panes 29 200" });

        Check("Toolfenster: Schliessen, Wieder zeigen, Doppelklick schweben/andocken, Groesse aendern", Tools + """
            var pc = classes.Pane()
            var closes = 0
            props.onClose = func () => { closes = closes + 1 }
            // the close button of the caption closes the visible tab (Properties)
            var cx = pc.CloseLeft(ui) + 3
            ui.MouseDown(1, cx, pc.ay + 6)
            ui.MouseUp(1, cx, pc.ay + 6)
            ui.Draw()
            print("closed " + closes + " hidden " + props.isHidden + " tabs " + pc.windows.count + " " + dm.hidden.count)
            props.Show()
            ui.Draw()
            print("shown " + props.isDocked + " same pane " + (props.Pane() == pc) + " tabs " + pc.windows.count + " " + dm.hidden.count)
            explorer.Hide()
            ui.Draw()
            print("hide explorer " + dm.leftPanes.count + " " + dm.sparePanes.count)
            explorer.Show()
            ui.Draw()
            print("show explorer " + dm.leftPanes.count + " side " + explorer.side + " at " + dm.leftPanes.IndexOf(explorer.Pane()) + " " + dm.sparePanes.count)
            // double click on the caption: floats, again: docks
            var po = output.Pane()
            ui.MouseDown(1, po.ax + 30, po.ay + 6)
            ui.MouseUp(1, po.ax + 30, po.ay + 6)
            ui.MouseDown(1, po.ax + 30, po.ay + 6)
            ui.MouseUp(1, po.ax + 30, po.ay + 6)
            ui.Draw()
            var f = po.parent
            print("float " + output.isFloating + " " + dm.floats.count + " " + dm.bottomPanes.count + " " + f.Width + "x" + f.Height)
            // resize at the right bottom corner
            var w0 = f.Width
            var h0 = f.Height
            ui.MouseDown(1, f.ax + f.actualWidth - 2, f.ay + f.actualHeight - 2)
            ui.MouseMove(f.ax + f.actualWidth + 18, f.ay + f.actualHeight + 8)
            ui.MouseUp(1, f.ax + f.actualWidth + 18, f.ay + f.actualHeight + 8)
            ui.Draw()
            print("resized " + (f.Width - w0) + " " + (f.Height - h0))
            __Shot(fb.id, "floating")
            ui.cycles = ui.cycles + 100
            ui.MouseDown(1, po.ax + 30, po.ay + 6)
            ui.MouseUp(1, po.ax + 30, po.ay + 6)
            ui.MouseDown(1, po.ax + 30, po.ay + 6)
            ui.MouseUp(1, po.ax + 30, po.ay + 6)
            ui.Draw()
            print("docked " + output.isDocked + " side " + output.side + " " + dm.floats.count)
            output.Float(50, 60)
            ui.Draw()
            print("Float() " + output.isFloating + " " + po.parent.Left + "," + po.parent.Top)
            output.DockTo(UI.Dock.Right)
            ui.Draw()
            print("DockTo " + output.side + " " + dm.rightPanes.count + " " + dm.floats.count + " " + dm.ToolWindows().count + " " + dm.FindToolWindow("Output").title)
            """, new[] { "closed 1 hidden True tabs 1 1", "shown True same pane True tabs 2 0", "hide explorer 1 1", "show explorer 2 side 0 at 0 0", "float True 1 0 196x140", "resized 20 10",
                "docked True side 3 0", "Float() True 50,60", "DockTo 2 1 0 4 Output" });

        Check("Toolfenster: Ziehen mit Vorschau - an eine Kante, als Tab, vor/nach eine Pane, schwebend lassen", Tools + """
            var po = output.Pane()
            // drag the Output caption to the guide of the right edge
            var gx = dm.ax + dm.GuideX(UI.Dock.Right) + 10
            var gy = dm.ay + dm.GuideY(UI.Dock.Right) + 10
            ui.MouseDown(1, po.ax + 30, po.ay + 6)
            ui.MouseMove(po.ax + 40, po.ay + 6)
            ui.MouseMove(gx, gy)
            ui.Draw()
            print("drag " + output.isFloating + " drop " + dm.dropKind + " side " + dm.dropSide + " preview " + dm.previewX + "," + dm.previewY + " " + dm.previewW + "x" + dm.previewH)
            __Shot(fb.id, "dragside")
            ui.MouseUp(1, gx, gy)
            ui.Draw()
            print("right " + output.side + " " + dm.rightPanes.count + " " + dm.bottomPanes.count + " " + dm.floats.count)
            // drag it onto the centre guide of the Explorer pane: becomes a tab there
            po = output.Pane()
            var pe = explorer.Pane()
            ui.cycles = ui.cycles + 100
            ui.MouseDown(1, po.ax + 30, po.ay + 6)
            ui.MouseMove(po.ax + 40, po.ay + 6)
            ui.MouseMove(pe.ax + 20, pe.ay + 20)
            ui.Draw()
            var cx = dm.ax + dm.GuideX(4) + 10
            var cy = dm.ay + dm.GuideY(4) + 10
            ui.MouseMove(cx, cy)
            ui.Draw()
            print("compass " + (dm.compassPane == pe) + " drop " + dm.dropKind)
            __Shot(fb.id, "dragtab")
            ui.MouseUp(1, cx, cy)
            ui.Draw()
            print("tab " + (output.Pane() == pe) + " " + pe.windows.count + " active " + pe.ActiveWindow().title + " right " + dm.rightPanes.count + " spare " + dm.sparePanes.count)
            // drag the Output tab out again and drop it after the Classes pane
            ui.cycles = ui.cycles + 100
            var tx = pe.TabLeft(ui, 1) + 5
            var ty = pe.ay + pe.actualHeight - 5
            ui.MouseDown(1, tx, ty)
            ui.MouseMove(tx + 10, ty)
            var pc = classes.Pane()
            ui.MouseMove(pc.ax + 20, pc.ay + 20)
            ui.Draw()
            var ax = dm.ax + dm.GuideX(6) + 10
            var ay = dm.ay + dm.GuideY(6) + 10
            ui.MouseMove(ax, ay)
            ui.Draw()
            print("after drop " + dm.dropKind + " explorer tabs " + pe.windows.count)
            ui.MouseUp(1, ax, ay)
            ui.Draw()
            print("after " + dm.leftPanes.count + " " + dm.leftPanes.IndexOf(output.Pane()) + " " + output.side)
            // released over nothing: stays floating where it was dropped
            po = output.Pane()
            ui.cycles = ui.cycles + 100
            ui.MouseDown(1, po.ax + 30, po.ay + 6)
            ui.MouseMove(po.ax + 40, po.ay + 6)
            ui.MouseMove(300, 120)
            ui.MouseUp(1, 300, 120)
            ui.Draw()
            print("floating " + output.isFloating + " " + dm.leftPanes.count + " at " + (po.parent.Left + 30 - 10) + "," + po.parent.Top)
            """, new[] { "drag True drop 1 side 2 preview 200,0 200x300", "right 2 1 0 0", "compass True drop 2", "tab True 2 active Output right 0 spare 1", "after drop 4 explorer tabs 1",
                "after 3 2 0", "floating True 2 at 220,114" });

        Check("Beispiel wie in docs/DOCKING.md: Symbole aus dem Aufruf und geteilte, alles zusammen gezeichnet", """
            var dock = new UI.Docking.DockManager()
            ui.Add(dock)
            var tools = new UI.Docking.ToolStrip("Standard")
            tools.AddButton("New", undefined, UI.Icons.File())
            tools.AddButton("Open", undefined, UI.Icons.Folder())
            var shared = UI.Icons.Check()
            tools.AddButton("", undefined, shared)
            tools.AddButton("", undefined, shared)
            tools.AddSeparator()
            var run = tools.AddSplitButton("Run")
            run.AddItem("Run without debugging")
            var target = tools.AddComboBox(70)
            target.Add("Debug")
            target.SelectedIndex = 0
            dock.AddToolStrip(tools)
            var files = new UI.ListBox()
            files.Add("main.fire")
            dock.Add(new UI.Docking.ToolWindow("Explorer", files), UI.Dock.Left)
            var output = dock.Add(new UI.Docking.ToolWindow("Output", new UI.TextBox("")), UI.Dock.Bottom)
            dock.AddTabbed(new UI.Docking.ToolWindow("Errors", new UI.ListBox()), output)
            dock.SetContent(new UI.TextBox("the document"))
            var status = new UI.Docking.StatusBar()
            status.AddLabel("Ready", true)
            status.AddLabel("Ln 1, Col 1")
            dock.SetStatusBar(status)
            ui.Draw()
            ui.Draw()
            print("windows " + dock.ToolWindows().count + " shown " + tools.shown.count + " overflow " + tools.overflowItems.count + " shared " + (shared is from ui))
            __Shot(fb.id, "example")
            """, new[] { "windows 3 shown 7 overflow 0 shared False" });

        string dir = Path.Combine(Path.GetTempPath(), "fire-docking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string fxml = Path.Combine(dir, "Pick.fxml").Replace('\\', '/');
            File.WriteAllText(fxml, """
                <Window Class="Pick" Title="Pick" Width="300" Height="200">
                  <StackPanel Padding="4">
                    <ComboBox Name="choice" Width="140" SelectedIndex="1" OnSelect="Picked">
                      <Item>One</Item>
                      <Item>Two</Item>
                      <Item Text="Three"/>
                    </ComboBox>
                  </StackPanel>
                </Window>
                """);
            Check("ComboBox im Markup: Item-Teile, selectedIndex, onSelect", $$"""
                #include "{{fxml}}"
                class PickApp : PickBase {
                    Picked(sender) { print("picked " + sender.Text) }
                }
                var app = new PickApp()
                app.ui.Draw()
                print(app.choice.count + " " + app.choice.Text)
                app.choice.Select(2)
                """, new[] { "picked Two", "3 Two", "picked Three" }, docking: false);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Console.WriteLine(_failures == 0 ? "Alle Docking-Pruefungen bestanden." : $"FEHLER: {_failures} Docking-Pruefung(en) fehlgeschlagen.");
    }
}
