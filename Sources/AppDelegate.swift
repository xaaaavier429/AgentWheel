import AppKit
import ServiceManagement

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var statusItem: NSStatusItem!
    private let watcher = DragWatcher()

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = Icons.menuBar()
        statusItem.button?.toolTip = "特戰輪盤：拖著檔案按 ⇧ Shift"
        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu

        watcher.onShift = { files in
            Overlay.shared.open(files: files, mode: .drag, at: NSEvent.mouseLocation)
        }
        watcher.onRelease = { Overlay.shared.mouseReleased() }
        watcher.isWheelOpen = { Overlay.shared.isOpen }
        watcher.start()
        Overlay.shared.prepare()

        if !Settings.shared.welcomed {
            Settings.shared.welcomed = true
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) { [weak self] in self?.showDemo() }
        }

        // `AgentWheel --preview` (another process) asks this instance to flash the demo wheel.
        DistributedNotificationCenter.default().addObserver(forName: Self.previewNotification, object: nil, queue: .main) { [weak self] _ in
            self?.showDemo()
            DispatchQueue.main.asyncAfter(deadline: .now() + 6) {
                if Overlay.shared.isOpen { Overlay.shared.close(animated: true) }
            }
        }
    }

    static let previewNotification = Notification.Name("com.agentwheel.preview")

    // MARK: - Menu

    func menuNeedsUpdate(_ menu: NSMenu) {
        // Picks up sound files dropped into the custom folder since last time.
        SFX.shared.reload()
        menu.removeAllItems()
        menu.addItem(disabled("特戰輪盤"))
        menu.addItem(disabled("拖著檔案按 ⇧ Shift 撥出輪盤"))
        menu.addItem(.separator())
        menu.addItem(item("看看效果（示範）", #selector(showDemo)))
        menu.addItem(item("選擇檔案開啟輪盤…", #selector(chooseFiles)))
        menu.addItem(.separator())

        let recent = Recents.shared.items
        let recentMenu = NSMenu()
        for url in recent {
            let folder = FileManager.default.displayName(atPath: url.deletingLastPathComponent().path)
            let entry = item("\(url.lastPathComponent)  —  \(folder)", #selector(revealRecent(_:)))
            entry.representedObject = url
            entry.isEnabled = FileManager.default.fileExists(atPath: url.path)
            entry.image = NSWorkspace.shared.icon(forFile: url.path)
            entry.image?.size = NSSize(width: 16, height: 16)
            recentMenu.addItem(entry)
        }
        if recent.isEmpty { recentMenu.addItem(disabled("還沒有轉換過檔案")) }
        let recentItem = submenu("最近轉換（點選在 Finder 中顯示）", recentMenu)
        menu.addItem(recentItem)
        menu.addItem(.separator())

        let crewMenu = NSMenu()
        for crew in Crew.allCases {
            let entry = item(crew.title, #selector(pickCrew(_:)))
            entry.representedObject = crew.rawValue
            entry.state = Settings.shared.crew == crew ? .on : .off
            entry.image = swatch(crew.color)
            crewMenu.addItem(entry)
        }
        menu.addItem(submenu("角色配色", crewMenu))

        let sizeMenu = NSMenu()
        for size in WheelSize.allCases {
            let entry = item(size.title, #selector(pickSize(_:)))
            entry.representedObject = size.rawValue
            entry.state = Settings.shared.wheelSize == size ? .on : .off
            sizeMenu.addItem(entry)
        }
        menu.addItem(submenu("輪盤大小", sizeMenu))

        menu.addItem(toggle("音效", Settings.shared.sound, #selector(toggleSound)))
        menu.addItem(item("自訂音效資料夾…", #selector(openSoundFolder)))
        menu.addItem(toggle("背景虛化", Settings.shared.blur, #selector(toggleBlur)))
        menu.addItem(toggle("完成橫幅（VICTORY / ACE）", Settings.shared.banner, #selector(toggleBanner)))
        menu.addItem(toggle("登入時自動啟動", SMAppService.mainApp.status == .enabled, #selector(toggleLogin)))
        menu.addItem(.separator())
        menu.addItem(disabled(toolStatus()))
        menu.addItem(.separator())
        menu.addItem(item("結束特戰輪盤", #selector(quit), key: "q"))
    }

    private func item(_ title: String, _ action: Selector, key: String = "") -> NSMenuItem {
        let entry = NSMenuItem(title: title, action: action, keyEquivalent: key)
        entry.target = self
        return entry
    }

    private func toggle(_ title: String, _ on: Bool, _ action: Selector) -> NSMenuItem {
        let entry = item(title, action)
        entry.state = on ? .on : .off
        return entry
    }

    private func disabled(_ title: String) -> NSMenuItem {
        let entry = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        entry.isEnabled = false
        return entry
    }

    private func submenu(_ title: String, _ menu: NSMenu) -> NSMenuItem {
        let entry = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        entry.submenu = menu
        return entry
    }

    private func swatch(_ color: NSColor) -> NSImage {
        NSImage(size: NSSize(width: 12, height: 12), flipped: false) { rect in
            color.setFill()
            NSBezierPath(ovalIn: rect.insetBy(dx: 1, dy: 1)).fill()
            return true
        }
    }

    private func toolStatus() -> String {
        let missing = ["ffmpeg", "cwebp"].filter { !Shell.has($0) }
        if missing.isEmpty { return "彈藥充足：ffmpeg ✓  cwebp ✓" }
        return "未裝備：" + missing.joined(separator: "、") + "（brew install 後解鎖更多格式）"
    }

    // MARK: - Actions

    @objc func showDemo() {
        let mouse = NSEvent.mouseLocation
        guard let screen = NSScreen.screens.first(where: { NSMouseInRect(mouse, $0.frame, false) }) ?? NSScreen.main else { return }
        let sample = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Desktop/IMG_2049.HEIC")
        var context = Catalog.context(for: [sample])
        context.headline = "IMG_2049.HEIC"
        context.summary = "2.4 MB · 4032×3024 · HEIC"
        let center = CGPoint(x: screen.frame.midX, y: screen.frame.midY)
        Overlay.shared.open(files: [sample], mode: .demo, at: center, context: context)
    }

    @objc func chooseFiles() {
        NSApp.activate(ignoringOtherApps: true)
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = true
        panel.prompt = "開啟輪盤"
        panel.message = "選好檔案後，用滑鼠點輪盤上的格子"
        guard panel.runModal() == .OK, !panel.urls.isEmpty, let screen = NSScreen.main else { return }
        Overlay.shared.open(files: panel.urls, mode: .click, at: CGPoint(x: screen.frame.midX, y: screen.frame.midY))
    }

    @objc func pickCrew(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let crew = Crew(rawValue: raw) else { return }
        Settings.shared.crew = crew
        showDemo()
    }

    @objc func pickSize(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let size = WheelSize(rawValue: raw) else { return }
        Settings.shared.wheelSize = size
        showDemo()
    }

    @objc func revealRecent(_ sender: NSMenuItem) {
        guard let url = sender.representedObject as? URL else { return }
        NSWorkspace.shared.activateFileViewerSelecting([url])
    }

    @objc func toggleSound() { Settings.shared.sound.toggle() }

    /// Opens (creating if needed) the folder where sound files replace the synthesized ones.
    @objc func openSoundFolder() {
        let folder = SFX.customFolder
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let readme = folder.appendingPathComponent("說明.txt")
        if !FileManager.default.fileExists(atPath: readme.path) {
            let text = """
            把音效檔放進這個資料夾，就會取代內建的合成音效（支援 wav、mp3、m4a、aiff、caf）。
            檔名要對應下面的名稱，例如 passed.wav。放好後重新點一次選單列圖示就會生效。

            open        打開輪盤
            tick        游標移到格子上
            page        切換 武器 / 技能 頁
            denied      點到不能用的格子
            passed      成功（VICTORY / THRIFTY）
            ace         成功（ACE，一次 5 個以上檔案）
            evaded      去隱私成功（FLAWLESS）
            wasted      失敗（DEFEAT）
            wanted      偵測到隱私資訊
            plant       開始處理（下包進度條出現）
            rackPistol / rackRifle / rackBolt / rackPump / rackHeavy / pinPull / bladeDraw / meleeSwing
                        各類武器的裝備聲

            刪掉檔案就會換回內建音效。
            """
            try? text.write(to: readme, atomically: true, encoding: .utf8)
        }
        NSWorkspace.shared.open(folder)
    }
    @objc func toggleBlur() { Settings.shared.blur.toggle() }
    @objc func toggleBanner() { Settings.shared.banner.toggle() }

    @objc func toggleLogin() {
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
            }
        } catch {
            NSApp.activate(ignoringOtherApps: true)
            let alert = NSAlert()
            alert.messageText = "沒能設定自動啟動"
            alert.informativeText = "\(error.localizedDescription)\n\n可以在「系統設定 → 通用 → 登入項」裡手動新增特戰輪盤。"
            alert.runModal()
        }
    }

    @objc func quit() { NSApp.terminate(nil) }
}

enum Icons {
    /// Menu bar glyph: a little octagonal eight-slot wheel with the top slot lit.
    static func menuBar() -> NSImage {
        let image = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { _ in
            guard let cg = NSGraphicsContext.current?.cgContext else { return false }
            let c = CGPoint(x: 9, y: 9)
            let step = CGFloat.pi / 4
            for i in 0..<8 {
                let t = CGFloat(i) * step
                cg.addPath(Geo.facet(center: c, r0: 4.0, r1: 7.8, from: t - step / 2 + 0.09, to: t + step / 2 - 0.09))
                cg.setFillColor(NSColor.black.withAlphaComponent(i == 0 ? 1 : 0.5).cgColor)
                cg.fillPath()
            }
            return true
        }
        image.isTemplate = true
        return image
    }

    /// App icon artwork (rendered by `--icon`, packed into AppIcon.icns by build.sh):
    /// a navy tile with a red slash, and the octagonal wheel with its top slot lit red.
    static func app(pixels: Int) -> NSImage {
        let side = CGFloat(pixels)
        return NSImage(size: NSSize(width: side, height: side), flipped: false) { rect in
            guard let cg = NSGraphicsContext.current?.cgContext else { return false }
            let s = side / 1024
            let tile = rect.insetBy(dx: 100 * s, dy: 100 * s)
            let shape = CGPath(roundedRect: tile, cornerWidth: 185 * s, cornerHeight: 185 * s, transform: nil)
            cg.saveGState()
            cg.setShadow(offset: CGSize(width: 0, height: -10 * s), blur: 28 * s, color: NSColor(white: 0, alpha: 0.45).cgColor)
            cg.addPath(shape)
            cg.setFillColor(Theme.navy.cgColor)
            cg.fillPath()
            cg.restoreGState()

            cg.saveGState()
            cg.addPath(shape)
            cg.clip()
            let bg = CGGradient(colorsSpace: nil, colors: [
                Theme.navy.mixed(with: .white, 0.08).cgColor, Theme.navy.cgColor, Theme.navy.mixed(with: .black, 0.35).cgColor,
            ] as CFArray, locations: [0, 0.5, 1])!
            cg.drawLinearGradient(bg, start: CGPoint(x: tile.minX, y: tile.maxY), end: CGPoint(x: tile.maxX, y: tile.minY), options: [])
            // A bold red slash across the lower-left corner.
            let slash = CGMutablePath()
            slash.move(to: CGPoint(x: tile.minX, y: tile.minY + 300 * s))
            slash.addLine(to: CGPoint(x: tile.minX + 300 * s, y: tile.minY))
            slash.addLine(to: CGPoint(x: tile.minX + 390 * s, y: tile.minY))
            slash.addLine(to: CGPoint(x: tile.minX, y: tile.minY + 390 * s))
            slash.closeSubpath()
            cg.addPath(slash)
            cg.setFillColor(Theme.red.withAlphaComponent(0.9).cgColor)
            cg.fillPath()

            let c = CGPoint(x: rect.midX, y: rect.midY)
            let R = 300 * s, r = 180 * s
            let step = CGFloat.pi / 4
            for i in 0..<8 {
                let t = CGFloat(i) * step
                cg.addPath(Geo.facet(center: c, r0: r, r1: R, from: t - step / 2, to: t + step / 2))
                cg.setFillColor(i == 0 ? Theme.red.cgColor : Theme.bone.withAlphaComponent(0.1).cgColor)
                cg.fillPath()
            }
            cg.setStrokeColor(Theme.bone.withAlphaComponent(0.75).cgColor)
            cg.setLineWidth(6 * s)
            for i in 0..<8 {
                let t = CGFloat(i) * step + step / 2
                let k = 1 / cos(step / 2)
                cg.move(to: Geo.point(c, r * k, t))
                cg.addLine(to: Geo.point(c, R * k, t))
            }
            cg.strokePath()
            cg.addPath(Geo.octagon(center: c, apothem: R))
            cg.setLineWidth(8 * s)
            cg.strokePath()
            cg.addPath(Geo.octagon(center: c, apothem: r))
            cg.setLineWidth(5 * s)
            cg.strokePath()
            cg.restoreGState()

            Draw.text("⇧", TextStyle(font: Fonts.badge(150 * s), color: Theme.bone, shadow: 0), center: CGPoint(x: c.x, y: c.y - 6 * s))
            for (i, name) in ["PNG", "JPG", "MP4", "PDF"].enumerated() {
                let t = CGFloat(i * 2) * step
                Draw.text(name, TextStyle(font: Fonts.badge(60 * s), color: Theme.bone, shadow: 0), center: Geo.point(c, (r + R) / 2, t))
            }
            return true
        }
    }
}
