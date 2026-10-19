namespace fire.Standard
{
    /// <summary>
    /// `#import "testing"`: the namespace <c>Testing</c> with the class <c>Assert</c> (like NUnit). A failed check throws an
    /// <c>Assertion</c> (an <c>Exception</c> of the core prelude); `forge test` counts it as a failure, every other exception as an error.
    ///
    ///   Testing.Assert.AreEqual(4, 2 + 2)
    ///   Testing.Assert.IsTrue(list.Count > 0, "the list is empty")
    ///   var e = Testing.Assert.Throws(() => { int x = 1 / 0 })
    ///
    /// Pure fire, so it behaves the same in the virtual machine and in a native build.
    /// </summary>
    public static class TestingPrelude
    {
        public const string Source = """
            namespace Testing {
                class Assert {
                    // A value as it is shown in a message: strings in quotes.
                    static string Show(any v) {
                        if (v == undefined) { return "undefined" }
                        if (v is of string) { return "\"" + v + "\"" }
                        return "" + v
                    }

                    static string With(string text, string message) {
                        if (message == "") { return text }
                        return message + " - " + text
                    }

                    // Fails the test at once.
                    static undefined Fail(string message = "failed") { throw new Assertion(message) }

                    static undefined IsTrue(bool condition, string message = "") {
                        if (!condition) { throw new Assertion(Testing.Assert.With("expected true", message)) }
                    }

                    static undefined IsFalse(bool condition, string message = "") {
                        if (condition) { throw new Assertion(Testing.Assert.With("expected false", message)) }
                    }

                    static undefined AreEqual(any expected, any actual, string message = "") {
                        if (!(expected == actual)) {
                            throw new Assertion(Testing.Assert.With("expected " + Testing.Assert.Show(expected) + ", got " + Testing.Assert.Show(actual), message))
                        }
                    }

                    static undefined AreNotEqual(any notExpected, any actual, string message = "") {
                        if (notExpected == actual) {
                            throw new Assertion(Testing.Assert.With("did not expect " + Testing.Assert.Show(actual), message))
                        }
                    }

                    // Two numbers that may differ by at most `tolerance` (same unit dimension).
                    static undefined AreNear(any expected, any actual, any tolerance, string message = "") {
                        if (Math.Abs(expected - actual) > tolerance) {
                            throw new Assertion(Testing.Assert.With("expected " + Testing.Assert.Show(expected) + " +- " + Testing.Assert.Show(tolerance) + ", got " + Testing.Assert.Show(actual), message))
                        }
                    }

                    // Arrays (or lists) with the same items in the same order.
                    static undefined AreSequenceEqual(any expected, any actual, string message = "") {
                        if (expected.Length != actual.Length) {
                            throw new Assertion(Testing.Assert.With("expected " + expected.Length + " items, got " + actual.Length, message))
                        }
                        for (int i = 0; i < expected.Length; i++) {
                            if (!(expected[i] == actual[i])) {
                                throw new Assertion(Testing.Assert.With("item " + i + ": expected " + Testing.Assert.Show(expected[i]) + ", got " + Testing.Assert.Show(actual[i]), message))
                            }
                        }
                    }

                    static undefined IsUndefined(any value, string message = "") {
                        if (value != undefined) { throw new Assertion(Testing.Assert.With("expected undefined, got " + Testing.Assert.Show(value), message)) }
                    }

                    static undefined IsNotUndefined(any value, string message = "") {
                        if (value == undefined) { throw new Assertion(Testing.Assert.With("expected a value, got undefined", message)) }
                    }

                    // A string that contains `part`, or an array/list that contains the item `part`.
                    static undefined Contains(any part, any whole, string message = "") {
                        if (whole is of string) {
                            if (whole.IndexOf(part) < 0) { throw new Assertion(Testing.Assert.With(Testing.Assert.Show(whole) + " does not contain " + Testing.Assert.Show(part), message)) }
                            return
                        }
                        for (int i = 0; i < whole.Length; i++) {
                            if (whole[i] == part) { return }
                        }
                        throw new Assertion(Testing.Assert.With("the items do not contain " + Testing.Assert.Show(part), message))
                    }

                    // Calls `action` and expects an exception; returns it (check its class with `is of`).
                    static any Throws(lambda action, string message = "") {
                        try {
                            action()
                        } catch (e) {
                            if (e is of Assertion) { throw e }
                            return e
                        }
                        throw new Assertion(Testing.Assert.With("expected an exception", message))
                    }

                    // Calls `action` and expects that it does not throw.
                    static undefined DoesNotThrow(lambda action, string message = "") {
                        try {
                            action()
                        } catch (e) {
                            if (e is of Assertion) { throw e }
                            throw new Assertion(Testing.Assert.With("unexpected exception: " + e.message, message))
                        }
                    }
                }
            }
            """;
    }
}
