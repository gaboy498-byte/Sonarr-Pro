using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using NzbDrone.SignalR;
using Sonarr.Api.V5.Queue;
using Sonarr.Http.REST;
using QueueModel = NzbDrone.Core.Queue.Queue;

namespace NzbDrone.Api.Test.v5.Queue
{
    [TestFixture]
    public class QueueRemovalFixture
    {
        private Mock<IPendingReleaseService> _pending;
        private Mock<IQueueService> _queue;
        private Mock<ITrackedDownloadService> _tracking;
        private Mock<IProvideDownloadClient> _clients;
        private Mock<IFailedDownloadService> _failed;
        private Mock<IIgnoredDownloadService> _ignored;
        private Mock<IBlocklistService> _blocklist;
        private Dictionary<int, QueueModel> _remaining;
        private Dictionary<int, int> _groups;
        private QueueController _controller;

        [SetUp]
        public void Setup()
        {
            _pending = new Mock<IPendingReleaseService>();
            _queue = new Mock<IQueueService>();
            _tracking = new Mock<ITrackedDownloadService>();
            _clients = new Mock<IProvideDownloadClient>();
            _failed = new Mock<IFailedDownloadService>();
            _ignored = new Mock<IIgnoredDownloadService>();
            _blocklist = new Mock<IBlocklistService>();
            _remaining = new Dictionary<int, QueueModel>();
            _groups = new Dictionary<int, int>();
            _pending.Setup(p => p.FindPendingQueueItem(It.IsAny<int>()))
                .Returns<int>(id => _remaining.GetValueOrDefault(id));
            _pending.Setup(p => p.RemovePendingQueueItems(It.IsAny<int>()))
                .Callback<int>(id =>
                {
                    if (!_remaining.ContainsKey(id))
                    {
                        throw new InvalidOperationException("The selected pending release has already been removed");
                    }

                    foreach (var sibling in _remaining.Keys.Where(key => _groups[key] == _groups[id]).ToList())
                    {
                        _remaining.Remove(sibling);
                    }
                });

            var profiles = new Mock<IQualityProfileService>();
            profiles.Setup(p => p.GetDefaultProfile(string.Empty)).Returns(new QualityProfile
            {
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Quality = Quality.HDTV720p, Allowed = true }
                }
            });
            _controller = new QueueController(Mock.Of<IBroadcastSignalRMessage>(), _queue.Object, _pending.Object, profiles.Object, _tracking.Object, _failed.Object, _ignored.Object, _clients.Object, _blocklist.Object)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            _controller.Request.Headers["X-Sonarr-Client"] = "Fixture";
        }

        private QueueModel AddPending(int id, int group)
        {
            var item = new QueueModel { Id = id, RemoteEpisode = new RemoteEpisode() };
            _remaining.Add(id, item);
            _groups.Add(id, group);
            return item;
        }

        private TrackedDownload AddTracked(DownloadItemStatus status)
        {
            var tracked = new TrackedDownload
            {
                DownloadClient = 42,
                DownloadItem = new DownloadClientItem { DownloadId = "synthetic-download", Status = status }
            };
            _queue.Setup(q => q.Find(99)).Returns(new QueueModel { Id = 99, DownloadId = tracked.DownloadItem.DownloadId });
            _tracking.Setup(t => t.Find(tracked.DownloadItem.DownloadId)).Returns(tracked);
            return tracked;
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void should_remove_overlapping_group_once_and_preserve_selected_blocklisting(bool blocklist, bool selectAll)
        {
            var first = AddPending(11, 1);
            var sibling = AddPending(12, 1);
            AddPending(13, 2);

            var ids = selectAll ? new List<int> { 11, 12, 13 } : new List<int> { 11, 12 };

            Assert.DoesNotThrow(() => _controller.RemoveMany(new QueueBulkResource { Ids = ids }, "Fixture", blocklist: blocklist));

            _remaining.Keys.Should().BeEquivalentTo(selectAll ? Array.Empty<int>() : new[] { 13 });
            _pending.Verify(p => p.RemovePendingQueueItems(11), Times.Once());
            _pending.Verify(p => p.RemovePendingQueueItems(12), Times.Never());
            _blocklist.Verify(b => b.Block(first.RemoteEpisode, "Fixture", "Fixture"), blocklist ? Times.Once() : Times.Never());
            _blocklist.Verify(b => b.Block(sibling.RemoteEpisode, "Fixture", "Fixture"), blocklist ? Times.Once() : Times.Never());
            _clients.Verify(c => c.Get(It.IsAny<int>()), Times.Never());
        }

        [TestCase(2)]
        [TestCase(3)]
        public void should_remove_multiple_or_all_independent_pending_groups(int selectedCount)
        {
            AddPending(11, 1);
            AddPending(12, 2);
            AddPending(13, 3);
            var ids = Enumerable.Range(11, selectedCount).ToList();

            _controller.RemoveMany(new QueueBulkResource { Ids = ids }, null);

            _remaining.Keys.Should().BeEquivalentTo(new[] { 11, 12, 13 }.Except(ids));
            foreach (var id in ids)
            {
                _pending.Verify(p => p.RemovePendingQueueItems(id), Times.Once());
            }
        }

        [Test]
        public void should_deduplicate_repeated_selected_id()
        {
            AddPending(11, 1);

            _controller.RemoveMany(new QueueBulkResource { Ids = new List<int> { 11, 11 } }, null, blocklist: true);

            _pending.Verify(p => p.RemovePendingQueueItems(11), Times.Once());
            _blocklist.Verify(b => b.Block(It.IsAny<RemoteEpisode>(), It.IsAny<string>(), "Fixture"), Times.Once());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_reject_initially_invalid_id_before_removing_valid_pending_items(bool invalidFirst)
        {
            AddPending(11, 1);
            var ids = invalidFirst ? new List<int> { 999, 11 } : new List<int> { 11, 999 };

            Assert.Throws<NotFoundException>(() => _controller.RemoveMany(new QueueBulkResource { Ids = ids }, null));

            _pending.Verify(p => p.RemovePendingQueueItems(It.IsAny<int>()), Times.Never());
            _remaining.Should().ContainSingle();
        }

        [Test]
        public void should_preserve_single_pending_removal()
        {
            AddPending(11, 1);

            _controller.RemoveAction(11);

            _remaining.Should().BeEmpty();
            _pending.Verify(p => p.RemovePendingQueueItems(11), Times.Once());
            _clients.Verify(c => c.Get(It.IsAny<int>()), Times.Never());
        }

        [TestCase(DownloadItemStatus.Downloading)]
        [TestCase(DownloadItemStatus.Completed)]
        public void should_preserve_mixed_removal_and_delete_data_flag(DownloadItemStatus status)
        {
            AddPending(11, 1);
            var tracked = AddTracked(status);
            var client = new Mock<IDownloadClient>();
            _clients.Setup(c => c.Get(42)).Returns(client.Object);

            _controller.RemoveMany(new QueueBulkResource { Ids = new List<int> { 11, 99, 99 } }, null);

            client.Verify(c => c.RemoveItem(tracked.DownloadItem, true), Times.Once());
            client.Verify(c => c.MarkItemAsImported(It.IsAny<DownloadClientItem>()), Times.Never());
            _tracking.Verify(t => t.StopTracking(It.Is<List<string>>(ids => ids.SequenceEqual(new[] { "synthetic-download" }))), Times.Once());
            _remaining.Should().BeEmpty();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void should_preserve_tracked_blocklist_and_skip_redownload(bool skipRedownload)
        {
            var tracked = AddTracked(DownloadItemStatus.Downloading);

            _controller.RemoveMany(new QueueBulkResource { Ids = new List<int> { 99 } }, "Fixture", removeFromClient: false, blocklist: true, skipRedownload: skipRedownload);

            _failed.Verify(f => f.MarkAsFailed(tracked, "Fixture", "Fixture", skipRedownload), Times.Once());
            _clients.Verify(c => c.Get(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_preserve_category_change_without_deleting_files()
        {
            var tracked = AddTracked(DownloadItemStatus.Completed);
            var client = new Mock<IDownloadClient>();
            _clients.Setup(c => c.Get(42)).Returns(client.Object);

            _controller.RemoveMany(new QueueBulkResource { Ids = new List<int> { 99 } }, null, removeFromClient: false, changeCategory: true);

            client.Verify(c => c.MarkItemAsImported(tracked.DownloadItem), Times.Once());
            client.Verify(c => c.RemoveItem(It.IsAny<DownloadClientItem>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_preserve_ignore_without_deleting_files()
        {
            var tracked = AddTracked(DownloadItemStatus.Completed);
            _ignored.Setup(i => i.IgnoreDownload(tracked)).Returns(true);

            _controller.RemoveMany(new QueueBulkResource { Ids = new List<int> { 99 } }, null, removeFromClient: false);

            _ignored.Verify(i => i.IgnoreDownload(tracked), Times.Once());
            _clients.Verify(c => c.Get(It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_honor_every_explicit_ui_option_without_unintended_file_deletion(
            [Values(false, true)] bool bulk,
            [Values(DownloadItemStatus.Downloading, DownloadItemStatus.Completed)] DownloadItemStatus status,
            [Values("removeFromClient", "changeCategory", "ignore")] string method,
            [Values(0, 1, 2)] int blocklistMethod)
        {
            var tracked = AddTracked(status);
            var client = new Mock<IDownloadClient>();
            _clients.Setup(c => c.Get(42)).Returns(client.Object);
            _ignored.Setup(i => i.IgnoreDownload(tracked)).Returns(true);
            var removeFromClient = method == "removeFromClient";
            var changeCategory = method == "changeCategory";
            var blocklist = blocklistMethod != 0;
            var skipRedownload = blocklistMethod == 2;

            if (bulk)
            {
                _controller.RemoveMany(new QueueBulkResource { Ids = new List<int> { 99 } }, "Fixture", removeFromClient, blocklist, skipRedownload, changeCategory);
            }
            else
            {
                _controller.RemoveAction(99, "Fixture", removeFromClient, blocklist, skipRedownload, changeCategory);
            }

            client.Verify(c => c.RemoveItem(tracked.DownloadItem, true), removeFromClient ? Times.Once() : Times.Never());
            client.Verify(c => c.RemoveItem(It.IsAny<DownloadClientItem>(), false), Times.Never());
            client.Verify(c => c.MarkItemAsImported(tracked.DownloadItem), changeCategory ? Times.Once() : Times.Never());
            _failed.Verify(f => f.MarkAsFailed(tracked, "Fixture", "Fixture", skipRedownload), blocklist ? Times.Once() : Times.Never());
            _ignored.Verify(i => i.IgnoreDownload(tracked), method == "ignore" && !blocklist ? Times.Once() : Times.Never());
        }

        [Test]
        public void should_surface_partial_bulk_failure_after_first_pending_group_was_removed()
        {
            AddPending(11, 1);
            AddPending(12, 2);
            _pending.Setup(p => p.RemovePendingQueueItems(12)).Throws(new InvalidOperationException("Synthetic failure"));

            Assert.Throws<InvalidOperationException>(() => _controller.RemoveMany(new QueueBulkResource { Ids = new List<int> { 11, 12 } }, null));

            _remaining.Keys.Should().BeEquivalentTo(new[] { 12 });
            _pending.Verify(p => p.RemovePendingQueueItems(11), Times.Once());
            _tracking.Verify(t => t.StopTracking(It.IsAny<List<string>>()), Times.Never());
        }

        [Test]
        public void should_surface_client_failure_after_pending_item_was_removed()
        {
            AddPending(11, 1);
            var tracked = AddTracked(DownloadItemStatus.Downloading);
            var client = new Mock<IDownloadClient>();
            _clients.Setup(c => c.Get(42)).Returns(client.Object);
            client.Setup(c => c.RemoveItem(tracked.DownloadItem, true)).Throws(new InvalidOperationException("Synthetic client failure"));

            Assert.Throws<InvalidOperationException>(() => _controller.RemoveMany(new QueueBulkResource { Ids = new List<int> { 11, 99 } }, null));

            _remaining.Should().BeEmpty();
            _tracking.Verify(t => t.StopTracking(It.IsAny<List<string>>()), Times.Never());
        }

        [Test]
        public void should_propagate_pending_deletion_failure()
        {
            AddPending(11, 1);
            _pending.Setup(p => p.RemovePendingQueueItems(11)).Throws(new InvalidOperationException("Synthetic failure"));

            Assert.Throws<InvalidOperationException>(() => _controller.RemoveMany(new QueueBulkResource { Ids = new List<int> { 11 } }, null));

            _tracking.Verify(t => t.StopTracking(It.IsAny<List<string>>()), Times.Never());
            _remaining.Should().ContainSingle();
        }
    }
}
