namespace fire.Runtime
{
    /// <summary>What a script wants to do on the network.</summary>
    [Flags]
    public enum NetAccess
    {
        /// <summary>Connect to a host (TCP), or send a datagram to it (UDP).</summary>
        Connect = 1,
        /// <summary>Listen for connections (TCP) or bind a datagram socket (UDP) - the host is the local address ("" = every interface).</summary>
        Listen = 2,
        /// <summary>Look up the addresses of a name (the port is 0).</summary>
        Resolve = 4,
    }

    /// <summary>
    /// The network policy of the HOST (editor, runtime, an embedding program) for scripts that use the net package (docs/NETWORK.md): every connection, listener, datagram and name lookup asks
    /// <see cref="IsAllowed"/> first. The script can neither see nor change the policy; a refused access becomes a catchable <c>Net.PermissionException</c> in fire. Like
    /// <see cref="fire.IO.Bridge.IoPolicy"/> for files. The default of a program started by hand is <see cref="AllowAll"/>.
    /// </summary>
    public abstract class NetPolicy
    {
        /// <summary>True if <paramref name="access"/> on <paramref name="host"/>:<paramref name="port"/> is allowed; otherwise false and (optionally) a reason for the message.</summary>
        public abstract bool IsAllowed(string host, int port, NetAccess access, out string? reason);

        /// <summary>Everything is allowed.</summary>
        public static NetPolicy AllowAll { get; } = new AllowAllPolicy();

        /// <summary>Nothing is allowed.</summary>
        public static NetPolicy DenyAll { get; } = new DenyAllPolicy();

        /// <summary>Only this computer: connecting to, listening on and resolving <c>localhost</c>, <c>127.x.x.x</c> and <c>::1</c> (listening on every interface, host "", is refused).</summary>
        public static NetPolicy LoopbackOnly { get; } = new LoopbackPolicy();

        /// <summary>Only the listed hosts. A rule is <c>host</c> (any port), <c>host:port</c> or <c>*:port</c> (any host); <c>*</c> alone is every host. Connecting and resolving follow the rules; listening is
        /// refused unless <paramref name="allowListen"/> (then only on the loopback addresses, as with <see cref="LoopbackOnly"/>).</summary>
        public static NetPolicy Hosts(IEnumerable<string> rules, bool allowListen = false) => new HostsPolicy(rules, allowListen);

        internal static bool IsLoopback(string host)
        {
            host = host.Trim().TrimStart('[').TrimEnd(']');
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (System.Net.IPAddress.TryParse(host, out var ip)) return System.Net.IPAddress.IsLoopback(ip);
            return false;
        }

        private sealed class AllowAllPolicy : NetPolicy
        {
            public override bool IsAllowed(string host, int port, NetAccess access, out string? reason) { reason = null; return true; }
        }

        private sealed class DenyAllPolicy : NetPolicy
        {
            public override bool IsAllowed(string host, int port, NetAccess access, out string? reason) { reason = "Network access is not allowed for this program."; return false; }
        }

        private sealed class LoopbackPolicy : NetPolicy
        {
            public override bool IsAllowed(string host, int port, NetAccess access, out string? reason)
            {
                if (IsLoopback(host)) { reason = null; return true; }
                reason = "This program may only use the network of this computer (localhost).";
                return false;
            }
        }

        private sealed class HostsPolicy : NetPolicy
        {
            private readonly List<(string Host, int Port)> _rules = new();
            private readonly bool _allowListen;

            public HostsPolicy(IEnumerable<string> rules, bool allowListen)
            {
                _allowListen = allowListen;
                foreach (string raw in rules)
                {
                    string rule = raw.Trim();
                    int colon = rule.LastIndexOf(':');
                    if (colon > 0 && int.TryParse(rule.AsSpan(colon + 1), out int port) && !rule.Contains("::")) _rules.Add((rule.Substring(0, colon), port));
                    else _rules.Add((rule, 0));
                }
            }

            public override bool IsAllowed(string host, int port, NetAccess access, out string? reason)
            {
                if ((access & NetAccess.Listen) != 0)
                {
                    reason = _allowListen && IsLoopback(host) ? null : "This program may not listen for connections.";
                    return reason == null;
                }
                foreach (var (ruleHost, rulePort) in _rules)
                {
                    bool hostOk = ruleHost == "*" || ruleHost.Equals(host, StringComparison.OrdinalIgnoreCase);
                    bool portOk = rulePort == 0 || rulePort == port || (access & NetAccess.Resolve) != 0;
                    if (hostOk && portOk) { reason = null; return true; }
                }
                reason = $"This program may not use the network address '{host}{(port > 0 ? ":" + port : "")}'.";
                return false;
            }
        }
    }
}
