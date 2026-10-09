namespace fire.Runtime
{
    /// <summary>
    /// The ownership layer knows WHEN an object instance is destroyed
    /// (cascade), but not HOW a destruct() method body is executed
    /// (that requires a statement interpreter). The evaluator implements
    /// this interface and is called from Scope.Release / ObjectInstance.Destroy.
    /// For tests/before the evaluator is complete, a
    /// simple implementation that only logs or does nothing suffices.
    /// </summary>
    public interface IDestructRunner
    {
        void RunDestructor(ObjectInstance instance);
    }

    /// <summary>No-op implementation for contexts without destructor semantics
    /// (e.g. unit tests that only want to check the ownership tree itself).</summary>
    public sealed class NullDestructRunner : IDestructRunner
    {
        public static readonly NullDestructRunner Instance = new();
        public void RunDestructor(ObjectInstance instance) { }
    }
}
