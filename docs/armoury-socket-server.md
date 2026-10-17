# Armoury Crate Lite 架構逆向筆記（AcPowerNotification / ArmourySocketServer）

> 問題：ASUS 的常駐托盤（`AcPowerNotification.exe`）是怎麼拿到耳機電量的？
> 逆向對象是 `C:\Program Files (x86)\ASUS\ArmouryDevice\` 整個 Armoury Crate Lite 樹。
> 日期：2026-10-17。結論直接影響本專案要抄哪一段：最後我們放棄這條路，
> 直接用 G-Helper 驗證過的 HID 指令查耳機，見 `docs\protocol-asus.md`。

## 結論

- `ArmourySocketServer.exe` 是 native x64（不是 .NET），整支是 WebSocket 路由器。
  它的 imports 沒有 hid.dll，字串裡也沒有任何 battery、powerStatus、isCharging。
- 托盤 `AcPowerNotification.exe` 是 .NET 4.8 WPF，只負責連本機 WebSocket 收 JSON 畫 UI。
- 「誰在對耳機讀電量」這題在二進位檔裡找不到答案：全部服務都只是轉發或管理層。
  runtime log 也只有 JSON，看不到 HID frame。
- 所以本專案直接跟耳機的 vendor HID 介面溝通（G-Helper 的做法），不走這一整串服務。

## 執行期架構（由 runtime log 拼出）

```
ROG Strix Go 2.4（USB\VID_0B05&PID_18D6，vendor HID 介面 MI_03&COL03）
      ▲ 這條鏈上每支 exe 都掃不出電量的 HID 指令
      │
ArmourySocketServer.exe
  ‧ 跟 asus_framework 之間：ws://127.0.0.1:1042/?role=socketServer
  ‧ 自己另開 AC 9012 / HTML 9013 / Debug 9014（registry MainSDK 有記）
  ‧ 對 trays 推 cmd 10200 的 JSON；用 CoCreateInstance 跟 COM 服務查裝置狀態
      │
asus_framework.exe（Electron 主程序）
      │ log 在 C:\ProgramData\ASUS\ARMOURY CRATE Diagnosis\Framework\<hash>\
      │ 自己聽 1042；DeviceManager 列出 USB [0B0518D6 …]；載入 modules\headset\index.js
      │ deviceService（View\6358\service.js）連
      │  ws://127.0.0.1:1042/?role=deviceService&deviceType=headset&pid=6358
      ▼
AcPowerNotification.exe（托盤）
  ‧ 連 ws://127.0.0.1:{HTTPPort}/?role=trayIcon，HTTPPort 讀
    HKLM\Software\Wow6432Node\ASUS\ArmouryDevice\HTTPPort，讀不到就用 1042
  ‧ 收到 cmd 10200 更新畫面；啟動或按按鈕時送 cmd 20102 要求全機電量
```

Framework log 的實錄（同一天）：

```
[Server] listening on port 1042, pid is 39020
[DeviceManager] getConnectUsbDeviceList: [{"vidpid":"0B0518D6"} …]
[Server] initialize Main SDK / SDK list count 12
[Websocket] trayIcon -> alertPage,deviceService, cmd: 20102
[Websocket] 6358 deviceService -> trayIcon, cmd: 10200   （約每 5.45 秒一輪）
```

托盤 log 的實錄：

```
recv msg: {"cmd":"10200","modelNumber":"6358","powerStatus":100,
           "isCharging":false,"isLowBattery":false,"levels":4,
           "isDeviceAlive":true,"isHPC":false}
```

## 個別細節

### AcPowerNotification.exe（托盤，.NET 4.8）

| 主題 | 內容 |
|---|---|
| 身分 | 版本 1.0.12.34，sha256 `62595E06…2C2` |
| WebSocketService | 連 `ws://127.0.0.1:{HTTPPort}/?role=trayIcon`，每 10 秒檢查斷線就重連 |
| 20102 請求 | `{"command":"broadcastEvent","target":{"role":["alertPage","deviceService"]},"msg":{"cmd":"20102"}}` |
| 10200 解析 | `modelNumber`（對 deviceinfo.ini 換卡片索引）、`powerStatus`（數字，或 {L,R,Case}）、`levels`（電池圖示 1–5）、`isCharging`、`isHPC`、`isLowBattery`、`isDeviceAlive` |
| 顯示 | 收到的 `powerStatus` 就是百分比字串，直接補 `%` 顯示，不做任何換算 |

### ArmourySocketServer.exe（路由器，native x64）

- 版本 2.01.57 相關（registry MainSDK 有 Version），sha256 `A099E97A…154`
- PDB 路徑：`D:\Jenkins\workspace\AC_MainSDK\ArmourySocketServer_master\x64\Release\ArmourySocketServer.pdb`
- 用 websocketpp + nlohmann JSON + tinyxml2；連線前檢查 Origin，port 寫回 registry
- 跟 COM 服務的介面：`[QueryAllDeviceStatusFromLS] CoCreateInstance → get_QueryAllDeviceStatus`；
  GUID `{5AAA201A-…}`（sensorbox）、`{5DEFE16A-…}`（noisecancelingengine）、`{6A6BD344-…}`（festival effect）、
  有 `[LightingServiceMonitor]` 開 event sink 監看 LightingService 存活
- 裝置相關的動態載入：`AuraIC_x64.dll`（GetProductName 用，簽章驗證失敗就 fallback）
- 沒有任何 HID frame 常數，也沒有 `powerStatus`、`isCharging` 字串

周邊裝置的偵測分工（跟電量無關）：WMI `Win32_USBHub`＋registry
`BTHPORT\Parameters\Devices` 對 VID/PID 建「哪張卡片」，native DLL
（Booster_DLL.dll、UnifyScanner.dll）回報插上拔下。

## 周邊元件（查過、排除）

| 元件 | 判斷依據 | 結果 |
|---|---|---|
| `FMService_USB64.exe`（System32，服務名 FMUSBAPOService） | 216 個 exports 全是 ForteMic/Speaker EQ 控制介面；二進位裡的 battery 是 `GUID_BATTERY_PERCENTAGE_REMAINING` 後面接的 %d，指電腦本身的電池 | 跟耳機電量無關 |
| `ledcontrolservice3.exe` + `AudioSDK\Protocol\AudioLEDControl\R2Clib*.dll` | 目錄名為 AudioLEDControl、config.ini 只有版本與 DeviceIdMapping | 定位為燈光控制服務 |
| `FMAPOCTLAPI.dll` | 216 個 exports 全是 mic/EQ 介面 | 沒有電量 API |
| `ArmourySDK.dll`、`ArmouryAudioAgent.exe`、`ArmourySwAgent.exe`、`ArmouryCrate.Service.exe`、`ArmouryCrate.UserSessionHelper.exe` | binary 全文掃描 battery/powerStatus/isCharging | 全部沒有 |
| `ac_node_addon.node`（Electron native addon） | 同目錄的 UnifyDeviceDetector/UnifyScanner.dll、托盤引用的 UnifyDongle | 是 dongle 掃描器，不管電量 |

## 為什麼不再沿這條路挖

1. 鏈路太長：HID → 未知的讀值服務 → socket server → asus_framework（Electron）→ WebSocket → 托盤，每一層都各自斷線重連、各自有緩衝與逾時。
2. 托盤拿到的 `powerStatus` 已經是算好的百分比；想拿一手的 HID 回應就必須定位最底層那支服務，而靜態掃描六七支二進位都沒有 battery 字串，_cost 顯然高於直接開 HID。
3. G-Helper 一支 `StrixGo24.cs` 就把和耳機的介面寫完了：64 bytes feature report，Set → 等 35ms → Get，本專案實機驗證成功。

## 逆向過程留檔

| 檔案 | 內容 |
|---|---|
| `C:\Users\admin\AppData\Local\Temp\opencode\acpower-decomp\` | 托盤 exe 的 dnSpy 反編譯輸出（C# 專案） |
| `C:\Users\admin\AppData\Local\Temp\opencode\asus-battery-probe2.ps1` | 驗證 HID 協定用的 PowerShell probe（照 G-Helper frame） |
| `C:\ProgramData\ASUS\ARMOURY CRATE Diagnosis\MainSDK\AcPowerNotification\<日期>.log` | 托盤收到的 10200 JSON 原文 |
| `C:\ProgramData\ASUS\ARMOURY CRATE Diagnosis\Framework\<hash>\Framework.*.log` | asus_framework 的連線與 deviceService 生態 |

## 相關檔案

- G-Helper 的耳機實作：`app/Peripherals/Headset/AsusHeadset.cs`、`app/Peripherals/Headset/Models/StrixGo24.cs`（github.com/seerge/g-helper）
- 本專案的 ASUS HID 協定：`docs\protocol-asus.md`
