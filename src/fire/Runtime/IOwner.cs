using System.Collections.Generic;

namespace ScriptLang.Runtime
{
    /// <summary>
    /// Alles, das Owner einer Objektinstanz sein kann: ein Scope oder eine andere
    /// Objektinstanz (SPEC 2). Hält die Liste der aktuell besessenen Objekte, damit
    /// Kaskadenlöschung (beim Verlassen eines Scopes bzw. beim Zerstören eines
    /// Objekts) und Zyklenschutz bei TakeTo darauf zugreifen können.
    /// </summary>
    public interface IOwner
    {
        IReadOnlyList<ObjectInstance> OwnedObjects { get; }
        void AddOwned(ObjectInstance obj);
        void RemoveOwned(ObjectInstance obj);
    }
}
