using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TurboTradeWatch.Database;

/// <summary>
/// How far the watch has read core's outbox, written in the transaction of what it made of those
/// rows, and what it lost to pruning before it read it (the plugin was off longer than the outbox
/// keeps rows).
/// </summary>
[Table("cursor")]
public class CursorEntity
{
    public const string MOVEMENTS = "movements";

    [Key]
    [Column("name")]
    [MaxLength(32)]
    public required string Name { get; set; }

    [Column("last_id")]
    public long LastId { get; set; }

    /// <summary>Outbox ids pruned before the watch read them, counted over every gap.</summary>
    [Column("missed")]
    public long Missed { get; set; }

    [Column("last_gap")]
    [MaxLength(64)]
    public string? LastGap { get; set; }

    [Column("pruned_at")]
    public DateTime? PrunedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
