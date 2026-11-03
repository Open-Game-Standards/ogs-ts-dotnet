#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Ogs.Telemetry
{

/// <summary>One recorded datagram (recording.md § Records).</summary>
public sealed class RecordedDatagram
{
    public RecordedDatagram(long arrivalMicros, IPEndPoint source, byte[] datagram)
    {
        ArrivalMicros = arrivalMicros; Source = source; Datagram = datagram;
    }
    /// <summary>Microseconds since the capture start.</summary>
    public long ArrivalMicros { get; }
    public IPEndPoint Source { get; }
    /// <summary>The datagram, envelope included.</summary>
    public byte[] Datagram { get; }
}

/// <summary>Writes an <c>.ogsrec</c> recording (recording.md). Feed it
/// from <see cref="TelemetryReceiver.RawDatagramReceived"/> via
/// <see cref="Attach"/>, or call <see cref="Write(IPEndPoint, ReadOnlySpan{byte})"/>.
/// Thread-safe.</summary>
public sealed class RecordingWriter : IDisposable
{
    public const int Version = 1;
    internal static readonly byte[] Magic = { (byte)'O', (byte)'G', (byte)'S', (byte)'R' };

    readonly Stream _stream;
    readonly bool _ownsStream;
    readonly object _gate = new();
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public RecordingWriter(Stream stream, long captureStartUnixMicros, bool ownsStream = true)
    {
        _stream = stream; _ownsStream = ownsStream;
        var header = new byte[16];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), Version);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), captureStartUnixMicros);
        _stream.Write(header, 0, header.Length);
    }

    /// <summary>A writer to a new file, its capture start now.</summary>
    public static RecordingWriter Create(string path) =>
        new(File.Create(path), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);

    /// <summary>Records every OMT1 datagram the receiver gets.</summary>
    public RecordingWriter Attach(TelemetryReceiver receiver)
    {
        receiver.RawDatagramReceived += (source, datagram) => Write(source, datagram.Span);
        return this;
    }

    /// <summary>Records a datagram that arrived now.</summary>
    public void Write(IPEndPoint source, ReadOnlySpan<byte> datagram) =>
        Write(_clock.ElapsedTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency, source, datagram);

    /// <summary>Records a datagram with an explicit arrival time (µs since the capture start).</summary>
    public void Write(long arrivalMicros, IPEndPoint source, ReadOnlySpan<byte> datagram)
    {
        var address = source.Address.GetAddressBytes();
        var record = new byte[16 + address.Length + datagram.Length];
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(0), arrivalMicros);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), (uint)datagram.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12), (ushort)source.Port);
        record[14] = (byte)(address.Length == 16 ? 6 : 4);
        address.CopyTo(record, 16);
        datagram.CopyTo(record.AsSpan(16 + address.Length));
        lock (_gate) _stream.Write(record, 0, record.Length);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stream.Flush();
            if (_ownsStream) _stream.Dispose();
        }
    }
}

/// <summary>Reads an <c>.ogsrec</c> recording (recording.md).</summary>
public static class RecordingReader
{
    /// <summary>The capture start (Unix µs) and every record, in order; a
    /// truncated final record is dropped.</summary>
    public static (long CaptureStartUnixMicros, List<RecordedDatagram> Records) Read(Stream stream)
    {
        var header = new byte[16];
        if (ReadFully(stream, header) < 16 || !header.AsSpan(0, 4).SequenceEqual(RecordingWriter.Magic))
            throw new InvalidDataException("not an .ogsrec recording");
        int version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        if (version != RecordingWriter.Version)
            throw new InvalidDataException($"unsupported .ogsrec version {version}");
        long start = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));

        var records = new List<RecordedDatagram>();
        var head = new byte[16];
        while (ReadFully(stream, head) == 16)
        {
            long at = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(0));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
            int port = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(12));
            int addressLength = head[14] switch
            {
                4 => 4,
                6 => 16,
                _ => throw new InvalidDataException($"unknown address family {head[14]}"),
            };
            var address = new byte[addressLength];
            var datagram = new byte[length];
            if (ReadFully(stream, address) < addressLength || ReadFully(stream, datagram) < length)
                break; // a recorder cut off mid-write
            records.Add(new RecordedDatagram(at, new IPEndPoint(new IPAddress(address), port), datagram));
        }
        return (start, records);
    }

    public static (long CaptureStartUnixMicros, List<RecordedDatagram> Records) Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    static int ReadFully(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

/// <summary>Replays a recording at its original pace, one socket per
/// recorded source (recording.md § Replay).</summary>
public static class RecordingReplayer
{
    public static async Task ReplayAsync(IReadOnlyList<RecordedDatagram> records,
        string group = "239.255.17.17", int port = 40123, double speed = 1.0,
        CancellationToken cancellation = default)
    {
        if (records.Count == 0) return;
        if (speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));
        var senders = new Dictionary<IPEndPoint, TelemetrySender>();
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long first = records[0].ArrivalMicros;
            foreach (var r in records)
            {
                double wait = (r.ArrivalMicros - first) / 1e6 / speed - clock.Elapsed.TotalSeconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), cancellation).ConfigureAwait(false);
                if (!senders.TryGetValue(r.Source, out var sender))
                    senders[r.Source] = sender = new TelemetrySender(group, port);
                sender.Send(r.Datagram);
            }
        }
        finally
        {
            foreach (var s in senders.Values) s.Dispose();
        }
    }

    /// <summary>Plays a recording into one receiver's handlers at its
    /// original pace (<see cref="TelemetryReceiver.Inject"/>), each record
    /// from its recorded source. Nothing is sent on the network, so no other
    /// receiver sees it.</summary>
    public static async Task PlayIntoAsync(IReadOnlyList<RecordedDatagram> records,
        TelemetryReceiver receiver, double speed = 1.0, CancellationToken cancellation = default)
    {
        if (records.Count == 0) return;
        if (speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long first = records[0].ArrivalMicros;
        foreach (var r in records)
        {
            double wait = (r.ArrivalMicros - first) / 1e6 / speed - clock.Elapsed.TotalSeconds;
            if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), cancellation).ConfigureAwait(false);
            receiver.Inject(r.Source, r.Datagram);
        }
    }
}
}
