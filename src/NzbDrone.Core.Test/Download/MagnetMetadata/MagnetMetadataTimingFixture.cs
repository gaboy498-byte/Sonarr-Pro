using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MonoTorrent;
using MonoTorrent.Client;
using Moq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.MagnetMetadata;

namespace NzbDrone.Core.Test.Download.MagnetMetadata
{
    [TestFixture]
    public class MagnetMetadataTimingFixture
    {
        private const string Secret = "timing-sensitive-marker";
        private Mock<IConfigService> _config;
        private CaptureTarget _target;
        private LogFactory _logs;
        private SemaphoreSlim _slots;
        private FakeEngine _engine;
        private MagnetMetadataDiagnostics _diagnostics;
        private string _cache;

        [SetUp]
        public void SetUp()
        {
            _config = new Mock<IConfigService>();
            _target = new CaptureTarget();
            var logging = new LoggingConfiguration();
            logging.AddRule(LogLevel.Trace, LogLevel.Fatal, _target);
            _logs = new LogFactory { Configuration = logging };
            _slots = new SemaphoreSlim(2, 2);
            _engine = new FakeEngine();
        }

        [TearDown]
        public void TearDown()
        {
            _logs.Dispose();
            _slots.Dispose();
        }

        private MagnetMetadataResolver Resolver(Func<EngineSettings, MagnetMetadataDiagnostics, IMagnetMetadataEngine> factory = null)
        {
            return new MagnetMetadataResolver(
                _config.Object,
                _logs.GetLogger("ResolverTiming"),
                factory ?? ((settings, diagnostics) =>
                {
                    _cache = settings.CacheDirectory;
                    _diagnostics = diagnostics;
                    return _engine;
                }),
                _slots);
        }

        [Test]
        public async Task successful_resolution_measures_each_stage_and_keeps_success_at_trace()
        {
            _engine.Start = () => Task.Delay(20);
            _engine.Download = async token =>
            {
                _diagnostics.ObserveConnections(2, 1);
                await Task.Delay(20, token);
                return new byte[] { 1, 2, 3 };
            };
            _engine.Stop = () => Task.Delay(20);
            _engine.OnDispose = () => Thread.Sleep(20);

            var result = await Resolver().ResolveAsync(Magnet(), CancellationToken.None);

            result.IsResolved.Should().BeTrue();
            result.Metadata.Should().Equal(1, 2, 3);
            var d = result.Diagnostics;
            d.EngineInitMs.Should().BeGreaterThanOrEqualTo(15);
            d.DiscoveryMs.Should().BeGreaterThanOrEqualTo(15);
            d.StopMs.Should().BeGreaterThanOrEqualTo(15);
            d.DisposeMs.Should().BeGreaterThanOrEqualTo(15);
            d.CleanupMs.Should().BeGreaterThanOrEqualTo(0);
            d.TotalMs.Should().BeGreaterThanOrEqualTo(60);
            d.MetadataDownloaded.Should().BeTrue();
            d.Cleanup.Should().Be("Succeeded");
            d.PeakOpenConnectionsObserved.Should().Be(2);
            d.PeakHalfOpenConnectionsObserved.Should().Be(1);
            d.ToString().Should().Contain("PeerDiscoveryStats=Unavailable").And.Contain("TrackerStats=Unavailable");
            Directory.Exists(_cache).Should().BeFalse();
            _engine.RequestedStopTimeout.Should().Be(TimeSpan.FromSeconds(5));
            _target.Events.Should().OnlyContain(x => x.Level == LogLevel.Trace);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task preflight_rejections_do_not_invoke_an_engine_or_invent_unmeasured_timings(bool proxy)
        {
            _config.SetupGet(x => x.ProxyEnabled).Returns(proxy);
            var result = await Resolver((_, _) => throw new AssertionException("Engine must not be created"))
                .ResolveAsync(proxy ? Magnet() : Magnet("udp://tracker.synthetic.invalid:6969/announce"), CancellationToken.None);

            result.Reason.Should().Be(proxy ? MagnetMetadataUninspectableReason.ProxyConfigured : MagnetMetadataUninspectableReason.NoAllowedTrackers);
            result.Diagnostics.SlotWaitMs.Should().BeNull();
            result.Diagnostics.EngineInitMs.Should().BeNull();
            result.Diagnostics.DiscoveryMs.Should().BeNull();
            result.Diagnostics.Cleanup.Should().Be("NotRun");
            result.Diagnostics.PeakOpenConnectionsObserved.Should().BeNull();
            result.Diagnostics.ToString().Should().Contain("EngineInitMs=Unavailable");
            if (proxy)
            {
                result.Diagnostics.PermittedTrackerCount.Should().BeNull();
            }
        }

        [Test]
        public async Task zero_metadata_is_no_peers_without_claiming_a_peer_count()
        {
            _engine.Download = _ => Task.FromResult(ReadOnlyMemory<byte>.Empty);
            var result = await Resolver().ResolveAsync(Magnet(), CancellationToken.None);
            result.Reason.Should().Be(MagnetMetadataUninspectableReason.NoPeers);
            result.Diagnostics.MetadataDownloaded.Should().BeFalse();
            result.Diagnostics.ConnectionSamples.Should().Be(0);
            result.Diagnostics.PeakOpenConnectionsObserved.Should().BeNull();
            result.Diagnostics.PermittedTrackerCount.Should().Be(1);
        }

        [TestCase("init")]
        [TestCase("discovery")]
        [TestCase("stop")]
        [TestCase("dispose")]
        public async Task exceptions_remain_fail_closed_and_never_log_raw_exception_material(string phase)
        {
            var error = new InvalidOperationException("https://private.synthetic.invalid/" + Secret + "?passkey=" + Secret);
            if (phase == "discovery")
            {
                _engine.Download = _ => Task.FromException<ReadOnlyMemory<byte>>(error);
            }
            else if (phase == "stop")
            {
                _engine.Stop = () => Task.FromException(error);
            }
            else if (phase == "dispose")
            {
                _engine.OnDispose = () => throw error;
            }

            var subject = phase == "init" ? Resolver((_, _) => throw error) : Resolver();
            var result = await subject.ResolveAsync(Magnet(), CancellationToken.None);

            result.IsResolved.Should().BeFalse();
            result.Reason.Should().Be(MagnetMetadataUninspectableReason.EngineFailure);
            result.Diagnostics.EngineInitMs.Should().NotBeNull();
            result.Diagnostics.DiscoveryMs.HasValue.Should().Be(phase != "init");
            result.Diagnostics.Cleanup.Should().Be(phase is "stop" or "dispose" ? "Failed" : "Succeeded");
            _slots.CurrentCount.Should().Be(2);
            _target.Events.Should().OnlyContain(x => x.Exception == null && !x.FormattedMessage.Contains(Secret));
            _target.Events.Select(x => x.FormattedMessage).Should().NotContain(x => x.Contains("private.synthetic.invalid"));
        }

        [Test]
        public async Task timeout_category_does_not_invent_a_requested_timeout_or_thirty_second_measurement()
        {
            _engine.Download = _ => Task.FromException<ReadOnlyMemory<byte>>(new OperationCanceledException(Secret));
            var result = await Resolver().ResolveAsync(Magnet(), CancellationToken.None);
            result.Reason.Should().Be(MagnetMetadataUninspectableReason.Timeout);
            result.Diagnostics.CancellationObserved.Should().BeTrue();
            result.Diagnostics.CancellationStage.Should().Be("Discovery");
            result.Diagnostics.TimeoutCancellationRequested.Should().BeFalse();
            result.Diagnostics.CallerCancellationRequested.Should().BeFalse();
            result.Diagnostics.DiscoveryMs.Should().BeLessThan(30000);
        }

        [Test]
        public void caller_cancellation_during_discovery_preserves_the_original_exception_and_cleans_up()
        {
            using var caller = new CancellationTokenSource();
            var error = new OperationCanceledException(Secret, caller.Token);
            _engine.Download = async _ =>
            {
                await caller.CancelAsync();
                throw error;
            };
            var subject = Resolver();
            var actual = Assert.CatchAsync<OperationCanceledException>(() => subject.ResolveAsync(Magnet(), caller.Token));
            actual.Should().BeSameAs(error);
            _diagnostics.CallerCancellationRequested.Should().BeTrue();
            _diagnostics.CancellationObserved.Should().BeTrue();
            _diagnostics.CancellationStage.Should().Be("Discovery");
            _diagnostics.StopMs.Should().NotBeNull();
            Directory.Exists(_cache).Should().BeFalse();
            _slots.CurrentCount.Should().Be(2);
            _target.Events.Should().OnlyContain(x => x.Exception == null && !x.FormattedMessage.Contains(Secret));
        }

        [Test]
        public void cancellation_before_slot_acquisition_is_measured_without_releasing_an_unowned_slot()
        {
            using var caller = new CancellationTokenSource();
            caller.Cancel();
            var subject = Resolver((_, _) => throw new AssertionException("Engine must not be created"));
            Assert.CatchAsync<OperationCanceledException>(() => subject.ResolveAsync(Magnet(), caller.Token));
            _slots.CurrentCount.Should().Be(2);
            _target.Events.Last().Properties["Status"].Should().Be("Canceled");
            _target.Events.Last().FormattedMessage.Should().Contain("CancellationStage=SlotWait").And.Contain("EngineInitMs=Unavailable");
        }

        [Test]
        public async Task cancellation_observed_during_shutdown_does_not_claim_discovery_was_canceled()
        {
            _engine.Stop = () => Task.FromException(new OperationCanceledException(Secret));
            var result = await Resolver().ResolveAsync(Magnet(), CancellationToken.None);
            result.Reason.Should().Be(MagnetMetadataUninspectableReason.EngineFailure);
            result.Diagnostics.MetadataDownloaded.Should().BeTrue();
            result.Diagnostics.CancellationObserved.Should().BeTrue();
            result.Diagnostics.CancellationStage.Should().Be("Shutdown");
            result.Diagnostics.TimeoutCancellationRequested.Should().BeFalse();
        }

        [Test]
        public async Task saturated_two_slot_queue_keeps_measurements_and_outcomes_separate()
        {
            var entered = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var complete = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var index = -1;
            var subject = Resolver((_, _) =>
            {
                var i = Interlocked.Increment(ref index);
                return new FakeEngine
                {
                    Download = _ =>
                    {
                        entered[i].SetResult();
                        return complete[i].Task;
                    }
                };
            });

            var first = subject.ResolveAsync(Magnet(), CancellationToken.None);
            var second = subject.ResolveAsync(Magnet(), CancellationToken.None);
            await Task.WhenAll(entered[0].Task, entered[1].Task).WaitAsync(TimeSpan.FromSeconds(5));
            var third = subject.ResolveAsync(Magnet(), CancellationToken.None);
            index.Should().Be(1);
            await Task.Delay(20);
            complete[0].SetResult(new byte[] { 1 });
            await entered[2].Task.WaitAsync(TimeSpan.FromSeconds(5));
            complete[1].SetResult(ReadOnlyMemory<byte>.Empty);
            complete[2].SetException(new InvalidOperationException(Secret));
            var results = await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5));
            results[0].IsResolved.Should().BeTrue();
            results[1].Reason.Should().Be(MagnetMetadataUninspectableReason.NoPeers);
            results[2].Reason.Should().Be(MagnetMetadataUninspectableReason.EngineFailure);
            results[2].Diagnostics.SlotWaitMs.Should().BeGreaterThanOrEqualTo(15);
            results.Select(x => x.Diagnostics).Distinct().Should().HaveCount(3);
            _slots.CurrentCount.Should().Be(2);
        }

        [Test]
        public void numeric_bounds_and_unavailable_connection_observation_are_explicit()
        {
            MagnetMetadataDiagnostics.BoundedMilliseconds(TimeSpan.FromDays(1000)).Should().Be(int.MaxValue);
            MagnetMetadataDiagnostics.BoundedMilliseconds(TimeSpan.FromMilliseconds(-1)).Should().Be(0);
            var d = new MagnetMetadataDiagnostics();
            d.ObserveConnections(4, 2);
            d.ObserveConnections(1, 0);
            d.PeakOpenConnectionsObserved.Should().Be(4);
            d.ConnectionObservationFailed();
            d.PeakOpenConnectionsObserved.Should().BeNull();
            d.ToString().Should().Contain("PeakOpenConnectionsObserved=Unavailable");
        }

        private static MagnetLink Magnet(string tracker = "https://tracker.synthetic.invalid/announce")
        {
            return MagnetLink.Parse("magnet:?xt=urn:btih:1111111111111111111111111111111111111111&dn=" + Secret + "&tr=" + Uri.EscapeDataString(tracker));
        }

        private sealed class FakeEngine : IMagnetMetadataEngine
        {
            public Func<Task> Start { get; set; } = () => Task.CompletedTask;
            public Func<CancellationToken, Task<ReadOnlyMemory<byte>>> Download { get; set; } = _ => Task.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1 });
            public Func<Task> Stop { get; set; } = () => Task.CompletedTask;
            public Action OnDispose { get; set; } = () => { };
            public TimeSpan RequestedStopTimeout { get; private set; }
            public Task StartAllAsync() => Start();
            public Task<ReadOnlyMemory<byte>> DownloadMetadataAsync(MagnetLink magnet, TorrentSettings settings, CancellationToken token) => Download(token);
            public Task StopAllAsync(TimeSpan timeout)
            {
                RequestedStopTimeout = timeout;
                return Stop();
            }

            public void Dispose() => OnDispose();
        }

        private sealed class CaptureTarget : Target
        {
            public ConcurrentQueue<LogEventInfo> Events { get; } = new ConcurrentQueue<LogEventInfo>();
            protected override void Write(LogEventInfo logEvent) => Events.Enqueue(logEvent);
        }
    }
}
