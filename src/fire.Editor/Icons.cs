using System;
using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace fire.Editor
{
    /// <summary>The icons of the editor (the Material PNGs in the folder icons, compiled into the program).</summary>
    internal static class Icons
    {
        private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new();

        /// <summary>The icon `name` (e.g. "Material-Play"), null if there is none.</summary>
        public static Bitmap? Load(string name) => Cache.GetOrAdd(name, n =>
        {
            try { return new Bitmap(AssetLoader.Open(new Uri($"avares://spark/icons/{n}.png"))); }
            catch (Exception) { return null; }
        });
    }
}
