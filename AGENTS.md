# Agents — BatteryWidget

## 這是什麼

常駐系統匣小工具，查詢 **Razer Viper V4 Pro 滑鼠**和 **ROG Strix Go 2.4 耳機**
的電量，不必開 Razer Synapse 或 Armoury Crate。Windows 11，.NET 10 + WinForms，
僅依賴 `System.Management`（WMI）。

## 專案地圖

```
BatteryWidget\
├── Program.cs            # 全部程式碼：Razer + ASUS 的 HID 協定 + 托盤 UI
├── BatteryWidget.csproj
├── docs\protocol.md      # Razer Viper V4 Pro 的 HID 協定（最重要，先讀）
├── docs\protocol-asus.md # ROG Strix Go 2.4 耳機的 HID 協定（G-Helper 來源）
├── docs\armoury-socket-server.md  # ASUS 服務層逆向筆記（為什麼不走它）
└── bin\Release\net10.0-windows\BatteryWidget.exe
    命令列參數：
    --probe    查滑鼠＋耳機電量後印結果即退出（自檢）
    --debug    連同 --probe 使用，印出 frame debug 資訊
    --set-leds 0..4  設定接收器 LED1..3 顯示項目
               （0=關閉、1=電池電量、2=連線品質、3=輪循率、4=DPI 指示）
```

## 協定重點（沒讀 docs\protocol.md 之前不要改 HID code）

1. 91-byte feature report：`txid@2`、`rw@6 / class@7 / id@8`、
   checksum 在 byte89（bytes 3–88 XOR）
2. 電量指令：rw `0x02`、class `0x07`、id `0x80`；回應 level 在 `resp[10]`，
   % = floor(raw × 100 / 255)
3. 充電狀態是**另一條指令**：rw `0x02`、class `0x07`、id `0x84`（132），
   `resp[10]` = 0 不充電 / 1 充電中 / 2 失敗；battery(id 0x80) 回應裡
   沒有充電 byte，`resp[11]` 恆為 0，不要從那裡猜
4. 讀回應要迴圈直到 `resp[1]==2 && resp[2]==txid` 才代表有效
5. 滑鼠睡著時裝置就是查不到（TIMEOUT/BUSY），**不是程式 bug**；
   UI 要保最後已知值（和 Synapse 相同的做法）

## 動手前注意

- 沒有測試框架，`--probe` 就是最小驗證模式。
  改 frame／協定後一定先跑 `--probe --debug` 看輸出
- 改完 exe 前先 `Stop-Process` 舊的 BatteryWidget 再跑 `--probe`
- 兩個 process 同時讀同一個 HID 介面會互相干擾，
  輪詢實驗一次只留一個 reader
- 系統裡其他裝置（如 ASUS VID 0x0B05）也有名字看起來一樣的 MI_03 介面，
  一定要過濾 `VID_1532 & PID_00E6`，否則會開到錯的裝置 →
  HidD_SetFeature 失敗 error 1
- node 對照版在 `C:\Users\admin\AppData\Local\Temp\opencode\hid-probe\probe-battery.mjs`
  （JS 版協定驗證一直正確，可當 ground truth 對照）

## 目前功能邊界

- 認兩台裝置：Viper V4 Pro（PID 00E6）＋ ROG Strix Go 2.4（PID 18D6）；
  換型號要改各自的 WMI filter
- ASUS 耳機：走 G-Helper 驗證過的 vendor HID（MI_03&COL03、reportId 0xFF），
  百分比用 mV 線性插值（實測飽充 4142mV=100%、保護下限 3400mV=0%），跟 Armoury Crate 不一定一致
- 托盤圖示永遠顯示滑鼠；耳機只在 tooltip 第二行顯示，
  第三行是接收器連線品質（絕佳／良好／尚可／不良；讀不到時保留上次值）
- 托盤選單只有「設定…／手動更新／離開」；LED 設定、動態警示、開機自啟動
  都在設定視窗（`SettingsForm`）
  - 3 顆 LED 的顯示項目：關閉／電池電量／連線品質／輪循率／DPI 指示
    （五個選項跟 Synapse 一樣）；滑鼠睡著時讀、寫都會失敗並跳錯誤訊息
  - LED 動態警示：充電中或電量 ≤15% 時把 LED 1 切成電池電量
    （低電量該模式是紅色），狀態結束後還原使用者設定；預設關
  - 收工熄燈：托盤「離開」與系統關機／登出（`SystemEvents.SessionEnding`）
    都會寫 `0,0,0` 把三顆 LED 熄掉；設定記在
    `HKCU\Software\BatteryWidget`（值 `ReceiverLeds`），啟動後第一輪查詢
    把設定寫回裝置（HKCU 還沒值時就先認養裝置上的現值）。滑鼠睡著時
    寫不進去，那次燈就熄不掉（關機太快、程序被砍也是同一種下場）
- 開機自動啟動：設定視窗的勾選項，寫 HKCU Run key（`BatteryWidget` 值指向目前 exe）；
  設定只有 LED 那一組（見上），輪詢週期 30s、低電量 15% 都是 hardcode const
  （Run key 值名從 RazerBatteryWidget 改為 BatteryWidget，需重新勾選）
- 一個 exe 若是前一版還在跑，記得先 Stop-Process 再 build/probe

## 潛在待辦（user 尚未拍板）

- 喚醒／重連行為調整
- ASUS mV→% 曲線校準（目前跟 G-Helper 一致，跟 Armoury Crate 差一點）

## 環境陷阱

- Windows 本機 console code page 950：跑 dotnet／CLI 時輸出中文可能
  亂碼，判讀用 ASCII 或數字為準
- RazerAppEngine 重啟後 device middleware 不會自己恢復，
  需要重插接收器或重開 Synapse UI 才會重新 claim HID 介面
