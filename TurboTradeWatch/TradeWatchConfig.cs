namespace TurboTradeWatch;

/// <summary>
/// The plugin's settings, under <c>Turbo:TradeWatch</c> in the host's configuration, and on the
/// admin panel's Settings page while the plugin is loaded (each rule is a section of its own). A
/// change applies to the next movements read. The defaults are cautious: a normal hotel sees few
/// flags. The value table is kept in the plugin's tables and set on its own page.
/// </summary>
public sealed class TradeWatchConfig
{
    public const string SECTION_NAME = "Turbo:TradeWatch";

    /// <summary>
    /// Whether movements are read at all. Off: the watch reads nothing and flags nothing, and
    /// picks up where it was when turned on again (as far as the outbox still holds).
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// A flag about something older than this, in minutes, when the watch reads it (the plugin was
    /// off or behind) is kept but not announced: it is not news any more.
    /// </summary>
    public int NoticeMaxAgeMinutes { get; init; } = 60;

    /// <summary>
    /// How long, in days, the movements, transfers and hand-overs the watch keeps are kept. Flags
    /// are kept for good, with their figures; their timeline is empty once the movements are gone.
    /// </summary>
    public int RetentionDays { get; init; } = 90;
}

/// <summary>What the watch counts an item as worth (<c>Turbo:TradeWatch:Valuation</c>).</summary>
public sealed class ValuationConfig
{
    public const string SECTION_NAME = "Turbo:TradeWatch:Valuation";

    /// <summary>
    /// Whether a credit furni counts as the credits its name says (<c>CF_50_goldbar</c>: 50).
    /// </summary>
    public bool CreditFurni { get; init; } = true;

    /// <summary>
    /// What an LTD with no value of its own in the value table counts as, in credits. 0: such an
    /// LTD is unvalued, like any other furni.
    /// </summary>
    public int LtdCredits { get; init; } = 1000;
}

/// <summary>
/// A trade (or a marketplace sale) where one side gives much more than it gets
/// (<c>Turbo:TradeWatch:Lopsided</c>): a scam, or value pushed to another account.
/// </summary>
public sealed class LopsidedConfig
{
    public const string SECTION_NAME = "Turbo:TradeWatch:Lopsided";

    /// <summary>Whether the rule runs.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The least the richer side must be worth, in credits, to be looked at.</summary>
    public int MinValue { get; init; } = 5000;

    /// <summary>
    /// The most the other side may be worth, as a percent of the richer side, for the trade to be
    /// lopsided: 10 flags 5,000 credits of value given for 500 or less.
    /// </summary>
    public int MaxReturnPercent { get; init; } = 10;

    /// <summary>
    /// Whether a poorer side holding an item the watch has no value for is taken as fair: it may be
    /// a rare not in the value table. Off: such items count as nothing.
    /// </summary>
    public bool UnvaluedIsFair { get; init; } = true;

    /// <summary>Whether marketplace sales are looked at too: the price against the item's value.</summary>
    public bool Marketplace { get; init; } = true;
}

/// <summary>
/// A young account receiving valuables (<c>Turbo:TradeWatch:NewAccount</c>): a scammer's fresh
/// account, or an alt being fed.
/// </summary>
public sealed class NewAccountConfig
{
    public const string SECTION_NAME = "Turbo:TradeWatch:NewAccount";

    /// <summary>Whether the rule runs.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>An account younger than this, in days, when it receives, is new.</summary>
    public int MaxAgeDays { get; init; } = 3;

    /// <summary>The least value, in credits, it must receive in one trade, gift or sale.</summary>
    public int MinValue { get; init; } = 2000;
}

/// <summary>
/// One account receiving valuables from many others in a short time
/// (<c>Turbo:TradeWatch:Funnel</c>): alts farmed into a main, or a scam's takings collected.
/// </summary>
public sealed class FunnelConfig
{
    public const string SECTION_NAME = "Turbo:TradeWatch:Funnel";

    /// <summary>Whether the rule runs.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How many different accounts must have given to it within the window.</summary>
    public int MinSenders { get; init; } = 5;

    /// <summary>The window, in hours.</summary>
    public int WindowHours { get; init; } = 24;

    /// <summary>The least one account's transfer must be worth, in credits, to count.</summary>
    public int MinValue { get; init; } = 500;
}

/// <summary>
/// The same item passing through many accounts in a short time
/// (<c>Turbo:TradeWatch:RapidFlips</c>): a duped item being laundered.
/// </summary>
public sealed class RapidFlipsConfig
{
    public const string SECTION_NAME = "Turbo:TradeWatch:RapidFlips";

    /// <summary>Whether the rule runs.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How many different accounts must have held the item within the window, the first owner included.</summary>
    public int MinAccounts { get; init; } = 4;

    /// <summary>The window, in minutes.</summary>
    public int WindowMinutes { get; init; } = 60;

    /// <summary>Whether only items the watch values count (credit furni, LTDs, the value table).</summary>
    public bool ValuedOnly { get; init; } = true;
}
