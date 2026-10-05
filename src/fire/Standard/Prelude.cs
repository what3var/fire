namespace fire.Standard
{
    /// <summary>
    /// Ein Stück fire-Quelltext, das vor jedes Programm gesetzt wird und
    /// die "eingebauten" Typen IndexOutOfBoundsException/AccessDeniedException/
    /// IEnumerable/IEnumerator/List definiert - bewusst in fire selbst
    /// geschrieben statt als native C#-Implementierung, da die Sprache dafür
    /// inzwischen genug Substanz hat (Klassen, Arrays, Interfaces) und das
    /// konsistent mit allem anderen bleibt. `IndexOutOfBoundsException`/
    /// `AccessDeniedException` werden von der VM bei einem ungültigen
    /// Array-Index bzw. einer verletzten Zugriffsmodifikator-Regel selbst
    /// konstruiert und geworfen (siehe VM.ThrowIndexOutOfBounds/
    /// ThrowAccessDenied) - Skripte fangen sie ganz normal per `try`/`catch`,
    /// wie jede andere Exception auch.
    ///
    /// `List` nutzt intern ein Array fester Größe, das bei Bedarf verdoppelt
    /// wird (klassisches dynamisches Array). `GetEnumerator` erzeugt einen
    /// eigenen `ListEnumerator`, der nur MoveNext()/GetCurrent() kennt -
    /// `foreach` ruft diese beiden (und GetEnumerator selbst) rein per
    /// Namens-Dispatch auf, funktioniert also auch auf jeder ANDEREN Klasse,
    /// die dieselben drei Methoden hat, nicht nur auf 'List' selbst.
    /// </summary>
    public static class Prelude
    {
        /// <summary>Der Prelude: die Kernklassen unten in fire, dazu die Erweiterungen der Basistypen
        /// `string` und `char` (`class extends string { ... }`, SPEC 5.5.1/8.12), die aus den
        /// Methoden-Tabellen in <see cref="StringMethods"/>/<see cref="CharMethods"/> erzeugt werden,
        /// damit die Methoden-IDs im fire-Text nicht von Hand gepflegt werden müssen.</summary>
        public static readonly string Source = CoreSource + StringMethods.PreludeSource + CharMethods.PreludeSource;

        private const string CoreSource = """
            class IndexOutOfBoundsException : Exception {
                string message
                int index
                int length

                construct(string message, int index, int length) {
                    this.message = message
                    this.index = index
                    this.length = length
                }
            }

            class DestroyedException : Exception {
                string message

                construct(string message) {
                    this.message = message
                }
            }

            class AccessDeniedException : Exception {
                string message

                construct(string message) {
                    this.message = message
                }
            }

            class UnitMismatchException : Exception {
                string message
                string expectedUnit
                string actualUnit

                construct(string message, string expectedUnit, string actualUnit) {
                    this.message = message
                    this.expectedUnit = expectedUnit
                    this.actualUnit = actualUnit
                }
            }

            // Ein Befehl als Objekt: `Command` ohne, `Command<T>` mit einem Kontext (z.B. `Command<IDevice>` - ein Befehl, den `Device.DoCommand` mit dem
            // Gerät als Kontext ausführt). Das Lambda `Command` ist der Rumpf; wer mehr braucht, leitet ab und überschreibt Execute (`class Home : Command<IDevice>`).
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

                // 'new List([1, 2, 3, 4])' - Listen-Literal-artige
                // Initialisierung über eine Konstruktor-Überladung mit einem
                // Array-Parameter (unterschieden von construct() rein über
                // die Parameteranzahl, wie jede andere Überladung in dieser
                // Sprache) - kopiert jedes Element einzeln über Add() (statt
                // 'initial' direkt als items zu übernehmen), damit spätere
                // Add()-Aufrufe ganz normal weiter wachsen können, ohne an
                // die genaue Größe des ursprünglich übergebenen Arrays
                // gebunden zu sein.
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
