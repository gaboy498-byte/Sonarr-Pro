using System;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

namespace NzbDrone.Core.Download.MagnetMetadata
{
    // A lifecycle-only test seam; production delegates unchanged to ClientEngine's public API.
    internal interface IMagnetMetadataEngine : IDisposable
    {
        Task StartAllAsync();
        Task<ReadOnlyMemory<byte>> DownloadMetadataAsync(MagnetLink magnet, TorrentSettings settings, CancellationToken token);
        Task StopAllAsync(TimeSpan timeout);
    }
}
