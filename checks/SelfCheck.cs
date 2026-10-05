using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CodexUsageTray
{
    internal class SelfCheck
    {
        private static readonly string ProjectRoot = Directory.GetParent(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)).FullName;

        [STAThread]
        internal static int Main(string[] args)
        {
            try
            {
                if (args.Length >= 3 && (args[0] == "--ui-check" || args[0] == "--render-check"))
                    Thread.CurrentThread.CurrentCulture = Thread.CurrentThread.CurrentUICulture = new CultureInfo(args[2]);
                if (args.Length >= 2 && args[0] == "--fake-server") return FakeServer(args[1]);
                if (args.Length >= 1 && args[0] == "--integration-check") return IntegrationCheck();
                if (args.Length >= 1 && args[0] == "--live-check") return LiveCheck();
                if (args.Length >= 2 && args[0] == "--ui-check") return UiCheck(args[1]);
                if (args.Length >= 2 && args[0] == "--render-check") return RenderCheck(args[1]);
                if (args.Length == 1 && args[0] == "--reopen-check") return ReopenCheck();
                if (args.Length >= 2 && args[0] == "--icon-check")
                {
                    CheckIconRaster();
                    SaveIconPreview(args[1]);
                    Console.WriteLine("PASS: icon raster and preview");
                    return 0;
                }
                if (args.Length >= 2 && args[0] == "--live-series") return LiveSeries(args[1]);
                if (args.Length >= 2 && args[0] == "--measure") return MeasureProduct(args[1]);
                if (args.Length >= 2 && args[0] == "--measure-visible") return MeasureProduct(args[1], true);
                if (args.Length >= 2 && args[0] == "--joint-launch-check") return MeasureProduct(args[1], false, true);
                if (args.Length != 0) throw new InvalidOperationException("Unknown or incomplete check arguments; UI/series/measure require an existing verification directory");
                Thread.CurrentThread.CurrentCulture = Thread.CurrentThread.CurrentUICulture = new CultureInfo("ru-RU");
                CheckParserPriorityAndMetadata();
                CheckParserFallbackAndWindows();
                CheckParserMalformedValues();
                CheckIconRaster();
                CheckPopupLayout();
                CheckPresentation();
                CheckOutputBoundary();
                CheckSubscriptionMetadata();
                Console.WriteLine("PASS: SelfCheck");
                return 0;
            }
            catch (Exception exception)
            {
                Console.WriteLine("FAIL:" + exception.Message);
                return 1;
            }
        }

        private static int FakeServer(string scenario)
        {
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                if (scenario == "hang-initialize") continue;
                if (line.IndexOf("\"id\":1", StringComparison.Ordinal) >= 0)
                {
                    if (scenario == "broken-json") { Console.WriteLine("{"); Console.Out.Flush(); return 0; }
                    Console.WriteLine("{\"method\":\"notice\"}");
                    Console.WriteLine("{\"id\":1,\"result\":{}}"); Console.Out.Flush();
                }
                else if (line.IndexOf("\"id\":2", StringComparison.Ordinal) >= 0)
                {
                    if (scenario == "hang-query") continue;
                    if (scenario == "eof") return 0;
                    if (scenario == "oversized") Console.WriteLine(new string('x', 4 * 1024 * 1024 + 1));
                    if (scenario == "stderr") { Console.Error.Write(new string('e', 200 * 1024)); Console.Error.Flush(); }
                    if (scenario == "auth401") { Console.WriteLine("{\"id\":2,\"error\":{\"code\":401,\"message\":\"unauthorized\"}}"); }
                    else if (scenario == "forbidden403") { Console.WriteLine("{\"id\":2,\"error\":{\"code\":403,\"message\":\"forbidden\"}}"); }
                    else if (scenario == "service") { Console.WriteLine("{\"id\":2,\"error\":{\"code\":500,\"message\":\"service\"}}"); }
                    else
                    {
                        Console.WriteLine("{\"id\":99,\"result\":{}}");
                        Console.WriteLine("{\"method\":\"account/rateLimits/updated\",\"params\":{}}");
                        Console.WriteLine("{\"id\":2,\"method\":\"foreign/request\",\"params\":{}}");
                        string plan = scenario.StartsWith("account-", StringComparison.Ordinal) ? "" : "\"planType\":\"plus\",";
                        Console.WriteLine("{\"id\":2,\"result\":{" + plan + "\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":25,\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":88,\"windowDurationMins\":10080}}}}}");
                    }
                    Console.Out.Flush();
                }
                else if (line.IndexOf("\"id\":3", StringComparison.Ordinal) >= 0)
                {
                    if (scenario == "account-hang") continue;
                    if (scenario == "account-error") Console.WriteLine("{\"id\":3,\"error\":{\"code\":500,\"message\":\"optional account unavailable\"}}");
                    else Console.WriteLine("{\"id\":3,\"result\":{\"account\":{\"planType\":\"pro\"}}}");
                    Console.Out.Flush();
                }
            }
            return 0;
        }

        private static int IntegrationCheck()
        {
            string[] scenarios = new[] { "success", "auth401", "forbidden403", "service", "broken-json", "eof", "oversized", "stderr", "account-error", "account-hang", "account-success", "hang-initialize", "hang-query" };
            foreach (string scenario in scenarios)
            {
                ProcessStartInfo start = CodexLocator.Hidden(Assembly.GetExecutingAssembly().Location, "--fake-server " + scenario);
                using (CodexReader reader = new CodexReader())
                {
                    using (CancellationTokenSource stop = new CancellationTokenSource())
                    {
                    try
                    {
                        Task<UsageSnapshot> task = reader.ReadAsync(start, stop.Token, 1000);
                        if (scenario == "hang-query" || scenario == "hang-initialize")
                        {
                            try { task.GetAwaiter().GetResult(); throw new InvalidOperationException("timeout " + scenario); }
                            catch (UsageReadException) { }
                        }
                        else if (scenario == "success" || scenario == "stderr" || scenario.StartsWith("account-", StringComparison.Ordinal))
                        {
                            UsageSnapshot snapshot = task.GetAwaiter().GetResult();
                            Check(snapshot.FiveHour.Remaining == 75 && snapshot.Weekly.Remaining == 12, scenario + " result");
                            Check(snapshot.Subscription == null, "fake does not read saved auth");
                            if (scenario == "account-success") Check(snapshot.PlanType == "pro", "optional plan");
                            if (scenario == "account-error" || scenario == "account-hang") Check(snapshot.PlanType == null, "optional failure preserves limits");
                        }
                        else
                        {
                            try { task.GetAwaiter().GetResult(); throw new InvalidOperationException("error " + scenario); }
                            catch (UsageReadException) { }
                        }
                    }
                    finally { stop.Cancel(); }
                    CheckCleanup(reader, scenario);
                    Console.WriteLine("PASS: " + scenario);
                    }
                }
            }
            CheckOverlapAndCancellation();
            using (Process control = new Process { StartInfo = CodexLocator.Hidden(Assembly.GetExecutingAssembly().Location, "--fake-server hang-query") })
            {
                control.Start();
                try
                {
                    using (CodexReader closing = new CodexReader())
                    {
                        Task<UsageSnapshot> request = closing.ReadAsync(CodexLocator.Hidden(Assembly.GetExecutingAssembly().Location, "--fake-server hang-query"), CancellationToken.None, 1500);
                        Thread.Sleep(100); closing.Dispose();
                        try { request.GetAwaiter().GetResult(); throw new InvalidOperationException("disposed request returned success"); }
                        catch (OperationCanceledException) { }
                        catch (UsageReadException) { }
                        CheckCleanup(closing, "dispose during read");
                    }
                    Check(!control.HasExited, "unrelated control process untouched");
                }
                finally { control.StandardInput.Close(); if (!control.WaitForExit(500)) CodexLocator.Stop(control); }
            }
            using (CodexReader missing = new CodexReader())
            {
                try { missing.ReadAsync(CodexLocator.Hidden(Path.Combine(ProjectRoot, "checks", "missing-codex.exe"), ""), CancellationToken.None, 1000).GetAwaiter().GetResult(); throw new InvalidOperationException("missing CLI accepted"); }
                catch (UsageReadException) { }
                Check(missing.OwnedProcessId == 0, "missing CLI cleanup");
            }
            Console.WriteLine("PASS: integration");
            return 0;
        }

        private static void CheckOverlapAndCancellation()
        {
            ProcessStartInfo start = CodexLocator.Hidden(Assembly.GetExecutingAssembly().Location, "--fake-server hang-query");
            using (CodexReader reader = new CodexReader())
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                Task<UsageSnapshot> first = reader.ReadAsync(start, stop.Token, 1200);
                Thread.Sleep(100);
                Task<UsageSnapshot> second = reader.ReadAsync(start, stop.Token, 1200);
                try { second.GetAwaiter().GetResult(); throw new InvalidOperationException("overlap accepted"); }
                catch (UsageReadException) { }
                stop.Cancel();
                try { first.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                catch (UsageReadException) { }
                CheckCleanup(reader, "cancel");
            }
        }
        private static void CheckCleanup(CodexReader reader, string name)
        {
            Check(reader.OwnedProcessId == 0 && reader.LastMetrics != null && reader.LastMetrics.Exited, name + " owned cleanup");
            Check(Exited(reader.LastMetrics.ProcessId), name + " OS pid gone");
        }
        private static bool Exited(int id)
        {
            if (id == 0) return true;
            try { using (Process process = Process.GetProcessById(id)) return process.HasExited; }
            catch (ArgumentException) { return true; }
        }
        private static int LiveCheck()
        {
            using (CodexReader reader = new CodexReader())
            {
                UsageSnapshot data = reader.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
                CheckCleanup(reader, "live");
                ReadMetrics metrics = reader.LastMetrics;
                Console.WriteLine("PASS: live; plan={0}; 5h={1}; weekly={2}; resets={3}; records={4}; savedSubscription={5}; localZone={6}",
                    data.PlanType ?? "unknown", TrayIconRenderer.FormatPercent(data.FiveHour.Remaining), TrayIconRenderer.FormatPercent(data.Weekly.Remaining),
                    data.AvailableResetCount.HasValue ? data.AvailableResetCount.Value.ToString(CultureInfo.InvariantCulture) : "unknown",
                    data.Credits.Count, data.Subscription != null, TimeZoneInfo.Local.Id);
                Console.WriteLine("CLI: responseMs={0:F1}; cpuMs={1:F1}; peakWorkingMiB={2:F2}; privateAtResponseMiB={3:F2}; cleanupMs={4:F1}; exited={5}",
                    metrics.ResponseMilliseconds, metrics.CpuMilliseconds, metrics.PeakWorkingSet / 1048576d,
                    metrics.PrivateAtResponse / 1048576d, metrics.CleanupMilliseconds, metrics.Exited);
            }
            return 0;
        }
        private static int LiveSeries(string directory)
        {
            using (StreamWriter csv = new StreamWriter(OutputPath(directory, "CLI-METRICS.csv"), false, new UTF8Encoding(false)))
            using (CodexReader reader = new CodexReader())
            {
                csv.WriteLine("Cycle,ResponseMilliseconds,CpuMilliseconds,PeakWorkingSetBytes,PrivateAtResponseBytes,PrivatePeakSampledBytes,CleanupMilliseconds,Exited");
                for (int cycle = 1; cycle <= 5; cycle++)
                {
                    long privatePeak = 0;
                    Task<UsageSnapshot> task = reader.ReadAsync(CancellationToken.None);
                    while (!task.IsCompleted)
                    {
                        int id = reader.OwnedProcessId;
                        if (id != 0)
                        {
                            try { using (Process child = Process.GetProcessById(id)) privatePeak = Math.Max(privatePeak, child.PrivateMemorySize64); }
                            catch (ArgumentException) { }
                            catch (InvalidOperationException) { }
                        }
                        Thread.Sleep(50);
                    }
                    UsageSnapshot data = task.GetAwaiter().GetResult();
                    Check(data.FiveHour.Remaining.HasValue && data.Weekly.Remaining.HasValue, "live windows");
                    CheckCleanup(reader, "live series");
                    ReadMetrics metrics = reader.LastMetrics;
                    csv.WriteLine(String.Format(CultureInfo.InvariantCulture, "{0},{1:F3},{2:F3},{3},{4},{5},{6:F3},{7}",
                        cycle, metrics.ResponseMilliseconds, metrics.CpuMilliseconds, metrics.PeakWorkingSet,
                        metrics.PrivateAtResponse, privatePeak, metrics.CleanupMilliseconds, metrics.Exited));
                    csv.Flush();
                    Console.WriteLine("PASS: live cycle " + cycle);
                }
            }
            return 0;
        }
        private static int MeasureProduct(string directory, bool visibleOnly = false, bool withCodex = false)
        {
            string executable = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "CodexUsageTray.exe");
            if (!File.Exists(executable)) executable = Path.Combine(ProjectRoot, "CodexUsageTray.exe");
            executable = Path.GetFullPath(executable);
            Check(executable.StartsWith(ProjectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "product boundary");
            Native.EnableDpi();
            Point previousCursor = Cursor.Position;
            string arguments = withCodex ? "--with-codex" : "";
            using (Process utility = new Process { StartInfo = new ProcessStartInfo(executable) { Arguments = arguments, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden } })
            using (StreamWriter csv = new StreamWriter(OutputPath(directory, withCodex ? "JOINT-LAUNCH.csv" : visibleOnly ? "METRICS-VISIBLE-2.csv" : "METRICS.csv"), false, new UTF8Encoding(false)))
            {
                csv.WriteLine("Phase,Cycle,ElapsedSeconds,Pid,WorkingSetBytes,PrivateBytes,CpuTotalMilliseconds,Handles,Gdi,User,OwnedCliCount,CardVisible");
                utility.Start();
                Console.WriteLine("Measurement product pid=" + utility.Id);
                Stopwatch elapsed = Stopwatch.StartNew();
                IntPtr tray = IntPtr.Zero;
                IntPtr card = IntPtr.Zero;
                int lastCycle = 0;
                int lastSecond = -1;
                bool duplicateChecked = false;
                try
                {
                    while (elapsed.Elapsed.TotalSeconds < (withCodex ? 10 : visibleOnly ? 180 : 480))
                    {
                        Check(!utility.HasExited, "product stayed running");
                        double seconds = elapsed.Elapsed.TotalSeconds;
                        int cycle = (int)(seconds / 60) + (visibleOnly ? 6 : 1);
                        if (tray == IntPtr.Zero) tray = OwnWindow(utility.Id, "Codex Usage Tray message window");
                        Native.IconIdentifier initializedId = new Native.IconIdentifier { Size = 40, Window = tray, Id = 1 };
                        Native.Rect initializedRect;
                        if (tray == IntPtr.Zero || Native.ShellGetRect(ref initializedId, out initializedRect) != 0)
                        {
                            Check(seconds < 10, "product tray initialization deadline");
                            Thread.Sleep(50); continue;
                        }
                        if (tray != IntPtr.Zero && !duplicateChecked)
                        {
                            using (Process duplicate = Process.Start(new ProcessStartInfo(executable) { Arguments = arguments, UseShellExecute = false, CreateNoWindow = true }))
                                Check(duplicate.WaitForExit(5000) && duplicate.ExitCode == 0, "second product launch exits");
                            duplicateChecked = true;
                        }
                        if (cycle != lastCycle)
                        {
                            if (cycle >= 6)
                            {
                                Check(tray != IntPtr.Zero, "product tray HWND");
                                Rectangle anchor = ExternalIconBounds(tray);
                                int point = ((anchor.Top & 0xffff) << 16) | (anchor.Left & 0xffff);
                                SendMessage(tray, TrayWindow.CallbackMessage, new IntPtr(point), new IntPtr((1 << 16) | 0x401));
                                card = OwnWindow(utility.Id, "Codex Usage Tray");
                                Check(card != IntPtr.Zero, "product card HWND");
                                Native.Rect rect; Check(GetWindowRect(card, out rect), "product card rect");
                                if (!visibleOnly) Cursor.Position = new Point(rect.Left + 15, rect.Top + 15);
                                if (cycle == 6 && !visibleOnly)
                                {
                                    Thread.Sleep(150);
                                    using (Bitmap capture = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top))
                                    {
                                        using (Graphics graphics = Graphics.FromImage(capture)) graphics.CopyFromScreen(new Point(rect.Left, rect.Top), Point.Empty, capture.Size);
                                        capture.Save(OutputPath(directory, "card-live.png"), System.Drawing.Imaging.ImageFormat.Png);
                                    }
                                }
                            }
                            Console.WriteLine("Measurement cycle " + cycle + " / " + (cycle <= 5 ? "hidden" : "visible"));
                            lastCycle = cycle;
                        }
                        if (visibleOnly && tray != IntPtr.Zero)
                            SendMessage(tray, TrayWindow.CallbackMessage, IntPtr.Zero, new IntPtr((1 << 16) | 0x200));
                        if (visibleOnly && seconds >= 10 && seconds < 10.2)
                        {
                            Native.Rect rect; GetWindowRect(card, out rect);
                            using (Bitmap capture = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top))
                            {
                                using (Graphics graphics = Graphics.FromImage(capture)) graphics.CopyFromScreen(new Point(rect.Left, rect.Top), Point.Empty, capture.Size);
                                capture.Save(OutputPath(directory, "card-live-visible.png"), System.Drawing.Imaging.ImageFormat.Png);
                            }
                        }
                        int second = (int)seconds;
                        if (second != lastSecond)
                        {
                            utility.Refresh();
                            List<int> children = OwnedChildren(utility.Id);
                            Check(children.Count <= 1, "product no overlapping CLI");
                            bool visible = card != IntPtr.Zero && IsWindowVisible(card);
                            if (cycle >= 6) Check(visible, "card remains visible during measurement");
                            csv.WriteLine(String.Format(CultureInfo.InvariantCulture, "{0},{1},{2:F3},{3},{4},{5},{6:F3},{7},{8},{9},{10},{11}",
                                cycle <= 5 ? "hidden" : "visible", cycle, seconds, utility.Id, utility.WorkingSet64,
                                utility.PrivateMemorySize64, utility.TotalProcessorTime.TotalMilliseconds, utility.HandleCount,
                                GetGuiResources(utility.Handle, 0), GetGuiResources(utility.Handle, 1), children.Count, visible));
                            csv.Flush();
                            lastSecond = second;
                        }
                        Thread.Sleep(100);
                    }
                    if (withCodex) Check(duplicateChecked, "joint launch initializes the tray and exits the duplicate");
                }
                finally
                {
                    if (tray != IntPtr.Zero) SendMessage(tray, 0x10, IntPtr.Zero, IntPtr.Zero);
                    if (!utility.WaitForExit(5000)) { utility.Kill(); utility.WaitForExit(1000); }
                    Cursor.Position = previousCursor;
                }
                Check(utility.HasExited && utility.ExitCode == 0, "product clean exit");
                Check(OwnedChildren(utility.Id).Count == 0, "no CLI after product exit");
                Console.WriteLine(withCodex ? "PASS: joint startup, tray icon, duplicate launch and clean exit" :
                    visibleOnly ? "PASS: 3 visible minute cycles with synthetic hover; clean exit" :
                    "PASS: 5 hidden and 3 visible minute cycles; clean exit");
            }
            return 0;
        }
        private static Rectangle ExternalIconBounds(IntPtr window)
        {
            Native.IconIdentifier id = new Native.IconIdentifier { Size = 40, Window = window, Id = 1 };
            Native.Rect rect;
            Check(Native.ShellGetRect(ref id, out rect) == 0, "external Shell icon bounds");
            return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }
        private static IntPtr OwnWindow(int processId, string title)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr window, IntPtr param) {
                uint owner; GetWindowThreadProcessId(window, out owner);
                if (owner == processId)
                {
                    StringBuilder name = new StringBuilder(160);
                    GetWindowText(window, name, name.Capacity);
                    if (name.ToString() == title) { found = window; return false; }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }
        private static List<int> OwnedChildren(int processId)
        {
            List<int> children = new List<int>();
            IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot == new IntPtr(-1)) throw new InvalidOperationException("process snapshot unavailable");
            try
            {
                ProcessEntry entry = new ProcessEntry { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
                if (Process32First(snapshot, ref entry))
                {
                    do { if (entry.ParentId == processId && String.Equals(entry.Exe, "codex.exe", StringComparison.OrdinalIgnoreCase)) children.Add((int)entry.Id); }
                    while (Process32Next(snapshot, ref entry));
                }
            }
            finally { CloseHandle(snapshot); }
            return children;
        }
        private static void Pump(int milliseconds)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            do { Application.DoEvents(); Thread.Sleep(10); } while (elapsed.ElapsedMilliseconds < milliseconds);
        }
        private static string OutputPath(string directory, string name)
        {
            string path = Path.GetFullPath(Path.Combine(directory, name));
            Check(path.StartsWith(Path.Combine(ProjectRoot, "verification") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "output boundary");
            Check(Directory.Exists(Path.GetDirectoryName(path)), "output directory exists");
            return path;
        }
        private static void CheckOutputBoundary()
        {
            string directory = Path.Combine(ProjectRoot, "verification");
            if (Directory.Exists(directory))
                Check(OutputPath(directory, "boundary-check.txt") == Path.Combine(directory, "boundary-check.txt"),
                    "verification path follows the project directory");
            try { OutputPath(ProjectRoot, "boundary-check.txt"); }
            catch (InvalidOperationException exception)
            {
                Check(exception.Message == "output boundary", "outside output rejected before writing");
                return;
            }
            throw new InvalidOperationException("outside output accepted");
        }
        private static int UiCheck(string directory)
        {
            Native.EnableDpi();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Check(Marshal.SizeOf(typeof(Native.IconData)) == 976 && Marshal.SizeOf(typeof(Native.IconIdentifier)) == 40, "x64 Shell structures");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            UsageSnapshot data = Parse("{\"result\":{\"planType\":\"plus\",\"rateLimits\":{\"primary\":{\"usedPercent\":25,\"windowDurationMins\":300,\"resetsAt\":1900000000},\"secondary\":{\"usedPercent\":88,\"windowDurationMins\":10080,\"resetsAt\":1900003600}},\"rateLimitResetCredits\":{\"availableCount\":7,\"credits\":[]}}}");
            data.ReceivedAtUtc = now;
            Check(!PopupForm.IsStale(data, null, now.AddSeconds(119)) && PopupForm.IsStale(data, null, now.AddSeconds(120)), "stale 119/120");
            Check(PopupForm.IsStale(data, "offline", now), "error stale");
            Rectangle work = new Rectangle(-1920, -200, 1920, 1032);
            Check(work.Contains(PopupForm.Place(new Rectangle(-2, 825, 32, 32), new Size(360, 1600), work)), "negative monitor geometry");
            Point previous = Cursor.Position;
            try
            {
            using (TrayContext context = new TrayContext(false))
            {
                ContextMenuStrip menu = (ContextMenuStrip)typeof(TrayContext).GetField("menu", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(context);
                Check(menu.Items.Count == 1 && menu.Items[0].Text == UiText.T("Выход", "Exit"), "exit-only tray menu");
                context.SetSnapshot(data, null);
                Pump(100);
                Check(context.Tray.Registered, "real Shell registration");
                Rectangle anchor = context.Tray.IconBounds();
                Check(!anchor.IsEmpty, "real Shell icon rect");
                Console.WriteLine("Shell: rect={0}; work={1}", anchor, Screen.FromPoint(anchor.Location).WorkingArea);
                Native.IconData version = new Native.IconData { Size = 976, Window = context.Tray.Handle, Id = 1, Version = 4, Tip = "", Info = "", InfoTitle = "" };
                Check(Native.ShellNotify(4, ref version), "Shell version 4 accepted");
                using (Bitmap iconCapture = new Bitmap(anchor.Width, anchor.Height))
                {
                    using (Graphics graphics = Graphics.FromImage(iconCapture)) graphics.CopyFromScreen(anchor.Location, Point.Empty, anchor.Size);
                    iconCapture.Save(OutputPath(directory, "tray.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                List<int> callbacks = new List<int>();
                Action<int, Point> callback = context.Tray.Callback;
                context.Tray.Callback = delegate(int code, Point point) { callbacks.Add(code); callback(code, point); };
                context.Popup.ShowCard(anchor, false); Pump(50);
                Control reusedRow = context.Popup.Controls[0].Controls[0];
                context.Popup.Hide(); context.Popup.ShowCard(anchor, false);
                Check(Object.ReferenceEquals(reusedRow, context.Popup.Controls[0].Controls[0]), "hover reuses card controls");
                Check(context.Popup.CreditRows == 0 && !context.Popup.ScrollNeeded, "empty card");
                CheckPopupStyles(context.Popup, Native.DpiAt(anchor.Location));
                Check(Screen.FromControl(context.Popup).WorkingArea.Right - context.Popup.Right == 6, "card right-edge gap");
                data.Subscription = new SubscriptionMetadata { LastCheckedUtc = now };
                context.SetSnapshot(data, null);
                bool checkedLabel = false;
                foreach (Control control in context.Popup.Controls[0].Controls) if (control.Text.StartsWith("Проверка срока:", StringComparison.Ordinal)) checkedLabel = true;
                Check(!checkedLabel, "check date removed from compact card");
                data.Subscription = null; context.SetSnapshot(data, null);
                Capture(context.Popup, OutputPath(directory, "card-empty.png"));
                UsageSnapshot compact = new UsageSnapshot {
                    PlanType = "plus", ReceivedAtUtc = now, AvailableResetCount = 3,
                    FiveHour = new UsageWindow { Remaining = 29, ResetUtc = new DateTimeOffset(2026, 10, 2, 19, 1, 0, TimeSpan.FromHours(5)) },
                    Weekly = new UsageWindow { Remaining = 22, ResetUtc = new DateTimeOffset(2026, 10, 4, 11, 13, 0, TimeSpan.FromHours(5)) },
                    Credits = new List<ResetCredit>(),
                    Subscription = new SubscriptionMetadata { ActiveUntilUtc = new DateTimeOffset(2026, 10, 16, 18, 22, 0, TimeSpan.FromHours(5)) }
                };
                foreach (int day in new[] { 19, 20, 29 }) compact.Credits.Add(new ResetCredit {
                    ExpiresUtc = new DateTimeOffset(2026, 10, day, 23, 56, 0, TimeSpan.FromHours(5))
                });
                context.SetSnapshot(compact, null); Pump(50);
                CheckPopupStyles(context.Popup, Native.DpiAt(anchor.Location));
                Check(context.Popup.CreditRows == 3 && !context.Popup.ScrollNeeded, "compact three-reset card");
                if (!UiText.Russian)
                {
                    bool header = false, reset = false, subscription = false;
                    foreach (Control row in context.Popup.Controls[0].Controls)
                    {
                        if (row.Text == "5 hours - 29%") header = true;
                        if (row.Text == "Limit resets: 3") reset = true;
                        if (row.Text.StartsWith("Subscription until: ", StringComparison.Ordinal)) subscription = true;
                        foreach (char character in row.Text) Check(character < 0x400 || character > 0x4ff, "English card has no Cyrillic");
                    }
                    Check(header && reset && subscription && PopupForm.Countdown(now.AddDays(2).AddHours(8).AddMinutes(53), now) == "2 days 8 h 53 min", "English compact card and countdown");
                    try { Parse("{"); throw new InvalidOperationException("malformed JSON accepted"); }
                    catch (UsageReadException exception) { Check(exception.Message == "Malformed JSON from Codex", "English error message"); }
                }
                Capture(context.Popup, OutputPath(directory, "card-compact.png"));
                context.SetSnapshot(data, null);
                int narrowWidth = context.Popup.Width;
                data.PlanType = new String('W', 70);
                context.SetSnapshot(data, null); Pump(50);
                Check(context.Popup.Width > narrowWidth, "card width grows with text");
                Capture(context.Popup, OutputPath(directory, "card-wide.png"));
                data.PlanType = "plus"; context.SetSnapshot(data, null);
                Check(context.Popup.Width == narrowWidth, "card width shrinks with text");
                for (int i = 0; i < 70; i++) data.Credits.Add(new ResetCredit { Title = "Ручной сброс " + (i + 1), Description = "Полный сброс лимитов.", ExpiresUtc = now.AddDays(i + 1) });
                data.AvailableResetCount = 70;
                context.SetSnapshot(data, null); Pump(50);
                Check(context.Popup.CreditRows == 70 && context.Popup.ScrollNeeded, "all credits with scrollbar");
                Check(Screen.FromControl(context.Popup).WorkingArea.Contains(context.Popup.Bounds), "card inside work area");
                Capture(context.Popup, OutputPath(directory, "card-long.png"));
                data.Credits.Clear();
                data.AvailableResetCount = 7;
                context.SetSnapshot(data, UiText.T("Синтетическая ошибка соединения", "Synthetic connection error")); Pump(50);
                Check(data.FiveHour.Remaining == 75 && data.AvailableResetCount == 7, "error keeps snapshot");
                Capture(context.Popup, OutputPath(directory, "card-stale.png"));
                context.Popup.Hide(); context.SetSnapshot(data, null);
                int registrations = context.Tray.RegistrationCount;
                SendMessage(context.Tray.Handle, context.Tray.TaskbarCreated, IntPtr.Zero, IntPtr.Zero);
                Check(context.Tray.Registered && context.Tray.RegistrationCount == registrations + 1, "TaskbarCreated simulated");
                Cursor.Position = new Point(100, 100); Pump(200);
                Cursor.Position = new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2);
                MouseEvent(1, 1, 0, 0, IntPtr.Zero); Pump(700);
                if (!context.Popup.Visible) { MouseEvent(2, 0, 0, 0, IntPtr.Zero); MouseEvent(4, 0, 0, 0, IntPtr.Zero); Pump(300); }
                anchor = context.Tray.IconBounds();
                Console.WriteLine("Shell visible icon rect: " + anchor);
                Cursor.Position = new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2);
                Console.WriteLine("Cursor: actual={0}; interactive={1}", Cursor.Position, SystemInformation.UserInteractive);
                MouseEvent(1, 1, 0, 0, IntPtr.Zero);
                Pump(1500);
                Console.WriteLine("Shell hover callbacks: " + String.Join(",", callbacks));
                Console.WriteLine("Hover state: cursor={0}; icon={1}; cached={2}; card={3}; visible={4}", Cursor.Position,
                    context.Tray.IconBounds(), typeof(TrayContext).GetField("hoverAnchor", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(context),
                    context.Popup.Bounds, context.Popup.Visible);
                Check(context.Popup.Visible, "actual Shell hover opens");
                Rectangle card = context.Popup.Bounds;
                Cursor.Position = new Point(card.Left + 30, card.Top + 30); Pump(300);
                Check(context.Popup.Visible, "icon to card hover holds");
                Cursor.Position = new Point(100, 100); Pump(150);
                Check(context.Popup.Visible, "hover grace");
                Pump(700); Check(!context.Popup.Visible, "hover leave closes");
                context.Popup.ShowCard(anchor, true); context.Popup.Close(); Pump(30);
                Check(!context.Popup.Visible && !context.Popup.IsDisposed, "close hides reusable card");
                for (int i = 0; i < 10; i++) { context.Popup.ShowCard(anchor, false); context.Popup.Hide(); }
                uint gdi = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
                uint user = GetGuiResources(Process.GetCurrentProcess().Handle, 1);
                for (int i = 0; i < 100; i++)
                {
                    using (Icon image = TrayIconRenderer.Create(i % 101, 100 - i % 101, 16)) { }
                    context.Popup.ShowCard(anchor, false); context.Popup.Hide();
                }
                GC.Collect(); GC.WaitForPendingFinalizers(); Pump(30);
                uint afterGdi = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
                uint afterUser = GetGuiResources(Process.GetCurrentProcess().Handle, 1);
                Console.WriteLine("UI: monitorCount={0}; dpi={1}; GDI={2}->{3}; USER={4}->{5}", Screen.AllScreens.Length, Native.DpiAt(anchor.Location), gdi, afterGdi, user, afterUser);
                Check(afterGdi <= gdi + 2 && afterUser <= user + 2, "no GDI/USER growth over 100 cycles");
                context.ExitThread();
            }
            }
            finally { Cursor.Position = previous; }
            SaveIconPreview(directory);
            Console.WriteLine("PASS: UI");
            return 0;
        }
        private static void SaveIconPreview(string directory)
        {
            using (Bitmap sheet = new Bitmap(600, 210))
            using (Graphics graphics = Graphics.FromImage(sheet))
            using (Font caption = new Font("Segoe UI", 9))
            {
                graphics.Clear(Color.FromArgb(17, 24, 39));
                double[] values = { 100, 31, 30, 11, 10, 0 };
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                for (int i = 0; i < values.Length; i++)
                {
                    using (Icon small = TrayIconRenderer.Create(values[i], 100 - values[i], 16))
                    using (Bitmap pixels = small.ToBitmap())
                    {
                        graphics.DrawImageUnscaled(pixels, i * 100 + 35, 10);
                        graphics.DrawImage(pixels, new Rectangle(i * 100 + 15, 40, 64, 64), 0, 0, 16, 16, GraphicsUnit.Pixel);
                    }
                    using (Icon large = TrayIconRenderer.Create(values[i], 100 - values[i], 32)) graphics.DrawIconUnstretched(large, new Rectangle(i * 100 + 28, 118, 32, 32));
                    graphics.DrawString(values[i].ToString(CultureInfo.InvariantCulture) + "% / " + (100 - values[i]) + "%", caption, Brushes.White, i * 100 + 8, 172);
                }
                sheet.Save(OutputPath(directory, "icons.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        private static void Capture(Form form, string path)
        {
            form.Refresh(); Application.DoEvents();
            using (Bitmap image = new Bitmap(form.Width, form.Height))
            {
                using (Graphics graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(form.Location, Point.Empty, form.Size);
                image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        private static int ReopenCheck()
        {
            Native.EnableDpi(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            UsageSnapshot data = Parse("{\"result\":{\"planType\":\"plus\",\"rateLimits\":{}}}");
            data.FiveHour.Remaining = 29; data.FiveHour.ResetUtc = DateTimeOffset.UtcNow.AddMinutes(41);
            data.Weekly.Remaining = 22; data.Weekly.ResetUtc = DateTimeOffset.UtcNow.AddDays(2);
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            Rectangle anchor = new Rectangle(work.Right - 32, work.Bottom, 32, 48);
            using (PopupForm popup = new PopupForm())
            {
                popup.SetSnapshot(data, null);
                popup.ShowCard(anchor, false); Pump(30);
                Control[] rows = new Control[popup.Controls[0].Controls.Count];
                popup.Controls[0].Controls.CopyTo(rows, 0);
                IntPtr handle = popup.Handle;
                for (int cycle = 0; cycle < 2; cycle++)
                {
                    if (cycle == 0) popup.Hide(); else popup.Close();
                    Check(!popup.Visible && !popup.IsDisposed && popup.IsHandleCreated, "ordinary hide preserves the popup");
                    popup.ShowCard(anchor, false); Pump(30);
                    Check(popup.Visible && popup.Handle == handle && popup.Controls[0].Controls.Count == rows.Length,
                        "reopen retains the window");
                    for (int i = 0; i < rows.Length; i++)
                        Check(Object.ReferenceEquals(rows[i], popup.Controls[0].Controls[i]), "reopen reuses text controls");
                    Check(work.Right - popup.Right == 6 && work.Bottom - popup.Bottom == 6, "reopen preserves equal margins");
                }
                object[] escape = { new Message(), Keys.Escape };
                Check((bool)typeof(PopupForm).GetMethod("ProcessCmdKey", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(popup, escape) && !popup.Visible && !popup.IsDisposed, "Escape hides the reusable popup");
            }
            Console.WriteLine("PASS: reopen reuses window/text controls; Hide, Close, Escape; right/taskbar gap=6");
            return 0;
        }
        private static int RenderCheck(string directory)
        {
            Native.EnableDpi(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            UsageSnapshot data = Parse("{\"result\":{\"planType\":\"plus\",\"rateLimits\":{},\"rateLimitResetCredits\":{\"availableCount\":3,\"credits\":[]}}}");
            data.ReceivedAtUtc = now;
            data.FiveHour.Remaining = 29; data.FiveHour.ResetUtc = now.AddMinutes(41);
            data.Weekly.Remaining = 22; data.Weekly.ResetUtc = now.AddDays(2).AddHours(8).AddMinutes(53);
            foreach (int day in new[] { 19, 20, 29 }) data.Credits.Add(new ResetCredit { ExpiresUtc = new DateTimeOffset(2026, 10, day, 23, 56, 0, TimeSpan.FromHours(5)) });
            data.Subscription = new SubscriptionMetadata { ActiveUntilUtc = new DateTimeOffset(2026, 10, 16, 18, 22, 0, TimeSpan.FromHours(5)) };
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            using (PopupForm popup = new PopupForm())
            {
                popup.SetSnapshot(data, null);
                typeof(PopupForm).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(popup, new object[] { work, Native.DpiAt(work.Location) });
                CheckPopupStyles(popup, Native.DpiAt(work.Location));
                Check(popup.CreditRows == 3 && !popup.ScrollNeeded && work.Right - popup.Right == 6, "compact render layout");
                int style = GetWindowLong(popup.Handle, -16), extended = GetWindowLong(popup.Handle, -20);
                Check((style & 0x00800000) != 0 && (style & 0x00C00000) != 0x00C00000 &&
                    (style & 0x00080000) == 0 && (extended & 0x02000080) == 0x02000080, "native border without caption/buttons and composited drawing");
                int corners;
                int cornerResult = DwmGetWindowAttribute(popup.Handle, 33, out corners, 4);
                if (cornerResult == 0) Check(corners == 2, "Windows rounded-corner preference");
                bool title = false, resets = false, until = false;
                foreach (Control row in popup.Controls[0].Controls)
                {
                    if (row.Text == UiText.T("5 часов - 29%", "5 hours - 29%")) title = true;
                    if (row.Text == UiText.T("Сбросов лимитов: 3", "Limit resets: 3")) resets = true;
                    if (row.Text.StartsWith(UiText.T("Подписка до: ", "Subscription until: "), StringComparison.Ordinal)) until = true;
                    if (!UiText.Russian) foreach (char character in row.Text) Check(character < 0x400 || character > 0x4ff, "English render has no Cyrillic");
                }
                Check(title && resets && until, "localized compact render fields");
                using (Bitmap bitmap = new Bitmap(popup.Width, popup.Height))
                {
                    popup.DrawToBitmap(bitmap, new Rectangle(Point.Empty, popup.Size));
                    bitmap.Save(OutputPath(directory, "card.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                Check(!popup.Visible, "render check never shows a desktop window");
                Check(work.Right - popup.Right == 6, "native handle and bitmap rendering preserve the right gap");
                Console.WriteLine("PASS: render; language={0}; size={1}; rightGap={2}; DWM corner HRESULT={3}; corner={4}",
                    UiText.Russian ? "ru" : "en", popup.Size, work.Right - popup.Right, cornerResult, corners);
            }
            return 0;
        }
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flag);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint x, uint y, uint data, IntPtr extra);
        private delegate bool EnumWindowCallback(IntPtr window, IntPtr param);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr param);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Native.Rect rect);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            internal uint Size, Usage, Id;
            internal UIntPtr Heap;
            internal uint Module, Threads, ParentId;
            internal int Priority;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Exe;
        }

        private static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
        }

        private static UsageSnapshot Parse(string json)
        {
            return UsageParser.Parse(json, new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        }

        private static void CheckParserPriorityAndMetadata()
        {
            UsageSnapshot snapshot = Parse(
                "{\"result\":{\"planType\":\"pro\",\"rateLimitResetCredits\":{\"availableCount\":7," +
                "\"credits\":[{\"title\":null,\"description\":\"D\",\"type\":\"trial\",\"expiresAt\":0}]}," +
                "\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":25,\"windowDurationMins\":300,\"resetsAt\":0}," +
                "\"secondary\":{\"usedPercent\":88,\"windowDurationMins\":10080,\"resetsAt\":3600}}}," +
                "\"rateLimits\":{\"primary\":{\"usedPercent\":99,\"windowDurationMins\":300}}}}");
            Check(snapshot.FiveHour.Remaining.HasValue && snapshot.FiveHour.Remaining.Value == 75, "priority five-hour");
            Check(snapshot.Weekly.Remaining.HasValue && snapshot.Weekly.Remaining.Value == 12, "priority weekly");
            Check(snapshot.PlanType == "pro" && snapshot.ResetCreditsPresent, "metadata fields");
            Check(snapshot.AvailableResetCount.HasValue && snapshot.AvailableResetCount.Value == 7, "reset count");
            Check(snapshot.CreditsState == CreditsState.Array && snapshot.Credits.Count == 1, "credits array");
            Check(snapshot.Credits[0].ExpiresUtc.HasValue && snapshot.Credits[0].ExpiresUtc.Value ==
                new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero), "zero expiry epoch");
        }

        private static void CheckParserFallbackAndWindows()
        {
            UsageSnapshot snapshot = Parse(
                "{\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":10,\"windowDurationMins\":10080}," +
                "\"secondary\":{\"usedPercent\":20,\"windowDurationMins\":300}," +
                "\"other\":{\"usedPercent\":1,\"windowDurationMins\":999}}}}");
            Check(snapshot.FiveHour.Remaining == 80, "fallback swapped five-hour");
            Check(snapshot.Weekly.Remaining == 90, "fallback swapped weekly");
            snapshot = Parse("{\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":10,\"windowDurationMins\":300},\"secondary\":{\"usedPercent\":20,\"windowDurationMins\":300}}}}");
            Check(!snapshot.FiveHour.Remaining.HasValue, "duplicate duration unavailable");
            Check(!snapshot.Weekly.Remaining.HasValue, "unknown duration ignored");
            Check(snapshot.CreditsState == CreditsState.Missing && snapshot.Credits.Count == 0, "credits absent");
        }

        private static void CheckParserMalformedValues()
        {
            UsageSnapshot snapshot = Parse(
                "{\"result\":{\"rateLimitResetCredits\":{\"availableCount\":null,\"credits\":null},\"rateLimits\":{\"primary\":{\"usedPercent\":-20,\"windowDurationMins\":300}," +
                "\"secondary\":{\"usedPercent\":150,\"windowDurationMins\":10080,\"resetsAt\":999999999999999999}}}}");
            Check(snapshot.FiveHour.Remaining == 100, "remaining lower clamp");
            Check(snapshot.Weekly.Remaining == 0, "remaining upper clamp");
            Check(snapshot.Weekly.ResetUtc == null, "invalid reset time");
            Check(!snapshot.AvailableResetCount.HasValue, "null reset count");
            Check(snapshot.CreditsState == CreditsState.Null && snapshot.Credits.Count == 0, "null credits");
            snapshot = Parse("{\"result\":{\"rateLimitResetCredits\":{\"availableCount\":5,\"credits\":[]},\"rateLimits\":{\"primary\":{\"usedPercent\":1,\"windowDurationMins\":300}}}}");
            Check(snapshot.CreditsState == CreditsState.Array && snapshot.Credits.Count == 0 && snapshot.AvailableResetCount == 5, "empty credits count mismatch");
            Check(Parse("{\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":\"NaN\",\"windowDurationMins\":300}}}}").FiveHour.Remaining == null, "invalid percent string");
            Check(Parse("{\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":true,\"windowDurationMins\":300}}}}").FiveHour.Remaining == null, "invalid percent bool");
            snapshot = Parse("{\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":10,\"windowDurationMins\":300,\"resetsAt\":1700000000000}}}}");
            Check(snapshot.FiveHour.Remaining == 90 && snapshot.FiveHour.ResetUtc == null, "millisecond reset preserves percent");
            Check(UsageParser.GetRemaining(Double.NaN) == null && UsageParser.GetRemaining(Double.PositiveInfinity) == null, "non-finite percent");
            Check(UsageParser.GetResetUtc(0) == new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero), "Unix epoch zero");
            Check(UsageParser.GetResetUtc(0.5) == null && UsageParser.GetResetUtc("3600") == null, "strict Unix seconds");
            ExpectParseFailure("broken JSON", "invalid JSON");
            ExpectParseFailure("{\"result\":null}", "invalid result");
        }

        private static void ExpectParseFailure(string json, string name)
        {
            try { Parse(json); }
            catch (UsageReadException) { return; }
            throw new InvalidOperationException(name);
        }

        private static void CheckIconRaster()
        {
            double?[] values = { 100, 31, 30, 11, 10, 0, null };
            Color[] colors = { Color.FromArgb(0, 176, 0), Color.FromArgb(0, 176, 0),
                Color.FromArgb(234, 179, 8), Color.FromArgb(234, 179, 8), Color.FromArgb(239, 68, 68),
                Color.FromArgb(239, 68, 68), Color.FromArgb(148, 163, 184) };
            foreach (int size in new[] { 16, 20, 24, 32 })
            for (int i = 0; i < values.Length; i++)
            using (Icon icon = TrayIconRenderer.Create(values[i], values[i], size))
            using (Bitmap bitmap = icon.ToBitmap())
            {
                int[] left = { size, size }, right = { -1, -1 }, top = { size, size }, bottom = { -1, -1 };
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    Check(pixel.A == 0 || pixel.A == 255, "icon text has no partial alpha");
                    if (y == size / 2 - 1 || y == size / 2)
                        Check(pixel.A == 0, "two clear pixels between icon rows");
                    if (pixel.A == 0) continue;
                    Check(pixel.ToArgb() == colors[i].ToArgb(), "icon text retains its solid color");
                    int row = y < size / 2 ? 0 : 1;
                    left[row] = Math.Min(left[row], x); right[row] = Math.Max(right[row], x);
                    top[row] = Math.Min(top[row], y); bottom[row] = Math.Max(bottom[row], y);
                }
                for (int row = 0; row < 2; row++)
                {
                    Check(left[row] == 0 && right[row] == size - 1,
                        String.Format(CultureInfo.InvariantCulture, "icon row fills the width: {0}px, {1}, row {2}, x={3}..{4}",
                            size, values[i], row, left[row], right[row]));
                    if (size == 16 && values[i] == 100)
                        Check(bottom[row] - top[row] + 1 <= 5, "100 percent text is not stretched vertically");
                }
            }
        }

        private static void CheckPopupStyles(PopupForm popup, int dpi)
        {
            Font system = SystemFonts.MessageBoxFont;
            Color background = Native.PopupDark ? Color.FromArgb(32, 32, 32) : SystemColors.Window;
            Color foreground = Native.PopupDark ? Color.White : SystemColors.WindowText;
            Check(popup.BackColor.ToArgb() == background.ToArgb() &&
                popup.Controls[0].BackColor.ToArgb() == background.ToArgb(), "system window background");
            foreach (Control control in popup.Controls[0].Controls)
            {
                Check(control.ForeColor.ToArgb() == foreground.ToArgb(), "system window text color");
                bool limit = control.Text.StartsWith("5 часов - ", StringComparison.Ordinal) ||
                    control.Text.StartsWith("Неделя - ", StringComparison.Ordinal) ||
                    control.Text.StartsWith("5 hours - ", StringComparison.Ordinal) || control.Text.StartsWith("Week - ", StringComparison.Ordinal);
                Check(control.Font.Name == system.Name && control.Font.Style == (limit ? system.Style | FontStyle.Bold : system.Style) &&
                    control.Font.Unit == GraphicsUnit.Pixel &&
                    Math.Abs(control.Font.Size - system.SizeInPoints * dpi / 72f) < 0.01, "uniform system window font");
                Check(((Label)control).TextAlign == ContentAlignment.TopCenter, "centered card text");
            }
        }

        private static void CheckPopupLayout()
        {
            Native.EnableDpi();
            using (TrayWindow tray = new TrayWindow())
            {
                bool moved = false;
                tray.Callback = delegate(int code, Point point) { moved = code == 0x200; };
                SendMessage(tray.Handle, TrayWindow.CallbackMessage, new IntPtr(1), new IntPtr(0x200));
                Check(moved, "legacy Shell hover callback");
                moved = false;
                SendMessage(tray.Handle, TrayWindow.CallbackMessage, IntPtr.Zero, new IntPtr((1 << 16) | 0x200));
                Check(moved, "version 4 Shell hover callback");
            }
            Rectangle work = new Rectangle(-1920, -200, 1920, 1032);
            Check(PopupForm.Place(new Rectangle(-1500, 700, 32, 32), new Size(360, 200), work).Right == work.Right - 6,
                "popup aligns to right edge independently of icon position");
            Rectangle spaced = PopupForm.Place(new Rectangle(work.Right - 48, work.Bottom, 32, 48), new Size(196, 256), work);
            Check(work.Right - spaced.Right == work.Bottom - spaced.Bottom && work.Right - spaced.Right == 6, "equal right and taskbar gaps");
            MethodInfo build = typeof(PopupForm).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance);
            using (PopupForm popup = new PopupForm())
            {
                UsageSnapshot data = Parse("{\"result\":{\"rateLimits\":{}}}");
                popup.SetSnapshot(data, null);
                build.Invoke(popup, new object[] { work, 96 });
                CheckPopupStyles(popup, 96);
                int narrow = popup.Width;
                data.PlanType = new String('W', 120);
                build.Invoke(popup, new object[] { work, 96 });
                Check(popup.Width > narrow * 2 && work.Contains(popup.Bounds) && popup.Right == work.Right - 6,
                    "width follows longest text and remains on screen");
                Rectangle smallWork = new Rectangle(0, 0, 640, 480);
                build.Invoke(popup, new object[] { smallWork, 96 });
                Check(smallWork.Contains(popup.Bounds) && popup.Right == smallWork.Right - 6, "wide text stays inside small screen");
                data.PlanType = null;
                build.Invoke(popup, new object[] { work, 96 });
                Check(popup.Width == narrow, "width shrinks after removing long text");
                build.Invoke(popup, new object[] { work, 144 });
                CheckPopupStyles(popup, 144);
                Check(work.Contains(popup.Bounds) && popup.Right == work.Right - 6, "scaled popup stays on screen");

                DateTimeOffset now = DateTimeOffset.UtcNow;
                data.ReceivedAtUtc = now;
                data.PlanType = "plus";
                data.FiveHour.Remaining = 29; data.FiveHour.ResetUtc = now.AddMinutes(41);
                data.Weekly.Remaining = 22; data.Weekly.ResetUtc = now.AddDays(2).AddHours(8).AddMinutes(53);
                data.AvailableResetCount = 3;
                for (int i = 0; i < 3; i++) data.Credits.Add(new ResetCredit {
                    Title = new String('W', 120), Type = "Full reset", Description = "Removed description", ExpiresUtc = now.AddDays(i + 17)
                });
                data.Subscription = new SubscriptionMetadata { ActiveUntilUtc = now.AddDays(14), LastCheckedUtc = now.AddDays(-1) };
                build.Invoke(popup, new object[] { work, 96 });
                List<string> text = new List<string>();
                foreach (Control row in popup.Controls[0].Controls) if (row.Text.Length > 0) text.Add(row.Text);
                string[] expected = {
                    "Codex · Plus", "5 часов - 29%", "До сброса: 0 ч 41 мин", "Сброс: " + PopupForm.LocalDate(data.FiveHour.ResetUtc.Value),
                    "Неделя - 22%", "До сброса: 2 дня 8 ч 53 мин", "Сброс: " + PopupForm.LocalDate(data.Weekly.ResetUtc.Value),
                    "Сбросов лимитов: 3", "До: " + PopupForm.LocalDate(data.Credits[0].ExpiresUtc.Value),
                    "До: " + PopupForm.LocalDate(data.Credits[1].ExpiresUtc.Value), "До: " + PopupForm.LocalDate(data.Credits[2].ExpiresUtc.Value),
                    "Подписка до: " + PopupForm.LocalDate(data.Subscription.ActiveUntilUtc.Value)
                };
                Check(String.Join("\n", text) == String.Join("\n", expected) && popup.CreditRows == 3, "compact card matches requested fields");
                CheckPopupStyles(popup, 96);
                Check(popup.Width < work.Width / 2, "removed credit text does not widen compact card");
                data.Credits[1].ExpiresUtc = null;
                build.Invoke(popup, new object[] { work, 96 });
                bool unknownExpiry = false;
                foreach (Control row in popup.Controls[0].Controls) if (row.Text == "До: нет данных") unknownExpiry = true;
                Check(unknownExpiry && popup.CreditRows == 3, "unknown credit expiry is not fabricated");
                data.AvailableResetCount = 0;
                build.Invoke(popup, new object[] { work, 96 });
                Check(popup.CreditRows == 0, "zero available resets has no expiry rows");
                data.AvailableResetCount = 1;
                build.Invoke(popup, new object[] { work, 96 });
                Check(popup.CreditRows == 1, "available count limits expiry rows");
                popup.SetSnapshot(data, "Синтетическая ошибка соединения");
                build.Invoke(popup, new object[] { work, 96 });
                bool stale = false;
                foreach (Control row in popup.Controls[0].Controls) if (row.Text.StartsWith("Данные устарели.", StringComparison.Ordinal)) stale = true;
                Check(stale, "stale warning remains available");
            }
        }

        private static void CheckPresentation()
        {
            foreach (string region in "AM AZ BY EE GE KZ KG LV LT MD RU TJ TM UA UZ".Split(' '))
                Check(UiText.RussianRegion(region), "Russian for former-USSR region " + region);
            foreach (string region in "US GB FR DE PL TR CN IN CA AU".Split(' '))
                Check(!UiText.RussianRegion(region), "English for other region " + region);
            Check(!UiText.RussianRegion(null) && !UiText.RussianRegion("") && !UiText.RussianRegion("R"), "unknown regions do not match");
            Check(UiText.UsesRussian(new CultureInfo("en-US"), new CultureInfo("ru-RU")), "Russian regional settings");
            Check(UiText.UsesRussian(new CultureInfo("et-EE"), new CultureInfo("en-US")), "former-USSR interface language");
            Check(UiText.UsesRussian(new CultureInfo("ru"), CultureInfo.InvariantCulture), "neutral Russian culture");
            Check(!UiText.UsesRussian(new CultureInfo("en-US"), new CultureInfo("de-DE")), "English outside former USSR");
            Check(TrayIconRenderer.ColorFor(31).ToArgb() == Color.FromArgb(34, 197, 94).ToArgb(), "green 31");
            Check(TrayIconRenderer.ColorFor(30).ToArgb() == Color.FromArgb(234, 179, 8).ToArgb(), "yellow 30");
            Check(TrayIconRenderer.ColorFor(11).ToArgb() == Color.FromArgb(234, 179, 8).ToArgb(), "yellow 11");
            Check(TrayIconRenderer.ColorFor(10).ToArgb() == Color.FromArgb(239, 68, 68).ToArgb(), "red 10");
            Check(TrayIconRenderer.ColorFor(0).ToArgb() == Color.FromArgb(239, 68, 68).ToArgb(), "red 0");
            Check(TrayIconRenderer.ColorFor(100).ToArgb() == Color.FromArgb(34, 197, 94).ToArgb(), "green 100");
            Check(TrayIconRenderer.ColorFor(30.1).ToArgb() == Color.FromArgb(34, 197, 94).ToArgb(), "green 30.1");
            Check(TrayIconRenderer.ColorFor(10.1).ToArgb() == Color.FromArgb(234, 179, 8).ToArgb(), "yellow 10.1");
            Check(TrayIconRenderer.ColorFor(null).ToArgb() == Color.FromArgb(148, 163, 184).ToArgb(), "unknown color");
            Check(TrayIconRenderer.FormatPercent(31.99) == "31%", "floor percent");
            Check(TrayIconRenderer.FormatPercent(null) == "—", "unknown percent");
            DateTimeOffset now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
            Check(PopupForm.Countdown(now.AddMinutes(61), now) == "1 ч 1 мин", "countdown 61 minutes");
            Check(PopupForm.Countdown(now.AddSeconds(61), now) == "0 ч 2 мин", "countdown ceil minute");
            Check(PopupForm.Countdown(now.AddSeconds(-1), now) == "0 ч 0 мин", "countdown past");
            Check(PopupForm.Countdown(now.AddDays(1), now) == "1 день 0 ч 0 мин", "one day countdown");
            Check(PopupForm.Countdown(now.AddDays(2).AddHours(8).AddMinutes(53), now) == "2 дня 8 ч 53 мин", "days hours minutes countdown");
            Check(PopupForm.Countdown(now.AddDays(5), now) == "5 дней 0 ч 0 мин", "five days countdown");
            Check(PopupForm.Countdown(now.AddDays(11), now) == "11 дней 0 ч 0 мин", "eleven days countdown");
            Check(PopupForm.Countdown(now.AddDays(21), now) == "21 день 0 ч 0 мин", "twenty-one days countdown");
            Check(PopupForm.LocalDate(now).Length == 16, "local dates omit timezone suffix");
        }

        private static void CheckSubscriptionMetadata()
        {
            string payload = "{\"https://api.openai.com/auth\":{\"chatgpt_subscription_active_until\":\"2026-10-03T00:00:00Z\",\"chatgpt_subscription_last_checked\":\"2026-10-02T12:00:00Z\"}}";
            string token = "eyJhbGciOiJub25lIn0." + Base64Url(payload) + ".sig";
            SubscriptionMetadata metadata = UsageParser.ParseSubscriptionToken(token);
            Check(metadata.ActiveUntilUtc.HasValue && metadata.LastCheckedUtc.HasValue, "JWT two claims");
            metadata = UsageParser.ParseSubscriptionToken("not-a-jwt");
            Check(metadata.ActiveUntilUtc == null, "invalid JWT safe");
        }

        private static string Base64Url(string value)
        {
            string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
            return encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
