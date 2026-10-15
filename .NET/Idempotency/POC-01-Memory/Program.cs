using System.Collections.Concurrent;
using Idempotency.Poc01;

var builder = WebApplication.CreateBuilder(args);
var experimentDelay = builder.Configuration.GetValue("Experiment:DelayMilliseconds", 0);
if (experimentDelay is < 0 or > 30_000)
    throw new ArgumentOutOfRangeException(nameof(experimentDelay), "Use um atraso entre 0 e 30000 ms.");
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<OrderStore>();

var app = builder.Build();
app.UseMiddleware<IdempotencyMiddleware>();

app.MapPost("/orders", async (CreateOrder request, OrderStore store, CancellationToken cancellationToken) =>
{
    // A consulta ao cache já ocorreu. O atraso amplia a janela para que outras
    // requisições também encontrem a chave ausente antes de salvarmos a resposta.
    if (experimentDelay > 0)
        await Task.Delay(experimentDelay, cancellationToken);
    var order = store.Create(request);
    return Results.Created($"/orders/{order.Id}", order);
});

app.MapGet("/orders", (OrderStore store) => store.All());
app.MapGet("/orders/{id:int}", (int id, OrderStore store) =>
    store.Find(id) is { } order ? Results.Ok(order) : Results.NotFound());

app.Run();

public record CreateOrder(int ProductId, int Quantity);
public record Order(int Id, int ProductId, int Quantity);

public sealed class OrderStore
{
    private readonly ConcurrentDictionary<int, Order> _orders = new();
    private int _lastId;

    public Order Create(CreateOrder request)
    {
        var order = new Order(Interlocked.Increment(ref _lastId), request.ProductId, request.Quantity);
        _orders[order.Id] = order;
        return order;
    }

    public Order[] All() => _orders.Values.OrderBy(order => order.Id).ToArray();
    public Order? Find(int id) => _orders.GetValueOrDefault(id);
}
