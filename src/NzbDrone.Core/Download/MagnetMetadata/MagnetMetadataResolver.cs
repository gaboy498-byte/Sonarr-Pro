using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;
using NLog;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Download.MagnetMetadata
{
    public class MagnetMetadataResolver : IMagnetMetadataResolver
    {
        internal const int MaximumMetadataSize = 16 * 1024 * 1024;
        internal const int MaximumConcurrentResolutions = 2;
        internal static readonly TimeSpan ResolutionTimeout = TimeSpan.FromSeconds(30);
        private static readonly SemaphoreSlim ResolutionSlots = new SemaphoreSlim(MaximumConcurrentResolutions, MaximumConcurrentResolutions);
        private readonly IConfigService _configService;
        private readonly Logger _logger;
        private readonly SemaphoreSlim _resolutionSlots;
        private readonly Func<EngineSettings, MagnetMetadataDiagnostics, IMagnetMetadataEngine> _createEngine;

        public MagnetMetadataResolver(IConfigService configService, Logger logger)
            : this(configService, logger, (settings, diagnostics) => new MetadataEngine(settings, diagnostics), ResolutionSlots)
        {
        }

        internal MagnetMetadataResolver(IConfigService configService,
            Logger logger,
            Func<EngineSettings, MagnetMetadataDiagnostics, IMagnetMetadataEngine> createEngine,
            SemaphoreSlim resolutionSlots)
        {
            _configService = configService;
            _logger = logger;
            _createEngine = createEngine;
            _resolutionSlots = resolutionSlots;
        }

        public async Task<MagnetMetadataResolutionResult> ResolveAsync(MagnetLink resolutionMagnet, CancellationToken cancellationToken)
        {
            var totalStarted = Stopwatch.GetTimestamp();
            var diagnostics = new MagnetMetadataDiagnostics();
            MagnetMetadataResolutionResult result = null;
            var status = "Exception";
            var stage = "Preflight";

            try
            {
                if (_configService.ProxyEnabled)
                {
                    result = MagnetMetadataResolutionResult.Uninspectable(MagnetMetadataUninspectableReason.ProxyConfigured);
                    return result.WithDiagnostics(diagnostics);
                }

                var sanitizedMagnet = resolutionMagnet == null ? null : CreateResolutionMagnet(resolutionMagnet);
                diagnostics.PermittedTrackerCount = sanitizedMagnet?.AnnounceUrls.Count ?? 0;
                if (sanitizedMagnet == null || sanitizedMagnet.AnnounceUrls.Count == 0)
                {
                    result = MagnetMetadataResolutionResult.Uninspectable(MagnetMetadataUninspectableReason.NoAllowedTrackers);
                    return result.WithDiagnostics(diagnostics);
                }

                stage = "SlotWait";
                var slotStarted = Stopwatch.GetTimestamp();
                try
                {
                    await _resolutionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    diagnostics.SlotWaitMs = MagnetMetadataDiagnostics.ElapsedMs(slotStarted);
                }

                // Keep the existing timeout after acquisition: it does not bound slot wait or cleanup.
                using var timeout = new CancellationTokenSource(ResolutionTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                var cacheDirectory = Path.Combine(Path.GetTempPath(), "sonarr-magnet-metadata", Guid.NewGuid().ToString("N"));
                IMagnetMetadataEngine engine = null;
                var cleanupFailed = false;

                try
                {
                    stage = "EngineInit";
                    var initStarted = Stopwatch.GetTimestamp();
                    TorrentSettings torrentSettings;
                    try
                    {
                        Directory.CreateDirectory(cacheDirectory);
                        var engineSettings = CreateEngineSettings(cacheDirectory);
                        torrentSettings = CreateTorrentSettings();
                        engine = _createEngine(engineSettings, diagnostics);
                        await engine.StartAllAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        diagnostics.EngineInitMs = MagnetMetadataDiagnostics.ElapsedMs(initStarted);
                    }

                    stage = "Discovery";
                    var discoveryStarted = Stopwatch.GetTimestamp();
                    ReadOnlyMemory<byte> metadata;
                    try
                    {
                        metadata = await engine.DownloadMetadataAsync(sanitizedMagnet, torrentSettings, linked.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        diagnostics.DiscoveryMs = MagnetMetadataDiagnostics.ElapsedMs(discoveryStarted);
                    }

                    diagnostics.MetadataDownloaded = !metadata.IsEmpty;
                    result = metadata.IsEmpty
                        ? MagnetMetadataResolutionResult.Uninspectable(MagnetMetadataUninspectableReason.NoPeers)
                        : MagnetMetadataResolutionResult.Resolved(metadata.ToArray());
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    diagnostics.ObserveCancellation(stage);
                    result = MagnetMetadataResolutionResult.Uninspectable(MagnetMetadataUninspectableReason.Timeout);
                }
                catch (OperationCanceledException)
                {
                    diagnostics.ObserveCancellation(stage);
                    throw;
                }
                catch (Exception)
                {
                    // Exceptions can contain tracker URLs, cache paths or credentials.
                    _logger.Debug("Magnet metadata resolution failed; Category=EngineFailure; Stage={Stage}", stage);
                    result = MagnetMetadataResolutionResult.Uninspectable(MagnetMetadataUninspectableReason.EngineFailure);
                }
                finally
                {
                    if (engine != null)
                    {
                        var stopStarted = Stopwatch.GetTimestamp();
                        try
                        {
                            await engine.StopAllAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            if (ex is OperationCanceledException)
                            {
                                diagnostics.ObserveCancellation("Shutdown");
                            }

                            cleanupFailed = true;
                            _logger.Warn("Magnet metadata resolver engine cleanup failed; Stage=Shutdown");
                        }
                        finally
                        {
                            diagnostics.StopMs = MagnetMetadataDiagnostics.ElapsedMs(stopStarted);
                        }

                        var disposeStarted = Stopwatch.GetTimestamp();
                        try
                        {
                            engine.Dispose();
                        }
                        catch (Exception ex)
                        {
                            if (ex is OperationCanceledException)
                            {
                                diagnostics.ObserveCancellation("Disposal");
                            }

                            cleanupFailed = true;
                            _logger.Warn("Magnet metadata resolver engine disposal failed; Stage=Disposal");
                        }
                        finally
                        {
                            diagnostics.DisposeMs = MagnetMetadataDiagnostics.ElapsedMs(disposeStarted);
                        }
                    }

                    var cleanupStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        if (Directory.Exists(cacheDirectory))
                        {
                            Directory.Delete(cacheDirectory, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ex is OperationCanceledException)
                        {
                            diagnostics.ObserveCancellation("CacheCleanup");
                        }

                        cleanupFailed = true;
                        _logger.Warn("Magnet metadata resolver temporary directory cleanup failed; Stage=CacheCleanup");
                    }
                    finally
                    {
                        diagnostics.CleanupMs = MagnetMetadataDiagnostics.ElapsedMs(cleanupStarted);
                        diagnostics.Cleanup = cleanupFailed ? "Failed" : "Succeeded";
                        diagnostics.TimeoutCancellationRequested = timeout.IsCancellationRequested;
                        _resolutionSlots.Release();
                    }
                }

                if (cleanupFailed && result?.IsResolved == true)
                {
                    result = MagnetMetadataResolutionResult.Uninspectable(MagnetMetadataUninspectableReason.EngineFailure);
                }

                result ??= MagnetMetadataResolutionResult.Uninspectable(MagnetMetadataUninspectableReason.EngineFailure);
                return result.WithDiagnostics(diagnostics);
            }
            catch (OperationCanceledException)
            {
                status = "Canceled";
                diagnostics.ObserveCancellation(stage);

                throw;
            }
            finally
            {
                diagnostics.TotalMs = MagnetMetadataDiagnostics.ElapsedMs(totalStarted);
                diagnostics.CallerCancellationRequested = cancellationToken.IsCancellationRequested;
                if (result != null)
                {
                    status = result.IsResolved ? "Resolved" : "Uninspectable";
                }

                if (result?.IsResolved == true)
                {
                    if (_logger.IsTraceEnabled)
                    {
                        _logger.Trace("Magnet metadata resolution completed; Status={Status}; Stages={Stages}", status, diagnostics.ToString());
                    }
                }
                else
                {
                    _logger.Debug("Magnet metadata resolution completed; Status={Status}; Reason={Reason}; Stages={Stages}", status, result?.Reason?.ToString() ?? "Unspecified", diagnostics.ToString());
                }
            }
        }

        private sealed class MetadataEngine : IMagnetMetadataEngine
        {
            private readonly ClientEngine _engine;
            private readonly MagnetMetadataDiagnostics _diagnostics;

            public MetadataEngine(EngineSettings settings, MagnetMetadataDiagnostics diagnostics)
            {
                _engine = new ClientEngine(settings);
                _diagnostics = diagnostics;
                _engine.StatsUpdate += ObserveConnections;
            }

            public Task StartAllAsync() => _engine.StartAllAsync();

            public Task<ReadOnlyMemory<byte>> DownloadMetadataAsync(MagnetLink magnet, TorrentSettings settings, CancellationToken token)
                => _engine.DownloadMetadataAsync(magnet, settings, token);

            public Task StopAllAsync(TimeSpan timeout) => _engine.StopAllAsync(timeout);

            public void Dispose()
            {
                _engine.StatsUpdate -= ObserveConnections;
                _engine.Dispose();
            }

            private void ObserveConnections(object sender, StatsUpdateEventArgs args)
            {
                try
                {
                    _diagnostics.ObserveConnections(_engine.ConnectionManager.OpenConnections, _engine.ConnectionManager.HalfOpenConnections);
                }
                catch (Exception)
                {
                    // Observation must never interfere with the engine or expose its exception text.
                    _diagnostics.ConnectionObservationFailed();
                }
            }
        }

        public static MagnetLink CreateResolutionMagnet(MagnetLink originalMagnet)
        {
            var allowedTrackers = originalMagnet.AnnounceUrls
                .Where(IsAllowedTracker)
                .ToList();

            return new MagnetLink(originalMagnet.InfoHashes, originalMagnet.Name, allowedTrackers, Array.Empty<string>(), originalMagnet.Size);
        }

        internal static EngineSettings CreateEngineSettings(string cacheDirectory)
        {
            return new EngineSettingsBuilder
            {
                AllowLocalPeerDiscovery = false,
                AllowPortForwarding = false,
                AutoSaveLoadDhtCache = false,
                AutoSaveLoadFastResume = false,
                AutoSaveLoadMagnetLinkMetadata = false,
                CacheDirectory = cacheDirectory,
                DhtEndPoint = null,
                DiskCacheBytes = 0,
                ListenEndPoints = new Dictionary<string, IPEndPoint>(),
                MaximumMetadataSize = MaximumMetadataSize
            }.ToSettings();
        }

        internal static TorrentSettings CreateTorrentSettings()
        {
            return new TorrentSettingsBuilder
            {
                AllowDht = false,
                AllowPeerExchange = false,
                CreateContainingDirectory = false
            }.ToSettings();
        }

        private static bool IsAllowedTracker(string tracker)
        {
            return Uri.TryCreate(tracker, UriKind.Absolute, out var uri) &&
                   (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                    uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        }
    }
}
