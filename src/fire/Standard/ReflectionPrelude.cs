using System.Collections.Generic;

namespace fire.Standard
{
    /// <summary>
    /// The reflection library (`#import "reflection"`): query and use classes and members at runtime. The source here is
    /// the fire side (`Type`, `Member`, `Reflect`, `Selector`); the actual work is done by native functions of the runtime
    /// (`fire.Runtime.ReflectionNatives`), so that access checks, units and locking apply as in normal code.
    /// See docs/DESIGN_LAMBDA_REFLECTION_PROBE.md and SPEC 8.13.
    /// </summary>
    public static class ReflectionPrelude
    {
        /// <summary>If this native function is registered, the program uses the library (the compiler then also writes type metadata).</summary>
        public const string MembersNative = "__refl_members";

        /// <summary>The classes of the library: for an access through them, for private/protected the code BEFORE it counts.</summary>
        public static readonly HashSet<string> HelperClasses = new() { "Reflect", "Type", "Member", "Selector" };

        public const string Source = """
            class ReflectionException : Exception {
                construct(string message) { this.message = message }
            }

            // A declared field, property, method or constructor of a class
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
                    this.ParamNames = flat d[10]   // (copies that belong to the member: the arrays of the native call die with the scope that made them)
                    this.ParamTypes = flat d[11]
                }

                ParamCount() { return this.ParamNames.length }
                IsField() { return this.Kind == "field" }
                IsProperty() { return this.Kind == "property" }
                IsMethod() { return this.Kind == "method" }

                // Use with an instance (access rules as in normal code)
                Get(class obj) { return Reflect.Get(obj, this.Name) }
                Set(class obj, class value) { Reflect.Set(obj, this.Name, value) }
                Call(class obj, class args) { return Reflect.Call(obj, this.Name, args) }

                // Like `probe obj.name changed|changing handler` (returns the handle for Reflect.SilenceHandle)
                Probe(class obj, string kind, class handler) { return Reflect.Probe(obj, this.Name, kind, handler) }
            }

            // A class: name, base, interfaces and all (also inherited) members
            class Type {
                string Name
                class Base
                bool IsActor
                class Interfaces
                class All

                construct(string name) {
                    var info = __refl_class_info(name)
                    if (info == undefined) { throw new ReflectionException("Unknown class '" + name + "'") }
                    this.Name = info[0]
                    if (info[1] != undefined) { this.Base = new Type(info[1]) }
                    this.IsActor = info[2]
                    this.Interfaces = flat info[3]
                    this.All = new List()   // (assigned directly: the list belongs to the type)
                    var raw = __refl_members(name)
                    // (the members belong to the type, not to the loop body - a list does not own its elements)
                    for (var i = 0; i < raw.length; i = i + 1) { var member = new Member(raw[i]); member.TakeTo(this); this.All.Add(member) }
                }

                // The class of an object (or the one with this name if a string is passed)
                static Of(class x) {
                    var name = __refl_class_name(x)
                    if (name == undefined) { throw new ReflectionException("Not an object and not a known class") }
                    return new Type(name)
                }

                // The class with this name or undefined
                static Named(string name) {
                    if (__refl_class_info(name) == undefined) { return undefined }
                    return new Type(name)
                }

                // The names of all classes of the program
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

                // The first member of this name (field, property or method) or undefined
                Find(string name) {
                    foreach (m in this.All) { if (m.Kind != "constructor" && m.Name == name) { return m } }
                    return undefined
                }
                Has(string name) { return this.Find(name) != undefined }

                IsSubclassOf(class other) { return __refl_is_sub(this.Name, other.Name) }
                New(class args) { return Reflect.New(this.Name, args) }
            }

            // A selector: the reflection of the member that a lambda `c => c.radius` selects (parameter type `lambda member<T>`; `Kind` is the
            // kind of the parameter type: "field", "property", "member" (field or property), "method" or "selector" (everything))
            class Selector {
                class Path
                string Name
                string Kind

                construct(class path, string kind) {
                    this.Path = flat path   // (a copy that belongs to the selector)
                    this.Name = path[path.length - 1]
                    this.Kind = kind
                }

                // The kind of the selected member on `obj`: "field", "property", "method" or undefined
                ActualKind(class obj) { return __refl_member_kind(this.Parent(obj), this.Name) }

                // Checks that the selected member matches the kind of the selector (as soon as there is an object); returns the object it belongs to
                CheckKind(class parent) {
                    var actual = __refl_member_kind(parent, this.Name)
                    if (!Reflect.KindAllowed(actual, this.Kind)) { throw new ReflectionException(Reflect.KindMessage(this.Name, actual, this.Kind)) }
                    return parent
                }

                // The object to which the selected member belongs (for `p => p.address.city` that is `p.address`)
                Parent(class obj) {
                    var o = obj
                    for (var i = 0; i < this.Path.length - 1; i = i + 1) { o = Reflect.Get(o, this.Path[i]) }
                    return o
                }

                // Read/write field or property (a method: Call)
                Get(class obj) {
                    var parent = this.CheckKind(this.Parent(obj))
                    if (__refl_member_kind(parent, this.Name) == "method") { throw new ReflectionException("'" + this.Name + "' is a method - Call(obj, args) calls it") }
                    return Reflect.Get(parent, this.Name)
                }
                Set(class obj, class value) {
                    var parent = this.CheckKind(this.Parent(obj))
                    if (__refl_member_kind(parent, this.Name) == "method") { throw new ReflectionException("'" + this.Name + "' is a method and cannot be assigned") }
                    Reflect.Set(parent, this.Name, value)
                }

                // Call a method (only for `lambda selector<T>`, which permits methods)
                Call(class obj, class args) {
                    var parent = this.CheckKind(this.Parent(obj))
                    if (__refl_member_kind(parent, this.Name) != "method") { throw new ReflectionException("'" + this.Name + "' is not a method") }
                    return Reflect.Call(parent, this.Name, args)
                }

                Describe(class obj) { return Type.Of(this.CheckKind(this.Parent(obj))).Find(this.Name) }

                // Probe on the selected field/property (kind: "changed" or "changing"); Silence removes it again
                Probe(class obj, string kind, class handler) {
                    var parent = this.CheckKind(this.Parent(obj))
                    if (__refl_member_kind(parent, this.Name) == "method") { throw new ReflectionException("A probe cannot be registered on a method ('" + this.Name + "')") }
                    return Reflect.Probe(parent, this.Name, kind, handler)
                }
                Silence(class obj) { Reflect.Silence(this.Parent(obj), this.Name) }
            }

            class Reflect {
                static Get(class obj, string name) { return __refl_get(obj, name) }
                static Set(class obj, string name, class value) { __refl_set(obj, name, value) }
                static Call(class obj, string name, class args) { return __refl_call(obj, name, args) }
                static New(string className, class args) { return __refl_new(className, args) }
                static Has(class obj, string name) { return __refl_has(obj, name) }

                // Probes (see `probe`/`silence`): kind is "changed" or "changing"; depending on the parameter count the handler gets
                // (new), (old, new), (object, old, new) or (object, name, old, new). Returns the handle.
                static Probe(class obj, string name, string kind, class handler) { return __refl_probe(obj, name, kind, handler) }
                static ProbeAll(class obj, string kind, class handler) { return __refl_probe(obj, undefined, kind, handler) }
                static Silence(class obj, string name) { __refl_silence(obj, name) }
                static SilenceAll(class obj) { __refl_silence(obj, undefined) }
                static SilenceHandle(class handle) { __refl_silence_handle(handle) }

                // Converts the lambda of a selector parameter (`lambda field|property|member|selector<T>`) into a Selector (called by the compiler at the
                // start of the function); `kind` is the kind of the parameter type
                static SelectorOf(class l, string kind) {
                    if (l is of Selector) { return l }
                    return new Selector(__refl_selector_path(l), kind)
                }

                // May a member of this kind (actual: "field", "property", "method" or undefined) be chosen by a selector of this kind?
                static KindAllowed(class actual, string kind) {
                    if (actual == undefined) { return false }
                    if (kind == "selector") { return true }
                    if (kind == "member") { return actual == "field" || actual == "property" }
                    return actual == kind
                }

                static KindWord(class actual) {
                    if (actual == "field") { return "a field" }
                    if (actual == "property") { return "a property" }
                    return "a method"
                }

                // Message for a member that does not match the selector
                static KindMessage(string name, class actual, string kind) {
                    if (actual == undefined) { return "'" + name + "' is not a member" }
                    var expected = "a field"
                    if (kind == "property") { expected = "a property" }
                    if (kind == "method") { expected = "a method" }
                    if (kind == "member") { expected = "a field or a property" }
                    return "'" + name + "' is " + Reflect.KindWord(actual) + ", expected (lambda " + kind + "<...>): " + expected
                }
            }
            """;
    }
}
