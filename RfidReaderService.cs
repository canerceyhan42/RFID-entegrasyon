using System.IO.Ports;
using System.Text;

namespace RfidIntegration;

public sealed class RfidReaderService : IDisposable
{
    private const string ReaderPrefix = "A0112OKD";
    private const int TagNumberLength = 15;
    private const int ChecksumLength = 2;
    private const int MaximumFrameLength = 256;

    private readonly object _sync = new();
    private readonly StringBuilder _buffer = new();
    private SerialPort? _serialPort;
    private bool _disposed;

    public event EventHandler<RfidTagReadEventArgs>? TagRead;
    public event EventHandler<RfidFrameEventArgs>? UnrecognizedFrame;
    public event EventHandler<RfidReaderErrorEventArgs>? Error;

    public bool IsConnected
    {
        get
        {
            lock (_sync)
            {
                return _serialPort?.IsOpen == true;
            }
        }
    }

    public void Connect(string portName, int baudRate = 9600)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        if (baudRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baudRate));
        }

        Disconnect();

        var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 500,
            DtrEnable = true,
            RtsEnable = true
        };
        port.DataReceived += SerialPort_DataReceived;

        lock (_sync)
        {
            _buffer.Clear();
            _serialPort = port;
        }

        try
        {
            port.Open();
        }
        catch
        {
            lock (_sync)
            {
                if (ReferenceEquals(_serialPort, port))
                {
                    _serialPort = null;
                }
            }

            port.DataReceived -= SerialPort_DataReceived;
            port.Dispose();
            throw;
        }
    }

    public void Disconnect()
    {
        SerialPort? port;
        lock (_sync)
        {
            port = _serialPort;
            _serialPort = null;
            _buffer.Clear();
        }

        if (port is null)
        {
            return;
        }

        port.DataReceived -= SerialPort_DataReceived;
        if (port.IsOpen)
        {
            port.Close();
        }

        port.Dispose();
    }

    private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (sender is not SerialPort port)
        {
            return;
        }

        List<string> frames = [];
        List<string> unrecognizedFrames = [];
        try
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_serialPort, port) || !port.IsOpen)
                {
                    return;
                }

                _buffer.Append(port.ReadExisting());
                ExtractFrames(frames, unrecognizedFrames);
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, new RfidReaderErrorEventArgs(ex));
            return;
        }

        foreach (string frame in unrecognizedFrames)
        {
            UnrecognizedFrame?.Invoke(this, new RfidFrameEventArgs(frame));
        }

        foreach (string frame in frames)
        {
            if (TryParseTagNumber(frame, out string tagNumber))
            {
                TagRead?.Invoke(this, new RfidTagReadEventArgs(tagNumber, frame));
            }
            else
            {
                UnrecognizedFrame?.Invoke(this, new RfidFrameEventArgs(frame));
            }
        }
    }

    private void ExtractFrames(List<string> frames, List<string> unrecognizedFrames)
    {
        while (true)
        {
            int startIndex = IndexOf('$');
            if (startIndex < 0)
            {
                _buffer.Clear();
                return;
            }

            if (startIndex > 0)
            {
                _buffer.Remove(0, startIndex);
            }

            int endIndex = IndexOf('#', 1);
            if (endIndex < 0)
            {
                if (_buffer.Length > MaximumFrameLength)
                {
                    unrecognizedFrames.Add(_buffer.ToString());
                    _buffer.Clear();
                }

                return;
            }

            frames.Add(_buffer.ToString(0, endIndex + 1));
            _buffer.Remove(0, endIndex + 1);
        }
    }

    private int IndexOf(char value, int startIndex = 0)
    {
        for (int index = startIndex; index < _buffer.Length; index++)
        {
            if (_buffer[index] == value)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryParseTagNumber(string frame, out string tagNumber)
    {
        tagNumber = string.Empty;
        if (frame.Length < 2 || frame[0] != '$' || frame[^1] != '#')
        {
            return false;
        }

        string payload = frame.Substring(1, frame.Length - 2);
        if (!payload.StartsWith(ReaderPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string encodedTag = payload[ReaderPrefix.Length..];
        if (encodedTag.Length != TagNumberLength + ChecksumLength)
        {
            return false;
        }

        string candidate = encodedTag[..TagNumberLength];
        if (candidate.Any(character => !char.IsAsciiDigit(character)))
        {
            return false;
        }

        string checksum = encodedTag[TagNumberLength..];
        if (checksum.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        tagNumber = candidate;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Disconnect();
        _disposed = true;
    }
}

public sealed class RfidTagReadEventArgs : EventArgs
{
    public string TagNumber { get; }
    public string RawData { get; }
    public DateTimeOffset Timestamp { get; }

    public RfidTagReadEventArgs(string tagNumber, string rawData)
    {
        TagNumber = tagNumber;
        RawData = rawData;
        Timestamp = DateTimeOffset.Now;
    }
}

public sealed class RfidFrameEventArgs : EventArgs
{
    public string RawData { get; }

    public RfidFrameEventArgs(string rawData)
    {
        RawData = rawData;
    }
}

public sealed class RfidReaderErrorEventArgs : EventArgs
{
    public Exception Exception { get; }

    public RfidReaderErrorEventArgs(Exception exception)
    {
        Exception = exception;
    }
}
