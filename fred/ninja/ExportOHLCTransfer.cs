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

        // Static so transfers survive AddOn instance churn within the process,
        // same pattern as _activeDownloads above.
        private static readonly ConcurrentDictionary<string, TransferJob>
            _activeTransfers = new ConcurrentDictionary<string, TransferJob>();

        // Idle timeout from prepare → first connection. After this, the temp
        // file and listener are torn down. Generous: 5 minutes covers slow
        // clients + network round-trips.
        private static readonly TimeSpan TransferIdleTimeout = TimeSpan.FromMinutes(5);

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

        private object HandlePrepareTransferRpc(object payloadObj)
        {
            var payload = payloadObj as IDictionary<string, object> ?? new Dictionary<string, object>();
            string instrumentFullName = ReadString(payload, "instrument", required: true);
            string periodTypeStr = ReadString(payload, "period_type", required: true);
            int periodValue = ReadInt(payload, "period_value", defaultValue: 1);
            string fromIso = ReadString(payload, "from_iso", required: true);
            string toIso = ReadString(payload, "to_iso", required: true);
            string mdTypeStr = ReadString(payload, "market_data_type", required: false) ?? "Last";
            string format = (ReadString(payload, "format", required: false) ?? "csv").ToLowerInvariant();

            if (format != "csv" && format != "duckdb" && format != "parquet")
                throw new ArgumentException($"format must be csv|duckdb|parquet, got '{format}'");

            DateTime fromDt = DateTime.Parse(fromIso).ToUniversalTime();
            DateTime toDt = DateTime.Parse(toIso).ToUniversalTime();
            if (toDt <= fromDt) throw new ArgumentException("to_iso must be > from_iso");

            BarsPeriodType periodType = ParsePeriodType(periodTypeStr);
            MarketDataType mdType = ParseMarketDataType(mdTypeStr);

            var inst = Instrument.GetInstrument(instrumentFullName);
            if (inst == null) throw new ArgumentException($"Instrument not found: {instrumentFullName}");

            // 1. Query bars (LOCAL CACHE ONLY -- caller should have ensured
            //    coverage via historical.download first if needed)
            var bars = ExportOHLCWindow.GetOHLCFromContract(inst, periodType, periodValue, mdType, fromDt, toDt, out string diag);
            if (bars.Count == 0)
                throw new InvalidOperationException(
                    $"No bars in cache for {inst.FullName} {periodTypeStr}{periodValue} {fromIso}..{toIso}. " +
                    "Use historical.download to fetch from provider first.");

            // 2. Prepare ID + paths
            string id = Guid.NewGuid().ToString("N").Substring(0, 12);
            string safeSymbol = SafeFileName(inst.FullName);
            string baseName = $"{id}_{safeSymbol}_{periodTypeStr}{periodValue}_{fromDt:yyyyMMdd}-{toDt:yyyyMMdd}";
            string ext = FormatExtension(format);
            string rawPath = Path.Combine(TransferTempRoot, $"{baseName}.{ext}");
            string gzPath = rawPath + ".gz";

            // 3. Materialize the format-specific file
            try
            {
                switch (format)
                {
                    case "csv":     WriteTransferCsv(bars, rawPath); break;
                    case "duckdb":  WriteTransferDuckDb(inst.FullName, periodTypeStr, mdTypeStr, bars, rawPath); break;
                    case "parquet": WriteTransferParquet(inst.FullName, periodTypeStr, mdTypeStr, bars, rawPath); break;
                }
            }
            catch
            {
                SafeDelete(rawPath); SafeDelete(gzPath);
                throw;
            }

            // 4. Gzip-compress to wire-ready file
            try
            {
                GzipFile(rawPath, gzPath);
            }
            finally
            {
                // Raw file no longer needed once compressed copy exists
                SafeDelete(rawPath);
            }

            // 5. Compute SHA256 of the on-wire payload (gzipped file)
            string sha256 = Sha256Hex(gzPath);
            long sizeBytes = new FileInfo(gzPath).Length;

            // 6. Generate token + bind listener
            byte[] token = new byte[32];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(token);

            var listener = new TcpListener(IPAddress.Any, 0); // port=0 → OS picks free
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            // 7. Register job
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
                SizeBytes = sizeBytes,
                Sha256Hex = sha256,
                Port = port,
                Token = token,
                BarCount = bars.Count,
                Listener = listener,
                Cts = new CancellationTokenSource(),
                PreparedAt = now,
                ExpiresAt = now + TransferIdleTimeout,
            };
            _activeTransfers[id] = job;

            // 8. Accept-loop on a background task (single connection then close)
            Task.Run(() => RunTransferListener(job));

            // 9. Idle timeout watchdog
            Task.Run(async () =>
            {
                await Task.Delay(TransferIdleTimeout);
                if (job.Status == "ready") ExpireTransfer(job);
            });

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["transfer_id"] = id,
                ["host"] = "",                   // client substitutes the bus host
                ["port"] = port,
                ["token_hex"] = HexEncode(token),
                ["size_bytes"] = sizeBytes,
                ["sha256"] = sha256,
                ["format"] = format,
                ["bar_count"] = bars.Count,
                ["instrument"] = inst.FullName,
                ["period_type"] = periodTypeStr,
                ["period_value"] = periodValue,
                ["market_data_type"] = mdTypeStr,
                ["from_iso"] = fromDt.ToString("o"),
                ["to_iso"] = toDt.ToString("o"),
                ["prepared_at_iso"] = now.ToString("o"),
                ["expires_at_iso"] = job.ExpiresAt.ToString("o"),
                ["message"] = $"Connect to bus_host:{port} within {TransferIdleTimeout.TotalMinutes:F0} min, "
                            + "send 32-byte token + 8-byte LE resume_offset, then read length-prefixed chunks "
                            + "(uint32 LE length, terminated by length=0). After download, verify SHA256, then gunzip.",
            };
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
        // Format writers
        // -------------------------------------------------------------------

        private static void WriteTransferCsv(List<ExportOHLCWindow.OHLCBar> bars, string path)
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            using (var w = new StreamWriter(path, false, Encoding.UTF8))
            {
                w.WriteLine("unix_ms,open,high,low,close,volume");
                foreach (var b in bars)
                {
                    long unixMs = (long)(b.Time.ToUniversalTime() - epoch).TotalMilliseconds;
                    w.WriteLine($"{unixMs},{b.Open},{b.High},{b.Low},{b.Close},{b.Volume}");
                }
            }
        }

        private static void WriteTransferDuckDb(string symbol, string periodType, string priceType,
            List<ExportOHLCWindow.OHLCBar> bars, string path)
        {
            // Fresh single-table DB per transfer. Single price_type per request
            // (avoids the multi-pass quirks of the on-disk archive DB).
            DuckDBConnection cnx = null;
            try
            {
                cnx = new DuckDBConnection($"Data Source={path}");
                cnx.Open();
                ExecuteNonQueryStatic(cnx, @"
                    CREATE TABLE bars (
                        symbol      VARCHAR NOT NULL,
                        price_type  VARCHAR NOT NULL,
                        unix_ms     BIGINT NOT NULL,
                        timestamp   TIMESTAMP NOT NULL,
                        open        DOUBLE NOT NULL,
                        high        DOUBLE NOT NULL,
                        low         DOUBLE NOT NULL,
                        close       DOUBLE NOT NULL,
                        volume      BIGINT NOT NULL,
                        PRIMARY KEY (unix_ms)
                    )");
                InsertBarsStatic(cnx, "bars", symbol, priceType.ToLowerInvariant(), bars);
            }
            finally
            {
                if (cnx != null) { try { cnx.Close(); } catch { } try { cnx.Dispose(); } catch { } }
            }
        }

        private static void WriteTransferParquet(string symbol, string periodType, string priceType,
            List<ExportOHLCWindow.OHLCBar> bars, string path)
        {
            // No Parquet.Net NuGet -- pipe through DuckDB COPY TO. The intermediate
            // .db file lives next to the output briefly then is deleted.
            string tmpDb = path + ".tmpdb";
            try
            {
                WriteTransferDuckDb(symbol, periodType, priceType, bars, tmpDb);
                DuckDBConnection cnx = null;
                try
                {
                    cnx = new DuckDBConnection($"Data Source={tmpDb}");
                    cnx.Open();
                    // DuckDB writes to the path verbatim, overwriting if exists.
                    var sql = $"COPY (SELECT * FROM bars ORDER BY unix_ms) TO '{path.Replace("'", "''")}' (FORMAT PARQUET, COMPRESSION SNAPPY)";
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
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            int chunkSize = 1000;
            for (int i = 0; i < bars.Count; i += chunkSize)
            {
                int n = Math.Min(chunkSize, bars.Count - i);
                var sb = new StringBuilder();
                sb.Append($"INSERT OR REPLACE INTO {tableName} (symbol, price_type, unix_ms, timestamp, open, high, low, close, volume) VALUES ");
                for (int j = 0; j < n; j++)
                {
                    var b = bars[i + j];
                    long unixMs = (long)(b.Time.ToUniversalTime() - epoch).TotalMilliseconds;
                    string ts = b.Time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
                    if (j > 0) sb.Append(",");
                    sb.Append($"('{symbol}','{priceType}',{unixMs},'{ts}',{b.Open},{b.High},{b.Low},{b.Close},{b.Volume})");
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
