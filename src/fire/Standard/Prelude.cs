namespace fire.Standard
{
    /// <summary>
    /// A piece of fire source that is placed before every program and
    /// defines the "built-in" types IndexOutOfBoundsException/AccessDeniedException/
    /// IEnumerable/IEnumerator/List - deliberately written in fire itself
    /// instead of as a native C# implementation, since the language
    /// now has enough substance for that (classes, arrays, interfaces) and this
    /// stays consistent with everything else. `IndexOutOfBoundsException`/
    /// `AccessDeniedException` are constructed and thrown by the VM itself on an invalid
    /// array index or a violated access-modifier rule
    /// (see VM.ThrowIndexOutOfBounds/
    /// ThrowAccessDenied) - scripts catch them quite normally via `try`/`catch`,
    /// like any other exception.
    ///
    /// `List` internally uses a fixed-size array that is doubled when needed
    /// (classic dynamic array). `GetEnumerator` creates an
    /// own `ListEnumerator` that knows only MoveNext()/GetCurrent() -
    /// `foreach` calls these two (and GetEnumerator itself) purely via
    /// name dispatch, so it also works on any OTHER class
    /// that has the same three methods, not only on 'List' itself.
    /// </summary>
    public static class Prelude
    {
        /// <summary>The prelude: the core classes below in fire, plus the extensions of the base types
        /// `string` and `char` (`class extends string { ... }`, SPEC 5.5.1/8.12), which are generated from the
        /// method tables in <see cref="StringMethods"/>/<see cref="CharMethods"/>,
        /// so that the method IDs in the fire text do not have to be maintained by hand.</summary>
        public static readonly string Source = CoreSource + StringMethods.PreludeSource + CharMethods.PreludeSource + ResourceMethods.PreludeSource;

        private const string CoreSource = """
            /// The base class of all exceptions (SPEC 7.1): it carries a `message`. Own exception classes derive from it (`class NotFound : Exception { }`);
            /// `catch (Exception e)` catches every thrown value. A program that declares a class `Exception` itself replaces this one.
            class Exception {
                string message

                construct(string message = "") {
                    this.message = message
                }
            }

            class IndexOutOfBoundsException : Exception {
                int index
                int length

                construct(string message, int index, int length) {
                    this.message = message
                    this.index = index
                    this.length = length
                }
            }

            // what the ownership methods take along (SPEC 2.2): `x.TakeUpwards(Takes.Locals)`
            enum Takes { This = 0, Children = 1, Locals = 2, All = 3 }

            class DestroyedException : Exception {

                construct(string message) {
                    this.message = message
                }
            }

            class AccessDeniedException : Exception {

                construct(string message) {
                    this.message = message
                }
            }

            class UnitMismatchException : Exception {
                string expectedUnit
                string actualUnit

                construct(string message, string expectedUnit, string actualUnit) {
                    this.message = message
                    this.expectedUnit = expectedUnit
                    this.actualUnit = actualUnit
                }
            }

            // A command as an object: `Command` without, `Command<T>` with a context (e.g. `Command<IDevice>` - a command that `Device.DoCommand` executes with the
            // device as context). The lambda `Command` is the body; whoever needs more derives and overrides Execute (`class Home : Command<IDevice>`).
            //   var home = new Command<IDevice>()
            //   home.Command = d => { d.WriteString("G28\n") }
            //   Device.Default.DoCommand(home)
            /// <summary>A command as an object that is executed without a context.</summary>
            interface ICommand {
                Execute()
            }

            /// <summary>A command as an object that is executed with a context - e.g. `ICommand<IDevice>` is run by `Device.DoCommand` with the device as its context.</summary>
            interface ICommand<T> {
                Execute(T context)
            }

            /// <summary>A command object without a context. The lambda in `Command` is its body; derive from the class and override `Execute` if you need more.</summary>
            class Command : ICommand {
                lambda Command

                construct() { }

                construct(lambda command) {
                    this.Command = command
                }

                /// <summary>Calls the lambda `Command`; without a lambda nothing happens.</summary>
                /// <returns>The result of the lambda, or `undefined`.</returns>
                Execute() {
                    var body = this.Command
                    if (body == undefined) {
                        return undefined
                    }
                    return body()
                }
            }

            /// <summary>A command object with a context of type T (e.g. `Command<IDevice>`). The lambda in `Command` receives the context as its argument.</summary>
            class Command<T> : ICommand<T> {
                lambda<T> Command

                construct() { }

                construct(lambda<T> command) {
                    this.Command = command
                }

                /// <summary>Calls the lambda `Command` with the context; without a lambda nothing happens.</summary>
                /// <param name="context">What the command works on, e.g. the device.</param>
                /// <returns>The result of the lambda, or `undefined`.</returns>
                Execute(T context) {
                    var body = this.Command
                    if (body == undefined) {
                        return undefined
                    }
                    return body(context)
                }
            }

            interface IEnumerator {
                bool MoveNext()
                class GetCurrent()
            }

            interface IEnumerable {
                IEnumerator GetEnumerator()
            }

            class ListEnumerator : IEnumerator {
                class items
                int count
                int index

                construct(class items, int count) {
                    this.items = items
                    this.count = count
                    this.index = -1
                }

                MoveNext() {
                    this.index = this.index + 1
                    return this.index < this.count
                }

                GetCurrent() {
                    return this.items[this.index]
                }
            }

            // The enumerator of a List: reads through the list, so it stays valid when the list grows (its backing array is replaced).
            class ListIter : IEnumerator {
                class list
                int count
                int index

                construct(class list, int count) {
                    this.list = list
                    this.count = count
                    this.index = -1
                }

                MoveNext() {
                    this.index = this.index + 1
                    return this.index < this.count
                }

                GetCurrent() {
                    return this.list.items[this.index]
                }
            }

            class List : IEnumerable {
                class items
                int count

                construct() {
                    this.items = new class[8]
                    this.count = 0
                }

                // 'new List([1, 2, 3, 4])' - list-literal-like
                // initialisation via a constructor overload with an
                // array parameter (distinguished from construct() purely by
                // the parameter count, like any other overload in this
                // language) - copies each element individually via Add() (instead of
                // taking 'initial' directly as items), so that later
                // Add() calls can keep growing quite normally, without
                // being bound to the exact size of the array originally
                // passed in.
                construct(class initial) {
                    this.items = new class[8]
                    this.count = 0
                    for (var i = 0; i < initial.length; i = i + 1) {
                        this.Add(initial[i])
                    }
                }

                Add(class value) {
                    if (this.count >= this.items.length) {
                        this.Grow()
                    }
                    this.items[this.count] = value
                    this.count = this.count + 1
                }

                operator[](int index) {
                    return this.items[index]
                }

                operator[](int index, class value) {
                    this.items[index] = value
                }

                // removes the element at `index` (the ones behind it move up); the element itself is not destroyed
                RemoveAt(int index) {
                    var i = index
                    while (i < this.count - 1) {
                        this.items[i] = this.items[i + 1]
                        i = i + 1
                    }
                    this.count = this.count - 1
                    this.items[this.count] = undefined
                }

                // puts `value` in at `index`; the elements from there on move down
                Insert(int index, class value) {
                    if (this.count >= this.items.length) {
                        this.Grow()
                    }
                    var i = this.count
                    while (i > index) {
                        this.items[i] = this.items[i - 1]
                        i = i - 1
                    }
                    this.items[index] = value
                    this.count = this.count + 1
                }

                // the index of `value` (the same object), -1 if it is not in the list
                int IndexOf(class value) {
                    var i = 0
                    while (i < this.count) {
                        if (this.items[i] == value) {
                            return i
                        }
                        i = i + 1
                    }
                    return -1
                }

                // removes `value` (the first one); false if it is not in the list
                bool Remove(class value) {
                    var at = this.IndexOf(value)
                    if (at < 0) {
                        return false
                    }
                    this.RemoveAt(at)
                    return true
                }

                // empties the list (the elements are not destroyed)
                Clear() {
                    var i = 0
                    while (i < this.count) {
                        this.items[i] = undefined
                        i = i + 1
                    }
                    this.count = 0
                }

                Grow() {
                    var newItems = new class[this.items.length * 2]
                    var i = 0
                    while (i < this.count) {
                        newItems[i] = this.items[i]
                        i = i + 1
                    }
                    // the new array belongs to the list (a local array dies with this method), the old one is released
                    var old = this.items
                    this.items = newItems
                    newItems.TakeTo(this)
                    delete old
                }

                GetEnumerator() {
                    return new ListIter(this, this.count)
                }
            }
            """;
    }
}
