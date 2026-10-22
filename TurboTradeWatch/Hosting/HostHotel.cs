using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans;
using Turbo.Primitives.Furniture.Providers;
using Turbo.Primitives.Orleans;
using Turbo.Primitives.Players;

namespace TurboTradeWatch.Hosting;

/// <summary>The host's player directory, players and furniture definitions.</summary>
public sealed class HostHotel(
    IGrainFactory grains,
    IFurnitureDefinitionProvider furniture,
    IConfiguration configuration
) : IWatchHotel
{
    public async Task<IReadOnlyDictionary<int, string>> NamesAsync(
        IEnumerable<int> ids,
        CancellationToken ct
    )
    {
        var wanted = ids.Where(x => x > 0).Distinct().ToList();

        if (wanted.Count == 0)
            return new Dictionary<int, string>();

        ImmutableDictionary<PlayerId, string> names;

        try
        {
            names = await grains
                .GetPlayerDirectoryGrain()
                .GetPlayerNamesAsync([.. wanted.Select(x => PlayerId.Parse(x))], ct)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            names = ImmutableDictionary<PlayerId, string>.Empty;
        }

        return wanted.ToDictionary(
            x => x,
            x =>
                names.TryGetValue(PlayerId.Parse(x), out var name) && !string.IsNullOrEmpty(name)
                    ? name
                    : $"#{x}"
        );
    }

    public async Task<int?> PlayerIdAsync(string name, CancellationToken ct)
    {
        try
        {
            return
                await grains
                    .GetPlayerDirectoryGrain()
                    .GetPlayerIdAsync(name, ct)
                    .ConfigureAwait(false)
                    is { } id
                ? id.Value
                : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<DateTime?> CreatedAtAsync(int playerId, CancellationToken ct)
    {
        if (playerId <= 0)
            return null;

        try
        {
            var summary = await grains
                .GetPlayerGrain(PlayerId.Parse(playerId))
                .GetSummaryAsync(ct)
                .ConfigureAwait(false);

            return summary.CreatedAt == default
                ? null
                : DateTime.SpecifyKind(summary.CreatedAt, DateTimeKind.Utc);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public string? FurniName(int definitionId) =>
        furniture.TryGetDefinition(definitionId)?.Name is { Length: > 0 } name ? name : null;

    public int? FurniId(string name) => furniture.TryGetDefinitionByName(name)?.Id;

    public string? Panel(string path) =>
        configuration["Turbo:Admin:PanelUrl"]?.TrimEnd('/') is { Length: > 0 } panel
            ? $"{panel}/{path}"
            : null;
}
