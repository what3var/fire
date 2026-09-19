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
        public const string Source = """
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

            class AccessDeniedException : Exception {
                string message

                construct(string message) {
                    this.message = message
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

                Get(int index) {
                    return this.items[index]
                }

                GetIndex(int index) {
                    return this.items[index]
                }

                SetIndex(int index, class value) {
                    this.items[index] = value
                }

                Grow() {
                    var newItems = new class[this.items.length * 2]
                    var i = 0
                    while (i < this.count) {
                        newItems[i] = this.items[i]
                        i = i + 1
                    }
                    this.items = newItems
                }

                GetEnumerator() {
                    return new ListEnumerator(this.items, this.count)
                }
            }
            """;
    }
}
