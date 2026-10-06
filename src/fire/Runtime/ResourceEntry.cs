using MemoryPack;

namespace fire.Runtime
{
    /// <summary>A file the compiler embedded in the program (`new Resource("path")`, docs/RESOURCES.md): the path as written in the source (for messages and
    /// <c>Resource.Name()</c>) and the bytes. The compiler hands out the index in <see cref="CompiledProgram.Resources"/> as the id of the resource.</summary>
    [MemoryPackable]
    public sealed partial class ResourceEntry
    {
        public string Name { get; set; } = "";
        public byte[] Data { get; set; } = System.Array.Empty<byte>();
    }
}
