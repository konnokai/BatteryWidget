# ROG Strix Go 2.4 電量查詢 HID 協定（逆向筆記）

> 協定來源：G-Helper（github.com/seerge/g-helper，`app/Peripherals/Headset/Models/StrixGo24.cs`
> 與其基底 `AsusHeadset.cs`、`WindowsUsbProvider.cs`）。
> 本專案在 2026-10-17 對實機（插在dongle上的 Strix Go 2.4）驗證成功。
> 架構背景（為何不走 Armoury 服務）見 `docs\armoury-socket-server.md`。

## 硬體 / 裝置

| 項目 | 值 |
|---|---|
| 耳機 | ROG Strix Go 2.4（走 dongle，PnP 顯示的裝置名就是耳機名） |
| VID | 0x0B05（ASUS） |
| PID | 0x18D6（6358，這也是 ASUS 內部設定的 modelNumber） |
| 指令介面 | **MI_03&COL03**（vendor 集合；COL01、COL02 開了也不收 feature report，`SetFeature` 回 error 1） |
| 介面路徑範例 | `\\?\HID#VID_0B05&PID_18D6&MI_03&COL03#A&5A0B1AB&0&0002#{4d1e55b2-f16f-11cf-88cb-001111000030}` |

路徑產生法跟 Razer 版完全一樣：WMI `Win32_PnPEntity` 撈 `DeviceID`，
`\` 換 `#`，尾端補 HID class GUID。實作上直接過濾
`HID\VID_0B05&PID_18D6&MI_03`，三個集合都試一把（COL03 命中時把
路徑快取起來），之後的查詢就不用再猜。

## Frame 格式（64 bytes，feature report）

```
送出：HidD_SetFeature，64 bytes
[0]  = reportId 0xFF
[1]  = 0x08（查詢）
[2]  = 0x00
[3]  = 0xFD
[4]  = 0x04
[5]  = 0x12
[6]  = 0xF1
[7]  = 0x03
[8]  = 0x52
[9]  = 0x01（battery 是 01；AI 降噪開關是 0x0D/0x0C，見 G-Helper）
[10] = 0（其餘補零到 64）
```

**frame 一定要補滿 64 bytes**。補零省略時 `SetFeature` 回
error 87（ERROR_INVALID_PARAMETER），裝置完全不回應。
（這個坑本專案踩過：第一版只送 10 bytes 直接失敗。）

查電量的時序跟 Razer 幾乎相反：只要一次 `SetFeature`，等 35ms，
然後 `GetFeature` 讀回應。裝置剛從休眠醒來或 USB 閒置時第一次可能還没準備好，
這時**重複 `GetFeature`（間隔 40ms、最多 4 次）**，不要重發 `SetFeature`；
重發會讓查詢重新開始，反而更久等到資料（跟 Razer 的傳承坑同款）。

## 回應（64 bytes）

```
[0]  = reportId 0xFF
[1]  = 0x1B 才是有效電量資料框
[2]  = 0x05（實測固定值）
[3]  = 0xFE（= 0xFD - 1，查詢碼回音）
[4]  = 0x12（回音）
[9]  = 充電狀態：0x0A = 充電中，其他 = 沒充電
[11] = millivolts 低 byte
[12] = millivolts 高 byte
[21] = 自動關機秒數低 byte
[22] = 自動關機秒數高 byte
```

實測資料（耳機放著沒充電）：

```
FF 1B 05 FE 12 04 1F 14 01 03 05 EE 0F 40 15 01 01 17 25 05 20 2C 01 09 FA …
→ millivolts = 0x0FEE = 4078，充電 = 0，自動關機 = (0x01<<8|0x2C)/60 = 5 分鐘
```

## mV → 百分比（線性插值）

本專案不照抄 G-Helper 的五段折線（4078mV 只給 75%、能見度太低），
改用 Li-ion 直覺的線性插值：滿電 4200mV = 100%、保護下限約 3400mV = 0%，
中間按電壓直算，clamp 0–100:

```
percent = clamp((millivolts − 3400) × 100 / 800, 0, 100)
```

實測對照：

| 狀態 | millivolts | 顯示 |
|---|---|---|
| 充電中 | 4167 | 96%（G-Helper 折線會給 100%） |
| 待機沒充電 | 4078 | 85%（Armoury Crate 同時顯示 100%） |
| 之前待機沒充電 | 4077 | 85% |

估計說明：耳機吐的是電壓不是真百分比，任何換算都是近似；充電中電壓
會被抬高，讀數略上飄。要更準就同一台耳機放電錄一組 mV→% 死表替換。

## 實機驗證資料（本專案 probe）

| 狀態 | SetFeature | GetFeature | resp[1] | millivolts | 充電 |
|---|---|---|---|---|---|
| 正常待機（未充電） | True | True | 0x1B | 4078 → 75% | 0 |
| 冷啟動第一次查（就休眠晃回來） | True | True | 0x01（還没就緒）→ 等 40ms 再讀 | — | — |

`--probe` 輸出（成功時）：

```
mouse = 98% charging = NotCharging
headset = 75% charging = NotCharging
```

## 已知未驗證 / 未解

- 充電中的 `resp[9]==0x0A` 是 G-Helper 的判斷，本專案沒實際插線驗證過充電位。
- 耳機完全斷電（自關、dongle 沒查到裝置）時的行為：WMI 枚舉不到路徑，
  就視為不在，跟滑鼠同樣保留最後已知值。
- 藍牙模式（不用 dongle / LEAUDIO）沒驗證：G-Helper 也還沒支援，
  這台裝置藍牙下不出 vendor HID，先不管。

## G-Helper 內其他已驗證的指令（StrixGo24 的變化）

| 用途 | frame |
|---|---|
| 查電量 | `FF 08 00 FD 04 12 F1 03 52 01` |
| AI 降噪開 / 關 | `FF 08 00 FF 04 12 F1 03 52 0D` / `…52 0C` |
| 自動關機 N 分鐘 | `FF 0A 00 FF 04 12 F1 05 52 0B [sec 低][sec 高]`（sec 是秒） |

（本專案只用查電量那段，其餘是抄錄以利日後擴充。）

## 參考程式碼

- 本專案主程式：`Program.cs`（`AsusHeadsetBattery` 類別）
- 命令列自檢：`bin\Release\net10.0-windows\RazerBatteryWidget.exe --probe`

## 協定參考來源

- G-Helper repo：https://github.com/seerge/g-helper
  - `app/Peripherals/Headset/Models/StrixGo24.cs`（frame 常數與 mV 折線）
  - `app/Peripherals/Headset/AsusHeadset.cs`（裝置挑選、報文時序）
  - `app/AnimeMatrix/Communication/Platform/WindowsUsbProvider.cs`（SetFeature/GetFeature 包裝）
- 授權：G-Helper 是 GPL-3。本專案只取協定常數與解析規則自行實作，不是直接複製其原始碼。
