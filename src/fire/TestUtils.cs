using fire.Runtime;
using System;

sealed class LoggingDestructRunner : IDestructRunner
{
    public void RunDestructor(ObjectInstance instance) =>
        Console.WriteLine($"  destruct() aufgerufen für Instanz von '{instance.ClassDef.Name}'");
}

sealed class RaceDemoRunner : IDestructRunner
{
    private readonly ObjectInstance _watchFor;
    private readonly ObjectInstance _transferSource;
    private readonly IDestructRunner _inner;

    public RaceDemoRunner(ObjectInstance watchFor, ObjectInstance transferSource, IDestructRunner inner)
    {
        _watchFor = watchFor;
        _transferSource = transferSource;
        _inner = inner;
    }

    public void RunDestructor(ObjectInstance instance)
    {
        _inner.RunDestructor(instance);
        if (ReferenceEquals(instance, _watchFor))
        {
            Console.WriteLine("  (simuliert: waehrend destruct() wird TakeTo auf das gerade zerstoerte Objekt aufgerufen)");
            _transferSource.TakeTo(_watchFor, this);
        }
    }
}
