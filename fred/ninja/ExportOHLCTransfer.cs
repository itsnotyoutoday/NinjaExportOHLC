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
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    /// <summary>
    /// historical.prepare_transfer / cancel_transfer / list_transfers
    ///
    /// Partial of ExportOHLCAddOn that adds a P2P file-transfer layer on top
    /// of the existing GetOHLCFromContract reader. Use when bars-as-JSON (the
    /// fetch_bars RPC) is too heavy for the bus — multi-MB to multi-GB pulls.
    ///
    /// Architecture:
    ///   1. Client calls historical.prepare_transfer(query, format='csv|duckdb|parquet')
    ///   2. Server runs the query, writes the file, GZips to disk, computes SHA256,
    ///      starts a one-shot TcpListener on a random ephemeral port, returns
    ///      {transfer_id, port, token, size_bytes, sha256, expires_at_iso}
    ///   3. Client connects to (bus_host, port), sends 32-byte token + 8-byte
    ///      resume_offset, server replies with 1-byte status + 8-byte remaining,
    ///      then streams length-prefixed raw bytes (terminated by 0-length).
    ///   4. Client writes bytes to local .gz, verifies SHA256, gunzips to final.
    ///
    /// Why pre-compress on disk instead of stream-compressing on the wire:
    ///   - Resume is trivial (seek to offset, write more)
    ///   - SHA256 is computable upfront and unambiguous
    ///   - Single-connection model — no complex multipart protocols
    ///   - Tradeoff: doubles peak disk usage briefly; we clean up immediately
    ///
    /// Security:
    ///   - 32-byte cryptographically-random token, single-use, constant-time compare
    ///   - Listener binds to 0.0.0.0 but only accepts the FIRST valid handshake
    ///   - 5-minute idle timeout from prepare; auto-cancels + deletes temp file
    ///   - Runs INSIDE NT process — inherits NT's Windows Firewall allow rule
    ///
    /// Format support:
    ///   - csv:     reuses the existing OHLCBar → CSV writer pattern
    ///   - duckdb:  fresh single-table DB written via DuckDB.NET
    ///   - parquet: via DuckDB COPY TO ... (FORMAT PARQUET) — no Parquet.Net NuGet
    /// </summary>
    public partial class ExportOHLCAddOn
    {
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
