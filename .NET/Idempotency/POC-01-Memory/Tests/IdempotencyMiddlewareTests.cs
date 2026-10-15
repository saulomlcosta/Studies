using System.Text;
using System.Text.Json;
using Idempotency.Poc01;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Idempotency.Poc01.Tests;

// Integração entre componentes reais, sem sockets ou servidor HTTP.
// Cada teste recebe cache, armazenamento e contextos próprios.
public sealed class IdempotencyMiddlewareTests : IDisposable
{
    private const string Body = "{\"productId\":123,\"quantity\":2}";
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly ServiceProvider _services = new ServiceCollection().AddLogging().BuildServiceProvider();
    private readonly OrderStore _store = new();
    private readonly List<DefaultHttpContext> _contexts = [];

    private DefaultHttpContext Context(string? key = "purchase-1", string body = Body)
    {
        var context = new DefaultHttpContext { RequestServices = _services };
        context.Request.Method = "POST";
        context.Request.Path = "/orders";
        if (key is not null) context.Request.Headers["Idempotency-Key"] = key;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        _contexts.Add(context);
        return context;
    }

    private async Task CreateOrder(HttpContext context)
    {
        // Também verifica que o middleware reposicionou o corpo para o próximo leitor.
        var request = await JsonSerializer.DeserializeAsync<CreateOrder>(context.Request.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(request);
        var order = _store.Create(request);
        await Results.Created($"/orders/{order.Id}", order).ExecuteAsync(context);
    }

    private static string ResponseBody(HttpContext context) =>
        Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static int OrderId(HttpContext context)
    {
        using var json = JsonDocument.Parse(ResponseBody(context));
        return json.RootElement.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task NovaChave_CriaPedidoEPreservaResposta()
    {
        var context = Context();
        var originalStream = context.Response.Body;
        await new IdempotencyMiddleware(CreateOrder).InvokeAsync(context, _cache);

        var order = Assert.Single(_store.All());
        Assert.Equal(123, order.ProductId);
        Assert.Equal(2, order.Quantity);
        Assert.Equal(201, context.Response.StatusCode);
        Assert.Equal($"/orders/{order.Id}", context.Response.Headers.Location.ToString());
        Assert.StartsWith("application/json", context.Response.ContentType);
        Assert.Equal("false", context.Response.Headers["Idempotency-Replayed"].ToString());
        Assert.Equal(order.Id, OrderId(context));
        Assert.Same(originalStream, context.Response.Body);
    }

    [Fact]
    public async Task MesmaChaveECorpo_Sequenciais_ReutilizamRespostaSemExecutarNovamente()
    {
        var executions = 0;
        var middleware = new IdempotencyMiddleware(async context =>
        {
            executions++;
            await CreateOrder(context);
        });
        var first = Context();
        var retry = Context();
        await middleware.InvokeAsync(first, _cache);
        await middleware.InvokeAsync(retry, _cache);

        Assert.Equal(1, executions);
        Assert.Single(_store.All());
        Assert.Equal(first.Response.StatusCode, retry.Response.StatusCode);
        Assert.Equal(first.Response.ContentType, retry.Response.ContentType);
        Assert.Equal(first.Response.Headers.Location.ToString(), retry.Response.Headers.Location.ToString());
        Assert.Equal(ResponseBody(first), ResponseBody(retry));
        Assert.Equal("true", retry.Response.Headers["Idempotency-Replayed"].ToString());
    }

    [Theory]
    [InlineData("{\"productId\":123,\"quantity\":3}")]
    [InlineData("{ \"quantity\": 2, \"productId\": 123 }")]
    public async Task MesmaChaveComBytesDiferentes_RetornaConflito(string otherBody)
    {
        var middleware = new IdempotencyMiddleware(CreateOrder);
        await middleware.InvokeAsync(Context(), _cache);
        var retry = Context(body: otherBody);
        await middleware.InvokeAsync(retry, _cache);

        Assert.Equal(409, retry.Response.StatusCode);
        Assert.Single(_store.All());
    }

    [Fact]
    public async Task OutraChaveMesmoCorpo_CriaOutroPedido()
    {
        var middleware = new IdempotencyMiddleware(CreateOrder);
        await middleware.InvokeAsync(Context("a"), _cache);
        await middleware.InvokeAsync(Context("b"), _cache);
        Assert.Equal(2, _store.All().Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ChaveAusenteOuVazia_Retorna400SemExecutar(string? key)
    {
        var context = Context(key);
        await new IdempotencyMiddleware(CreateOrder).InvokeAsync(context, _cache);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Empty(_store.All());
    }

    [Fact]
    public async Task ChaveLongaOuMultiplosValores_Retornam400SemExecutar()
    {
        var middleware = new IdempotencyMiddleware(CreateOrder);
        var longKey = Context(new string('x', 129));
        var multiple = Context();
        multiple.Request.Headers["Idempotency-Key"] = new[] { "a", "b" };
        await middleware.InvokeAsync(longKey, _cache);
        await middleware.InvokeAsync(multiple, _cache);
        Assert.Equal(400, longKey.Response.StatusCode);
        Assert.Equal(400, multiple.Response.StatusCode);
        Assert.Empty(_store.All());
    }

    [Theory]
    [InlineData("GET", "/orders")]
    [InlineData("POST", "/other")]
    public async Task ForaDaOperacaoProtegida_ContinuaSemExigirChave(string method, string path)
    {
        var called = false;
        var context = Context(null);
        context.Request.Method = method;
        context.Request.Path = path;
        await new IdempotencyMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        }).InvokeAsync(context, _cache);
        Assert.True(called);
    }

    [Fact]
    public async Task RespostaDeErro_NaoFicaNoCache()
    {
        var attempts = 0;
        var middleware = new IdempotencyMiddleware(async context =>
        {
            if (++attempts == 1)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsync("temporarily unavailable");
                return;
            }
            await CreateOrder(context);
        });
        var first = Context();
        var retry = Context();
        await middleware.InvokeAsync(first, _cache);
        await middleware.InvokeAsync(retry, _cache);
        Assert.Equal(503, first.Response.StatusCode);
        Assert.Equal(201, retry.Response.StatusCode);
        Assert.Equal("false", retry.Response.Headers["Idempotency-Replayed"].ToString());
        Assert.Equal(2, attempts);
        Assert.Single(_store.All());
    }

    [Fact]
    public async Task Excecao_RestauraStreamENaoRegistraResultado()
    {
        var context = Context();
        var original = context.Response.Body;
        var failing = new IdempotencyMiddleware(_ => throw new InvalidOperationException("simulated"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.InvokeAsync(context, _cache));
        Assert.Same(original, context.Response.Body);
        var retry = Context();
        await new IdempotencyMiddleware(CreateOrder).InvokeAsync(retry, _cache);
        Assert.Equal("false", retry.Response.Headers["Idempotency-Replayed"].ToString());
        Assert.Single(_store.All());
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task MesmaChaveConcorrente_DemonstraDuplicacaoConhecidaDaPoc01()
    {
        const int requests = 20;
        var entered = 0;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new IdempotencyMiddleware(async context =>
        {
            if (Interlocked.Increment(ref entered) == requests)
                allEntered.TrySetResult();
            // Nenhum endpoint pode concluir enquanto todos não passaram pelo cache.
            await release.Task;
            await CreateOrder(context);
        });
        var contexts = Enumerable.Range(0, requests).Select(_ => Context()).ToArray();
        var tasks = contexts.Select(context => middleware.InvokeAsync(context, _cache)).ToArray();
        try
        {
            // Timeout é proteção contra travamento, não o mecanismo de sincronização.
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(_store.All());
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(requests, _store.All().Length);
        Assert.Equal(requests, contexts.Select(OrderId).Distinct().Count());
        Assert.All(contexts, context =>
        {
            Assert.Equal(201, context.Response.StatusCode);
            Assert.Equal("false", context.Response.Headers["Idempotency-Replayed"].ToString());
        });

        // Depois da corrida, só um dos resultados fica associado à chave no cache.
        // Não assumimos qual chamada venceu a última gravação.
        var retry = Context();
        await middleware.InvokeAsync(retry, _cache);
        Assert.Equal("true", retry.Response.Headers["Idempotency-Replayed"].ToString());
        Assert.Contains(OrderId(retry), contexts.Select(OrderId));
        Assert.Equal(requests, entered);
        Assert.Equal(requests, _store.All().Length);
    }

    public void Dispose()
    {
        foreach (var context in _contexts)
        {
            context.Request.Body.Dispose();
            context.Response.Body.Dispose();
        }
        _cache.Dispose();
        _services.Dispose();
    }
}
