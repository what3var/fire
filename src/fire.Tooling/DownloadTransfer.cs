namespace fire.Tooling
{
    /// <summary>
    /// A download into a file for the setups of fire (the portable toolchains, <see cref="ToolchainSetup"/> and <see cref="WatcomSetup"/>; spark's first-start setup): the bytes as they
    /// come (<paramref name="progress"/> gets done and total, total 0 when the server does not say), a cancellation between two blocks, a complete file or an exception.
    /// </summary>
    public static class DownloadTransfer
    {
        public const string UserAgent = "fire-toolchain-setup";

        /// <summary>Downloads <paramref name="url"/> into <paramref name="file"/> (replaced). With <paramref name="resume"/> a file that is there is continued with a Range request
        /// (a server that answers 200 instead sends it all again). Throws <see cref="IOException"/> when the download is incomplete, <see cref="OperationCanceledException"/>
        /// when it was cancelled (the file stays as far as it came). Returns the bytes of the whole file.</summary>
        public static long Fetch(HttpClient http, string url, string file, Action<long, long>? progress = null, Action<string>? log = null, CancellationToken token = default, bool resume = false)
        {
            long have = resume && File.Exists(file) ? new FileInfo(file).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            using var response = http.Send(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (have > 0 && response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable) { progress?.Invoke(have, have); return have; }   // (complete already)
            response.EnsureSuccessStatusCode();
            bool continued = have > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (!continued) have = 0;
            long length = response.Content.Headers.ContentLength ?? 0;
            long total = length > 0 ? have + length : 0;
            using var input = response.Content.ReadAsStream(token);
            using var stop = token.Register(() => { try { input.Dispose(); } catch (Exception) { } });   // (a read that hangs ends with the cancellation)
            using var output = new FileStream(file, continued ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
            var buffer = new byte[81920];
            long done = have;
            int lastPercent = -1;
            progress?.Invoke(done, total);
            int n;
            while (true)
            {
                try { n = input.Read(buffer, 0, buffer.Length); }
                catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
                if (n <= 0) break;
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, n);
                done += n;
                progress?.Invoke(done, total);
                int percent = total > 0 ? (int)(done * 100 / total) : -1;
                if (log != null && percent != lastPercent && (percent % 5 == 0 || percent < 0))
                {
                    lastPercent = percent;
                    log(total > 0 ? $"Downloading... {percent}% ({done / 1048576} MB)" : $"Downloading... {done / 1048576} MB");
                }
            }
            token.ThrowIfCancellationRequested();
            if (total > 0 && done != total) throw new IOException($"The download is incomplete ({done} of {total} bytes). Check the connection (or a proxy / virus scanner) and try again.");
            return done;
        }
    }
}
