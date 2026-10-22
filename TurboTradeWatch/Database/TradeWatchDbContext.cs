using Microsoft.EntityFrameworkCore;
using Turbo.Database.Context;
using Turbo.Database.Delegates;

namespace TurboTradeWatch.Database;

/// <summary>
/// The plugin's tables, under its prefix (<c>tradewatch_</c>): the movements it keeps, the
/// transfers and hand-overs made of them, the flags, the value table and its cursor. No foreign
/// keys into core: players and furni are kept by id.
/// </summary>
public class TradeWatchDbContext(
    DbContextOptions<TradeWatchDbContext> options,
    TablePrefixProvider prefix
) : PluginDbContextBase<TradeWatchDbContext>(options, prefix)
{
    public DbSet<MovementEntity> Movements => Set<MovementEntity>();

    public DbSet<TransferEntity> Transfers => Set<TransferEntity>();

    public DbSet<HandoverEntity> Handovers => Set<HandoverEntity>();

    public DbSet<FlagEntity> Flags => Set<FlagEntity>();

    public DbSet<ValueEntity> Values => Set<ValueEntity>();

    public DbSet<CursorEntity> Cursors => Set<CursorEntity>();
}
