using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TurboTradeWatch.Database;

/// <summary>
/// Something a rule found, kept with its evidence (the operations, the values, the rule's figures)
/// for staff to look at. The watch never acts on a player: staff mark a flag actioned (they did
/// something, on the player's page) or dismissed, with a note. <see cref="DedupeKey"/> is unique,
/// so a batch read again never flags the same thing twice; a funnel or a chain still going on
/// adds to its open flag rather than making another.
/// </summary>
[Table("flags")]
[Index(nameof(DedupeKey), IsUnique = true)]
[Index(nameof(Status), nameof(Id))]
[Index(nameof(Rule), nameof(SubjectRef), nameof(LastSeenAt))]
[Index(nameof(PlayerId))]
public class FlagEntity
{
    public const string OPEN = "open";
    public const string ACTIONED = "actioned";
    public const string DISMISSED = "dismissed";

    public const int MAX_SUMMARY = 255;
    public const int MAX_NOTE = 1000;
    public const int MAX_OPERATIONS = 50;

    [Key]
    [Column("id")]
    public long Id { get; set; }

    /// <summary>A rule's key (<see cref="Watch.Rules"/>).</summary>
    [Column("rule")]
    [MaxLength(24)]
    public required string Rule { get; set; }

    [Column("dedupe_key")]
    [MaxLength(200)]
    public required string DedupeKey { get; set; }

    /// <summary>What a still-going case adds to: <c>player:12</c> (a funnel), <c>item:345</c> (a chain).</summary>
    [Column("subject_ref")]
    [MaxLength(64)]
    public string SubjectRef { get; set; } = string.Empty;

    /// <summary>The player the flag is about: who gained.</summary>
    [Column("player_id")]
    public int PlayerId { get; set; }

    /// <summary>The other players in it, comma separated.</summary>
    [Column("other_player_ids")]
    [MaxLength(255)]
    public string OtherPlayerIds { get; set; } = string.Empty;

    /// <summary>The value in credits the rule measured.</summary>
    [Column("value")]
    public long Value { get; set; }

    [Column("summary")]
    [MaxLength(MAX_SUMMARY)]
    public string Summary { get; set; } = string.Empty;

    /// <summary>The rule's figures and what was and was not valued, one line each.</summary>
    [Column("evidence", TypeName = "text")]
    public string Evidence { get; set; } = string.Empty;

    /// <summary>The operations behind it, comma separated, at most <see cref="MAX_OPERATIONS"/>.</summary>
    [Column("operations", TypeName = "text")]
    public string Operations { get; set; } = string.Empty;

    [Column("status")]
    [MaxLength(16)]
    public string Status { get; set; } = OPEN;

    [Column("note")]
    [MaxLength(MAX_NOTE)]
    public string? Note { get; set; }

    [Column("handled_by_id")]
    public int? HandledById { get; set; }

    [Column("handled_at")]
    public DateTime? HandledAt { get; set; }

    /// <summary>When the first movement it is about happened.</summary>
    [Column("occurred_at")]
    public DateTime OccurredAt { get; set; }

    /// <summary>When the last movement it is about happened.</summary>
    [Column("last_seen_at")]
    public DateTime LastSeenAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    /// <summary>When its notice was published; null: not yet (a restart publishes it).</summary>
    [Column("notified_at")]
    public DateTime? NotifiedAt { get; set; }
}
