namespace fire.IO.Bridge
{
    /// <summary>Was ein Skript an einem Pfad tun will.</summary>
    [Flags]
    public enum IoAccess
    {
        Read = 1,
        Write = 2,
        Delete = 4,
        List = 8,
    }

    /// <summary>
    /// Die Sicherheitsrichtlinie des HOSTS (Editor, Runtime, ein eingebettetes
    /// Programm) für Dateisystemzugriffe von Skripten: jede Operation auf einem
    /// Pfad fragt vorher <see cref="IsAllowed"/> mit dem VOLLSTÄNDIGEN, schon
    /// normalisierten Pfad (`Path.GetFullPath`, also ohne `..`). Das Skript
    /// selbst kann die Richtlinie weder sehen noch ändern - ein abgelehnter
    /// Zugriff wird in fire zu einer fangbaren `IO.PermissionException`.
    ///
    /// Grenze: es werden KEINE symbolischen Links aufgelöst - ein Link INNERHALB
    /// eines erlaubten Verzeichnisses, der nach außen zeigt, führt heraus.
    /// Wer das ausschließen muss, implementiert eine eigene Richtlinie.
    /// </summary>
    public abstract class IoPolicy
    {
        /// <summary>`true`, wenn `access` auf `fullPath` erlaubt ist; sonst
        /// `false` und (optional) ein Grund für die Fehlermeldung.</summary>
        public abstract bool IsAllowed(string fullPath, IoAccess access, out string? reason);

        /// <summary>Alles erlaubt - Vorgabe, wenn der Host nichts anderes
        /// festlegt (wie ein normales Programm).</summary>
        public static IoPolicy AllowAll { get; } = new AllowAllPolicy();

        /// <summary>Nichts erlaubt.</summary>
        public static IoPolicy DenyAll { get; } = new DenyAllPolicy();

        /// <summary>Nur innerhalb von `root` (samt allen Unterverzeichnissen);
        /// mit `readOnly` nur Lesen/Auflisten.</summary>
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
                reason = "Dateizugriff ist für dieses Programm nicht erlaubt.";
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
                    reason = $"Pfad liegt außerhalb des erlaubten Verzeichnisses '{_root}'.";
                    return false;
                }
                if (_readOnly && (access & (IoAccess.Write | IoAccess.Delete)) != 0)
                {
                    reason = "Nur Lesezugriff erlaubt.";
                    return false;
                }
                reason = null;
                return true;
            }
        }
    }
}
