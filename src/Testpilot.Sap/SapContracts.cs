namespace Testpilot.Sap;

/// <summary>Order data read from SAP: article number, production state (FZ) and article description (may be empty).</summary>
public sealed record OrderInfo(string ArtNr, string Fz, string Description = "");

public class SapException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException);
public sealed class SapOrderNotFoundException(string fauf, string? detail = null)
    : SapException(string.IsNullOrWhiteSpace(detail) ? $"SAP order '{fauf}' was not found." : $"SAP order '{fauf}' was not found: {detail}");

public interface ISapClient
{
    Task<OrderInfo> GetOrderAsync(string fauf, CancellationToken cancellationToken = default);
}
