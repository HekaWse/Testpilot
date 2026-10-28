using System.Collections.Concurrent;

namespace Testpilot.Sap;

public sealed class MockSapClient : ISapClient
{
    private readonly ConcurrentDictionary<string, OrderInfo> orders = new(StringComparer.Ordinal);

    public MockSapClient(IEnumerable<KeyValuePair<string, OrderInfo>>? orders = null)
    {
        if (orders is null)
            return;
        foreach (var (fauf, order) in orders)
            AddOrder(fauf, order);
    }

    public void AddOrder(string fauf, OrderInfo order)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fauf);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentException.ThrowIfNullOrWhiteSpace(order.ArtNr);
        ArgumentException.ThrowIfNullOrWhiteSpace(order.Fz);
        if (!orders.TryAdd(fauf, order))
            throw new InvalidOperationException($"SAP order '{fauf}' is already configured in the mock.");
    }

    public bool Contains(string fauf) => orders.ContainsKey(fauf);

    public Task<OrderInfo> GetOrderAsync(string fauf, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(fauf);
        return Task.FromResult(orders.TryGetValue(fauf, out var order)
            ? order
            : throw new SapOrderNotFoundException(fauf));
    }
}
