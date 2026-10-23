namespace CluadeX.Models;

public sealed class ProviderSpendSettings
{
    public decimal? CreditBalanceUsd { get; set; }
    public DateTime? CreditSetAt { get; set; }
    public decimal CreditSpentOnAnchorDay { get; set; }
    public decimal? MonthlyPackageUsd { get; set; }
    public DateTime? PackageStartDate { get; set; }
}
