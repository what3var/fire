using System;
using fire.Values;

namespace fire.Standard
{
    /// <summary>`await [ f(), g() ]` (docs/ROADMAP_TASKS.md section 3): the parser makes every call a lambda `(res, fails, i) => { try { res[i] = call }
    /// catch (Exception e) { fails[i] = e } }` and the prelude's `__Await.All` runs them with <see cref="NativeName"/>`(lambdas, res, fails)`, then throws
    /// the first exception in the order of the brackets or gives the results. The VM runs the lambdas one after the other (it gets on the scheduler in step 7);
    /// a native build runs them as tasks side by side.</summary>
    public static class AwaitMethods
    {
        public const string NativeName = "__await_run";

        /// <summary>Set by the VM (fire.Runtime): runs the lambdas on the VM of the calling thread.</summary>
        public static Func<Value[], Value>? Implementation;

        public static Value Call(Value[] args) =>
            (Implementation ?? throw new InvalidOperationException("'await [ ... ]' needs a running program."))(args);
    }
}
