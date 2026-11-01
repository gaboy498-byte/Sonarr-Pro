using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentValidation.Results;
using MonoTorrent;
using Moq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.Download.MagnetMetadata;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Test.Download.MagnetMetadata
{
    [TestFixture]
    public class MagnetRedirectDiagnosticsFixture
    {
        private const string InfoHash = "1111111111111111111111111111111111111111";
        private const string Secret = "synthetic-secret-redaction-marker";
        private const string HttpTracker = "https://tracker.synthetic.invalid/" + Secret + "/announce?api_key=" + Secret;
        private Mock<IHttpClient> _http;
        private Mock<IConfigService> _config;
        private Mock<ITorrentFileInfoReader> _reader;
        private Mock<IMagnetMetadataResolver> _resolver;
        private Mock<IIndexer> _indexer;
        private RecordingClient _client;
        private CaptureTarget _target;
        private LogFactory _logs;

        [SetUp]
        public void SetUp()
        {
            _http = new Mock<IHttpClient>();
            _config = new Mock<IConfigService>();
            _reader = new Mock<ITorrentFileInfoReader>();
            _resolver = new Mock<IMagnetMetadataResolver>();
            _indexer = new Mock<IIndexer>();
            _config.SetupGet(x => x.EnableFakeReleaseProtection).Returns(true);
            _indexer.SetupGet(x => x.Definition).Returns(new IndexerDefinition
            {
                Id = 42,
                Name = "https://indexer.synthetic.invalid/?api_key=" + Secret + "\r\n" + Magnet(HttpTracker)
            });
            _reader.Setup(x => x.GetHashFromTorrentFile(It.IsAny<byte[]>())).Returns(InfoHash);
            GivenContents("Synthetic.Show.S01E01.mkv");
            GivenUnresolved(MagnetMetadataUninspectableReason.Timeout);
            _target = new CaptureTarget();
            var logging = new LoggingConfiguration();
            logging.AddRule(LogLevel.Trace, LogLevel.Fatal, _target);
            _logs = new LogFactory { Configuration = logging };
            _client = new RecordingClient(_reader.Object, _http.Object, _config.Object, _resolver.Object, _logs.GetLogger("MagnetDiagnostics"));
        }

        [TearDown]
        public void TearDown()
        {
            _logs.Dispose();
        }

        [Test]
        public void udp_only_redirect_reports_no_allowed_trackers_without_resolving()
        {
            GivenRedirect(Magnet("udp://tracker.synthetic.invalid:6969/announce"));
            var error = Reject();
            error.Message.Should().Contain("Reason=NoAllowedTrackers").And.Contain("PermittedTrackerCount=0").And.Contain("ElapsedMs=0");
            _resolver.Verify(x => x.ResolveAsync(It.IsAny<MagnetLink>(), It.IsAny<CancellationToken>()), Times.Never());
            VerifyNoSubmission();
        }

        [TestCase(MagnetMetadataUninspectableReason.ProxyConfigured)]
        [TestCase(MagnetMetadataUninspectableReason.NoAllowedTrackers)]
        [TestCase(MagnetMetadataUninspectableReason.Timeout)]
        [TestCase(MagnetMetadataUninspectableReason.NoPeers)]
        [TestCase(MagnetMetadataUninspectableReason.TrackerFailure)]
        [TestCase(MagnetMetadataUninspectableReason.InvalidMetadata)]
        [TestCase(MagnetMetadataUninspectableReason.OversizedMetadata)]
        [TestCase(MagnetMetadataUninspectableReason.InfoHashMismatch)]
        [TestCase(MagnetMetadataUninspectableReason.EngineFailure)]
        public void strict_redirect_preserves_supplied_reason_and_rejection(MagnetMetadataUninspectableReason reason)
        {
            GivenRedirect(Magnet(HttpTracker));
            GivenUnresolved(reason);
            Reject().Message.Should().Contain("Reason=" + reason).And.Contain("Context=HttpRedirectToMagnet");
            VerifyNoSubmission();
        }

        [Test]
        public void unavailable_reason_is_unspecified_not_an_invented_failure()
        {
            GivenRedirect(Magnet(HttpTracker));
            _resolver.Setup(x => x.ResolveAsync(It.IsAny<MagnetLink>(), It.IsAny<CancellationToken>())).ReturnsAsync((MagnetMetadataResolutionResult)null);
            Reject().Message.Should().Contain("Reason=Unspecified").And.NotContain("Reason=EngineFailure").And.NotContain("Reason=Timeout");
            VerifyNoSubmission();
        }

        [Test]
        public void identical_hash_with_different_tracker_lists_has_independent_categories()
        {
            GivenRedirect(Magnet("udp://tracker.synthetic.invalid:6969/announce"));
            Reject().Message.Should().Contain("Reason=NoAllowedTrackers");
            GivenRedirect(Magnet(HttpTracker));
            GivenUnresolved(MagnetMetadataUninspectableReason.NoPeers);
            Reject().Message.Should().Contain("Reason=NoPeers");
            _resolver.Verify(x => x.ResolveAsync(It.IsAny<MagnetLink>(), It.IsAny<CancellationToken>()), Times.Once());
            VerifyNoSubmission();
        }

        [TestCase(MagnetMetadataUninspectableReason.EngineFailure, "mock DNS failure")]
        [TestCase(MagnetMetadataUninspectableReason.TrackerFailure, "mock tracker unavailable")]
        public void transport_failure_diagnostics_do_not_guess_a_more_specific_cause(MagnetMetadataUninspectableReason supplied, string scenario)
        {
            // The resolver contract supplies a category, not DNS/HTTP internals. Do not infer those from timing.
            scenario.Should().NotBeNullOrEmpty();
            GivenRedirect(Magnet(HttpTracker));
            GivenUnresolved(supplied);
            Reject().Message.Should().Contain("Reason=" + supplied).And.NotContain("Reason=DnsFailure");
            VerifyNoSubmission();
        }

        [Test]
        public void diagnostics_are_structured_bounded_and_free_of_sensitive_values()
        {
            GivenRedirect(Magnet(HttpTracker));
            _resolver.Setup(x => x.ResolveAsync(It.IsAny<MagnetLink>(), It.IsAny<CancellationToken>())).Returns(async () =>
            {
                await Task.Delay(20);
                return MagnetMetadataResolutionResult.Uninspectable(MagnetMetadataUninspectableReason.NoPeers);
            });
            var error = Reject();
            var diagnostic = _target.Events.Single(x => x.Message.StartsWith("Magnet metadata inspection rejected", StringComparison.Ordinal));
            diagnostic.Properties["Reason"].Should().Be("NoPeers");
            diagnostic.Properties["Context"].Should().Be("HttpRedirectToMagnet");
            diagnostic.Properties["IndexerId"].Should().Be(42);
            diagnostic.Properties["PermittedTrackerCount"].Should().Be(1);
            Convert.ToInt64(diagnostic.Properties["ElapsedMs"]).Should().BeInRange(0, int.MaxValue);
            error.Message.Should().Contain("IndexerId=42").And.Contain("PermittedTrackerCount=1");
            VerifySanitized(error);
            VerifyNoSubmission();
        }

        [TestCase("magnet:?xt=broken")]
        [TestCase("magnet:?xt=urn:btih:not-a-hash")]
        public void malformed_redirect_is_a_sanitized_release_error(string malformed)
        {
            GivenRedirect(malformed + "&tr=" + Uri.EscapeDataString(HttpTracker));
            var error = Reject();
            error.Message.Should().Contain("Failed to parse magnet").And.Contain("not submitted");
            error.InnerException.Should().BeNull();
            VerifySanitized(error);
            VerifyNoSubmission();
        }

        [Test]
        public void malformed_direct_magnet_is_rejected_without_fallback()
        {
            var release = Release();
            release.Release.DownloadUrl = "magnet:?xt=broken&api_key=" + Secret;
            var error = Assert.ThrowsAsync<ReleaseDownloadException>(() => _client.Download(release, _indexer.Object));
            VerifySanitized(error);
            VerifyNoSubmission();
        }

        [Test]
        public async Task clean_inspected_metadata_preserves_original_magnet_submission()
        {
            var magnet = Magnet(HttpTracker);
            GivenRedirect(magnet);
            _resolver.Setup(x => x.ResolveAsync(It.IsAny<MagnetLink>(), It.IsAny<CancellationToken>())).ReturnsAsync(MagnetMetadataResolutionResult.Resolved(new byte[] { 1, 2, 3 }));
            await _client.Download(Release(), _indexer.Object);
            _client.Magnets.Should().Equal(magnet);
            _client.TorrentSubmissions.Should().Be(0);
        }

        [Test]
        public void executable_metadata_is_still_rejected_before_submission()
        {
            GivenRedirect(Magnet(HttpTracker));
            GivenContents("Synthetic.mkv.exe");
            _resolver.Setup(x => x.ResolveAsync(It.IsAny<MagnetLink>(), It.IsAny<CancellationToken>())).ReturnsAsync(MagnetMetadataResolutionResult.Resolved(new byte[] { 1, 2, 3 }));
            Assert.ThrowsAsync<FakeReleaseException>(() => _client.Download(Release(), _indexer.Object));
            VerifyNoSubmission();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task direct_magnet_policy_is_unchanged_when_uninspectable(bool httpTracker)
        {
            var release = Release();
            var magnet = Magnet(httpTracker ? HttpTracker : "udp://tracker.synthetic.invalid:6969/announce");
            release.Release.DownloadUrl = magnet;
            await _client.Download(release, _indexer.Object);
            _client.Magnets.Should().Equal(magnet);
        }

        [Test]
        public async Task http_failure_fallback_policy_is_unchanged()
        {
            var release = Release();
            ((TorrentInfo)release.Release).MagnetUrl = Magnet(HttpTracker);
            _http.Setup(x => x.GetAsync(It.IsAny<HttpRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new WebException("synthetic fetch failure"));
            await _client.Download(release, _indexer.Object);
            _client.Magnets.Should().HaveCount(1);
        }

        [Test]
        public async Task ordinary_torrent_response_still_uses_file_inspection()
        {
            _http.Setup(x => x.GetAsync(It.IsAny<HttpRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((HttpRequest request, CancellationToken _) => new HttpResponse(request, new HttpHeader(), new byte[] { 1, 2, 3 }));
            await _client.Download(Release(), _indexer.Object);
            _client.TorrentSubmissions.Should().Be(1);
            _client.Magnets.Should().BeEmpty();
            _resolver.Verify(x => x.ResolveAsync(It.IsAny<MagnetLink>(), It.IsAny<CancellationToken>()), Times.Never());
            _reader.Verify(x => x.GetContentsFromTorrentFile(It.IsAny<byte[]>()), Times.Once());
        }

        private static string Magnet(string tracker)
        {
            return "magnet:?xt=urn:btih:" + InfoHash + "&dn=Synthetic.Show.S01E01&tr=" + Uri.EscapeDataString(tracker);
        }

        private static RemoteEpisode Release()
        {
            return new RemoteEpisode
            {
                Release = new TorrentInfo { Title = "Synthetic.Show.S01E01", IndexerId = 17, DownloadUrl = "http://download.synthetic.invalid/test.torrent" },
                ReleaseSource = ReleaseSourceType.InteractiveSearch
            };
        }

        private void GivenUnresolved(MagnetMetadataUninspectableReason reason)
        {
            _resolver.Setup(x => x.ResolveAsync(It.IsAny<MagnetLink>(), It.IsAny<CancellationToken>())).ReturnsAsync(MagnetMetadataResolutionResult.Uninspectable(reason));
        }

        private void GivenContents(string filename)
        {
            _reader.Setup(x => x.GetContentsFromTorrentFile(It.IsAny<byte[]>())).Returns(new TorrentContents { Files = new List<TorrentContentFile> { new TorrentContentFile(filename, 20000000) } });
        }

        private void GivenRedirect(string magnet)
        {
            _http.Setup(x => x.GetAsync(It.IsAny<HttpRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((HttpRequest request, CancellationToken _) =>
            {
                request.AllowAutoRedirect.Should().BeFalse();
                var headers = new HttpHeader();
                headers["Location"] = magnet;
                return new HttpResponse(request, headers, Array.Empty<byte>(), HttpStatusCode.Found);
            });
        }

        private ReleaseDownloadException Reject()
        {
            return Assert.ThrowsAsync<ReleaseDownloadException>(() => _client.Download(Release(), _indexer.Object));
        }

        private void VerifyNoSubmission()
        {
            _client.Magnets.Should().BeEmpty();
            _client.TorrentSubmissions.Should().Be(0);
        }

        private void VerifySanitized(Exception error)
        {
            var text = error.ToString() + string.Join("\n", _target.Events.Select(x => x.FormattedMessage + x.Exception + string.Join(" ", x.Properties.Values)));
            text.Should().NotContain(Secret).And.NotContain(InfoHash).And.NotContain("magnet:?").And.NotContain("tracker.synthetic.invalid").And.NotContain("download.synthetic.invalid").And.NotContain("indexer.synthetic.invalid");
        }

        private sealed class CaptureTarget : Target
        {
            public List<LogEventInfo> Events { get; } = new List<LogEventInfo>();

            protected override void Write(LogEventInfo logEvent)
            {
                _ = logEvent.FormattedMessage;
                Events.Add(logEvent);
            }
        }

        private sealed class RecordingClient : TorrentClientBase<QBittorrentSettings>
        {
            public RecordingClient(ITorrentFileInfoReader reader, IHttpClient http, IConfigService config, IMagnetMetadataResolver resolver, Logger logger)
                : base(reader, http, config, null, null, null, null, logger, resolver)
            {
                Definition = new DownloadClientDefinition { Implementation = "Synthetic", Settings = new QBittorrentSettings() };
            }

            public List<string> Magnets { get; } = new List<string>();
            public int TorrentSubmissions { get; private set; }
            public override string Name => "Synthetic";
            public override IEnumerable<DownloadClientItem> GetItems() => Array.Empty<DownloadClientItem>();
            public override void RemoveItem(DownloadClientItem item, bool deleteData) => throw new NotSupportedException();
            public override DownloadClientInfo GetStatus() => null;
            protected override void Test(List<ValidationFailure> failures)
            {
            }

            protected override string AddFromMagnetLink(RemoteEpisode remoteEpisode, string hash, string magnetLink)
            {
                Magnets.Add(magnetLink);
                return hash;
            }

            protected override string AddFromTorrentFile(RemoteEpisode remoteEpisode, string hash, string filename, byte[] fileContent)
            {
                TorrentSubmissions++;
                return hash;
            }
        }
    }
}
