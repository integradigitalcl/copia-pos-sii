using System.Collections.Concurrent;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using GrunflexPOS.HardwareBridge.Models;

namespace GrunflexPOS.HardwareBridge.Hardware;

public sealed class ScannerService : IDisposable
{
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<Guid, Channel<ScannerEvent>> _subscribers = new();
    private readonly StringBuilder _buffer = new();
    private SerialPort? _serial;
    private bool _disposed;

    public ScannerStateResponse GetState()
    {
        lock (_sync)
        {
            return new ScannerStateResponse(
                _serial?.IsOpen == true,
                _serial?.PortName);
        }
    }

    public ScannerStateResponse Connect(
        string port,
        int baudRate,
        int dataBits,
        Parity parity,
        StopBits stopBits,
        Handshake handshake)
    {
        var validatedPort = HardwareValidation.RequireComPort(port);
        HardwareValidation.RequireBaudRate(baudRate);
        HardwareValidation.RequireDataBits(dataBits);

        Disconnect();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _buffer.Clear();
            try
            {
                _serial = new SerialPort(
                    validatedPort, baudRate, parity, dataBits, stopBits)
                {
                    Handshake = handshake,
                    Encoding = Encoding.ASCII,
                    NewLine = "\r\n",
                    ReadTimeout = 200,
                    WriteTimeout = 200
                };
                _serial.DataReceived += SerialDataReceived;
                _serial.Open();
                Publish(new ScannerEvent(
                    "connected", Port: _serial.PortName, At: DateTimeOffset.UtcNow));
                return new ScannerStateResponse(true, _serial.PortName);
            }
            catch (Exception ex)
            {
                DisposeSerialNoLock();
                Publish(new ScannerEvent(
                    "error", Port: validatedPort, Error: ex.Message, At: DateTimeOffset.UtcNow));
                throw;
            }
        }
    }

    public ScannerStateResponse Disconnect()
    {
        lock (_sync)
        {
            var port = _serial?.PortName;
            var wasConnected = _serial is not null;
            DisposeSerialNoLock();
            if (wasConnected)
            {
                Publish(new ScannerEvent(
                    "disconnected", Port: port, At: DateTimeOffset.UtcNow));
            }
            return new ScannerStateResponse(false, null);
        }
    }

    public async IAsyncEnumerable<ScannerEvent> Subscribe(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<ScannerEvent>(
            new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
        _subscribers[id] = channel;
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
                yield return item;
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            DisposeSerialNoLock();
            foreach (var channel in _subscribers.Values)
                channel.Writer.TryComplete();
            _subscribers.Clear();
        }
    }

    private void SerialDataReceived(object? sender, SerialDataReceivedEventArgs args)
    {
        try
        {
            lock (_sync)
            {
                if (_serial?.IsOpen != true)
                    return;

                var incoming = _serial.ReadExisting();
                if (string.IsNullOrWhiteSpace(incoming))
                    return;

                _buffer.Append(incoming);
                var data = _buffer.ToString();
                var parts = data.Split(
                    new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var complete = data.EndsWith('\n') || data.EndsWith('\r');
                _buffer.Clear();

                if (!complete && parts.Length > 0)
                {
                    _buffer.Append(parts[^1]);
                    parts = parts[..^1];
                }

                foreach (var part in parts)
                {
                    var code = part.Trim();
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        Publish(new ScannerEvent(
                            "code", Code: code, Port: _serial.PortName, At: DateTimeOffset.UtcNow));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Publish(new ScannerEvent("error", Error: ex.Message, At: DateTimeOffset.UtcNow));
        }
    }

    private void DisposeSerialNoLock()
    {
        if (_serial is null)
            return;

        _serial.DataReceived -= SerialDataReceived;
        try
        {
            if (_serial.IsOpen)
                _serial.Close();
        }
        finally
        {
            _serial.Dispose();
            _serial = null;
            _buffer.Clear();
        }
    }

    private void Publish(ScannerEvent item)
    {
        foreach (var subscriber in _subscribers.Values)
            subscriber.Writer.TryWrite(item);
    }
}
