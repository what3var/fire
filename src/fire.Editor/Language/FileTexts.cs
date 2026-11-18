using System;
using System.Collections.Concurrent;
using System.IO;

namespace fire.Editor
{
    /// <summary>
    /// The texts of the files on the disk that the checks of the editor read (the files of the projects that are not open): read once and kept while the time the file was written
    /// stays the same - a check after every pause in typing read every file of the project and of its libraries again (and the error list, the completion asked for them, too).
    /// Thread-safe: the checks run in the background. Throws like File.ReadAllText when the file cannot be read.
    /// </summary>
    public static class FileTexts
    {
        private static readonly ConcurrentDictionary<string, (DateTime Written, long Length, string Text)> Known = new(fire.Projects.ProjectFiles.PathComparer);

        /// <summary>How often a file was read from the disk / given from memory (the timing log, the tests).</summary>
        public static int Reads, Hits;

        public static string Read(string path)
        {
            string full = Path.GetFullPath(path);
            var info = new FileInfo(full);
            if (!info.Exists) throw new FileNotFoundException($"Could not find file '{full}'.", full);
            var written = info.LastWriteTimeUtc;
            long length = info.Length;
            if (Known.TryGetValue(full, out var known) && known.Written == written && known.Length == length)
            {
                System.Threading.Interlocked.Increment(ref Hits);
                return known.Text;
            }
            string text = File.ReadAllText(full);
            System.Threading.Interlocked.Increment(ref Reads);
            Known[full] = (written, length, text);
            return text;
        }

        /// <summary>Forgets every text (a solution was closed).</summary>
        public static void Clear() => Known.Clear();
    }
}
