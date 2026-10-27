using System;
using System.Collections.Generic;
using System.Linq;
using Turbo.Primitives.Movements;
using Turbo.Primitives.Movements.Enums;
using TurboTradeWatch.Database;

namespace TurboTradeWatch.Watch;

/// <summary>What one player handed another in one operation, with what it was worth.</summary>
public sealed record Transfer(
    string Kind,
    int From,
    int To,
    long Value,
    long Credits,
    IReadOnlyList<MovementEntity> Items,
    int Unvalued,
    int Ltds,
    string Summary
);

/// <summary>An item passing from one player to another: <see cref="ListedAssetId"/> is the item it was before a sale made it again.</summary>
public sealed record Handover(
    long AssetId,
    long? ListedAssetId,
    int DefinitionId,
    int From,
    int To,
    bool Valued
);

/// <summary>An operation read: its transfers and hand-overs, and what kind of exchange it was.</summary>
public sealed record OperationRead(
    string OperationId,
    string Kind,
    DateTime OccurredAt,
    IReadOnlyList<Transfer> Transfers,
    IReadOnlyList<Handover> Handovers
);

/// <summary>
/// Reads an operation's movements (<c>docs/movements.md</c>) as who handed whom what:
/// <list type="bullet">
/// <item>A <c>trade</c>: each side's items, from the player who offered them to the other. A
/// trade with a compensation (its second half failed and the first was handed back) moved nothing
/// and is no exchange.</item>
/// <item>A <c>catalog.gift</c>: the sender (who paid) to the receiver: the present's content,
/// valued at the content, or at the credits paid when the content has no value the watch knows.
/// A gift to oneself is no exchange.</item>
/// <item>A <c>market.buy</c>: the price, buyer to seller, and the item, seller to buyer (made again
/// under a new id, which the hand-over ties to the listed one).</item>
/// </list>
/// </summary>
public static class Operations
{
    /// <summary>The causes the watch keeps: exchanges between players.</summary>
    public static readonly IReadOnlySet<string> CAUSES = new HashSet<string>
    {
        MovementCauses.TRADE,
        MovementCauses.CATALOG_GIFT,
        MovementCauses.MARKET_BUY,
    };

    private static readonly string CURRENCY = nameof(AssetKind.Currency);

    public static OperationRead? Read(IReadOnlyList<MovementEntity> rows, Valuer valuer)
    {
        if (rows.Count == 0)
            return null;

        var ordered = rows.OrderBy(x => x.Seq).ToList();
        var first = ordered[0];

        return first.Cause switch
        {
            MovementCauses.TRADE => Trade(ordered, valuer),
            MovementCauses.CATALOG_GIFT => Gift(ordered, valuer),
            MovementCauses.MARKET_BUY => Market(ordered, valuer),
            _ => null,
        };
    }

    private static bool IsItem(MovementEntity x) => x.AssetKind != CURRENCY;

    private static DateTime When(IReadOnlyList<MovementEntity> rows) => rows.Min(x => x.OccurredAt);

    private static OperationRead? Trade(List<MovementEntity> rows, Valuer valuer)
    {
        if (rows.Any(x => x.CauseRef == MovementCauses.TRADE_REF_COMPENSATION))
            return null;

        var items = rows.Where(x =>
                IsItem(x)
                && x.FromKind == nameof(PlaceKind.Inventory)
                && x.ToKind == nameof(PlaceKind.Inventory)
                && x.FromPlayerId != x.ToPlayerId
            )
            .ToList();

        if (items.Count == 0)
            return null;

        var transfers = items
            .GroupBy(x => (x.FromPlayerId, x.ToPlayerId))
            .Select(x =>
                Of(MovementsKind.TRADE, x.Key.FromPlayerId, x.Key.ToPlayerId, 0, [.. x], valuer)
            )
            .ToList();
        var players = items
            .SelectMany(x => new[] { x.FromPlayerId, x.ToPlayerId })
            .Distinct()
            .ToList();

        // A side that offered nothing has no rows: it is there all the same, worth nothing.
        if (players.Count == 2 && transfers.Count == 1)
        {
            var only = transfers[0];

            transfers.Add(Of(MovementsKind.TRADE, only.To, only.From, 0, [], valuer));
        }

        return new OperationRead(
            rows[0].OperationId,
            MovementsKind.TRADE,
            When(rows),
            transfers,
            [
                .. items.Select(x => new Handover(
                    x.AssetId,
                    null,
                    x.DefinitionId,
                    x.FromPlayerId,
                    x.ToPlayerId,
                    valuer.Item(x.AssetKind, x.DefinitionId).Valued
                )),
            ]
        );
    }

    private static OperationRead? Gift(List<MovementEntity> rows, Valuer valuer)
    {
        var content = rows.Where(x => IsItem(x) && x.ToKind == nameof(PlaceKind.Holder)).ToList();
        var present = rows.FirstOrDefault(x =>
            IsItem(x)
            && x.FromKind == nameof(PlaceKind.None)
            && x.ToKind == nameof(PlaceKind.Inventory)
        );
        var paid = rows.Where(x =>
                x.AssetKind == CURRENCY
                && x.Currency == Valuer.CREDITS
                && x.FromKind == nameof(PlaceKind.Wallet)
            )
            .ToList();
        var sender = paid.Count > 0 ? paid[0].FromPlayerId : rows[0].ActorId;
        var receiver = (content.FirstOrDefault() ?? present)?.ToPlayerId ?? 0;

        if (sender <= 0 || receiver <= 0 || sender == receiver)
            return null;

        var transfer = Of(MovementsKind.GIFT, sender, receiver, 0, content, valuer);
        var credits = paid.Sum(x => x.Amount);

        // A gift's content valued at nothing the watch knows is worth what was paid for it.
        if (transfer.Value == 0 && credits > 0)
            transfer = transfer with
            {
                Value = credits,
                Credits = credits,
                Summary = $"{transfer.Summary}, bought for {Valuer.Credits(credits)}",
            };

        return new OperationRead(
            rows[0].OperationId,
            MovementsKind.GIFT,
            When(rows),
            [transfer],
            []
        );
    }

    private static OperationRead? Market(List<MovementEntity> rows, Valuer valuer)
    {
        var price = rows.FirstOrDefault(x =>
            x.AssetKind == CURRENCY
            && x.Cause == MovementCauses.MARKET_BUY
            && x.FromKind == nameof(PlaceKind.Wallet)
        );
        var listed = rows.FirstOrDefault(x =>
            IsItem(x) && x.FromKind == nameof(PlaceKind.MarketplaceOffer)
        );
        var bought = rows.FirstOrDefault(x =>
            IsItem(x)
            && x.FromKind == nameof(PlaceKind.None)
            && x.ToKind == nameof(PlaceKind.Inventory)
        );
        // The seller is the offer's owner: on the item leaving it, or on the revenue reaching it.
        var seller =
            listed?.FromPlayerId
            ?? rows.FirstOrDefault(x => x.ToKind == nameof(PlaceKind.MarketplaceOffer))?.ToPlayerId
            ?? 0;
        var buyer = price?.FromPlayerId ?? bought?.ToPlayerId ?? 0;

        if (buyer <= 0 || seller <= 0 || buyer == seller || bought is null)
            return null;

        var credits = price is { Currency: Valuer.CREDITS } ? price.Amount : 0;

        return new OperationRead(
            rows[0].OperationId,
            MovementsKind.MARKET,
            When(rows),
            [
                Of(MovementsKind.MARKET, buyer, seller, credits, [], valuer),
                Of(MovementsKind.MARKET, seller, buyer, 0, [bought], valuer),
            ],
            [
                new Handover(
                    bought.AssetId,
                    listed?.AssetId,
                    bought.DefinitionId,
                    seller,
                    buyer,
                    valuer.Item(bought.AssetKind, bought.DefinitionId).Valued
                ),
            ]
        );
    }

    private static Transfer Of(
        string kind,
        int from,
        int to,
        long credits,
        IReadOnlyList<MovementEntity> items,
        Valuer valuer
    )
    {
        long value = credits;
        int unvalued = 0,
            ltds = 0;

        foreach (var item in items)
        {
            var worth = valuer.Item(item.AssetKind, item.DefinitionId);

            value += worth.Credits;

            if (!worth.Valued)
                unvalued++;

            if (worth.Ltd)
                ltds++;
        }

        var summary = new List<string>();

        if (credits > 0)
            summary.Add(Valuer.Credits(credits));

        if (items.Count > 0)
            summary.Add(valuer.Names(items));

        return new Transfer(
            kind,
            from,
            to,
            value,
            credits,
            items,
            unvalued,
            ltds,
            summary.Count == 0 ? "nothing" : string.Join(" + ", summary)
        );
    }
}

/// <summary>The kinds of exchange, as <see cref="TransferEntity.Kind"/> stores them.</summary>
public static class MovementsKind
{
    public const string TRADE = TransferEntity.TRADE;
    public const string GIFT = TransferEntity.GIFT;
    public const string MARKET = TransferEntity.MARKET;
}
