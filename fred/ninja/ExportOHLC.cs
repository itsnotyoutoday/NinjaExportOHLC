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

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Export ALL OHLC history from all contracts";
                Name = "ExportOHLC";
            }
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

        private List<OHLCBar> GetOHLCFromContract(Instrument instrument, BarsPeriodType periodType, int period,
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

        private class OHLCBar
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
