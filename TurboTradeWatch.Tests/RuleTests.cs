using System;
using System.Linq;
using System.Threading.Tasks;
using TurboTradeWatch.Database;
using TurboTradeWatch.Watch;
using Xunit;
using static TurboTradeWatch.Tests.TestHost;

namespace TurboTradeWatch.Tests;

public class LopsidedTests
{
    [Fact]
    public async Task ATradeGivingMuchForNothing_IsFlaggedOnce_AndAnnouncedOnce()
    {
        using var host = new TestHost();
        var rows = host.Trade(
            2,
            3,
            Items((host.NewItem(), DIAMOND), (host.NewItem(), DIAMOND)),
            []
        );

        await host.DeliverAsync();
        // Delivered again (a second dispatcher, a retried poll): nothing new.
        await host.DeliverAsync(rows);

        var flag = Assert.Single(host.Flags());
        Assert.Equal(Rules.LOPSIDED, flag.Rule);
        Assert.Equal(3, flag.PlayerId);
        Assert.Equal("2", flag.OtherPlayerIds);
        Assert.Equal(5000, flag.Value);
        Assert.Equal(
            "Carol got 5,000 credits of value from Bob for 0 credits in a trade",
            flag.Summary
        );
        Assert.Contains("Bob gave: 2 x CF_diamond_2500 = 5,000 credits", flag.Evidence);
        Assert.Equal(FlagEntity.OPEN, flag.Status);

        var notice = Assert.Single(host.Published);
        Assert.Equal("tradewatch", notice.Source);
        Assert.Equal("flag.new", notice.Topic);
        Assert.Equal(flag.Id.ToString(), notice.Key);
        Assert.Equal($"https://panel.test/plugin/tradewatch/flag/{flag.Id}", notice.Url);
        Assert.NotNull(flag.NotifiedAt);
    }

    [Fact]
    public async Task AFairTrade_OrOneUnderTheThreshold_IsNotFlagged()
    {
        using var host = new TestHost();

        // 5,000 for 2,500: half back, over the 10% it takes.
        host.Trade(
            2,
            3,
            Items((host.NewItem(), DIAMOND), (host.NewItem(), DIAMOND)),
            Items((host.NewItem(), DIAMOND))
        );
        // 2,500 for nothing: under the 5,000 the richer side must be worth.
        host.Trade(4, 5, Items((host.NewItem(), DIAMOND)), []);
        await host.DeliverAsync();

        Assert.Empty(host.Flags());
        Assert.Equal(4, host.Query(db => db.Transfers.Count()));
    }

    [Fact]
    public async Task AnUnvaluedItemOnThePoorerSide_IsFairByDefault_AndNothingWhenTurnedOff()
    {
        using var host = new TestHost();

        host.Trade(
            2,
            3,
            Items((host.NewItem(), DIAMOND), (host.NewItem(), DIAMOND)),
            Items((host.NewItem(), DRAGON))
        );
        await host.DeliverAsync();
        Assert.Empty(host.Flags());

        host.Settings.Current = host.Settings.Current with
        {
            Lopsided = new LopsidedConfig { UnvaluedIsFair = false },
        };
        host.Trade(
            4,
            5,
            Items((host.NewItem(), DIAMOND), (host.NewItem(), DIAMOND)),
            Items((host.NewItem(), DRAGON))
        );
        await host.DeliverAsync();

        var flag = Assert.Single(host.Flags());
        Assert.Equal(5, flag.PlayerId);
        Assert.Contains(
            "Erin gave: rare_dragonlamp = 0 credits (1 item not valued)",
            flag.Evidence
        );
    }

    [Fact]
    public async Task TheValueTable_ValuesARare()
    {
        using var host = new TestHost();
        host.Change(db => db.Values.Add(new ValueEntity { DefinitionId = DRAGON, Credits = 8000 }));

        host.Trade(2, 3, Items((host.NewItem(), DRAGON)), Items((host.NewItem(), GOLDBAR)));
        await host.DeliverAsync();

        var flag = Assert.Single(host.Flags());
        Assert.Equal(8000, flag.Value);
        Assert.Equal(3, flag.PlayerId);
    }

    [Fact]
    public async Task ACompensatedTrade_MovedNothing()
    {
        using var host = new TestHost();

        host.Trade(
            2,
            3,
            Items((host.NewItem(), DIAMOND), (host.NewItem(), DIAMOND)),
            [],
            compensated: true
        );
        await host.DeliverAsync();

        Assert.Empty(host.Flags());
        Assert.Equal(0, host.Query(db => db.Transfers.Count()));
    }

    [Fact]
    public async Task AMarketplaceSaleFarOverTheItemsValue_IsFlagged_UnlessTurnedOff()
    {
        using var host = new TestHost();

        // 10,000 credits for one gold bar (50).
        host.Sale(buyer: 2, seller: 3, host.NewItem(), GOLDBAR, 10000);
        await host.DeliverAsync();

        var flag = Assert.Single(host.Flags());
        Assert.Equal(3, flag.PlayerId);
        Assert.Equal(10000, flag.Value);
        Assert.Contains("marketplace sale", flag.Summary);

        host.Settings.Current = host.Settings.Current with
        {
            Lopsided = new LopsidedConfig { Marketplace = false },
        };
        host.Sale(buyer: 4, seller: 5, host.NewItem(), GOLDBAR, 10000, offer: 78);
        await host.DeliverAsync();

        Assert.Single(host.Flags());
    }

    [Fact]
    public async Task ARuleTurnedOff_FlagsNothing()
    {
        using var host = new TestHost();
        host.Settings.Current = host.Settings.Current with
        {
            Lopsided = new LopsidedConfig { Enabled = false },
        };

        host.Trade(2, 3, Items((host.NewItem(), DIAMOND), (host.NewItem(), DIAMOND)), []);
        await host.DeliverAsync();

        Assert.Empty(host.Flags());
    }
}

public class NewAccountTests
{
    [Fact]
    public async Task AYoungAccountReceivingValuables_IsFlagged()
    {
        using var host = new TestHost();
        host.Hotel.Created[3] = host.Now.AddHours(-20);

        // 2,500 for a gold bar: not lopsided enough to matter here, but a new account got 2,500.
        host.Trade(2, 3, Items((host.NewItem(), DIAMOND)), Items((host.NewItem(), GOLDBAR)));
        await host.DeliverAsync();

        var flag = Assert.Single(host.Flags());
        Assert.Equal(Rules.NEW_ACCOUNT, flag.Rule);
        Assert.Equal(3, flag.PlayerId);
        Assert.Equal(2500, flag.Value);
        Assert.StartsWith("Carol, an account 20 h old, received 2,500 credits", flag.Summary);
    }

    [Fact]
    public async Task AnOlderAccount_OrTooLittle_IsNotFlagged()
    {
        using var host = new TestHost();
        host.Hotel.Created[3] = host.Now.AddDays(-4);
        host.Hotel.Created[5] = host.Now.AddHours(-1);

        host.Trade(2, 3, Items((host.NewItem(), DIAMOND)), []);
        host.Trade(4, 5, Items((host.NewItem(), GOLDBAR)), []);
        await host.DeliverAsync();

        Assert.Empty(host.Flags());
    }

    [Fact]
    public async Task AGiftWhoseContentHasNoValue_IsWorthWhatWasPaid()
    {
        using var host = new TestHost();
        host.Hotel.Created[3] = host.Now.AddDays(-1);

        host.Gift(2, 3, CHAIR, 3000);
        await host.DeliverAsync();

        var flag = Assert.Single(host.Flags());
        Assert.Equal(Rules.NEW_ACCOUNT, flag.Rule);
        Assert.Equal(3000, flag.Value);
        Assert.Contains("(gift): chair, bought for 3,000 credits = 3,000 credits", flag.Evidence);
    }
}

public class FunnelTests
{
    [Fact]
    public async Task ManyAccountsFeedingOne_IsFlaggedOnce_AndTheFlagGrows()
    {
        using var host = new TestHost();

        // Bob, Carol, Dave, Erin give Ivy 500 each: four, one short.
        foreach (var sender in new[] { 2, 3, 4, 5 })
        {
            host.Trade(sender, 9, host.Goldbars(10), []);
            host.Time.Advance(TimeSpan.FromHours(1));
        }

        await host.DeliverAsync();
        Assert.Empty(host.Flags());

        host.Trade(6, 9, host.Goldbars(10), []);
        await host.DeliverAsync();

        var flag = Assert.Single(host.Flags());
        Assert.Equal(Rules.FUNNEL, flag.Rule);
        Assert.Equal(9, flag.PlayerId);
        Assert.Equal("2,3,4,5,6", flag.OtherPlayerIds);
        Assert.Equal(2500, flag.Value);
        Assert.Equal(5, flag.Operations.Split(',').Length);

        // A sixth adds to it: no second flag, no second notice.
        host.Time.Advance(TimeSpan.FromHours(1));
        host.Trade(7, 9, host.Goldbars(10), []);
        await host.DeliverAsync();

        flag = Assert.Single(host.Flags());
        Assert.Equal("2,3,4,5,6,7", flag.OtherPlayerIds);
        Assert.Equal(3000, flag.Value);
        Assert.Single(host.Published);
    }

    [Fact]
    public async Task FairExchanges_FeedNoOne()
    {
        using var host = new TestHost();
        host.Change(db => db.Values.Add(new ValueEntity { DefinitionId = DRAGON, Credits = 600 }));

        // A seller of five dragons at their value to five buyers, and five fair trades into one account.
        foreach (var buyer in new[] { 2, 3, 4, 5, 6 })
        {
            host.Sale(buyer, seller: 9, host.NewItem(), DRAGON, 600, offer: 100 + buyer);
            host.Trade(buyer, 8, host.Goldbars(10), Items((host.NewItem(), DRAGON)));
        }

        await host.DeliverAsync();

        Assert.Empty(host.Flags());
    }

    [Fact]
    public async Task SendersOutsideTheWindow_OrUnderTheValue_DoNotCount()
    {
        using var host = new TestHost();

        foreach (var sender in new[] { 2, 3, 4 })
        {
            host.Trade(sender, 9, host.Goldbars(10), []);
            host.Time.Advance(TimeSpan.FromHours(13));
        }

        // A gold bar each: 50, under the 500 a transfer must be worth.
        host.Trade(5, 9, host.Goldbars(1), []);
        host.Trade(6, 9, host.Goldbars(1), []);
        host.Trade(7, 9, host.Goldbars(10), []);
        host.Trade(8, 9, host.Goldbars(10), []);
        await host.DeliverAsync();

        Assert.Empty(host.Flags());
    }
}

public class RapidFlipTests
{
    [Fact]
    public async Task AnItemThroughFourAccountsWithinTheHour_IsFlaggedOnce()
    {
        using var host = new TestHost();
        var bar = host.NewItem();

        host.Trade(2, 3, Items((bar, GOLDBAR)), []);
        host.Time.Advance(TimeSpan.FromMinutes(10));
        host.Trade(3, 4, Items((bar, GOLDBAR)), []);
        await host.DeliverAsync();
        Assert.Empty(host.Flags());

        host.Time.Advance(TimeSpan.FromMinutes(10));
        host.Trade(4, 5, Items((bar, GOLDBAR)), []);
        await host.DeliverAsync();

        var flag = Assert.Single(host.Flags());
        Assert.Equal(Rules.RAPID_FLIPS, flag.Rule);
        Assert.Equal(5, flag.PlayerId);
        Assert.Equal("2,3,4", flag.OtherPlayerIds);
        Assert.Equal($"item:{bar}", flag.SubjectRef);
        Assert.StartsWith(
            "CF_50_goldbar went through 4 accounts in 20 min, now with Erin",
            flag.Summary
        );

        host.Time.Advance(TimeSpan.FromMinutes(5));
        host.Trade(5, 6, Items((bar, GOLDBAR)), []);
        await host.DeliverAsync();

        flag = Assert.Single(host.Flags());
        Assert.Equal(6, flag.PlayerId);
        Assert.Single(host.Published);
    }

    [Fact]
    public async Task ASaleKeepsTheItemsChain()
    {
        using var host = new TestHost();
        var bar = host.NewItem();

        host.Trade(2, 3, Items((bar, GOLDBAR)), []);
        host.Time.Advance(TimeSpan.FromMinutes(5));
        var sold = host.Sale(buyer: 4, seller: 3, bar, GOLDBAR, 50);
        host.Time.Advance(TimeSpan.FromMinutes(5));
        host.Trade(4, 5, Items((sold, GOLDBAR)), []);
        await host.DeliverAsync();

        var flag = Assert.Single(host.Flags(), x => x.Rule == Rules.RAPID_FLIPS);
        Assert.Equal($"item:{bar}", flag.SubjectRef);
        Assert.Equal(5, flag.PlayerId);
    }

    [Fact]
    public async Task ASlowChain_OrAnUnvaluedItem_IsNotFlagged()
    {
        using var host = new TestHost();
        var bar = host.NewItem();
        var chair = host.NewItem();

        foreach (var (from, to) in new[] { (2, 3), (3, 4), (4, 5) })
        {
            host.Trade(from, to, Items((bar, GOLDBAR)), []);
            host.Trade(from, to, Items((chair, CHAIR)), []);
            host.Time.Advance(TimeSpan.FromMinutes(40));
        }

        await host.DeliverAsync();

        Assert.Empty(host.Flags());
    }
}
