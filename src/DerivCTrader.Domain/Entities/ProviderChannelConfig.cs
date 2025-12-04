namespace DerivCTrader.Domain.Entities;

public class ProviderChannelConfig
{
    public string ProviderChannelId { get; set; } = string.Empty;
    public string? ProviderName { get; set; }
    public bool TakeOriginal { get; set; }
    public bool TakeOpposite { get; set; }
}
