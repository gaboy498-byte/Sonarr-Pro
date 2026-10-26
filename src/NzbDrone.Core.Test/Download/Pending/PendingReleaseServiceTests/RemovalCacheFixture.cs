using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Crypto;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.Download.Pending.PendingReleaseServiceTests
{
    [TestFixture]
    [NonParallelizable]
    public class RemovalCacheFixture : CoreTest<PendingReleaseService>
    {
        private List<PendingRelease> _stored;
        private List<int[]> _observedQueueIds;
        private Series _series;

        [SetUp]
        public void Setup()
        {
            _stored = new List<PendingRelease>();
            _observedQueueIds = new List<int[]>();
            _series = new Series
            {
                Id = 1,
                Title = "Synthetic Series",
                Tags = new HashSet<int>(),
                QualityProfile = new QualityProfile
                {
                    Items = new List<QualityProfileQualityItem>
                    {
                        new QualityProfileQualityItem { Quality = Quality.HDTV720p, Allowed = true }
                    }
                }
            };

            Mocker.GetMock<IPendingReleaseRepository>().Setup(r => r.All()).Returns(() => _stored.ToList());
            Mocker.GetMock<IPendingReleaseRepository>().Setup(r => r.AllBySeriesId(1)).Returns(() => _stored.ToList());
            Mocker.GetMock<IPendingReleaseRepository>()
                .Setup(r => r.DeleteMany(It.IsAny<IEnumerable<int>>()))
                .Callback<IEnumerable<int>>(ids =>
                {
                    var deleted = ids.ToHashSet();
                    _stored.RemoveAll(p => deleted.Contains(p.Id));
                });

            Mocker.GetMock<ISeriesService>()
                .Setup(s => s.GetSeries(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<Series> { _series });
            Mocker.GetMock<IParsingService>()
                .Setup(p => p.Map(It.IsAny<ParsedEpisodeInfo>(), _series))
                .Returns<ParsedEpisodeInfo, Series>((parsed, series) => new RemoteEpisode
                {
                    Episodes = parsed.EpisodeNumbers.Select(number => new Episode
                    {
                        Id = (parsed.SeasonNumber * 100) + number,
                        SeriesId = series.Id,
                        SeasonNumber = parsed.SeasonNumber,
                        EpisodeNumber = number
                    }).ToList()
                });

            Mocker.GetMock<IDelayProfileService>()
                .Setup(d => d.AllForTags(It.IsAny<HashSet<int>>()))
                .Returns(new List<DelayProfile> { new DelayProfile() });
            Mocker.GetMock<IDelayProfileService>()
                .Setup(d => d.BestForTags(It.IsAny<HashSet<int>>()))
                .Returns(new DelayProfile());
            Mocker.GetMock<ITaskManager>()
                .Setup(t => t.GetNextExecution(It.IsAny<Type>()))
                .Returns(DateTime.UtcNow.AddMinutes(5));
            Mocker.GetMock<IConfigService>().SetupGet(c => c.RssSyncInterval).Returns(15);
            Mocker.GetMock<IEventAggregator>()
                .Setup(e => e.PublishEvent(It.IsAny<PendingReleasesUpdatedEvent>()))
                .Callback<PendingReleasesUpdatedEvent>(_ => _observedQueueIds.Add(Subject.GetPendingQueue().Select(q => q.Id).ToArray()));
        }

        private void AddRelease(int id, int season, int episode, PendingReleaseReason reason)
        {
            _stored.Add(new PendingRelease
            {
                Id = id,
                SeriesId = _series.Id,
                Title = "Synthetic.Series.S01E01-" + id,
                Reason = reason,
                ParsedEpisodeInfo = new ParsedEpisodeInfo
                {
                    SeriesTitle = _series.Title,
                    SeasonNumber = season,
                    EpisodeNumbers = new[] { episode },
                    Languages = new List<Language> { Language.English },
                    Quality = new QualityModel(Quality.HDTV720p)
                },
                Release = new ReleaseInfo { PublishDate = DateTime.UtcNow, Size = 1024 }
            });
        }

        private void LoadCache()
        {
            Subject.Handle(new ApplicationStartedEvent());
            _observedQueueIds.Clear();
        }

        private void Remove(int id, int season, int episode, bool obsolete)
        {
            if (obsolete)
            {
                Subject.RemovePendingQueueItemsObsolete(HashConverter.GetHashInt31($"pending-{id}-ep{(season * 100) + episode}"));
            }
            else
            {
                Subject.RemovePendingQueueItems(HashConverter.GetHashInt31($"pending-{id}"));
            }
        }

        [Test]
        public void should_refresh_cache_before_each_notification(
            [Values(PendingReleaseReason.Delay, PendingReleaseReason.DownloadClientUnavailable)] PendingReleaseReason reason,
            [Values(false, true)] bool obsolete,
            [Values(1, 2, 3)] int selectedCount)
        {
            for (var id = 1; id <= 3; id++)
            {
                AddRelease(id, 1, id, reason);
            }

            LoadCache();

            for (var id = 1; id <= selectedCount; id++)
            {
                Remove(id, 1, id, obsolete);
                Subject.GetPendingQueue().Select(q => q.Id).Should().BeEquivalentTo(_stored.Select(p => HashConverter.GetHashInt31($"pending-{p.Id}")));
                Subject.GetPendingRemoteEpisodes(_series.Id).Should().HaveCount(_stored.Count);
                _observedQueueIds.Should().HaveCount(id);
                _observedQueueIds.Last().Should().BeEquivalentTo(Subject.GetPendingQueue().Select(q => q.Id));
            }
        }

        [Test]
        public void should_preserve_other_groups_and_remove_matching_siblings(
            [Values(PendingReleaseReason.Delay, PendingReleaseReason.DownloadClientUnavailable)] PendingReleaseReason reason,
            [Values(false, true)] bool obsolete)
        {
            AddRelease(1, 1, 1, reason);
            AddRelease(2, 1, 1, reason);
            AddRelease(3, 2, 1, reason);
            AddRelease(4, 1, 2, reason);
            LoadCache();

            Remove(1, 1, 1, obsolete);

            _stored.Select(p => p.Id).Should().Equal(3, 4);
            Subject.GetPendingQueue().Should().HaveCount(2);
            Subject.GetPendingRemoteEpisodes(_series.Id).Should().HaveCount(2);
            _observedQueueIds.Should().ContainSingle();
            _observedQueueIds.Single().Should().BeEquivalentTo(Subject.GetPendingQueue().Select(q => q.Id));
        }

        [Test]
        public void should_leave_cache_and_notifications_unchanged_when_delete_fails(
            [Values(PendingReleaseReason.Delay, PendingReleaseReason.DownloadClientUnavailable)] PendingReleaseReason reason,
            [Values(false, true)] bool obsolete)
        {
            AddRelease(1, 1, 1, reason);
            LoadCache();
            Mocker.GetMock<IPendingReleaseRepository>()
                .Setup(r => r.DeleteMany(It.IsAny<IEnumerable<int>>()))
                .Throws(new InvalidOperationException("Synthetic deletion failure"));

            Assert.Throws<InvalidOperationException>(() => Remove(1, 1, 1, obsolete));

            _stored.Should().ContainSingle();
            Subject.GetPendingQueue().Should().ContainSingle();
            Subject.GetPendingRemoteEpisodes(_series.Id).Should().ContainSingle();
            _observedQueueIds.Should().BeEmpty();
        }

        [Test]
        public void should_publish_once_when_cache_is_loaded()
        {
            AddRelease(1, 1, 1, PendingReleaseReason.Delay);

            Subject.Handle(new ApplicationStartedEvent());

            _observedQueueIds.Should().ContainSingle();
            _observedQueueIds.Single().Should().ContainSingle();
        }
    }
}
