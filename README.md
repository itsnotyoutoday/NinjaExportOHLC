# ExportOHLC

**Version: 1.10.1**
**Last Updated: 2026-06-10**

A NinjaTrader 8 AddOn that exports historical futures data (Tick / 1-Minute / Day OHLCV) from the local NT cache across **all contract months** of a symbol, stitched into a continuous series with **front-month-wins** dedup at roll boundaries.

Output formats: **DuckDB**, **CSV**, **Parquet** (any combination, all three written incrementally so memory stays bounded for multi-million-row tick datasets).

## What this is for

You've downloaded historical data into NT (via Tools → Historical Data Manager) across many contract months — e.g. `MNQ 03-25`, `MNQ 06-25`, `MNQ 09-25`, ..., `MNQ 06-26`. You want one continuous series for analysis, not 20 separate per-contract files. This AddOn does the per-contract `BarsRequest`, the date-window filtering, the cross-contract stitching, and the export, with progress visible in a GUI status box and resumable across crashes.

## File structure

```
ExportOHLC/
├── README.md                  ← this file
└── ninja/
    ├── ExportOHLC.cs          ← REQUIRED: GUI menu, BarsRequest, DuckDB, CSV/Parquet, stitching
    └── ExportOHLC.Plexus.cs   ← OPTIONAL: Plexus-bus RPC integration
```

### ExportOHLC.cs (required, base)

- Adds a "Export OHLC History (All Contracts)" menu item under **Control Center → New / Tools**
- WPF window with symbol input, data-type checkboxes (Tick/Minute/Day), date pickers, output-format checkboxes, output folder, status log
- Discovers all instruments matching the symbol root, **filters to contracts whose expiry overlaps the requested date range** (`[startDate − 1mo, endDate + 3mo]`)
- Per-contract: chunks the `BarsRequest` (1-day for Tick, 30-day for Minute) for visible progress, stages bars into a per-contract DuckDB table, then merges into the final keyed table with `INSERT … ON CONFLICT … WHERE EXCLUDED.volume > volume` (front-month wins)
- Tracks completion in an `export_progress` table — re-runs in the same date range skip already-finished contracts with no `BarsRequest` calls
- "Replace existing data" wipes both data and progress entries for the symbol

**Zero Plexus dependency.** Drop this file alone into `bin/Custom/AddOns/` and it works.

### ExportOHLC.Plexus.cs (optional)

Adds these RPCs to the [Plexus](https://github.com/...) bus (if you use it):

| Group | Methods |
|---|---|
| Discovery | `historical.list_instruments`, `list_periods`, `check_inventory`, `probe_availability` |
| Read | `historical.fetch_bars` (size-capped, paginated) |
| Async download | `historical.download`, `download_status`, `download_cancel`, `list_downloads` |
| P2P transfer | `historical.prepare_transfer`, `cancel_transfer`, `list_transfers`, `get_transfer_ticket` |

Uses C# `partial void` hooks — the base file declares them, this file implements them. Drop this file to ship without Plexus; the compiler erases the declarations and call sites entirely (zero overhead, no NoOps file needed).

## Installation

1. Copy the `.cs` file(s) you want into NT's AddOn directory:
   ```
   %USERPROFILE%\Documents\NinjaTrader 8\bin\Custom\AddOns\
   ```
   - **Minimum:** `ExportOHLC.cs`
   - **With Plexus bus:** also copy `ExportOHLC.Plexus.cs`
2. Native dependencies (place in NT's `bin\` folder):
   - `DuckDB.NET.Data.dll`
   - `DuckDB.NET.Bindings.dll`
   - `duckdb.dll` (native x64)
3. In NT: **Tools → NinjaScript Editor → F5** (or Compile button)
4. Restart NT or wait for the AddOn to load — menu item appears under **Control Center → New / Tools**

## Usage

1. **Control Center → New → Export OHLC History (All Contracts)**
2. Enter **Symbol Root** (e.g. `MNQ`, `MYM`, `ES`, `M2K`)
3. Select **Data Types** — Tick / 1 Minute / Day (any combination)
4. Set **Date Range** — pre-filled to the last 90 days; click "Clear dates" for all-history
5. Select **Export Format** — CSV / DuckDB / Parquet (any combination)
6. **"Replace existing data"** — wipes the existing rows + progress entries for this symbol before re-fetching. Leave unchecked to merge / resume.
7. Verify **Output Folder** (default: `%USERPROFILE%\Documents\NinjaTrader_OHLC\`)
8. Click **EXPORT**

The status box shows per-contract progress, per-chunk timing, and a final summary. Output goes to `<Output Folder>\<SYMBOL>\`.

### Reading the log

```
Filtered 47 contracts → 5 that overlap [2026-03-11, 2026-06-09]:
  • MNQ 03-26 (expires 2026-03-21)
  • MNQ 06-26 (expires 2026-06-20)
  • MNQ 09-26 (expires 2026-09-19)
  ...

[1/5] Processing MNQ 03-26...
    Last 1/90 [2026-03-11]: 184,231 bars (0.6s fetch + 0.4s stage)
    Last 2/90 [2026-03-12]: 198,712 bars (0.5s fetch + 0.4s stage)
    ...
  Last: 8,234,512 bars total → 8,234,512 staged in 92.3s (90 chunks)
  Bid: ...
  Ask: ...
  ✓ Merged 9,887,221 rows into final in 14.2s

[2/5] MNQ 06-26: SKIPPED — already covered by prior run (12,340,123 bars in DB)
...
```

## Output formats

### DuckDB

Single `.db` file per symbol: `<OutDir>/<SYMBOL>/<SYMBOL>.db`. Three tables — `ticks`, `minutes`, `days` — each with the same schema:

```sql
CREATE TABLE ticks (
    symbol      VARCHAR NOT NULL,
    contract    VARCHAR DEFAULT '',     -- which contract supplied the winning bar
    price_type  VARCHAR NOT NULL,        -- 'last' | 'bid' | 'ask'
    unix_ms     BIGINT NOT NULL,         -- UTC ms since epoch
    timestamp   TIMESTAMP NOT NULL,
    open        DOUBLE NOT NULL,
    high        DOUBLE NOT NULL,
    low         DOUBLE NOT NULL,
    close       DOUBLE NOT NULL,
    volume      BIGINT NOT NULL,
    PRIMARY KEY (symbol, price_type, unix_ms)
);
```

Plus a meta-table `export_progress` tracking which contracts completed which date ranges.

### CSV

One file per `(timeframe, price_type)`: `<SYMBOL>_Last_OHLC_<timestamp>.csv` etc.

Columns: `unix_ms, open, high, low, close, volume, contract`

### Parquet

One file per `(timeframe, price_type)`: `<SYMBOL>_Last_OHLC_<timestamp>.parquet` etc.

Columns: `symbol, contract, price_type, unix_ms, timestamp, open, high, low, close, volume` (richer than CSV — you can union multiple files into one frame and group by contract / price_type).

## Stitching behavior

When two contracts have a bar at the same timestamp (e.g. during a roll), the bar with **higher volume** wins. The `contract` column on the surviving row records which contract that was.

To audit stitching across a roll:

```sql
SELECT contract, COUNT(*) AS bars, MIN(timestamp), MAX(timestamp)
FROM minutes
WHERE symbol = 'MNQ' AND timestamp BETWEEN '2026-06-01' AND '2026-07-01'
GROUP BY contract
ORDER BY MIN(timestamp);
```

Expected output around June 20 roll: Jun-26 dominates pre-roll, Sep-26 dominates post-roll, with a brief overlap window where volume tips the choice each way.

## Resumability

Every successfully-completed contract gets a row in `export_progress`. A re-run with the **same or narrower date window** will:

- Query `export_progress` per contract
- `SKIP` the entire `BarsRequest` if the prior run's range fully covers the current request
- Process the contract from scratch otherwise

To force a full refetch: check **"Replace existing data"** before clicking EXPORT — wipes both the OHLC data and the progress entries for the symbol.

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| "Processing X" with no further log lines | NT's `BarsRequest` is loading the contract's cache from disk. For tick data, can take minutes per contract. | Narrow the date range; verify NT is responsive. |
| "Parser Error: Adding columns with constraints not yet supported" | Schema migration of pre-1.9.1 DB; benign (column gets added without NOT NULL). | Re-run — the column is now there. |
| CS0246 `DatePickerTextBox` not found | Missing `using System.Windows.Controls.Primitives;` | Already in v1.8.5+. |
| Compile errors mentioning `Plexus` namespace | You copied `ExportOHLC.Plexus.cs` without having `PlexusBridge.dll` on the AddOn search path | Delete `ExportOHLC.Plexus.cs` if you don't use Plexus; or install PlexusBridge. |

## Version history

- **1.10.0** — Staging inserts now use DuckDB's binary `Appender` API (100×+ faster on tick data — chunks that took 60+ minutes via SQL INSERT now finish in seconds); auto-drop orphan staging tables from prior crashed runs
- **1.9.2** — Schema migration ALTER without `NOT NULL` constraint (DuckDB limitation)
- **1.9.1** — Added `contract` column for audit trail; CSV/Parquet/DuckDB all expose it
- **1.9.0** — `export_progress` table for resumability; per-contract atomic merge; selectable status log; DatePicker dark theme; `yyyy-MM-dd` dates
- **1.8.4** — Contract filter `+3mo` forward (was `+12mo`)
- **1.8.3** — Filter contracts to those overlapping requested date range
- **1.8.2** — Chunked `BarsRequest` (1-day for tick) for visible progress
- **1.8.1** — Per-day defaults; elapsed-time logging; staging-table inserts
- **1.8.0** — DatePicker UI, incremental DuckDB writes with `ON CONFLICT DO UPDATE WHERE EXCLUDED.volume > volume`, Parquet support
- **1.7** — Initial published version (synchronous in-memory aggregation)

## License

MIT — see [LICENSE](LICENSE).
