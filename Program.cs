// Razer 電量小工具：常駐顯示 Viper V4 Pro 電量，不用開 Synapse。
// 協定來源：Synapse 4 的 web UI（rzDevice25），_frame 構造已對裝置實測驗證：
//   SetFeature(0)：90B frame [0, txid, 0,0,0, rw, class, id, payload…, XOR(frame[2..87])@88, 0]
//   GetFeature  ：回應在同一 handle：resp[0]=status(0=NOT_READY,1=BUSY,2=SUCCESS,3=FAILED…)
//                 resp[1]=txid echo，resp[9]=batteryId，resp[10]=level(0-255)，% = round(level*100/255)
// 查電量指令：rw=2, class=0x07, id=0x80（Get Battery Level）
// 查充電狀態：rw=2, class=0x07, id=0x84（Get Device Charging Status），
//   resp[10]=0 不充電 / 1 充電中 / 2 充電失敗；battery 回應裡沒有充電 byte。
// ASUS 部分：ROG Strix Go 2.4 耳機（dongle VID 0B05 / PID 18D6）的 vendor 控制介面
//   （HID usage page 0xFF00）走另一種 feature report：64B、reportId 0xFF，
//   查電量 Set → 等 35ms → Get，回應 resp[1]=0x1B、resp[12]<<8|resp[11]=millivolts。
//   協定來源：G-Helper（github.com/seerge/g-helper；Peripherals/Headset/Models/StrixGo24.cs），
//   本專案對實機驗證成功（2026-10-17），細節見 docs\protocol-asus.md。
using Microsoft.Win32.SafeHandles;
using System.Drawing.Drawing2D;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace RazerBatteryWidget;

internal static class HidNative
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle device, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle device, byte[] buffer, int length);

    internal static SafeFileHandle OpenHid(string path) =>
        CreateFileW(path, 0xC0000000 | 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); // GENERIC_READ|WRITE, FILE_SHARE_R|W, OPEN_EXISTING

    internal static bool SetFeature(SafeFileHandle device, byte[] buffer) =>
        HidD_SetFeature(device, buffer, buffer.Length);

    internal static bool GetFeature(SafeFileHandle device, byte[] buffer) =>
        HidD_GetFeature(device, buffer, buffer.Length);
}

// 充電狀態列舉：Synapse SDK 定義 NO_CHARGE_BATTERY_FULL=0、CHARGING=1、CHARGING_FAIL=2。
internal enum ChargingState : byte
{
    NotCharging = 0,
    Charging = 1,
    ChargingFailed = 2,
}

internal readonly record struct BatteryReading(int Percent, bool Ok, ChargingState Charging = ChargingState.NotCharging);

// WMI 裡 HID 裝置的 DeviceID 是 HID\VID…\… 格式，CreateFile 要 \\?\HID#VID…#…#{GUID} 格式。
internal static class HidPathFinder
{
    private const string HidClassInterfaceGuid = "{4d1e55b2-f16f-11cf-88cb-001111000030}";

    // 用 WMI 枚舉符合 filter 的 HID 介面並轉成 CreateFile 可用的路徑。
    public static List<string> FindPaths(string deviceFilter)
    {
        var result = new List<string>();
        using var searcher = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_PnPEntity");
        foreach (var item in searcher.Get())
        {
            if (item["DeviceID"] is not string id || !id.StartsWith(deviceFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add(@"\\?\" + id.Replace('\\', '#') + "#" + HidClassInterfaceGuid);
        }
        return result;
    }
}

internal static class RazerMouseBattery
{
    // Viper V4 Pro 的滑鼠類產品線都以 VID 1532 開頭；只挑指令介面 MI_03。
    private const string DeviceFilter = "HID\\VID_1532&PID_00E6&MI_03";

    private static byte[] BuildFrame(byte rw, byte cls, byte id, byte txid)
    {
        // 91B wire = [reportId 0][0][txid][0,0,0][rw@6][class@7][id@8][payload@9…][xor(3..88)@89][0@90]
        //（對照 node-hid 實測成功 frame 與 SDK _createDataSend 的 90B dataSend＝bridge 再補一個 report byte）
        var frame = new byte[91];
        frame[2] = txid;
        frame[6] = rw;
        frame[7] = cls;
        frame[8] = id;
        byte xor = 0;
        for (int i = 3; i <= 88; i++) xor ^= frame[i];
        frame[89] = xor;                   // checksum
        return frame;
    }

    private static byte[] BuildGetBatteryFrame(byte txid) => BuildFrame(0x02, 0x07, 0x80, txid);

    // Get Device Charging Status（Synapse SDK：getChargingStatus → [2,7,132]，payload batteryId）
    private static byte[] BuildGetChargingFrame(byte txid) => BuildFrame(0x02, 0x07, 0x84, txid);

    private static string? _cachedPath;

    private static IEnumerable<string> CandidatePaths()
    {
        if (_cachedPath is not null) yield return _cachedPath;
        foreach (var p in HidPathFinder.FindPaths(DeviceFilter)) yield return p;
    }

    // 送出 frame 並輪詢回應；等不到有效回應（status=2 且 txid 相符）就回 null。
    private static byte[]? TryQuery(SafeFileHandle device, byte[] frame, byte txid, string[] dbgArgs)
    {
        bool sent = HidNative.SetFeature(device, frame);
        if (dbgArgs.Contains("--debug")) Console.WriteLine($"   SetFeature={sent} txid={txid}");
        if (!sent) return null;
        for (int retry = 0; retry < 30; retry++)
        {
            Thread.Sleep(80);                       // 裝置要時間準備回應
            var response = new byte[91];
            bool got = HidNative.GetFeature(device, response);
            if (dbgArgs.Contains("--debug") && retry < 3) Console.WriteLine($"   Get={got} resp={Convert.ToHexString(response[..12])}");
            // 睡的時候回應會一直卡 BUSY / 舊 txid，多重試；裝置會持續回報同 frame 直到資料就緒。
            if (got && response[1] == 2 && response[2] == txid) return response;
        }
        return null;
    }

    // 回傳 -1 代表裝置不在或睡了；否則回 0-100 電量百分比與充電狀態。
    public static BatteryReading Read(string[]? args = null)
    {
        args ??= Array.Empty<string>();
        bool debug = args.Contains("--debug");
        _cachedPath = null;
        foreach (var path in CandidatePaths())
        {
            if (debug) Console.WriteLine($"trying: {path}");
            using (var device = HidNative.OpenHid(path))
            {
                if (device.IsInvalid)
                {
                    if (args.Contains("--debug")) Console.WriteLine("   open failed, err " + Marshal.GetLastWin32Error());
                    continue;
                }
                if (args.Contains("--debug")) Console.WriteLine("   opened");

            for (int attempt = 0; attempt < 3; attempt++)
            {
                byte txid = (byte)Random.Shared.Next(32);
                var battery = TryQuery(device, BuildGetBatteryFrame(txid), txid, args);
                if (battery is null) continue;

                // 同 handle 續問充電狀態（class 7 / id 132）：resp[9]=batteryId、resp[10]=chargingStatus。
                _cachedPath = path;                  // 記住成功路徑，後續輪詢直接用
                byte ctid = (byte)Random.Shared.Next(32);
                var charging = TryQuery(device, BuildGetChargingFrame(ctid), ctid, args);
                int chargeRaw = charging is not null ? charging[10] : 0;
                if (debug) Console.WriteLine($"   battery raw={battery[10]} charging byte raw={chargeRaw}");
                return new BatteryReading(
                    (int)Math.Floor(battery[10] * 100.0 / 255),
                    true,
                    (ChargingState)Math.Min((byte)chargeRaw, (byte)2));
            }
        }
        }
        return new BatteryReading(-1, false);
    }
}

// ROG Strix Go 2.4（dongle）電量查詢。
// 協定抄自 G-Helper 的 StrixGo24（GPL-3，github.com/seerge/g-helper），
// 已在本機實機驗證：SetFeature 64B → 35ms → GetFeature，COL03 是唯一會回的 vendor 介面。
internal static class AsusHeadsetBattery
{
    private const string DeviceFilter = "HID\\VID_0B05&PID_18D6&MI_03";
    private const byte ReportId = 0xFF;

    private static string? _cachedPath;

    // 查電量：[0xFF][08][00][FD][04][0x12][0xF1][0x03][0x52][0x01]，補零到 64B。
    // 回應 resp[1]=0x1B 才有效；resp[11..12]=millivolts (LE)、resp[9]==0x0A 表充電中。
    // 百分比是 mV 對應的五段折線（G-Helper 的曲線，非線性百分比）：
    //   >=4100→100、>=3800→75、>=3700→50、>=3500→25、其他→10。
    // Armoury Crate 對同一 mV 會顯示不同 %（它有自己的曲線），本工具與 G-Helper 一致。
    private static byte[] BuildBatteryFrame()
    {
        var frame = new byte[64];               // 裝置的 feature report 長度是 64，短了 SetFeature 會回 error 87
        frame[0] = ReportId;
        frame[1] = 0x08; frame[2] = 0x00; frame[3] = 0xFD;
        frame[4] = 0x04; frame[5] = 0x12; frame[6] = 0xF1; frame[7] = 0x03;
        frame[8] = 0x52; frame[9] = 0x01;
        return frame;
    }

    private static int PercentFromMillivolts(int mv) =>
        mv >= 4100 ? 100 : mv >= 3800 ? 75 : mv >= 3700 ? 50 : mv >= 3500 ? 25 : 10;

    private static IEnumerable<string> CandidatePaths()
    {
        if (_cachedPath is not null) yield return _cachedPath;
        foreach (var p in HidPathFinder.FindPaths(DeviceFilter)) yield return p;
    }

    public static BatteryReading Read(string[]? args = null)
    {
        args ??= Array.Empty<string>();
        bool debug = args.Contains("--debug");
        _cachedPath = null;
        foreach (var path in CandidatePaths())
        {
            if (debug) Console.WriteLine($"[asus] trying: {path}");
            using (var device = HidNative.OpenHid(path))
            {
                if (device.IsInvalid)
                {
                    if (debug) Console.WriteLine("   open failed, err " + Marshal.GetLastWin32Error());
                    continue;
                }

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var frame = BuildBatteryFrame();
                    bool sent = HidNative.SetFeature(device, frame);
                    if (!sent)
                    {
                        if (debug) Console.WriteLine("   SetFeature failed, err " + Marshal.GetLastWin32Error());
                        break;                      // 這個介面不收 feature report，換下一個路徑
                    }
                    Thread.Sleep(35);               // G-Helper 同款：Set 後等 35ms 再 Get
                    // 裝置剛醒來時第一次 Get 可能還没備好資料：持續重讀 GetFeature
                    // （resp[1]==0x1B 才是好資料），不要重發 SetFeature，
                    // 跟滑鼠那邊的坑同一款。
                    for (int retry = 0; retry < 4; retry++)
                    {
                        var response = new byte[64];
                        response[0] = ReportId;
                        bool got = HidNative.GetFeature(device, response);
                        if (debug) Console.WriteLine($"   Get={got} resp={Convert.ToHexString(response[..16])}");
                        if (got && response[1] == 0x1B)
                        {
                            _cachedPath = path;     // 記住成功路徑
                            int millivolts = (response[12] << 8) | response[11];
                            bool charging = response[9] == 0x0A;
                            int percent = PercentFromMillivolts(millivolts);
                            if (debug) Console.WriteLine($"   millivolts={millivolts} charging={charging}");
                            return new BatteryReading(percent, true,
                                charging ? ChargingState.Charging : ChargingState.NotCharging);
                        }
                        Thread.Sleep(40);
                    }
                }
            }
        }
        return new BatteryReading(-1, false);
    }
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 自檢模式：node probe 的 C# 版本，直接印結果後離開。
        if (args.Contains("--probe"))
        {
            var mouse = RazerMouseBattery.Read(args);
            Console.WriteLine(mouse.Ok
                ? $"mouse = {mouse.Percent}% charging = {mouse.Charging}"
                : "mouse not found / asleep");
            var headset = AsusHeadsetBattery.Read(args);
            Console.WriteLine(headset.Ok
                ? $"headset = {headset.Percent}% charging = {headset.Charging}"
                : "headset not found / asleep");
            return;
        }

        using var mutex = new Mutex(true, "RazerBatteryWidget_SingleInstance", out var isFirst);
        if (!isFirst) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new HiddenTrayContext());
    }
}

internal sealed class HiddenTrayContext : ApplicationContext
{
    private static readonly int PollSeconds = 30;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = PollSeconds * 1000 };
    private BatteryReading _lastGood = new(-1, false);
    private BatteryReading _lastGoodHeadset = new(-1, false);

    // 每輪都重新走 WMI 查路徑；_cachedPath 記住上一輪成功的路徑，下次優先試。
    public HiddenTrayContext()
    {
        _tray = new NotifyIcon
        {
            Icon = MakeIcon(null),
            Text = "電量小工具：查詢中…",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        _timer.Tick += async (_, _) => await PollAsync();
        // 開程式馬上查一次，之後每 30 秒自動更新。
        _ = PollAsync();
        _timer.Start();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("手動更新", null, (_, _) => _ = PollAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("離開", null, (_, _) =>
        {
            _tray.Visible = false;
            Application.Exit();
        });
        return menu;
    }

    private async Task PollAsync()
    {
        var (mouse, headset) = await Task.Run(() => (RazerMouseBattery.Read(), AsusHeadsetBattery.Read()));

        if (mouse.Ok) _lastGood = mouse;
        if (headset.Ok) _lastGoodHeadset = headset;

        // 圖示維持顯示滑鼠（跟原本一樣）；耳機走 tooltip 第二行。
        var shown = mouse.Ok ? mouse : _lastGood;
        string mouseTip = mouse.Ok
            ? $"Razer Viper V4 Pro：{mouse.Percent}%" +
              (mouse.Charging == ChargingState.Charging ? "（充電中）"
               : mouse.Charging == ChargingState.ChargingFailed ? "（充電異常？）" : "")
            : _lastGood.Ok
                ? $"Razer Viper V4 Pro：{_lastGood.Percent}%（目前連不上，睡著了？）"
                : "沒找到滑鼠（接收器沒插？）";

        string headsetTip = headset.Ok
            ? $"ROG Strix Go 2.4：{headset.Percent}%" + (headset.Charging == ChargingState.Charging ? "（充電中）" : "")
            : _lastGoodHeadset.Ok
                ? $"ROG Strix Go 2.4：{_lastGoodHeadset.Percent}%（目前連不上，睡著了？）"
                : "沒找到耳機";

        _tray.Text = $"{mouseTip}\n{headsetTip}";
        _tray.Icon?.Dispose();
        _tray.Icon = MakeIcon(shown.Ok ? shown.Percent : null, shown.Ok && shown.Charging == ChargingState.Charging);
        if (mouse.Ok && mouse.Percent <= 15)
            _tray.ShowBalloonTip(5000, "Razer 電量小工具", "滑鼠快沒電了！", ToolTipIcon.Warning);
        if (headset.Ok && headset.Percent >= 0 && headset.Percent <= 15)
            _tray.ShowBalloonTip(5000, "電量小工具", "耳機快沒電了！", ToolTipIcon.Warning);
    }

    // 用數字畫成正方形小圖示：充電中＝藍、綠 >40、橘 15-40、紅 <15、灰＝查不到。
    private Icon MakeIcon(int? percent, bool charging = false)
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bg = percent is null ? Color.FromArgb(120, 120, 120)
               : charging ? Color.FromArgb(0, 130, 230)
               : percent <= 15 ? Color.FromArgb(200, 40, 40)
               : percent <= 40 ? Color.FromArgb(230, 160, 30)
               : Color.FromArgb(60, 160, 60);
        using var brush = new SolidBrush(bg);
        g.FillEllipse(brush, 1, 1, 30, 30);
        var text = percent?.ToString() ?? "?";
        using var font = new Font("Segoe UI", text.Length >= 3 ? 10 : 13, FontStyle.Bold, GraphicsUnit.Pixel);
        var size = g.MeasureString(text, font);
        using var textBrush = new SolidBrush(Color.White);
        g.DrawString(text, font, textBrush, (32 - size.Width) / 2f, (32 - size.Height) / 2f + 1);
        return Icon.FromHandle(bmp.GetHicon());
    }
}
