using fire.Tooling;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Tooling
{
    public class AssemblyInfo : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        
        public VmExecutionMode ExecutionMode { get; set; } = VmExecutionMode.Release;

        public SubsystemType Subsystem { get; set; } = SubsystemType.Console;

        public int? FileMajor { get; set; }

        public int? FileMinor { get; set; }

        public int? FileBuild { get; set; }

        public int? FileRevision { get; set; }

        public int? ProductMajor { get; set; }

        public int? ProductMinor { get; set; }

        public int? ProductBuild { get; set; }

        public int? ProductRevision { get; set; }


        public string? IconPath { get; set; }


        public string? CompanyName { get; set; }

        public string? FileDescription { get; set; }

        public string? OriginalFilename { get; set; }

        public string? ProductName { get; set; }

        public string? LegalCopyright { get; set; }

        public string? InternalName { get; set; }

        public string? Comments { get; set; }

        public string FileVersion
        {
            get
            {
                var major = FileMajor;
                var minor = FileMinor;
                var build = FileBuild;
                var rev = FileRevision;

                if (major == null || minor == null || build == null || rev == null)
                    return "0.0.0.0";

                return $"{major}.{minor}.{build}.{rev}";
            }
            set
            {
                var numbers = value?.Split('.');

                if (numbers?.Length != 4)
                    return;

                if (!int.TryParse(numbers[0], out var major))
                    return;

                if (!int.TryParse(numbers[1], out var minor))
                    return;

                if (!int.TryParse(numbers[2], out var build))
                    return;

                if (!int.TryParse(numbers[3], out var rev))
                    return;

                FileMajor = major;
                FileMinor = minor;
                FileBuild = build;
                FileRevision = rev;
            }
        }

        public string ProductVersion
        {
            get
            {
                var major = ProductMajor;
                var minor = ProductMinor;
                var build = ProductBuild;
                var rev = ProductRevision;

                if (major == null || minor == null || build == null || rev == null)
                    return "0.0.0.0";

                return $"{major}.{minor}.{build}.{rev}";
            }
            set
            {
                var numbers = value?.Split('.');

                if (numbers?.Length != 4)
                    return;

                if (!int.TryParse(numbers[0], out var major))
                    return;

                if (!int.TryParse(numbers[1], out var minor))
                    return;

                if (!int.TryParse(numbers[2], out var build))
                    return;

                if (!int.TryParse(numbers[3], out var rev))
                    return;

                ProductMajor = major;
                ProductMinor = minor;
                ProductBuild = build;
                ProductRevision = rev;
            }
        }

        public void CopyTo(AssemblyInfo model)
        {
            model.Comments = this.Comments;
            model.CompanyName = this.CompanyName;
            model.FileDescription = this.FileDescription;
            model.InternalName = this.InternalName;
            model.LegalCopyright = this.LegalCopyright;
            model.ProductName = this.ProductName;

            model.IconPath = this.IconPath;

            model.ExecutionMode = this.ExecutionMode;

            model.Subsystem = this.Subsystem;

            model.FileMajor = this.FileMajor;
            model.FileMinor = this.FileMinor;
            model.FileBuild = this.FileBuild;
            model.FileRevision = this.FileRevision;

            model.ProductMajor = this.ProductMajor;
            model.ProductMinor = this.ProductMinor;
            model.ProductBuild = this.ProductBuild;
            model.ProductRevision = this.ProductRevision;
        }

        public PeVersionInfo ToVersionInfo()
        {
            var info = new PeVersionInfo();

            info.ProductVersion = new Version(ProductVersion);

            if (FileVersion != "0.0.0.0")
                info.FileVersion = new Version(FileVersion);
            else
                info.FileVersion = new Version(ProductVersion);

            info.Subsystem = Subsystem;

            info.Comments = Comments;
            info.CompanyName = CompanyName;
            info.FileDescription = FileDescription;
            info.InternalName = InternalName;
            info.LegalCopyright = LegalCopyright;
            info.OriginalFilename = OriginalFilename;
            info.ProductName = ProductName;

            return info;
        }

        private static readonly System.Text.RegularExpressions.Regex StringDirective =
            new(@"^\s*#(name|codename|description|author|comments|icon|version|fileversion)[ \t]+""((?:[^""\\]|\\.)*)""\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex FlagDirective =
            new(@"^\s*#(noconsole|debug|performance)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>The settings of the build that the directives of a script set (`#name "..."`, `#version "1.2.3.4"`, `#noconsole`, `#debug`, ...: the dialog of the build
        /// settings shows them). Read from the lines of the text - not through the preprocessor (forge's): a directive in a branch of `#if` or in an `#include`d file counts
        /// as written; the build itself takes what forge reads.</summary>
        public static AssemblyInfo FromSource(string text)
        {
            var info = new AssemblyInfo { Subsystem = SubsystemType.Console, ExecutionMode = VmExecutionMode.Release, FileVersion = "0.0.0.0", ProductVersion = "0.0.0.0" };
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                var flag = FlagDirective.Match(line);
                if (flag.Success)
                {
                    switch (flag.Groups[1].Value)
                    {
                        case "noconsole": info.Subsystem = SubsystemType.GUI; break;
                        case "debug": info.ExecutionMode = VmExecutionMode.Debug; break;
                        case "performance": info.ExecutionMode = VmExecutionMode.Performance; break;
                    }
                    continue;
                }
                var m = StringDirective.Match(line);
                if (!m.Success) continue;
                string value = System.Text.RegularExpressions.Regex.Unescape(m.Groups[2].Value);
                switch (m.Groups[1].Value)
                {
                    case "name": info.ProductName = value; break;
                    case "codename": info.InternalName = value; break;
                    case "description": info.FileDescription = value; break;
                    case "author": info.CompanyName = value; break;
                    case "comments": info.Comments = value; break;
                    case "icon": info.IconPath = value; break;
                    case "version": info.ProductVersion = value; break;
                    case "fileversion": info.FileVersion = value; break;
                }
            }
            return info;
        }
    }
}
