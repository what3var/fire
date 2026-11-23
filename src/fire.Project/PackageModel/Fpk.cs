using System.IO.Compression;
using System.Text;

namespace fire.Projects
{
    /// <summary>The package file (.fpk): a zip with the `package.json` in its root and the files it names. spark only reads it (its manifest); packing, unpacking and
    /// installing are forge's and ember's (docs/ROADMAP_C_PORT.md, M3).</summary>
    public static class Fpk
    {
        public const string ManifestEntry = "package.json";
        public const string Extension = ".fpk";
        /// <summary>The folder of a package with its templates (docs/tools/templates.md).</summary>
        public const string TemplatesEntry = "templates";

        /// <summary>The `package.json` of a package file; the problems of a package that cannot be used are an exception.</summary>
        public static PackageManifest ReadManifest(string fpkPath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(fpkPath);
                return ReadManifest(zip, fpkPath);
            }
            catch (InvalidDataException) { throw new PackageException($"'{fpkPath}' is not a package file (.fpk)."); }
            catch (IOException ex) { throw new PackageException($"Cannot read '{fpkPath}': {ex.Message}"); }
        }

        private static PackageManifest ReadManifest(ZipArchive zip, string fpkPath)
        {
            var entry = zip.GetEntry(ManifestEntry) ?? throw new PackageException($"'{fpkPath}' has no {ManifestEntry} in its root.");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var manifest = PackageManifest.Parse(reader.ReadToEnd());
            var problems = manifest.Validate(path => zip.GetEntry(path.Replace('\\', '/')) != null);
            if (problems.Count > 0) throw new PackageException($"The package '{fpkPath}' has problems:\n  " + string.Join("\n  ", problems));
            return manifest;
        }
    }
}
