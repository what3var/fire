namespace fire.Projects
{
    /// <summary>One version of a package that a source offers. <paramref name="Yanked"/>: withdrawn (a broken release) - never chosen as the newest, but still installable by its
    /// exact version (`name@1.2.0`). <paramref name="Size"/>: the size of the file in bytes, when the index gives it (checked after the download).</summary>
    public sealed record PackageVersion(string Version, string Url, string? Sha256 = null, bool Yanked = false, long? Size = null);

    /// <summary>What a source says about a package: the metadata of its package.json and where the files of the versions are.</summary>
    public sealed record PackageListing(string Name, string Author, string Description, IReadOnlyList<PackageVersion> Versions, string Source, string License = "", string Homepage = "")
    {
        /// <summary>The newest version that is not yanked (null: there is none).</summary>
        public PackageVersion? Latest
        {
            get
            {
                var offered = Versions.Where(v => !v.Yanked).ToList();
                return offered.Select(v => (v, ok: SemanticVersion.TryParse(v.Version, out var s), s))
                    .Where(x => x.ok).OrderByDescending(x => x.s).Select(x => x.v).FirstOrDefault() ?? offered.LastOrDefault();
            }
        }
    }
}
