using System;
using System.Collections.Generic;

namespace fire.Device.Bridge
{
    /// <summary>
    /// Der Empfangspuffer EINES Geräts: die empfangenen Pakete in der Reihenfolge ihres Eintreffens (der Hintergrund-Thread des Geräts hängt sie an, das
    /// Skript liest). Zwei Sichten auf dieselben Bytes: <see cref="TakePacket"/> holt das nächste Paket (bzw. den Rest davon), und
    /// <see cref="TryConsumeThrough"/> sucht eine Bytefolge ÜBER Paketgrenzen hinweg - ein Gerät schickt "ok\n" gern in zwei Stücken - und schneidet den
    /// Puffer hinter dem ersten Treffer ab: alles davor und der Treffer selbst sind verbraucht, was dahinter steht (auch ein zweiter Treffer) bleibt für den
    /// nächsten Aufruf.
    /// </summary>
    internal sealed class ReceiveBuffer
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _packets = new();
        private int _headOffset; // schon verbrauchte Bytes des ersten Pakets

        public void Add(byte[] packet)
        {
            if (packet.Length == 0) return;
            lock (_gate) _packets.Add(packet);
        }

        public bool HasData
        {
            get { lock (_gate) return _packets.Count > 0; }
        }

        /// <summary>Das nächste Paket (nach einem <see cref="TryConsumeThrough"/> der Rest des angebrochenen); null, wenn nichts da ist.</summary>
        public byte[]? TakePacket()
        {
            lock (_gate)
            {
                if (_packets.Count == 0) return null;
                var packet = _packets[0];
                _packets.RemoveAt(0);
                var result = _headOffset == 0 ? packet : packet[_headOffset..];
                _headOffset = 0;
                return result;
            }
        }

        /// <summary>Sucht `pattern` im Puffer; bei einem Treffer wird alles bis einschließlich des ersten Treffers entfernt und true geliefert. Eine leere
        /// Bytefolge gilt immer als gefunden und verbraucht nichts.</summary>
        public bool TryConsumeThrough(byte[] pattern)
        {
            if (pattern.Length == 0) return true;
            lock (_gate)
            {
                if (_packets.Count == 0) return false;

                int total = -_headOffset;
                foreach (var packet in _packets) total += packet.Length;
                var all = new byte[total];
                int pos = 0;
                for (int i = 0; i < _packets.Count; i++)
                {
                    int from = i == 0 ? _headOffset : 0;
                    Buffer.BlockCopy(_packets[i], from, all, pos, _packets[i].Length - from);
                    pos += _packets[i].Length - from;
                }

                int index = all.AsSpan().IndexOf(pattern);
                if (index < 0) return false;

                // `consumed` Bytes (ab dem Anfang des Puffers) sind verbraucht: ganze Pakete entfernen, das angebrochene vorn beschneiden
                int consumed = index + pattern.Length;
                while (_packets.Count > 0)
                {
                    int available = _packets[0].Length - _headOffset;
                    if (consumed >= available)
                    {
                        consumed -= available;
                        _packets.RemoveAt(0);
                        _headOffset = 0;
                    }
                    else
                    {
                        _headOffset += consumed;
                        break;
                    }
                }
                return true;
            }
        }
    }
}
