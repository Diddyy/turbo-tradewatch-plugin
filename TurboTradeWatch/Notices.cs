using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Turbo.Primitives.Plugins.Events;
using TurboTradeWatch.Database;
using TurboTradeWatch.Hosting;
using TurboTradeWatch.Watch;

namespace TurboTradeWatch;

/// <summary>
/// Tells other plugins of a new flag with a <see cref="PluginNoticeEvent"/> on the host's event
/// system (turbo-cloud's <c>docs/plugin-notices.md</c>): the Discord plugin posts it as
/// <c>plugin:tradewatch:flag.new</c> once staff route it, without this plugin knowing it. Keyed by
/// the flag's id, so a notice published twice is posted once. Nothing is published until the
/// plugin's start sets <see cref="Publisher"/>, and a publish that throws is logged: the flag stands
/// either way. When the host has an admin panel URL (<c>Turbo:Admin:PanelUrl</c>) a notice links
/// to the flag's page.
/// </summary>
public sealed class Notices(IWatchHotel hotel, ILogger<Notices> logger)
{
    public const string SOURCE = "tradewatch";
    public const string FLAG_NEW = "flag.new";

    /// <summary>The host's <c>IEventPublisher.PublishAsync</c> while the plugin runs; null: nothing is published.</summary>
    public Func<PluginNoticeEvent, CancellationToken, Task>? Publisher { get; set; }

    public async Task FlagAsync(FlagEntity flag, CancellationToken ct)
    {
        if (Publisher is not { } publish)
            return;

        try
        {
            var others = flag
                .OtherPlayerIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x, CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Where(x => x > 0)
                .ToList();
            var names = await hotel
                .NamesAsync([flag.PlayerId, .. others], ct)
                .ConfigureAwait(false);
            var id = flag.Id.ToString(CultureInfo.InvariantCulture);

            await publish(
                    Fit(
                        new PluginNoticeEvent
                        {
                            Source = SOURCE,
                            Topic = FLAG_NEW,
                            Title = $"{Rules.Title(flag.Rule)}: {flag.Summary}",
                            Text =
                                "Trade Watch flagged this for staff to look at. Nothing was done to anyone.",
                            Url = hotel.Panel($"plugin/tradewatch/flag/{id}"),
                            Fields =
                            [
                                new("Rule", Rules.Title(flag.Rule)),
                                new(
                                    "Player",
                                    $"{names.GetValueOrDefault(flag.PlayerId, $"#{flag.PlayerId}")} ({flag.PlayerId.ToString(CultureInfo.InvariantCulture)})"
                                ),
                                new(
                                    "With",
                                    others.Count == 0
                                        ? "-"
                                        : string.Join(
                                            ", ",
                                            others.Select(x => names.GetValueOrDefault(x, $"#{x}"))
                                        )
                                ),
                                new("Value", flag.Value > 0 ? Valuer.Credits(flag.Value) : "-"),
                                new("Flag", id),
                            ],
                            Level = PluginNoticeLevel.Warning,
                            Key = id,
                        }
                    ),
                    ct
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Plugin notice for trade watch flag {Id} could not be published",
                flag.Id
            );
        }
    }

    /// <summary>The notice within <see cref="PluginNoticeEvent"/>'s limits.</summary>
    internal static PluginNoticeEvent Fit(PluginNoticeEvent notice) =>
        notice with
        {
            Topic = Cut(notice.Topic, PluginNoticeEvent.MAX_TOPIC_LENGTH),
            Title = Cut(notice.Title, PluginNoticeEvent.MAX_TITLE_LENGTH),
            Text = notice.Text is null ? null : Cut(notice.Text, PluginNoticeEvent.MAX_TEXT_LENGTH),
            Fields = notice.Fields.IsDefaultOrEmpty
                ? []
                : [.. notice.Fields.Take(PluginNoticeEvent.MAX_FIELDS)],
        };

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
