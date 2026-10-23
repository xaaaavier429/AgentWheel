特戰輪盤 AgentWheel（Windows 版）

【開始使用】
1. 把整個資料夾解壓縮到固定的位置，例如「文件\AgentWheel」（不要直接在壓縮檔裡執行）。
2. 雙擊 AgentWheel.exe。
   第一次開啟時 Windows 可能跳出「Windows 已保護您的電腦」，按「其他資訊」→「仍要執行」。
3. 右下角通知區域會出現八角形圖示，並自動播一次示範輪盤。
   Windows 11 的圖示可能收在「^」裡，可以把它拖到工作列上常駐。

【怎麼用】
在檔案總管或桌面拖著檔案不放，按一下 Shift，輪盤就會出現。
把檔案拖到想要的格子上放開，結果會存在原檔案旁邊。
滾輪或按 Shift 可以切換「武器（轉換）」和「技能（工具）」兩頁。

【資料夾裡的檔案】
AgentWheel.exe      主程式
ffmpeg.exe          影片、音訊轉檔用（GPL 授權，來源：github.com/BtbN/FFmpeg-Builds）
ffprobe.exe         讀影片資訊用
models\u2netp.onnx  去背用的 AI 模型（Apache-2.0）

【開機自動執行】
通知區域圖示 → 勾選「登入時自動啟動」。
如果之後把資料夾搬到別的地方，要取消再重新勾一次。

【更新到新版本】
1. 通知區域圖示 → 「結束特戰輪盤」。
2. 用新版的檔案覆蓋舊資料夾裡的檔案。
3. 重新開啟 AgentWheel.exe。設定會保留。

【出問題時】
通知區域圖示 →「開啟紀錄資料夾」，把 AgentWheel.log 附上回報。
設定檔在 %APPDATA%\AgentWheel，刪掉就會回到預設值。
