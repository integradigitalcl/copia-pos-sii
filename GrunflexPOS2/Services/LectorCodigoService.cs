using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;

namespace GrunflexPOS2.Services
{
    public static class LectorCodigoService
    {
        private static SerialPort? _serial;
        private static readonly StringBuilder _buffer = new();

        public static event Action<string>? CodigoRecibido;
        public static event Action<bool, string?>? EstadoConexionCambiado;

        public static bool EstaConectado => _serial?.IsOpen == true;
        public static string? PuertoActual => _serial?.PortName;

        public static IReadOnlyList<string> ObtenerPuertos() =>
            SerialPort.GetPortNames().OrderBy(x => x).ToList();

        public static bool ProbarConexion(
            string puerto,
            int baudRate,
            int dataBits,
            Parity parity,
            StopBits stopBits,
            Handshake handshake,
            out string error)
        {
            try
            {
                using var test = new SerialPort(puerto, baudRate, parity, dataBits, stopBits)
                {
                    Handshake = handshake,
                    ReadTimeout = 200,
                    WriteTimeout = 200
                };
                test.Open();
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static void IniciarDesdeConfiguracion(ConfiguracionService cfg)
        {
            if (cfg.Get("lector_serial_habilitado") != "true")
            {
                Detener();
                return;
            }

            string puerto = cfg.Get("lector_serial_puerto");
            if (string.IsNullOrWhiteSpace(puerto))
                return;

            int baud = ParseInt(cfg.Get("lector_serial_baud"), 9600);
            int dataBits = ParseInt(cfg.Get("lector_serial_databits"), 8);
            var parity = ParseEnum(cfg.Get("lector_serial_parity"), Parity.None);
            var stopBits = ParseEnum(cfg.Get("lector_serial_stopbits"), StopBits.One);
            var handshake = ParseEnum(cfg.Get("lector_serial_handshake"), Handshake.None);

            Iniciar(puerto, baud, dataBits, parity, stopBits, handshake);
        }

        public static void Iniciar(
            string puerto,
            int baudRate,
            int dataBits,
            Parity parity,
            StopBits stopBits,
            Handshake handshake)
        {
            Detener();
            _buffer.Clear();

            try
            {
                _serial = new SerialPort(puerto, baudRate, parity, dataBits, stopBits)
                {
                    Handshake = handshake,
                    Encoding = Encoding.ASCII,
                    NewLine = "\r\n"
                };
                _serial.DataReceived += Serial_DataReceived;
                _serial.Open();
                EstadoConexionCambiado?.Invoke(true, _serial.PortName);
            }
            catch (Exception ex)
            {
                Detener();
                EstadoConexionCambiado?.Invoke(false, ex.Message);
            }
        }

        public static void Detener()
        {
            try
            {
                if (_serial != null)
                    _serial.DataReceived -= Serial_DataReceived;
                if (_serial?.IsOpen == true)
                    _serial.Close();
                _serial?.Dispose();
            }
            catch
            {
                // noop
            }
            finally
            {
                if (_serial != null)
                    EstadoConexionCambiado?.Invoke(false, null);
                _serial = null;
            }
        }

        private static void Serial_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                if (_serial == null || !_serial.IsOpen)
                    return;

                var incoming = _serial.ReadExisting();
                if (string.IsNullOrWhiteSpace(incoming))
                    return;

                lock (_buffer)
                {
                    _buffer.Append(incoming);
                    var data = _buffer.ToString();
                    var partes = data.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                    // Si no termina en salto de línea, conservar el último fragmento.
                    bool completo = data.EndsWith("\n", StringComparison.Ordinal) || data.EndsWith("\r", StringComparison.Ordinal);
                    _buffer.Clear();

                    if (!completo && partes.Length > 0)
                    {
                        _buffer.Append(partes[^1]);
                        partes = partes.Take(partes.Length - 1).ToArray();
                    }

                    foreach (var p in partes)
                    {
                        var codigo = p.Trim();
                        if (!string.IsNullOrWhiteSpace(codigo))
                            CodigoRecibido?.Invoke(codigo);
                    }
                }
            }
            catch
            {
                // Evitar romper app por lector.
            }
        }

        private static int ParseInt(string text, int def) =>
            int.TryParse(text, out var n) ? n : def;

        private static T ParseEnum<T>(string text, T def) where T : struct
        {
            if (Enum.TryParse<T>(text, true, out var val))
                return val;
            return def;
        }
    }
}
