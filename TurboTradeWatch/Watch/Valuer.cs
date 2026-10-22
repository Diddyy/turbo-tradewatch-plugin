using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Turbo.Primitives.Furniture;
using Turbo.Primitives.Players.Enums.Wallet;
using Turbo.Primitives.Players.Wallet;
using TurboTradeWatch.Database;
using TurboTradeWatch.Hosting;

namespace TurboTradeWatch.Watch;

/// <summary>What an item is worth to the watch, and whether it knows at all.</summary>
public readonly record struct ItemValue(long Credits, bool Valued, bool Ltd);

/// <summary>
/// Values what moved, in credits. Credits count as themselves. A furni is worth, in this order:
/// its row in the value table (staff's figure), a credit furni's face value (its name), an LTD's
/// <see cref="ValuationConfig.LtdCredits"/>; anything else is unvalued (0, and counted as such).
/// Other currencies, pets and bots are never valued.
/// </summary>
public sealed class Valuer(
    IReadOnlyDictionary<int, ValueEntity> table,
    IWatchHotel hotel,
    ValuationConfig config
)
{
    public const string CREDITS = "credits";

    public ItemValue Item(string assetKind, int definitionId)
    {
        if (assetKind != nameof(Turbo.Primitives.Movements.Enums.AssetKind.Furni))
            return new ItemValue(0, false, false);

        var row = table.GetValueOrDefault(definitionId);
        var ltd = row?.IsLtd == true;

        if (row is { Credits: > 0 })
            return new ItemValue(row.Credits, true, ltd);

        if (
            config.CreditFurni
            && hotel.FurniName(definitionId) is { } name
            && CreditFurniValue.TryParse(name, out var face)
        )
            return new ItemValue(face, true, ltd);

        if (ltd && config.LtdCredits > 0)
            return new ItemValue(config.LtdCredits, true, true);

        return new ItemValue(0, false, ltd);
    }

    /// <summary>A furni's name for a summary: its definition's, or <c>furni #id</c>.</summary>
    public string Name(string assetKind, int definitionId) =>
        assetKind switch
        {
            "Pet" => "a pet",
            "Bot" => "a bot",
            _ => hotel.FurniName(definitionId) ?? $"furni #{definitionId}",
        };

    /// <summary><c>2 x CF_50_goldbar, throne</c>: items by name, most first.</summary>
    public string Names(IEnumerable<MovementEntity> items)
    {
        var list = items
            .GroupBy(x => Name(x.AssetKind, x.DefinitionId))
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Count() > 1 ? $"{x.Count()} x {x.Key}" : x.Key)
            .ToList();

        return list.Count == 0 ? "nothing" : string.Join(", ", list);
    }

    /// <summary>A currency's name: credits, duckets (points 0), diamonds (points 5), ...</summary>
    public static string Currency(CurrencyKind kind) =>
        kind.CurrencyType switch
        {
            CurrencyType.Credits => CREDITS,
            CurrencyType.Silver => "silver",
            CurrencyType.Emeralds => "emeralds",
            CurrencyType.ActivityPoints => kind.ActivityPointType switch
            {
                null or 0 => "duckets",
                5 => "diamonds",
                var type => $"points {type}",
            },
            _ => kind.CurrencyType.ToString().ToLowerInvariant(),
        };

    /// <summary>Whether a furni's stuff data is a limited edition's: a serial (<c>UniqueNumber</c>) above 0.</summary>
    public static bool IsLimited(string? extraData)
    {
        if (string.IsNullOrEmpty(extraData))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(extraData);

            return Find(doc.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }

        static bool Find(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object)
                return false;

            if (
                e.TryGetProperty("UniqueNumber", out var number)
                && number.TryGetInt32(out var n)
                && n > 0
            )
                return true;

            foreach (var property in e.EnumerateObject())
                if (Find(property.Value))
                    return true;

            return false;
        }
    }

    public static string Credits(long amount) =>
        $"{amount.ToString("N0", CultureInfo.InvariantCulture)} credits";
}
