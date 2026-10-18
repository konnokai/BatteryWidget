// Razer 電量小工具：常駐顯示 Viper V4 Pro 電量，不用開 Synapse。
// 協定來源：Synapse 4 的 web UI（rzDevice25），_frame 構造已對裝置實測驗證：
//   SetFeature(0)：90B frame [0, txid, 0,0,0, rw, class, id, payload…, XOR(frame[2..87])@88, 0]
//   GetFeature  ：回應在同一 handle：resp[0]=status(0=NOT_READY,1=BUSY,2=SUCCESS,3=FAILED…)
//                 resp[1]=txid echo，resp[9]=batteryId，resp[10]=level(0-255)，% = round(level*100/255)
// 查電量指令：rw=2, class=0x07, id=0x80（Get Battery Level）
// 查充電狀態：rw=2, class=0x07, id=0x84（Get Device Charging Status），
//   resp[10]=0 不充電 / 1 充電中 / 2 充電失敗；battery 回應裡沒有充電 byte。
// 接收器 LED 指示燈：rw=3、class=0x07、id=149（讀）/21（寫）（Get/Set indicator LEDs Configuration），
//   payload 與回應 0..2 = LED1..3 的顯示項目，1=電池電量、2=連線品質、3=輪循率。
// ASUS 部分：ROG Strix Go 2.4 耳機（dongle VID 0B05 / PID 18D6）的 vendor 控制介面
//   （HID usage page 0xFF00）走另一種 feature report：64B、reportId 0xFF，
//   查電量 Set → 等 35ms → Get，回應 resp[1]=0x1B、resp[12]<<8|resp[11]=millivolts。
//   協定來源：G-Helper（github.com/seerge/g-helper；Peripherals/Headset/Models/StrixGo24.cs），
//   本專案對實機驗證成功（2026-10-17），細節見 docs\protocol-asus.md。
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Drawing.Drawing2D;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace BatteryWidget;

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

// 接收器 LED 的顯示項目（Synapse「LED 指示燈」頁的五個選項）；
// 數值就是寫進裝置的 ledStatus，來源：Synapse UI 的 `za4` 列舉
// （Off:0, Battery_Level:1, Connection_Quality:2, Polling_Rate:3, Dpi_Stage:4）。
internal enum IndicatorLedMode : byte
{
    Off = 0,                 // 關閉
    BatteryLevel = 1,        // 電池電量
    ConnectionQuality = 2,   // 連線品質
    PollingRate = 3,         // 輪循率
    DpiStage = 4,            // DPI 指示
}

// 接收器連線品質列舉（Synapse SDK 的 Connection_Quality_Status_*），值 = 回應 resp[9]。
internal enum DongleQuality : byte
{
    Red = 1,     // 不良
    Orange = 2,  // 尚可
    Yellow = 3,  // 良好
    Green = 4,   // 絕佳
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

    private static byte[] BuildFrame(byte rw, byte cls, byte id, byte txid, byte[]? payload = null)
    {
        // 91B wire = [reportId 0][0][txid][0,0,0][rw@6][class@7][id@8][payload@9…][xor(3..88)@89][0@90]
        //（對照 node-hid 實測成功 frame 與 SDK _createDataSend 的 90B dataSend＝bridge 再補一個 report byte）
        var frame = new byte[91];
        frame[2] = txid;
        frame[6] = rw;
        frame[7] = cls;
        frame[8] = id;
        payload?.CopyTo(frame, 9);
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
    // retries 是給關機路徑用的：那個場合沒時間等裝置慢慢醒。
    private static byte[]? TryQuery(SafeFileHandle device, byte[] frame, byte txid, string[] dbgArgs, int retries = 30)
    {
        bool sent = HidNative.SetFeature(device, frame);
        if (dbgArgs.Contains("--debug")) Console.WriteLine($"   SetFeature={sent} txid={txid}");
        if (!sent) return null;
        for (int retry = 0; retry < retries; retry++)
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

    // 從候選路徑開裝置、送單一指令、等成功回應；全部失敗回 null（裝置睡了或不在）。
    // attempts／retries 只在關機路徑調小（見 TurnOffIndicatorLeds），其餘走預設值。
    private static byte[]? Query(byte rw, byte cls, byte id, byte[]? payload = null, string[]? args = null,
        int attempts = 3, int retries = 30)
    {
        args ??= Array.Empty<string>();
        bool debug = args.Contains("--debug");
        foreach (var path in CandidatePaths())
        {
            if (debug) Console.WriteLine($"[cmd] trying: {path}");
            using var device = HidNative.OpenHid(path);
            if (device.IsInvalid) continue;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                byte txid = (byte)Random.Shared.Next(32);
                var response = TryQuery(device, BuildFrame(rw, cls, id, txid, payload), txid, args, retries);
                if (response is null) continue;
                _cachedPath = path;          // 記住成功路徑，後續直接用
                return response;
            }
        }
        return null;
    }

    // 讀接收器 LED1..3 的顯示項目（Synapse SDK：Get indicator LEDs Configuration → [3,7,149]，
    // 回應 resp[9..11] = LED1..3 的 ledStatus，值見 IndicatorLedMode）；讀不到回 null。
    public static byte[]? ReadIndicatorLeds(string[]? args = null) => Query(0x03, 0x07, 149, null, args)?[9..12];

    // 寫接收器 LED1..3 的顯示項目（Synapse SDK：Set indicator LEDs Configuration → [3,7,21]，
    // payload[0..2] = LED1..3 的 ledStatus）；成功回 true。
    public static bool SetIndicatorLeds(byte[] modes, string[]? args = null) =>
        Query(0x03, 0x07, 21, modes, args) is not null;

    // 三顆 LED 全部關掉（就是寫 0,0,0）。程式結束與系統關機前用：
    // 接收器自己記著顯示項目，程式沒跑時燈照樣亮著。
    // 重試次數砍到預設值的零頭，關機路徑不能停下來等裝置慢慢醒。
    public static bool TurnOffIndicatorLeds() =>
        Query(0x03, 0x07, 21, new byte[3], attempts: 2, retries: 12) is not null;

    // 讀接收器連線品質（Synapse SDK：Get Dongle Connection Quality Status → [1,7,150]，
    // 回應 resp[9] = 1 紅 / 2 橘 / 3 黃 / 4 綠）；讀不到回 null。
    public static DongleQuality? ReadDongleQuality(string[]? args = null)
    {
        var response = Query(0x01, 0x07, 150, null, args);
        return response is null ? null : (DongleQuality)response[9];
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
    // 百分比用線性插值（滿電 4.2V、保護下限 3.4V），見 PercentFromMillivolts。
    private static byte[] BuildBatteryFrame()
    {
        var frame = new byte[64];               // 裝置的 feature report 長度是 64，短了 SetFeature 會回 error 87
        frame[0] = ReportId;
        frame[1] = 0x08; frame[2] = 0x00; frame[3] = 0xFD;
        frame[4] = 0x04; frame[5] = 0x12; frame[6] = 0xF1; frame[7] = 0x03;
        frame[8] = 0x52; frame[9] = 0x01;
        return frame;
    }

    // 線性插值：飽充（充電燈熄）實測 4142mV = 100%，保護下限以 3400mV 為 0%。
    // 用 round-half-up 除法：浮充電壓會抖 ±1mV，truncation 會讓插著電的耳機在 99/100% 之間跳。
    // ponytail: 想更準就用同一台耳機放電錄一組 mv→% 查表。
    private static int PercentFromMillivolts(int mv) =>
        Math.Clamp(((mv - 3400) * 100 + 371) / 742, 0, 100);

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
                            // 耳機休眠中／醒來的過渡窗會回 valid frame 但 mV 是垃圾（實測出現 0%），
                            // 物理合理範圍之外的讀數丟掉重讀，保留 上一輪的 last known。
                            if (millivolts is < 3000 or > 4400)
                            {
                                if (debug) Console.WriteLine($"   millivolts={millivolts} out of sane range, skip");
                                Thread.Sleep(40);
                                continue;
                            }
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
        // 設定接收器 LED1..3 的顯示項目：--set-leds 1,2,3
        //（1=電池電量、2=連線品質、3=輪循率；驗證用，托盤選單才是主要入口）
        int setLedIdx = Array.IndexOf(args, "--set-leds");
        if (setLedIdx >= 0)
        {
            var parts = (setLedIdx + 1 < args.Length ? args[setLedIdx + 1] : "").Split(',');
            if (parts.Length != 3 ||
                !parts.All(p => byte.TryParse(p, out var v) && v is >= (byte)IndicatorLedMode.Off and <= (byte)IndicatorLedMode.DpiStage))
            {
                Console.WriteLine("usage: --set-leds 1,2,3  (0=off, 1=battery, 2=connection, 3=polling, 4=dpi)");
                return;
            }
            Console.WriteLine(RazerMouseBattery.SetIndicatorLeds(parts.Select(byte.Parse).ToArray(), args)
                ? "set receiver LEDs ok"
                : "set receiver LEDs failed (asleep?)");
            return;
        }

        // 自檢模式：node probe 的 C# 版本，直接印結果後離開。
        if (args.Contains("--probe"))
        {
            var mouse = RazerMouseBattery.Read(args);
            Console.WriteLine(mouse.Ok
                ? $"mouse = {mouse.Percent}% charging = {mouse.Charging}"
                : "mouse not found / asleep");
            var leds = RazerMouseBattery.ReadIndicatorLeds(args);
            Console.WriteLine(leds is null
                ? "receiver LED1..3 not readable"
                : $"receiver LED1..3 = {string.Join(' ', leds)}");
            var quality = RazerMouseBattery.ReadDongleQuality(args);
            Console.WriteLine(quality is null
                ? "dongle quality not readable"
                : $"dongle quality = {(byte)quality.Value} ({quality})");
            var headset = AsusHeadsetBattery.Read(args);
            Console.WriteLine(headset.Ok
                ? $"headset = {headset.Percent}% charging = {headset.Charging}"
                : "headset not found / asleep");
            return;
        }

        using var mutex = new Mutex(true, "BatteryWidget_SingleInstance", out var isFirst);
        if (!isFirst) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new HiddenTrayContext());
    }
}

internal sealed class HiddenTrayContext : ApplicationContext
{
    private static readonly int PollSeconds = 30;
    private const int LowPercent = 15;          // 低電量門檻：tooltip 警示跟 LED 動態警示共用
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = PollSeconds * 1000 };
    private BatteryReading _lastGood = new(-1, false);
    private BatteryReading _lastGoodHeadset = new(-1, false);
    private byte[]? _receiverLeds;      // 接收器 LED1..3 的顯示項目；裝置睡著時保留上一次
    private DongleQuality? _lastQuality;   // 接收器連線品質；裝置睡著時保留上一次
    private bool _ledAlertEnabled;      // LED 1 動態警示（設定視窗的開關，預設關）
    private byte? _led1Saved;           // 警示前 LED 1 的設定，結束後還原
    private byte[]? _savedLeds = LoadSavedLeds();   // HKCU 記著的使用者設定（null = 還沒記過）
    private bool _ledsSynced;                        // 啟動後已經跟裝置對過一次
    private string _mouseTip = "Razer Viper V4 Pro：查詢中…";
    private string _headsetTip = "ROG Strix Go 2.4：查詢中…";
    private DateTime _lastUpdate;
    private SettingsForm? _settingsForm;

    internal event Action? StateChanged;   // 每輪查詢完通知設定視窗更新畫面

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
        _tray.DoubleClick += (_, _) => OpenSettings();     // 托盤圖示雙擊＝開設定

        // _savedLeds 在欄位初始化時就讀好了（上面 _savedLeds = LoadSavedLeds()）。
        // 關機／登出時 Windows 不會讓我們跑完 Application.Exit 之後的收尾，所以另外接這條。
        SystemEvents.SessionEnding += (_, _) => TurnOffReceiverLeds();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("設定…", null, (_, _) => OpenSettings());
        menu.Items.Add("手動更新", null, (_, _) => _ = PollAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("離開", null, (_, _) =>
        {
            TurnOffReceiverLeds();
            _tray.Visible = false;
            Application.Exit();
        });
        return menu;
    }

    // 收工前把接收器 LED 熄掉（托盤「離開」與系統關機／登出都走這裡）。
    // 設定本身記在 HKCU，下次啟動的第一輪查詢會寫回裝置。
    private static void TurnOffReceiverLeds()
    {
        // 關機途中 WMI 可能已經收攤（列舉路徑會丟例外），熄不掉就算了，
        // 不能讓它在關機時炸出一個錯誤視窗。
        try { RazerMouseBattery.TurnOffIndicatorLeds(); }
        catch { }
    }

    // 從托盤開的視窗不會自己跳到前景（背景程序沒有前景權），要明講一次。
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    internal void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.WindowState = FormWindowState.Normal;
            SetForegroundWindow(_settingsForm.Handle);
            return;
        }
        _settingsForm = new SettingsForm(this);
        _settingsForm.Show();
        SetForegroundWindow(_settingsForm.Handle);
    }

    // 設定視窗讀的狀態（都跟 tooltip 同一份來源）。
    internal string MouseStatusText => _mouseTip;
    internal string HeadsetStatusText => _headsetTip;
    internal string QualityText => _lastQuality is { } q ? $"接收器連線品質：{QualityName(q)}" : "接收器連線品質：未知";
    internal string LastUpdateText => _lastUpdate == default ? "上次更新：—" : $"上次更新：{_lastUpdate:HH:mm:ss}";
    internal byte[]? ReceiverLeds => _receiverLeds;

    internal bool LedAlertEnabled
    {
        get => _ledAlertEnabled;
        set
        {
            _ledAlertEnabled = value;
            StateChanged?.Invoke();
        }
    }

    internal bool AutoStart
    {
        get => IsAutoStartEnabled();
        set
        {
            try { SetAutoStart(value); }
            catch (Exception ex) { MessageBox.Show($"設定開機自動啟動失敗：{ex.Message}", "電量小工具"); }
            StateChanged?.Invoke();
        }
    }

    // 設定視窗改 LED：實際寫入在背景做，寫完更新畫面。
    internal void ApplyLedSetting(int index, IndicatorLedMode mode) => _ = Task.Run(() => SetReceiverLed(index, mode));

    // 設定視窗按「立即更新」。
    internal void RequestUpdate() => _ = PollAsync();

    // 只改指定那一顆：先讀裝置現值再改，避免蓋掉其他兩顆的設定。
    private void SetReceiverLed(int index, IndicatorLedMode mode)
    {
        var current = _receiverLeds ?? RazerMouseBattery.ReadIndicatorLeds();
        if (current is null)
        {
            MessageBox.Show("讀不到接收器 LED 設定（滑鼠睡著或在忙？）", "電量小工具");
            return;
        }
        var next = (byte[])current.Clone();
        next[index] = (byte)mode;
        if (!RazerMouseBattery.SetIndicatorLeds(next))
        {
            MessageBox.Show("設定接收器 LED 失敗（滑鼠睡著或在忙？）", "電量小工具");
            return;
        }
        _receiverLeds = next;
        if (index == 0) _led1Saved = (byte)mode;   // 使用者改成新設定，警示結束後還原到這個
        // 存「使用者選的」那一份：警示蓋掉的 LED1 記在 _led1Saved，別存成電池電量模式。
        var user = (byte[])next.Clone();
        if (_led1Saved is { } preAlert) user[0] = preAlert;
        SaveLeds(user);
        StateChanged?.Invoke();
    }

    // 動態警示：充電中或低電量時，LED 1 顯示電池電量（該模式低電量會變紅），
    // 狀態結束後還原使用者原本的設定；本來就是電量模式就不動。
    private byte[]? ApplyLedAlert(BatteryReading mouse, byte[]? current)
    {
        if (current is not { Length: 3 } leds) return current;
        bool showBattery = _ledAlertEnabled &&
            (mouse.Charging != ChargingState.NotCharging || mouse.Percent <= LowPercent);
        if (showBattery && leds[0] != (byte)IndicatorLedMode.BatteryLevel)
        {
            var forced = (byte[])leds.Clone();
            forced[0] = (byte)IndicatorLedMode.BatteryLevel;
            if (!RazerMouseBattery.SetIndicatorLeds(forced)) return leds;
            _led1Saved ??= leds[0];        // 記住警示前的設定，結束後還原
            return forced;
        }
        if (!showBattery && _led1Saved is { } saved)
        {
            if (leds[0] != saved)
            {
                var restore = (byte[])leds.Clone();
                restore[0] = saved;
                if (!RazerMouseBattery.SetIndicatorLeds(restore)) return leds;
                leds = restore;
            }
            _led1Saved = null;
        }
        return leds;
    }

    // 接收器 LED 設定：程式結束時會把 LED 熄掉，所以使用者的選擇得自己記著，
    // 下次啟動再寫回裝置（裝置裡那組已經被我們蓋成 0,0,0 了）。
    private const string LedKeyPath = @"Software\BatteryWidget";
    private const string LedValueName = "ReceiverLeds";

    private static byte[]? LoadSavedLeds()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LedKeyPath);
        var parts = (key?.GetValue(LedValueName) as string)?.Split(',');
        if (parts is not { Length: 3 } ||
            !parts.All(p => byte.TryParse(p, out var v) && v <= (byte)IndicatorLedMode.DpiStage))
            return null;
        return parts.Select(byte.Parse).ToArray();
    }

    private static void SaveLeds(byte[] modes)
    {
        using var key = Registry.CurrentUser.CreateSubKey(LedKeyPath);
        key.SetValue(LedValueName, string.Join(',', modes));
    }

    // 開機自啟動：HKCU Run key，只對目前使用者生效，不需要管理員權限。
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "BatteryWidget";

    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        // 比對完整路徑：exe 搬過家時舊值算沒設，選單會顯示未勾選，重勾一次就修好。
        return key?.GetValue(RunValueName) is string value && value == $"\"{Environment.ProcessPath}\"";
    }

    private static void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enable) key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(RunValueName, false);
    }

    private async Task PollAsync()
    {
        // 裝置醒著才順便讀接收器 LED 設定；睡了就保留上次值（跟電量同一個做法）。
        var (mouse, headset, leds, quality) = await Task.Run(() =>
        {
            var m = RazerMouseBattery.Read();
            var cfg = m.Ok ? RazerMouseBattery.ReadIndicatorLeds() : null;
            // 收工會把 LED 熄掉，使用者的設定記在 HKCU：裝置醒了就把記著的寫回裝置；
            // 還沒記過（第一次跑這版）就拿裝置上的現值當使用者的設定記下來。
            // 裝置睡著讀不到就等之後某一輪再對；只對一次，之後不再動使用者的設定。
            if (cfg is not null && !_ledsSynced)
            {
                if (_savedLeds is { } want)
                {
                    if (RazerMouseBattery.SetIndicatorLeds(want))
                    {
                        cfg = want;
                        _ledsSynced = true;
                    }
                }
                else
                {
                    SaveLeds(cfg);
                    _savedLeds = cfg;
                    _ledsSynced = true;
                }
            }
            return (m, AsusHeadsetBattery.Read(), ApplyLedAlert(m, cfg),
                m.Ok ? RazerMouseBattery.ReadDongleQuality() : null);
        });
        if (leds is not null) _receiverLeds = leds;
        if (quality is not null) _lastQuality = quality;

        if (mouse.Ok) _lastGood = mouse;
        if (headset.Ok) _lastGoodHeadset = headset;

        // 圖示維持顯示滑鼠（跟原本一樣）；耳機走 tooltip 第二行。
        var shown = mouse.Ok ? mouse : _lastGood;
        _mouseTip = mouse.Ok
            ? $"Razer Viper V4 Pro：{mouse.Percent}%" +
              (mouse.Charging == ChargingState.Charging ? "（充電中）"
               : mouse.Charging == ChargingState.ChargingFailed ? "（充電異常？）" : "")
            : _lastGood.Ok
                ? $"Razer Viper V4 Pro：{_lastGood.Percent}%（目前連不上，睡著了？）"
                : "沒找到滑鼠（接收器沒插？）";

        // resp[9]=0x0A 的語義其實是「插著線」：插著飽電進浮充後 byte 照樣 0x0A、LED 已熄。
        // 所以插著且讀到滿電（>=100%）改標「充飽」，還在充的才顯示「充電中」。
        _headsetTip = headset.Ok
            ? $"ROG Strix Go 2.4：{headset.Percent}%" +
              (headset.Charging == ChargingState.Charging
                  ? headset.Percent >= 100 ? "（充飽，插著電）" : "（充電中）" : "")
            : _lastGoodHeadset.Ok
                ? $"ROG Strix Go 2.4：{_lastGoodHeadset.Percent}%（目前連不上，睡著了？）"
                : "沒找到耳機";

        _tray.Text = _lastQuality is { } q
            ? $"{_mouseTip}\n{_headsetTip}\n接收器連線品質：{QualityName(q)}"
            : $"{_mouseTip}\n{_headsetTip}";
        _tray.Icon?.Dispose();
        _tray.Icon = MakeIcon(shown.Ok ? shown.Percent : null, shown.Ok && shown.Charging == ChargingState.Charging);
        if (mouse.Ok && mouse.Percent <= LowPercent)
            _tray.ShowBalloonTip(5000, "Razer 電量小工具", "滑鼠快沒電了！", ToolTipIcon.Warning);
        if (headset.Ok && headset.Percent >= 0 && headset.Percent <= LowPercent)
            _tray.ShowBalloonTip(5000, "電量小工具", "耳機快沒電了！", ToolTipIcon.Warning);

        _lastUpdate = DateTime.Now;
        StateChanged?.Invoke();             // 設定視窗開著就順手更新
    }

    // 連線品質的 LED 顏色 → Synapse 連線品質頁的說法。
    private static string QualityName(DongleQuality quality) => quality switch
    {
        DongleQuality.Green => "絕佳",
        DongleQuality.Yellow => "良好",
        DongleQuality.Orange => "尚可",
        _ => "不良",
    };

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

// 設定視窗：托盤選單只留入口，LED 設定、動態警示、開機自啟動都在這裡。
internal sealed class SettingsForm : Form
{
    private static readonly (IndicatorLedMode Mode, string Name)[] LedOptions =
    {
        (IndicatorLedMode.Off, "關閉"),
        (IndicatorLedMode.BatteryLevel, "電池電量"),
        (IndicatorLedMode.ConnectionQuality, "連線品質"),
        (IndicatorLedMode.PollingRate, "輪循率"),
        (IndicatorLedMode.DpiStage, "DPI 指示"),
    };

    private readonly HiddenTrayContext _owner;
    private readonly Label _mouse = new() { AutoSize = true };
    private readonly Label _headset = new() { AutoSize = true };
    private readonly Label _quality = new() { AutoSize = true };
    private readonly Label _updated = new() { AutoSize = true };
    private readonly ComboBox[] _leds = new ComboBox[3];
    private readonly CheckBox _ledAlert = new() { Text = "LED 1 自動顯示電量（充電或低於 15%）", AutoSize = true };
    private readonly CheckBox _autoStart = new() { Text = "開機自動啟動", AutoSize = true };
    private bool _loading;      // 更新畫面時不要把 SelectedIndexChanged 當成使用者操作

    public SettingsForm(HiddenTrayContext owner)
    {
        _owner = owner;
        Text = "電量小工具設定";
        Font = new Font("Microsoft JhengHei UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(400, 400);

        var status = new GroupBox { Text = "目前狀態", Location = new Point(12, 12), Size = new Size(376, 128) };
        _mouse.Location = new Point(16, 28);
        _headset.Location = new Point(16, 52);
        _quality.Location = new Point(16, 76);
        _updated.Location = new Point(16, 100);
        status.Controls.AddRange(new Control[] { _mouse, _headset, _quality, _updated });

        var leds = new GroupBox { Text = "接收器 LED 指示燈", Location = new Point(12, 148), Size = new Size(376, 160) };
        for (int i = 0; i < 3; i++)
        {
            leds.Controls.Add(new Label { Text = $"LED {i + 1}", Location = new Point(16, 30 + 30 * i), AutoSize = true });
            var combo = new ComboBox
            {
                Location = new Point(84, 26 + 30 * i),
                Width = 180,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Tag = i,
            };
            combo.Items.AddRange(LedOptions.Select(o => (object)o.Name).ToArray());
            combo.SelectedIndexChanged += LedChanged;
            _leds[i] = combo;
            leds.Controls.Add(combo);
        }
        _ledAlert.Location = new Point(16, 124);
        _ledAlert.CheckedChanged += (_, _) => { if (!_loading) _owner.LedAlertEnabled = _ledAlert.Checked; };
        leds.Controls.Add(_ledAlert);

        _autoStart.Location = new Point(16, 324);
        _autoStart.CheckedChanged += (_, _) => { if (!_loading) _owner.AutoStart = _autoStart.Checked; };

        var refresh = new Button { Text = "立即更新", Location = new Point(12, 360), Size = new Size(100, 30) };
        refresh.Click += (_, _) => _owner.RequestUpdate();
        var close = new Button { Text = "關閉", Location = new Point(288, 360), Size = new Size(100, 30) };
        close.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { status, leds, _autoStart, refresh, close });
        AcceptButton = refresh;

        _owner.StateChanged += OnStateChanged;
        FormClosed += (_, _) => _owner.StateChanged -= OnStateChanged;
        Populate();
    }

    // 事件可能從背景執行緒來（LED 寫完），拉回 UI 執行緒再動控制項。
    private void OnStateChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(Populate);
        else Populate();
    }

    private void Populate()
    {
        _loading = true;
        try
        {
            _mouse.Text = _owner.MouseStatusText;
            _headset.Text = _owner.HeadsetStatusText;
            _quality.Text = _owner.QualityText;
            _updated.Text = _owner.LastUpdateText;
            var leds = _owner.ReceiverLeds;
            for (int i = 0; i < _leds.Length; i++)
                _leds[i].SelectedIndex = leds is not null && i < leds.Length
                    ? Array.FindIndex(LedOptions, o => (byte)o.Mode == leds[i])
                    : -1;
            _ledAlert.Checked = _owner.LedAlertEnabled;
            _autoStart.Checked = _owner.AutoStart;
        }
        finally { _loading = false; }
    }

    private void LedChanged(object? sender, EventArgs e)
    {
        if (_loading || sender is not ComboBox { Tag: int index } combo || combo.SelectedIndex < 0) return;
        _owner.ApplyLedSetting(index, LedOptions[combo.SelectedIndex].Mode);
    }
}
