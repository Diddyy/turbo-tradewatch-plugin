using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Turbo.Contracts.Plugins.Admin;
using Turbo.Database.Delegates;
using Turbo.Primitives.Movements;
using Turbo.Primitives.Movements.Enums;
using Turbo.Primitives.Players.Wallet;
using Turbo.Primitives.Plugins.Events;
using TurboTradeWatch.Database;
using TurboTradeWatch.Hosting;
using TurboTradeWatch.Watch;

namespace TurboTradeWatch.Tests;

internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

internal sealed class Settings : IWatchSettings
{
    public WatchSettings Current { get; set; } = WatchSettings.Defaults;
}

/// <summary>
/// The hotel as the watch sees it. Players 1 to 9 are Alice, Bob, Carol, Dave, Erin, Frank, Gina,
/// Hank, Ivy, all made a year ago unless <see cref="Created"/> says otherwise. Furni: 100 throne,
/// 101 CF_50_goldbar, 102 CF_diamond_2500, 103 chair, 104 rare_dragonlamp, 105 ltd_bear.
/// </summary>
internal sealed class FakeHotel(ManualTime time) : IWatchHotel
{
    public static readonly string[] NAMES =
    [
        "",
        "Alice",
        "Bob",
        "Carol",
        "Dave",
        "Erin",
        "Frank",
        "Gina",
        "Hank",
        "Ivy",
    ];

    public Dictionary<int, DateTime> Created { get; } = [];

    public Dictionary<int, string> Furni { get; } =
        new()
        {
            [100] = "throne",
            [101] = "CF_50_goldbar",
            [102] = "CF_diamond_2500",
            [103] = "chair",
            [104] = "rare_dragonlamp",
            [105] = "ltd_bear",
        };

    public int AgeCalls { get; private set; }

    public Task<IReadOnlyDictionary<int, string>> NamesAsync(
        IEnumerable<int> ids,
        CancellationToken ct
    ) =>
        Task.FromResult<IReadOnlyDictionary<int, string>>(
            ids.Where(x => x > 0)
                .Distinct()
                .ToDictionary(x => x, x => x < NAMES.Length ? NAMES[x] : $"#{x}")
        );

    public Task<int?> PlayerIdAsync(string name, CancellationToken ct) =>
        Task.FromResult<int?>(
            Array.FindIndex(NAMES, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))
                is > 0
                    and var i
                ? i
                : null
        );

    public Task<DateTime?> CreatedAtAsync(int playerId, CancellationToken ct)
    {
        AgeCalls++;

        return Task.FromResult<DateTime?>(
            Created.TryGetValue(playerId, out var created)
                ? created
                : time.Now.UtcDateTime.AddYears(-1)
        );
    }

    public string? FurniName(int definitionId) => Furni.GetValueOrDefault(definitionId);

    public int? FurniId(string name) =>
        Furni
            .Where(x => string.Equals(x.Value, name, StringComparison.OrdinalIgnoreCase))
            .Select(x => (int?)x.Key)
            .FirstOrDefault();

    public string? Panel(string path) => $"https://panel.test/{path}";
}

/// <summary>
/// The watch over an in-memory SQLite database (the real SQL: unique keys, transactions), a fake
/// hotel and a clock the test moves. <see cref="Outbox"/> numbers the movements as core's outbox
/// would; <see cref="DeliverAsync"/> hands them over as the dispatcher does.
/// </summary>
internal sealed class TestHost : IDisposable
{
    public const int THRONE = 100;
    public const int GOLDBAR = 101;
    public const int DIAMOND = 102;
    public const int CHAIR = 103;
    public const int DRAGON = 104;
    public const int LTD_BEAR = 105;

    public static readonly CancellationToken CT = CancellationToken.None;

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private long _outboxId;
    private long _itemId = 1000;

    public TestHost()
    {
        _connection.Open();
        Hotel = new FakeHotel(Time);

        var services = new ServiceCollection();

        services.AddSingleton<TablePrefixProvider>(() => "tradewatch_");
        services.AddDbContext<TradeWatchDbContext>(o => o.UseSqlite(_connection));
        Services = services.BuildServiceProvider();

        using (var scope = Services.CreateScope())
            scope
                .ServiceProvider.GetRequiredService<TradeWatchDbContext>()
                .Database.EnsureCreated();

        Scopes = Services.GetRequiredService<IServiceScopeFactory>();
        Notices = new Notices(Hotel, NullLogger<Notices>.Instance)
        {
            Publisher = (notice, _) =>
            {
                Published.Add(notice);

                return Task.CompletedTask;
            },
        };
        Consumer = NewConsumer();
        AdminService = new TradeWatchAdmin(Scopes, Hotel, Settings, Time);
    }

    public ManualTime Time { get; } = new();

    public Settings Settings { get; } = new();

    public FakeHotel Hotel { get; }

    public ServiceProvider Services { get; }

    public IServiceScopeFactory Scopes { get; }

    public List<PluginNoticeEvent> Published { get; } = [];

    public Notices Notices { get; }

    public WatchConsumer Consumer { get; private set; }

    public TradeWatchAdmin AdminService { get; }

    public List<OutboxMovement> Outbox { get; } = [];

    public DateTime Now => Time.Now.UtcDateTime;

    /// <summary>A consumer as a restart (or a hot reload) makes it: nothing in memory, the same tables.</summary>
    public WatchConsumer NewConsumer() =>
        Consumer = new WatchConsumer(
            Scopes,
            Hotel,
            Settings,
            new Rules(Hotel),
            Notices,
            Time,
            NullLogger<WatchConsumer>.Instance
        );

    public long NewItem() => ++_itemId;

    public T Query<T>(Func<TradeWatchDbContext, T> query)
    {
        using var scope = Services.CreateScope();

        return query(scope.ServiceProvider.GetRequiredService<TradeWatchDbContext>());
    }

    public void Change(Action<TradeWatchDbContext> change)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeWatchDbContext>();

        change(db);
        db.SaveChanges();
    }

    public List<FlagEntity> Flags() =>
        Query(db => db.Flags.AsNoTracking().OrderBy(x => x.Id).ToList());

    /// <summary>Writes movements into the outbox, numbered after the last; returns them.</summary>
    public List<OutboxMovement> Write(IEnumerable<AssetMovement> movements)
    {
        var rows = movements.Select(m => new OutboxMovement(++_outboxId, m, Now)).ToList();

        Outbox.AddRange(rows);

        return rows;
    }

    /// <summary>Hands <paramref name="rows"/> (default: the outbox past the consumer's cursor) to the consumer.</summary>
    public async Task DeliverAsync(
        IReadOnlyList<OutboxMovement>? rows = null,
        OutboxGap? gap = null
    )
    {
        var cursor = await Consumer.GetCursorAsync(CT);

        rows ??= [.. Outbox.Where(x => x.Id > cursor)];

        await Consumer.HandleAsync(
            new AssetMovementBatch
            {
                Movements = [.. rows],
                Cursor = rows.Count == 0 ? cursor : rows.Max(x => x.Id),
                Gap = gap,
            },
            CT
        );
    }

    // Movements as core writes them (docs/movements.md).

    private static AssetMovement Move(
        string operation,
        short seq,
        DateTime at,
        string cause,
        AssetRef asset,
        AssetPlace from,
        AssetPlace to,
        int actor,
        string? causeRef = null,
        long amount = 1,
        AssetSnapshot? snapshot = null
    ) =>
        new()
        {
            OperationId = operation,
            Seq = seq,
            OccurredAtUtc = at,
            Cause = cause,
            CauseRef = causeRef,
            Asset = asset,
            From = from,
            To = to,
            Amount = amount,
            ActorPlayerId = actor,
            Snapshot = snapshot,
        };

    public static AssetRef Furni(long id, int definition) =>
        new(AssetKind.Furni, id, definition, null);

    public static AssetRef Credits => new(AssetKind.Currency, 0, 0, CurrencyKind.Credits);

    public static AssetPlace Inventory(int owner) => new(owner, PlaceKind.Inventory, 0, null);

    public static AssetPlace Wallet(int owner) => new(owner, PlaceKind.Wallet, 0, null);

    /// <summary>
    /// A trade: <paramref name="a"/> gives <paramref name="aGives"/> (item id, definition) to
    /// <paramref name="b"/>, then <paramref name="b"/> gives <paramref name="bGives"/>.
    /// </summary>
    public List<OutboxMovement> Trade(
        int a,
        int b,
        (long Id, int Definition)[] aGives,
        (long Id, int Definition)[] bGives,
        string? operation = null,
        bool compensated = false
    )
    {
        operation ??= Guid.NewGuid().ToString();
        var at = Now;
        short seq = 0;
        var moves = new List<AssetMovement>();

        foreach (var (id, def) in aGives)
            moves.Add(
                Move(
                    operation,
                    seq++,
                    at,
                    MovementCauses.TRADE,
                    Furni(id, def),
                    Inventory(a),
                    Inventory(b),
                    b,
                    "7"
                )
            );

        foreach (var (id, def) in bGives)
            moves.Add(
                Move(
                    operation,
                    seq++,
                    at,
                    MovementCauses.TRADE,
                    Furni(id, def),
                    Inventory(b),
                    Inventory(a),
                    b,
                    "7"
                )
            );

        if (compensated)
            foreach (var (id, def) in aGives)
                moves.Add(
                    Move(
                        operation,
                        seq++,
                        at,
                        MovementCauses.TRADE,
                        Furni(id, def),
                        Inventory(b),
                        Inventory(a),
                        b,
                        MovementCauses.TRADE_REF_COMPENSATION
                    )
                );

        return Write(moves);
    }

    /// <summary>A catalogue gift: <paramref name="sender"/> pays, <paramref name="receiver"/> gets a present holding one furni.</summary>
    public List<OutboxMovement> Gift(int sender, int receiver, int definition, int price)
    {
        var operation = Guid.NewGuid().ToString();
        var present = NewItem();

        return Write([
            Move(
                operation,
                0,
                Now,
                MovementCauses.CATALOG_GIFT,
                Credits,
                Wallet(sender),
                AssetPlace.Nowhere,
                sender,
                "55",
                price
            ),
            Move(
                operation,
                1,
                Now,
                MovementCauses.CATALOG_GIFT,
                Furni(present, 9000),
                AssetPlace.Nowhere,
                Inventory(receiver),
                sender,
                "55",
                snapshot: new AssetSnapshot(9000, "")
            ),
            Move(
                operation,
                2,
                Now,
                MovementCauses.CATALOG_GIFT,
                Furni(NewItem(), definition),
                AssetPlace.Nowhere,
                new AssetPlace(receiver, PlaceKind.Holder, present, null),
                sender,
                "55",
                snapshot: new AssetSnapshot(definition, "")
            ),
        ]);
    }

    /// <summary>A marketplace sale of <paramref name="listedId"/> by <paramref name="seller"/>; returns the bought item's new id.</summary>
    public long Sale(
        int buyer,
        int seller,
        long listedId,
        int definition,
        int price,
        int offer = 77
    )
    {
        var operation = Guid.NewGuid().ToString();
        var offerPlace = new AssetPlace(seller, PlaceKind.MarketplaceOffer, offer, null);
        var bought = NewItem();

        Write([
            Move(
                operation,
                0,
                Now,
                MovementCauses.MARKET_BUY,
                Credits,
                Wallet(buyer),
                AssetPlace.Nowhere,
                buyer,
                $"{offer}",
                price
            ),
            Move(
                operation,
                1,
                Now,
                MovementCauses.MARKET_BUY,
                Furni(listedId, definition),
                offerPlace,
                AssetPlace.Nowhere,
                buyer,
                $"{offer}",
                snapshot: new AssetSnapshot(definition, "")
            ),
            Move(
                operation,
                2,
                Now,
                MovementCauses.MARKET_BUY,
                Credits,
                AssetPlace.Nowhere,
                offerPlace,
                buyer,
                $"{offer}",
                price
            ),
            Move(
                operation,
                3,
                Now,
                MovementCauses.MARKET_FEE,
                Credits,
                offerPlace,
                AssetPlace.Nowhere,
                buyer,
                $"{offer}",
                Math.Max(1, price / 100)
            ),
            Move(
                operation,
                4,
                Now,
                MovementCauses.MARKET_BUY,
                Furni(bought, definition),
                AssetPlace.Nowhere,
                Inventory(buyer),
                buyer,
                $"{offer}",
                snapshot: new AssetSnapshot(definition, "")
            ),
        ]);

        return bought;
    }

    /// <summary>An LTD bought from the catalogue: made with its serial.</summary>
    public long BuyLtd(int buyer, int definition, int serial)
    {
        var id = NewItem();
        var operation = Guid.NewGuid().ToString();

        Write([
            Move(
                operation,
                0,
                Now,
                MovementCauses.CATALOG_LTD_RAFFLE,
                Credits,
                Wallet(buyer),
                AssetPlace.Nowhere,
                buyer,
                "9",
                100
            ),
            Move(
                operation,
                1,
                Now,
                MovementCauses.CATALOG_LTD_RAFFLE,
                Furni(id, definition),
                AssetPlace.Nowhere,
                Inventory(buyer),
                buyer,
                "9",
                snapshot: new AssetSnapshot(
                    definition,
                    $"{{\"Data\":{{\"UniqueNumber\":{serial},\"UniqueSeries\":100}}}}"
                )
            ),
        ]);

        return id;
    }

    public static (long, int)[] Items(params (long, int)[] items) => items;

    public (long, int)[] Goldbars(int count) =>
        [.. Enumerable.Range(0, count).Select(_ => (NewItem(), GOLDBAR))];

    public Admin Admin() => new(AdminService);

    public void Dispose()
    {
        Services.Dispose();
        _connection.Dispose();
    }
}

/// <summary>The admin endpoints as the host maps them, called as the panel would.</summary>
internal sealed class Admin : IPluginAdminBuilder
{
    public Admin(TradeWatchAdmin admin) => admin.Configure(this);

    public Dictionary<
        string,
        (string Node, Func<PluginAdminRequest, CancellationToken, Task<PluginAdminResult>> Handler)
    > Map { get; } = [];

    public List<string> Nodes { get; } = [];

    public List<PluginAdminPage> Pages { get; } = [];

    public string Node(string name, string description)
    {
        Nodes.Add($"plugin.tradewatch.{name}");

        return $"plugin.tradewatch.{name}";
    }

    void IPluginAdminBuilder.Map(
        string method,
        string pattern,
        string node,
        Func<PluginAdminRequest, CancellationToken, Task<PluginAdminResult>> handler
    ) => Map[$"{method} {pattern}"] = (node, handler);

    public void Page(PluginAdminPage page) => Pages.Add(page);

    public Task<PluginAdminResult> CallAsync(
        string route,
        long id = 0,
        string query = "",
        string? body = null,
        int staff = 9
    ) =>
        Map[route]
            .Handler(
                new PluginAdminRequest(
                    route.Split(' ')[0],
                    route,
                    new Dictionary<string, string> { ["id"] = id.ToString() },
                    query
                        .Split('&', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Split('='))
                        .ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1])),
                    body is null ? null : JsonDocument.Parse(body).RootElement,
                    staff
                ),
                TestHost.CT
            );

    public static string Json(PluginAdminResult result) => JsonSerializer.Serialize(result.Value);
}
