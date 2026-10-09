using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace fire.Device.Manager.DeviceManager
{
    public enum PacketDirection
    {
        /// <summary>From the host (script/editor) to the device.</summary>
        HostToDevice = 0,

        /// <summary>From the device to the host.</summary>
        DeviceToHost = 1,
    }

    /// <summary>A captured packet (a sent line or a received block of raw data).</summary>
    public sealed record PacketRecord(DateTime Time, string DeviceIdentifier, PacketDirection Direction, byte[] Data);

    /// <summary>The file format of the packet logs (`.fplog`): a text file, one line per packet, columns separated by tabs
    /// - UTC timestamp (ISO 8601), direction (`H2D`/`D2H`), device identifier, content as hex bytes.
    /// Lines with `#` and empty lines are skipped. Lossless (binary data is preserved) and human-readable.</summary>
    public static class PacketLog
    {
        public const string Header = "# fire-packetlog 1";
        public const string FileExtension = ".fplog";

        public static string ToHex(byte[] data) => string.Join(" ", data.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

        public static string Serialize(IEnumerable<PacketRecord> packets)
        {
            var sb = new StringBuilder();
            sb.Append(Header).Append('\n');
            foreach (var p in packets)
            {
                sb.Append(p.Time.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)).Append('\t')
                  .Append(p.Direction == PacketDirection.HostToDevice ? "H2D" : "D2H").Append('\t')
                  .Append(p.DeviceIdentifier.Replace('\t', ' ').Replace('\n', ' ')).Append('\t')
                  .Append(ToHex(p.Data)).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Reads a log; on a faulty line a FormatException with the line number is thrown.</summary>
        public static List<PacketRecord> Parse(string text)
        {
            var result = new List<PacketRecord>();
            int lineNo = 0;
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                lineNo++;
                string line = raw.TrimEnd('\r');
                if (line.Trim().Length == 0 || line.StartsWith('#')) continue;

                var cols = line.Split('\t');
                if (cols.Length < 3) throw new FormatException($"Line {lineNo}: expected time, direction, device and content (separated by tabs).");

                if (!DateTime.TryParse(cols[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
                    throw new FormatException($"Line {lineNo}: invalid timestamp '{cols[0]}'.");

                PacketDirection direction = cols[1].Trim().ToUpperInvariant() switch
                {
                    "H2D" => PacketDirection.HostToDevice,
                    "D2H" => PacketDirection.DeviceToHost,
                    _ => throw new FormatException($"Line {lineNo}: unknown direction '{cols[1]}' (expected H2D or D2H)."),
                };

                byte[] data;
                try
                {
                    string hex = cols.Length > 3 ? cols[3] : "";
                    data = hex.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => byte.Parse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
                }
                catch (FormatException)
                {
                    throw new FormatException($"Line {lineNo}: invalid content (expected hex bytes like '48 65 6C').");
                }

                result.Add(new PacketRecord(time.ToUniversalTime(), cols[2], direction, data));
            }
            return result;
        }
    }
}
