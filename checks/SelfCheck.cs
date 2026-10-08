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
                if (args.Length >= 2 && args[0] == "--fake-server") return FakeServer(args[1]);
                if (args.Length >= 1 && args[0] == "--integration-check") return IntegrationCheck();
                if (args.Length >= 1 && args[0] == "--live-check") return LiveCheck();
                if (args.Length == 1 && args[0] == "--ui-check") return UiCheck();
                if (args.Length == 1 && args[0] == "--joint-launch-check") return JointLaunchCheck();
                if (args.Length != 0) throw new InvalidOperationException("Unknown check argument");
                Thread.CurrentThread.CurrentCulture = Thread.CurrentThread.CurrentUICulture = new CultureInfo("ru-RU");
                CheckParserContract();
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
        private static void Pump(int milliseconds)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            do { Application.DoEvents(); Thread.Sleep(10); } while (elapsed.ElapsedMilliseconds < milliseconds);
        }
        private static int UiCheck()
        {
            Native.EnableDpi();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Point previous = Cursor.Position;
            try
            {
                using (TrayContext context = new TrayContext(false))
                {
                    UsageSnapshot data = Parse("{\"result\":{\"planType\":\"plus\",\"rateLimits\":{}}}");
                    data.FiveHour.Remaining = 75;
                    data.Weekly.Remaining = 20;
                    context.SetSnapshot(data, null);
                    Pump(100);
                    Check(context.Tray.Registered, "tray icon registered");
                    Rectangle anchor = context.Tray.IconBounds();
                    Check(!anchor.IsEmpty, "tray icon bounds available");
                    Cursor.Position = new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2);
                    context.Tray.Callback(0x200, Cursor.Position);
                    Pump(600);
                    Check(context.Popup.Visible, "hover opens card");
                    object[] escape = { new Message(), Keys.Escape };
                    Check((bool)typeof(PopupForm).GetMethod("ProcessCmdKey", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(context.Popup, escape) && !context.Popup.Visible, "Escape hides card");
                    context.ExitThread();
                }
            }
            finally { Cursor.Position = previous; }
            Console.WriteLine("PASS: UI hover and Escape");
            return 0;
        }

        private static int JointLaunchCheck()
        {
            string executable = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "CodexUsageTray.exe");
            Check(File.Exists(executable), "product executable exists");
            using (Process utility = new Process { StartInfo = new ProcessStartInfo(executable) {
                Arguments = "--with-codex", UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            } })
            {
                IntPtr tray = IntPtr.Zero;
                utility.Start();
                try
                {
                    Stopwatch timeout = Stopwatch.StartNew();
                    Native.Rect rect;
                    while (timeout.ElapsedMilliseconds < 10000)
                    {
                        tray = OwnWindow(utility.Id, "Codex Usage Tray message window");
                        if (tray != IntPtr.Zero)
                        {
                            Native.IconIdentifier id = new Native.IconIdentifier { Size = 40, Window = tray, Id = 1 };
                            if (Native.ShellGetRect(ref id, out rect) == 0) break;
                        }
                        Thread.Sleep(100);
                    }
                    Check(tray != IntPtr.Zero && !utility.HasExited, "joint launch registers tray icon");
                    Native.IconIdentifier registered = new Native.IconIdentifier { Size = 40, Window = tray, Id = 1 };
                    Check(Native.ShellGetRect(ref registered, out rect) == 0, "joint launch icon visible to Shell");
                    using (Process duplicate = Process.Start(new ProcessStartInfo(executable) {
                        Arguments = "--with-codex", UseShellExecute = false, CreateNoWindow = true
                    }))
                        Check(duplicate.WaitForExit(5000) && duplicate.ExitCode == 0, "duplicate launch reuses instance");
                }
                finally
                {
                    if (tray != IntPtr.Zero) SendMessage(tray, 0x10, IntPtr.Zero, IntPtr.Zero);
                    if (!utility.WaitForExit(5000)) { utility.Kill(); utility.WaitForExit(1000); }
                }
                Check(utility.ExitCode == 0, "joint launch exits cleanly");
            }
            Console.WriteLine("PASS: joint launch and singleton");
            return 0;
        }

        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        private delegate bool EnumWindowCallback(IntPtr window, IntPtr param);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr param);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

        private static void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
        }

        private static UsageSnapshot Parse(string json)
        {
            return UsageParser.Parse(json, new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        }

        private static void CheckParserContract()
        {
            UsageSnapshot snapshot = Parse(
                "{\"result\":{\"planType\":\"pro\",\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":25,\"windowDurationMins\":300}," +
                "\"secondary\":{\"usedPercent\":88,\"windowDurationMins\":10080}}},\"rateLimits\":{\"primary\":{\"usedPercent\":99,\"windowDurationMins\":300}}," +
                "\"rateLimitResetCredits\":{\"availableCount\":2,\"credits\":[{\"expiresAt\":0}]}}}");
            Check(snapshot.FiveHour.Remaining == 75 && snapshot.Weekly.Remaining == 12, "codex limit id priority");
            Check(snapshot.PlanType == "pro" && snapshot.AvailableResetCount == 2 && snapshot.Credits.Count == 1, "plan and reset metadata");
            Check(snapshot.Credits[0].ExpiresUtc == new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero), "Unix epoch expiry");

            snapshot = Parse("{\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":10,\"windowDurationMins\":10080}," +
                "\"secondary\":{\"usedPercent\":20,\"windowDurationMins\":300}}}}");
            Check(snapshot.FiveHour.Remaining == 80 && snapshot.Weekly.Remaining == 90, "duration fallback");
            snapshot = Parse("{\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":10,\"windowDurationMins\":300}," +
                "\"secondary\":{\"usedPercent\":20,\"windowDurationMins\":300}}}}");
            Check(!snapshot.FiveHour.Remaining.HasValue && !snapshot.Weekly.Remaining.HasValue, "duplicate windows unknown");

            snapshot = Parse("{\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":-20,\"windowDurationMins\":300}," +
                "\"secondary\":{\"usedPercent\":150,\"windowDurationMins\":10080,\"resetsAt\":1700000000000}}}}");
            Check(snapshot.FiveHour.Remaining == 100 && snapshot.Weekly.Remaining == 0, "percentage clamp");
            Check(snapshot.Weekly.ResetUtc == null && UsageParser.GetResetUtc("3600") == null, "invalid reset rejected");
            ExpectParseFailure("broken JSON", "invalid JSON");
            ExpectParseFailure("{\"result\":null}", "invalid result");
        }

        private static void ExpectParseFailure(string json, string name)
        {
            try { Parse(json); }
            catch (UsageReadException) { return; }
            throw new InvalidOperationException(name);
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
