using System.Text;
using Idempotency.Poc02;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using var services = new ServiceCollection().AddLogging().BuildServiceProvider();

DefaultHttpContext Context(string key, string body = "{}", CancellationToken token = default)
{
    var context = new DefaultHttpContext { RequestServices = services };
    context.Request.Method = "POST";
    context.Request.Path = "/orders";
    context.Request.Headers["Idempotency-Key"] = key;
    context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
    context.Response.Body = new MemoryStream();
    context.RequestAborted = token;
    return context;
}

static IConfiguration Config(bool enabled) => new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Idempotency:CoordinationEnabled"] = enabled.ToString()
    }).Build();

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine($"PASS: {message}");
}

// Barreira controlada mantém o primeiro endpoint aberto enquanto chegam os retries.
foreach (var enabled in new[] { false, true })
{
    using var cache = new MemoryCache(new MemoryCacheOptions());
    var locks = new KeyedRequestLock();
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var store = new OrderStore();
    var middleware = new IdempotencyMiddleware(async context =>
    {
        await release.Task;
        var order = store.Create(new CreateOrder(123, 2));
        context.Response.StatusCode = 201;
        context.Response.ContentType = "application/json";
        context.Response.Headers.Location = $"/orders/{order.Id}";
        await context.Response.WriteAsync($"{{\"id\":{order.Id}}}");
    });
    var contexts = Enumerable.Range(0, 20).Select(_ => Context("same-key")).ToArray();
    var tasks = contexts.Select(c => middleware.InvokeAsync(c, cache, locks, Config(enabled))).ToArray();
    release.SetResult();
    await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
    Check(store.All().Length == (enabled ? 1 : 20), $"coordenação={enabled}: quantidade real de pedidos");
    if (enabled)
    {
        Check(contexts.Count(c => c.Response.Headers["Idempotency-Replayed"] == "true") == 19,
            "20 chamadas: uma execução e 19 replays");
        Check(contexts.All(c => c.Response.StatusCode == 201 && c.Response.Headers.Location == "/orders/1"
            && Encoding.UTF8.GetString(((MemoryStream)c.Response.Body).ToArray()) == "{\"id\":1}"),
            "status, Location e corpo preservados");
        var conflict = Context("same-key", "{\"different\":true}");
        await middleware.InvokeAsync(conflict, cache, locks, Config(true));
        Check(conflict.Response.StatusCode == 409 && store.All().Length == 1, "corpo diferente: conflito sem novo pedido");
    }
}

// Chaves distintas devem entrar no endpoint antes de qualquer uma terminar.
using (var cache = new MemoryCache(new MemoryCacheOptions()))
{
    var locks = new KeyedRequestLock();
    var entered = 0;
    var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var middleware = new IdempotencyMiddleware(async _ =>
    {
        if (Interlocked.Increment(ref entered) == 2) bothEntered.SetResult();
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    });
    await Task.WhenAll(middleware.InvokeAsync(Context("a"), cache, locks, Config(true)),
        middleware.InvokeAsync(Context("b"), cache, locks, Config(true)));
    Check(entered == 2, "chaves diferentes progridem independentemente");
}

using (var cache = new MemoryCache(new MemoryCacheOptions()))
{
    var locks = new KeyedRequestLock();
    var gate = locks.Get("POST:/orders:cancel");
    await gate.WaitAsync();
    using var cts = new CancellationTokenSource();
    var middleware = new IdempotencyMiddleware(_ => throw new Exception("Endpoint não deveria executar"));
    var waiting = middleware.InvokeAsync(Context("cancel", token: cts.Token), cache, locks, Config(true));
    cts.Cancel();
    try { await waiting; throw new Exception("Esperava cancelamento"); }
    catch (OperationCanceledException) { }
    Check(gate.CurrentCount == 0, "cancelar uma espera não libera o semáforo de outra requisição");
    gate.Release();

    var attempts = 0;
    var failsOnce = new IdempotencyMiddleware(async context =>
    {
        if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("falha simulada");
        context.Response.StatusCode = 201;
        await context.Response.WriteAsync("ok");
    });
    var first = Context("failure");
    var original = first.Response.Body;
    try { await failsOnce.InvokeAsync(first, cache, locks, Config(true)); }
    catch (InvalidOperationException) { }
    await failsOnce.InvokeAsync(Context("failure"), cache, locks, Config(true)).WaitAsync(TimeSpan.FromSeconds(5));
    Check(attempts == 2 && ReferenceEquals(first.Response.Body, original), "exceção libera a chave e restaura o stream");
}

Console.WriteLine("Verificação em processo concluída. Não substitui o experimento HTTP com Kestrel.");
