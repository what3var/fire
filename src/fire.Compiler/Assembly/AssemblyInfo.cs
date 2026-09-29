using fire.Runtime;
using fire.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Compiler.Assembly
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
    }
}
