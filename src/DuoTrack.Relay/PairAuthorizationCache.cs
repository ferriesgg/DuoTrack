using Microsoft.Extensions.Caching.Memory;

namespace DuoTrack.Relay;

internal sealed class PairAuthorizationCache(Func<Guid, Guid, Task<bool>> lookup) : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 4096 });
    private readonly object _gate = new();
    private int _version;

    public async ValueTask<bool> IsAllowedAsync(Guid first, Guid second)
    {
        var key = Key(first, second);
        while (true)
        {
            if (_cache.TryGetValue(key, out bool allowed)) return allowed;
            var version = Volatile.Read(ref _version);
            allowed = await lookup(first, second);
            lock (_gate)
            {
                if (version != _version) continue;
                _cache.Set(key, allowed, new MemoryCacheEntryOptions
                {
                    Size = 1,
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(allowed ? 5 : 1),
                });
                return allowed;
            }
        }
    }

    public void Paired(Guid first, Guid second)
    {
        lock (_gate) { _version++; _cache.Remove(Key(first, second)); }
    }
    private static (Guid, Guid) Key(Guid a, Guid b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);
    public void Dispose() => _cache.Dispose();
}
