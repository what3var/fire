using fire.Bytecode;

namespace fire.Runtime
{
    /// <summary>
    /// Laufzeit-Wert einer Lambda-Funktion. Braucht - anders als klassische
    /// Closures - KEINEN eingefangenen umgebenden Scope: Lambdas sehen laut SPEC
    /// 4.2 ohnehin nur ihren eigenen (bei jedem Aufruf neu erzeugten) Scope plus
    /// den globalen Scope. Ein LambdaValue muss sich daher nur den kompilierten
    /// Funktionskörper (Proto) und das aktuell gebundene 'this' (OnTarget) merken.
    ///
    /// Ownership (SPEC 4.2 / 2.1): Lambda-Werte werden wie Objektinstanzen
    /// behandelt - Owner ist das Objekt bei direkter Feldzuweisung, sonst der
    /// aktuelle Scope. Diese Politik (welcher Owner bei welcher Zuweisung) setzt
    /// der Evaluator/Compiler an der jeweiligen Zuweisungsstelle um; das
    /// Owner-Feld hier ist deshalb optional und wird in dieser Ausbaustufe
    /// (Funktions-/Call-Frames) noch nicht befüllt - Lambda-Werte können bereits
    /// erzeugt, gebunden und aufgerufen werden, ihre Ownership-Einbindung folgt
    /// mit der Klassen-/Objekt-Ausbaustufe.
    /// </summary>
    public sealed class LambdaValue
    {
        public FunctionProto Proto { get; }

        /// <summary>Das über 'on' gebundene Objekt (this-Kontext), oder null,
        /// wenn kein 'on' verwendet wurde.</summary>
        public object? OnTarget { get; }

        public IOwner? Owner { get; private set; }

        public LambdaValue(FunctionProto proto, object? onTarget, IOwner? owner = null)
        {
            Proto = proto;
            OnTarget = onTarget;
            Owner = owner;
        }

        /// <summary>`a on obj2` - erzeugt einen NEUEN Lambda-Wert mit anderem
        /// this-Kontext; `a` selbst bleibt unverändert (SPEC 4.2). Derselbe Proto
        /// wird wiederverwendet (Ausführung, nicht Definition, ändert sich).</summary>
        public LambdaValue WithOnTarget(object? newTarget, IOwner? owner = null) =>
            new(Proto, newTarget, owner);

        internal void SetOwnerInternal(IOwner newOwner) => Owner = newOwner;

        public bool IsOwnedBy(object ownerCandidate) => Owner != null && ReferenceEquals(Owner, ownerCandidate);

        public bool IsTransitivelyOwnedBy(object ownerCandidate)
        {
            IOwner? current = Owner;
            while (current != null)
            {
                if (ReferenceEquals(current, ownerCandidate)) return true;
                current = current is ObjectInstance oi ? oi.Owner : null;
            }
            return false;
        }
    }
}
