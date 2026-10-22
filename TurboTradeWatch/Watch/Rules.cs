using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TurboTradeWatch.Database;
using TurboTradeWatch.Hosting;

namespace TurboTradeWatch.Watch;

/// <summary>
/// The rules, run on each operation the watch reads, in the reader's transaction. Each flag has a
/// key of what it found (the operation, or the funnel's receiver or the item's chain while it goes
/// on), so reading the same movements again flags nothing new. Windows are measured in when the
/// movements happened, not when they were read, so a backlog read late flags what a live read would
/// have.
/// </summary>
public sealed class Rules(IWatchHotel hotel)
{
    public const string LOPSIDED = "lopsided";
    public const string NEW_ACCOUNT = "new_account";
    public const string FUNNEL = "funnel";
    public const string RAPID_FLIPS = "rapid_flips";

    public static readonly IReadOnlyList<string> ALL = [LOPSIDED, NEW_ACCOUNT, FUNNEL, RAPID_FLIPS];

    public static string Title(string rule) =>
        rule switch
        {
            LOPSIDED => "Lopsided trade",
            NEW_ACCOUNT => "New account receives",
            FUNNEL => "Funnel",
            RAPID_FLIPS => "Rapid flips",
            _ => rule,
        };

    /// <summary>Runs every rule that is on over <paramref name="read"/>; the flags made, not yet saved.</summary>
    public async Task<List<FlagEntity>> RunAsync(
        TradeWatchDbContext db,
        OperationRead read,
        IReadOnlyList<HandoverEntity> handovers,
        WatchSettings settings,
        DateTime now,
        CancellationToken ct
    )
    {
        var made = new List<FlagEntity>();

        if (
            settings.Lopsided.Enabled
            && await LopsidedAsync(db, read, settings.Lopsided, now, ct).ConfigureAwait(false)
                is { } lopsided
        )
            made.Add(lopsided);

        if (settings.NewAccount.Enabled)
            made.AddRange(
                await NewAccountAsync(db, read, settings.NewAccount, now, ct).ConfigureAwait(false)
            );

        if (settings.Funnel.Enabled)
            made.AddRange(
                await FunnelAsync(db, read, settings.Funnel, now, ct).ConfigureAwait(false)
            );

        if (settings.RapidFlips.Enabled)
            made.AddRange(
                await FlipsAsync(db, read, handovers, settings.RapidFlips, now, ct)
                    .ConfigureAwait(false)
            );

        return made;
    }

    private async Task<FlagEntity?> LopsidedAsync(
        TradeWatchDbContext db,
        OperationRead read,
        LopsidedConfig config,
        DateTime now,
        CancellationToken ct
    )
    {
        if (
            read.Transfers.Count != 2
            || read.Kind == MovementsKind.GIFT
            || (read.Kind == MovementsKind.MARKET && !config.Marketplace)
        )
            return null;

        var (heavy, light) =
            read.Transfers[0].Value >= read.Transfers[1].Value
                ? (read.Transfers[0], read.Transfers[1])
                : (read.Transfers[1], read.Transfers[0]);

        if (
            heavy.Value < Math.Max(1, config.MinValue)
            || light.Value * 100 > heavy.Value * (long)Math.Max(0, config.MaxReturnPercent)
            || (config.UnvaluedIsFair && light.Unvalued > 0)
        )
            return null;

        var names = await hotel.NamesAsync([heavy.From, heavy.To], ct).ConfigureAwait(false);
        var what = read.Kind == MovementsKind.MARKET ? "marketplace sale" : "trade";

        return await NewAsync(
                db,
                LOPSIDED,
                $"{LOPSIDED}:{read.OperationId}",
                $"player:{heavy.To}",
                heavy.To,
                [heavy.From],
                heavy.Value,
                $"{N(names, heavy.To)} got {Valuer.Credits(heavy.Value)} of value from {N(names, heavy.From)} for {Valuer.Credits(light.Value)} in a {what}",
                [
                    $"{N(names, heavy.From)} gave: {heavy.Summary} = {Valuer.Credits(heavy.Value)}{Unvalued(heavy)}",
                    $"{N(names, heavy.To)} gave: {light.Summary} = {Valuer.Credits(light.Value)}{Unvalued(light)}",
                    $"Rule: the richer side worth {Valuer.Credits(config.MinValue)} or more, the other {config.MaxReturnPercent}% of it or less.",
                ],
                [read.OperationId],
                read.OccurredAt,
                now,
                ct
            )
            .ConfigureAwait(false);
    }

    private async Task<List<FlagEntity>> NewAccountAsync(
        TradeWatchDbContext db,
        OperationRead read,
        NewAccountConfig config,
        DateTime now,
        CancellationToken ct
    )
    {
        var made = new List<FlagEntity>();

        foreach (var received in read.Transfers.GroupBy(x => x.To))
        {
            var value = received.Sum(x => x.Value);

            if (received.Key <= 0 || value < Math.Max(1, config.MinValue))
                continue;

            if (
                await hotel.CreatedAtAsync(received.Key, ct).ConfigureAwait(false)
                is not { } created
            )
                continue;

            var age = read.OccurredAt - created;

            if (age >= TimeSpan.FromDays(config.MaxAgeDays))
                continue;

            var givers = received.Select(x => x.From).Distinct().ToList();
            var names = await hotel.NamesAsync([received.Key, .. givers], ct).ConfigureAwait(false);

            if (
                await NewAsync(
                        db,
                        NEW_ACCOUNT,
                        $"{NEW_ACCOUNT}:{read.OperationId}:{received.Key}",
                        $"player:{received.Key}",
                        received.Key,
                        givers,
                        value,
                        $"{N(names, received.Key)}, an account {Age(age)} old, received {Valuer.Credits(value)} of value from {string.Join(", ", givers.Select(x => N(names, x)))}",
                        [
                            .. received.Select(x =>
                                $"From {N(names, x.From)} ({x.Kind}): {x.Summary} = {Valuer.Credits(x.Value)}{Unvalued(x)}"
                            ),
                            $"Account made {created:yyyy-MM-dd HH:mm} UTC.",
                            $"Rule: an account younger than {config.MaxAgeDays} days receiving {Valuer.Credits(config.MinValue)} or more at once.",
                        ],
                        [read.OperationId],
                        read.OccurredAt,
                        now,
                        ct
                    )
                    .ConfigureAwait(false) is
                { } flag
            )
                made.Add(flag);
        }

        return made;
    }

    private async Task<List<FlagEntity>> FunnelAsync(
        TradeWatchDbContext db,
        OperationRead read,
        FunnelConfig config,
        DateTime now,
        CancellationToken ct
    )
    {
        var made = new List<FlagEntity>();
        var min = Math.Max(1, config.MinValue);

        foreach (
            var receiver in read
                .Transfers.Where(x => x.Value >= min && x.To > 0)
                .Select(x => x.To)
                .Distinct()
        )
        {
            var since = read.OccurredAt.AddHours(-Math.Max(1, config.WindowHours));
            var received = await db
                .Transfers.AsNoTracking()
                .Where(x =>
                    x.ToPlayerId == receiver
                    && x.Value >= min
                    && x.OccurredAt >= since
                    && x.OccurredAt <= read.OccurredAt
                    && x.FromPlayerId != receiver
                )
                .OrderBy(x => x.OccurredAt)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var senders = received.Select(x => x.FromPlayerId).Distinct().ToList();

            if (senders.Count < Math.Max(2, config.MinSenders))
                continue;

            var value = received.Sum(x => x.Value);
            var names = await hotel.NamesAsync([receiver, .. senders], ct).ConfigureAwait(false);
            var summary =
                $"{N(names, receiver)} received {Valuer.Credits(value)} of value from {senders.Count} accounts in {config.WindowHours} h";
            List<string> evidence =
            [
                .. received.Select(x =>
                    $"{x.OccurredAt:yyyy-MM-dd HH:mm} from {N(names, x.FromPlayerId)} ({x.Kind}): {x.Summary} = {Valuer.Credits(x.Value)}"
                ),
                $"Rule: {config.MinSenders} or more accounts giving {Valuer.Credits(min)} or more each within {config.WindowHours} h.",
            ];
            var operations = received.Select(x => x.OperationId).Distinct().ToList();

            if (
                await OngoingAsync(db, FUNNEL, $"player:{receiver}", since, ct)
                    .ConfigureAwait(false) is
                { } open
            )
            {
                Update(open, senders, value, summary, evidence, operations, read.OccurredAt);
                continue;
            }

            if (
                await NewAsync(
                        db,
                        FUNNEL,
                        $"{FUNNEL}:{receiver}:{read.OperationId}",
                        $"player:{receiver}",
                        receiver,
                        senders,
                        value,
                        summary,
                        evidence,
                        operations,
                        received[0].OccurredAt,
                        now,
                        ct
                    )
                    .ConfigureAwait(false) is
                { } flag
            )
            {
                flag.LastSeenAt = read.OccurredAt;
                made.Add(flag);
            }
        }

        return made;
    }

    private async Task<List<FlagEntity>> FlipsAsync(
        TradeWatchDbContext db,
        OperationRead read,
        IReadOnlyList<HandoverEntity> handovers,
        RapidFlipsConfig config,
        DateTime now,
        CancellationToken ct
    )
    {
        var made = new List<FlagEntity>();
        var valued = read.Handovers.Where(x => x.Valued).Select(x => x.AssetId).ToHashSet();

        foreach (
            var chain in handovers
                .Where(x => !config.ValuedOnly || valued.Contains(x.AssetId))
                .Select(x => x.ChainId)
                .Distinct()
        )
        {
            var since = read.OccurredAt.AddMinutes(-Math.Max(1, config.WindowMinutes));
            var hands = await db
                .Handovers.AsNoTracking()
                .Where(x =>
                    x.ChainId == chain && x.OccurredAt >= since && x.OccurredAt <= read.OccurredAt
                )
                .OrderBy(x => x.OccurredAt)
                .ThenBy(x => x.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var accounts = new List<int>();

            foreach (var hand in hands)
            foreach (var player in new[] { hand.FromPlayerId, hand.ToPlayerId })
                if (player > 0 && !accounts.Contains(player))
                    accounts.Add(player);

            if (accounts.Count < Math.Max(3, config.MinAccounts))
                continue;

            var holder = hands[^1].ToPlayerId;
            var names = await hotel.NamesAsync(accounts, ct).ConfigureAwait(false);
            var item =
                hotel.FurniName(hands[^1].DefinitionId) ?? $"furni #{hands[^1].DefinitionId}";
            var summary =
                $"{item} went through {accounts.Count} accounts in {Minutes(hands[^1].OccurredAt - hands[0].OccurredAt)}, now with {N(names, holder)}";
            List<string> evidence =
            [
                .. hands.Select(x =>
                    $"{x.OccurredAt:yyyy-MM-dd HH:mm:ss} {N(names, x.FromPlayerId)} -> {N(names, x.ToPlayerId)} (item {x.AssetId.ToString(CultureInfo.InvariantCulture)})"
                ),
                $"Item chain {chain.ToString(CultureInfo.InvariantCulture)} (a marketplace sale makes an item again under a new id; the chain keeps the first).",
                $"Rule: {config.MinAccounts} or more accounts holding one item within {config.WindowMinutes} min.",
            ];
            var operations = hands.Select(x => x.OperationId).Distinct().ToList();
            var others = accounts.Where(x => x != holder).ToList();

            if (
                await OngoingAsync(db, RAPID_FLIPS, $"item:{chain}", since, ct)
                    .ConfigureAwait(false) is
                { } open
            )
            {
                open.PlayerId = holder;
                Update(open, others, 0, summary, evidence, operations, read.OccurredAt);
                continue;
            }

            if (
                await NewAsync(
                        db,
                        RAPID_FLIPS,
                        $"{RAPID_FLIPS}:{chain}:{read.OperationId}",
                        $"item:{chain}",
                        holder,
                        others,
                        0,
                        summary,
                        evidence,
                        operations,
                        hands[0].OccurredAt,
                        now,
                        ct
                    )
                    .ConfigureAwait(false) is
                { } flag
            )
            {
                flag.LastSeenAt = read.OccurredAt;
                made.Add(flag);
            }
        }

        return made;
    }

    /// <summary>A flag of the rule on the subject that is still going on (seen within the window), tracked.</summary>
    private static async Task<FlagEntity?> OngoingAsync(
        TradeWatchDbContext db,
        string rule,
        string subject,
        DateTime since,
        CancellationToken ct
    ) =>
        db.Flags.Local.FirstOrDefault(x =>
            x.Rule == rule && x.SubjectRef == subject && x.LastSeenAt >= since
        )
        ?? await db
            .Flags.Where(x => x.Rule == rule && x.SubjectRef == subject && x.LastSeenAt >= since)
            .OrderByDescending(x => x.LastSeenAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    private static void Update(
        FlagEntity flag,
        IEnumerable<int> others,
        long value,
        string summary,
        IEnumerable<string> evidence,
        IEnumerable<string> operations,
        DateTime seen
    )
    {
        flag.OtherPlayerIds = Ids(others);
        flag.Value = Math.Max(flag.Value, value);
        flag.Summary = Cut(summary, FlagEntity.MAX_SUMMARY);
        flag.Evidence = string.Join('\n', evidence);
        flag.Operations = Operations(
            flag.Operations.Split(',', StringSplitOptions.RemoveEmptyEntries).Concat(operations)
        );
        flag.LastSeenAt = seen > flag.LastSeenAt ? seen : flag.LastSeenAt;
    }

    /// <summary>A new flag under <paramref name="key"/>, added to the context; null when that key is flagged already.</summary>
    private static async Task<FlagEntity?> NewAsync(
        TradeWatchDbContext db,
        string rule,
        string key,
        string subject,
        int player,
        IEnumerable<int> others,
        long value,
        string summary,
        IEnumerable<string> evidence,
        IEnumerable<string> operations,
        DateTime occurredAt,
        DateTime now,
        CancellationToken ct
    )
    {
        key = Cut(key, 200);

        if (
            db.Flags.Local.Any(x => x.DedupeKey == key)
            || await db.Flags.AnyAsync(x => x.DedupeKey == key, ct).ConfigureAwait(false)
        )
            return null;

        var flag = new FlagEntity
        {
            Rule = rule,
            DedupeKey = key,
            SubjectRef = subject,
            PlayerId = player,
            OtherPlayerIds = Ids(others),
            Value = value,
            Summary = Cut(summary, FlagEntity.MAX_SUMMARY),
            Evidence = string.Join('\n', evidence),
            Operations = Operations(operations),
            OccurredAt = occurredAt,
            LastSeenAt = occurredAt,
            CreatedAt = now,
        };

        db.Flags.Add(flag);

        return flag;
    }

    private static string N(IReadOnlyDictionary<int, string> names, int id) =>
        names.TryGetValue(id, out var name) ? name : $"#{id}";

    private static string Unvalued(Transfer x) =>
        (x.Unvalued > 0 ? $" ({x.Unvalued} item{(x.Unvalued == 1 ? "" : "s")} not valued)" : "")
        + (x.Ltds > 0 ? $" ({x.Ltds} LTD{(x.Ltds == 1 ? "" : "s")})" : "");

    private static string Ids(IEnumerable<int> ids) =>
        Cut(
            string.Join(',', ids.Distinct().Select(x => x.ToString(CultureInfo.InvariantCulture))),
            255
        );

    private static string Operations(IEnumerable<string> operations) =>
        string.Join(',', operations.Distinct().TakeLast(FlagEntity.MAX_OPERATIONS));

    private static string Age(TimeSpan age) =>
        age.TotalHours < 48
            ? $"{Math.Max(0, (int)age.TotalHours)} h"
            : $"{(int)age.TotalDays} days";

    private static string Minutes(TimeSpan span) =>
        $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))} min";

    internal static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
