using System.Collections.Concurrent;
using System.Globalization;

namespace Fixtures.Leaks;

// The five leaks of sprint 0.4. Item classes are sealed and only created by their leak, so per-type instance counts
// in a dump equal the leaked item counts exactly (fixtures/KnownAnswers).

/// <summary>Leak 1: a static cache that only grows.</summary>
public sealed class StaticCacheLeak : ILeak
{
    private static readonly Dictionary<int, StaticCacheEntry> s_cache = [];
    private static readonly object s_gate = new();

    public string Name => "StaticCache";

    public int ItemCount
    {
        get
        {
            lock (s_gate)
            {
                return s_cache.Count;
            }
        }
    }

    public void Grow(int items, int payloadBytes)
    {
        lock (s_gate)
        {
            for (var i = 0; i < items; i++)
            {
                var key = s_cache.Count;
                s_cache.Add(key, new StaticCacheEntry(new byte[payloadBytes], new EntryMetadata("entry-" + key.ToString(CultureInfo.InvariantCulture))));
            }
        }
    }
}

public sealed record StaticCacheEntry(byte[] Payload, EntryMetadata Metadata);

public sealed record EntryMetadata(string Name);

/// <summary>Leak 2: short-lived subscribers attached to long-lived publishers and never detached.</summary>
public sealed class EventHandlerLeak : ILeak
{
    private static readonly object s_gate = new();
    private static LongLivedPublisher[]? s_publishers;
    private static int s_count;

    /// <summary>
    /// Number of long-lived publishers subscribers are spread over (default 16,384). Many publishers keep each
    /// invocation list short (one delegate with millions of targets copies the whole list on every subscription,
    /// sprint 0.4 decision 3); tests use few publishers so multicast invocation lists appear. Set before the first
    /// <see cref="Grow"/>; the publishers are created lazily, so a disabled leak allocates nothing.
    /// </summary>
    public static int PublisherCount { get; set; } = 16_384;

    public string Name => "EventHandler";

    public int ItemCount => Volatile.Read(ref s_count);

    public void Grow(int items, int payloadBytes)
    {
        lock (s_gate)
        {
            for (var i = 0; i < items; i++)
            {
                s_publishers ??= CreatePublishers();
                var subscriber = new LeakySubscriber(new byte[payloadBytes]);
                s_publishers[s_count % s_publishers.Length].Changed += subscriber.OnChanged;
                s_count++;
            }
        }
    }

    private static LongLivedPublisher[] CreatePublishers()
    {
        var publishers = new LongLivedPublisher[PublisherCount];
        for (var i = 0; i < publishers.Length; i++)
        {
            publishers[i] = new LongLivedPublisher();
        }

        return publishers;
    }
}

public sealed class LongLivedPublisher
{
    public event EventHandler? Changed;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

public sealed class LeakySubscriber(byte[] buffer)
{
    public byte[] Buffer { get; } = buffer;

    public int Notifications { get; private set; }

    public void OnChanged(object? sender, EventArgs e) => Notifications++;
}

/// <summary>Leak 3: lambdas capturing state, kept alive by a long-lived list of callbacks.</summary>
public sealed class ClosureLeak : ILeak
{
    private static readonly List<Action> s_callbacks = [];
    private static readonly object s_gate = new();

    public string Name => "Closure";

    public int ItemCount
    {
        get
        {
            lock (s_gate)
            {
                return s_callbacks.Count;
            }
        }
    }

    public void Grow(int items, int payloadBytes)
    {
        lock (s_gate)
        {
            for (var i = 0; i < items; i++)
            {
                var state = new CapturedState(new byte[payloadBytes]);
                s_callbacks.Add(() => state.Touch());
            }
        }
    }
}

public sealed class CapturedState(byte[] data)
{
    public byte[] Data { get; } = data;

    public int Touches { get; private set; }

    public void Touch() => Touches++;
}

/// <summary>Leak 4: disposable objects kept in a list and never disposed.</summary>
public sealed class UndisposedListLeak : ILeak
{
    private static readonly List<UndisposedResource> s_resources = [];
    private static readonly object s_gate = new();

    public string Name => "UndisposedList";

    public int ItemCount
    {
        get
        {
            lock (s_gate)
            {
                return s_resources.Count;
            }
        }
    }

    public void Grow(int items, int payloadBytes)
    {
        lock (s_gate)
        {
            for (var i = 0; i < items; i++)
            {
                s_resources.Add(new UndisposedResource(new byte[payloadBytes]));
            }
        }
    }
}

public sealed class UndisposedResource(byte[] buffer) : IDisposable
{
    public byte[]? Buffer { get; private set; } = buffer;

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        Buffer = null;
        IsDisposed = true;
    }
}

/// <summary>Leak 5: an unbounded concurrent dictionary keyed by unique strings.</summary>
public sealed class UnboundedDictionaryLeak : ILeak
{
    private static readonly ConcurrentDictionary<string, SessionRecord> s_sessions = new(StringComparer.Ordinal);
    private static int s_next;

    public string Name => "UnboundedDictionary";

    public int ItemCount => s_sessions.Count;

    public void Grow(int items, int payloadBytes)
    {
        for (var i = 0; i < items; i++)
        {
            var id = Interlocked.Increment(ref s_next);
            s_sessions.TryAdd("session-" + id.ToString(CultureInfo.InvariantCulture), new SessionRecord(new byte[payloadBytes], id));
        }
    }
}

public sealed record SessionRecord(byte[] State, int Sequence);
