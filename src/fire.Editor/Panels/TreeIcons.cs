using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace fire.Editor
{
    /// <summary>
    /// The icons of the solution explorer: the file type icons (filetypes hires/png) as 16 px pictures, and as 32 px for screens with a scaling of 1.5 and more
    /// (made by treeicons.sh). Loaded once.
    /// </summary>
    internal static class TreeIcons
    {
        private static readonly Dictionary<string, Bitmap?> Cache = new();

        /// <summary>The icon of a row: solution, project, folders, and the files by their extension.</summary>
        public static string NameFor(ExplorerNode node) => node.Kind switch
        {
            ExplorerKind.Solution => "firesln",
            ExplorerKind.Project => "fireproj",
            ExplorerKind.SolutionFolder or ExplorerKind.Folder or ExplorerKind.References => "folder",
            ExplorerKind.Reference => node.Reference?.IsProject == true ? "fireproj" : "folderbase",
            _ => NameForFile(node.Path),
        };

        public static string NameForFile(string? path) => Path.GetExtension(path ?? "").ToLowerInvariant() switch
        {
            ".fire" => "firescript",
            ".fxml" => "firexml",
            ".biscuit" => "biscuit",
            ".md" => "md",
            ".xmd" => "xmd",
            ".fireproj" => "fireproj",
            ".firesln" => "firesln",
            _ => "file",
        };

        private static Bitmap? Load(string name, int size)
        {
            string key = $"{name}_{size}";
            if (Cache.TryGetValue(key, out var bitmap)) return bitmap;
            try { bitmap = new Bitmap(AssetLoader.Open(new Uri($"avares://spark/Assets/treeicons/{key}.png"))); }
            catch (Exception) { bitmap = null; }   // a missing icon leaves the place empty
            Cache[key] = bitmap;
            return bitmap;
        }

        /// <summary>An image of 16x16 (logical pixels); the picture of 32 px is taken once the scaling of the screen is known and is 1.5 or more.</summary>
        public static Image Image(string name)
        {
            var image = new Image { Width = 16, Height = 16, Source = Load(name, 16), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            image.AttachedToVisualTree += (_, _) =>
            {
                double scaling = TopLevel.GetTopLevel(image)?.RenderScaling ?? 1;
                if (scaling >= 1.5 && Load(name, 32) is { } large) image.Source = large;
            };
            return image;
        }
    }
}
