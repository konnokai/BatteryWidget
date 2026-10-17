# Razer Viper V4 Pro 電量查詢 HID 協定（逆向筆記）

> 逆向來源：Razer Synapse 4（RazerAppEngine 4.0.827）的 web UI JavaScript 與 runtime log，
> 並已對實機驗證成功。日期：2026-10-17。

## 硬體 / 裝置

| 項目 | 值 |
|---|---|
| 滑鼠 | Razer Viper V4 Pro |
| 滑鼠 PID | 0x00E6 (230) |
| 接收器 PID | 0x00E5 (229)，會以滑鼠的 PID 完整代理出 HID 裝置（無獨立介面可捉） |
| VID | 0x1532 |
| 指令介面 | **MI_03**（vendor usage page 12，無 usage 特徵碼） |
| 介面路徑範例 | `\\?\HID#VID_1532&PID_00E6&MI_03#a&16eb29af&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}` |

路徑產生法：先用 WMI `Win32_PnPEntity` 抓 `DeviceID`（格式
`HID\VID_1532&PID_00E6&MI_03\<key>`），將 `\` 全部換成 `#` 後再加上 HID class
GUID `#{4d1e55b2-f16f-11cf-88cb-001111000030}`，前綴 `\\?\`。

## Frame 格式（91 bytes）

指令透過 **Feature Report** 傳送：

```
送出：HidD_SetFeature，91 bytes
[0]  = reportId 0
[1]  = 0（保留）
[2]  = txid（0–31，會輪替；回應須 echo 相同值）
[3..5] = 0
[6]  = rw（0x02 = GET，其他值見下方指令表）
[7]  = command class（電量 = 0x07）
[8]  = command id（電量 = 0x80）
[9..] = payload（查詢類指令通常為空）
[89] = checksum：bytes[3..88] 全部 XOR
[90] = 0
```

⚠️ **欄位位移是最大的坑**：Synapse 內部組的是 90-byte `dataSend`
（`[0][txid]…`），bridge 再補一個 reportId byte 所以整體 +1。
如果只有 txid 移位、後面的 rw/class/id 沒跟著位移，裝置會回 BUSY 到天荒地老。

## 回應

透過 **同一個 handle** 重複 `HidD_GetFeature` 讀取：

```
回應（91 bytes）
[0]  = reportId 0
[1]  = status：0=NOT_READY、1=BUSY、2=SUCCESS、3=FAILED、4=TIMEOUT、5=NOT_SUPPORTED
[2]  = txid echo（要與送出的 txid 一致才是有效回應）
[6]  = rw echo
[7]  = command class echo
[8]  = command id echo
[9]  = batteryId
[10] = 電量 raw (0–255)，% = floor(raw * 100 / 255)
```

## 電量指令

GET_BATTERY_LEVEL = rw `0x02`、class `0x07`、id `0x80`。

已驗證其他指令：

| 名稱 | rw / class / id | 備註 |
|---|---|---|
| Get Firmware Version | 2 / 0 / 129 | 回 firmware 版本 |
| Get Serial No / PID | 2 / 0 / 130 | 回 "PM2630H…" 之類字串 |
| Get Device Charging Status | 2 / 7 / 132 (0x84) | 充電狀態是**獨立指令**，不是 battery 回應的一 byte |

## 實際操作流程（已驗證）

1. `CreateFileW(路徑, GENERIC_READ|GENERIC_WRITE, FILE_SHARE_READ|WRITE, OPEN_EXISTING)`
2. 組 frame → `HidD_SetFeature`
3. 迴圈 `HidD_GetFeature`：等 `status == 2 && txid echo 一致`，才能讀值
   - 裝置醒著時 ≈ 25ms 內回 SUCCESS
   - 裝置睡著時：status 卡 `0` / `1` / `4` 大家都有，等不到 SUCCESS
4. 輪詢失敗每次重來（重組 frame、重發一輪），**不要只重複 GetFeature 不重發 SetFeature**

## 協定驗證資料

| 會觸發電池輪詢的時間點 | Synapse 表現 | 本工具表現 | 說明 |
|---|---|---|---|
| 滑鼠喚醒中 | BUSY→BUSY→SUCCESS（約 12–60ms）、level 78 | 修好後一致 | `products_229_mw` log 證明 |
| 滑鼠睡著 | 卡 TIMEOUT / BUSY | 卡 TIMEOUT / BUSY | 與 Synapse 一致，無法查詢 |
| 充電狀態 | 未逆向（log 只有解析結果） | 本工具直接查 id 0x84 | 插線=1、拔線=0，見上方「充電狀態」 |

## 充電狀態（2026-10-17 已實機驗證）

`GET_BATTERY_LEVEL`（id 0x80）的回應裡**沒有**充電 byte——`resp[9..11]` 只有
batteryId 和 level，`resp[11]` 恆為 0，跟充不要充無關。Synapse SDK
（service worker）內 `getChargingStatus` 是另一條指令：

```
指令：rw 0x02、class 0x07、id 0x84（132），payload 可空
回應：resp[9]  = batteryId
      resp[10] = chargingStatus 列舉：0=NO_CHARGE_BATTERY_FULL、1=CHARGING、2=CHARGING_FAIL
```

實測（node probe + 本工具 `--probe` 一致）：

| 狀態 | level byte | charging byte |
|---|---|---|
| 插線充電中（99%） | 0xFD (99%) | **1（CHARGING）** |
| 拔線後 | 0xFD (99%) | **0** |

SDK 來源（解包的 `3647df12ddf26c37_0`）：`m=[2,7,132]`、desc
`"Get Device Charging Status"`，回應解析 `jsonData={batteryId:data[0],
chargingStatus:data[1], …}`，列舉表 `{NO_CHARGE_BATTERY_FULL:0, CHARGING:1,
CHARGING_FAIL:2}`。

## 已知未解

- 判別「滑鼠睡了」vs「真的斷線」目前只能由連續 TIMEOUT/BUSY 推估。

## 常見錯誤坑（來自本專案的實測）

1. frame 欄位 offset 錯一 byte → 裝置永遠回 BUSY（txid 沒 echo）。
2. 兩個 process 同時輪詢裝置會互相干擾（SetFeature 失敗或回應錯亂）。
3. 系統裡其他廠牌裝置（例：ASUS VID 0x0B05）也有 MI_03 介面，
   查詢前一定要過濾 VID+PID，否則會開到錯的裝置（HidD_SetFeature 回 error 1）。
4. 開始輪詢時裝置可能還在喚醒中，前幾次失敗不代表完全查不到。

## 參考程式碼

- 本專案主程式：`Program.cs`
- 命令列自檢模式：`bin\Release\net10.0-windows\BatteryWidget.exe --probe`

## 相關檔案（逆向證據）

| 檔案 | 內容 |
|---|---|
| `C:\Users\admin\AppData\Local\Temp\opencode\razer-asar\` | Synapse 4 的 app.asar 解開完整內容（JS 原始碼） |
| `razer-asar\electron\UsbRzDeviceAction.js` | Electron 端 HID 橋接（openDevice / sendFeatureReport 實作） |
| `razer-asar\electron\lib\constants.js` | productName / device 記錄 |
| `razer-asar\node_modules\node-rz-hid\` | Razer fork 的 node-hid（HID.node 預編譯元件） |
| Service Worker `CacheStorage\39a7ef2d\3647df12ddf26c37_0` | web UI SDK：`_createDataSend`、`getBatteryLevel`、指令清單定義 |
| `User Data\Logs\products_229_mw *.log` | Synapse 實際執行時的 USB frame log（可驗證回應資料格式） |
| Synapse 完成支援的 `--log-protocol` | 把 Protocol 訊息寫到 `User Data\Logs\protocol.log` |

### 取 SDK key frame 檢視指令

```powershell
rg -a -o "_createDataSend\(e,t,n\)\{.{0,700}" `
  "C:\Users\admin\AppData\Local\Razer\RazerAppEngine\User Data\Default\Service Worker\CacheStorage\3842aea8f625086ac73d0f8e1c00277da03b0e65\39a7ef2d-16ed-4525-9ef4-ddd872d3f89b\3647df12ddf26c37_0"
```
