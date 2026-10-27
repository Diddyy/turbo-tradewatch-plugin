using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Turbo.Contracts.Plugins;
using Turbo.Contracts.Plugins.Admin;
using Turbo.Contracts.Plugins.Exports;
using Turbo.Database.Extensions;
using Turbo.Primitives.Events;
using Turbo.Primitives.Furniture.Providers;
using Turbo.Primitives.Movements;
using TurboTradeWatch.Database;
using TurboTradeWatch.Hosting;
using TurboTradeWatch.Watch;

namespace TurboTradeWatch;

/// <summary>
/// Watches every trade, catalogue gift and marketplace sale in core's movement outbox and flags the
/// suspicious ones for staff: lopsided trades, new accounts receiving valuables, one account fed by
/// many, and items passing through many hands fast. Flags are kept with their evidence on the admin
/// panel's Trade Watch pages, and each new one is a <c>PluginNoticeEvent</c> the Discord plugin may
/// post. The watch never acts on a player by itself.
/// </summary>
public sealed class TradeWatchPlugin : ITurboPlugin
{
    private readonly Lock _lock = new();
    private IAssetMovementConsumerRegistry? _registry;
    private WatchConsumer? _consumer;
    private IDisposable? _registration;
    private IDisposable? _onChange;
    private Notices? _notices;
    private CancellationTokenSource? _stop;
    private Task? _announcer;

    public string Key => "tradewatch";

    public string Version => "1.0.0";

    public void ConfigureServices(IServiceCollection services, PluginManifest manifest)
    {
        services.AddPluginDatabaseContext<TradeWatchDbContext, TradeWatchDbModule>();
        // Bound as a host module binds its options: the panel lists them, and saves reach the watch.
        services.AddOptions<TradeWatchConfig>().BindConfiguration(TradeWatchConfig.SECTION_NAME);
        services.AddOptions<ValuationConfig>().BindConfiguration(ValuationConfig.SECTION_NAME);
        services.AddOptions<LopsidedConfig>().BindConfiguration(LopsidedConfig.SECTION_NAME);
        services.AddOptions<NewAccountConfig>().BindConfiguration(NewAccountConfig.SECTION_NAME);
        services.AddOptions<FunnelConfig>().BindConfiguration(FunnelConfig.SECTION_NAME);
        services.AddOptions<RapidFlipsConfig>().BindConfiguration(RapidFlipsConfig.SECTION_NAME);
        services.AddSingleton<IWatchSettings, HostWatchSettings>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IWatchHotel>(sp => new HostHotel(
            Host<IGrainFactory>(sp),
            Host<IFurnitureDefinitionProvider>(sp),
            sp.GetRequiredService<IConfiguration>()
        ));
        services.AddSingleton<Notices>();
        services.AddSingleton<Rules>();
        services.AddSingleton<WatchConsumer>();
        services.AddSingleton<IPluginAdmin, TradeWatchAdmin>();
    }

    private static T Host<T>(IServiceProvider sp)
        where T : notnull => sp.GetRequiredService<IHostServices>().GetRequiredService<T>();

    public Task StartAsync(IServiceProvider services, CancellationToken ct)
    {
        _notices = services.GetRequiredService<Notices>();
        _notices.Publisher = Host<IEventPublisher>(services).PublishAsync;
        _registry = Host<IAssetMovementConsumerRegistry>(services);
        _consumer = services.GetRequiredService<WatchConsumer>();

        var options = services.GetRequiredService<IOptionsMonitor<TradeWatchConfig>>();

        Apply(options.CurrentValue.Enabled);
        _onChange = options.OnChange((config, _) => Apply(config.Enabled));

        _stop = new CancellationTokenSource();
        _announcer = AnnounceLeftAsync(
            Host<IHostApplicationLifetime>(services).ApplicationStarted,
            _consumer,
            services.GetRequiredService<ILogger<TradeWatchPlugin>>(),
            _stop.Token
        );

        return Task.CompletedTask;
    }

    /// <summary>Reads the outbox only while the watch is on: off, nothing is read and nothing is lost but what the outbox prunes.</summary>
    private void Apply(bool enabled)
    {
        lock (_lock)
        {
            if (enabled && _registration is null && _registry is not null && _consumer is not null)
                _registration = _registry.Register(_consumer);
            else if (!enabled && _registration is not null)
            {
                _registration.Dispose();
                _registration = null;
            }
        }
    }

    /// <summary>Once the host has started, announces the flags a restart kept from being announced.</summary>
    internal static async Task AnnounceLeftAsync(
        CancellationToken started,
        WatchConsumer consumer,
        ILogger logger,
        CancellationToken ct
    )
    {
        try
        {
            var ready = new TaskCompletionSource();

            await using (started.Register(() => ready.TrySetResult()))
                await ready.Task.WaitAsync(ct).ConfigureAwait(false);

            await consumer.AnnounceAsync(null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Trade watch could not announce the flags left from before a restart"
            );
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _onChange?.Dispose();
        _onChange = null;
        Apply(false);

        if (_stop is not null)
            await _stop.CancelAsync().ConfigureAwait(false);

        if (_announcer is not null)
            await _announcer.ConfigureAwait(false);

        _stop?.Dispose();
        _stop = null;
        _announcer = null;

        if (_notices is not null)
            _notices.Publisher = null;

        _notices = null;
        _registry = null;
        _consumer = null;
    }

    public Task BindExportsAsync(IExportBinder binder, IServiceProvider services) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
