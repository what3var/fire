using System;
using System.Collections.Generic;
using System.Text;

namespace fire.Device.Manager.Drivers
{
    public interface IDevice : IDisposable
    {
        DeviceAvailability Availability { get; }

        bool IsConnected { get; }

        string? PortName { get; }

        /// <summary>Empfangene Rohdaten. Wird vom EIGENEN Thread des Geräts aufgerufen. Mehrere Interessenten
        /// hängen sich mit `+=` ein (Skript-Brücke, Paketverfolgung des DeviceManagers) - ein direktes `=`
        /// würde die anderen verdrängen.</summary>
        Action<byte[]>? OnRawDataReceived { get; set; }

        /// <summary>Gesendete Rohdaten (die Bytes, wie sie wirklich auf die Leitung gehen, also inklusive Zeilenende).
        /// Gleiche Regeln wie <see cref="OnRawDataReceived"/>.</summary>
        Action<byte[]>? OnRawDataSent { get; set; }

        /// <summary>Verbindungs- oder Verfügbarkeitsstatus hat sich geändert (Verbinden, Trennen, Verbindungsverlust,
        /// Verfügbarkeitsprüfung). Kann auf einem Hintergrund-Thread feuern.</summary>
        event Action? StateChanged;

        DeviceAvailability TestAvailability();
        void Connect();
        void Disconnect();

        /// <summary>Sendet `command` (mit Zeilenende). false, wenn nicht verbunden - nichts wurde gesendet.</summary>
        bool SendCommand(string command);

        /// <summary>Sendet `data` genau so, wie sie sind (kein Zeilenende, keine Umkodierung). false, wenn nicht verbunden - nichts wurde gesendet.</summary>
        bool Write(byte[] data);
    }
}
