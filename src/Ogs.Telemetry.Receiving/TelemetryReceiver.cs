#nullable enable
using System;
using System.Threading.Tasks;
using System.Threading;
using System.Net;
using System.Net.NetworkInformation;
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

    /// <summary>Every OMT1 datagram as received, envelope included, with
    /// its source, for recorders (RecordingWriter). The memory is the
    /// receive buffer: copy it to keep it past the handler.</summary>
    public event Action<IPEndPoint, ReadOnlyMemory<byte>>? RawDatagramReceived;

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

    /// <summary>The group for traffic pinned to the loopback interface: demos
    /// and tests. Never send over loopback on the standard group: on Windows
    /// that moves the group's default-route traffic on this host onto
    /// loopback, whatever the port, and it stays moved after the sender
    /// closes, so a game's stream stops reaching other machines.</summary>
    public const string LoopbackGroup = "239.255.17.99";
    /// <summary>The IPv6 counterpart of <see cref="LoopbackGroup"/>.</summary>
    public const string LoopbackGroupV6 = "ff12::4f47:5499";

    /// <param name="group">IPv4 group (default) or the IPv6 group
    /// (<see cref="TelemetrySender.DefaultGroupV6"/>); receivers SHOULD run
    /// one of each so IPv6-only senders are found.</param>
    /// <param name="localInterface">NIC to join the group on, as that
    /// interface's local IPv4 address (IPv6: an address whose ScopeId is
    /// the interface index). Left null, it joins on the default route's
    /// interface (<see cref="TelemetrySender.DefaultRouteAddress"/>,
    /// <see cref="TelemetrySender.DefaultInterfaceIndexV6"/>) — on a
    /// multi-homed host that can silently join on an interface with no path
    /// to the LAN. Pass the interface you want when the host has more than
    /// one. A receiver also joins on loopback, so it keeps hearing senders
    /// on this host if their traffic moves there (see
    /// <see cref="LoopbackGroup"/>).</param>
    public TelemetryReceiver(string group = "239.255.17.17", int port = 40123,
                     IPAddress? localInterface = null)
    {
        var address = IPAddress.Parse(group);
        bool v6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        _socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        TryEnableReusePort(_socket);
        _socket.Bind(new IPEndPoint(v6 ? IPAddress.IPv6Any : IPAddress.Any, port));
        if (v6)
            JoinIPv6(address, localInterface);
        else
            JoinIPv4(address, localInterface);
    }

    void JoinIPv6(IPAddress group, IPAddress? localInterface)
    {
        // link-local group: join on the interface whose index is the
        // address's ScopeId (0 = the system's default interface)
        long index = localInterface?.ScopeId ?? TelemetrySender.DefaultInterfaceIndexV6() ?? 0;
        _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership,
            new IPv6MulticastOption(group, index));
        // Best-effort, as for IPv4
        try
        {
            long loopback = NetworkInterface.IPv6LoopbackInterfaceIndex;
            if (index != loopback)
                _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership,
                    new IPv6MulticastOption(group, loopback));
        }
        catch (Exception ex) when (ex is SocketException or NetworkInformationException or PlatformNotSupportedException)
        {
        }
    }

    void JoinIPv4(IPAddress group, IPAddress? localInterface)
    {
        if (localInterface is not null)
        {
            Join(new MulticastOption(group, localInterface));
        }
        else if (!TryJoinOnDefaultRoute(group))
        {
            Join(new MulticastOption(group)); // the OS chooses
        }
        // Best-effort: Linux often ships with multicast off on loopback, and
        // the OS's own choice above may already have been loopback.
        if (localInterface is null || !IPAddress.IsLoopback(localInterface))
        {
            try { Join(new MulticastOption(group, IPAddress.Loopback)); }
            catch (SocketException) { }
        }
    }

    bool TryJoinOnDefaultRoute(IPAddress group)
    {
        // Named rather than left to the OS: once a group's traffic has moved
        // to loopback (see LoopbackGroup), the OS's choice for it is loopback
        // too, and senders on other machines would go unheard.
        var nic = TelemetrySender.DefaultRouteAddress();
        if (nic is null) return false;
        try
        {
            Join(new MulticastOption(group, nic));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    void Join(MulticastOption option) =>
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, option);

    // SO_REUSEADDR alone lets a second bind to the same port succeed on
    // Windows and Linux, but on macOS/BSD a second multicast listener
    // still won't receive traffic without SO_REUSEPORT too (there's no
    // SocketOptionName for it, so it's a raw setsockopt call). Best-effort:
    // a lone receiver works fine without it, so failures are swallowed.
    // Compiled out where SetRawSocketOption doesn't exist.
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
        EndPoint from = new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Any : IPAddress.Any, 0);
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
                var src = (IPEndPoint)from;
                Handle(new IPEndPoint(src.Address, src.Port), buf.AsMemory(0, len));
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

    /// <summary>Handles a datagram as if it had arrived from
    /// <paramref name="source"/>: the same events, counters and decoding as
    /// the receive loop, with no network involved. For replaying a recording
    /// into this receiver's handlers (recording.md) without sending it to
    /// other receivers. Call from one thread at a time, and not while live
    /// datagrams are arriving unless the handlers tolerate both.</summary>
    public void Inject(IPEndPoint source, ReadOnlyMemory<byte> datagram) => Handle(source, datagram);

    void Handle(IPEndPoint source, ReadOnlyMemory<byte> datagram)
    {
        if (!Envelope.TryUnwrap(datagram, out var type, out var payload))
        {
            ForeignSkipped++;
            return;
        }
        try
        {
            DatagramReceived?.Invoke(type, datagram.Length);
            DatagramReceivedFrom?.Invoke(source, type, datagram.Length);
            RawDatagramReceived?.Invoke(source, datagram);
        }
        catch (Exception ex) { HandlerErrors++; try { HandlerFaulted?.Invoke(ex); } catch (Exception) { } }
        try
        {
            var root = MessageDecoder.DecodeRoot(payload.Span);
            Dispatch(type, root, source);
        }
        catch (Capnp.DeserializationException)
        {
            DecodeErrors++;
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
