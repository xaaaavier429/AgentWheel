# 特戰輪盤 AgentWheel

**拖住檔案，按一下 ⇧ Shift，朝格子一甩，鬆手：轉好了。**

住在 macOS 選單列的格式轉換輪盤，介面改成特戰英豪風格：八角形輪盤、購買選單音效、VICTORY / ACE / THRIFTY / DEFEAT 結算畫面。圖片、PDF、影片、音訊、文件，70 多種轉換和小工具，全部在本機完成、不連網。

macOS 14 以上 · Apple 晶片 / Intel · 免費開源

[![下載 macOS 版](https://img.shields.io/badge/%E4%B8%8B%E8%BC%89-macOS%20%E7%89%88%20.dmg-ff4655?style=for-the-badge&logo=apple&logoColor=white)](https://github.com/xaaaavier429/AgentWheel/releases/latest/download/AgentWheel.dmg)

## 安裝

1. 下載 **[AgentWheel.dmg](https://github.com/xaaaavier429/AgentWheel/releases/latest/download/AgentWheel.dmg)**，雙擊打開，把「AgentWheel」拖進「Applications（應用程式）」。
2. 第一次打開時 macOS 會擋一下（「無法驗證開發者」）。這是個人做的免費開源小工具，沒有付費給蘋果做公證，不是病毒：
   - **macOS 14**：在「應用程式」裡**右鍵**點 AgentWheel →「打開」→ 跳出視窗後再點「打開」。
   - **macOS 15 以後**：先雙擊一次（會被擋下），然後到「系統設定 → 隱私權與安全性」，拉到下面按「強制打開」，輸入開機密碼。
   - 還是打不開：打開「終端機」，貼上下面這行按 Enter，再雙擊打開。

     ```bash
     xattr -dr com.apple.quarantine /Applications/AgentWheel.app
     ```
3. 打開後選單列會多一個八角形小輪盤圖示（Dock 裡沒有），第一次會自動示範一遍。

想自己編譯？見[從原始碼編譯](#從原始碼編譯)。

## 怎麼用

1. 在 Finder 或桌面**拖住**一個或多個檔案（先別放開），拖動中按一下 **⇧ Shift**，輪盤就會在滑鼠位置彈出。
2. 朝想要的格子**方向**移動滑鼠，然後**放開**。
3. 結果存在原檔案旁邊，原檔案不動。

- 再按一次 Shift 切換到第 2 頁「技能 · 工具」；移回中心再放開 = 取消。
- 不想拖曳：選單列圖示 →「選擇檔案打開輪盤…」，直接點格子。

## 結算畫面

| 畫面 | 什麼時候出現 |
| --- | --- |
| **ACE** | 一次處理 5 個以上檔案 |
| **THRIFTY** | 轉完的檔案只剩原本一半以下 |
| **FLAWLESS** | 「去隱私」成功，解除偵查 |
| **VICTORY** | 其他成功的情況 |
| **DEFEAT** | 失敗，會告訴你怎麼修 |

## 被偵查

輪盤打開時，右上角的紅色菱形代表檔案裡能追蹤到你的資訊有多少：照片 GPS 定位、手機型號、拍攝時間、PDF / Word 作者等。指向「去隱私」格子時菱形開始閃，鬆手就解除偵查。

## 設定（選單列圖示）

- **角色配色**：特戰紅、Jett 冰藍、Phoenix 火橘、Sage 青綠、Reyna 紫、Viper 毒綠
- **輪盤大小**、**音效**、**背景虛化**、**完成橫幅**、**登入時自動啟動**
- **自訂音效資料夾…**：放入自己的音效檔（例如 `passed.wav`、`ace.wav`、`wasted.wav`）就會取代內建音效，資料夾裡的「說明.txt」有完整檔名對照

## 可選：更多格式

WebP 需要 cwebp；AVIF、WebM、MP3、Opus、影片變速等需要 ffmpeg。沒裝時這些格子顯示「未裝備」。

```bash
brew install ffmpeg webp
```

## 從原始碼編譯

只需要 Xcode Command Line Tools（`xcode-select --install`），不用裝 Xcode。

```bash
git clone https://github.com/xaaaavier429/AgentWheel.git
cd AgentWheel
./build.sh                 # → build/AgentWheel.app
mv build/AgentWheel.app /Applications/
open /Applications/AgentWheel.app
./package.sh               # → dist/AgentWheel.dmg 和 .zip（Apple 晶片 + Intel 通用）
```

測試用指令（`B=build/AgentWheel.app/Contents/MacOS/AgentWheel`）：

- `$B --snapshot 圖.png --files 照片.jpg --hover 0 --wanted 3`：輸出輪盤截圖
- `$B --banner 圖.png --word ace`（或 `victory` / `thrifty` / `flawless`，加 `--wasted` 是 DEFEAT）：輸出結算畫面
- `$B --icon 圖.png --px 512`：App 圖示
- `$B --sounds 資料夾`：匯出全部內建音效
- `$B --ids` / `$B --run img.to.png 照片.heic`：列出 / 執行轉換動作

## 出處與授權

改編自 [FormatWheel](https://github.com/Elise616/FormatWheel)（© 2026 Elise616），以 [MIT](LICENSE) 授權釋出。

這是粉絲向的個人作品，與 Riot Games 無關。圖示和音效都是用程式碼即時繪製與合成的原創內容，沒有使用任何遊戲素材。
