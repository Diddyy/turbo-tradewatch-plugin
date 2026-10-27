using Microsoft.Extensions.Options;

namespace TurboTradeWatch.Watch;

/// <summary>Every setting of the watch as it is now.</summary>
public sealed record WatchSettings(
    TradeWatchConfig Watch,
    ValuationConfig Valuation,
    LopsidedConfig Lopsided,
    NewAccountConfig NewAccount,
    FunnelConfig Funnel,
    RapidFlipsConfig RapidFlips
)
{
    public static WatchSettings Defaults { get; } = new(new(), new(), new(), new(), new(), new());
}

/// <summary>The settings as the panel last saved them, read each time they are used.</summary>
public interface IWatchSettings
{
    WatchSettings Current { get; }
}

/// <summary>The host's configuration, one section per rule.</summary>
public sealed class HostWatchSettings(
    IOptionsMonitor<TradeWatchConfig> watch,
    IOptionsMonitor<ValuationConfig> valuation,
    IOptionsMonitor<LopsidedConfig> lopsided,
    IOptionsMonitor<NewAccountConfig> newAccount,
    IOptionsMonitor<FunnelConfig> funnel,
    IOptionsMonitor<RapidFlipsConfig> rapidFlips
) : IWatchSettings
{
    public WatchSettings Current =>
        new(
            watch.CurrentValue,
            valuation.CurrentValue,
            lopsided.CurrentValue,
            newAccount.CurrentValue,
            funnel.CurrentValue,
            rapidFlips.CurrentValue
        );
}
