using System.Collections.Concurrent;
using EngineeringAI.Core.Abstractions;

namespace EngineeringAI.Core.State;

public sealed class DesignFlowStore<TState> where TState : class, IDraftState, new()
{
    private readonly ConcurrentDictionary<string, TState> _drafts = new();
    private readonly TimeSpan _ttl;

    public DesignFlowStore(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromHours(2);
    }

    public TState GetOrCreate(string sessionId)
    {
        PurgeExpired();

        return _drafts.GetOrAdd(sessionId, id => new TState
        {
            SessionId = id,
            LastUpdatedUtc = DateTime.UtcNow
        });
    }

    public bool TryGet(string sessionId, out TState? state)
    {
        return _drafts.TryGetValue(sessionId, out state);
    }

    public void Update(string sessionId, Action<TState> mutate)
    {
        var state = GetOrCreate(sessionId);

        lock (state)
        {
            mutate(state);
            state.LastUpdatedUtc = DateTime.UtcNow;
        }
    }

    public bool Remove(string sessionId)
    {
        return _drafts.TryRemove(sessionId, out _);
    }

    private void PurgeExpired()
    {
        var cutoff = DateTime.UtcNow - _ttl;

        foreach (var pair in _drafts)
        {
            if (pair.Value.LastUpdatedUtc < cutoff)
            {
                _drafts.TryRemove(pair.Key, out _);
            }
        }
    }
}