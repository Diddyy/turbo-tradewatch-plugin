using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TurboTradeWatch.Database;

/// <summary>
/// What one player handed another in one operation, valued when it was read: one side of a trade,
/// a catalogue gift, a marketplace sale's price (buyer to seller) or its item (seller to buyer).
/// The funnel rule counts these, and a player's history lists them.
/// </summary>
[Table("transfers")]
[Index(nameof(OperationId), nameof(FromPlayerId), nameof(ToPlayerId), IsUnique = true)]
[Index(nameof(ToPlayerId), nameof(OccurredAt))]
[Index(nameof(FromPlayerId), nameof(OccurredAt))]
public class TransferEntity
{
    public const string TRADE = "trade";
    public const string GIFT = "gift";
    public const string MARKET = "market";

    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("operation_id")]
    [MaxLength(160)]
    public required string OperationId { get; set; }

    /// <summary><see cref="TRADE"/>, <see cref="GIFT"/> or <see cref="MARKET"/>.</summary>
    [Column("kind")]
    [MaxLength(16)]
    public required string Kind { get; set; }

    [Column("from_player_id")]
    public int FromPlayerId { get; set; }

    [Column("to_player_id")]
    public int ToPlayerId { get; set; }

    [Column("occurred_at")]
    public DateTime OccurredAt { get; set; }

    /// <summary>What it was worth, in credits: the credits moved and the valued items.</summary>
    [Column("value")]
    public long Value { get; set; }

    [Column("credits")]
    public long Credits { get; set; }

    [Column("items")]
    public int Items { get; set; }

    /// <summary>Items with no value the watch knows (not credit furni, not in the value table).</summary>
    [Column("unvalued")]
    public int Unvalued { get; set; }

    [Column("ltds")]
    public int Ltds { get; set; }

    /// <summary>What was handed over, by name: <c>2 x CF_50_goldbar, throne</c>.</summary>
    [Column("summary")]
    [MaxLength(255)]
    public string Summary { get; set; } = string.Empty;
}
