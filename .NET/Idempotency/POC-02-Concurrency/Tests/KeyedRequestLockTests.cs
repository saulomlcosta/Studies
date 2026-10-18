using Idempotency.Poc02;
using Xunit;

namespace Idempotency.Poc02.Tests;

[Trait("Category", "Unit")]
public sealed class KeyedRequestLockTests
{
    [Fact]
    public async Task MesmaChave_CompartilhaInstanciaMesmoComAcessosParalelos()
    {
        var locks = new KeyedRequestLock();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return locks.Get("same");
        })).ToArray();
        start.SetResult();
        var gates = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(gates, gate => Assert.Same(gates[0], gate));
        Assert.Same(gates[0], locks.Get("same"));
    }

    [Fact]
    public async Task UmaPermissaoPorChave_ChavesDiferentesIndependentes()
    {
        var locks = new KeyedRequestLock();
        var first = locks.Get("a");
        var other = locks.Get("b");
        Assert.NotSame(first, other);
        Assert.True(await first.WaitAsync(0));
        try
        {
            Assert.False(await locks.Get("a").WaitAsync(0));
            Assert.True(await other.WaitAsync(0));
            other.Release();
        }
        finally { first.Release(); }
        Assert.True(await first.WaitAsync(0));
        first.Release();
    }

    [Fact]
    public async Task EsperaCancelada_NaoLiberaPermissaoAdquirida()
    {
        var gate = new KeyedRequestLock().Get("a");
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var waiting = gate.WaitAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(0, gate.CurrentCount);
        }
        finally { gate.Release(); }
        Assert.Equal(1, gate.CurrentCount);
    }
}
