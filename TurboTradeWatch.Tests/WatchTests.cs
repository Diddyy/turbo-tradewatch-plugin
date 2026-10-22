using System;
using System.Linq;
using System.Threading.Tasks;
using Turbo.Primitives.Movements;
using TurboTradeWatch.Database;
using TurboTradeWatch.Watch;
using Xunit;
using static TurboTradeWatch.Tests.TestHost;

namespace TurboTradeWatch.Tests;

public class WatchTests
{
    [Fact]
    public async Task ARestart_ReadsOnFromItsCursor_AndFlagsNothingTwice()
    {
        using var host = new TestHost();
        var rows = host.Trade(
            2,
            3,
            Items((host.NewItem(), DIAMOND), (host.NewItem(), DIAMOND)),
            []
        );

        await host.DeliverAsync();
        var cursor = await host.Consumer.GetCursorAsync(CT);

        // A restart: a new consumer over the same tables, and the dispatcher delivering again from 0.
        host.NewConsumer();
        Assert.Equal(cursor, await host.Consumer.GetCursorAsync(CT));
        await host.DeliverAsync(rows);
        await host.DeliverAsync(rows);

        Assert.Single(host.Flags());
        Assert.Single(host.Published);
        Assert.Equal(2, host.Query(db => db.Movements.Count()));
        Assert.Equal(2, host.Query(db => db.Transfers.Count()));
    }

    [Fact]
    public async Task AFlagARestartKeptFromBeingAnnounced_IsAnnouncedOnceAtTheNextStart()
    {
        using var host = new TestHost();
        var publish = host.Notices.Publisher;

        // Committed, then the server stopped before announcing it.
        host.Notices.Publisher = null;
        host.Trade(2, 3, Items((host.NewItem(), DIAMOND), (host.NewItem(), DIAMOND)), []);
        await host.DeliverAsync();
        host.Change(db => db.Flags.Single().NotifiedAt = null);

        host.Notices.Publisher = publish;
        host.NewConsumer();
        await host.Consumer.AnnounceAsync(null, CT);
        await host.Consumer.AnnounceAsync(null, CT);

        Assert.Single(host.Published);
    }

    [Fact]
    public async Task AFlagAboutSomethingOld_IsKeptButNotAnnounced()
    {
        using var host = new TestHost();

        host.Trade(2, 3, Items((host.NewItem(), DIAMOND), (host.NewItem(), DIAMOND)), []);
        // Read two hours later: the plugin was off.
        host.Time.Advance(TimeSpan.FromHours(2));
        await host.DeliverAsync();

        Assert.Single(host.Flags());
        Assert.Empty(host.Published);
    }

    [Fact]
    public async Task AnLtdMadeWithASerial_IsLearnt_AndCountsAsTheLtdValue()
    {
        using var host = new TestHost();
        var bear = host.BuyLtd(2, LTD_BEAR, serial: 7);

        await host.DeliverAsync();

        var learnt = host.Query(db => db.Values.Single());
        Assert.Equal(LTD_BEAR, learnt.DefinitionId);
        Assert.True(learnt.IsLtd);
        Assert.Equal(ValueEntity.LEARNT, learnt.Source);
        Assert.Equal(0, learnt.Credits);

        host.Hotel.Created[3] = host.Now.AddDays(-1);
        host.Settings.Current = host.Settings.Current with
        {
            Valuation = new ValuationConfig { LtdCredits = 2500 },
        };
        host.Trade(2, 3, Items((bear, LTD_BEAR)), []);
        await host.DeliverAsync();

        var flag = Assert.Single(host.Flags());
        Assert.Equal(Rules.NEW_ACCOUNT, flag.Rule);
        Assert.Contains("ltd_bear = 2,500 credits (1 LTD)", flag.Evidence);
        Assert.Equal(1, host.Query(db => db.Transfers.Single(x => x.ToPlayerId == 3).Ltds));
    }

    [Fact]
    public async Task OnlyExchangesAreKept_AndAGapIsCounted()
    {
        using var host = new TestHost();

        host.BuyLtd(2, LTD_BEAR, serial: 1);
        host.Trade(2, 3, Items((host.NewItem(), CHAIR)), []);
        await host.DeliverAsync(gap: new OutboxGap(1, 40));

        Assert.Equal(1, host.Query(db => db.Movements.Count()));
        Assert.Equal(MovementCauses.TRADE, host.Query(db => db.Movements.Single().Cause));

        var cursor = host.Query(db => db.Cursors.Single());
        Assert.Equal(host.Outbox.Max(x => x.Id), cursor.LastId);
        Assert.Equal(40, cursor.Missed);
        Assert.Equal("1-40", cursor.LastGap);
    }

    [Fact]
    public async Task ALateBatch_LeavesTheCursorWhereItWas()
    {
        using var host = new TestHost();

        host.Trade(2, 3, Items((host.NewItem(), CHAIR)), []);
        host.Trade(4, 5, Items((host.NewItem(), CHAIR)), []);
        await host.DeliverAsync();
        var cursor = await host.Consumer.GetCursorAsync(CT);

        await host.Consumer.HandleAsync(
            new AssetMovementBatch
            {
                Movements = [host.Outbox[0]],
                Cursor = 1,
                IsLate = true,
            },
            CT
        );

        Assert.Equal(cursor, await host.Consumer.GetCursorAsync(CT));
    }

    [Fact]
    public async Task OldMovements_ArePruned_FlagsAreKept()
    {
        using var host = new TestHost();

        host.Trade(2, 3, Items((host.NewItem(), DIAMOND), (host.NewItem(), DIAMOND)), []);
        await host.DeliverAsync();
        host.Time.Advance(TimeSpan.FromDays(91));
        host.Trade(4, 5, Items((host.NewItem(), CHAIR)), []);
        await host.DeliverAsync();

        Assert.Equal(1, host.Query(db => db.Movements.Count()));
        Assert.Equal(0, host.Query(db => db.Handovers.Count(x => x.FromPlayerId == 2)));
        Assert.Single(host.Flags());
    }
}

public class AdminTests
{
    [Fact]
    public async Task StaffHandleAFlag_WithANote()
    {
        using var host = new TestHost();
        host.Trade(2, 3, Items((host.NewItem(), DIAMOND), (host.NewItem(), DIAMOND)), []);
        await host.DeliverAsync();
        var admin = host.Admin();
        var id = host.Flags().Single().Id;

        var refused = await admin.CallAsync("POST flags/{id}", id, body: """{"status":"banned"}""");
        Assert.Equal(400, refused.Status);

        var saved = await admin.CallAsync(
            "POST flags/{id}",
            id,
            body: """{"status":"dismissed","note":"Siblings sharing furni."}"""
        );
        Assert.Equal(200, saved.Status);

        var flag = host.Flags().Single();
        Assert.Equal(FlagEntity.DISMISSED, flag.Status);
        Assert.Equal("Siblings sharing furni.", flag.Note);
        Assert.Equal(9, flag.HandledById);

        Assert.Contains(
            "\"total\":0",
            Admin.Json(await admin.CallAsync("GET flags", query: "status=open"))
        );
        Assert.Contains(
            "\"total\":1",
            Admin.Json(await admin.CallAsync("GET flags", query: "q=bob"))
        );
        Assert.Contains(
            "\"total\":1",
            Admin.Json(await admin.CallAsync("GET flags", query: "q=3"))
        );
        Assert.Contains(
            "\"total\":0",
            Admin.Json(await admin.CallAsync("GET flags", query: "q=dave"))
        );
    }

    [Fact]
    public async Task AFlagsTimelineAndPlayers_AreItsMovementsAndPeople()
    {
        using var host = new TestHost();
        host.Trade(2, 3, Items((host.NewItem(), DIAMOND), (host.NewItem(), DIAMOND)), []);
        await host.DeliverAsync();
        var admin = host.Admin();
        var id = host.Flags().Single().Id;

        var timeline = Admin.Json(await admin.CallAsync("GET flags/{id}/movements", id));
        Assert.Contains("\"total\":2", timeline);
        Assert.Contains("CF_diamond_2500 (item", timeline);
        Assert.Contains("\"value\":2500", timeline);

        var evidence = Admin.Json(await admin.CallAsync("GET flags/{id}/evidence", id));
        Assert.Contains("\"total\":3", evidence);
        Assert.Contains("Bob gave: 2 x CF_diamond_2500 = 5,000 credits", evidence);

        var players = Admin.Json(await admin.CallAsync("GET flags/{id}/players", id));
        Assert.Contains("Carol (3)", players);
        Assert.Contains("Bob (2)", players);

        var detail = Admin.Json(await admin.CallAsync("GET flags/{id}", id));
        Assert.Contains("https://panel.test/players/3", detail);

        var history = Admin.Json(await admin.CallAsync("GET history", query: "player=Bob"));
        Assert.Contains("\"total\":2", history);
        Assert.Contains("2 x CF_diamond_2500", history);
    }

    [Fact]
    public async Task TheValueTable_IsChangedOnThePanel()
    {
        using var host = new TestHost();
        var admin = host.Admin();

        Assert.Equal(
            400,
            (await admin.CallAsync("POST values", body: """{"furni":"nope","credits":5}""")).Status
        );
        Assert.Equal(
            200,
            (
                await admin.CallAsync(
                    "POST values",
                    body: """{"furni":"rare_dragonlamp","credits":8000,"note":"Rare"}"""
                )
            ).Status
        );
        Assert.Equal(
            400,
            (
                await admin.CallAsync(
                    "POST values",
                    body: """{"furni":"rare_dragonlamp","credits":1}"""
                )
            ).Status
        );

        var row = host.Query(db => db.Values.Single());
        Assert.Equal(DRAGON, row.DefinitionId);
        Assert.Equal(8000, row.Credits);
        Assert.Equal(9, row.UpdatedById);

        await admin.CallAsync(
            "POST values/{id}",
            row.Id,
            body: """{"credits":9000,"isLtd":true}"""
        );
        row = host.Query(db => db.Values.Single());
        Assert.Equal(9000, row.Credits);
        Assert.True(row.IsLtd);

        Assert.Contains("rare_dragonlamp", Admin.Json(await admin.CallAsync("GET values")));
        Assert.Equal(204, (await admin.CallAsync("DELETE values/{id}", row.Id)).Status);
        Assert.Equal(0, host.Query(db => db.Values.Count()));
    }

    [Fact]
    public async Task TheRulesPage_SaysEachRulesThresholds()
    {
        using var host = new TestHost();
        host.Settings.Current = host.Settings.Current with
        {
            Funnel = new FunnelConfig { Enabled = false, MinSenders = 8 },
        };

        var rules = Admin.Json(await host.Admin().CallAsync("GET rules"));

        Assert.Contains("Turbo:TradeWatch:Lopsided", rules);
        Assert.Contains("8 or more accounts", rules);
        Assert.Contains("\"enabled\":false", rules);
    }

    [Fact]
    public void ThePages_NeedThePluginsNodes()
    {
        using var host = new TestHost();
        var admin = host.Admin();

        Assert.Equal(["plugin.tradewatch.view", "plugin.tradewatch.manage"], admin.Nodes);
        Assert.All(admin.Pages, x => Assert.Equal("plugin.tradewatch.view", x.Node));
        Assert.All(
            admin.Map.Where(x => !x.Key.StartsWith("GET ")),
            x => Assert.Equal("plugin.tradewatch.manage", x.Value.Node)
        );
        Assert.Equal(
            ["flags", "rules", "values", "history"],
            admin.Pages.Where(x => x.Nav).Select(x => x.Id)
        );
    }
}
