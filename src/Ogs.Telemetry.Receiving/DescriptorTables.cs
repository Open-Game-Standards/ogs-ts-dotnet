#nullable enable
using System.Collections.Generic;

namespace Ogs.Telemetry
{

/// <summary>
/// Receiver-side descriptor bookkeeping for paged discovery
/// (implementation-udp.md § Paged discovery and § Generations in motion
/// frames). Feed every DiscoveryFrame to <see cref="Apply"/>; look up a
/// motion frame's ids with the generation the motion frame names.
///
/// Pages of one generation merge by object name and channel id, each page
/// usable on arrival. An object's identity comes from the lowest-numbered
/// page listing it. An unpaged frame replaces its generation's set whole.
/// The current generation and the one before it are kept, so motion frames
/// still naming the previous generation resolve while a new one arrives;
/// a new <c>sessionStartUnixUs</c> starts over. Not thread-safe: call from
/// the receive loop.
/// </summary>
public sealed class DescriptorTables
{
    public sealed class Channel
    {
        public ushort Id { get; internal set; }
        public string Name { get; internal set; } = "";
        public string Unit { get; internal set; } = "";
        public string Description { get; internal set; } = "";
        /// <summary>Declared value type; Unspecified when the sender did not declare one.</summary>
        public CapnpGen.ChannelType Type { get; internal set; }

        /// <summary>False for a value whose variant differs from the declared
        /// type: receivers drop it (DESIGN.md, Typed channel values).</summary>
        public bool Accepts(CapnpGen.ChannelValue.WHICH variant) => Type switch
        {
            CapnpGen.ChannelType.number => variant == CapnpGen.ChannelValue.WHICH.Number,
            CapnpGen.ChannelType.boolean => variant == CapnpGen.ChannelValue.WHICH.Boolean,
            CapnpGen.ChannelType.text => false, // text rides TextValue
            _ => true,
        };

        /// <summary>False when a TextValue arrives for a channel declared
        /// number or boolean.</summary>
        public bool AcceptsText => Type is CapnpGen.ChannelType.text or CapnpGen.ChannelType.unspecified;
        public bool HasRange { get; internal set; }
        public double Min { get; internal set; }
        public double Max { get; internal set; }
    }

    public sealed class Object
    {
        public string Name { get; internal set; } = "";
        public CapnpGen.ObjectType Type { get; internal set; }
        public string TypeLabel { get; internal set; } = "";
        public string Location { get; internal set; } = "";
        public bool Primary { get; internal set; }
        public int DrivePointCount { get; internal set; }
        /// <summary>Page the identity fields came from; int.MaxValue = none yet.</summary>
        internal int IdentityPage = int.MaxValue;
        public Dictionary<ushort, Channel> Channels { get; } = new();
        /// <summary>Session values by channel id: double, bool or string.</summary>
        public Dictionary<ushort, object> Values { get; } = new();
    }

    public sealed class Generation
    {
        public uint Number { get; internal set; }
        /// <summary>Pages in the generation (1 = unpaged).</summary>
        public int PageCount { get; internal set; } = 1;
        internal readonly HashSet<int> PagesSeen = new();
        public bool Complete => PagesSeen.Count >= PageCount;
        public Dictionary<string, Object> Objects { get; } = new();
    }

    long _sessionStart = long.MinValue;
    Generation? _current, _previous;

    /// <summary>The newest generation seen, or null before any discovery.</summary>
    public Generation? Current => _current;

    /// <summary>Merges one discovery frame (page). Returns its generation.</summary>
    public Generation Apply(CapnpGen.DiscoveryFrame.READER d)
    {
        // a page whose index is not below its count is malformed: ignored
        // (implementation-udp.md § Paged discovery)
        if (d.PageCount > 1 && d.Page >= d.PageCount)
            return Find(d.Generation) ?? _current ?? new Generation { Number = d.Generation };

        if (d.SessionStartUnixUs != _sessionStart)
        {
            _sessionStart = d.SessionStartUnixUs;
            _current = _previous = null;
        }

        var gen = Find(d.Generation);
        if (gen is null)
        {
            gen = new Generation { Number = d.Generation };
            _previous = _current;
            _current = gen;
        }

        bool paged = d.PageCount > 1;
        if (!paged)
        {
            gen.Objects.Clear();
            gen.PagesSeen.Clear();
            gen.PageCount = 1;
        }
        else gen.PageCount = d.PageCount;
        int page = paged ? d.Page : 0;
        gen.PagesSeen.Add(page);

        foreach (var o in d.Objects)
        {
            var name = o.Name ?? "";
            if (!gen.Objects.TryGetValue(name, out var obj))
                gen.Objects[name] = obj = new Object { Name = name };
            if (page <= obj.IdentityPage)
            {
                // the lowest-numbered page listing an object carries its identity
                obj.IdentityPage = page;
                obj.Type = o.Type;
                obj.TypeLabel = o.TypeLabel ?? "";
                obj.Location = o.Location ?? "";
                obj.Primary = o.Primary;
                obj.DrivePointCount = o.DrivePoints.Count;
            }
            foreach (var ch in o.Channels)
            {
                bool range = ch.which == CapnpGen.ChannelDescriptor.WHICH.Range;
                obj.Channels[ch.Id] = new Channel
                {
                    Id = ch.Id, Name = ch.Name ?? "", Unit = ch.Unit ?? "",
                    Description = ch.Description ?? "", Type = ch.Type, HasRange = range,
                    Min = range ? ch.Range.Min : 0, Max = range ? ch.Range.Max : 0,
                };
            }
            foreach (var v in o.Values)
                obj.Values[v.Id] = v.which == CapnpGen.ChannelValue.WHICH.Boolean
                    ? v.Boolean : (object)(double)v.Number;
            foreach (var t in o.Texts)
                obj.Values[t.Id] = t.Text ?? "";
        }
        return gen;
    }

    /// <summary>The tables for <paramref name="generation"/> if it is the
    /// current or the previous one.</summary>
    public Generation? Find(uint generation) =>
        _current?.Number == generation ? _current
        : _previous?.Number == generation ? _previous
        : null;

    /// <summary>Resolves a motion frame's channel id. False when the
    /// generation or the id's page has not arrived: hold the value back.</summary>
    public bool TryGetChannel(uint generation, string objectName, ushort id, out Channel channel)
    {
        channel = null!;
        return Find(generation) is { } gen &&
               gen.Objects.TryGetValue(objectName, out var obj) &&
               obj.Channels.TryGetValue(id, out channel!);
    }
}
}
