using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TurboTradeWatch.Hosting;

/// <summary>What the watch asks of the hotel: names, a player's account age and the furni definitions.</summary>
public interface IWatchHotel
{
    /// <summary>Each player's name; a player the hotel does not know is <c>#id</c>.</summary>
    Task<IReadOnlyDictionary<int, string>> NamesAsync(IEnumerable<int> ids, CancellationToken ct);

    /// <summary>The player of that name, or null.</summary>
    Task<int?> PlayerIdAsync(string name, CancellationToken ct);

    /// <summary>When the player's account was made; null when the hotel cannot say.</summary>
    Task<DateTime?> CreatedAtAsync(int playerId, CancellationToken ct);

    /// <summary>A furni definition's name, or null when the hotel does not know it.</summary>
    string? FurniName(int definitionId);

    /// <summary>The furni definition of that name (any case), or null.</summary>
    int? FurniId(string name);

    /// <summary>A page of the admin panel (<c>players/12</c>); null when the host has no panel URL.</summary>
    string? Panel(string path);
}
