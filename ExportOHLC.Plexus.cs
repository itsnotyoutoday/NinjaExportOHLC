#region Using declarations
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using DuckDB.NET.Data;
// Plexus bus integration — uses IPlexusService.RegisterMethod (v0.7+) to expose
// historical.* RPCs over the bus without modifying PlexusAddOn/PlexusNTBridge.
using Plexus;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    /// <summary>
    /// OPTIONAL Plexus-bus partial of ExportOHLCAddOn.
    ///
    /// Adds these RPCs to the Plexus bus:
    ///   historical.list_instruments  /  list_periods  /  check_inventory
    ///   historical.fetch_bars        /  probe_availability
    ///   historical.download          /  download_status / download_cancel
    ///                                /  list_downloads
    ///   historical.prepare_transfer  /  cancel_transfer / list_transfers
    ///                                /  get_transfer_ticket
    ///
    /// To ship ExportOHLC WITHOUT Plexus:
    ///   - Delete this file.
    ///   - The base ExportOHLC.cs continues to compile and run; its
    ///     partial void TryRegisterPlexusRpcs / DisposePlexusRpcs hooks
    ///     become no-ops (C# language feature: empty partial method
    ///     declarations vanish at compile time if no implementation exists).
    /// </summary>
    public partial class ExportOHLCAddOn
    {
        // Plexus bus RPC registration tokens. Filled on State.Active when
        // PlexusAddOn is available; disposed on State.Terminated.
        // We try registration but never fail the AddOn if PlexusAddOn is
        // not deployed — the menu-driven export still works standalone.
        private readonly List<IDisposable> _plexusRpcTokens = new List<IDisposable>();

        // Dispose Plexus RPC registration tokens at AddOn teardown.
        // Called from the base file's OnStateChange when State==Terminated.
        partial void DisposePlexusRpcs()
        {
            foreach (var t in _plexusRpcTokens) { try { t.Dispose(); } catch { } }
            _plexusRpcTokens.Clear();
        }

        partial void TryRegisterPlexusRpcs()
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
                    _plexusRpcTokens.Add(svc.RegisterMethod("historical.probe_availability", HandleProbeAvailabilityRpc));
                    // P2P file-transfer trio (handlers live in ExportOHLCTransfer.cs)
                    _plexusRpcTokens.Add(svc.RegisterMethod("historical.prepare_transfer", HandlePrepareTransferRpc));
                    _plexusRpcTokens.Add(svc.RegisterMethod("historical.cancel_transfer",  HandleCancelTransferRpc));
                    _plexusRpcTokens.Add(svc.RegisterMethod("historical.list_transfers",   HandleListTransfersRpc));
                    _plexusRpcTokens.Add(svc.RegisterMethod("historical.get_transfer_ticket", HandleGetTransferTicketRpc));
                    // AddOnBase.Log(message, LogLevel) -- NinjaScript base class signature requires LogLevel.
                    Log("[plexus] Registered historical.* RPCs (list_instruments, check_inventory, fetch_bars, list_periods, download/status/cancel/list_downloads, prepare_transfer/cancel_transfer/list_transfers).", LogLevel.Information);
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
            public string Status = "running"; // running | complete | failed | cancelled | stopped_no_data
            public string Error;
            // Diagnostic state from the most recent BarsRequest callback so
            // download_status can show WHY a chunk came back empty (e.g.
            // 'HistoricalDataNotAvailable' from provider). Without this we
            // can't distinguish "no trades happened" from "provider has no
            // data for this range" -- both look like 0 bars.
            public string LastChunkErrorCode;     // e.g. "NoError", "HistoricalDataNotAvailable"
            public string LastChunkErrorMessage;  // provider-supplied message if any
            public int    LastChunkBars;          // last chunk's bar count
            public int    ConsecutiveEmptyChunks; // how many in a row returned 0 bars
            // Set when auto-stop kicked in. Helpful for clients to know the
            // boundary where data dried up.
            public DateTime? FirstDataAt;         // earliest ts seen in any non-empty chunk so far
            public DateTime? LastDataAt;          // latest ts seen
            public int MaxEmptyChunks = 3;        // auto-stop threshold; overridable from RPC payload

            public int ChunksTotal => Chunks?.Count ?? 0;
            public double ElapsedSec => ((FinishedAt ?? DateTime.UtcNow) - StartedAt).TotalSeconds;
            public double? EtaSec => (ChunksDone > 0 && Status == "running")
                ? (ElapsedSec / ChunksDone) * (ChunksTotal - ChunksDone) : (double?)null;
        }

        // Static so jobs survive AddOn instance churn within the NT process.
        // ConcurrentDictionary is thread-safe for the read paths used by status polling.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DownloadJob>
            _activeDownloads = new System.Collections.Concurrent.ConcurrentDictionary<string, DownloadJob>();

        // CRITICAL: serializes BarsRequest calls across all active jobs.
        // Without this, triggering N downloads launches N parallel Task.Runs
        // each calling BarsRequest concurrently -- NT's BarsRequest pipeline
        // is not designed for high concurrency and gets choked (UI freezes,
        // provider connection congests, callbacks queue indefinitely). We
        // hit this on the 126-job MNQ+MYM bulk run: NT had to be force-killed.
        //
        // Semaphore size = 2 is a conservative default that lets ONE BarsRequest
        // be in-flight while NEXT one is queueing -- avoids the dead-time gap
        // between requests but doesn't overwhelm the provider. Tunable via the
        // historical.set_concurrency RPC if needed.
        private static readonly System.Threading.SemaphoreSlim _barsRequestGate
            = new System.Threading.SemaphoreSlim(2, 2);

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
            int maxEmptyChunks = ReadInt(payload, "max_empty_chunks", defaultValue: 3);

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
                MaxEmptyChunks = Math.Max(1, maxEmptyChunks),
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

        // ===================================================================
        // historical.probe_availability
        // ===================================================================
        //
        // Fast discovery: "how far back can I pull data for this instrument
        // at each granularity, before the provider gives me nothing?"
        //
        // Strategy: per-granularity, do a small (1-day) BarsRequest at
        // exponentially-increasing lookback dates (1d, 1wk, 1mo, 3mo, 6mo,
        // 1yr, 2yr, 5yr, 10yr ago). Each probe returns either bars or 0.
        // The boundary tells the client which granularities are usable for
        // which time depths -- without doing a single big speculative
        // download. ~10 probes per period_type × 3 period_types = ~30s.
        //
        // Uses MergeBackAdjusted so we actually trigger Continuum lookups,
        // not just cache reads. (cache-only probes would give a misleading
        // picture -- cache state != provider availability.)
        //
        // RPC input:
        //   instrument        : "MNQ 06-26" (required)
        //   market_data_type  : "Last" (default)
        //   period_types      : ["Day","Minute","Tick"] (default - all three)
        //
        // RPC output:
        //   { ok, instrument, probes: [
        //       {period_type, period_value, granularity,
        //        probes_run, first_available_iso, last_available_iso,
        //        results: [{date_iso, bars, err_code, err_message, elapsed_sec}, ...]},
        //       ...
        //   ]}

        private object HandleProbeAvailabilityRpc(object payloadObj)
        {
            var payload = payloadObj as IDictionary<string, object> ?? new Dictionary<string, object>();
            string instrumentFullName = ReadString(payload, "instrument", required: true);
            string mdTypeStr = ReadString(payload, "market_data_type", required: false) ?? "Last";
            // Optional: ["Day","Minute","Tick"] subset. Default = all three.
            // For dense symbols (MNQ-class), call this once per granularity to
            // stay under PRISM's cross-client RPC forwarding timeout.
            var periodTypesParam = payload.TryGetValue("period_types", out var ptv) ? ptv : null;

            var inst = Instrument.GetInstrument(instrumentFullName);
            if (inst == null) throw new ArgumentException($"Instrument not found: {instrumentFullName}");

            MarketDataType mdType = ParseMarketDataType(mdTypeStr);

            // Build granularity list. Filter to requested subset if given.
            var allGrans = new List<(string label, BarsPeriodType pt, int pv)>
            {
                ("Day1",     BarsPeriodType.Day,    1),
                ("Minute1",  BarsPeriodType.Minute, 1),
                ("Tick1",    BarsPeriodType.Tick,   1),
            };
            var granularities = new List<(string label, BarsPeriodType pt, int pv)>();
            if (periodTypesParam is System.Collections.IEnumerable ptList && !(periodTypesParam is string))
            {
                var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var x in ptList) requested.Add(x?.ToString() ?? "");
                foreach (var g in allGrans)
                    if (requested.Contains(g.pt.ToString()) || requested.Contains(g.label))
                        granularities.Add(g);
                if (granularities.Count == 0)
                    throw new ArgumentException($"period_types must contain at least one of Day/Minute/Tick, got: [{string.Join(",", requested)}]");
            }
            else
            {
                granularities = allGrans;
            }

            // Lookback ladder for Day/Minute: full range up to 10yr.
            // Tick: skip 5yr+ since provider retention is empirically ~1yr.
            var fullLookbacks = new List<(string label, TimeSpan ago)>
            {
                ("1d",   TimeSpan.FromDays(1)),
                ("1wk",  TimeSpan.FromDays(7)),
                ("1mo",  TimeSpan.FromDays(30)),
                ("3mo",  TimeSpan.FromDays(90)),
                ("6mo",  TimeSpan.FromDays(180)),
                ("1yr",  TimeSpan.FromDays(365)),
                ("2yr",  TimeSpan.FromDays(365 * 2)),
                ("5yr",  TimeSpan.FromDays(365 * 5)),
                ("10yr", TimeSpan.FromDays(365 * 10)),
            };
            // Trim list at lookup time per granularity (see foreach below).
            var lookbacks = fullLookbacks;

            var granResults = new List<object>();
            foreach (var g in granularities)
            {
                var results = new List<object>();
                DateTime? firstAvail = null;
                DateTime? lastAvail = null;
                int probesRun = 0;

                // Per-granularity lookback selection: Tick's max retention is
                // empirically ~1yr on Continuum (per M2K probe finding); skip
                // probes beyond 2yr for Tick to keep total probe time bounded.
                var perGranLookbacks = (g.pt == BarsPeriodType.Tick)
                    ? lookbacks.Where(lb => lb.ago <= TimeSpan.FromDays(365 * 2)).ToList()
                    : lookbacks;

                foreach (var lb in perGranLookbacks)
                {
                    // Tick: 4-hour window (still tons of data on liquid contracts,
                    //   but caps total fetch under the probe timeout for MNQ-class
                    //   density: 8.8M ticks/3day x 9 probes was blowing 120s budget).
                    // Day/Minute: 1-day window (small, fast).
                    TimeSpan probeWindow = (g.pt == BarsPeriodType.Tick)
                        ? TimeSpan.FromHours(4)
                        : TimeSpan.FromDays(1);
                    var probeEnd = DateTime.UtcNow - lb.ago;
                    var probeStart = probeEnd - probeWindow;
                    probesRun++;

                    // Acquire the BarsRequest gate so we don't compete with
                    // active downloads. Probe is fast (1-day window) so this
                    // is brief contention.
                    _barsRequestGate.Wait();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    ChunkResult res;
                    try { res = DownloadChunk(inst, g.pt, g.pv, mdType, probeStart, probeEnd); }
                    finally { _barsRequestGate.Release(); }
                    sw.Stop();

                    results.Add(new Dictionary<string, object>
                    {
                        ["lookback"]     = lb.label,
                        ["date_iso"]     = probeStart.ToString("o"),
                        ["bars"]         = res.BarsCount,
                        ["err_code"]     = res.ErrorCode,
                        ["err_message"]  = res.ErrorMessage,
                        ["elapsed_sec"]  = Math.Round(sw.Elapsed.TotalSeconds, 1),
                        ["first_bar_iso"]= res.FirstBarTs?.ToUniversalTime().ToString("o"),
                        ["last_bar_iso"] = res.LastBarTs?.ToUniversalTime().ToString("o"),
                    });

                    if (res.BarsCount > 0)
                    {
                        if (!firstAvail.HasValue || res.FirstBarTs < firstAvail) firstAvail = res.FirstBarTs;
                        if (!lastAvail.HasValue  || res.LastBarTs  > lastAvail)  lastAvail  = res.LastBarTs;
                    }
                }

                granResults.Add(new Dictionary<string, object>
                {
                    ["granularity"]          = g.label,
                    ["period_type"]          = g.pt.ToString(),
                    ["period_value"]         = g.pv,
                    ["probes_run"]           = probesRun,
                    ["first_available_iso"]  = firstAvail?.ToUniversalTime().ToString("o"),
                    ["last_available_iso"]   = lastAvail?.ToUniversalTime().ToString("o"),
                    ["results"]              = results,
                });
            }

            return new Dictionary<string, object>
            {
                ["ok"]              = true,
                ["instrument"]      = inst.FullName,
                ["market_data_type"]= mdTypeStr,
                ["probed_at_iso"]   = DateTime.UtcNow.ToString("o"),
                ["probes"]          = granResults,
                ["summary"]         = $"Probed {granularities.Count} granularities x {lookbacks.Count} lookbacks. "
                                    + "Tick probes use 4hr window; Day/Minute use 1-day window. MergeBackAdjusted so represents provider availability not cache. "
                                    + "CAVEAT: probes are per-CONTRACT. For symbols where data layout splits across contracts (e.g. expired back-month contracts have no trades), "
                                    + "use historical.list_instruments + per-contract probe for the right time window. "
                                    + "0 bars at deep lookbacks for an active contract may just mean 'contract didn't trade then', not 'provider has no data'.",
            };
        }

        // Runs in a background Task. Walks chunks, updating job state.
        //
        // Auto-stop logic: if MaxEmptyChunks consecutive chunks return 0 bars
        // (and NT didn't error out), we treat that as "provider has no data
        // in this direction" and stop early with status="stopped_no_data".
        // The boundary in FirstDataAt/LastDataAt tells the client what we
        // DID find. Without this, asking for 20 years of M2K (which the
        // provider doesn't have) would still iterate all chunks at 0 bars each.
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

                    // ACQUIRE the shared bars-request gate before issuing the
                    // BarsRequest. Caps concurrency across ALL active jobs so
                    // NT doesn't get hammered by N parallel BarsRequests.
                    // Wait honors cancellation. Released in finally below.
                    bool acquired = false;
                    try
                    {
                        _barsRequestGate.Wait(job.Cts.Token);
                        acquired = true;
                    }
                    catch (OperationCanceledException)
                    {
                        job.Status = "cancelled";
                        job.FinishedAt = DateTime.UtcNow;
                        return;
                    }

                    ChunkResult res;
                    try
                    {
                        res = DownloadChunk(inst, periodType, job.PeriodValue, mdType, cFrom, cTo);
                    }
                    finally
                    {
                        if (acquired) _barsRequestGate.Release();
                    }
                    job.LastChunkErrorCode = res.ErrorCode;
                    job.LastChunkErrorMessage = res.ErrorMessage;
                    job.LastChunkBars = res.BarsCount;
                    job.BarsDownloaded += res.BarsCount;
                    job.ChunksDone++;

                    if (res.BarsCount > 0)
                    {
                        job.ConsecutiveEmptyChunks = 0;
                        if (!job.FirstDataAt.HasValue || res.FirstBarTs < job.FirstDataAt)
                            job.FirstDataAt = res.FirstBarTs;
                        if (!job.LastDataAt.HasValue || res.LastBarTs > job.LastDataAt)
                            job.LastDataAt = res.LastBarTs;
                    }
                    else
                    {
                        job.ConsecutiveEmptyChunks++;
                        // Auto-stop only if the empty was benign (no provider error).
                        // If err code is benign AND we've crossed threshold, stop.
                        // We let "interesting" error codes propagate by also stopping
                        // (so e.g. HistoricalDataNotAvailable can short-circuit).
                        if (job.ConsecutiveEmptyChunks >= job.MaxEmptyChunks)
                        {
                            job.Status = "stopped_no_data";
                            job.Error = $"Stopped after {job.ConsecutiveEmptyChunks} consecutive empty chunks. "
                                      + $"Last err: {job.LastChunkErrorCode}. Provider likely has no data in this range.";
                            job.FinishedAt = DateTime.UtcNow;
                            return;
                        }
                    }
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
        // Continuum if missing locally). Returns full result so caller can
        // distinguish "no trades happened" from "provider has no data here".
        private struct ChunkResult
        {
            public int BarsCount;
            public string ErrorCode;     // .ToString() of NT's ErrorCode enum
            public string ErrorMessage;  // provider-supplied message (often empty)
            public DateTime? FirstBarTs;
            public DateTime? LastBarTs;
        }

        private ChunkResult DownloadChunk(Instrument inst, BarsPeriodType periodType, int periodValue,
                                          MarketDataType mdType, DateTime fromDt, DateTime toDt)
        {
            var result = new ChunkResult { ErrorCode = "NoError" };
            var request = new BarsRequest(inst, fromDt, toDt);
            request.BarsPeriod = new BarsPeriod
            {
                BarsPeriodType = periodType,
                Value = periodValue,
                MarketDataType = mdType,
            };
            // CRITICAL: use the instrument's own TradingHours template, NOT
            // "Default 24 x 7". HDM internally uses the per-instrument template
            // (e.g. "CME US Index Futures ETH" for MNQ/M2K). The wrong template
            // causes BarsRequest to miss the session-bucketed cache HDM
            // populates -- silent failure: 0 bars + NoError. See research in
            // STATUS_AND_ROADMAP.md (forum thread 1036851).
            request.TradingHours = inst.MasterInstrument.TradingHours;
            // MergeBackAdjusted: ask provider (Continuum) for any data
            // we don't already have cached locally.
            request.MergePolicy = MergePolicy.MergeBackAdjusted;

            var wait = new System.Threading.ManualResetEvent(false);
            request.Request((req, err, msg) =>
            {
                // Capture every signal the callback gives us -- without this we
                // can't tell "provider has no data for this range" (silent fail
                // mode we observed with M2K) from "trading was halted that day".
                result.ErrorCode = err.ToString();
                result.ErrorMessage = msg;
                if (err == ErrorCode.NoError && req.Bars != null && req.Bars.Count > 0)
                {
                    result.BarsCount = req.Bars.Count;
                    result.FirstBarTs = req.Bars.GetTime(0);
                    result.LastBarTs  = req.Bars.GetTime(req.Bars.Count - 1);
                }
                wait.Set();
            });
            wait.WaitOne(TimeSpan.FromMinutes(5));
            return result;
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
                // Diagnostic visibility into the most recent BarsRequest callback.
                // last_chunk_error_code='NoError' + last_chunk_bars=0 => quiet market /
                // closed session.  last_chunk_error_code!='NoError' => actual provider
                // issue (e.g. HistoricalDataNotAvailable on M2K).
                ["last_chunk_error_code"] = job.LastChunkErrorCode,
                ["last_chunk_error_message"] = job.LastChunkErrorMessage,
                ["last_chunk_bars"] = job.LastChunkBars,
                ["consecutive_empty_chunks"] = job.ConsecutiveEmptyChunks,
                ["max_empty_chunks"] = job.MaxEmptyChunks,
                ["first_data_at_iso"] = job.FirstDataAt?.ToUniversalTime().ToString("o"),
                ["last_data_at_iso"]  = job.LastDataAt?.ToUniversalTime().ToString("o"),
            };
        }

        // ============================================================
        // P2P file-transfer RPCs (formerly ExportOHLCTransfer.cs)
        // ============================================================

        // -------------------------------------------------------------------
        // Job state
        // -------------------------------------------------------------------

        private class TransferJob
        {
            public string Id;
            public string Format;              // csv | duckdb | parquet
            public string Instrument;
            public string PeriodType;
            public int PeriodValue;
            public string MarketDataType;
            public DateTime From;
            public DateTime To;

            public string TempPath;            // the gzipped output we serve from
            public long SizeBytes;             // size of TempPath
            public string Sha256Hex;           // hex sha256 of TempPath
            public int Port;
            public byte[] Token;               // 32 random bytes
            public int BarCount;
            public Dictionary<string, int> BarCountByStream;

            public TcpListener Listener;
            public CancellationTokenSource Cts;
            public DateTime PreparedAt;
            public DateTime ExpiresAt;
            public DateTime? StartedAt;        // first byte sent
            public DateTime? FinishedAt;
            public long BytesSent;             // running counter
            public string Status = "ready";    // ready | streaming | complete | cancelled | expired | failed
            public string Error;

            public double? ElapsedSec =>
                StartedAt.HasValue ? ((FinishedAt ?? DateTime.UtcNow) - StartedAt.Value).TotalSeconds : (double?)null;
        }

        // Simple holder class — replaced C# value-tuple generic param
        // List<(string priceType, List<OHLCBar> bars)> because NT8's
        // compiler intermittently produced corrupt assemblies when value
        // tuples appeared as generic type arguments (2026-06-07 incident).
        private class StreamBars
        {
            public string PriceType;
            public List<ExportOHLCWindow.OHLCBar> Bars;
        }

        // Static so transfers survive AddOn instance churn within the process,
        // same pattern as _activeDownloads above.
        private static readonly ConcurrentDictionary<string, TransferJob>
            _activeTransfers = new ConcurrentDictionary<string, TransferJob>();

        // ONLY ONE materialization at a time. The OHLC export GUI works by
        // serializing all heavy work (read cache -> write file -> compress)
        // sequentially. Parallel materializations bog NT down: cache reads
        // contend, temp dir balloons with orphan files, gzip CPU spikes,
        // .NET thread pool exhausts. Gate everything heavy through this.
        //
        // RPC handlers still return instantly (they just register the job).
        // Background task waits on this gate before doing any actual work.
        private static readonly System.Threading.SemaphoreSlim _materializeGate
            = new System.Threading.SemaphoreSlim(1, 1);

        // Max wall-clock from prepare RPC to "ready" status. Includes time
        // waiting in the materialize gate behind other transfers. Generous
        // because deep tick payloads (months of MNQ × 3 streams) take many
        // minutes to materialize + compress. Server-side guardrail; clients
        // can have shorter timeouts.
        private static readonly TimeSpan PrepareMaxWait = TimeSpan.FromMinutes(60);

        // Idle timeout AFTER the file is "ready" and listener is open, waiting
        // for the client to connect. 10 min is plenty for normal connect+stream.
        private static readonly TimeSpan ReadyIdleTimeout = TimeSpan.FromMinutes(10);

        // Where prepared files live. Cleaned up after each transfer finishes.
        // Per-AddOn directory so multiple NT installs on one box don't collide.
        private static string TransferTempRoot
        {
            get
            {
                var p = Path.Combine(Path.GetTempPath(), "PlexusTransfers");
                Directory.CreateDirectory(p);
                return p;
            }
        }

        // -------------------------------------------------------------------
        // RPC: historical.prepare_transfer
        // -------------------------------------------------------------------
        //
        // Input:
        //   instrument        : "MNQ 06-26"  (full contract name)
        //   period_type       : "Minute" | "Tick" | "Second" | "Day"
        //   period_value      : int (default 1)
        //   from_iso, to_iso  : ISO-8601 timestamps
        //   market_data_type  : "Last" | "Bid" | "Ask"  (default "Last")
        //   format            : "csv" | "duckdb" | "parquet"
        //
        // Output (the "ticket" the client uses to connect):
        //   ok, transfer_id, host (empty -- client substitutes bus host),
        //   port, token_hex, size_bytes, sha256, format, bar_count,
        //   expires_at_iso, message

        // ASYNC: returns transfer_id IMMEDIATELY with status="preparing". The
        // heavy work (3 BarsRequests + write file + gzip + sha256 + bind
        // listener) runs in a background Task. Client polls
        // historical.get_transfer_ticket(id) until status="ready" to get the
        // port/token/size/sha256 ticket, then connects via TCP as before.
        //
        // Why: synchronous prepare for tick payloads (millions of bars × 3
        // streams) takes 30-90s. PRISM's RPC forwarding timeout is ~10s, so
        // the RPC call appears to fail from the client side while the dispatcher
        // thread is still blocked — exhausts bus thread pool, NT looks frozen.
        // Async pattern (same as historical.download) returns the dispatcher
        // thread immediately and uses background Task.Run for materialization.

        private object HandlePrepareTransferRpc(object payloadObj)
        {
            var payload = payloadObj as IDictionary<string, object> ?? new Dictionary<string, object>();
            string instrumentFullName = ReadString(payload, "instrument", required: true);
            string periodTypeStr = ReadString(payload, "period_type", required: true);
            int periodValue = ReadInt(payload, "period_value", defaultValue: 1);
            string fromIso = ReadString(payload, "from_iso", required: true);
            string toIso = ReadString(payload, "to_iso", required: true);
            string mdTypeStr = ReadString(payload, "market_data_type", required: false) ?? "all";
            string format = (ReadString(payload, "format", required: false) ?? "csv").ToLowerInvariant();

            if (format != "csv" && format != "duckdb" && format != "parquet")
                throw new ArgumentException($"format must be csv|duckdb|parquet, got '{format}'");

            DateTime fromDt = DateTime.Parse(fromIso).ToUniversalTime();
            DateTime toDt = DateTime.Parse(toIso).ToUniversalTime();
            if (toDt <= fromDt) throw new ArgumentException("to_iso must be > from_iso");

            BarsPeriodType periodType = ParsePeriodType(periodTypeStr);

            var inst = Instrument.GetInstrument(instrumentFullName);
            if (inst == null) throw new ArgumentException($"Instrument not found: {instrumentFullName}");

            // Validate streams param fast (synchronous, cheap)
            var streams = ResolveStreams(mdTypeStr);

            // Allocate IDs + paths + register the job with status="preparing"
            string id = Guid.NewGuid().ToString("N").Substring(0, 12);
            string safeSymbol = SafeFileName(inst.FullName);
            string baseName = $"{id}_{safeSymbol}_{periodTypeStr}{periodValue}_{fromDt:yyyyMMdd}-{toDt:yyyyMMdd}";
            string ext = FormatExtension(format);
            string rawPath = Path.Combine(TransferTempRoot, $"{baseName}.{ext}");
            string gzPath = rawPath + ".gz";

            var now = DateTime.UtcNow;
            var job = new TransferJob
            {
                Id = id,
                Format = format,
                Instrument = inst.FullName,
                PeriodType = periodTypeStr,
                PeriodValue = periodValue,
                MarketDataType = mdTypeStr,
                From = fromDt,
                To = toDt,
                TempPath = gzPath,
                Cts = new CancellationTokenSource(),
                PreparedAt = now,
                ExpiresAt = now + PrepareMaxWait,  // hard ceiling on prepare phase
                Status = "preparing",
            };
            _activeTransfers[id] = job;

            // Cleanup orphan temp files from prior runs (>1hr old) once per
            // prepare. Cheap (just stat/delete a few files).
            try { CleanupOrphanTempFiles(); } catch { }

            // Background materialization. Sets job.Status -> "ready" or "failed"
            // when complete. The RPC returns IMMEDIATELY below.
            Task.Run(() => PrepareTransferAsync(job, inst, periodType, periodValue,
                                                  fromDt, toDt, streams, format, rawPath, gzPath));

            // Watchdog: expire prepares that exceed the hard ceiling.
            // (Does NOT expire 'preparing' jobs prematurely — the inner task
            // is doing real work. Only kicks in if something deadlocks.)
            Task.Run(async () =>
            {
                await Task.Delay(PrepareMaxWait);
                if (job.Status == "preparing") ExpireTransfer(job);
            });
            // Separate watchdog for the ready-but-idle case: once status flips
            // to 'ready', if no client connects within ReadyIdleTimeout, expire.
            Task.Run(async () =>
            {
                while (job.Status == "preparing" && DateTime.UtcNow < now + PrepareMaxWait)
                    await Task.Delay(TimeSpan.FromSeconds(2));
                if (job.Status == "ready")
                {
                    var readyAt = DateTime.UtcNow;
                    while (job.Status == "ready" && DateTime.UtcNow - readyAt < ReadyIdleTimeout)
                        await Task.Delay(TimeSpan.FromSeconds(2));
                    if (job.Status == "ready") ExpireTransfer(job);
                }
            });

            // Return the partial ticket — port/token/size/sha256/bar_count
            // fill in once status flips to "ready". Client polls
            // historical.get_transfer_ticket(id) to get the complete ticket.
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["transfer_id"] = id,
                ["status"] = "preparing",
                ["instrument"] = inst.FullName,
                ["period_type"] = periodTypeStr,
                ["period_value"] = periodValue,
                ["market_data_type"] = mdTypeStr,
                ["from_iso"] = fromDt.ToString("o"),
                ["to_iso"] = toDt.ToString("o"),
                ["format"] = format,
                ["prepared_at_iso"] = now.ToString("o"),
                ["expires_at_iso"] = job.ExpiresAt.ToString("o"),
                ["message"] = "Materialization started. Poll historical.get_transfer_ticket "
                            + "until status='ready', then connect to (bus_host, port) with the returned token.",
            };
        }

        // Runs in a background Task. Does the heavy work that used to be
        // synchronous in the RPC handler. Flips job.Status to "ready" with
        // populated port/token/size/sha256 when done, or "failed" with
        // job.Error explaining why.
        private void PrepareTransferAsync(TransferJob job, Instrument inst,
            BarsPeriodType periodType, int periodValue, DateTime fromDt, DateTime toDt,
            List<Tuple<string, MarketDataType>> streams, string format,
            string rawPath, string gzPath)
        {
            // SINGLE-FILE-AT-A-TIME gate. All concurrent preps queue here.
            // While waiting, job.Status remains "preparing" — that's fine, the
            // client polls patiently. Honors cancellation if user calls
            // historical.cancel_transfer.
            bool gateAcquired = false;
            try
            {
                _materializeGate.Wait(job.Cts.Token);
                gateAcquired = true;
            }
            catch (OperationCanceledException)
            {
                job.Status = "cancelled";
                job.FinishedAt = DateTime.UtcNow;
                return;
            }

            try
            {
                // 1. Query bars per stream (LOCAL CACHE ONLY)
                var allBars = new List<StreamBars>();
                int totalBars = 0;
                foreach (var s in streams)
                {
                    var b = ExportOHLCWindow.GetOHLCFromContract(inst, periodType, periodValue, s.Item2, fromDt, toDt, out string _);
                    allBars.Add(new StreamBars { PriceType = s.Item1, Bars = b });
                    totalBars += b.Count;
                }
                if (totalBars == 0)
                {
                    job.Status = "no_data";
                    job.Error = $"No bars in cache for {inst.FullName} {job.PeriodType}{job.PeriodValue} "
                              + $"{job.From:yyyy-MM-dd}..{job.To:yyyy-MM-dd}";
                    job.FinishedAt = DateTime.UtcNow;
                    return;
                }
                job.BarCount = totalBars;
                job.BarCountByStream = allBars.ToDictionary(t => t.PriceType, t => t.Bars.Count);

                // 2. Materialize the format-specific file
                switch (format)
                {
                    case "csv":     WriteTransferCsv(inst.FullName, allBars, rawPath); break;
                    case "duckdb":  WriteTransferDuckDb(inst.FullName, allBars, rawPath); break;
                    case "parquet": WriteTransferParquet(inst.FullName, allBars, rawPath); break;
                }

                // 3. Gzip
                try { GzipFile(rawPath, gzPath); }
                finally { SafeDelete(rawPath); }

                // 4. SHA256 + size
                job.Sha256Hex = Sha256Hex(gzPath);
                job.SizeBytes = new FileInfo(gzPath).Length;

                // 5. Token + listener
                var token = new byte[32];
                using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(token);
                job.Token = token;
                var listener = new TcpListener(IPAddress.Any, 0);
                listener.Start();
                job.Listener = listener;
                job.Port = ((IPEndPoint)listener.LocalEndpoint).Port;

                // 6. Mark ready + start accept loop
                // CRITICAL: reset ExpiresAt now that the listener is opening.
                // The original ExpiresAt was prepare_start + PrepareMaxWait,
                // which can be hours in the past if the materialize gate held
                // us up. The listener's wall-clock budget should start from
                // "ready" — 10 min for the client to connect.
                job.ExpiresAt = DateTime.UtcNow + ReadyIdleTimeout;
                job.Status = "ready";
                Task.Run(() => RunTransferListener(job));
            }
            catch (Exception ex)
            {
                job.Status = "failed";
                job.Error = ex.Message;
                job.FinishedAt = DateTime.UtcNow;
                SafeDelete(rawPath);
                SafeDelete(gzPath);
            }
            finally
            {
                if (gateAcquired) _materializeGate.Release();
            }
        }

        // Sweep orphan files in TransferTempRoot older than 1 hour. Cheap
        // defensive cleanup so abandoned prepares (PRISM disconnect, NT crash,
        // client gave up) don't fill disk over time. Called on every prepare.
        private static void CleanupOrphanTempFiles()
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            string dir = TransferTempRoot;
            if (!Directory.Exists(dir)) return;
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.LastWriteTimeUtc < cutoff) fi.Delete();
                }
                catch { /* best-effort */ }
            }
        }

        // -------------------------------------------------------------------
        // RPC: historical.get_transfer_ticket (poll for async prepare result)
        // -------------------------------------------------------------------
        //
        // Returns the same shape as the old prepare_transfer once status="ready".
        // Until then returns {status: "preparing", ...} so client knows to wait.

        private object HandleGetTransferTicketRpc(object payloadObj)
        {
            var payload = payloadObj as IDictionary<string, object> ?? new Dictionary<string, object>();
            string id = ReadString(payload, "transfer_id", required: true);
            if (!_activeTransfers.TryGetValue(id, out var job))
                throw new KeyNotFoundException($"transfer_id not found: {id}");

            var resp = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["transfer_id"] = job.Id,
                ["status"] = job.Status,
                ["instrument"] = job.Instrument,
                ["period_type"] = job.PeriodType,
                ["period_value"] = job.PeriodValue,
                ["market_data_type"] = job.MarketDataType,
                ["from_iso"] = job.From.ToString("o"),
                ["to_iso"] = job.To.ToString("o"),
                ["format"] = job.Format,
                ["prepared_at_iso"] = job.PreparedAt.ToString("o"),
                ["expires_at_iso"] = job.ExpiresAt.ToString("o"),
                ["error"] = job.Error,
            };
            if (job.Status == "ready" || job.Status == "streaming" ||
                job.Status == "complete" || job.Status == "interrupted")
            {
                resp["host"] = "";
                resp["port"] = job.Port;
                resp["token_hex"] = HexEncode(job.Token);
                resp["size_bytes"] = job.SizeBytes;
                resp["sha256"] = job.Sha256Hex;
                resp["bar_count"] = job.BarCount;
                if (job.BarCountByStream != null)
                    resp["bar_count_by_stream"] = job.BarCountByStream.ToDictionary(
                        kv => (object)kv.Key, kv => (object)kv.Value);
            }
            return resp;
        }

        // -------------------------------------------------------------------
        // RPC: historical.cancel_transfer
        // -------------------------------------------------------------------

        private object HandleCancelTransferRpc(object payloadObj)
        {
            var payload = payloadObj as IDictionary<string, object> ?? new Dictionary<string, object>();
            string id = ReadString(payload, "transfer_id", required: true);

            if (!_activeTransfers.TryGetValue(id, out var job))
                throw new KeyNotFoundException($"transfer_id not found: {id}");

            try { job.Cts.Cancel(); } catch { }
            try { job.Listener?.Stop(); } catch { }
            job.Status = "cancelled";
            job.FinishedAt = DateTime.UtcNow;
            SafeDelete(job.TempPath);

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["transfer_id"] = id,
                ["status"] = "cancelled",
                ["message"] = "Transfer cancelled. Temp file deleted; listener closed.",
            };
        }

        // -------------------------------------------------------------------
        // RPC: historical.list_transfers
        // -------------------------------------------------------------------

        private object HandleListTransfersRpc(object payloadObj)
        {
            var list = new List<object>();
            foreach (var kv in _activeTransfers)
                list.Add(TransferJobToDict(kv.Value));
            // Most recent first
            list = list.OrderByDescending(d => ((Dictionary<string, object>)d)["prepared_at_iso"]).ToList();
            return new Dictionary<string, object> { ["transfers"] = list };
        }

        private static object TransferJobToDict(TransferJob j) => new Dictionary<string, object>
        {
            ["transfer_id"] = j.Id,
            ["format"] = j.Format,
            ["instrument"] = j.Instrument,
            ["period_type"] = j.PeriodType,
            ["period_value"] = j.PeriodValue,
            ["market_data_type"] = j.MarketDataType,
            ["from_iso"] = j.From.ToString("o"),
            ["to_iso"] = j.To.ToString("o"),
            ["port"] = j.Port,
            ["size_bytes"] = j.SizeBytes,
            ["sha256"] = j.Sha256Hex,
            ["bar_count"] = j.BarCount,
            ["bytes_sent"] = j.BytesSent,
            ["status"] = j.Status,
            ["error"] = j.Error,
            ["prepared_at_iso"] = j.PreparedAt.ToString("o"),
            ["expires_at_iso"] = j.ExpiresAt.ToString("o"),
            ["started_at_iso"] = j.StartedAt?.ToString("o"),
            ["finished_at_iso"] = j.FinishedAt?.ToString("o"),
            ["elapsed_sec"] = j.ElapsedSec,
        };

        // -------------------------------------------------------------------
        // TCP listener: accept ONE connection, validate token, stream bytes
        // -------------------------------------------------------------------
        //
        // Wire protocol (all integers little-endian):
        //
        //   Handshake (client -> server): 40 bytes
        //     [0..32)  : token
        //     [32..40) : resume_offset (uint64)
        //
        //   Handshake reply (server -> client): 9 bytes
        //     [0]      : status (0=ok, 1=bad_token, 2=cancelled, 3=offset_oob, 4=error)
        //     [1..9)   : remaining_bytes_from_offset (uint64)
        //
        //   Stream (server -> client): repeat until length=0
        //     [0..4)   : chunk_length (uint32)
        //     [4..)    : chunk_length bytes of raw gzipped file content
        //
        //   Terminator: chunk_length = 0 marks EOF.

        private const int ChunkBytes = 64 * 1024;

        // Per successful byte, bump expiry by this much so an actively-progressing
        // client doesn't get torn down mid-transfer. Idle-only expiry still kicks
        // in if the client stops making progress.
        private static readonly TimeSpan ProgressExtension = TimeSpan.FromMinutes(2);

        // Accept-loop: keeps the listener open until cancel/expire, allowing the
        // client to reconnect after a network drop and resume from its byte
        // offset. The pre-compressed temp file lives on disk until the job
        // terminally completes/cancels/expires -- never deleted on per-connection
        // failure, so cross-drop resume Just Works.
        private void RunTransferListener(TransferJob job)
        {
            try
            {
                while (!job.Cts.IsCancellationRequested && DateTime.UtcNow < job.ExpiresAt)
                {
                    TcpClient client;
                    try
                    {
                        // Pending() lets us cooperate with the expiry watchdog
                        // (don't block forever on Accept when we're about to expire)
                        while (!job.Listener.Pending())
                        {
                            if (job.Cts.IsCancellationRequested) return;
                            if (DateTime.UtcNow >= job.ExpiresAt) return;
                            Thread.Sleep(200);
                        }
                        client = job.Listener.AcceptTcpClient();
                    }
                    catch (SocketException) { return; }        // listener closed
                    catch (ObjectDisposedException) { return; }

                    HandleOneConnection(job, client); // never throws
                    if (job.Status == "complete") return; // happy-path exit
                    // Else: stays in loop for resume reconnects
                }
            }
            finally
            {
                try { job.Listener?.Stop(); } catch { }
                // File lives on disk for any non-terminal state (e.g. "interrupted"
                // mid-resume). Terminal states clean it up here OR in their RPC
                // handler (cancel/expire).
                if (job.Status == "complete" || job.Status == "cancelled" || job.Status == "expired")
                    SafeDelete(job.TempPath);
            }
        }

        // One connection lifecycle: handshake → stream → either complete or
        // interrupt. Never throws -- failures flip status to "interrupted" and
        // the outer accept-loop keeps the listener alive for the next attempt.
        private void HandleOneConnection(TransferJob job, TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    client.NoDelay = true;

                    // 1. Read handshake (40 bytes)
                    var hdr = ReadExact(stream, 40);
                    if (hdr == null) { Interrupt(job, "client closed before handshake"); return; }

                    byte[] gotToken = new byte[32];
                    Array.Copy(hdr, 0, gotToken, 0, 32);
                    long resumeOffset = BitConverter.ToInt64(hdr, 32);

                    // 2. Validate token (constant-time)
                    if (!ConstantTimeEq(gotToken, job.Token))
                    {
                        WriteHandshakeReply(stream, status: 1, remaining: 0);
                        Interrupt(job, "bad token");
                        return;
                    }

                    long fileSize = job.SizeBytes;
                    if (resumeOffset < 0 || resumeOffset > fileSize)
                    {
                        WriteHandshakeReply(stream, status: 3, remaining: 0);
                        Interrupt(job, $"offset {resumeOffset} out of range [0,{fileSize}]");
                        return;
                    }

                    long remaining = fileSize - resumeOffset;
                    WriteHandshakeReply(stream, status: 0, remaining: (ulong)remaining);

                    // Mark streaming + capture the first stream start time
                    job.Status = "streaming";
                    if (!job.StartedAt.HasValue) job.StartedAt = DateTime.UtcNow;

                    // 3. Stream from offset, length-prefixed chunks
                    using (var fs = new FileStream(job.TempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        fs.Seek(resumeOffset, SeekOrigin.Begin);
                        var buf = new byte[ChunkBytes];
                        long connBytes = 0;
                        DateTime lastBump = DateTime.UtcNow;
                        while (!job.Cts.IsCancellationRequested)
                        {
                            int n = fs.Read(buf, 0, buf.Length);
                            if (n <= 0) break;
                            WriteUInt32LE(stream, (uint)n);
                            stream.Write(buf, 0, n);
                            job.BytesSent += n;
                            connBytes += n;
                            // Bump expiry on progress so big transfers don't time out mid-stream.
                            // Throttle to once per second to avoid lock contention.
                            if ((DateTime.UtcNow - lastBump).TotalSeconds >= 1.0)
                            {
                                job.ExpiresAt = DateTime.UtcNow + ProgressExtension;
                                lastBump = DateTime.UtcNow;
                            }
                        }

                        if (!job.Cts.IsCancellationRequested)
                        {
                            // Terminator (length=0) signals EOF cleanly
                            WriteUInt32LE(stream, 0);
                            stream.Flush();
                            job.Status = "complete";
                            job.FinishedAt = DateTime.UtcNow;
                        }
                        // Else: cancelled mid-stream -- outer finally tears down
                    }
                }
            }
            catch (Exception ex)
            {
                // Network error mid-stream: file stays on disk, listener stays
                // open, client can reconnect with offset = bytes already on disk.
                Interrupt(job, ex.Message);
            }
        }

        private static void Interrupt(TransferJob job, string err)
        {
            // Non-terminal: keeps the file alive for the next reconnect attempt.
            job.Status = "interrupted";
            job.Error = err;
        }

        private static void ExpireTransfer(TransferJob job)
        {
            try { job.Listener?.Stop(); } catch { }
            try { job.Cts.Cancel(); } catch { }
            job.Status = "expired";
            job.FinishedAt = DateTime.UtcNow;
            SafeDelete(job.TempPath);
        }

        // -------------------------------------------------------------------
        // Stream resolution
        // -------------------------------------------------------------------
        //
        // market_data_type is interpreted as:
        //   "all"   -> [last, bid, ask]  (default; missing streams just contribute 0 bars)
        //   "Last"  -> [last]
        //   "Bid"   -> [bid]
        //   "Ask"   -> [ask]
        //
        // We tag each pulled set with its lower-case price_type for the output's
        // price_type column. This is the dimension Databento ohlcv doesn't have
        // (their ohlcv is implicit "last") -- it's how we represent NT's natively
        // 3-streamed model in a flat row-oriented file.

        private static List<Tuple<string, MarketDataType>> ResolveStreams(string mdTypeStr)
        {
            var s = (mdTypeStr ?? "all").Trim().ToLowerInvariant();
            switch (s)
            {
                case "all": return new List<Tuple<string, MarketDataType>>
                {
                    Tuple.Create("last", MarketDataType.Last),
                    Tuple.Create("bid",  MarketDataType.Bid),
                    Tuple.Create("ask",  MarketDataType.Ask),
                };
                case "last": return new List<Tuple<string, MarketDataType>> { Tuple.Create("last", MarketDataType.Last) };
                case "bid":  return new List<Tuple<string, MarketDataType>> { Tuple.Create("bid",  MarketDataType.Bid)  };
                case "ask":  return new List<Tuple<string, MarketDataType>> { Tuple.Create("ask",  MarketDataType.Ask)  };
                default: throw new ArgumentException($"market_data_type must be all|Last|Bid|Ask, got '{mdTypeStr}'");
            }
        }

        // -------------------------------------------------------------------
        // Format writers — unified schema, Databento-compatible
        // -------------------------------------------------------------------
        //
        // All formats produce the same logical row shape:
        //
        //   ts_event   (TIMESTAMP UTC, nanosecond precision in CSV via int64)
        //   symbol     (full instrument name, e.g. "MNQ 06-26")
        //   price_type ('last' | 'bid' | 'ask')
        //   source     ('nt')   -- provenance flag; Databento data uses 'databento'
        //   open, high, low, close   (DOUBLE)
        //   volume     (BIGINT)
        //
        // For the row primary key: (price_type, ts_event) -- same bar timestamp
        // can exist in multiple streams. Databento ohlcv is implicit "last" only,
        // so when joining NT+Databento corpora, filter NT to price_type='last'.

        private static void WriteTransferCsv(string symbol, List<StreamBars> allBars, string path)
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var utf8NoBom = new UTF8Encoding(false);
            using (var w = new StreamWriter(path, false, utf8NoBom))
            {
                w.WriteLine("ts_event_ns,symbol,price_type,source,open,high,low,close,volume");
                foreach (var stream in allBars)
                {
                    foreach (var b in stream.Bars)
                    {
                        long nsSinceEpoch = (long)((b.Time.ToUniversalTime() - epoch).Ticks) * 100L;
                        w.WriteLine($"{nsSinceEpoch},{EscapeCsv(symbol)},{stream.PriceType},nt,"
                                    + $"{b.Open},{b.High},{b.Low},{b.Close},{b.Volume}");
                    }
                }
            }
        }

        private static string EscapeCsv(string s)
        {
            if (s == null) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static void WriteTransferDuckDb(string symbol, List<StreamBars> allBars, string path)
        {
            DuckDBConnection cnx = null;
            try
            {
                cnx = new DuckDBConnection($"Data Source={path}");
                cnx.Open();
                ExecuteNonQueryStatic(cnx, @"
                    CREATE TABLE bars (
                        ts_event    TIMESTAMP NOT NULL,
                        symbol      VARCHAR NOT NULL,
                        price_type  VARCHAR NOT NULL,
                        source      VARCHAR NOT NULL DEFAULT 'nt',
                        open        DOUBLE NOT NULL,
                        high        DOUBLE NOT NULL,
                        low         DOUBLE NOT NULL,
                        close       DOUBLE NOT NULL,
                        volume      BIGINT NOT NULL,
                        PRIMARY KEY (price_type, ts_event)
                    )");
                foreach (var stream in allBars)
                    InsertBarsStatic(cnx, "bars", symbol, stream.PriceType, stream.Bars);
            }
            finally
            {
                if (cnx != null) { try { cnx.Close(); } catch { } try { cnx.Dispose(); } catch { } }
            }
        }

        private static void WriteTransferParquet(string symbol, List<StreamBars> allBars, string path)
        {
            // No Parquet.Net NuGet -- pipe through DuckDB COPY TO.
            string tmpDb = path + ".tmpdb";
            try
            {
                WriteTransferDuckDb(symbol, allBars, tmpDb);
                DuckDBConnection cnx = null;
                try
                {
                    cnx = new DuckDBConnection($"Data Source={tmpDb}");
                    cnx.Open();
                    // ORDER BY (price_type, ts_event) so consumers can stream-read
                    // grouped by stream without an extra sort pass.
                    var sql = $"COPY (SELECT ts_event, symbol, price_type, source, open, high, low, close, volume "
                            + $"FROM bars ORDER BY price_type, ts_event) "
                            + $"TO '{path.Replace("'", "''")}' (FORMAT PARQUET, COMPRESSION SNAPPY)";
                    ExecuteNonQueryStatic(cnx, sql);
                }
                finally
                {
                    if (cnx != null) { try { cnx.Close(); } catch { } try { cnx.Dispose(); } catch { } }
                }
            }
            finally
            {
                SafeDelete(tmpDb);
            }
        }

        // Static analogs of the instance helpers on ExportOHLCWindow -- needed
        // because the AddOn class is independent of the Window class instance.
        private static void ExecuteNonQueryStatic(DuckDBConnection cnx, string sql)
        {
            var cmd = cnx.CreateCommand();
            try { cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            finally { try { cmd.Dispose(); } catch { } }
        }

        private static void InsertBarsStatic(DuckDBConnection cnx, string tableName,
            string symbol, string priceType, List<ExportOHLCWindow.OHLCBar> bars)
        {
            if (bars.Count == 0) return;
            int chunkSize = 1000;
            // Escape symbol for SQL literal (handles "MNQ 06-26" -- space is fine,
            // but defensively quote anything that has special chars). Single-quote
            // escape via doubling.
            string sym = symbol.Replace("'", "''");
            for (int i = 0; i < bars.Count; i += chunkSize)
            {
                int n = Math.Min(chunkSize, bars.Count - i);
                var sb = new StringBuilder();
                sb.Append($"INSERT OR REPLACE INTO {tableName} (ts_event, symbol, price_type, source, open, high, low, close, volume) VALUES ");
                for (int j = 0; j < n; j++)
                {
                    var b = bars[i + j];
                    // DuckDB TIMESTAMP literal: 'YYYY-MM-DD HH:MM:SS.ffffff' (microseconds)
                    string ts = b.Time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.ffffff");
                    if (j > 0) sb.Append(",");
                    sb.Append($"(TIMESTAMP '{ts}','{sym}','{priceType}','nt',{b.Open},{b.High},{b.Low},{b.Close},{b.Volume})");
                }
                ExecuteNonQueryStatic(cnx, sb.ToString());
            }
        }

        // -------------------------------------------------------------------
        // Wire helpers
        // -------------------------------------------------------------------

        private static byte[] ReadExact(NetworkStream s, int n)
        {
            var buf = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r = s.Read(buf, off, n - off);
                if (r <= 0) return null;
                off += r;
            }
            return buf;
        }

        private static void WriteHandshakeReply(NetworkStream s, byte status, ulong remaining)
        {
            var reply = new byte[9];
            reply[0] = status;
            Buffer.BlockCopy(BitConverter.GetBytes(remaining), 0, reply, 1, 8);
            s.Write(reply, 0, 9);
            s.Flush();
        }

        private static void WriteUInt32LE(NetworkStream s, uint v)
        {
            var b = BitConverter.GetBytes(v); // little-endian on Intel
            s.Write(b, 0, 4);
        }

        private static bool ConstantTimeEq(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        // -------------------------------------------------------------------
        // File helpers
        // -------------------------------------------------------------------

        private static void GzipFile(string srcPath, string gzPath)
        {
            using (var src = new FileStream(srcPath, FileMode.Open, FileAccess.Read))
            using (var dst = new FileStream(gzPath, FileMode.Create, FileAccess.Write))
            using (var gz = new GZipStream(dst, CompressionLevel.Optimal, leaveOpen: false))
                src.CopyTo(gz);
        }

        private static string Sha256Hex(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                var hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var bt in hash) sb.Append(bt.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string HexEncode(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static void SafeDelete(string path)
        {
            try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string SafeFileName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }

        private static string FormatExtension(string format)
        {
            switch (format)
            {
                case "csv":     return "csv";
                case "duckdb":  return "db";
                case "parquet": return "parquet";
                default:        return "bin";
            }
        }
    }
}
