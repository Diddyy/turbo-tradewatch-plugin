using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TurboTradeWatch.Database;

/// <summary>
/// One movement the watch keeps from core's outbox: those of trades, catalogue gifts and
/// marketplace sales (and a listing's item onto its offer, which ties a sold item to the one
/// listed). They are the evidence a flag's timeline and a player's history show; the outbox itself
/// is pruned after a few days. (operation, seq) is unique: a movement delivered twice is kept once.
/// </summary>
[Table("movements")]
[Index(nameof(OperationId), nameof(Seq), IsUnique = true)]
[Index(nameof(AssetId))]
[Index(nameof(OccurredAt))]
public class MovementEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("outbox_id")]
    public long OutboxId { get; set; }

    [Column("operation_id")]
    [MaxLength(160)]
    public required string OperationId { get; set; }

    [Column("seq")]
    public short Seq { get; set; }

    [Column("occurred_at")]
    public DateTime OccurredAt { get; set; }

    [Column("cause")]
    [MaxLength(48)]
    public required string Cause { get; set; }

    [Column("cause_ref")]
    [MaxLength(64)]
    public string? CauseRef { get; set; }

    [Column("actor_id")]
    public int ActorId { get; set; }

    /// <summary><c>Furni</c>, <c>Pet</c>, <c>Bot</c>, <c>Currency</c>, ...</summary>
    [Column("asset_kind")]
    [MaxLength(16)]
    public required string AssetKind { get; set; }

    [Column("asset_id")]
    public long AssetId { get; set; }

    [Column("definition_id")]
    public int DefinitionId { get; set; }

    /// <summary>A currency's name (credits, duckets, diamonds, ...); null for anything else.</summary>
    [Column("currency")]
    [MaxLength(16)]
    public string? Currency { get; set; }

    [Column("amount")]
    public long Amount { get; set; }

    [Column("from_player_id")]
    public int FromPlayerId { get; set; }

    [Column("from_kind")]
    [MaxLength(24)]
    public required string FromKind { get; set; }

    [Column("from_place_id")]
    public long FromPlaceId { get; set; }

    [Column("to_player_id")]
    public int ToPlayerId { get; set; }

    [Column("to_kind")]
    [MaxLength(24)]
    public required string ToKind { get; set; }

    [Column("to_place_id")]
    public long ToPlaceId { get; set; }
}
