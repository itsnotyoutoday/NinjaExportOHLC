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
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    /// <summary>
    /// Exports ALL available OHLC history by scanning ALL contract months
    /// and merging them into continuous data.
    ///
    /// Output: Up to 3 CSV files with Unix timestamps (milliseconds)
    ///   - {Symbol}_Last_OHLC_{timestamp}.csv (trade prices)
    ///   - {Symbol}_Bid_OHLC_{timestamp}.csv (bid prices, if available)
    ///   - {Symbol}_Ask_OHLC_{timestamp}.csv (ask prices, if available)
    ///
    /// CSV Columns: unix_ms,open,high,low,close,volume
    ///
    /// Note: Bid/Ask data availability depends on your data provider.
    /// Some providers only supply Last (trade) data.
    ///
    /// Version: 1.2
    /// Last Updated: 2025-01-13
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
        private ComboBox tfCombo;
        private TextBox periodBox;
        private TextBox outputBox;
        private Button exportBtn;
        private TextBlock statusBlock;
        private ScrollViewer statusScroll;
        private ProgressBar progressBar;
        private bool running;

        public ExportOHLCWindow()
        {
            Title = "Export OHLC - All Contracts";
            Width = 520;
            Height = 550;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var stack = new StackPanel { Margin = new Thickness(20) };

            // Title
            stack.Children.Add(new TextBlock
            {
                Text = "Export ALL OHLC History",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 5)
            });
            stack.Children.Add(new TextBlock
            {
                Text = "Scans ALL contract months and merges into continuous data",
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 20)
            });

            // Symbol input (root symbol like MYM, MES, etc)
            stack.Children.Add(new TextBlock { Text = "Symbol Root:", FontWeight = FontWeights.SemiBold });
            symbolBox = new TextBox { Text = "MYM", Margin = new Thickness(0, 5, 0, 5), Padding = new Thickness(8), FontSize = 14 };
            stack.Children.Add(symbolBox);
            stack.Children.Add(new TextBlock
            {
                Text = "Enter root symbol only (MYM, MES, MNQ, ES, NQ, etc.)\nWill scan all contract months automatically.",
                Foreground = Brushes.Gray,
                FontSize = 10,
                Margin = new Thickness(0, 0, 0, 15)
            });

            // Timeframe
            stack.Children.Add(new TextBlock { Text = "Timeframe:", FontWeight = FontWeights.SemiBold });
            var tfStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 15) };
            periodBox = new TextBox { Text = "1", Width = 60, Padding = new Thickness(8), FontSize = 14 };
            tfCombo = new ComboBox { Width = 110, Margin = new Thickness(10, 0, 0, 0), FontSize = 14 };
            tfCombo.Items.Add("Minute");
            tfCombo.Items.Add("Tick");
            tfCombo.Items.Add("Day");
            tfCombo.SelectedIndex = 0;
            tfStack.Children.Add(periodBox);
            tfStack.Children.Add(tfCombo);
            stack.Children.Add(tfStack);

            // Output folder
            stack.Children.Add(new TextBlock { Text = "Output Folder:", FontWeight = FontWeights.SemiBold });
            outputBox = new TextBox
            {
                Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader_OHLC"),
                Margin = new Thickness(0, 5, 0, 20),
                Padding = new Thickness(8)
            };
            stack.Children.Add(outputBox);

            // Export button
            exportBtn = new Button
            {
                Content = "EXPORT ALL CONTRACTS",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(20, 15, 20, 15),
                Background = new SolidColorBrush(Color.FromRgb(0, 130, 80)),
                Foreground = Brushes.White
            };
            exportBtn.Click += OnExportClick;
            stack.Children.Add(exportBtn);

            // Progress
            progressBar = new ProgressBar { Height = 8, Margin = new Thickness(0, 15, 0, 10), Visibility = Visibility.Collapsed };
            stack.Children.Add(progressBar);

            // Status log
            statusScroll = new ScrollViewer
            {
                Height = 140,
                Background = new SolidColorBrush(Color.FromRgb(25, 25, 30)),
                Padding = new Thickness(10),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            statusBlock = new TextBlock
            {
                Text = "Ready. Enter symbol root and click Export.",
                Foreground = Brushes.LightGreen,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            };
            statusScroll.Content = statusBlock;
            stack.Children.Add(statusScroll);

            Content = stack;
        }

        private void OnExportClick(object sender, RoutedEventArgs e)
        {
            if (running) return;

            string symbolRoot = symbolBox.Text.Trim().ToUpper();
            if (string.IsNullOrEmpty(symbolRoot)) { Log("Enter a symbol root", true); return; }

            if (!int.TryParse(periodBox.Text.Trim(), out int period) || period < 1)
            { Log("Invalid period", true); return; }

            running = true;
            exportBtn.IsEnabled = false;
            progressBar.Visibility = Visibility.Visible;
            progressBar.IsIndeterminate = true;
            statusBlock.Text = ""; // Clear log

            string tf = tfCombo.SelectedItem.ToString();
            string outDir = outputBox.Text.Trim();

            ThreadPool.QueueUserWorkItem(_ => RunExport(symbolRoot, period, tf, outDir));
        }

        private void RunExport(string symbolRoot, int period, string tfType, string outDir)
        {
            try
            {
                Log($"Scanning for all {symbolRoot} contracts...");

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
                    Finish();
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
                    var lastBars = GetOHLCFromContract(contract, periodType, period, MarketDataType.Last);
                    if (lastBars.Count > 0)
                    {
                        Log($"  Last: {lastBars.Count:N0} bars");
                        allLastBars.AddRange(lastBars);
                    }

                    // Get Bid OHLC
                    var bidBars = GetOHLCFromContract(contract, periodType, period, MarketDataType.Bid);
                    if (bidBars.Count > 0)
                    {
                        Log($"  Bid: {bidBars.Count:N0} bars");
                        allBidBars.AddRange(bidBars);
                    }

                    // Get Ask OHLC
                    var askBars = GetOHLCFromContract(contract, periodType, period, MarketDataType.Ask);
                    if (askBars.Count > 0)
                    {
                        Log($"  Ask: {askBars.Count:N0} bars");
                        allAskBars.AddRange(askBars);
                    }
                }

                if (allLastBars.Count == 0 && allBidBars.Count == 0 && allAskBars.Count == 0)
                {
                    Log("\nNo data retrieved from any contract!", true);
                    Finish();
                    return;
                }

                // Merge and deduplicate (sort by time, keep one bar per timestamp)
                Log($"\nMerging bars...");

                var mergedLast = MergeAndDedupe(allLastBars);
                var mergedBid = MergeAndDedupe(allBidBars);
                var mergedAsk = MergeAndDedupe(allAskBars);

                Log($"After merge - Last: {mergedLast.Count:N0}, Bid: {mergedBid.Count:N0}, Ask: {mergedAsk.Count:N0}");

                // Write CSV files
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                int filesWritten = 0;

                if (mergedLast.Count > 0)
                {
                    string lastFile = Path.Combine(outDir, $"{symbolRoot}_Last_OHLC_{ts}.csv");
                    WriteOHLCFile(lastFile, mergedLast);
                    Log($"\n✓ Last: {mergedLast.Count:N0} bars → {Path.GetFileName(lastFile)}");
                    filesWritten++;
                }

                if (mergedBid.Count > 0)
                {
                    string bidFile = Path.Combine(outDir, $"{symbolRoot}_Bid_OHLC_{ts}.csv");
                    WriteOHLCFile(bidFile, mergedBid);
                    Log($"✓ Bid: {mergedBid.Count:N0} bars → {Path.GetFileName(bidFile)}");
                    filesWritten++;
                }

                if (mergedAsk.Count > 0)
                {
                    string askFile = Path.Combine(outDir, $"{symbolRoot}_Ask_OHLC_{ts}.csv");
                    WriteOHLCFile(askFile, mergedAsk);
                    Log($"✓ Ask: {mergedAsk.Count:N0} bars → {Path.GetFileName(askFile)}");
                    filesWritten++;
                }

                // Summary - use whichever has data for date range
                var primaryBars = mergedLast.Count > 0 ? mergedLast : (mergedBid.Count > 0 ? mergedBid : mergedAsk);
                var minDate = primaryBars.Min(b => b.Time);
                var maxDate = primaryBars.Max(b => b.Time);

                Log("\n══════════════════════════════════════════");
                Log("EXPORT COMPLETE!");
                Log($"Symbol: {symbolRoot}");
                Log($"Contracts processed: {contracts.Count}");
                Log($"Files created: {filesWritten}");
                Log($"Date range: {minDate:yyyy-MM-dd} to {maxDate:yyyy-MM-dd}");
                Log($"Output: {outDir}");
                Log("══════════════════════════════════════════");

                Finish();
            }
            catch (Exception ex)
            {
                Log($"\nERROR: {ex.Message}", true);
                Finish();
            }
        }

        private List<OHLCBar> GetOHLCFromContract(Instrument instrument, BarsPeriodType periodType, int period, MarketDataType mdType)
        {
            var bars = new List<OHLCBar>();

            try
            {
                var request = new BarsRequest(instrument, DateTime.MinValue, DateTime.Now);
                request.BarsPeriod = new BarsPeriod
                {
                    BarsPeriodType = periodType,
                    Value = period,
                    MarketDataType = mdType  // Set MarketDataType on BarsPeriod, not BarsRequest
                };
                request.TradingHours = TradingHours.Get("Default 24 x 7");

                var wait = new ManualResetEvent(false);

                request.Request((req, err, msg) =>
                {
                    if (err == ErrorCode.NoError && req.Bars != null)
                    {
                        for (int i = 0; i < req.Bars.Count; i++)
                        {
                            bars.Add(new OHLCBar
                            {
                                Time = req.Bars.GetTime(i),
                                Open = req.Bars.GetOpen(i),
                                High = req.Bars.GetHigh(i),
                                Low = req.Bars.GetLow(i),
                                Close = req.Bars.GetClose(i),
                                Volume = req.Bars.GetVolume(i)
                            });
                        }
                    }
                    wait.Set();
                });

                wait.WaitOne(TimeSpan.FromMinutes(5));
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
    }
}
