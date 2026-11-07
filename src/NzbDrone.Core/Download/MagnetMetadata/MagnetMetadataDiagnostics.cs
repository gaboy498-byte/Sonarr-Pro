using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace NzbDrone.Core.Download.MagnetMetadata
{
    public sealed class MagnetMetadataDiagnostics
    {
        private int _connectionSamples;
        private int _peakOpenConnections;
        private int _peakHalfOpenConnections;
        private int _connectionObservationFailed;

        public int? SlotWaitMs { get; internal set; }
        public int? EngineInitMs { get; internal set; }
        public int? DiscoveryMs { get; internal set; }
        public int? StopMs { get; internal set; }
        public int? DisposeMs { get; internal set; }
        public int? CleanupMs { get; internal set; }
        public int TotalMs { get; internal set; }
        public int? PermittedTrackerCount { get; internal set; }
        public bool CallerCancellationRequested { get; internal set; }
        public bool TimeoutCancellationRequested { get; internal set; }
        public bool CancellationObserved { get; internal set; }
        public string CancellationStage { get; internal set; } = "NotObserved";
        public bool? MetadataDownloaded { get; internal set; }
        public string Cleanup { get; internal set; } = "NotRun";

        // These are sampled engine-wide connection peaks, not peer discoveries or attempt totals.
        public int ConnectionSamples => Math.Max(0, Volatile.Read(ref _connectionSamples));
        public int? PeakOpenConnectionsObserved => ConnectionSamples == 0 || Volatile.Read(ref _connectionObservationFailed) != 0 ? null : Volatile.Read(ref _peakOpenConnections);
        public int? PeakHalfOpenConnectionsObserved => ConnectionSamples == 0 || Volatile.Read(ref _connectionObservationFailed) != 0 ? null : Volatile.Read(ref _peakHalfOpenConnections);

        internal static int ElapsedMs(long started)
        {
            return BoundedMilliseconds(Stopwatch.GetElapsedTime(started));
        }

        internal static int BoundedMilliseconds(TimeSpan elapsed)
        {
            return (int)Math.Clamp(elapsed.TotalMilliseconds, 0, int.MaxValue);
        }

        internal void ObserveConnections(int open, int halfOpen)
        {
            Interlocked.Increment(ref _connectionSamples);
            UpdateMaximum(ref _peakOpenConnections, Math.Max(0, open));
            UpdateMaximum(ref _peakHalfOpenConnections, Math.Max(0, halfOpen));
        }

        internal void ConnectionObservationFailed()
        {
            Interlocked.Exchange(ref _connectionObservationFailed, 1);
        }

        internal void ObserveCancellation(string stage)
        {
            CancellationObserved = true;
            if (CancellationStage == "NotObserved")
            {
                CancellationStage = stage;
            }
        }

        private static void UpdateMaximum(ref int location, int value)
        {
            var previous = Volatile.Read(ref location);
            while (value > previous)
            {
                var observed = Interlocked.CompareExchange(ref location, value, previous);
                if (observed == previous)
                {
                    return;
                }

                previous = observed;
            }
        }

        private static string Measured(int? value)
        {
            return value?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable";
        }

        public override string ToString()
        {
            return FormattableString.Invariant(
                $"SlotWaitMs={Measured(SlotWaitMs)}; EngineInitMs={Measured(EngineInitMs)}; DiscoveryMs={Measured(DiscoveryMs)}; StopMs={Measured(StopMs)}; DisposeMs={Measured(DisposeMs)}; CleanupMs={Measured(CleanupMs)}; TotalMs={TotalMs}; PermittedTrackerCount={Measured(PermittedTrackerCount)}; CallerCancellationRequested={CallerCancellationRequested}; TimeoutCancellationRequested={TimeoutCancellationRequested}; CancellationObserved={CancellationObserved}; CancellationStage={CancellationStage}; ConnectionSamples={ConnectionSamples}; PeakOpenConnectionsObserved={Measured(PeakOpenConnectionsObserved)}; PeakHalfOpenConnectionsObserved={Measured(PeakHalfOpenConnectionsObserved)}; TrackerStats=Unavailable; PeerDiscoveryStats=Unavailable; MetadataProgress=Unavailable; MetadataDownloaded={MetadataDownloaded?.ToString() ?? "Unavailable"}; Cleanup={Cleanup}");
        }
    }
}
