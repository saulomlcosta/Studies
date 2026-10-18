using Idempotency.Poc02;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Idempotency.Poc02.Tests;

public sealed partial class IdempotencyMiddlewareTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task Run(IdempotencyMiddleware middleware, HttpContext context) =>
        middleware.InvokeAsync(context, _cache, _locks, _configuration).WaitAsync(Deadline);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Concurrency")]
    public async Task ChamadasSimultaneas_ComparaProtecaoComDuplicacao(bool enabled)
    {
        const int count = 20;
        var executions = 0;
        var release = Signal();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Idempotency:CoordinationEnabled"] = enabled.ToString()
        }).Build();
        var middleware = new IdempotencyMiddleware(async context =>
        {
            Interlocked.Increment(ref executions);
            await release.Task.WaitAsync(Deadline);
            await CreateOrder(context);
        });
        var contexts = Enumerable.Range(0, count).Select(_ => Context()).ToArray();
        // Os streams são MemoryStream; cada chamada avança até o endpoint ou WaitAsync.
        var tasks = contexts.Select(c => middleware.InvokeAsync(c, _cache, _locks, config)).ToArray();
        try
        {
            Assert.Equal(enabled ? 1 : count, executions);
            Assert.Empty(_store.All());
            Assert.All(tasks, task => Assert.False(task.IsCompleted));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(tasks).WaitAsync(Deadline);
        }
        Assert.Equal(enabled ? 1 : count, _store.All().Length);
        Assert.Equal(enabled ? 1 : count, contexts.Select(OrderId).Distinct().Count());
        Assert.Equal(enabled ? 19 : 0, contexts.Count(c => c.Response.Headers["Idempotency-Replayed"] == "true"));
        Assert.All(contexts, c => Assert.Equal(201, c.Response.StatusCode));
        if (enabled)
            Assert.All(contexts, c =>
            {
                Assert.Equal(ResponseBody(contexts[0]), ResponseBody(c));
                Assert.Equal(contexts[0].Response.ContentType, c.Response.ContentType);
                Assert.Equal(contexts[0].Response.Headers.Location.ToString(), c.Response.Headers.Location.ToString());
            });
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task ChavesDiferentes_EntramAntesDeQualquerUmaConcluir()
    {
        var entered = 0;
        var bothEntered = Signal();
        var middleware = new IdempotencyMiddleware(async context =>
        {
            if (Interlocked.Increment(ref entered) == 2) bothEntered.TrySetResult();
            await bothEntered.Task.WaitAsync(Deadline);
            await CreateOrder(context);
        });
        await Task.WhenAll(Run(middleware, Context("a")), Run(middleware, Context("b")));
        Assert.Equal(2, _store.All().Length);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task CorpoDiferenteConcorrente_AguardaERetornaConflito_LiberandoParaReplay()
    {
        var release = Signal();
        var entered = Signal();
        var middleware = new IdempotencyMiddleware(async context =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(Deadline);
            await CreateOrder(context);
        });
        var first = Run(middleware, Context());
        var conflict = Context(body: "{\"productId\":123,\"quantity\":3}");
        Task? second = null;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            second = Run(middleware, conflict);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await first;
            if (second is not null) await second;
        }
        Assert.Equal(409, conflict.Response.StatusCode);
        var replay = Context();
        await Run(middleware, replay);
        await Run(middleware, Context()); // também comprova liberação no retorno de replay
        Assert.Equal("true", replay.Response.Headers["Idempotency-Replayed"].ToString());
        Assert.Single(_store.All());
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task CancelarUmaEspera_NaoInterrompeOriginalNemOutroParticipante()
    {
        var release = Signal();
        var entered = Signal();
        var executions = 0;
        var middleware = new IdempotencyMiddleware(async context =>
        {
            Interlocked.Increment(ref executions);
            entered.TrySetResult();
            await release.Task.WaitAsync(Deadline);
            await CreateOrder(context);
        });
        using var cancellation = new CancellationTokenSource();
        var cancelled = Context();
        cancelled.RequestAborted = cancellation.Token;
        var survivor = Context();
        var first = Run(middleware, Context());
        Task? waiting = null;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            var cancelledTask = Run(middleware, cancelled);
            waiting = Run(middleware, survivor);
            Assert.False(cancelledTask.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledTask);
            Assert.False(first.IsCompleted);
            Assert.False(waiting.IsCompleted);
            Assert.Equal(1, executions);
        }
        finally
        {
            release.TrySetResult();
            await first;
            if (waiting is not null) await waiting;
        }
        Assert.Single(_store.All());
        Assert.Equal(1, executions);
        Assert.Equal("true", survivor.Response.Headers["Idempotency-Replayed"].ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Concurrency")]
    public async Task FalhaAntesDoEfeito_LiberaParticipanteQueEstavaEsperando(bool throws)
    {
        var release = Signal();
        var entered = Signal();
        var attempts = 0;
        var middleware = new IdempotencyMiddleware(async context =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(Deadline);
                if (throws) throw new InvalidOperationException("simulated failure before effect");
                context.Response.StatusCode = 503;
                return;
            }
            await CreateOrder(context);
        });
        var original = Context();
        var originalStream = original.Response.Body;
        var first = Run(middleware, original);
        var retry = Context();
        Task? second = null;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            second = Run(middleware, retry);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            if (throws) await Assert.ThrowsAsync<InvalidOperationException>(() => first);
            else await first;
            if (second is not null) await second;
        }
        Assert.Same(originalStream, original.Response.Body);
        if (!throws) Assert.Equal(503, original.Response.StatusCode);
        Assert.Equal(201, retry.Response.StatusCode);
        Assert.Equal("false", retry.Response.Headers["Idempotency-Replayed"].ToString());
        Assert.Equal(2, attempts);
        Assert.Single(_store.All());
    }
}
