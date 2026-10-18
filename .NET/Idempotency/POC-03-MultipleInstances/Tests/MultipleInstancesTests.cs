using System.Net;
using System.Net.Http.Json;
using System.Text;
using Idempotency.Poc03;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Idempotency.Poc03.Tests;

[Trait("Category", "Integration")]
public sealed class MultipleInstancesTests
{
    private const string Body = "{\"productId\":123,\"quantity\":2}";

    private static async Task<HttpResponseMessage> Post(HttpClient client, string key = "purchase", string body = Body)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/orders");
        message.Headers.Add("Idempotency-Key", key);
        message.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(message);
    }

    private static string Header(HttpResponseMessage response, string name) => response.Headers.GetValues(name).Single();

    [Fact]
    public async Task RepeticaoNaMesmaInstancia_ReutilizaRespostaEIdentificacao()
    {
        await using var a = await TestInstance.Start("A");
        using var first = await Post(a.Client);
        using var retry = await Post(a.Client);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("false", Header(first, "Idempotency-Replayed"));
        Assert.Equal("true", Header(retry, "Idempotency-Replayed"));
        Assert.Equal("A", Header(retry, "X-Instance-Id"));
        Assert.Equal(Header(first, "X-Instance-Run-Id"), Header(retry, "X-Instance-Run-Id"));
        Assert.Equal(first.Headers.Location, retry.Headers.Location);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        Assert.Single((await a.Client.GetFromJsonAsync<Order[]>("/orders"))!);
    }

    [Fact]
    public async Task MesmaChaveEmDuasInstancias_CriaUmPedidoEmCadaMesmoSequencialmente()
    {
        await using var a = await TestInstance.Start("A");
        await using var b = await TestInstance.Start("B");
        using var first = await Post(a.Client);
        using var second = await Post(b.Client);
        Assert.Equal("false", Header(first, "Idempotency-Replayed"));
        Assert.Equal("false", Header(second, "Idempotency-Replayed"));
        Assert.Equal("A", Header(first, "X-Instance-Id"));
        Assert.Equal("B", Header(second, "X-Instance-Id"));
        var orderA = Assert.Single((await a.Client.GetFromJsonAsync<Order[]>("/orders"))!);
        var orderB = Assert.Single((await b.Client.GetFromJsonAsync<Order[]>("/orders"))!);
        Assert.Equal(orderA.Id, orderB.Id); // Mesmo ID local não significa uma única execução.
        Assert.NotEqual(Header(first, "X-Instance-Run-Id"), Header(second, "X-Instance-Run-Id"));
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task VinteChamadasDistribuidas_CriamDoisPedidosEDezoitoReplays()
    {
        await using var a = await TestInstance.Start("A");
        await using var b = await TestInstance.Start("B");
        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(index => Post(index % 2 == 0 ? a.Client : b.Client)));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
            foreach (var group in responses.GroupBy(response => Header(response, "X-Instance-Id")))
            {
                Assert.Equal(10, group.Count());
                Assert.Equal(9, group.Count(response => Header(response, "Idempotency-Replayed") == "true"));
            }
            Assert.Equal(2, responses.Select(r => Header(r, "X-Instance-Run-Id")).Distinct().Count());
            Assert.Single((await a.Client.GetFromJsonAsync<Order[]>("/orders"))!);
            Assert.Single((await b.Client.GetFromJsonAsync<Order[]>("/orders"))!);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task OutraInstancia_NaoConheceConflitoRegistradoNaPrimeira()
    {
        await using var a = await TestInstance.Start("A");
        await using var b = await TestInstance.Start("B");
        using var first = await Post(a.Client);
        const string changed = "{\"productId\":123,\"quantity\":3}";
        using var conflict = await Post(a.Client, body: changed);
        using var accepted = await Post(b.Client, body: changed);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(2, Assert.Single((await a.Client.GetFromJsonAsync<Order[]>("/orders"))!).Quantity);
        Assert.Equal(3, Assert.Single((await b.Client.GetFromJsonAsync<Order[]>("/orders"))!).Quantity);
    }

    [Fact]
    public async Task ReiniciarInstancia_PerdeCacheEPedidosSemAfetarOutra()
    {
        await using var b = await TestInstance.Start("B");
        using var initialB = await Post(b.Client);
        string oldRun;
        await using (var original = await TestInstance.Start("A"))
        {
            using var first = await Post(original.Client);
            oldRun = Header(first, "X-Instance-Run-Id");
        }
        await using var restarted = await TestInstance.Start("A");
        Assert.Empty((await restarted.Client.GetFromJsonAsync<Order[]>("/orders"))!);
        using var afterRestart = await Post(restarted.Client);
        using var retryB = await Post(b.Client);
        Assert.Equal(HttpStatusCode.Created, afterRestart.StatusCode);
        Assert.Equal("false", Header(afterRestart, "Idempotency-Replayed"));
        Assert.NotEqual(oldRun, Header(afterRestart, "X-Instance-Run-Id"));
        Assert.Equal("true", Header(retryB, "Idempotency-Replayed"));
        Assert.Equal(Header(initialB, "X-Instance-Run-Id"), Header(retryB, "X-Instance-Run-Id"));
    }

    [Fact]
    public async Task PipelineReal_ValidaChaveEFazBindingDoJson()
    {
        await using var a = await TestInstance.Start("A");
        using var missing = await a.Client.PostAsync("/orders", new StringContent(Body, Encoding.UTF8, "application/json"));
        using var malformed = await Post(a.Client, body: "not-json");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("A", Header(missing, "X-Instance-Id"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Empty((await a.Client.GetFromJsonAsync<Order[]>("/orders"))!);
        using var valid = await Post(a.Client);
        var order = (await valid.Content.ReadFromJsonAsync<Order>())!;
        var found = await a.Client.GetFromJsonAsync<Order>(valid.Headers.Location);
        Assert.Equal(order, found);
        Assert.Equal(123, order.ProductId);
    }

    [Fact]
    public async Task MetadadosIdentificamCadaInicializacao()
    {
        await using var a = await TestInstance.Start("A");
        await using var b = await TestInstance.Start("B");
        var metaA = (await a.Client.GetFromJsonAsync<InstanceInfo>("/instance"))!;
        var metaB = (await b.Client.GetFromJsonAsync<InstanceInfo>("/instance"))!;
        Assert.Equal("A", metaA.Id);
        Assert.Equal("B", metaB.Id);
        Assert.NotEqual(metaA.RunId, metaB.RunId);
    }

    private sealed class TestInstance(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public static async Task<TestInstance> Start(string id)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Production",
                ApplicationName = typeof(ApplicationSetup).Assembly.GetName().Name
            });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Instance:Id"] = id,
                ["Experiment:DelayMilliseconds"] = "0",
                ["Idempotency:CoordinationEnabled"] = "true"
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseTestServer();
            ApplicationSetup.AddServices(builder);
            var app = builder.Build();
            ApplicationSetup.ConfigurePipeline(app);
            try { await app.StartAsync(); }
            catch { await app.DisposeAsync(); throw; }
            var client = app.GetTestClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            return new TestInstance(app, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }
}
