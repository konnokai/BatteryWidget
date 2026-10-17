// Razer 電量小工具：常駐顯示 Viper V4 Pro 電量，不用開 Synapse。
// 協定來源：Synapse 4 的 web UI（rzDevice25），_frame 構造已對裝置實測驗證：
//   SetFeature(0)：90B frame [0, txid, 0,0,0, rw, class, id, payload…, XOR(frame[2..87])@88, 0]
//   GetFeature  ：回應在同一 handle：resp[0]=status(0=NOT_READY,1=BUSY,2=SUCCESS,3=FAILED…)
//                 resp[1]=txid echo，resp[9]=batteryId，resp[10]=level(0-255)，% = round(level*100/255)
// 查電量指令：rw=2, class=0x07, id=0x80（Get Battery Level）
// 查充電狀態：rw=2, class=0x07, id=0x84（Get Device Charging Status），
//   resp[10]=0 不充電 / 1 充電中 / 2 充電失敗；battery 回應裡沒有充電 byte。
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

internal static class RazerMouseBattery
{
    // Viper V4 Pro 的滑鼠類產品線都以 VID 1532 開頭；只挑指令介面 MI_03。
    private const string DeviceFilter = "HID\\VID_1532&PID_00E6&MI_03";
    private const string HidClassInterfaceGuid = "{4d1e55b2-f16f-11cf-88cb-001111000030}";

    private static List<string> FindCommandInterfacePaths()
    {
        var result = new List<string>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID FROM Win32_PnPEntity");
        foreach (var item in searcher.Get())        {
            if (item["DeviceID"] is not string id || !id.StartsWith(DeviceFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            // WMI 的 DeviceID 是 HID\VID…\…\… 格式，CreateFile 要 \\?\HID#VID…#…#{GUID} 格式。
            var path = @"\\?\" + id.Replace('\\', '#') + "#" + HidClassInterfaceGuid;
            result.Add(path);
        }
        return result;
    }

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
        foreach (var p in FindCommandInterfacePaths()) yield return p;
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

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 自檢模式：node probe 的 C# 版本，直接印結果後離開。
        if (args.Contains("--probe"))
        {
            var r = RazerMouseBattery.Read(args);
            Console.WriteLine(r.Ok ? $"battery = {r.Percent}% charging = {r.Charging}" : "mouse not found / asleep");
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

    // hold one handle so我們的 GetFeature 可以持續拿到當下狀態。
    public HiddenTrayContext()
    {
        _tray = new NotifyIcon
        {
            Icon = MakeIcon(null),
            Text = "Razer 電量小工具：查詢中…",
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
        var reading = await Task.Run(() => RazerMouseBattery.Read());
        if (reading.Ok) _lastGood = reading;

        // 滑鼠睡著時保留最後已知電量（跟 Synapse 一樣），tooltip 標注連不上。
        var shown = reading.Ok ? reading : _lastGood;
        string tip = reading.Ok
            ? $"Razer Viper V4 Pro：{reading.Percent}%" +
              (reading.Charging == ChargingState.Charging ? "（充電中）"
               : reading.Charging == ChargingState.ChargingFailed ? "（充電異常？）" : "")
            : _lastGood.Ok
                ? $"Razer Viper V4 Pro：{_lastGood.Percent}%（目前連不上，睡著了？）"
                : "沒找到滑鼠（接收器沒插？）";
        _tray.Text = tip;
        _tray.Icon?.Dispose();
        _tray.Icon = MakeIcon(shown.Ok ? shown.Percent : null, shown.Ok && shown.Charging == ChargingState.Charging);
        if (reading.Ok && reading.Percent <= 15)
            _tray.ShowBalloonTip(5000, "Razer 電量小工具", "滑鼠快沒電了！", ToolTipIcon.Warning);
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
