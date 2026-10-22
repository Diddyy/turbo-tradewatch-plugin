using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Turbo.Contracts.Plugins.Admin;
using TurboTradeWatch.Database;
using TurboTradeWatch.Hosting;
using TurboTradeWatch.Watch;

namespace TurboTradeWatch;

/// <summary>
/// The Trade Watch pages of the admin panel: the flags (filters; a flag's evidence, its movement
/// timeline and players, and its status and note), the rules with their thresholds, the value
/// table, and a player's trade history. The thresholds themselves are on the panel's Settings page
/// under <c>Turbo:TradeWatch</c>. Seeing them needs <c>plugin.tradewatch.view</c>; handling flags
/// and changing the value table <c>plugin.tradewatch.manage</c>. No one holds them until a hotel
/// grants them.
/// </summary>
public sealed class TradeWatchAdmin(
    IServiceScopeFactory scopes,
    IWatchHotel hotel,
    IWatchSettings settings,
    TimeProvider time
) : IPluginAdmin
{
    public const int PAGE_SIZE = 25;

    private static readonly PluginAdminColumn[] FLAG_COLUMNS =
    [
        new("summary", "Flag"),
        new("rule", "Rule"),
        new("player", "Player"),
        new("value", "Value (credits)", "number"),
        new("status", "Status", "badge"),
        new("occurredAt", "When", "time"),
    ];

    private static readonly PluginAdminColumn[] TRANSFER_COLUMNS =
    [
        new("what", "Handed over"),
        new("kind", "How"),
        new("from", "From"),
        new("to", "To"),
        new("value", "Value (credits)", "number"),
        new("unvalued", "Not valued", "number"),
        new("occurredAt", "When", "time"),
    ];

    public void Configure(IPluginAdminBuilder admin)
    {
        var view = admin.Node(
            "view",
            "See trade watch flags, their evidence and players' trade history"
        );
        var manage = admin.Node(
            "manage",
            "Mark trade watch flags actioned or dismissed, and change the value table"
        );

        admin.Map("GET", "stats", view, (_, ct) => StatsAsync(ct));
        admin.Map("GET", "flags", view, FlagsAsync);
        admin.Map("GET", "flags/{id}", view, FlagAsync);
        admin.Map("GET", "flags/{id}/evidence", view, FlagEvidenceAsync);
        admin.Map("GET", "flags/{id}/movements", view, FlagMovementsAsync);
        admin.Map("GET", "flags/{id}/players", view, FlagPlayersAsync);
        admin.Map("POST", "flags/{id}", manage, HandleAsync);
        admin.Map("GET", "rules", view, (_, ct) => Task.FromResult(RulesResult()));
        admin.Map("GET", "values", view, ValuesAsync);
        admin.Map("POST", "values", manage, AddValueAsync);
        admin.Map("GET", "values/{id}", view, ValueAsync);
        admin.Map("POST", "values/{id}", manage, EditValueAsync);
        admin.Map("DELETE", "values/{id}", manage, RemoveValueAsync);
        admin.Map("GET", "history", view, HistoryAsync);
        admin.Map("GET", "players/{id}", view, PlayerAsync);
        admin.Map("GET", "players/{id}/history", view, PlayerHistoryAsync);
        admin.Map("GET", "players/{id}/flags", view, PlayerFlagsAsync);

        admin.Page(
            new PluginAdminPage(
                "flags",
                "Trade Watch",
                view,
                [
                    new PluginAdminBlock("stats", Endpoint: "stats"),
                    new PluginAdminBlock(
                        "table",
                        Title: "Flags",
                        Endpoint: "flags",
                        Columns: FLAG_COLUMNS,
                        Filters:
                        [
                            new PluginAdminField(
                                "status",
                                "Status",
                                "select",
                                Options:
                                [
                                    "",
                                    FlagEntity.OPEN,
                                    FlagEntity.ACTIONED,
                                    FlagEntity.DISMISSED,
                                ]
                            ),
                            new PluginAdminField(
                                "rule",
                                "Rule",
                                "select",
                                Options: ["", .. Rules.ALL]
                            ),
                            new PluginAdminField("q", "Player name or id"),
                        ],
                        ItemPage: "flag"
                    ),
                    new PluginAdminBlock(
                        "text",
                        Title: "How it works",
                        Text: "Trade Watch reads every trade, catalogue gift and marketplace sale from the server's movement record (**Turbo:Movements:Enabled** must be on) and flags what looks like a scam, a dupe or value pushed between accounts. It never does anything to a player: look at the evidence, act on the player's page if needed, then mark the flag **actioned** or **dismissed** with a note.\n\n"
                            + "**What is valued**: credits, credit furni at the credits their name says (CF_50_goldbar: 50), furni in the value table at staff's figure, and LTDs at **Turbo:TradeWatch:Valuation:LtdCredits** until valued. **Not valued**: any other furni, pets, bots and other currencies; each flag says how many items it could not value.\n\n"
                            + "Each new flag is a plugin notice: the Discord plugin posts it as **plugin:tradewatch:flag.new** once routed."
                    ),
                ],
                "Suspicious trades, gifts and sales, with their evidence."
            )
        );
        admin.Page(
            new PluginAdminPage(
                "flag",
                "Trade Watch flag",
                view,
                [
                    new PluginAdminBlock(
                        "detail",
                        Endpoint: "flags/{id}",
                        Columns:
                        [
                            new("summary", "Flag"),
                            new("rule", "Rule"),
                            new("player", "Player"),
                            new("playerPage", "Player page"),
                            new("others", "With"),
                            new("value", "Value (credits)", "number"),
                            new("status", "Status", "badge"),
                            new("note", "Note"),
                            new("handledBy", "Handled by"),
                            new("handledAt", "Handled", "time"),
                            new("occurredAt", "First movement", "time"),
                            new("lastSeenAt", "Last movement", "time"),
                            new("createdAt", "Flagged", "time"),
                        ]
                    ),
                    new PluginAdminBlock(
                        "table",
                        Title: "Evidence",
                        Endpoint: "flags/{id}/evidence",
                        Columns: [new("line", "What the rule saw")]
                    ),
                    new PluginAdminBlock(
                        "form",
                        Title: "Handle",
                        Text: "Actioned: you did something about it (on the player's page). Dismissed: nothing wrong. Open: look again later.",
                        Fields:
                        [
                            new PluginAdminField(
                                "status",
                                "Status",
                                "select",
                                Required: true,
                                Options:
                                [
                                    FlagEntity.OPEN,
                                    FlagEntity.ACTIONED,
                                    FlagEntity.DISMISSED,
                                ]
                            ),
                            new PluginAdminField(
                                "note",
                                "Note",
                                "textarea",
                                Max: FlagEntity.MAX_NOTE
                            ),
                        ],
                        Submit: new PluginAdminAction("Save", "POST", "flags/{id}", Node: manage)
                    ),
                    new PluginAdminBlock(
                        "table",
                        Title: "Movements",
                        Endpoint: "flags/{id}/movements",
                        Columns:
                        [
                            new("occurredAt", "When", "time"),
                            new("cause", "Cause"),
                            new("what", "What"),
                            new("from", "From"),
                            new("to", "To"),
                            new("value", "Value (credits)", "number"),
                            new("operation", "Operation"),
                        ]
                    ),
                    new PluginAdminBlock(
                        "table",
                        Title: "Players",
                        Endpoint: "flags/{id}/players",
                        Columns:
                        [
                            new("player", "Player"),
                            new("role", "In it"),
                            new("flags", "Flags", "number"),
                        ],
                        ItemPage: "player"
                    ),
                ],
                Nav: false
            )
        );
        admin.Page(
            new PluginAdminPage(
                "rules",
                "Trade Watch rules",
                view,
                [
                    new PluginAdminBlock(
                        "table",
                        Endpoint: "rules",
                        Columns:
                        [
                            new("title", "Rule"),
                            new("enabled", "On", "bool"),
                            new("thresholds", "Thresholds"),
                            new("section", "Settings section"),
                        ]
                    ),
                    new PluginAdminBlock(
                        "text",
                        Text: "Each rule is turned on or off and tuned on the **Settings** page, in its section. A change applies to the next movements read. The defaults are cautious: a normal hotel sees few flags."
                    ),
                ],
                "What the watch looks for, and how much it takes."
            )
        );
        admin.Page(
            new PluginAdminPage(
                "values",
                "Trade Watch values",
                view,
                [
                    new PluginAdminBlock(
                        "table",
                        Endpoint: "values",
                        Columns:
                        [
                            new("furni", "Furni"),
                            new("credits", "Credits", "number"),
                            new("isLtd", "LTD", "bool"),
                            new("source", "Source"),
                            new("note", "Note"),
                            new("updatedAt", "Changed", "time"),
                        ],
                        Filters: [new PluginAdminField("q", "Furni")],
                        ItemPage: "value"
                    ),
                    new PluginAdminBlock(
                        "form",
                        Title: "Value a furni",
                        Text: "What a rare or an LTD is worth in credits. Credit furni need no row. An LTD the watch saw made with a serial is added by itself (learnt), with credits 0 until valued here.",
                        Fields: ValueFields(true),
                        Submit: new PluginAdminAction("Add", "POST", "values", Node: manage)
                    ),
                ],
                "What furni are worth to the watch."
            )
        );
        admin.Page(
            new PluginAdminPage(
                "value",
                "Trade Watch value",
                view,
                [
                    new PluginAdminBlock(
                        "detail",
                        Endpoint: "values/{id}",
                        Columns:
                        [
                            new("furni", "Furni"),
                            new("credits", "Credits", "number"),
                            new("isLtd", "LTD", "bool"),
                            new("source", "Source"),
                            new("note", "Note"),
                            new("updatedBy", "Changed by"),
                            new("updatedAt", "Changed", "time"),
                        ]
                    ),
                    new PluginAdminBlock(
                        "actions",
                        Actions:
                        [
                            new PluginAdminAction(
                                "Remove",
                                "DELETE",
                                "values/{id}",
                                "Remove this value? The furni counts as not valued again (an LTD is learnt again when the watch sees one made).",
                                Danger: true,
                                Node: manage,
                                Then: "back"
                            ),
                        ]
                    ),
                    new PluginAdminBlock(
                        "form",
                        Title: "Change",
                        Fields: ValueFields(false),
                        Submit: new PluginAdminAction("Save", "POST", "values/{id}", Node: manage)
                    ),
                ],
                Nav: false
            )
        );
        admin.Page(
            new PluginAdminPage(
                "history",
                "Trade history",
                view,
                [
                    new PluginAdminBlock(
                        "table",
                        Endpoint: "history",
                        Columns: TRANSFER_COLUMNS,
                        Filters: [new PluginAdminField("player", "Player name or id")]
                    ),
                ],
                "What a player handed over and received: trades, gifts and marketplace sales the watch has kept."
            )
        );
        admin.Page(
            new PluginAdminPage(
                "player",
                "Trade Watch player",
                view,
                [
                    new PluginAdminBlock(
                        "detail",
                        Endpoint: "players/{id}",
                        Columns:
                        [
                            new("player", "Player"),
                            new("playerPage", "Player page"),
                            new("createdAt", "Account made", "time"),
                            new("given", "Given, 30 days (credits)", "number"),
                            new("received", "Received, 30 days (credits)", "number"),
                            new("partners", "Partners, 30 days", "number"),
                            new("flags", "Flags", "number"),
                        ]
                    ),
                    new PluginAdminBlock(
                        "table",
                        Title: "Flags",
                        Endpoint: "players/{id}/flags",
                        Columns: FLAG_COLUMNS,
                        ItemPage: "flag"
                    ),
                    new PluginAdminBlock(
                        "table",
                        Title: "Trade history",
                        Endpoint: "players/{id}/history",
                        Columns: TRANSFER_COLUMNS
                    ),
                ],
                Nav: false
            )
        );
    }

    private static List<PluginAdminField> ValueFields(bool adding)
    {
        var fields = new List<PluginAdminField>();

        if (adding)
            fields.Add(
                new PluginAdminField(
                    "furni",
                    "Furni",
                    Required: true,
                    Hint: "A furni definition's name, as :giveitem"
                )
            );

        fields.Add(new PluginAdminField("credits", "Credits", "number", Min: 0, Max: int.MaxValue));
        fields.Add(new PluginAdminField("isLtd", "LTD", "bool"));
        fields.Add(new PluginAdminField("note", "Note", Max: ValueEntity.MAX_NOTE));

        return fields;
    }

    private async Task<T> WithDbAsync<T>(Func<TradeWatchDbContext, Task<T>> work)
    {
        await using var scope = scopes.CreateAsyncScope();

        return await work(scope.ServiceProvider.GetRequiredService<TradeWatchDbContext>())
            .ConfigureAwait(false);
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private Task<PluginAdminResult> StatsAsync(CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            var week = Now.AddDays(-7);
            var open = await db
                .Flags.CountAsync(x => x.Status == FlagEntity.OPEN, ct)
                .ConfigureAwait(false);
            var flagged = await db
                .Flags.CountAsync(x => x.CreatedAt >= week, ct)
                .ConfigureAwait(false);
            var exchanges = await db
                .Transfers.Where(x => x.OccurredAt >= week)
                .Select(x => x.OperationId)
                .Distinct()
                .CountAsync(ct)
                .ConfigureAwait(false);
            var cursor = await db
                .Cursors.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Name == CursorEntity.MOVEMENTS, ct)
                .ConfigureAwait(false);

            return PluginAdminResult.Ok(
                new object[]
                {
                    new { label = "Open flags", value = open },
                    new { label = "Flagged (7 days)", value = flagged },
                    new { label = "Exchanges read (7 days)", value = exchanges },
                    new { label = "Read up to (outbox id)", value = cursor?.LastId ?? 0 },
                    new { label = "Missed (pruned before read)", value = cursor?.Missed ?? 0 },
                }
            );
        });

    // Flags

    private Task<PluginAdminResult> FlagsAsync(PluginAdminRequest request, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            var query = db.Flags.AsNoTracking();

            if (request.Query.GetValueOrDefault("status") is { Length: > 0 } status)
                query = query.Where(x => x.Status == status);

            if (request.Query.GetValueOrDefault("rule") is { Length: > 0 } rule)
                query = query.Where(x => x.Rule == rule);

            if (request.Query.GetValueOrDefault("q")?.Trim() is { Length: > 0 } q)
            {
                var player = await PlayerOfAsync(q, ct).ConfigureAwait(false) ?? -1;
                var text = player.ToString(CultureInfo.InvariantCulture);

                query = query.Where(x =>
                    x.PlayerId == player
                    || x.OtherPlayerIds == text
                    || x.OtherPlayerIds.StartsWith(text + ",")
                    || x.OtherPlayerIds.EndsWith("," + text)
                    || x.OtherPlayerIds.Contains("," + text + ",")
                );
            }

            return await FlagPageAsync(query, request, ct).ConfigureAwait(false);
        });

    private async Task<PluginAdminResult> FlagPageAsync(
        IQueryable<FlagEntity> query,
        PluginAdminRequest request,
        CancellationToken ct
    )
    {
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var page = Page(request);
        var rows = await query
            .OrderByDescending(x => x.Id)
            .Skip((page - 1) * PAGE_SIZE)
            .Take(PAGE_SIZE)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var names = await hotel.NamesAsync(rows.Select(x => x.PlayerId), ct).ConfigureAwait(false);

        return PluginAdminResult.Ok(
            new
            {
                items = rows.Select(x => new
                {
                    id = x.Id,
                    summary = x.Summary,
                    rule = Rules.Title(x.Rule),
                    player = Name(names, x.PlayerId),
                    value = x.Value,
                    status = x.Status,
                    occurredAt = x.OccurredAt,
                }),
                total,
                pageSize = PAGE_SIZE,
            }
        );
    }

    private Task<PluginAdminResult> FlagAsync(PluginAdminRequest request, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            if (await FindFlagAsync(db, request, ct).ConfigureAwait(false) is not { } x)
                return PluginAdminResult.NotFound("No such flag.");

            var others = Ids(x.OtherPlayerIds);
            var names = await hotel
                .NamesAsync(
                    [x.PlayerId, .. others, .. x.HandledById is { } h ? new[] { h } : []],
                    ct
                )
                .ConfigureAwait(false);

            return PluginAdminResult.Ok(
                new
                {
                    id = x.Id,
                    summary = x.Summary,
                    rule = Rules.Title(x.Rule),
                    player = $"{Name(names, x.PlayerId)} ({x.PlayerId})",
                    playerPage = hotel.Panel($"players/{x.PlayerId}") ?? $"/players/{x.PlayerId}",
                    others = others.Count == 0
                        ? "-"
                        : string.Join(", ", others.Select(o => $"{Name(names, o)} ({o})")),
                    value = x.Value,
                    status = x.Status,
                    note = x.Note ?? "",
                    handledBy = x.HandledById is { } by ? Name(names, by) : "-",
                    handledAt = x.HandledAt,
                    occurredAt = x.OccurredAt,
                    lastSeenAt = x.LastSeenAt,
                    createdAt = x.CreatedAt,
                }
            );
        });

    private Task<PluginAdminResult> FlagEvidenceAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            if (await FindFlagAsync(db, request, ct).ConfigureAwait(false) is not { } flag)
                return PluginAdminResult.NotFound("No such flag.");

            var lines = flag.Evidence.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            return PluginAdminResult.Ok(
                new
                {
                    items = lines.Select((line, i) => new { id = i + 1, line }),
                    total = lines.Length,
                    pageSize = Math.Max(lines.Length, PAGE_SIZE),
                }
            );
        });

    private Task<PluginAdminResult> FlagMovementsAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            if (await FindFlagAsync(db, request, ct).ConfigureAwait(false) is not { } flag)
                return PluginAdminResult.NotFound("No such flag.");

            var operations = flag
                .Operations.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            var rows = await db
                .Movements.AsNoTracking()
                .Where(x => operations.Contains(x.OperationId))
                .OrderBy(x => x.OccurredAt)
                .ThenBy(x => x.OperationId)
                .ThenBy(x => x.Seq)
                .Take(500)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var names = await hotel
                .NamesAsync(rows.SelectMany(x => new[] { x.FromPlayerId, x.ToPlayerId }), ct)
                .ConfigureAwait(false);
            var valuer = await ValuerAsync(db, ct).ConfigureAwait(false);

            return PluginAdminResult.Ok(
                new
                {
                    items = rows.Select(x => new
                    {
                        id = x.Id,
                        occurredAt = x.OccurredAt,
                        cause = x.Cause,
                        what = x.Currency is { } currency
                            ? $"{x.Amount.ToString("N0", CultureInfo.InvariantCulture)} {currency}"
                            : $"{valuer.Name(x.AssetKind, x.DefinitionId)} (item {x.AssetId})",
                        from = Place(names, x.FromPlayerId, x.FromKind, x.FromPlaceId),
                        to = Place(names, x.ToPlayerId, x.ToKind, x.ToPlaceId),
                        value = x.Currency == Valuer.CREDITS ? x.Amount
                        : x.Currency is null ? valuer.Item(x.AssetKind, x.DefinitionId).Credits
                        : 0,
                        operation = x.OperationId,
                    }),
                    total = rows.Count,
                    pageSize = Math.Max(rows.Count, PAGE_SIZE),
                }
            );
        });

    private Task<PluginAdminResult> FlagPlayersAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            if (await FindFlagAsync(db, request, ct).ConfigureAwait(false) is not { } flag)
                return PluginAdminResult.NotFound("No such flag.");

            List<int> players =
            [
                flag.PlayerId,
                .. Ids(flag.OtherPlayerIds).Where(x => x != flag.PlayerId),
            ];
            var names = await hotel.NamesAsync(players, ct).ConfigureAwait(false);
            var others = players.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToList();
            var counts = new Dictionary<int, int>();

            foreach (var player in players)
                counts[player] = await db
                    .Flags.CountAsync(x => x.PlayerId == player, ct)
                    .ConfigureAwait(false);

            return PluginAdminResult.Ok(
                new
                {
                    items = players.Select(x => new
                    {
                        id = x,
                        player = $"{Name(names, x)} ({x})",
                        role = x == flag.PlayerId ? "flagged" : "with them",
                        flags = counts[x],
                    }),
                    total = players.Count,
                    pageSize = Math.Max(players.Count, PAGE_SIZE),
                }
            );
        });

    private Task<PluginAdminResult> HandleAsync(PluginAdminRequest request, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            if (
                await FindFlagAsync(db, request, ct, tracked: true).ConfigureAwait(false)
                is not { } flag
            )
                return PluginAdminResult.NotFound("No such flag.");

            var status = Text(request, "status")?.Trim();

            if (status is not (FlagEntity.OPEN or FlagEntity.ACTIONED or FlagEntity.DISMISSED))
                return PluginAdminResult.BadRequest("A flag is open, actioned or dismissed.");

            var note = Text(request, "note")?.Trim();

            if (note?.Length > FlagEntity.MAX_NOTE)
                return PluginAdminResult.BadRequest(
                    $"A note is at most {FlagEntity.MAX_NOTE} characters."
                );

            flag.Status = status;
            flag.Note = string.IsNullOrEmpty(note) ? flag.Note : note;
            flag.HandledById = request.PlayerId;
            flag.HandledAt = Now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return PluginAdminResult.Ok(new { id = flag.Id, status = flag.Status });
        });

    // Rules

    internal PluginAdminResult RulesResult()
    {
        var s = settings.Current;
        var rows = new[]
        {
            new
            {
                id = Rules.LOPSIDED,
                title = Rules.Title(Rules.LOPSIDED),
                enabled = s.Lopsided.Enabled,
                thresholds = $"richer side {Valuer.Credits(s.Lopsided.MinValue)} or more, the other {s.Lopsided.MaxReturnPercent}% of it or less; unvalued items on the poorer side {(s.Lopsided.UnvaluedIsFair ? "make it fair" : "count as nothing")}; marketplace sales {(s.Lopsided.Marketplace ? "too" : "not looked at")}",
                section = LopsidedConfig.SECTION_NAME,
            },
            new
            {
                id = Rules.NEW_ACCOUNT,
                title = Rules.Title(Rules.NEW_ACCOUNT),
                enabled = s.NewAccount.Enabled,
                thresholds = $"an account younger than {s.NewAccount.MaxAgeDays} days receiving {Valuer.Credits(s.NewAccount.MinValue)} or more at once",
                section = NewAccountConfig.SECTION_NAME,
            },
            new
            {
                id = Rules.FUNNEL,
                title = Rules.Title(Rules.FUNNEL),
                enabled = s.Funnel.Enabled,
                thresholds = $"{s.Funnel.MinSenders} or more accounts giving one account {Valuer.Credits(s.Funnel.MinValue)} or more each within {s.Funnel.WindowHours} h",
                section = FunnelConfig.SECTION_NAME,
            },
            new
            {
                id = Rules.RAPID_FLIPS,
                title = Rules.Title(Rules.RAPID_FLIPS),
                enabled = s.RapidFlips.Enabled,
                thresholds = $"one {(s.RapidFlips.ValuedOnly ? "valued " : "")}item held by {s.RapidFlips.MinAccounts} or more accounts within {s.RapidFlips.WindowMinutes} min",
                section = RapidFlipsConfig.SECTION_NAME,
            },
            new
            {
                id = "valuation",
                title = "Valuation",
                enabled = s.Watch.Enabled,
                thresholds = $"credit furni {(s.Valuation.CreditFurni ? "at their face value" : "not valued")}; an LTD with no value of its own {(s.Valuation.LtdCredits > 0 ? Valuer.Credits(s.Valuation.LtdCredits) : "not valued")}",
                section = ValuationConfig.SECTION_NAME,
            },
        };

        return PluginAdminResult.Ok(
            new
            {
                items = rows,
                total = rows.Length,
                pageSize = PAGE_SIZE,
            }
        );
    }

    // Values

    private Task<PluginAdminResult> ValuesAsync(PluginAdminRequest request, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            var rows = await db
                .Values.AsNoTracking()
                .OrderByDescending(x => x.Credits)
                .ThenBy(x => x.DefinitionId)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var q = request.Query.GetValueOrDefault("q")?.Trim();
            var items = rows.Select(ValueRow)
                .Where(x =>
                    string.IsNullOrEmpty(q)
                    || x.furni.Contains(q, StringComparison.OrdinalIgnoreCase)
                )
                .ToList();
            var page = Page(request);

            return PluginAdminResult.Ok(
                new
                {
                    items = items.Skip((page - 1) * PAGE_SIZE).Take(PAGE_SIZE),
                    total = items.Count,
                    pageSize = PAGE_SIZE,
                }
            );
        });

    private sealed record ValueView(
        int id,
        string furni,
        int credits,
        bool isLtd,
        string source,
        string note,
        DateTime updatedAt
    );

    private ValueView ValueRow(ValueEntity x) =>
        new(
            x.Id,
            hotel.FurniName(x.DefinitionId) ?? $"furni #{x.DefinitionId}",
            x.Credits,
            x.IsLtd,
            x.Source,
            x.Note ?? "",
            x.UpdatedAt
        );

    private Task<PluginAdminResult> ValueAsync(PluginAdminRequest request, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            if (await FindValueAsync(db, request, ct).ConfigureAwait(false) is not { } x)
                return PluginAdminResult.NotFound("No such value.");

            var names = await hotel.NamesAsync([x.UpdatedById], ct).ConfigureAwait(false);
            var row = ValueRow(x);

            return PluginAdminResult.Ok(
                new
                {
                    row.id,
                    row.furni,
                    row.credits,
                    row.isLtd,
                    row.source,
                    row.note,
                    updatedBy = x.UpdatedById > 0 ? Name(names, x.UpdatedById) : "the watch",
                    row.updatedAt,
                }
            );
        });

    private Task<PluginAdminResult> AddValueAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            var name = Text(request, "furni")?.Trim() ?? "";

            if (hotel.FurniId(name) is not { } definition)
                return PluginAdminResult.BadRequest($"The hotel has no furni called {name}.");

            if (
                await db
                    .Values.AnyAsync(x => x.DefinitionId == definition, ct)
                    .ConfigureAwait(false)
            )
                return PluginAdminResult.BadRequest(
                    $"{name} has a value already: change it on its page."
                );

            var row = new ValueEntity { DefinitionId = definition, Source = ValueEntity.STAFF };

            if (Apply(row, request) is { } problem)
                return PluginAdminResult.BadRequest(problem);

            db.Values.Add(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return PluginAdminResult.Ok(new { id = row.Id, furni = name });
        });

    private Task<PluginAdminResult> EditValueAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            if (
                await FindValueAsync(db, request, ct, tracked: true).ConfigureAwait(false)
                is not { } row
            )
                return PluginAdminResult.NotFound("No such value.");

            if (Apply(row, request) is { } problem)
                return PluginAdminResult.BadRequest(problem);

            row.Source = ValueEntity.STAFF;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return PluginAdminResult.Ok(new { id = row.Id });
        });

    private Task<PluginAdminResult> RemoveValueAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            if (
                await FindValueAsync(db, request, ct, tracked: true).ConfigureAwait(false)
                is not { } row
            )
                return PluginAdminResult.NotFound("No such value.");

            db.Values.Remove(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return PluginAdminResult.NoContent();
        });

    private string? Apply(ValueEntity row, PluginAdminRequest request)
    {
        var credits = Number(request, "credits") ?? 0;

        if (credits is < 0 or > int.MaxValue)
            return "Credits are 0 or more.";

        var note = Text(request, "note")?.Trim();

        if (note?.Length > ValueEntity.MAX_NOTE)
            return $"A note is at most {ValueEntity.MAX_NOTE} characters.";

        row.Credits = (int)credits;
        row.IsLtd = Bool(request, "isLtd") ?? row.IsLtd;
        row.Note = string.IsNullOrEmpty(note) ? null : note;
        row.UpdatedById = request.PlayerId;
        row.UpdatedAt = Now;

        return null;
    }

    // History

    private async Task<PluginAdminResult> HistoryAsync(
        PluginAdminRequest request,
        CancellationToken ct
    )
    {
        if (request.Query.GetValueOrDefault("player")?.Trim() is not { Length: > 0 } q)
            return PluginAdminResult.Ok(
                new
                {
                    items = Array.Empty<object>(),
                    total = 0,
                    pageSize = PAGE_SIZE,
                }
            );

        if (await PlayerOfAsync(q, ct).ConfigureAwait(false) is not { } player)
            return PluginAdminResult.Ok(
                new
                {
                    items = Array.Empty<object>(),
                    total = 0,
                    pageSize = PAGE_SIZE,
                }
            );

        return await TransfersAsync(player, request, ct).ConfigureAwait(false);
    }

    private Task<PluginAdminResult> PlayerHistoryAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        int.TryParse(request.Route.GetValueOrDefault("id"), out var player)
            ? TransfersAsync(player, request, ct)
            : Task.FromResult(PluginAdminResult.NotFound("No such player."));

    private Task<PluginAdminResult> TransfersAsync(
        int player,
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(async db =>
        {
            var query = db
                .Transfers.AsNoTracking()
                .Where(x => x.FromPlayerId == player || x.ToPlayerId == player);
            var total = await query.CountAsync(ct).ConfigureAwait(false);
            var page = Page(request);
            var rows = await query
                .OrderByDescending(x => x.OccurredAt)
                .ThenByDescending(x => x.Id)
                .Skip((page - 1) * PAGE_SIZE)
                .Take(PAGE_SIZE)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var names = await hotel
                .NamesAsync(rows.SelectMany(x => new[] { x.FromPlayerId, x.ToPlayerId }), ct)
                .ConfigureAwait(false);

            return PluginAdminResult.Ok(
                new
                {
                    items = rows.Select(x => new
                    {
                        id = x.Id,
                        what = x.Summary,
                        kind = x.Kind,
                        from = Name(names, x.FromPlayerId),
                        to = Name(names, x.ToPlayerId),
                        value = x.Value,
                        unvalued = x.Unvalued,
                        occurredAt = x.OccurredAt,
                    }),
                    total,
                    pageSize = PAGE_SIZE,
                }
            );
        });

    private Task<PluginAdminResult> PlayerAsync(PluginAdminRequest request, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            if (!int.TryParse(request.Route.GetValueOrDefault("id"), out var player) || player <= 0)
                return PluginAdminResult.NotFound("No such player.");

            var since = Now.AddDays(-30);
            var recent = await db
                .Transfers.AsNoTracking()
                .Where(x =>
                    (x.FromPlayerId == player || x.ToPlayerId == player) && x.OccurredAt >= since
                )
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var names = await hotel.NamesAsync([player], ct).ConfigureAwait(false);

            return PluginAdminResult.Ok(
                new
                {
                    id = player,
                    player = $"{Name(names, player)} ({player})",
                    playerPage = hotel.Panel($"players/{player}") ?? $"/players/{player}",
                    createdAt = await hotel.CreatedAtAsync(player, ct).ConfigureAwait(false),
                    given = recent.Where(x => x.FromPlayerId == player).Sum(x => x.Value),
                    received = recent.Where(x => x.ToPlayerId == player).Sum(x => x.Value),
                    partners = recent
                        .Select(x => x.FromPlayerId == player ? x.ToPlayerId : x.FromPlayerId)
                        .Distinct()
                        .Count(),
                    flags = await db
                        .Flags.CountAsync(x => x.PlayerId == player, ct)
                        .ConfigureAwait(false),
                }
            );
        });

    private Task<PluginAdminResult> PlayerFlagsAsync(
        PluginAdminRequest request,
        CancellationToken ct
    ) =>
        WithDbAsync(db =>
            int.TryParse(request.Route.GetValueOrDefault("id"), out var player)
                ? FlagPageAsync(
                    db.Flags.AsNoTracking().Where(x => x.PlayerId == player),
                    request,
                    ct
                )
                : Task.FromResult(PluginAdminResult.NotFound("No such player."))
        );

    // Helpers

    private async Task<Valuer> ValuerAsync(TradeWatchDbContext db, CancellationToken ct) =>
        new(
            await db
                .Values.AsNoTracking()
                .ToDictionaryAsync(x => x.DefinitionId, ct)
                .ConfigureAwait(false),
            hotel,
            settings.Current.Valuation
        );

    private async Task<int?> PlayerOfAsync(string q, CancellationToken ct) =>
        int.TryParse(q, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : await hotel.PlayerIdAsync(q, ct).ConfigureAwait(false);

    private static string Place(
        IReadOnlyDictionary<int, string> names,
        int owner,
        string kind,
        long place
    ) =>
        kind switch
        {
            "None" => "-",
            "Inventory" or "Wallet" => Name(names, owner),
            "Holder" => $"present {place} for {Name(names, owner)}",
            "MarketplaceOffer" => $"offer {place} of {Name(names, owner)}",
            _ => $"{kind} {place}",
        };

    private static List<int> Ids(string ids) =>
        [
            .. ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x, CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Where(x => x > 0),
        ];

    private static string Name(IReadOnlyDictionary<int, string> names, int id) =>
        names.TryGetValue(id, out var name) ? name : $"#{id}";

    private static async Task<FlagEntity?> FindFlagAsync(
        TradeWatchDbContext db,
        PluginAdminRequest request,
        CancellationToken ct,
        bool tracked = false
    )
    {
        if (!long.TryParse(request.Route.GetValueOrDefault("id"), out var id))
            return null;

        var query = tracked ? db.Flags : db.Flags.AsNoTracking();

        return await query.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
    }

    private static async Task<ValueEntity?> FindValueAsync(
        TradeWatchDbContext db,
        PluginAdminRequest request,
        CancellationToken ct,
        bool tracked = false
    )
    {
        if (!int.TryParse(request.Route.GetValueOrDefault("id"), out var id))
            return null;

        var query = tracked ? db.Values : db.Values.AsNoTracking();

        return await query.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
    }

    private static int Page(PluginAdminRequest request) =>
        int.TryParse(request.Query.GetValueOrDefault("page"), out var page) && page > 0 ? page : 1;

    private static string? Text(PluginAdminRequest request, string name) =>
        request.Body is { ValueKind: JsonValueKind.Object } body
        && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? Bool(PluginAdminRequest request, string name) =>
        request.Body is { ValueKind: JsonValueKind.Object } body
        && body.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(value.GetString(), out var b) => b,
                _ => null,
            }
            : null;

    private static long? Number(PluginAdminRequest request, string name) =>
        request.Body is { ValueKind: JsonValueKind.Object } body
        && body.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var n) => n,
                JsonValueKind.String
                    when long.TryParse(
                        value.GetString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var n
                    ) => n,
                _ => null,
            }
            : null;
}
