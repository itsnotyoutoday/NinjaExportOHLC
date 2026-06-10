#region Using declarations
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
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
    /// Version: 1.10.0
    /// Last Updated: 2026-06-10
    /// </summary>
    public partial class ExportOHLCAddOn : AddOnBase
    {
        private NTMenuItem menuItem;
        private NTMenuItem existingMenu;


        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Export OHLC history from all contracts (optionally exposes historical.* RPCs via Plexus bus)";
                Name = "ExportOHLC";
            }
            else if (State == State.Active)
            {
                // partial-method hook: no-op when ExportOHLC.Plexus.cs is absent.
                // When present, the implementation registers historical.* RPCs.
                TryRegisterPlexusRpcs();
            }
            else if (State == State.Terminated)
            {
                // partial-method hook: no-op when ExportOHLC.Plexus.cs is absent.
                DisposePlexusRpcs();
            }
        }

        // Optional Plexus integration. Implemented in ExportOHLC.Plexus.cs.
        // If that file is deleted, the C# compiler erases these declarations
        // AND their call sites in OnStateChange above — zero runtime overhead.
        partial void TryRegisterPlexusRpcs();
        partial void DisposePlexusRpcs();


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
        private DatePicker fromDatePicker;
        private DatePicker toDatePicker;
        private CheckBox exportCsvCheck;
        private CheckBox exportDuckDbCheck;
        private CheckBox exportParquetCheck;
        private CheckBox replaceExistingDataCheck;
        private TextBox outputBox;
        private Button exportBtn;
        private Button checkDbBtn;
        private TextBox statusBox;     // selectable + Ctrl-C-copyable
        private ProgressBar progressBar;
        private bool running;

        public ExportOHLCWindow()
        {
            Title = "Export OHLC - All Contracts";
            Width = 520;
            Height = 680;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // ------------------------------------------------------------------
            // Dark-theme overrides for built-in WPF controls that otherwise pick
            // up Windows defaults and clash with NT's window. The DatePickerTextBox
            // style targets the editable field inside each DatePicker; the
            // DatePicker style covers the calendar-popup chrome.
            // ------------------------------------------------------------------
            var darkBg     = new SolidColorBrush(Color.FromRgb(45, 45, 55));
            var darkInputBg = new SolidColorBrush(Color.FromRgb(55, 55, 65));
            var lightFg    = Brushes.LightGray;
            var subtleBorder = new SolidColorBrush(Color.FromRgb(90, 90, 100));

            var dpTextBoxStyle = new Style(typeof(DatePickerTextBox));
            dpTextBoxStyle.Setters.Add(new Setter(Control.BackgroundProperty, darkInputBg));
            dpTextBoxStyle.Setters.Add(new Setter(Control.ForegroundProperty, lightFg));
            dpTextBoxStyle.Setters.Add(new Setter(Control.BorderBrushProperty, subtleBorder));
            dpTextBoxStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
            Resources.Add(typeof(DatePickerTextBox), dpTextBoxStyle);

            var dpStyle = new Style(typeof(DatePicker));
            dpStyle.Setters.Add(new Setter(DatePicker.BackgroundProperty, darkInputBg));
            dpStyle.Setters.Add(new Setter(DatePicker.ForegroundProperty, lightFg));
            dpStyle.Setters.Add(new Setter(DatePicker.BorderBrushProperty, subtleBorder));
            Resources.Add(typeof(DatePicker), dpStyle);

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

            // Date Range — calendar pickers prevent typos that produced silent
            // wrong-date exports under the old free-text TextBox UI.
            stack.Children.Add(new TextBlock { Text = "Date Range (optional, blank = all local data):", FontWeight = FontWeights.SemiBold });
            var dateStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            dateStack.Children.Add(new TextBlock { Text = "From:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            // Sensible defaults — last 90 days. Blank/all-history is a foot-gun
            // on tick data: NT's BarsRequest can take 10+ min per contract per
            // market data type, with the GUI showing no progress in between.
            fromDatePicker = new DatePicker { Width = 130, SelectedDate = DateTime.Today.AddDays(-90), Margin = new Thickness(0, 0, 15, 0) };
            dateStack.Children.Add(fromDatePicker);
            dateStack.Children.Add(new TextBlock { Text = "To:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            toDatePicker = new DatePicker { Width = 130, SelectedDate = DateTime.Today };
            dateStack.Children.Add(toDatePicker);
            stack.Children.Add(dateStack);
            var clearDatesBtn = new Button { Content = "Clear dates (all history)", FontSize = 10, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
            clearDatesBtn.Click += (s, e) => { fromDatePicker.SelectedDate = null; toDatePicker.SelectedDate = null; };
            stack.Children.Add(clearDatesBtn);

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
            exportDuckDbCheck = new CheckBox { Content = "DuckDB", IsChecked = false, Margin = new Thickness(0, 0, 20, 0), Foreground = Brushes.LightGray };
            formatStack.Children.Add(exportDuckDbCheck);
            exportParquetCheck = new CheckBox { Content = "Parquet", IsChecked = false, Foreground = Brushes.LightGray };
            formatStack.Children.Add(exportParquetCheck);
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

            // Status log — read-only TextBox so the user can highlight + Ctrl-C
            // log lines and paste them back to me when something looks wrong.
            // Single-color (TextBox can't do mixed colors); errors are flagged
            // with a "⚠️ " prefix.
            statusBox = new TextBox
            {
                Text = "Ready.\n",
                IsReadOnly = true,
                IsUndoEnabled = false,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = new SolidColorBrush(Color.FromRgb(25, 25, 30)),
                Foreground = Brushes.LightGreen,
                BorderBrush = subtleBorder,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Padding = new Thickness(8),
                Height = 160,
            };
            stack.Children.Add(statusBox);

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

            // Dates come straight from the calendar pickers — no parsing /
            // typo risk. SelectedDate is null when the user hasn't picked.
            DateTime? fromDate = fromDatePicker.SelectedDate;
            DateTime? toDate = toDatePicker.SelectedDate;

            if (fromDate.HasValue && toDate.HasValue && toDate.Value.Date < fromDate.Value.Date)
            {
                Log("To date must be on or after From date.", true);
                return;
            }

            bool exportCsv = exportCsvCheck.IsChecked == true;
            bool exportDuckDb = exportDuckDbCheck.IsChecked == true;
            bool exportParquet = exportParquetCheck.IsChecked == true;
            bool replaceExisting = replaceExistingDataCheck.IsChecked == true;

            if (!exportCsv && !exportDuckDb && !exportParquet)
            {
                Log("Select at least one export format (CSV, DuckDB, or Parquet)", true);
                return;
            }

            string outDir = outputBox.Text.Trim();

            running = true;
            exportBtn.IsEnabled = false;
            progressBar.Visibility = Visibility.Visible;
            progressBar.IsIndeterminate = true;
            statusBox.Text = "";

            // Run export for each selected data type
            ThreadPool.QueueUserWorkItem(_ => RunExportAll(symbolRoot, outDir, fromDate, toDate, exportCsv, exportDuckDb, exportParquet, replaceExisting, doTick, doMinute, doDay));
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

            statusBox.Text = "";
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
            bool exportCsv, bool exportDuckDb, bool exportParquet, bool replaceExisting, bool doTick, bool doMinute, bool doDay)
        {
            try
            {
                if (doTick)
                {
                    Log("═══ TICK DATA ═══");
                    RunExport(symbolRoot, 1, "Tick", outDir, fromDate, toDate, exportCsv, exportDuckDb, exportParquet, replaceExisting);
                }

                if (doMinute)
                {
                    Log("\n═══ MINUTE DATA ═══");
                    RunExport(symbolRoot, 1, "Minute", outDir, fromDate, toDate, exportCsv, exportDuckDb, exportParquet, replaceExisting);
                }

                if (doDay)
                {
                    Log("\n═══ DAY DATA ═══");
                    RunExport(symbolRoot, 1, "Day", outDir, fromDate, toDate, exportCsv, exportDuckDb, exportParquet, replaceExisting);
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
            DateTime? fromDate, DateTime? toDate, bool exportCsv, bool exportDuckDb, bool exportParquet, bool replaceExisting)
        {
            // Connection + staging-file state lives outside try so finally can clean up.
            DuckDBConnection conn = null;
            string stagingDbPath = null;
            bool isStagingDb = false;

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

                // CRITICAL: filter contracts whose Expiry can't possibly overlap
                // the requested date range. Without this, NT loads the entire
                // cached history of every long-expired contract (e.g. MNQ 12-20)
                // just to filter down to 0 rows — which is what made the export
                // appear "stuck on the first contract ever" for tick data.
                //
                // Window: Expiry ∈ [startDate − 1mo, endDate + 3mo].
                //  -1mo back:  catches the front-month that JUST expired before
                //              the start of the window (cache still has data for it).
                //  +3mo forward: includes the NEXT quarterly contract after endDate
                //              (e.g. for endDate=Jul-26 we keep Sep-26 because
                //              Jun-26 already expired on Jun-20 — Sep-26 is the
                //              only source of late-Jun / Jul data). Stops there;
                //              Dec-26 / Mar-27 etc are NOT loaded.
                int originalCount = contracts.Count;
                DateTime expiryMin = startDate.AddMonths(-1);
                DateTime expiryMax = endDate.AddMonths(3);
                var dropped = contracts.Where(c => c.Expiry < expiryMin || c.Expiry > expiryMax).ToList();
                contracts   = contracts.Where(c => c.Expiry >= expiryMin && c.Expiry <= expiryMax).ToList();

                if (contracts.Count == 0)
                {
                    Log($"No contracts overlap the requested date range " +
                        $"[{startDate:yyyy-MM-dd}, {endDate:yyyy-MM-dd}].", true);
                    Log($"  Found {originalCount} {symbolRoot} contracts total, but none have Expiry in " +
                        $"[{expiryMin:yyyy-MM-dd}, {expiryMax:yyyy-MM-dd}].", true);
                    Log("  Widen the date range or check that contracts for this period exist.", true);
                    return;
                }

                if (dropped.Count > 0)
                {
                    Log($"Filtered {originalCount} contracts → {contracts.Count} that overlap " +
                        $"[{startDate:yyyy-MM-dd}, {endDate:yyyy-MM-dd}]:");
                    Log($"  (skipped {dropped.Count} contracts whose expiry is outside [{expiryMin:yyyy-MM-dd}, {expiryMax:yyyy-MM-dd}])");
                }
                else
                {
                    Log($"Found {contracts.Count} contracts:");
                }
                foreach (var c in contracts)
                {
                    Log($"  • {c.FullName} (expires {c.Expiry:yyyy-MM-dd})");
                }

                if (!Directory.Exists(outDir))
                    Directory.CreateDirectory(outDir);

                string symbolDir = Path.Combine(outDir, symbolRoot);
                if (!Directory.Exists(symbolDir))
                    Directory.CreateDirectory(symbolDir);

                BarsPeriodType periodType = tfType switch
                {
                    "Tick" => BarsPeriodType.Tick,
                    "Day" => BarsPeriodType.Day,
                    _ => BarsPeriodType.Minute
                };

                string tableName = tfType switch
                {
                    "Tick" => "ticks",
                    "Day" => "days",
                    _ => "minutes"
                };

                // ============================================================
                // Open the DuckDB store BEFORE iterating contracts. We flush
                // each contract's bars to disk immediately so memory stays
                // bounded — full series can be tens of millions of ticks.
                //
                // Stitching across contracts: the PRIMARY KEY is
                //   (symbol, price_type, unix_ms)
                // and the INSERT uses ON CONFLICT DO UPDATE WHERE
                // EXCLUDED.volume > existing.volume — so when contracts
                // overlap during a roll the front-month (higher volume)
                // bar wins, regardless of the order we process contracts.
                //
                // If the user only chose CSV/Parquet we still use a DuckDB
                // file as the staging layer; it's deleted in finally.
                // ============================================================
                if (exportDuckDb)
                {
                    stagingDbPath = Path.Combine(symbolDir, $"{symbolRoot}.db");
                    isStagingDb = false;
                    Log($"\n--- DuckDB: {symbolRoot}/{Path.GetFileName(stagingDbPath)} (target) ---");
                }
                else
                {
                    stagingDbPath = Path.Combine(symbolDir, $".staging_{Guid.NewGuid():N}.db");
                    isStagingDb = true;
                    Log($"\n--- DuckDB: temporary staging file (will be deleted after export) ---");
                }
                Log($"Table: {tableName}");
                Log($"Mode: {(replaceExisting ? "REPLACE existing" : "MERGE with existing")}");

                conn = new DuckDBConnection($"Data Source={stagingDbPath}");
                conn.Open();

                // Final table (keyed). Per-contract merges happen as we go,
                // not all at the end — so a mid-run crash leaves every contract
                // that already finished durable in this table.
                //
                // The 'contract' column is informational (not part of PK): it
                // records WHICH contract supplied the winning bar at each
                // timestamp. Lets you audit stitching after the fact:
                //   SELECT contract, COUNT(*) FROM minutes
                //   WHERE symbol='MNQ' AND timestamp BETWEEN ... GROUP BY contract;
                //
                // Note: contract is declared without NOT NULL because DuckDB's
                // ALTER TABLE ADD COLUMN doesn't accept constraints (only
                // DEFAULT), and we want CREATE and ALTER to produce the same
                // shape. Code always writes a value so empty/null never
                // appears in practice.
                ExecuteNonQuery(conn, $@"
                    CREATE TABLE IF NOT EXISTS {tableName} (
                        symbol      VARCHAR NOT NULL,
                        contract    VARCHAR DEFAULT '',
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

                // Migrate pre-1.9.1 DBs (which didn't have the contract column).
                // DuckDB: ADD COLUMN supports DEFAULT but NOT NULL constraints
                // are not yet supported. IF NOT EXISTS makes it idempotent.
                try
                {
                    ExecuteNonQuery(conn, $"ALTER TABLE {tableName} ADD COLUMN IF NOT EXISTS contract VARCHAR DEFAULT ''");
                }
                catch (Exception mex) { Log($"  (schema migration note: {mex.Message})", true); }

                // Progress table — lets re-runs skip contracts that already
                // finished. Keyed on (symbol, contract, table, from, to) so:
                //   * same range → skip
                //   * subset range covered by a prior run → also skip
                //   * different range → re-fetch
                // Cleared by 'Replace existing data' alongside the data wipe.
                ExecuteNonQuery(conn, $@"
                    CREATE TABLE IF NOT EXISTS export_progress (
                        symbol       VARCHAR NOT NULL,
                        contract     VARCHAR NOT NULL,
                        table_name   VARCHAR NOT NULL,
                        from_date    DATE    NOT NULL,
                        to_date      DATE    NOT NULL,
                        bars_count   BIGINT  NOT NULL,
                        completed_at TIMESTAMP NOT NULL,
                        PRIMARY KEY (symbol, contract, table_name, from_date, to_date)
                    )");

                if (replaceExisting)
                {
                    Log($"  Deleting existing data + progress for {symbolRoot}...");
                    ExecuteNonQuery(conn, $"DELETE FROM {tableName} WHERE symbol = '{symbolRoot}'");
                    ExecuteNonQuery(conn, $"DELETE FROM export_progress WHERE symbol = '{symbolRoot}' AND table_name = '{tableName}'");
                }

                // Clean up orphan staging tables from prior crashed/killed runs.
                // Each contract creates _stage_<tableName>_<guid>. When NT is
                // hard-killed mid-run those don't get dropped, and they
                // accumulate in the DB file across runs.
                try
                {
                    var orphans = new List<string>();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT table_name FROM duckdb_tables()";
                        using (var rdr = cmd.ExecuteReader())
                        {
                            string prefix = $"_stage_{tableName}_";
                            while (rdr.Read())
                            {
                                string tn = rdr.GetString(0);
                                if (tn != null && tn.StartsWith(prefix)) orphans.Add(tn);
                            }
                        }
                    }
                    foreach (var t in orphans)
                    {
                        try { ExecuteNonQuery(conn, $"DROP TABLE IF EXISTS {t}"); } catch { }
                    }
                    if (orphans.Count > 0)
                        Log($"  Dropped {orphans.Count} orphan staging table(s) from prior runs");
                }
                catch { /* DB might not have duckdb_tables() in very old versions */ }

                DateTime requestedFromDate = startDate.Date;
                DateTime requestedToDate   = endDate.Date;

                int contractNum = 0;
                long totalLastWritten = 0, totalBidWritten = 0, totalAskWritten = 0;
                int contractsSkipped = 0;
                int contractsProcessed = 0;

                foreach (var contract in contracts)
                {
                    contractNum++;

                    // Idempotency: skip if a prior run already covered this
                    // contract's data over a date window that includes the
                    // current request.
                    long priorBars;
                    if (IsContractCovered(conn, symbolRoot, contract.FullName, tableName, requestedFromDate, requestedToDate, out priorBars))
                    {
                        Log($"\n[{contractNum}/{contracts.Count}] {contract.FullName}: SKIPPED — already covered by prior run ({priorBars:N0} bars in DB)");
                        contractsSkipped++;
                        continue;
                    }

                    Log($"\n[{contractNum}/{contracts.Count}] Processing {contract.FullName}...");
                    contractsProcessed++;

                    // Per-contract staging table. Created fresh so a crash
                    // mid-contract leaves nothing dangling on the next run —
                    // we just re-fetch this contract from scratch.
                    string stageContract = $"_stage_{tableName}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
                    ExecuteNonQuery(conn, $@"
                        CREATE TABLE {stageContract} (
                            symbol      VARCHAR NOT NULL,
                            price_type  VARCHAR NOT NULL,
                            unix_ms     BIGINT NOT NULL,
                            timestamp   TIMESTAMP NOT NULL,
                            open        DOUBLE NOT NULL,
                            high        DOUBLE NOT NULL,
                            low         DOUBLE NOT NULL,
                            close       DOUBLE NOT NULL,
                            volume      BIGINT NOT NULL
                        )");

                    try
                    {
                        long lastStaged = FetchAndStage(conn, stageContract, symbolRoot, contract, periodType, period, MarketDataType.Last, "last", startDate, endDate);
                        long bidStaged  = FetchAndStage(conn, stageContract, symbolRoot, contract, periodType, period, MarketDataType.Bid,  "bid",  startDate, endDate);
                        long askStaged  = FetchAndStage(conn, stageContract, symbolRoot, contract, periodType, period, MarketDataType.Ask,  "ask",  startDate, endDate);

                        totalLastWritten += lastStaged;
                        totalBidWritten  += bidStaged;
                        totalAskWritten  += askStaged;
                        long contractTotal = lastStaged + bidStaged + askStaged;

                        if (contractTotal > 0)
                        {
                            // Merge this one contract's staging → final. Dedup
                            // within-contract via QUALIFY (in case NT returned
                            // any duplicates), upsert via ON CONFLICT with
                            // higher-volume wins so the front month beats back
                            // months at roll-overlap timestamps.
                            //
                            // The contract name is injected as a literal in the
                            // SELECT — staging rows don't carry it (all rows in
                            // one staging table are from the same contract).
                            // On ON CONFLICT, contract is overwritten too, so
                            // the row records the contract that WON (front-month
                            // at that timestamp), not the contract that arrived
                            // first.
                            string contractLit = contract.FullName.Replace("'", "''");
                            var swMerge = System.Diagnostics.Stopwatch.StartNew();
                            ExecuteNonQuery(conn, $@"
                                INSERT INTO {tableName} (symbol, contract, price_type, unix_ms, timestamp, open, high, low, close, volume)
                                SELECT symbol, '{contractLit}', price_type, unix_ms, timestamp, open, high, low, close, volume
                                FROM {stageContract}
                                QUALIFY ROW_NUMBER() OVER (
                                    PARTITION BY symbol, price_type, unix_ms
                                    ORDER BY volume DESC
                                ) = 1
                                ON CONFLICT (symbol, price_type, unix_ms) DO UPDATE SET
                                    open = EXCLUDED.open, high = EXCLUDED.high, low = EXCLUDED.low,
                                    close = EXCLUDED.close, volume = EXCLUDED.volume, timestamp = EXCLUDED.timestamp,
                                    contract = EXCLUDED.contract
                                WHERE EXCLUDED.volume > volume");
                            swMerge.Stop();
                            Log($"  ✓ Merged {contractTotal:N0} rows into final in {swMerge.Elapsed.TotalSeconds:F1}s");
                        }
                        else
                        {
                            Log($"  (no bars for this contract in range — nothing to merge)");
                        }

                        // Mark complete EVEN IF 0 bars — that's a valid result
                        // (this contract genuinely has no cached data in the
                        // requested window) and we shouldn't re-probe it.
                        MarkContractComplete(conn, symbolRoot, contract.FullName, tableName, requestedFromDate, requestedToDate, contractTotal);
                    }
                    finally
                    {
                        // Always drop staging — even if merge threw — so it
                        // doesn't accumulate in the DB across runs.
                        try { ExecuteNonQuery(conn, $"DROP TABLE IF EXISTS {stageContract}"); } catch { }
                    }
                }

                long finalLast = CountRows(conn, tableName, symbolRoot, "last");
                long finalBid  = CountRows(conn, tableName, symbolRoot, "bid");
                long finalAsk  = CountRows(conn, tableName, symbolRoot, "ask");

                if (finalLast == 0 && finalBid == 0 && finalAsk == 0)
                {
                    Log("\nNo data in final table after processing!", true);
                    return;
                }

                Log($"\nFinal table - Last: {finalLast:N0}, Bid: {finalBid:N0}, Ask: {finalAsk:N0}");
                if (contractsSkipped > 0)
                    Log($"({contractsProcessed} contracts processed, {contractsSkipped} skipped via export_progress)");

                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                int filesWritten = 0;

                // CSV via DuckDB COPY — same schema as the previous hand-rolled writer
                // (unix_ms,open,high,low,close,volume), but emitted from disk instead
                // of an in-memory list.
                if (exportCsv)
                {
                    Log($"\n--- CSV Export ---");
                    if (finalLast > 0)
                    {
                        string p = Path.Combine(symbolDir, $"{symbolRoot}_Last_OHLC_{ts}.csv");
                        ExportToCsv(conn, tableName, symbolRoot, "last", p);
                        Log($"✓ Last: {finalLast:N0} bars → {symbolRoot}/{Path.GetFileName(p)}");
                        filesWritten++;
                    }
                    if (finalBid > 0)
                    {
                        string p = Path.Combine(symbolDir, $"{symbolRoot}_Bid_OHLC_{ts}.csv");
                        ExportToCsv(conn, tableName, symbolRoot, "bid", p);
                        Log($"✓ Bid: {finalBid:N0} bars → {symbolRoot}/{Path.GetFileName(p)}");
                        filesWritten++;
                    }
                    if (finalAsk > 0)
                    {
                        string p = Path.Combine(symbolDir, $"{symbolRoot}_Ask_OHLC_{ts}.csv");
                        ExportToCsv(conn, tableName, symbolRoot, "ask", p);
                        Log($"✓ Ask: {finalAsk:N0} bars → {symbolRoot}/{Path.GetFileName(p)}");
                        filesWritten++;
                    }
                }

                // Parquet via DuckDB COPY — richer schema (includes symbol, price_type,
                // timestamp) so a downstream reader can load multiple files into one
                // frame and slice without re-parsing filenames.
                if (exportParquet)
                {
                    Log($"\n--- Parquet Export ---");
                    if (finalLast > 0)
                    {
                        string p = Path.Combine(symbolDir, $"{symbolRoot}_Last_OHLC_{ts}.parquet");
                        ExportToParquet(conn, tableName, symbolRoot, "last", p);
                        Log($"✓ Last: {finalLast:N0} bars → {symbolRoot}/{Path.GetFileName(p)}");
                        filesWritten++;
                    }
                    if (finalBid > 0)
                    {
                        string p = Path.Combine(symbolDir, $"{symbolRoot}_Bid_OHLC_{ts}.parquet");
                        ExportToParquet(conn, tableName, symbolRoot, "bid", p);
                        Log($"✓ Bid: {finalBid:N0} bars → {symbolRoot}/{Path.GetFileName(p)}");
                        filesWritten++;
                    }
                    if (finalAsk > 0)
                    {
                        string p = Path.Combine(symbolDir, $"{symbolRoot}_Ask_OHLC_{ts}.parquet");
                        ExportToParquet(conn, tableName, symbolRoot, "ask", p);
                        Log($"✓ Ask: {finalAsk:N0} bars → {symbolRoot}/{Path.GetFileName(p)}");
                        filesWritten++;
                    }
                }

                if (exportDuckDb)
                {
                    Log($"\n✓ DuckDB: {(finalLast + finalBid + finalAsk):N0} total rows in '{tableName}' table");
                }

                GetDateRange(conn, tableName, symbolRoot, out DateTime? minTs, out DateTime? maxTs);

                Log("\n══════════════════════════════════════════");
                Log("EXPORT COMPLETE!");
                Log($"Symbol: {symbolRoot}");
                Log($"Timeframe: {period} {tfType}");
                Log($"Contracts processed: {contracts.Count}");
                if (exportCsv || exportParquet) Log($"Files created: {filesWritten}");
                if (exportDuckDb) Log($"DuckDB table: {tableName}");
                if (minTs.HasValue) Log($"Date range: {minTs.Value:yyyy-MM-dd} to {maxTs.Value:yyyy-MM-dd}");
                Log($"Output: {symbolDir}");
                Log("══════════════════════════════════════════");
            }
            catch (Exception ex)
            {
                Log($"\nERROR: {ex.Message}", true);
                if (ex.InnerException != null)
                    Log($"  Inner: {ex.InnerException.Message}", true);
            }
            finally
            {
                if (conn != null)
                {
                    try { conn.Close(); } catch { }
                    try { conn.Dispose(); } catch { }
                }
                // Drop the staging .db (and the .wal sidecar DuckDB writes) so the
                // CSV/Parquet-only flow leaves no trace.
                if (isStagingDb && stagingDbPath != null)
                {
                    try { if (File.Exists(stagingDbPath))         File.Delete(stagingDbPath); } catch { }
                    try { if (File.Exists(stagingDbPath + ".wal")) File.Delete(stagingDbPath + ".wal"); } catch { }
                }
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
                // Per-instrument TradingHours (HDM's canonical choice) so we read
                // from the same session-bucketed cache HDM populates. "Default 24 x 7"
                // can silently miss CME-templated cache entries.
                request.TradingHours = instrument.MasterInstrument.TradingHours;

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

        private void Log(string msg, bool isError = false)
        {
            string line = (isError ? "⚠️ " : "") + msg + "\n";
            Dispatcher.Invoke(() =>
            {
                // First Log call after a fresh run starts from "Ready.\n" — clear it.
                if (statusBox.Text == "Ready.\n") statusBox.Text = "";
                statusBox.AppendText(line);
                statusBox.ScrollToEnd();
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

        // Pull (contract, price_type) from NT IN CHUNKS, staging each as it
        // arrives. The chunk size is period-aware:
        //   Tick   → 1 day  (NT stores tick data per-day on disk; this maps
        //                    1:1 to one file read per chunk and gives visible
        //                    per-chunk progress so we don't ever look hung)
        //   Minute → 30 days
        //   Day    → single call (negligible data)
        // Each call's elapsed time is logged so the user can see which range
        // is actually slow instead of staring at "Processing X".
        private long FetchAndStage(DuckDBConnection conn, string stagingTable, string symbol,
            Instrument contract, BarsPeriodType periodType, int period,
            MarketDataType mdType, string priceLabel,
            DateTime startDate, DateTime endDate)
        {
            int chunkDays;
            if (periodType == BarsPeriodType.Tick)        chunkDays = 1;
            else if (periodType == BarsPeriodType.Day)    chunkDays = 365 * 50; // effectively single call
            else /* Minute */                              chunkDays = 30;

            string label = priceLabel.Substring(0, 1).ToUpper() + priceLabel.Substring(1);
            bool isTick = periodType == BarsPeriodType.Tick;

            long totalBars = 0;
            long totalStaged = 0;
            int totalChunks = Math.Max(1, (int)Math.Ceiling((endDate - startDate).TotalDays / chunkDays));
            int chunkIdx = 0;
            var swTotal = System.Diagnostics.Stopwatch.StartNew();

            DateTime cur = startDate;
            while (cur < endDate)
            {
                DateTime nxt = cur.AddDays(chunkDays);
                if (nxt > endDate) nxt = endDate;
                chunkIdx++;

                var swCall = System.Diagnostics.Stopwatch.StartNew();
                var bars = GetOHLCFromContract(contract, periodType, period, mdType, cur, nxt, out string diag);
                swCall.Stop();

                if (bars.Count > 0)
                {
                    totalBars += bars.Count;
                    var swIns = System.Diagnostics.Stopwatch.StartNew();
                    int wrote = InsertBarsToStaging(conn, stagingTable, symbol, priceLabel, bars);
                    swIns.Stop();
                    totalStaged += wrote;
                    if (isTick)
                        Log($"    {label} {chunkIdx}/{totalChunks} [{cur:yyyy-MM-dd}]: {bars.Count:N0} bars ({swCall.Elapsed.TotalSeconds:F1}s fetch + {swIns.Elapsed.TotalSeconds:F1}s stage)");
                }
                else if (isTick && (swCall.Elapsed.TotalSeconds > 1 || diag != null))
                {
                    // Empty chunk worth surfacing — slow OR diagnostic message from NT.
                    Log($"    {label} {chunkIdx}/{totalChunks} [{cur:yyyy-MM-dd}]: 0 bars ({swCall.Elapsed.TotalSeconds:F1}s)" + (diag != null ? $" — {diag}" : ""));
                }
                cur = nxt;
            }
            swTotal.Stop();

            // Always-visible per-price-type summary line.
            Log($"  {label}: {totalBars:N0} bars total → {totalStaged:N0} staged in {swTotal.Elapsed.TotalSeconds:F1}s ({chunkIdx} chunk{(chunkIdx==1?"":"s")})");
            return totalStaged;
        }

        // Bulk insert into the no-PK staging table using DuckDB's native
        // binary Appender API instead of string-built INSERT statements.
        //
        // Why: the prior multi-row INSERT approach measured ~300 bars/sec for
        // tick data (1M-bar chunks took 60+ minutes to stage). The Appender
        // pushes binary values directly through DuckDB's C API, skipping the
        // SQL parser, type-coercer, and per-statement transaction flush.
        // Realistic speedup: 100×+ — tick chunks should now finish in seconds.
        //
        // Column order MUST match the staging table schema exactly:
        //   symbol, price_type, unix_ms, timestamp, open, high, low, close, volume
        private int InsertBarsToStaging(DuckDBConnection connection, string stagingTable, string symbol, string priceType, List<OHLCBar> bars)
        {
            if (bars.Count == 0) return 0;

            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            using (var appender = connection.CreateAppender(stagingTable))
            {
                foreach (var bar in bars)
                {
                    DateTime ts = bar.Time.ToUniversalTime();
                    long unixMs = (long)(ts - epoch).TotalMilliseconds;
                    long volume = (long)bar.Volume; // staging schema = BIGINT

                    appender.CreateRow()
                        .AppendValue(symbol)
                        .AppendValue(priceType)
                        .AppendValue(unixMs)
                        .AppendValue(ts)
                        .AppendValue(bar.Open)
                        .AppendValue(bar.High)
                        .AppendValue(bar.Low)
                        .AppendValue(bar.Close)
                        .AppendValue(volume)
                        .EndRow();
                }
                appender.Close(); // explicit flush; using also disposes
            }
            return bars.Count;
        }

        // Resume-after-crash check. Returns true if a prior export run
        // already finished this (symbol, contract, table) over a date window
        // that fully covers [requestedFrom, requestedTo]. priorBars is the
        // row count that prior run wrote (0 = contract had no data in that
        // window — still counts as 'covered', no point re-asking NT).
        private bool IsContractCovered(DuckDBConnection connection, string symbol, string contractName,
            string tableName, DateTime requestedFrom, DateTime requestedTo, out long priorBars)
        {
            priorBars = 0;
            var cmd = connection.CreateCommand();
            try
            {
                // Match any progress row whose [from_date, to_date] fully
                // contains the currently-requested window. Sum bars_count
                // in case multiple rows match (different prior runs).
                cmd.CommandText = $@"
                    SELECT COALESCE(SUM(bars_count), 0)
                    FROM export_progress
                    WHERE symbol = '{symbol.Replace("'","''")}'
                      AND contract = '{contractName.Replace("'","''")}'
                      AND table_name = '{tableName}'
                      AND from_date <= DATE '{requestedFrom:yyyy-MM-dd}'
                      AND to_date   >= DATE '{requestedTo:yyyy-MM-dd}'";
                var result = cmd.ExecuteScalar();
                if (result == null || result == DBNull.Value) return false;
                priorBars = Convert.ToInt64(result);
                // The check is purely on coverage — even priorBars=0 is OK
                // (means the contract was probed and had no cached data in
                // that window). To distinguish "no match" from "matched but
                // 0 bars" we'd need a second query, but here a non-coverage
                // result returns 0 as well; refine if needed.
                cmd.CommandText = $@"
                    SELECT COUNT(*)
                    FROM export_progress
                    WHERE symbol = '{symbol.Replace("'","''")}'
                      AND contract = '{contractName.Replace("'","''")}'
                      AND table_name = '{tableName}'
                      AND from_date <= DATE '{requestedFrom:yyyy-MM-dd}'
                      AND to_date   >= DATE '{requestedTo:yyyy-MM-dd}'";
                long matchCount = Convert.ToInt64(cmd.ExecuteScalar());
                return matchCount > 0;
            }
            catch { return false; }
            finally { try { cmd.Dispose(); } catch { } }
        }

        private void MarkContractComplete(DuckDBConnection connection, string symbol, string contractName,
            string tableName, DateTime fromDate, DateTime toDate, long barsCount)
        {
            string sym = symbol.Replace("'", "''");
            string ctr = contractName.Replace("'", "''");
            string sql = $@"
                INSERT INTO export_progress
                    (symbol, contract, table_name, from_date, to_date, bars_count, completed_at)
                VALUES
                    ('{sym}', '{ctr}', '{tableName}',
                     DATE '{fromDate:yyyy-MM-dd}', DATE '{toDate:yyyy-MM-dd}',
                     {barsCount}, CURRENT_TIMESTAMP)
                ON CONFLICT (symbol, contract, table_name, from_date, to_date) DO UPDATE SET
                    bars_count = EXCLUDED.bars_count,
                    completed_at = EXCLUDED.completed_at";
            ExecuteNonQuery(connection, sql);
        }

        private long CountRows(DuckDBConnection connection, string tableName, string symbol, string priceType)
        {
            var cmd = connection.CreateCommand();
            try
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM {tableName} WHERE symbol = '{symbol}' AND price_type = '{priceType}'";
                var result = cmd.ExecuteScalar();
                return result == null || result == DBNull.Value ? 0 : Convert.ToInt64(result);
            }
            catch { return 0; }
            finally { try { cmd.Dispose(); } catch { } }
        }

        // Note: out params instead of a tuple. NT8's compiler has had issues
        // with ValueTuple synthesized types; out params keep this safe.
        private void GetDateRange(DuckDBConnection connection, string tableName, string symbol, out DateTime? minTs, out DateTime? maxTs)
        {
            minTs = null;
            maxTs = null;
            var cmd = connection.CreateCommand();
            try
            {
                cmd.CommandText = $"SELECT MIN(timestamp), MAX(timestamp) FROM {tableName} WHERE symbol = '{symbol}'";
                using (var rdr = cmd.ExecuteReader())
                {
                    if (rdr.Read() && !rdr.IsDBNull(0) && !rdr.IsDBNull(1))
                    {
                        minTs = rdr.GetDateTime(0);
                        maxTs = rdr.GetDateTime(1);
                    }
                }
            }
            catch { }
            finally { try { cmd.Dispose(); } catch { } }
        }

        // CSV via DuckDB COPY. Schema:
        //   unix_ms,open,high,low,close,volume,contract
        // Contract is appended at the end (vs. the v1.8 6-column format) so
        // column-name-aware readers (pandas, etc.) still work; positional
        // readers expecting exactly 6 cols would need a small update.
        // ORDER BY unix_ms so the file is monotonic-in-time.
        private void ExportToCsv(DuckDBConnection connection, string tableName, string symbol, string priceType, string outPath)
        {
            string escapedPath = outPath.Replace("'", "''");
            string sql = $@"COPY (
                SELECT unix_ms, open, high, low, close, volume, contract
                FROM {tableName}
                WHERE symbol = '{symbol}' AND price_type = '{priceType}'
                ORDER BY unix_ms
            ) TO '{escapedPath}' (HEADER, DELIMITER ',')";
            ExecuteNonQuery(connection, sql);
        }

        // Parquet carries the richer schema (symbol + price_type + contract +
        // timestamp) so a downstream loader can union files without reading
        // filenames and can group by contract to see roll boundaries.
        private void ExportToParquet(DuckDBConnection connection, string tableName, string symbol, string priceType, string outPath)
        {
            string escapedPath = outPath.Replace("'", "''");
            string sql = $@"COPY (
                SELECT symbol, contract, price_type, unix_ms, timestamp, open, high, low, close, volume
                FROM {tableName}
                WHERE symbol = '{symbol}' AND price_type = '{priceType}'
                ORDER BY unix_ms
            ) TO '{escapedPath}' (FORMAT PARQUET)";
            ExecuteNonQuery(connection, sql);
        }
    }
}
