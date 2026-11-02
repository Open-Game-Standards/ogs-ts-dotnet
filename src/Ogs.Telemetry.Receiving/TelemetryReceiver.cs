#nullable enable
using System;
using System.Threading.Tasks;
using System.Threading;
using System.Net;
using System.Net.Sockets;

namespace Ogs.Telemetry
{

/// <summary>
/// OGS-TS UDP-multicast receiver. Joins the group and demuxes by frame
/// type. Foreign packets (bad magic) and unknown frame types are skipped
/// silently per the evolution rules; neither decode failures nor exceptions
/// thrown by event handlers kill the receive loop.
///
/// The CapnpGen readers return <c>null</c> for a Text field whose pointer
/// is null, which any valid sender may emit (implementation-capnproto.md,
/// Encoding rules: receivers read null as the default) — handlers should
/// read text as <c>reader.Name ?? ""</c>.
/// </summary>
public sealed class TelemetryReceiver : IDisposable
{
    readonly Socket _socket;
    readonly CancellationTokenSource _cts = new();
    Task? _loop;
    bool _disposed;

    public event Action<CapnpGen.DiscoveryFrame.READER>? DiscoveryReceived;
    public event Action<CapnpGen.MotionFrame.READER>? MotionReceived;
    public event Action<CapnpGen.EventFrame.READER>? EventReceived;

    /// <summary>The same frames with the sender they came from: its source
    /// address and port, which identify a sender (implementation-udp.md §
    /// Senders and sessions). Receivers that may see more than one sender
    /// key their state by it — <see cref="SessionTracker"/> does.</summary>
    public event Action<IPEndPoint, CapnpGen.DiscoveryFrame.READER>? DiscoveryReceivedFrom;
    public event Action<IPEndPoint, CapnpGen.MotionFrame.READER>? MotionReceivedFrom;
    public event Action<IPEndPoint, CapnpGen.EventFrame.READER>? EventReceivedFrom;

    /// <summary>Every OMT1 datagram as it arrives, before it is decoded:
    /// frame type and length in bytes, envelope included — for receivers
    /// that check the 1,400-byte datagram budget (implementation-udp.md §
    /// Datagram budget). Raised on the receive thread.</summary>
    public event Action<FrameType, int>? DatagramReceived;

    /// <summary><see cref="DatagramReceived"/> with the sender's source
    /// address and port, for per-sender checks such as the sender size
    /// envelope (implementation-udp.md § Sender size envelope).</summary>
    public event Action<IPEndPoint, FrameType, int>? DatagramReceivedFrom;

    /// <summary>Completes when the receive loop has exited — after Dispose,
    /// or if it ever faults. Dispose observes faults, so a clean shutdown
    /// completes successfully; hosts may await this.</summary>
    public Task Completion => _loop ?? Task.CompletedTask;

    public long ForeignSkipped { get; private set; }
    public long DecodeErrors { get; private set; }

    /// <summary>Exceptions thrown by a DiscoveryReceived/MotionReceived/
    /// EventReceived handler. The frame is dropped and the loop keeps
    /// receiving; subscribe to surface handler bugs.</summary>
    public event Action<Exception>? HandlerFaulted;
    public long HandlerErrors { get; private set; }

    /// <param name="localInterface">NIC to join the group on, as that
    /// interface's local IPv4 address. Left null, the OS default route
    /// picks the interface — on a multi-homed host that can silently join
    /// on an interface with no path to the LAN. Pass the interface you
    /// want when the host has more than one.</param>
    public TelemetryReceiver(string group = "239.255.17.17", int port = 40123,
                     IPAddress? localInterface = null)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        TryEnableReusePort(_socket);
        _socket.Bind(new IPEndPoint(IPAddress.Any, port));
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
            localInterface is null
                ? new MulticastOption(IPAddress.Parse(group))
                : new MulticastOption(IPAddress.Parse(group), localInterface));
    }

    // SO_REUSEADDR alone lets a second bind to the same port succeed on
    // Windows and Linux, but on macOS/BSD a second multicast listener
    // still won't receive traffic without SO_REUSEPORT too (there's no
    // SocketOptionName for it, so it's a raw setsockopt call). Best-effort:
    // a lone receiver works fine without it, so failures are swallowed.
    // Skipped on netstandard (no SetRawSocketOption there) — Unity/Godot
    // vendor this file but only ever run a single in-process receiver.
    static void TryEnableReusePort(Socket socket)
    {
#if NET6_0_OR_GREATER
        try
        {
            byte[] enable = { 1, 0, 0, 0 };
            if (OperatingSystem.IsLinux())
                socket.SetRawSocketOption(1 /* SOL_SOCKET */, 15 /* SO_REUSEPORT */, enable);
            else if (OperatingSystem.IsMacOS())
                socket.SetRawSocketOption(0xffff /* SOL_SOCKET */, 0x0200 /* SO_REUSEPORT */, enable);
        }
        catch (SocketException)
        {
        }
#endif
    }

    public void Start() => _loop ??= Task.Run(() => ReceiveLoop(_cts.Token));

    void ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[65536];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                int len;
                if (_socket.Available == 0)
                {
                    if (!_socket.Poll(200_000, SelectMode.SelectRead)) continue; // 200 ms
                }
                len = _socket.ReceiveFrom(buf, ref from);
                if (!Envelope.TryUnwrap(buf.AsMemory(0, len), out var type, out var payload))
                {
                    ForeignSkipped++;
                    continue;
                }
                var src = (IPEndPoint)from;
                var source = new IPEndPoint(src.Address, src.Port);
                try
                {
                    DatagramReceived?.Invoke(type, len);
                    DatagramReceivedFrom?.Invoke(source, type, len);
                }
                catch (Exception ex) { HandlerErrors++; try { HandlerFaulted?.Invoke(ex); } catch (Exception) { } }
                var root = MessageDecoder.DecodeRoot(payload.Span);
                Dispatch(type, root, source);
            }
            catch (Capnp.DeserializationException)
            {
                DecodeErrors++;
            }
            catch (SocketException)
            {
                // transient; keep receiving
            }
            catch (ObjectDisposedException)
            {
                break; // socket closed by Dispose — clean shutdown, don't fault
            }
        }
    }

    void Dispatch(FrameType type, Capnp.DeserializerState root, IPEndPoint source)
    {
        try
        {
            switch (type)
            {
                case FrameType.Discovery:
                {
                    var d = CapnpGen.DiscoveryFrame.READER.create(root);
                    DiscoveryReceived?.Invoke(d);
                    DiscoveryReceivedFrom?.Invoke(source, d);
                    break;
                }
                case FrameType.Motion:
                {
                    var m = CapnpGen.MotionFrame.READER.create(root);
                    MotionReceived?.Invoke(m);
                    MotionReceivedFrom?.Invoke(source, m);
                    break;
                }
                case FrameType.Event:
                {
                    var e = CapnpGen.EventFrame.READER.create(root);
                    EventReceived?.Invoke(e);
                    EventReceivedFrom?.Invoke(source, e);
                    break;
                }
            }
        }
        catch (Capnp.DeserializationException)
        {
            throw; // malformed message read lazily by a handler: a decode error
        }
        catch (Exception ex)
        {
            HandlerErrors++;
            try { HandlerFaulted?.Invoke(ex); } catch (Exception) { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _socket.Close(); } catch (SocketException) { }
        // Cancel alone doesn't interrupt a pending Poll — the closed socket
        // does. Drain the loop (it exits immediately) and observe any
        // residual fault so it never surfaces as an unobserved task
        // exception; only then dispose the token source.
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
}
