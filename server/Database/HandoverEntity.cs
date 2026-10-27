using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TurboTradeWatch.Database;

/// <summary>
/// An item passing from one player to another (a trade, a marketplace sale). <see cref="ChainId"/>
/// is the item as it was first seen: a sale makes the item again under a new id, which keeps the
/// listed item's chain, so one item's hands are one chain however often it was sold.
/// </summary>
[Table("handovers")]
[Index(nameof(OperationId), nameof(AssetId), IsUnique = true)]
[Index(nameof(ChainId), nameof(OccurredAt))]
[Index(nameof(AssetId))]
public class HandoverEntity
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("chain_id")]
    public long ChainId { get; set; }

    [Column("asset_id")]
    public long AssetId { get; set; }

    [Column("definition_id")]
    public int DefinitionId { get; set; }

    [Column("operation_id")]
    [MaxLength(160)]
    public required string OperationId { get; set; }

    [Column("from_player_id")]
    public int FromPlayerId { get; set; }

    [Column("to_player_id")]
    public int ToPlayerId { get; set; }

    [Column("occurred_at")]
    public DateTime OccurredAt { get; set; }
}
