using System.Collections.Concurrent;

namespace Idempotency.Poc02;

public sealed class KeyedRequestLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public SemaphoreSlim Get(string key)
    {
        if (_gates.TryGetValue(key, out var existing))
            return existing;

        var candidate = new SemaphoreSlim(1, 1);
        var selected = _gates.GetOrAdd(key, candidate);
        if (!ReferenceEquals(candidate, selected))
            candidate.Dispose();
        return selected;
    }

    // Os semáforos permanecem durante a vida do processo nesta POC.
    // Remover após Release seria inseguro: pode haver requisições aguardando
    // o semáforo antigo enquanto novas chamadas recebem outro para a mesma chave.
}
