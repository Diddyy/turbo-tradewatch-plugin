using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Turbo.Primitives.Movements;
using Turbo.Primitives.Movements.Enums;
using TurboTradeWatch.Database;
using TurboTradeWatch.Hosting;

namespace TurboTradeWatch.Watch;

/// <summary>
/// Reads core's asset movement outbox (<c>docs/movements.md</c>; <c>Turbo:Movements:Enabled</c>
/// must be on). Each batch is one transaction: the movements of trades, gifts and marketplace sales
/// it keeps (a repeated (operation, seq) is dropped: delivery is at least once), the transfers and
/// hand-overs made of them, the flags the rules raise, the LTDs it learnt and the cursor. So a
/// crash, a hot reload or a batch delivered again never flags anything twice. A batch that throws
/// leaves everything as it was, and core delivers it again. The new flags are announced after the
/// commit; one a restart cut off is announced at the next start.
/// </summary>
public sealed class WatchConsumer(
    IServiceScopeFactory scopes,
    IWatchHotel hotel,
    IWatchSettings settings,
    Rules rules,
    Notices notices,
    TimeProvider time,
    ILogger<WatchConsumer> logger
) : IAssetMovementConsumer
{
    public const string NAME = "tradewatch";

    public string Name => NAME;

    public async Task<long> GetCursorAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeWatchDbContext>();

        return await db
            .Cursors.AsNoTracking()
            .Where(x => x.Name == CursorEntity.MOVEMENTS)
            .Select(x => x.LastId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task HandleAsync(AssetMovementBatch batch, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var current = settings.Current;
        List<FlagEntity> made;

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradeWatchDbContext>();

            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            var cursor = await db
                .Cursors.FirstOrDefaultAsync(x => x.Name == CursorEntity.MOVEMENTS, ct)
                .ConfigureAwait(false);

            if (cursor is null)
            {
                cursor = new CursorEntity { Name = CursorEntity.MOVEMENTS };
                db.Cursors.Add(cursor);
            }

            List<OutboxMovement> rows = batch.Movements.IsDefaultOrEmpty
                ? []
                : [.. batch.Movements.OrderBy(x => x.Id)];

            await LearnLtdsAsync(db, rows, now, ct).ConfigureAwait(false);

            var fresh = await InsertNewAsync(db, rows, ct).ConfigureAwait(false);

            made = await ReadAsync(db, fresh, current, now, ct).ConfigureAwait(false);

            if (batch.Gap is { } gap)
            {
                cursor.Missed += Math.Max(0, gap.LastId - gap.FirstId + 1);
                cursor.LastGap = $"{gap.FirstId}-{gap.LastId}";
            }

            // A second dispatcher can be behind, and a late batch leaves the cursor where it was.
            cursor.LastId = Math.Max(cursor.LastId, batch.Cursor);
            cursor.UpdatedAt = now;

            if (cursor.PrunedAt is not { } pruned || now - pruned >= TimeSpan.FromHours(1))
            {
                await PruneAsync(db, now.AddDays(-Math.Max(1, current.Watch.RetentionDays)), ct)
                    .ConfigureAwait(false);
                cursor.PrunedAt = now;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        if (made.Count > 0)
            await AnnounceAsync(made.Select(x => x.Id).ToList(), ct).ConfigureAwait(false);

        if (made.Count > 0 || batch.Gap is not null)
            logger.LogDebug(
                "Trade watch read up to outbox id {Cursor}: {Flags} new flags{Gap}",
                batch.Cursor,
                made.Count,
                batch.Gap is { } gap
                    ? $" (pruned before it was read: {gap.FirstId}-{gap.LastId})"
                    : ""
            );
    }

    /// <summary>
    /// Announces the flags not announced yet that are recent enough to be news, and marks them all
    /// announced (the old ones too: they never will be).
    /// </summary>
    public async Task AnnounceAsync(IReadOnlyCollection<long>? ids, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeWatchDbContext>();
        var now = time.GetUtcNow().UtcDateTime;
        var query = db.Flags.Where(x => x.NotifiedAt == null);

        if (ids is not null)
            query = query.Where(x => ids.Contains(x.Id));

        var flags = await query.OrderBy(x => x.Id).Take(100).ToListAsync(ct).ConfigureAwait(false);
        var newest = now.AddMinutes(-Math.Max(0, settings.Current.Watch.NoticeMaxAgeMinutes));

        foreach (var flag in flags)
        {
            if (flag.LastSeenAt >= newest)
                await notices.FlagAsync(flag, ct).ConfigureAwait(false);

            flag.NotifiedAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A definition made with a limited edition's serial is an LTD: it joins the value table
    /// (credits 0 until staff value it), so every later move of one counts as an LTD.
    /// </summary>
    private static async Task LearnLtdsAsync(
        TradeWatchDbContext db,
        IReadOnlyList<OutboxMovement> rows,
        DateTime now,
        CancellationToken ct
    )
    {
        var ltds = rows.Select(x => x.Movement)
            .Where(x =>
                x.Asset.Kind == AssetKind.Furni
                && x.Snapshot is { } snapshot
                && Valuer.IsLimited(snapshot.ExtraData)
            )
            .Select(x => x.Snapshot!.DefinitionId)
            .Distinct()
            .ToList();

        if (ltds.Count == 0)
            return;

        var known = await db
            .Values.Where(x => ltds.Contains(x.DefinitionId))
            .ToDictionaryAsync(x => x.DefinitionId, ct)
            .ConfigureAwait(false);

        foreach (var definition in ltds)
        {
            if (known.TryGetValue(definition, out var row))
            {
                row.IsLtd = true;
                continue;
            }

            db.Values.Add(
                new ValueEntity
                {
                    DefinitionId = definition,
                    IsLtd = true,
                    Source = ValueEntity.LEARNT,
                    UpdatedAt = now,
                }
            );
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Inserts the exchanges' movements the watch does not have yet, by (operation, seq).</summary>
    private static async Task<List<MovementEntity>> InsertNewAsync(
        TradeWatchDbContext db,
        IReadOnlyList<OutboxMovement> rows,
        CancellationToken ct
    )
    {
        var wanted = rows.Where(x => Operations.CAUSES.Contains(x.Movement.Cause)).ToList();

        if (wanted.Count == 0)
            return [];

        var operations = wanted.Select(x => x.Movement.OperationId).Distinct().ToList();
        var seen = (
            await db
                .Movements.AsNoTracking()
                .Where(x => operations.Contains(x.OperationId))
                .Select(x => new { x.OperationId, x.Seq })
                .ToListAsync(ct)
                .ConfigureAwait(false)
        )
            .Select(x => (x.OperationId, x.Seq))
            .ToHashSet();
        var fresh = new List<MovementEntity>();

        foreach (var row in wanted)
        {
            var m = row.Movement;

            // A retried save writes its rows again, in this batch or another.
            if (!seen.Add((m.OperationId, m.Seq)))
                continue;

            fresh.Add(
                new MovementEntity
                {
                    OutboxId = row.Id,
                    OperationId = m.OperationId,
                    Seq = m.Seq,
                    OccurredAt = DateTime.SpecifyKind(m.OccurredAtUtc, DateTimeKind.Utc),
                    Cause = m.Cause,
                    CauseRef = m.CauseRef,
                    ActorId = m.ActorPlayerId,
                    AssetKind = m.Asset.Kind.ToString(),
                    AssetId = m.Asset.Id,
                    DefinitionId =
                        m.Asset.Kind == AssetKind.Furni && m.Asset.DefinitionId == 0
                            ? m.Snapshot?.DefinitionId ?? 0
                            : m.Asset.DefinitionId,
                    Currency = m.Asset.Currency is { } currency ? Valuer.Currency(currency) : null,
                    Amount = m.Amount,
                    FromPlayerId = m.From.OwnerPlayerId,
                    FromKind = m.From.Kind.ToString(),
                    FromPlaceId = m.From.PlaceId,
                    ToPlayerId = m.To.OwnerPlayerId,
                    ToKind = m.To.Kind.ToString(),
                    ToPlaceId = m.To.PlaceId,
                }
            );
        }

        if (fresh.Count == 0)
            return fresh;

        db.Movements.AddRange(fresh);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return fresh;
    }

    /// <summary>
    /// Reads each operation that got new movements (with the ones it had already: an operation can
    /// straddle two batches), writes its transfers and hand-overs and runs the rules over it.
    /// </summary>
    private async Task<List<FlagEntity>> ReadAsync(
        TradeWatchDbContext db,
        IReadOnlyList<MovementEntity> fresh,
        WatchSettings current,
        DateTime now,
        CancellationToken ct
    )
    {
        var made = new List<FlagEntity>();

        if (fresh.Count == 0)
            return made;

        var operations = fresh.Select(x => x.OperationId).Distinct().ToList();
        var all = await db
            .Movements.AsNoTracking()
            .Where(x => operations.Contains(x.OperationId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var table = await db
            .Values.AsNoTracking()
            .ToDictionaryAsync(x => x.DefinitionId, ct)
            .ConfigureAwait(false);
        var valuer = new Valuer(table, hotel, current.Valuation);
        var reads = all.GroupBy(x => x.OperationId)
            .Select(x => Operations.Read([.. x], valuer))
            .OfType<OperationRead>()
            .OrderBy(x => x.OccurredAt)
            .ThenBy(x => x.OperationId, StringComparer.Ordinal)
            .ToList();

        foreach (var read in reads)
        {
            await WriteTransfersAsync(db, read, ct).ConfigureAwait(false);

            var handovers = await WriteHandoversAsync(db, read, ct).ConfigureAwait(false);

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            made.AddRange(
                await rules.RunAsync(db, read, handovers, current, now, ct).ConfigureAwait(false)
            );
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return made;
    }

    private static async Task WriteTransfersAsync(
        TradeWatchDbContext db,
        OperationRead read,
        CancellationToken ct
    )
    {
        var existing = await db
            .Transfers.Where(x => x.OperationId == read.OperationId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var t in read.Transfers)
        {
            var row = existing.FirstOrDefault(x =>
                x.FromPlayerId == t.From && x.ToPlayerId == t.To
            );

            if (row is null)
            {
                row = new TransferEntity
                {
                    OperationId = read.OperationId,
                    Kind = t.Kind,
                    FromPlayerId = t.From,
                    ToPlayerId = t.To,
                };
                db.Transfers.Add(row);
            }

            row.OccurredAt = read.OccurredAt;
            row.Value = t.Value;
            row.Credits = t.Credits;
            row.Items = t.Items.Count;
            row.Unvalued = t.Unvalued;
            row.Ltds = t.Ltds;
            row.Summary = Rules.Cut(t.Summary, 255);
        }
    }

    private static async Task<List<HandoverEntity>> WriteHandoversAsync(
        TradeWatchDbContext db,
        OperationRead read,
        CancellationToken ct
    )
    {
        var written = new List<HandoverEntity>();

        foreach (var h in read.Handovers)
        {
            var row = await db
                .Handovers.FirstOrDefaultAsync(
                    x => x.OperationId == read.OperationId && x.AssetId == h.AssetId,
                    ct
                )
                .ConfigureAwait(false);

            if (row is null)
            {
                // The item's chain: the listed item's when a sale made it again, else its own.
                var before = h.ListedAssetId ?? h.AssetId;
                var chain = await db
                    .Handovers.Where(x => x.AssetId == before)
                    .OrderBy(x => x.Id)
                    .Select(x => (long?)x.ChainId)
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);

                row = new HandoverEntity
                {
                    ChainId = chain ?? before,
                    AssetId = h.AssetId,
                    DefinitionId = h.DefinitionId,
                    OperationId = read.OperationId,
                    FromPlayerId = h.From,
                    ToPlayerId = h.To,
                    OccurredAt = read.OccurredAt,
                };
                db.Handovers.Add(row);
            }

            written.Add(row);
        }

        return written;
    }

    private static async Task PruneAsync(
        TradeWatchDbContext db,
        DateTime before,
        CancellationToken ct
    )
    {
        await db
            .Movements.Where(x => x.OccurredAt < before)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        await db
            .Transfers.Where(x => x.OccurredAt < before)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        await db
            .Handovers.Where(x => x.OccurredAt < before)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }
}
