namespace ScriptLang.Runtime
{
    /// <summary>
    /// Die Ownership-Schicht weiß, WANN eine Objektinstanz zerstört wird
    /// (Kaskade), aber nicht WIE ein destruct()-Methodenkörper ausgeführt wird
    /// (das erfordert einen Statement-Interpreter). Der Evaluator implementiert
    /// dieses Interface und wird von Scope.Release / ObjectInstance.Destroy
    /// aufgerufen. Für Tests/vor Fertigstellung des Evaluators reicht eine
    /// einfache Implementierung, die nur protokolliert oder nichts tut.
    /// </summary>
    public interface IDestructRunner
    {
        void RunDestructor(ObjectInstance instance);
    }

    /// <summary>No-op-Implementierung für Kontexte ohne Destruktor-Semantik
    /// (z.B. Unit-Tests, die nur den Ownership-Baum selbst prüfen wollen).</summary>
    public sealed class NullDestructRunner : IDestructRunner
    {
        public static readonly NullDestructRunner Instance = new();
        public void RunDestructor(ObjectInstance instance) { }
    }
}
