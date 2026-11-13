using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Zep.Core.Application
{
    public enum ZepPerfCounter
    {
        FullSynchronizerRuns,
        FullRecordEnumerations,
        FolderTreeWalks,
        RebuildProjectionCacheCalls,
        SearchFullBuilds,
        ProjectionPublications,
        RawPostprocessorCallbacks,
        NormalizedPostprocessorBatches,
        DeduplicatedAcknowledgements,
        EditorTurnPublications,
        AssetDatabaseFindAssetsCalls,
        AssetDatabaseGetAllAssetPathsCalls,
        AssetDatabaseGuidQueries,
        AssetDatabaseTypeQueries,
        AssetDatabaseLabelQueries,
        AssetDatabaseUniquePathQueries,
        AssetDatabaseCreateFolderCalls,
        AssetDatabaseCreateAssetCalls,
        AssetDatabaseMoveAssetCalls,
        AssetDatabaseCopyAssetCalls,
        AssetDatabaseDeleteAssetCalls,
        AssetDatabaseImportAssetCalls,
        AssetDatabaseSaveAssetsCalls,
        AssetDatabaseStartAssetEditingCalls,
        AssetDatabaseStopAssetEditingCalls,
        AssetDatabaseGlobalRefreshCalls,
        BulkCatalogApplications,
        SearchQueries,
        SearchProjectionRequests,
        SearchProjectionBuilds,
        AssetCollectionPublications,
        SearchEntriesMaterialized,
        CreateFallbackScopedCaptures,
        CreateFallbackProjectWideGuidDiffs,
        CreateFallbackAmbiguousResults,
        // Appended 2026-09-02 for the business-logic audit's scaling proofs.
        // The enum is append-only: ordinals index the counter array and are
        // quoted in ledger evidence, so never reorder or remove a member.
        MetaFileGuidReads,
        TagPresentationRebuilds,
        FolderCatalogRemerges,
        AssetChangesNormalizations,
        SearchAlphabeticalResorts,
        SubAssetCatalogChangedRaised,
        UndoHistorySaves,
        PaneChangedDispatches,
        // Raised once per cold-scan overlay shown to the user (the controller's
        // onFullScanStarted). Persisted snapshots read counters by ordinal, so new
        // members go last.
        ColdScanOverlays,
        // Raised once per silent verification walk the synchronizer starts after a
        // hydration it could not vouch for.
        VerificationJobs,
    }

    public enum ZepPerfStage
    {
        UnityBoundary,
        CatalogMutation,
        ProjectionBuild,
        ViewModelPublication,
        ShellRefresh,
        SidebarRefresh,
        AssetListRefresh,
        PostprocessorAcknowledgement,
        PersistentCacheValidation,
        PersistentCacheHydration,
        SearchWarmBuild,
        NativeCopy,
        TargetedImportGroup,
    }

    internal readonly struct ZepPerfMeasurement
    {
        internal ZepPerfMeasurement(
            string operation,
            ZepPerfStage stage,
            double elapsedMilliseconds)
        {
            Operation = operation ?? string.Empty;
            Stage = stage;
            ElapsedMilliseconds = elapsedMilliseconds;
        }

        public string Operation { get; }

        public ZepPerfStage Stage { get; }

        public double ElapsedMilliseconds { get; }
    }

    internal sealed class ZepPerfSnapshot
    {
        private readonly long[] _counters;

        internal ZepPerfSnapshot(
            long[] counters,
            IReadOnlyList<ZepPerfMeasurement> measurements)
        {
            _counters = counters ?? Array.Empty<long>();
            Measurements = measurements ?? Array.Empty<ZepPerfMeasurement>();
        }

        public IReadOnlyList<ZepPerfMeasurement> Measurements { get; }

        public long GetCount(ZepPerfCounter counter)
        {
            var index = (int)counter;
            return index >= 0 && index < _counters.Length ? _counters[index] : 0L;
        }
    }

    // Lightweight perf-logging sink shared across the package. Disabled by
    // default, so callers pay only a bool check unless a developer turns it on
    // via the Bridge menu toggle. Writes to System.Console (captured by
    // Editor.log), a dedicated file (survives a hard freeze), and an optional
    // Sink (Bridge wires this to UnityEngine.Debug.Log). Logging must never
    // throw into the editor, so all file IO is swallowed and serialized.
    public static class ZepPerfLog
    {
        private static readonly object Gate = new object();
        private static readonly long[] Counters =
            new long[Enum.GetValues(typeof(ZepPerfCounter)).Length];

        // Every reader asks "did the recent window contain operation X" (the tests
        // and the deletion-parity probe all filter a snapshot taken right after a
        // Reset), so only the most recent measurements can ever be inspected. The
        // toggle is EditorPrefs-persisted and records one entry per measured
        // operation, so an unbounded list would grow for the whole session; a ring
        // keeps the answer identical and the cost of CaptureSnapshot fixed.
        internal const int MeasurementCapacity = 4096;
        private static readonly ZepPerfMeasurement[] Measurements =
            new ZepPerfMeasurement[MeasurementCapacity];
        private static int _measurementNext;
        private static int _measurementCount;
        private static StreamWriter _writer;
        private static readonly int MainThreadId = Thread.CurrentThread.ManagedThreadId;

        private static bool _isEnabled;

        // Turning the toggle off is one of the three moments the buffered
        // writer has to reach disk (the others are Reset and domain unload):
        // after it, nothing else will ever write, so an unflushed tail would
        // be the most interesting part of the log and would never appear.
        public static bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value)
                {
                    return;
                }

                _isEnabled = value;
                if (!value)
                {
                    Flush();
                }
            }
        }

        public static Action<string> Sink;

        public static readonly string FilePath =
            Path.Combine(Path.GetTempPath(), "ZepPerf.log");

        // Core is engine-free, so the editor's own quit/reload hooks are not
        // reachable from here: AppDomain unload covers both a domain reload and
        // editor shutdown, which is when a session's tail would otherwise be
        // lost. The Bridge toggle already routes through IsEnabled.
        static ZepPerfLog()
        {
            AppDomain.CurrentDomain.DomainUnload += (_, __) => Flush();
        }

        public static MeasurementScope Measure(
            string operation,
            ZepPerfStage stage)
        {
            return IsEnabled
                ? new MeasurementScope(operation, stage, Stopwatch.GetTimestamp())
                : default;
        }

        public static void Count(ZepPerfCounter counter)
        {
            if (!IsEnabled)
            {
                return;
            }

            Interlocked.Increment(ref Counters[(int)counter]);
        }

        internal static ZepPerfSnapshot CaptureSnapshot()
        {
            var counters = new long[Counters.Length];
            for (var i = 0; i < counters.Length; i++)
            {
                counters[i] = Interlocked.Read(ref Counters[i]);
            }

            lock (Gate)
            {
                var measurements =
                    new ZepPerfMeasurement[_measurementCount];
                var oldest = _measurementCount == MeasurementCapacity
                    ? _measurementNext
                    : 0;
                for (var i = 0; i < _measurementCount; i++)
                {
                    measurements[i] =
                        Measurements[(oldest + i) % MeasurementCapacity];
                }

                return new ZepPerfSnapshot(counters, measurements);
            }
        }

        internal static void Reset()
        {
            for (var i = 0; i < Counters.Length; i++)
            {
                Interlocked.Exchange(ref Counters[i], 0L);
            }

            lock (Gate)
            {
                Array.Clear(Measurements, 0, Measurements.Length);
                _measurementNext = 0;
                _measurementCount = 0;
            }

            // A Reset means the window just read is closed: everything logged
            // for it has to be on disk before the next window starts.
            Flush();
        }

        // Safe to call at any time and from any thread, including when logging
        // was never turned on (there is no writer then).
        internal static void Flush()
        {
            lock (Gate)
            {
                try
                {
                    _writer?.Flush();
                }
                catch
                {
                    // Best-effort: never let a logging IO failure surface.
                }
            }
        }

        public static void StopFileLoggingForPackageRemoval()
        {
            lock (Gate)
            {
                _isEnabled = false;
                _writer?.Dispose();
                _writer = null;
            }
        }

        public static void Log(string message)
        {
            if (!IsEnabled)
            {
                return;
            }

            Console.WriteLine(message);

            lock (Gate)
            {
                if (!IsEnabled)
                {
                    return;
                }
                try
                {
                    if (_writer == null)
                    {
                        var stream = new FileStream(
                            FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                        _writer = new StreamWriter(stream) { AutoFlush = false };
                    }

                    // Buffered: a measured path can emit thousands of lines per
                    // user action and a flush per line turned the log itself
                    // into the slowest thing on it. Reset, disable and domain
                    // unload push the buffer out.
                    _writer.WriteLine(message);
                }
                catch
                {
                    // Best-effort: never let a logging IO failure surface.
                }
            }

            if (Thread.CurrentThread.ManagedThreadId == MainThreadId)
            {
                Sink?.Invoke(message);
            }
        }

        private static void RecordMeasurement(
            string operation,
            ZepPerfStage stage,
            long startedAt)
        {
            var elapsedMilliseconds =
                (Stopwatch.GetTimestamp() - startedAt) * 1000d / Stopwatch.Frequency;
            var measurement = new ZepPerfMeasurement(
                operation,
                stage,
                elapsedMilliseconds);
            lock (Gate)
            {
                Measurements[_measurementNext] = measurement;
                _measurementNext = (_measurementNext + 1) % MeasurementCapacity;
                if (_measurementCount < MeasurementCapacity)
                {
                    _measurementCount++;
                }
            }

            Log(
                "[Zep/perf-v2] operation=" + measurement.Operation
                + " stage=" + measurement.Stage
                + " ms=" + measurement.ElapsedMilliseconds.ToString("F3"));
        }

        public readonly struct MeasurementScope : IDisposable
        {
            private readonly string _operation;
            private readonly ZepPerfStage _stage;
            private readonly long _startedAt;

            internal MeasurementScope(
                string operation,
                ZepPerfStage stage,
                long startedAt)
            {
                _operation = operation;
                _stage = stage;
                _startedAt = startedAt;
            }

            public void Dispose()
            {
                if (_startedAt != 0L && IsEnabled)
                {
                    RecordMeasurement(_operation, _stage, _startedAt);
                }
            }
        }
    }
}
