#region Using declarations
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using DuckDB.NET.Data;
// Plexus bus integration — uses IPlexusService.RegisterMethod (v0.7+) to expose
// historical.* RPCs over the bus without modifying PlexusAddOn/PlexusNTBridge.
using Plexus;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    /// <summary>
    /// Exports OHLC history by scanning ALL contract months
    /// and merging them into continuous data.
    ///
    /// Features:
    ///   - LOCAL DATA ONLY - never triggers downloads from data provider
    ///   - Optional date range (From/To) - filters output to specified range
    ///   - Supports Minute, Tick, and Day timeframes
    ///   - Export to CSV and/or DuckDB database
    ///   - DuckDB: Merge mode (default) adds data without deleting existing
    ///   - DuckDB: Replace mode option to delete and re-export all data
    ///   - "Check Existing DB Data" button to see what's already in database
    ///   - Use Historical Data Manager to download data before exporting
    ///
    /// CSV Output: Up to 3 files with Unix timestamps (milliseconds)
    ///   - {Symbol}_Last_OHLC_{timestamp}.csv (trade prices)
    ///   - {Symbol}_Bid_OHLC_{timestamp}.csv (bid prices, if available)
    ///   - {Symbol}_Ask_OHLC_{timestamp}.csv (ask prices, if available)
    ///
    /// DuckDB Output: Single database with 3 tables
    ///   - ticks: tick data with bid/ask/last
    ///   - minutes: minute data with bid/ask/last
    ///   - days: daily data with bid/ask/last
    ///
    /// DuckDB Setup: Place DuckDB.NET.Data.dll, DuckDB.NET.Bindings.dll,
    /// and duckdb.dll (native x64) in NinjaTrader's bin folder.
    ///
    /// Note: Bid/Ask data availability depends on your data provider.
    /// Some providers only supply Last (trade) data.
    ///
    /// Version: 1.7
    /// Last Updated: 2025-01-23
    /// </summary>
    public class ExportOHLCAddOn : AddOnBase
    {
        private NTMenuItem menuItem;
        private NTMenuItem existingMenu;

        // Plexus bus RPC registration tokens. Filled on State.Active when
        // PlexusAddOn is available; disposed on State.Terminated.
        // We try registration but never fail the AddOn if PlexusAddOn is
        // not deployed — the menu-driven export still works standalone.
        private readonly List<IDisposable> _plexusRpcTokens = new List<IDisposable>();

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Export ALL OHLC history from all contracts; exposes historical.* over Plexus bus";
                Name = "ExportOHLC";
            }
            else if (State == State.Active)
            {
                // Register the historical.* RPCs on the bus. If PlexusAddOn
                // isn't loaded, RegisterMethod returns a no-op disposable —
                // safe to ignore. AddOn-load order doesn't matter because
                // PlexusServiceImpl queues registrations and re-applies on
                // every CreateBusClient/RestartBus.
                TryRegisterPlexusRpcs();
            }
            else if (State == State.Terminated)
            {
                foreach (var t in _plexusRpcTokens) { try { t.Dispose(); } catch { } }
                _plexusRpcTokens.Clear();
            }
        }

        private void TryRegisterPlexusRpcs()
        {
            try
            {
                // PlexusService.WhenAvailable handles AddOn load-order:
                //   - If PlexusAddOn already registered (rare for us since 'E' < 'P'),
                //     callback fires immediately.
                //   - If not (typical), callback queues until PlexusAddOn's
                //     OnStateChange(Active) calls PlexusService.Register.
                PlexusService.WhenAvailable(svc =>
                {
                    _plexusRpcTokens.Add(svc.RegisterMethod("historical.list_instruments",  HandleListInstrumentsRpc));
                    _plexusRpcTokens.Add(svc.RegisterMethod("historical.check_inventory",   HandleCheckInventoryRpc));
                    _plexusRpcTokens.Add(svc.RegisterMethod("historical.fetch_bars",        HandleFetchBarsRpc));
                    _plexusRpcTokens.Add(svc.RegisterMethod("historical.list_periods",      HandleListPeriodsRpc));
                    _plexusRpcTokens.Add(svc.RegisterMethod("historical.download",          HandleDownloadRpc));
                    _plexusRpcTokens.Add(svc.RegisterMethod("historical.download_status",   HandleDownloadStatusRpc));
                    _plexusRpcTokens.Add(svc.RegisterMethod("historical.download_cancel",   HandleDownloadCancelRpc));
                    _plexusRpcTokens.Add(svc.RegisterMethod("historical.list_downloads",    HandleListDownloadsRpc));
                    // AddOnBase.Log(message, LogLevel) -- NinjaScript base class signature requires LogLevel.
                    Log("[plexus] Registered historical.* RPCs (list_instruments, check_inventory, fetch_bars, list_periods, download, download_status, download_cancel, list_downloads).", LogLevel.Information);
                });
            }
            catch (Exception ex)
            {
                // PlexusBridge.dll may not be loadable (TypeLoadException) if it's
                // missing from NT's Custom dir.
                Log($"[plexus] RPC registration setup failed (PlexusBridge.dll may be missing): {ex.Message}", LogLevel.Warning);
            }
        }

        // ===================================================================
        // Plexus bus RPC handlers
        // ===================================================================
        // Common conventions for these handlers:
        //  * payloadObj is the deserialized JSON request body — always a
        //    Dictionary<string, object?> in practice.
        //  * Return a Dictionary<string, object?> (becomes the JSON response).
        //  * On error: throw — BusClient wraps the exception into a
        //    method_response error envelope for the caller.
        //  * All bar pulls use MergePolicy.DoNotMerge — LOCAL CACHE ONLY,
        //    never triggers provider downloads. Same safety as the menu UI.

        /// <summary>
        /// historical.list_instruments(symbol_root="MNQ") -> [{instrument, expiry, exchange, ...}]
        /// Enumerates Instrument.All filtered to symbol_root. Same scan the
        /// menu-driven export uses (line 511-525 in this file).
        /// </summary>
        private object HandleListInstrumentsRpc(object payloadObj)
        {
            var payload = payloadObj as IDictionary<string, object> ?? new Dictionary<string, object>();
            string symbolRoot = ReadString(payload, "symbol_root", required: true);

            var contracts = new List<Instrument>();
            foreach (var inst in Instrument.All)
            {
                if (inst.MasterInstrument != null &&
                    inst.MasterInstrument.Name == symbolRoot &&
                    inst.MasterInstrument.InstrumentType == InstrumentType.Future)
                {
                    contracts.Add(inst);
                }
            }
            contracts = contracts.OrderBy(c => c.Expiry).ToList();

            var result = new List<object>();
            foreach (var c in contracts)
            {
                result.Add(new Dictionary<string, object>
                {
                    ["full_name"]  = c.FullName,
                    ["expiry_iso"] = c.Expiry.ToString("yyyy-MM-dd"),
                    ["exchange"]   = c.Exchange.ToString(),
                });
            }
            return new Dictionary<string, object>
            {
                ["symbol_root"] = symbolRoot,
                ["count"]       = contracts.Count,
                ["instruments"] = result,
            };
        }

        /// <summary>
        /// historical.list_periods() -> {supported: [...]}
        /// Convenience for clients to discover what `period_type` values
        /// the other RPCs accept.
        /// </summary>
        private object HandleListPeriodsRpc(object payloadObj)
        {
            return new Dictionary<string, object>
            {
                ["supported"] = new List<string> { "Tick", "Second", "Minute", "Day" },
                ["market_data_types"] = new List<string> { "Last", "Bid", "Ask" },
                ["note"] = "For hourly bars: use period_type=Minute with period_value=60. NT has no native Hour type.",
                ["merge_policy"] = "DoNotMerge (local cache only — never triggers provider downloads)",
            };
        }

        /// <summary>
        /// historical.check_inventory(symbol_root, period_type, period_value=1, market_data_type="Last")
        ///   -> per-contract date ranges + bar counts of LOCAL cache.
        /// Issues a small BarsRequest per contract to measure what's there
        /// without actually downloading the full series.
        /// </summary>
        private object HandleCheckInventoryRpc(object payloadObj)
        {
            var payload = payloadObj as IDictionary<string, object> ?? new Dictionary<string, object>();
            string symbolRoot = ReadString(payload, "symbol_root", required: true);
            string periodTypeStr = ReadString(payload, "period_type", required: false) ?? "Minute";
            int periodValue = ReadInt(payload, "period_value", defaultValue: 1);
            string mdTypeStr = ReadString(payload, "market_data_type", required: false) ?? "Last";

            BarsPeriodType periodType = ParsePeriodType(periodTypeStr);
            MarketDataType mdType = ParseMarketDataType(mdTypeStr);

            var contracts = Instrument.All.Where(i =>
                i.MasterInstrument != null &&
                i.MasterInstrument.Name == symbolRoot &&
                i.MasterInstrument.InstrumentType == InstrumentType.Future)
                .OrderBy(c => c.Expiry)
                .ToList();

            var inventory = new List<object>();
            // Probe a wide window (1990 -> now) with DoNotMerge so NT returns
            // only what's cached locally — fast and cost-free.
            DateTime probeStart = new DateTime(1990, 1, 1);
            DateTime probeEnd   = DateTime.Now;
            foreach (var c in contracts)
            {
                // GetOHLCFromContract is internal static on ExportOHLCWindow
                // (so both the menu-driven export AND these RPC handlers can
                //  call the same battle-tested BarsRequest path).
                var bars = ExportOHLCWindow.GetOHLCFromContract(c, periodType, periodValue, mdType, probeStart, probeEnd, out _);
                var entry = new Dictionary<string, object>
                {
                    ["full_name"]    = c.FullName,
                    ["expiry_iso"]   = c.Expiry.ToString("yyyy-MM-dd"),
                    ["bar_count"]    = bars.Count,
                };
                if (bars.Count > 0)
                {
                    entry["first_bar_iso"] = bars[0].Time.ToUniversalTime().ToString("o");
                    entry["last_bar_iso"]  = bars[bars.Count - 1].Time.ToUniversalTime().ToString("o");
                }
                inventory.Add(entry);
            }
            return new Dictionary<string, object>
            {
                ["symbol_root"]      = symbolRoot,
                ["period_type"]      = periodTypeStr,
                ["period_value"]     = periodValue,
                ["market_data_type"] = mdTypeStr,
                ["contracts"]        = inventory,
            };
        }

        /// <summary>
        /// historical.fetch_bars(instrument="MNQ 06-26", period_type, period_value, from_iso, to_iso, market_data_type)
        ///   -> bars in [from_iso, to_iso] for that one contract.
        ///
        /// Per-call size limit: enforced to keep responses bus-safe. Caller
        /// paginates by walking from_iso forward.
        ///   - Tick:  max 1 day per call (can be hundreds of MB)
        ///   - Sec:   max 1 day per call
        ///   - Min:   max 31 days per call
        ///   - Hour/Day: max 5 years per call
        /// </summary>
        private object HandleFetchBarsRpc(object payloadObj)
        {
            var payload = payloadObj as IDictionary<string, object> ?? new Dictionary<string, object>();
            string instrumentFullName = ReadString(payload, "instrument", required: true);
            string periodTypeStr = ReadString(payload, "period_type", required: true);
            int periodValue = ReadInt(payload, "period_value", defaultValue: 1);
            string fromIso = ReadString(payload, "from_iso", required: true);
            string toIso   = ReadString(payload, "to_iso",   required: true);
            string mdTypeStr = ReadString(payload, "market_data_type", required: false) ?? "Last";

            DateTime fromDt = DateTime.Parse(fromIso).ToUniversalTime();
            DateTime toDt   = DateTime.Parse(toIso).ToUniversalTime();
            if (toDt <= fromDt) throw new ArgumentException("to_iso must be > from_iso");

            BarsPeriodType periodType = ParsePeriodType(periodTypeStr);
            MarketDataType mdType = ParseMarketDataType(mdTypeStr);

            // Per-call size guard. Minute-with-value-60 (= hourly bars) sized
            // generously since 1 hour = 60 min ≈ 17K bars/year, well within limits.
            var span = toDt - fromDt;
            int maxDays;
            if (periodType == BarsPeriodType.Tick)        maxDays = 1;
            else if (periodType == BarsPeriodType.Second) maxDays = 1;
            else if (periodType == BarsPeriodType.Minute && periodValue >= 60) maxDays = 365 * 5;  // hourly+
            else if (periodType == BarsPeriodType.Minute) maxDays = 31;
            else if (periodType == BarsPeriodType.Day)    maxDays = 365 * 50;
            else                                          maxDays = 31;
            if (span.TotalDays > maxDays)
                throw new ArgumentException(
                    $"Requested range {span.TotalDays:F1} days exceeds per-call limit of {maxDays} days for period_type={periodTypeStr}. " +
                    $"Caller must paginate.");

            var inst = Instrument.GetInstrument(instrumentFullName);
            if (inst == null) throw new ArgumentException($"Instrument not found: {instrumentFullName}");

            // Reuse the existing battle-tested local-cache reader. Returns
            // empty list if no data; bars are already date-range-filtered.
            var bars = ExportOHLCWindow.GetOHLCFromContract(inst, periodType, periodValue, mdType, fromDt, toDt, out string diag);

            // Serialize bars to dict format (matches Plexus.Bus.Trading.Bar shape)
            var barList = new List<object>(bars.Count);
            foreach (var b in bars)
            {
                barList.Add(new Dictionary<string, object>
                {
                    ["timestamp_iso"] = b.Time.ToUniversalTime().ToString("o"),
                    ["open"]   = b.Open,
                    ["high"]   = b.High,
                    ["low"]    = b.Low,
                    ["close"]  = b.Close,
                    ["volume"] = b.Volume,
                });
            }
            return new Dictionary<string, object>
            {
                ["instrument"]       = inst.FullName,
                ["period_type"]      = periodTypeStr,
                ["period_value"]     = periodValue,
                ["market_data_type"] = mdTypeStr,
                ["from_iso"]         = fromIso,
                ["to_iso"]           = toIso,
                ["bar_count"]        = bars.Count,
                ["bars"]             = barList,
                ["diagnostic"]       = diag,  // may be null
            };
        }

        // -------------------------------------------------------------------
        // Payload parsing helpers
        // -------------------------------------------------------------------

        private static string ReadString(IDictionary<string, object> p, string key, bool required)
        {
            if (!p.TryGetValue(key, out object v) || v == null)
            {
                if (required) throw new ArgumentException($"Missing required field: {key}");
                return null;
            }
            return Convert.ToString(v);
        }

        private static int ReadInt(IDictionary<string, object> p, string key, int defaultValue)
        {
            if (!p.TryGetValue(key, out object v) || v == null) return defaultValue;
            try { return Convert.ToInt32(v); }
            catch { throw new ArgumentException($"Field '{key}' must be an integer"); }
        }

        private static BarsPeriodType ParsePeriodType(string s)
        {
            switch (s)
            {
                case "Tick":   return BarsPeriodType.Tick;
                case "Second": return BarsPeriodType.Second;
                case "Minute": return BarsPeriodType.Minute;
                case "Day":    return BarsPeriodType.Day;
                default: throw new ArgumentException(
                    $"Unsupported period_type: {s}. Use Tick/Second/Minute/Day " +
                    "(hourly = Minute with period_value=60).");
            }
        }

        private static MarketDataType ParseMarketDataType(string s)
        {
            switch (s)
            {
                case "Last": return MarketDataType.Last;
                case "Bid":  return MarketDataType.Bid;
                case "Ask":  return MarketDataType.Ask;
                default: throw new ArgumentException($"Unsupported market_data_type: {s}");
            }
        }

        // ===================================================================
        // Background download jobs: chunked, cancellable, pollable
        // ===================================================================
        // historical.download triggers a background Task that walks the
        // requested [from, to] range in chunks. Per-chunk size depends on
        // period_type (Tick=1d, Sec=1d, Min=7d, Min*60+=30d, Day=1yr) so each
        // chunk completes in bounded time and we get progress visibility.
        //
        // Each chunk uses MergePolicy.MergeBackAdjusted, which means NT will
        // FETCH from the connected data provider (Continuum) if the data
        // isn't already in local cache — that's the whole point: trigger
        // new downloads, not just read what's there.
        //
        // Cancellation: each chunk checks the CancellationToken before
        // starting. NT's BarsRequest has no Cancel API, so the current
        // chunk runs to completion (or its 5-min timeout) before cancel
        // takes effect. Worst case: cancel acks within 5 min — vastly
        // better than NT HDM's "hangs forever, no status."

        private class DownloadJob
        {
            public string Id;
            public string Instrument;
            public string PeriodType;
            public int PeriodValue;
            public string MarketDataType;
            public DateTime From;
            public DateTime To;
            public List<(DateTime from, DateTime to)> Chunks;
            public System.Threading.CancellationTokenSource Cts;
            public DateTime StartedAt;
            public DateTime? FinishedAt;
            public volatile int ChunksDone;
            public volatile int BarsDownloaded;
            public DateTime? CurrentChunkFrom;
            public DateTime? CurrentChunkTo;
            public string Status = "running"; // running | complete | failed | cancelled
            public string Error;

            public int ChunksTotal => Chunks?.Count ?? 0;
            public double ElapsedSec => ((FinishedAt ?? DateTime.UtcNow) - StartedAt).TotalSeconds;
            public double? EtaSec => (ChunksDone > 0 && Status == "running")
                ? (ElapsedSec / ChunksDone) * (ChunksTotal - ChunksDone) : (double?)null;
        }

        // Static so jobs survive AddOn instance churn within the NT process.
        // ConcurrentDictionary is thread-safe for the read paths used by status polling.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DownloadJob>
            _activeDownloads = new System.Collections.Concurrent.ConcurrentDictionary<string, DownloadJob>();

        // Per-period chunk size (days) — chosen to keep each chunk's BarsRequest
        // under ~5 min wall time and bound memory per chunk.
        private static int ChunkSizeDays(BarsPeriodType periodType, int periodValue)
        {
            if (periodType == BarsPeriodType.Tick)   return 1;
            if (periodType == BarsPeriodType.Second) return 1;
            if (periodType == BarsPeriodType.Day)    return 365;
            // Minute: 7 days for sub-hour, 30 for >=hourly
            if (periodType == BarsPeriodType.Minute) return periodValue >= 60 ? 30 : 7;
            return 7;
        }

        private static List<(DateTime from, DateTime to)> ChunkRange(DateTime from, DateTime to, int chunkDays)
        {
            var chunks = new List<(DateTime from, DateTime to)>();
            DateTime cur = from;
            while (cur < to)
            {
                DateTime next = cur.AddDays(chunkDays);
                if (next > to) next = to;
                chunks.Add((cur, next));
                cur = next;
            }
            return chunks;
        }

        /// <summary>
        /// historical.download(instrument, period_type, period_value, from_iso, to_iso, market_data_type)
        ///   -> {ok, download_id, chunks_total, estimated_time_sec}
        ///
        /// Starts a background download. Returns IMMEDIATELY with a job id.
        /// Poll historical.download_status(id) for progress.
        /// Cancel via historical.download_cancel(id).
        /// </summary>
        private object HandleDownloadRpc(object payloadObj)
        {
            var payload = payloadObj as IDictionary<string, object> ?? new Dictionary<string, object>();
            string instrumentName = ReadString(payload, "instrument", required: true);
            string periodTypeStr = ReadString(payload, "period_type", required: true);
            int periodValue = ReadInt(payload, "period_value", defaultValue: 1);
            string fromIso = ReadString(payload, "from_iso", required: true);
            string toIso = ReadString(payload, "to_iso", required: true);
            string mdTypeStr = ReadString(payload, "market_data_type", required: false) ?? "Last";

            var inst = Instrument.GetInstrument(instrumentName);
            if (inst == null) throw new ArgumentException($"Instrument not found: {instrumentName}");

            DateTime fromDt = DateTime.Parse(fromIso).ToUniversalTime();
            DateTime toDt   = DateTime.Parse(toIso).ToUniversalTime();
            if (toDt <= fromDt) throw new ArgumentException("to_iso must be > from_iso");

            BarsPeriodType periodType = ParsePeriodType(periodTypeStr);
            MarketDataType mdType = ParseMarketDataType(mdTypeStr);

            int chunkDays = ChunkSizeDays(periodType, periodValue);
            var chunks = ChunkRange(fromDt, toDt, chunkDays);

            string id = Guid.NewGuid().ToString("N").Substring(0, 8);
            var job = new DownloadJob
            {
                Id = id,
                Instrument = inst.FullName,
                PeriodType = periodTypeStr,
                PeriodValue = periodValue,
                MarketDataType = mdTypeStr,
                From = fromDt, To = toDt,
                Chunks = chunks,
                Cts = new System.Threading.CancellationTokenSource(),
                StartedAt = DateTime.UtcNow,
            };
            _activeDownloads[id] = job;

            // Background task — fire-and-forget. Result tracked in job state.
            System.Threading.Tasks.Task.Run(() => RunDownloadJob(job, inst, periodType, mdType));

            // Conservative ETA: 30s/chunk for tick, 10s/chunk for higher periods.
            double secPerChunk = periodType == BarsPeriodType.Tick ? 30 : 10;
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["download_id"] = id,
                ["instrument"] = inst.FullName,
                ["period_type"] = periodTypeStr,
                ["period_value"] = periodValue,
                ["market_data_type"] = mdTypeStr,
                ["from_iso"] = fromDt.ToString("o"),
                ["to_iso"] = toDt.ToString("o"),
                ["chunks_total"] = chunks.Count,
                ["chunk_size_days"] = chunkDays,
                ["estimated_time_sec"] = chunks.Count * secPerChunk,
                ["message"] = "Download started. Poll historical.download_status with this id, or cancel with historical.download_cancel.",
            };
        }

        // Runs in a background Task. Walks chunks, updating job state.
        private void RunDownloadJob(DownloadJob job, Instrument inst, BarsPeriodType periodType, MarketDataType mdType)
        {
            try
            {
                for (int i = 0; i < job.Chunks.Count; i++)
                {
                    if (job.Cts.IsCancellationRequested)
                    {
                        job.Status = "cancelled";
                        job.FinishedAt = DateTime.UtcNow;
                        return;
                    }
                    var (cFrom, cTo) = job.Chunks[i];
                    job.CurrentChunkFrom = cFrom;
                    job.CurrentChunkTo = cTo;

                    int chunkBars = DownloadChunk(inst, periodType, job.PeriodValue, mdType, cFrom, cTo);
                    job.BarsDownloaded += chunkBars;
                    job.ChunksDone++;
                }
                job.Status = "complete";
                job.FinishedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                job.Status = "failed";
                job.Error = ex.Message;
                job.FinishedAt = DateTime.UtcNow;
            }
        }

        // Single chunk: BarsRequest with MergeBackAdjusted (fetches from
        // Continuum if missing locally). Returns the bar count for progress.
        private int DownloadChunk(Instrument inst, BarsPeriodType periodType, int periodValue,
                                  MarketDataType mdType, DateTime fromDt, DateTime toDt)
        {
            int count = 0;
            var request = new BarsRequest(inst, fromDt, toDt);
            request.BarsPeriod = new BarsPeriod
            {
                BarsPeriodType = periodType,
                Value = periodValue,
                MarketDataType = mdType,
            };
            request.TradingHours = TradingHours.Get("Default 24 x 7");
            // KEY difference from GetOHLCFromContract: MergeBackAdjusted
            // means NT will request missing data from the provider
            // (Continuum). DoNotMerge would only read what's already cached.
            request.MergePolicy = MergePolicy.MergeBackAdjusted;

            var wait = new System.Threading.ManualResetEvent(false);
            request.Request((req, err, msg) =>
            {
                if (err == ErrorCode.NoError && req.Bars != null)
                    count = req.Bars.Count;
                wait.Set();
            });
            // 5-min hard cap per chunk; cancel checked between chunks
            wait.WaitOne(TimeSpan.FromMinutes(5));
            return count;
        }

        /// <summary>
        /// historical.download_status(download_id) -> full job state
        /// </summary>
        private object HandleDownloadStatusRpc(object payloadObj)
        {
            var payload = payloadObj as IDictionary<string, object> ?? new Dictionary<string, object>();
            string id = ReadString(payload, "download_id", required: true);
            if (!_activeDownloads.TryGetValue(id, out var job))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = $"unknown download_id: {id}" };
            return JobToDict(job);
        }

        /// <summary>
        /// historical.download_cancel(download_id) -> {ok, message}
        /// Cancels the job. Current chunk continues to completion (~max 5 min).
        /// </summary>
        private object HandleDownloadCancelRpc(object payloadObj)
        {
            var payload = payloadObj as IDictionary<string, object> ?? new Dictionary<string, object>();
            string id = ReadString(payload, "download_id", required: true);
            if (!_activeDownloads.TryGetValue(id, out var job))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = $"unknown download_id: {id}" };
            if (job.Status != "running")
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["download_id"] = id,
                    ["message"] = $"Job already in terminal state: {job.Status}",
                };
            job.Cts.Cancel();
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["download_id"] = id,
                ["message"] = "Cancellation requested. Current chunk completes first (up to 5 min); then job marks cancelled.",
            };
        }

        /// <summary>
        /// historical.list_downloads() -> {jobs: [...]}
        /// All active + recent jobs (in-memory; lost on NT restart).
        /// </summary>
        private object HandleListDownloadsRpc(object payloadObj)
        {
            var jobs = new List<object>();
            foreach (var kv in _activeDownloads)
            {
                jobs.Add(JobToDict(kv.Value));
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["count"] = jobs.Count,
                ["jobs"] = jobs,
            };
        }

        private static object JobToDict(DownloadJob job)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["download_id"] = job.Id,
                ["instrument"] = job.Instrument,
                ["period_type"] = job.PeriodType,
                ["period_value"] = job.PeriodValue,
                ["market_data_type"] = job.MarketDataType,
                ["from_iso"] = job.From.ToString("o"),
                ["to_iso"] = job.To.ToString("o"),
                ["status"] = job.Status,
                ["chunks_done"] = job.ChunksDone,
                ["chunks_total"] = job.ChunksTotal,
                ["bars_downloaded"] = job.BarsDownloaded,
                ["current_chunk_from"] = job.CurrentChunkFrom?.ToString("o"),
                ["current_chunk_to"] = job.CurrentChunkTo?.ToString("o"),
                ["elapsed_sec"] = Math.Round(job.ElapsedSec, 1),
                ["eta_sec"] = job.EtaSec.HasValue ? (object)Math.Round(job.EtaSec.Value, 1) : null,
                ["started_at_iso"] = job.StartedAt.ToString("o"),
                ["finished_at_iso"] = job.FinishedAt?.ToString("o"),
                ["error"] = job.Error,
            };
        }

        // This is the correct way to add menu items in NinjaTrader 8
        protected override void OnWindowCreated(Window window)
        {
            // Only add to the Control Center window (check by type name)
            if (window.GetType().Name != "ControlCenter")
                return;

            // Find the "New" menu using FindFirst extension method
            existingMenu = window.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
            if (existingMenu == null)
            {
                // Try Tools menu as fallback
                existingMenu = window.FindFirst("ControlCenterMenuItemTools") as NTMenuItem;
            }

            if (existingMenu == null)
                return;

            // Create our menu item
            menuItem = new NTMenuItem
            {
                Header = "Export OHLC History (All Contracts)",
                Style = Application.Current.TryFindResource("MainMenuItem") as Style
            };
            menuItem.Click += OnMenuClick;

            // Add to the menu
            existingMenu.Items.Add(menuItem);
        }

        protected override void OnWindowDestroyed(Window window)
        {
            if (menuItem != null && window.GetType().Name == "ControlCenter")
            {
                if (existingMenu != null && existingMenu.Items.Contains(menuItem))
                    existingMenu.Items.Remove(menuItem);
            }
        }

        private void OnMenuClick(object sender, RoutedEventArgs e)
        {
            Core.Globals.RandomDispatcher.BeginInvoke(new Action(() => new ExportOHLCWindow().Show()));
        }
    }

    public class ExportOHLCWindow : NTWindow
    {
        private TextBox symbolBox;
        private CheckBox tickCheck;
        private CheckBox minuteCheck;
        private CheckBox dayCheck;
        private TextBox fromDateBox;
        private TextBox toDateBox;
        private CheckBox exportCsvCheck;
        private CheckBox exportDuckDbCheck;
        private CheckBox replaceExistingDataCheck;
        private TextBox outputBox;
        private Button exportBtn;
        private Button checkDbBtn;
        private TextBlock statusBlock;
        private ScrollViewer statusScroll;
        private ProgressBar progressBar;
        private bool running;

        public ExportOHLCWindow()
        {
            Title = "Export OHLC - All Contracts";
            Width = 480;
            Height = 580;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var stack = new StackPanel { Margin = new Thickness(15) };

            // Title
            stack.Children.Add(new TextBlock
            {
                Text = "Export OHLC History",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            });

            // Symbol input
            stack.Children.Add(new TextBlock { Text = "Symbol Root:", FontWeight = FontWeights.SemiBold });
            symbolBox = new TextBox { Text = "MYM", Margin = new Thickness(0, 3, 0, 10), Padding = new Thickness(6), FontSize = 13 };
            stack.Children.Add(symbolBox);

            // Data Types (checkboxes)
            stack.Children.Add(new TextBlock { Text = "Data Types:", FontWeight = FontWeights.SemiBold });
            var dataStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 10) };
            tickCheck = new CheckBox { Content = "Tick", IsChecked = false, Margin = new Thickness(0, 0, 20, 0), Foreground = Brushes.LightGray };
            minuteCheck = new CheckBox { Content = "1 Minute", IsChecked = true, Margin = new Thickness(0, 0, 20, 0), Foreground = Brushes.LightGray };
            dayCheck = new CheckBox { Content = "Day", IsChecked = false, Foreground = Brushes.LightGray };
            dataStack.Children.Add(tickCheck);
            dataStack.Children.Add(minuteCheck);
            dataStack.Children.Add(dayCheck);
            stack.Children.Add(dataStack);

            // Date Range
            stack.Children.Add(new TextBlock { Text = "Date Range (optional, blank = all local data):", FontWeight = FontWeights.SemiBold });
            var dateStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 10) };
            dateStack.Children.Add(new TextBlock { Text = "From:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            fromDateBox = new TextBox { Width = 90, Padding = new Thickness(4), Text = "", Margin = new Thickness(0, 0, 15, 0) };
            dateStack.Children.Add(fromDateBox);
            dateStack.Children.Add(new TextBlock { Text = "To:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            toDateBox = new TextBox { Width = 90, Padding = new Thickness(4), Text = "" };
            dateStack.Children.Add(toDateBox);
            stack.Children.Add(dateStack);
            stack.Children.Add(new TextBlock { Text = "Format: YYYY-MM-DD (e.g. 2024-01-15)", Foreground = Brushes.Gray, FontSize = 9, Margin = new Thickness(0, 0, 0, 10) });

            // Info text about data source
            stack.Children.Add(new TextBlock
            {
                Text = "Note: Only exports data already in NinjaTrader's local cache.\nUse Historical Data Manager to download data first.",
                Foreground = Brushes.Gray,
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            });

            // Export Format
            stack.Children.Add(new TextBlock { Text = "Export Format:", FontWeight = FontWeights.SemiBold });
            var formatStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            exportCsvCheck = new CheckBox { Content = "CSV", IsChecked = true, Margin = new Thickness(0, 0, 20, 0), Foreground = Brushes.LightGray };
            formatStack.Children.Add(exportCsvCheck);
            exportDuckDbCheck = new CheckBox { Content = "DuckDB", IsChecked = false, Foreground = Brushes.LightGray };
            formatStack.Children.Add(exportDuckDbCheck);
            stack.Children.Add(formatStack);

            // DuckDB options
            replaceExistingDataCheck = new CheckBox
            {
                Content = "Replace existing data (default: merge/add new data)",
                IsChecked = false,
                Margin = new Thickness(0, 5, 0, 5),
                Foreground = Brushes.Gray,
                FontSize = 11
            };
            stack.Children.Add(replaceExistingDataCheck);

            // Check DB button
            checkDbBtn = new Button
            {
                Content = "Check Existing DB Data",
                FontSize = 11,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 0, 10),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            checkDbBtn.Click += OnCheckDbClick;
            stack.Children.Add(checkDbBtn);

            // Output folder
            stack.Children.Add(new TextBlock { Text = "Output Folder:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
            outputBox = new TextBox
            {
                Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader_OHLC"),
                Margin = new Thickness(0, 3, 0, 15),
                Padding = new Thickness(6)
            };
            stack.Children.Add(outputBox);

            // Export button
            exportBtn = new Button
            {
                Content = "EXPORT",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(15, 10, 15, 10),
                Background = new SolidColorBrush(Color.FromRgb(0, 130, 80)),
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            exportBtn.Click += OnExportClick;
            stack.Children.Add(exportBtn);

            // Progress
            progressBar = new ProgressBar { Height = 6, Margin = new Thickness(0, 10, 0, 5), Visibility = Visibility.Collapsed };
            stack.Children.Add(progressBar);

            // Status log
            statusScroll = new ScrollViewer
            {
                Height = 120,
                Background = new SolidColorBrush(Color.FromRgb(25, 25, 30)),
                Padding = new Thickness(8),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            statusBlock = new TextBlock
            {
                Text = "Ready.",
                Foreground = Brushes.LightGreen,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap
            };
            statusScroll.Content = statusBlock;
            stack.Children.Add(statusScroll);

            scroll.Content = stack;
            Content = scroll;
        }

        private void OnExportClick(object sender, RoutedEventArgs e)
        {
            if (running) return;

            string symbolRoot = symbolBox.Text.Trim().ToUpper();
            if (string.IsNullOrEmpty(symbolRoot)) { Log("Enter a symbol root", true); return; }

            // Check at least one data type selected
            bool doTick = tickCheck.IsChecked == true;
            bool doMinute = minuteCheck.IsChecked == true;
            bool doDay = dayCheck.IsChecked == true;

            if (!doTick && !doMinute && !doDay)
            {
                Log("Select at least one data type (Tick, Minute, or Day)", true);
                return;
            }

            // Parse dates
            DateTime? fromDate = null;
            DateTime? toDate = null;

            string fromStr = fromDateBox.Text.Trim();
            string toStr = toDateBox.Text.Trim();

            if (!string.IsNullOrEmpty(fromStr))
            {
                if (DateTime.TryParse(fromStr, out DateTime fd))
                    fromDate = fd;
                else
                {
                    Log("Invalid From date. Use YYYY-MM-DD format.", true);
                    return;
                }
            }

            if (!string.IsNullOrEmpty(toStr))
            {
                if (DateTime.TryParse(toStr, out DateTime td))
                    toDate = td;
                else
                {
                    Log("Invalid To date. Use YYYY-MM-DD format.", true);
                    return;
                }
            }

            bool exportCsv = exportCsvCheck.IsChecked == true;
            bool exportDuckDb = exportDuckDbCheck.IsChecked == true;
            bool replaceExisting = replaceExistingDataCheck.IsChecked == true;

            if (!exportCsv && !exportDuckDb)
            {
                Log("Select at least one export format (CSV or DuckDB)", true);
                return;
            }

            string outDir = outputBox.Text.Trim();

            running = true;
            exportBtn.IsEnabled = false;
            progressBar.Visibility = Visibility.Visible;
            progressBar.IsIndeterminate = true;
            statusBlock.Text = "";

            // Run export for each selected data type
            ThreadPool.QueueUserWorkItem(_ => RunExportAll(symbolRoot, outDir, fromDate, toDate, exportCsv, exportDuckDb, replaceExisting, doTick, doMinute, doDay));
        }

        private void OnCheckDbClick(object sender, RoutedEventArgs e)
        {
            string symbolRoot = symbolBox.Text.Trim().ToUpper();
            if (string.IsNullOrEmpty(symbolRoot))
            {
                Log("Enter a symbol root to check", true);
                return;
            }

            string outDir = outputBox.Text.Trim();
            string symbolDir = Path.Combine(outDir, symbolRoot);
            string dbPath = Path.Combine(symbolDir, $"{symbolRoot}.db");

            if (!File.Exists(dbPath))
            {
                Log($"No database found at: {dbPath}");
                return;
            }

            statusBlock.Text = "";
            Log($"Checking database: {dbPath}\n");

            ThreadPool.QueueUserWorkItem(_ => CheckExistingDbData(dbPath, symbolRoot));
        }

        private void CheckExistingDbData(string dbPath, string symbolRoot)
        {
            DuckDBConnection connection = null;
            try
            {
                connection = new DuckDBConnection($"Data Source={dbPath}");
                connection.Open();

                string[] tables = { "ticks", "minutes", "days" };

                foreach (var table in tables)
                {
                    try
                    {
                        var cmd = connection.CreateCommand();
                        cmd.CommandText = $@"
                            SELECT
                                price_type,
                                COUNT(*) as bar_count,
                                MIN(timestamp) as min_date,
                                MAX(timestamp) as max_date
                            FROM {table}
                            WHERE symbol = '{symbolRoot}'
                            GROUP BY price_type
                            ORDER BY price_type";

                        using (var reader = cmd.ExecuteReader())
                        {
                            bool hasData = false;
                            while (reader.Read())
                            {
                                if (!hasData)
                                {
                                    Log($"═══ {table.ToUpper()} ═══");
                                    hasData = true;
                                }
                                string priceType = reader.GetString(0);
                                long count = reader.GetInt64(1);
                                var minDate = reader.GetDateTime(2);
                                var maxDate = reader.GetDateTime(3);
                                Log($"  {priceType}: {count:N0} bars ({minDate:yyyy-MM-dd} to {maxDate:yyyy-MM-dd})");
                            }
                            if (!hasData)
                            {
                                Log($"═══ {table.ToUpper()} ═══");
                                Log($"  No data for {symbolRoot}");
                            }
                        }
                        cmd.Dispose();
                    }
                    catch
                    {
                        // Table doesn't exist
                        Log($"═══ {table.ToUpper()} ═══");
                        Log($"  Table not found");
                    }
                }

                Log("\n✓ Database check complete");
            }
            catch (Exception ex)
            {
                Log($"Error checking database: {ex.Message}", true);
            }
            finally
            {
                if (connection != null)
                {
                    try { connection.Close(); } catch { }
                    try { connection.Dispose(); } catch { }
                }
            }
        }

        private void RunExportAll(string symbolRoot, string outDir, DateTime? fromDate, DateTime? toDate,
            bool exportCsv, bool exportDuckDb, bool replaceExisting, bool doTick, bool doMinute, bool doDay)
        {
            try
            {
                if (doTick)
                {
                    Log("═══ TICK DATA ═══");
                    RunExport(symbolRoot, 1, "Tick", outDir, fromDate, toDate, exportCsv, exportDuckDb, replaceExisting);
                }

                if (doMinute)
                {
                    Log("\n═══ MINUTE DATA ═══");
                    RunExport(symbolRoot, 1, "Minute", outDir, fromDate, toDate, exportCsv, exportDuckDb, replaceExisting);
                }

                if (doDay)
                {
                    Log("\n═══ DAY DATA ═══");
                    RunExport(symbolRoot, 1, "Day", outDir, fromDate, toDate, exportCsv, exportDuckDb, replaceExisting);
                }

                Finish();
            }
            catch (Exception ex)
            {
                Log($"\nERROR: {ex.Message}", true);
                Finish();
            }
        }

        private void RunExport(string symbolRoot, int period, string tfType, string outDir,
            DateTime? fromDate, DateTime? toDate, bool exportCsv, bool exportDuckDb, bool replaceExisting)
        {
            try
            {
                // Determine date range
                DateTime startDate, endDate;

                if (fromDate.HasValue && toDate.HasValue)
                {
                    startDate = fromDate.Value.Date;
                    endDate = toDate.Value.Date.AddDays(1).AddTicks(-1); // End of day
                    Log($"Date filter: {startDate:yyyy-MM-dd HH:mm} to {endDate:yyyy-MM-dd HH:mm}");
                }
                else if (fromDate.HasValue)
                {
                    startDate = fromDate.Value.Date;
                    endDate = DateTime.Now;
                    Log($"Date range: {startDate:yyyy-MM-dd} to now");
                }
                else if (toDate.HasValue)
                {
                    // Only To date specified - use very old start date for local data
                    startDate = new DateTime(1990, 1, 1);
                    endDate = toDate.Value.Date.AddDays(1).AddTicks(-1);
                    Log($"Date filter: earliest to {toDate.Value:yyyy-MM-dd}");
                }
                else
                {
                    // No dates specified - export all available local data
                    startDate = new DateTime(1990, 1, 1);
                    endDate = DateTime.Now;
                    Log("Exporting all available local data (no date filter)");
                }

                Log($"Source: Local cache only (use Historical Data Manager to download more)");
                Log($"\nScanning for all {symbolRoot} contracts...");

                // Find all instruments matching this symbol root
                var contracts = new List<Instrument>();

                foreach (var inst in Instrument.All)
                {
                    if (inst.MasterInstrument != null &&
                        inst.MasterInstrument.Name == symbolRoot &&
                        inst.MasterInstrument.InstrumentType == InstrumentType.Future)
                    {
                        contracts.Add(inst);
                    }
                }

                // Sort by expiry date
                contracts = contracts.OrderBy(c => c.Expiry).ToList();

                if (contracts.Count == 0)
                {
                    Log($"No contracts found for '{symbolRoot}'", true);
                    Log("Make sure you have downloaded historical data in Historical Data Manager", true);
                    return;
                }

                Log($"Found {contracts.Count} contracts:");
                foreach (var c in contracts)
                {
                    Log($"  • {c.FullName} (expires {c.Expiry:yyyy-MM-dd})");
                }

                if (!Directory.Exists(outDir))
                    Directory.CreateDirectory(outDir);

                BarsPeriodType periodType = tfType switch
                {
                    "Tick" => BarsPeriodType.Tick,
                    "Day" => BarsPeriodType.Day,
                    _ => BarsPeriodType.Minute
                };

                // Collect ALL OHLC data from ALL contracts for each MarketDataType
                var allLastBars = new List<OHLCBar>();
                var allBidBars = new List<OHLCBar>();
                var allAskBars = new List<OHLCBar>();

                int contractNum = 0;
                foreach (var contract in contracts)
                {
                    contractNum++;
                    Log($"\n[{contractNum}/{contracts.Count}] Processing {contract.FullName}...");

                    // Get Last OHLC
                    var lastBars = GetOHLCFromContract(contract, periodType, period, MarketDataType.Last, startDate, endDate, out string lastDiag);
                    if (lastBars.Count > 0)
                    {
                        Log($"  Last: {lastBars.Count:N0} bars" + (lastDiag != null ? $" ({lastDiag})" : ""));
                        allLastBars.AddRange(lastBars);
                    }

                    // Get Bid OHLC
                    var bidBars = GetOHLCFromContract(contract, periodType, period, MarketDataType.Bid, startDate, endDate, out string bidDiag);
                    if (bidBars.Count > 0)
                    {
                        Log($"  Bid: {bidBars.Count:N0} bars" + (bidDiag != null ? $" ({bidDiag})" : ""));
                        allBidBars.AddRange(bidBars);
                    }

                    // Get Ask OHLC
                    var askBars = GetOHLCFromContract(contract, periodType, period, MarketDataType.Ask, startDate, endDate, out string askDiag);
                    if (askBars.Count > 0)
                    {
                        Log($"  Ask: {askBars.Count:N0} bars" + (askDiag != null ? $" ({askDiag})" : ""));
                        allAskBars.AddRange(askBars);
                    }
                }

                if (allLastBars.Count == 0 && allBidBars.Count == 0 && allAskBars.Count == 0)
                {
                    Log("\nNo data retrieved from any contract!", true);
                    return;
                }

                // Merge and deduplicate (sort by time, keep one bar per timestamp)
                Log($"\nMerging bars...");

                var mergedLast = MergeAndDedupe(allLastBars);
                var mergedBid = MergeAndDedupe(allBidBars);
                var mergedAsk = MergeAndDedupe(allAskBars);

                Log($"After merge - Last: {mergedLast.Count:N0}, Bid: {mergedBid.Count:N0}, Ask: {mergedAsk.Count:N0}");

                // Create symbol subdirectory
                string symbolDir = Path.Combine(outDir, symbolRoot);
                if (!Directory.Exists(symbolDir))
                    Directory.CreateDirectory(symbolDir);

                int filesWritten = 0;
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                // Export to CSV if selected
                if (exportCsv)
                {
                    Log($"\n--- CSV Export ---");

                    if (mergedLast.Count > 0)
                    {
                        string lastFile = Path.Combine(symbolDir, $"{symbolRoot}_Last_OHLC_{ts}.csv");
                        WriteOHLCFile(lastFile, mergedLast);
                        Log($"✓ Last: {mergedLast.Count:N0} bars → {symbolRoot}/{Path.GetFileName(lastFile)}");
                        filesWritten++;
                    }

                    if (mergedBid.Count > 0)
                    {
                        string bidFile = Path.Combine(symbolDir, $"{symbolRoot}_Bid_OHLC_{ts}.csv");
                        WriteOHLCFile(bidFile, mergedBid);
                        Log($"✓ Bid: {mergedBid.Count:N0} bars → {symbolRoot}/{Path.GetFileName(bidFile)}");
                        filesWritten++;
                    }

                    if (mergedAsk.Count > 0)
                    {
                        string askFile = Path.Combine(symbolDir, $"{symbolRoot}_Ask_OHLC_{ts}.csv");
                        WriteOHLCFile(askFile, mergedAsk);
                        Log($"✓ Ask: {mergedAsk.Count:N0} bars → {symbolRoot}/{Path.GetFileName(askFile)}");
                        filesWritten++;
                    }
                }

                // Export to DuckDB if selected
                if (exportDuckDb)
                {
                    Log($"\n--- DuckDB Export ---");
                    ExportToDuckDb(symbolRoot, tfType, outDir, mergedLast, mergedBid, mergedAsk, replaceExisting);
                }

                // Summary - use whichever has data for date range
                var primaryBars = mergedLast.Count > 0 ? mergedLast : (mergedBid.Count > 0 ? mergedBid : mergedAsk);
                var minDate = primaryBars.Min(b => b.Time);
                var maxDate = primaryBars.Max(b => b.Time);

                Log("\n══════════════════════════════════════════");
                Log("EXPORT COMPLETE!");
                Log($"Symbol: {symbolRoot}");
                Log($"Timeframe: {period} {tfType}");
                Log($"Contracts processed: {contracts.Count}");
                if (exportCsv) Log($"CSV files created: {filesWritten}");
                if (exportDuckDb) Log($"DuckDB table: {(tfType == "Tick" ? "ticks" : tfType == "Day" ? "days" : "minutes")}");
                Log($"Date range: {minDate:yyyy-MM-dd} to {maxDate:yyyy-MM-dd}");
                Log($"Output: {symbolDir}");
                Log("══════════════════════════════════════════");
            }
            catch (Exception ex)
            {
                Log($"\nERROR: {ex.Message}", true);
            }
        }

        // Promoted to internal static so the ExportOHLCAddOn's RPC handlers
        // (historical.fetch_bars, historical.check_inventory) can call the
        // same battle-tested local-cache BarsRequest path that the menu UI uses.
        // No instance state used — only NT static APIs (Instrument, BarsRequest).
        internal static List<OHLCBar> GetOHLCFromContract(Instrument instrument, BarsPeriodType periodType, int period,
            MarketDataType mdType, DateTime startDate, DateTime endDate, out string diagnosticMsg)
        {
            var bars = new List<OHLCBar>();
            diagnosticMsg = null;
            string capturedMsg = null;

            try
            {
                // IMPORTANT: BarsRequest date range constructor
                // NinjaTrader sometimes ignores these dates and returns all cached data
                // We MUST filter results ourselves to respect the date range
                var request = new BarsRequest(instrument, startDate, endDate);
                request.BarsPeriod = new BarsPeriod
                {
                    BarsPeriodType = periodType,
                    Value = period,
                    MarketDataType = mdType
                };
                request.TradingHours = TradingHours.Get("Default 24 x 7");

                // MergePolicy controls how contracts are merged and whether to fetch from provider
                // DoNotMerge = use only local cache, do NOT request from provider
                // ALWAYS use DoNotMerge to prevent unwanted provider downloads
                // This ensures we only export what's already in NinjaTrader's local cache
                request.MergePolicy = MergePolicy.DoNotMerge;

                var wait = new ManualResetEvent(false);

                request.Request((req, err, msg) =>
                {
                    if (err == ErrorCode.NoError && req.Bars != null)
                    {
                        int totalBars = req.Bars.Count;
                        int filteredOut = 0;

                        for (int i = 0; i < req.Bars.Count; i++)
                        {
                            var barTime = req.Bars.GetTime(i);

                            // CRITICAL: Filter bars to respect the user's date range
                            // NinjaTrader may return data outside requested range
                            if (barTime >= startDate && barTime <= endDate)
                            {
                                bars.Add(new OHLCBar
                                {
                                    Time = barTime,
                                    Open = req.Bars.GetOpen(i),
                                    High = req.Bars.GetHigh(i),
                                    Low = req.Bars.GetLow(i),
                                    Close = req.Bars.GetClose(i),
                                    Volume = req.Bars.GetVolume(i)
                                });
                            }
                            else
                            {
                                filteredOut++;
                            }
                        }

                        // Log if significant filtering occurred (more than 10% filtered)
                        if (filteredOut > 0 && totalBars > 0)
                        {
                            double pctFiltered = (filteredOut * 100.0) / totalBars;
                            if (pctFiltered > 10 || filteredOut > 1000)
                            {
                                // This indicates NinjaTrader returned data outside our date range
                                // Get actual date range of returned data for diagnostics
                                var minTime = req.Bars.GetTime(0);
                                var maxTime = req.Bars.GetTime(req.Bars.Count - 1);
                                capturedMsg = $"NT returned {totalBars:N0} bars ({minTime:yyyy-MM-dd} to {maxTime:yyyy-MM-dd}), filtered to {bars.Count:N0}";
                            }
                        }
                    }
                    else if (err != ErrorCode.NoError)
                    {
                        capturedMsg = msg;
                    }
                    wait.Set();
                });

                wait.WaitOne(TimeSpan.FromMinutes(5));
                diagnosticMsg = capturedMsg;
            }
            catch { }

            return bars;
        }

        private List<OHLCBar> MergeAndDedupe(List<OHLCBar> bars)
        {
            if (bars.Count == 0) return bars;

            // Sort by time
            var sorted = bars.OrderBy(b => b.Time).ToList();

            // Dedupe: keep bar with highest volume for each timestamp
            var result = new List<OHLCBar>();
            OHLCBar prev = null;

            foreach (var bar in sorted)
            {
                if (prev == null || bar.Time != prev.Time)
                {
                    result.Add(bar);
                    prev = bar;
                }
                else if (bar.Volume > prev.Volume)
                {
                    // Same timestamp, keep higher volume (front month)
                    result[result.Count - 1] = bar;
                    prev = bar;
                }
            }

            return result;
        }

        private void WriteOHLCFile(string path, List<OHLCBar> bars)
        {
            using (var w = new StreamWriter(path, false, Encoding.UTF8))
            {
                w.WriteLine("unix_ms,open,high,low,close,volume");

                foreach (var bar in bars)
                {
                    long unixMs = (long)(bar.Time.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                    w.WriteLine($"{unixMs},{bar.Open},{bar.High},{bar.Low},{bar.Close},{bar.Volume}");
                }
            }
        }

        private void Log(string msg, bool isError = false)
        {
            Dispatcher.Invoke(() =>
            {
                statusBlock.Text += (statusBlock.Text.Length > 0 ? "\n" : "") + msg;
                statusBlock.Foreground = isError ? Brushes.OrangeRed : Brushes.LightGreen;
                statusScroll.ScrollToEnd();
            });
        }

        private void Finish()
        {
            Dispatcher.Invoke(() =>
            {
                running = false;
                exportBtn.IsEnabled = true;
                progressBar.Visibility = Visibility.Collapsed;
            });
        }

        // Made internal (was private) so AddOn-side RPC handlers can iterate
        // results from the now-static GetOHLCFromContract.
        internal class OHLCBar
        {
            public DateTime Time;
            public double Open, High, Low, Close, Volume;
        }

        private void ExportToDuckDb(string symbolRoot, string tfType, string outDir,
            List<OHLCBar> lastBars, List<OHLCBar> bidBars, List<OHLCBar> askBars, bool replaceExisting)
        {
            DuckDBConnection connection = null;

            try
            {
                // Determine table name based on timeframe
                string tableName = tfType switch
                {
                    "Tick" => "ticks",
                    "Day" => "days",
                    _ => "minutes"
                };

                // Create symbol subdirectory
                string symbolDir = Path.Combine(outDir, symbolRoot);
                if (!Directory.Exists(symbolDir))
                    Directory.CreateDirectory(symbolDir);

                string dbPath = Path.Combine(symbolDir, $"{symbolRoot}.db");
                Log($"\nExporting to DuckDB: {symbolRoot}/{Path.GetFileName(dbPath)}");
                Log($"Table: {tableName}");
                Log($"Mode: {(replaceExisting ? "REPLACE all existing data" : "MERGE with existing data")}");

                connection = new DuckDBConnection($"Data Source={dbPath}");
                connection.Open();

                // Create table if not exists
                ExecuteNonQuery(connection, $@"
                    CREATE TABLE IF NOT EXISTS {tableName} (
                        symbol      VARCHAR NOT NULL,
                        price_type  VARCHAR NOT NULL,
                        unix_ms     BIGINT NOT NULL,
                        timestamp   TIMESTAMP NOT NULL,
                        open        DOUBLE NOT NULL,
                        high        DOUBLE NOT NULL,
                        low         DOUBLE NOT NULL,
                        close       DOUBLE NOT NULL,
                        volume      BIGINT NOT NULL,
                        PRIMARY KEY (symbol, price_type, unix_ms)
                    )");

                // If replaceExisting is checked, delete all existing data for this symbol
                // Otherwise, we merge using INSERT OR REPLACE (keeps existing, updates duplicates)
                if (replaceExisting)
                {
                    Log($"  Deleting existing data for {symbolRoot}...");
                    ExecuteNonQuery(connection, $"DELETE FROM {tableName} WHERE symbol = '{symbolRoot}'");
                }

                // Insert data for each price type
                int totalInserted = 0;

                if (lastBars.Count > 0)
                {
                    int inserted = InsertOHLCData(connection, tableName, symbolRoot, "last", lastBars);
                    Log($"  Inserted {inserted:N0} 'last' bars");
                    totalInserted += inserted;
                }

                if (bidBars.Count > 0)
                {
                    int inserted = InsertOHLCData(connection, tableName, symbolRoot, "bid", bidBars);
                    Log($"  Inserted {inserted:N0} 'bid' bars");
                    totalInserted += inserted;
                }

                if (askBars.Count > 0)
                {
                    int inserted = InsertOHLCData(connection, tableName, symbolRoot, "ask", askBars);
                    Log($"  Inserted {inserted:N0} 'ask' bars");
                    totalInserted += inserted;
                }

                Log($"✓ DuckDB: {totalInserted:N0} total rows in '{tableName}' table");
            }
            catch (Exception ex)
            {
                Log($"DuckDB error: {ex.Message}", true);
                if (ex.InnerException != null)
                    Log($"  Inner: {ex.InnerException.Message}", true);
            }
            finally
            {
                // Manual cleanup
                if (connection != null)
                {
                    try { connection.Close(); } catch { }
                    try { connection.Dispose(); } catch { }
                }
            }
        }

        private void ExecuteNonQuery(DuckDBConnection connection, string sql)
        {
            var cmd = connection.CreateCommand();
            try
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            finally
            {
                try { cmd.Dispose(); } catch { }
            }
        }

        private int InsertOHLCData(DuckDBConnection connection, string tableName, string symbol, string priceType, List<OHLCBar> bars)
        {
            if (bars.Count == 0) return 0;

            int chunkSize = 1000;
            int inserted = 0;

            for (int i = 0; i < bars.Count; i += chunkSize)
            {
                var chunk = bars.Skip(i).Take(chunkSize).ToList();
                var sb = new StringBuilder();
                sb.AppendLine($"INSERT OR REPLACE INTO {tableName} (symbol, price_type, unix_ms, timestamp, open, high, low, close, volume) VALUES");

                for (int j = 0; j < chunk.Count; j++)
                {
                    var bar = chunk[j];
                    long unixMs = (long)(bar.Time.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                    string timestamp = bar.Time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");

                    if (j > 0) sb.Append(",");
                    sb.AppendLine($"('{symbol}', '{priceType}', {unixMs}, '{timestamp}', {bar.Open}, {bar.High}, {bar.Low}, {bar.Close}, {bar.Volume})");
                }

                ExecuteNonQuery(connection, sb.ToString());
                inserted += chunk.Count;
            }

            return inserted;
        }
    }
}
