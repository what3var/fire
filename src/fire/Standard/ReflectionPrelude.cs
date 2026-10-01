using System.Collections.Generic;

namespace fire.Standard
{
    /// <summary>
    /// Die Reflection-Bibliothek (`#import "reflection"`): Klassen und Mitglieder zur Laufzeit abfragen und benutzen. Der Quelltext hier ist
    /// die fire-Seite (`Type`, `Member`, `Reflect`, `Selector`); die eigentliche Arbeit machen native Funktionen der Laufzeit
    /// (`fire.Runtime.ReflectionNatives`), damit Zugriffsprüfung, Einheiten und Locking wie im normalen Code gelten.
    /// Siehe docs/DESIGN_LAMBDA_REFLECTION_PROBE.md und SPEC 8.13.
    /// </summary>
    public static class ReflectionPrelude
    {
        /// <summary>Ist diese native Funktion registriert, nutzt das Programm die Bibliothek (der Compiler schreibt dann Typ-Metadaten mit).</summary>
        public const string MembersNative = "__refl_members";

        /// <summary>Die Klassen der Bibliothek: bei einem Zugriff über sie zählt für private/protected der Code DAVOR.</summary>
        public static readonly HashSet<string> HelperClasses = new() { "Reflect", "Type", "Member", "Selector" };

        public const string Source = """
            class ReflectionException : Exception {
                string message
                construct(string message) { this.message = message }
            }

            // Ein deklariertes Feld, eine Property, Methode oder ein Konstruktor einer Klasse
            class Member {
                string Name
                string Kind
                string TypeName
                string Access
                bool IsStatic
                bool IsReadonly
                bool CanRead
                bool CanWrite
                string Unit
                string DeclaredIn
                class ParamNames
                class ParamTypes

                construct(class d) {
                    this.Name = d[0]
                    this.Kind = d[1]
                    this.TypeName = d[2]
                    this.Access = d[3]
                    this.IsStatic = d[4]
                    this.IsReadonly = d[5]
                    this.CanRead = d[6]
                    this.CanWrite = d[7]
                    this.Unit = d[8]
                    this.DeclaredIn = d[9]
                    this.ParamNames = d[10]
                    this.ParamTypes = d[11]
                }

                ParamCount() { return this.ParamNames.length }
                IsField() { return this.Kind == "field" }
                IsProperty() { return this.Kind == "property" }
                IsMethod() { return this.Kind == "method" }

                // Mit einer Instanz benutzen (Zugriffsregeln wie im normalen Code)
                Get(class obj) { return Reflect.Get(obj, this.Name) }
                Set(class obj, class value) { Reflect.Set(obj, this.Name, value) }
                Call(class obj, class args) { return Reflect.Call(obj, this.Name, args) }

                // Wie `probe obj.name changed|changing handler` (liefert das Handle für Reflect.SilenceHandle)
                Probe(class obj, string kind, class handler) { return Reflect.Probe(obj, this.Name, kind, handler) }
            }

            // Eine Klasse: Name, Basis, Interfaces und alle (auch geerbten) Mitglieder
            class Type {
                string Name
                class Base
                bool IsActor
                class Interfaces
                class All

                construct(string name) {
                    var info = __refl_class_info(name)
                    if (info == undefined) { throw new ReflectionException("Unbekannte Klasse '" + name + "'") }
                    this.Name = info[0]
                    if (info[1] != undefined) { this.Base = new Type(info[1]) }
                    this.IsActor = info[2]
                    this.Interfaces = info[3]
                    var list = new List()
                    var raw = __refl_members(name)
                    for (var i = 0; i < raw.length; i = i + 1) { list.Add(new Member(raw[i])) }
                    this.All = list
                }

                // Die Klasse eines Objekts (oder die mit diesem Namen, wenn ein string übergeben wird)
                static Of(class x) {
                    var name = __refl_class_name(x)
                    if (name == undefined) { throw new ReflectionException("Kein Objekt und keine bekannte Klasse") }
                    return new Type(name)
                }

                // Die Klasse mit diesem Namen oder undefined
                static Named(string name) {
                    if (__refl_class_info(name) == undefined) { return undefined }
                    return new Type(name)
                }

                // Die Namen aller Klassen des Programms
                static Names() { return __refl_classes() }

                Filter(string kind) {
                    var result = new List()
                    foreach (m in this.All) { if (m.Kind == kind) { result.Add(m) } }
                    return result
                }
                Fields() { return this.Filter("field") }
                Properties() { return this.Filter("property") }
                Methods() { return this.Filter("method") }
                Constructors() { return this.Filter("constructor") }

                // Das erste Mitglied dieses Namens (Feld, Property oder Methode) oder undefined
                Find(string name) {
                    foreach (m in this.All) { if (m.Kind != "constructor" && m.Name == name) { return m } }
                    return undefined
                }
                Has(string name) { return this.Find(name) != undefined }

                IsSubclassOf(class other) { return __refl_is_sub(this.Name, other.Name) }
                New(class args) { return Reflect.New(this.Name, args) }
            }

            // Ein Selektor: die Reflection des Mitglieds, das eine Lambda `c => c.radius` auswählt (Parametertyp `lambda property<T>`)
            class Selector {
                class Path
                string Name
                bool FieldOnly

                construct(class path) {
                    this.Path = path
                    this.Name = path[path.length - 1]
                    this.FieldOnly = false
                }

                // `lambda field<T>`: das gewählte Mitglied muss ein Feld sein (geprüft, sobald es ein Objekt gibt)
                CheckKind(class parent) {
                    if (this.FieldOnly) {
                        var kind = __refl_member_kind(parent, this.Name)
                        if (kind != "field") { throw new ReflectionException(Reflect.NotAField(this.Name, kind) + " (erwartet: lambda field<...>)") }
                    }
                    return parent
                }

                // Das Objekt, dem das gewählte Mitglied gehört (bei `p => p.address.city` ist das `p.address`)
                Parent(class obj) {
                    var o = obj
                    for (var i = 0; i < this.Path.length - 1; i = i + 1) { o = Reflect.Get(o, this.Path[i]) }
                    return o
                }
                Get(class obj) { return Reflect.Get(this.CheckKind(this.Parent(obj)), this.Name) }
                Set(class obj, class value) { Reflect.Set(this.CheckKind(this.Parent(obj)), this.Name, value) }
                Describe(class obj) { return Type.Of(this.CheckKind(this.Parent(obj))).Find(this.Name) }

                // Probe auf das gewählte Mitglied (kind: "changed" oder "changing"); Silence entfernt sie wieder
                Probe(class obj, string kind, class handler) { return Reflect.Probe(this.CheckKind(this.Parent(obj)), this.Name, kind, handler) }
                Silence(class obj) { Reflect.Silence(this.Parent(obj), this.Name) }
            }

            class Reflect {
                static Get(class obj, string name) { return __refl_get(obj, name) }
                static Set(class obj, string name, class value) { __refl_set(obj, name, value) }
                static Call(class obj, string name, class args) { return __refl_call(obj, name, args) }
                static New(string className, class args) { return __refl_new(className, args) }
                static Has(class obj, string name) { return __refl_has(obj, name) }

                // Proben (siehe `probe`/`silence`): kind ist "changed" oder "changing"; der Handler bekommt je nach Parameterzahl
                // (neu), (alt, neu), (Objekt, alt, neu) oder (Objekt, Name, alt, neu). Liefert das Handle.
                static Probe(class obj, string name, string kind, class handler) { return __refl_probe(obj, name, kind, handler) }
                static ProbeAll(class obj, string kind, class handler) { return __refl_probe(obj, undefined, kind, handler) }
                static Silence(class obj, string name) { __refl_silence(obj, name) }
                static SilenceAll(class obj) { __refl_silence(obj, undefined) }
                static SilenceHandle(class handle) { __refl_silence_handle(handle) }

                // Wandelt die Lambda eines `lambda property<T>`-Parameters in einen Selector (vom Compiler am Funktionsanfang aufgerufen)
                static SelectorOf(class l) {
                    if (l is of Selector) { return l }
                    return new Selector(__refl_selector_path(l))
                }

                // Meldung für ein Mitglied, das kein Feld ist (kind: "property", "method" oder undefined)
                static NotAField(string name, class kind) {
                    if (kind == undefined) { return "'" + name + "' ist kein Mitglied" }
                    return "'" + name + "' ist kein Feld, sondern " + kind
                }

                // Dasselbe für `lambda field<T>`: das Mitglied muss ein Feld sein
                static FieldSelectorOf(class l) {
                    var s = Reflect.SelectorOf(l)
                    s.FieldOnly = true
                    return s
                }
            }
            """;
    }
}
