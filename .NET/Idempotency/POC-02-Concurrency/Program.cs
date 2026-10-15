using System.Collections.Concurrent;
using Idempotency.Poc02;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<OrderStore>();
builder.Services.AddSingleton<KeyedRequestLock>();

var app = builder.Build();
app.UseMiddleware<IdempotencyMiddleware>();

app.MapPost("/orders", async (CreateOrder request, OrderStore store, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    // Atraso didático para tornar a janela de concorrência observável.
    await Task.Delay(configuration.GetValue("Experiment:DelayMilliseconds", 500), cancellationToken);
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
