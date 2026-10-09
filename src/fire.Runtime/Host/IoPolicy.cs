namespace fire.IO.Bridge
{
    /// <summary>What a script wants to do on a path.</summary>
    [Flags]
    public enum IoAccess
    {
        Read = 1,
        Write = 2,
        Delete = 4,
        List = 8,
    }

    /// <summary>
    /// The security policy of the HOST (editor, runtime, an embedded
    /// program) for file system accesses by scripts: every operation on a
    /// path first asks <see cref="IsAllowed"/> with the COMPLETE, already
    /// normalised path (`Path.GetFullPath`, i.e. without `..`). The script
    /// itself can neither see nor change the policy - a denied
    /// access becomes a catchable `IO.PermissionException` in fire.
    ///
    /// Limit: NO symbolic links are resolved - a link INSIDE
    /// an allowed directory that points outward leads out.
    /// Whoever has to rule that out implements a policy of their own.
    /// </summary>
    public abstract class IoPolicy
    {
        /// <summary>`true` if `access` is allowed on `fullPath`; otherwise
        /// `false` and (optionally) a reason for the error message.</summary>
        public abstract bool IsAllowed(string fullPath, IoAccess access, out string? reason);

        /// <summary>Everything allowed - the default if the host
        /// specifies nothing else (like a normal program).</summary>
        public static IoPolicy AllowAll { get; } = new AllowAllPolicy();

        /// <summary>Nichts erlaubt.</summary>
        public static IoPolicy DenyAll { get; } = new DenyAllPolicy();

        /// <summary>Only within `root` (including all subdirectories);
        /// with `readOnly` only reading/listing.</summary>
        public static IoPolicy Rooted(string root, bool readOnly = false) => new RootedPolicy(root, readOnly);

        private sealed class AllowAllPolicy : IoPolicy
        {
            public override bool IsAllowed(string fullPath, IoAccess access, out string? reason)
            {
                reason = null;
                return true;
            }
        }

        private sealed class DenyAllPolicy : IoPolicy
        {
            public override bool IsAllowed(string fullPath, IoAccess access, out string? reason)
            {
                reason = "File access is not allowed for this program.";
                return false;
            }
        }

        private sealed class RootedPolicy : IoPolicy
        {
            private readonly string _root;
            private readonly bool _readOnly;
            private static readonly StringComparison Comparison =
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            public RootedPolicy(string root, bool readOnly)
            {
                _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                _readOnly = readOnly;
            }

            public override bool IsAllowed(string fullPath, IoAccess access, out string? reason)
            {
                string path = Path.TrimEndingDirectorySeparator(fullPath);
                bool inside = path.Equals(_root, Comparison)
                    || path.StartsWith(_root + Path.DirectorySeparatorChar, Comparison)
                    || path.StartsWith(_root + Path.AltDirectorySeparatorChar, Comparison);
                if (!inside)
                {
                    reason = $"The path is outside of the permitted directory '{_root}'.";
                    return false;
                }
                if (_readOnly && (access & (IoAccess.Write | IoAccess.Delete)) != 0)
                {
                    reason = "Read access only.";
                    return false;
                }
                reason = null;
                return true;
            }
        }
    }
}
