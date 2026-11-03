#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace Ogs.Telemetry
{

/// <summary>
/// Tells several senders on one network apart (implementation-udp.md §
/// Senders and sessions). A sender is a source address and port; each has
/// its own <see cref="DescriptorTables"/>, so ids never resolve across
/// senders. A session is a sender plus <c>sessionStartUnixUs</c>.
///
/// <see cref="Selected"/> applies the standard's choice for a receiver that
/// drives one output: the pinned identity when one is set, otherwise a
/// sender on this host first, then the session that started most recently.
/// Monitors and loggers read <see cref="Sessions"/> instead.
///
/// Feed it from <see cref="TelemetryReceiver"/> (<see cref="Attach"/>) or by
/// calling <see cref="OnDiscovery"/> / <see cref="OnFrame"/> yourself.
/// Thread-safe: frames arrive on the receive thread, reads come from any.
/// </summary>
public sealed class SessionTracker
{
    /// <summary>A sender silent this long has ended (implementation-udp.md §
    /// Liveness and session end).</summary>
    public static readonly TimeSpan EndAfter = TimeSpan.FromSeconds(10);

    public sealed class Session
    {
        internal Session(IPEndPoint source, bool isLocal)
        {
            Source = source; IsLocal = isLocal;
        }

        /// <summary>The sender: source address and port.</summary>
        public IPEndPoint Source { get; }
        /// <summary>True when the sender runs on this host.</summary>
        public bool IsLocal { get; }
        public string GameName { get; internal set; } = "";
        public string InstanceLabel { get; internal set; } = "";
        public string Host { get; internal set; } = "";
        public long SessionStartUnixUs { get; internal set; }
        /// <summary>Declared motion rate, Hz; 0 = not declared.</summary>
        public float MotionRateHz { get; internal set; }

        /// <summary>How long without motion before the session is stale:
        /// 5 tick intervals of the declared rate, 1 s when undeclared
        /// (implementation-udp.md § Liveness and session end).</summary>
        public TimeSpan StaleAfter => MotionRateHz > 0
            ? TimeSpan.FromSeconds(5.0 / MotionRateHz) : TimeSpan.FromSeconds(1);
        /// <summary>This sender's descriptor tables. Read them on the
        /// receive thread (they are not thread-safe).</summary>
        public DescriptorTables Tables { get; } = new();
        /// <summary>Clock value of the sender's last frame of any type.</summary>
        public long LastFrame { get; internal set; }

        /// <summary>The identity a receiver pins this session by.</summary>
        public SessionPin Pin => new SessionPin(GameName, InstanceLabel, Host);

        /// <summary>For pickers: instance label, host or address, then game.</summary>
        public override string ToString()
        {
            var where = InstanceLabel.Length > 0 ? InstanceLabel
                : Host.Length > 0 ? Host
                : Source.ToString();
            return $"{GameName} ({where})";
        }
    }

    readonly object _gate = new();
    readonly Dictionary<IPEndPoint, Session> _senders = new();
    readonly HashSet<IPAddress> _localAddresses;
    readonly Func<long> _now;
    readonly long _endAfterTicks;
    SessionPin? _pinned;

    public SessionTracker() : this(Stopwatch.GetTimestamp, Stopwatch.Frequency, LocalAddresses()) { }

    /// <summary>For tests: a controllable clock in ticks of
    /// <paramref name="ticksPerSecond"/>, and the addresses that count as
    /// this host.</summary>
    public SessionTracker(Func<long> now, long ticksPerSecond, IEnumerable<IPAddress> localAddresses)
    {
        _now = now;
        _endAfterTicks = (long)(EndAfter.TotalSeconds * ticksPerSecond);
        _localAddresses = new HashSet<IPAddress>(localAddresses);
    }

    /// <summary>Raised when a sender's first discovery arrives, or a known
    /// sender starts a new session.</summary>
    public event Action<Session>? SessionStarted;
    /// <summary>Raised when a sender has been silent for <see cref="EndAfter"/>
    /// (checked by <see cref="Prune"/> and on every frame).</summary>
    public event Action<Session>? SessionEnded;

    /// <summary>Subscribes to a receiver's frames. Returns the tracker.</summary>
    public SessionTracker Attach(TelemetryReceiver receiver)
    {
        receiver.DiscoveryReceivedFrom += (src, d) => OnDiscovery(src, d);
        receiver.MotionReceivedFrom += (src, _) => OnFrame(src);
        receiver.EventReceivedFrom += (src, _) => OnFrame(src);
        return this;
    }

    /// <summary>The identity to bind to, or null for automatic choice. A
    /// pinned receiver selects nothing while no matching sender is present:
    /// it waits for that sender rather than following another game.</summary>
    public SessionPin? Pinned
    {
        get { lock (_gate) return _pinned; }
        set { lock (_gate) _pinned = value; }
    }

    /// <summary>Every live session, most recently started first.</summary>
    public IReadOnlyList<Session> Sessions
    {
        get { lock (_gate) return Ordered().ToList(); }
    }

    /// <summary>More than one session is live and nothing is pinned: the
    /// receiver should say so instead of choosing silently.</summary>
    public bool Ambiguous
    {
        get { lock (_gate) return _pinned is null && _senders.Count > 1; }
    }

    /// <summary>The session a single-output receiver binds to, or null.</summary>
    public Session? Selected
    {
        get
        {
            lock (_gate)
            {
                if (_pinned is { } pin)
                    return Ordered().FirstOrDefault(s => s.Pin.Equals(pin));
                return Ordered().OrderByDescending(s => s.IsLocal).FirstOrDefault();
            }
        }
    }

    /// <summary>The session of a sender, or null before its first discovery.</summary>
    public Session? Find(IPEndPoint source)
    {
        lock (_gate) return _senders.TryGetValue(source, out var s) ? s : null;
    }

    /// <summary>Folds in one discovery frame (page) from <paramref name="source"/>:
    /// starts the sender's session if new, merges the page into its tables.</summary>
    public Session OnDiscovery(IPEndPoint source, CapnpGen.DiscoveryFrame.READER d)
    {
        Session session;
        bool started;
        lock (_gate)
        {
            long now = _now();
            PruneLocked(now);
            started = !_senders.TryGetValue(source, out session!)
                      || session.SessionStartUnixUs != d.SessionStartUnixUs;
            if (session is null)
                _senders[source] = session = new Session(source, _localAddresses.Contains(source.Address));
            session.GameName = d.GameName ?? "";
            session.InstanceLabel = d.InstanceLabel ?? "";
            session.Host = d.Host ?? "";
            session.MotionRateHz = d.MotionRateHz;
            session.SessionStartUnixUs = d.SessionStartUnixUs;
            session.LastFrame = now;
        }
        // DescriptorTables resets itself on a new sessionStartUnixUs
        session.Tables.Apply(d);
        if (started) SessionStarted?.Invoke(session);
        return session;
    }

    /// <summary>Marks a motion or event frame from <paramref name="source"/>
    /// as seen. Returns its session, or null when the sender has sent no
    /// discovery yet (hold its ids back).</summary>
    public Session? OnFrame(IPEndPoint source)
    {
        lock (_gate)
        {
            long now = _now();
            PruneLocked(now);
            if (!_senders.TryGetValue(source, out var s)) return null;
            s.LastFrame = now;
            return s;
        }
    }

    /// <summary>Ends senders silent for <see cref="EndAfter"/>.</summary>
    public void Prune()
    {
        lock (_gate) PruneLocked(_now());
    }

    void PruneLocked(long now)
    {
        List<Session>? ended = null;
        foreach (var s in _senders.Values)
            if (now - s.LastFrame > _endAfterTicks)
                (ended ??= new List<Session>()).Add(s);
        if (ended is null) return;
        foreach (var s in ended)
        {
            _senders.Remove(s.Source);
            SessionEnded?.Invoke(s);
        }
    }

    IEnumerable<Session> Ordered() =>
        _senders.Values.OrderByDescending(s => s.SessionStartUnixUs);

    static IEnumerable<IPAddress> LocalAddresses()
    {
        var set = new List<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    set.Add(ua.Address);
        }
        catch (NetworkInformationException)
        {
        }
        return set;
    }
}

/// <summary>The identity a receiver remembers a session choice by
/// (implementation-udp.md § Senders and sessions): never address and port,
/// which change between runs.</summary>
public readonly struct SessionPin : IEquatable<SessionPin>
{
    public SessionPin(string gameName, string instanceLabel, string host)
    {
        GameName = gameName ?? ""; InstanceLabel = instanceLabel ?? ""; Host = host ?? "";
    }
    public string GameName { get; }
    public string InstanceLabel { get; }
    public string Host { get; }

    public bool Equals(SessionPin o) =>
        string.Equals(GameName, o.GameName, StringComparison.Ordinal) &&
        string.Equals(InstanceLabel, o.InstanceLabel, StringComparison.Ordinal) &&
        string.Equals(Host, o.Host, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is SessionPin p && Equals(p);
    public override int GetHashCode() =>
        ((GameName.GetHashCode() * 397) ^ InstanceLabel.GetHashCode()) * 397 ^ Host.GetHashCode();
}
}
