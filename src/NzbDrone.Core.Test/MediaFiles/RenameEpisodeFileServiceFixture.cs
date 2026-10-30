using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.MediaFiles
{
    public class RenameEpisodeFileServiceFixture : CoreTest<RenameEpisodeFileService>
    {
        private Series _series;
        private List<EpisodeFile> _episodeFiles;

        [SetUp]
        public void Setup()
        {
            _series = Builder<Series>.CreateNew()
                                     .Build();

            _episodeFiles = Builder<EpisodeFile>.CreateListOfSize(2)
                                                .All()
                                                .With(e => e.SeriesId = _series.Id)
                                                .With(e => e.SeasonNumber = 1)
                                                .Build()
                                                .ToList();

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.GetSeries(_series.Id))
                  .Returns(_series);
        }

        private void GivenNoEpisodeFiles()
        {
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.Get(It.IsAny<IEnumerable<int>>()))
                  .Returns(new List<EpisodeFile>());
        }

        private void GivenEpisodeFiles()
        {
            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.Get(It.IsAny<IEnumerable<int>>()))
                  .Returns(_episodeFiles);
        }

        private void GivenMovedFiles()
        {
            Mocker.GetMock<IMoveEpisodeFiles>()
                  .Setup(s => s.MoveEpisodeFile(It.IsAny<EpisodeFile>(), _series));
        }

        [Test]
        public void should_not_publish_event_if_no_files_to_rename()
        {
            GivenNoEpisodeFiles();

            Subject.Execute(new RenameFilesCommand(_series.Id, new List<int> { 1 }));

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<SeriesRenamedEvent>()), Times.Never());
        }

        [Test]
        public void should_not_publish_event_if_no_files_are_renamed()
        {
            GivenEpisodeFiles();

            Mocker.GetMock<IMoveEpisodeFiles>()
                  .Setup(s => s.MoveEpisodeFile(It.IsAny<EpisodeFile>(), It.IsAny<Series>()))
                  .Throws(new SameFilenameException("Same file name", "Filename"));

            Subject.Execute(new RenameFilesCommand(_series.Id, new List<int> { 1 }));

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<SeriesRenamedEvent>()), Times.Never());
        }

        [Test]
        public void should_publish_event_if_files_are_renamed()
        {
            GivenEpisodeFiles();
            GivenMovedFiles();

            Subject.Execute(new RenameFilesCommand(_series.Id, new List<int> { 1 }));

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.IsAny<SeriesRenamedEvent>()), Times.Once());
        }

        [Test]
        public void should_update_moved_files()
        {
            GivenEpisodeFiles();
            GivenMovedFiles();

            Subject.Execute(new RenameFilesCommand(_series.Id, new List<int> { 1 }));

            Mocker.GetMock<IMediaFileService>()
                  .Verify(v => v.Update(It.IsAny<EpisodeFile>()), Times.Exactly(2));
        }

        [Test]
        public void should_get_episodefiles_by_ids_only()
        {
            GivenEpisodeFiles();
            GivenMovedFiles();

            var files = new List<int> { 1 };

            Subject.Execute(new RenameFilesCommand(_series.Id, files));

            Mocker.GetMock<IMediaFileService>()
                  .Verify(v => v.Get(files), Times.Once());
        }

        [Test]
        public void selected_command_should_publish_only_successful_files_with_previous_and_current_paths()
        {
            var selected = _episodeFiles[0];
            selected.RelativePath = "old.mkv";
            Mocker.GetMock<IMediaFileService>().Setup(s => s.Get(It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { selected.Id }))))
                  .Returns(new List<EpisodeFile> { selected });
            Mocker.GetMock<IMoveEpisodeFiles>().Setup(s => s.MoveEpisodeFile(selected, _series))
                  .Callback(() => selected.RelativePath = "new.mkv");
            SeriesRenamedEvent published = null;
            Mocker.GetMock<IEventAggregator>().Setup(s => s.PublishEvent(It.IsAny<SeriesRenamedEvent>()))
                  .Callback<SeriesRenamedEvent>(message => published = message);

            Subject.Execute(new RenameFilesCommand(_series.Id, new List<int> { selected.Id }));

            published.Should().NotBeNull();
            published.RenamedFiles.Should().ContainSingle();
            published.RenamedFiles[0].EpisodeFile.Id.Should().Be(selected.Id);
            published.RenamedFiles[0].EpisodeFile.RelativePath.Should().Be("new.mkv");
            published.RenamedFiles[0].PreviousRelativePath.Should().Be("old.mkv");
        }

        [Test]
        public void full_series_command_should_publish_all_successfully_renamed_files()
        {
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(It.IsAny<IEnumerable<int>>()))
                  .Returns(new List<Series> { _series });
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesBySeries(_series.Id)).Returns(_episodeFiles);
            SeriesRenamedEvent published = null;
            Mocker.GetMock<IEventAggregator>().Setup(s => s.PublishEvent(It.IsAny<SeriesRenamedEvent>()))
                  .Callback<SeriesRenamedEvent>(message => published = message);

            Subject.Execute(new RenameSeriesCommand { SeriesIds = new List<int> { _series.Id } });

            published.Should().NotBeNull();
            published.RenamedFiles.Select(f => f.EpisodeFile.Id).Should().Equal(_episodeFiles.Select(f => f.Id));
        }

        [Test]
        public void full_series_event_should_exclude_files_that_did_not_rename()
        {
            Mocker.GetMock<ISeriesService>().Setup(s => s.GetSeries(It.IsAny<IEnumerable<int>>()))
                  .Returns(new List<Series> { _series });
            Mocker.GetMock<IMediaFileService>().Setup(s => s.GetFilesBySeries(_series.Id)).Returns(_episodeFiles);
            Mocker.GetMock<IMoveEpisodeFiles>().Setup(s => s.MoveEpisodeFile(_episodeFiles[1], _series))
                  .Throws(new SameFilenameException("Already correct", "same.mkv"));
            SeriesRenamedEvent published = null;
            Mocker.GetMock<IEventAggregator>().Setup(s => s.PublishEvent(It.IsAny<SeriesRenamedEvent>()))
                  .Callback<SeriesRenamedEvent>(message => published = message);

            Subject.Execute(new RenameSeriesCommand { SeriesIds = new List<int> { _series.Id } });

            published.Should().NotBeNull();
            published.RenamedFiles.Should().ContainSingle();
            published.RenamedFiles[0].EpisodeFile.Id.Should().Be(_episodeFiles[0].Id);
        }
    }
}
