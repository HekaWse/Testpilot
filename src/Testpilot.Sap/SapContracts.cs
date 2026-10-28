namespace Testpilot.Sap;

public sealed record OrderInfo(string ArtNr, string Fz);

public class SapException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException);
public sealed class SapOrderNotFoundException(string fauf) : SapException($"SAP order '{fauf}' was not found.");

public interface ISapClient
{
    Task<OrderInfo> GetOrderAsync(string fauf, CancellationToken cancellationToken = default);
}
