using System.Collections.Concurrent;

namespace Idempotency.Poc03;

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
