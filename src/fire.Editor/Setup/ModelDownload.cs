using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;

namespace fire.Editor.Setup
{
    /// <summary>
    /// Downloads a model of the catalog as <c>AI.ModelInstaller</c> of ai.models does: into "&lt;file&gt;.part" (a stopped download goes on where it stopped, Range), a few attempts
    /// again when the network goes away, then the size and the SHA-256 are checked - a wrong file is removed. A model that is there already is only checked.
    /// </summary>
    public static class ModelDownload
    {
        /// <summary>Downloads and checks <paramref name="model"/>; the step gets the phases and the bytes. Throws when it failed (the reason in the message) or was cancelled
        /// (what came stays in the .part file). Returns the path of the model.</summary>
        public static string Run(SetupModelInfo model, SetupStepContext ctx, string? folder = null, string? mirror = null, HttpClient? http = null, int attempts = 4)
        {
            folder ??= ModelCatalog.Folder();
            Directory.CreateDirectory(folder);
            string path = ModelCatalog.PathOf(model, folder);
            string part = path + ".part";
            if (!ModelCatalog.Installed(model, folder))
            {
                string url = ModelCatalog.UrlOf(model, mirror);
                bool own = http == null;
                http ??= new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                try
                {
                    for (int attempt = 1; ; attempt++)
                    {
                        ctx.Token.ThrowIfCancellationRequested();
                        ctx.Phase(attempt == 1 ? (File.Exists(part) ? "Continuing the download" : "Downloading") : $"Downloading (attempt {attempt} of {attempts})");
                        ctx.Log($"{url} -> {part}");
                        try
                        {
                            fire.Native.DownloadTransfer.Fetch(http, url, part, (done, total) => ctx.Bytes(done, total > 0 ? total : model.Size), null, ctx.Token, resume: true);
                            break;
                        }
                        catch (Exception ex) when (!ctx.Token.IsCancellationRequested && attempt < attempts && ex is HttpRequestException or IOException or TimeoutException
                                                       or System.Threading.Tasks.TaskCanceledException)
                        {
                            ctx.Log("The download stopped: " + ex.Message + " - again in a moment (the part stays).");
                            if (ctx.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(10, attempt * 2)))) ctx.Token.ThrowIfCancellationRequested();
                        }
                    }
                }
                finally { if (own) http.Dispose(); }
                long size = new FileInfo(part).Length;
                if (size != model.Size)
                {
                    File.Delete(part);
                    throw new IOException($"The file has {size} bytes, not {model.Size}; it was removed.");
                }
                File.Move(part, path, overwrite: true);
            }
            Check(model, path, ctx);
            return path;
        }

        /// <summary>The SHA-256 of the file against the catalog's; a wrong file is removed (an <see cref="InvalidDataException"/>).</summary>
        public static void Check(SetupModelInfo model, string path, SetupStepContext ctx)
        {
            ctx.Phase("Checking the SHA-256");
            using var sha = SHA256.Create();
            using (var input = File.OpenRead(path))
            {
                var buffer = new byte[1 << 20];
                long done = 0, total = input.Length;
                ctx.Bytes(0, total);
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ctx.Token.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    done += n;
                    ctx.Bytes(done, total);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            }
            string sum = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (!string.Equals(sum, model.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new InvalidDataException($"The file is not the right one (its SHA-256 is {sum}); it was removed.");
            }
            ctx.Log($"{model.Name}: SHA-256 {sum} - correct.");
        }
    }
}
