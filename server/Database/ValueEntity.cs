using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TurboTradeWatch.Database;

/// <summary>
/// What a furni is worth to the watch, in credits, set by staff on the Values page (rares, LTDs),
/// or an LTD the watch learnt by itself from a limited edition's serial (credits 0 until staff
/// value it). Credit furni need no row: their name says what they are worth.
/// </summary>
[Table("values")]
[Index(nameof(DefinitionId), IsUnique = true)]
public class ValueEntity
{
    public const string STAFF = "staff";
    public const string LEARNT = "learnt";

    public const int MAX_NOTE = 255;

    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("definition_id")]
    public int DefinitionId { get; set; }

    /// <summary>Its worth in credits; 0: not valued (an LTD then counts as <c>Valuation:LtdCredits</c>).</summary>
    [Column("credits")]
    public int Credits { get; set; }

    [Column("is_ltd")]
    public bool IsLtd { get; set; }

    /// <summary><see cref="STAFF"/> or <see cref="LEARNT"/>.</summary>
    [Column("source")]
    [MaxLength(16)]
    public string Source { get; set; } = STAFF;

    [Column("note")]
    [MaxLength(MAX_NOTE)]
    public string? Note { get; set; }

    [Column("updated_by_id")]
    public int UpdatedById { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
