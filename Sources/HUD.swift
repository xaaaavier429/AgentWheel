import AppKit

/// How a finished job is announced, borrowing Valorant's end-of-round callouts.
enum Outcome: String, CaseIterable {
    case victory, ace, thrifty, flawless, defeat

    var word: String { rawValue.uppercased() }

    /// Small line above the big word.
    var kicker: String {
        switch self {
        case .victory: return "回合勝利"
        case .ace: return "一口氣清空全部目標"
        case .thrifty: return "體積省下一大半"
        case .flawless: return "零暴露 · 完美回合"
        case .defeat: return "回合落敗"
        }
    }

    var color: NSColor {
        switch self {
        case .victory, .flawless: return Theme.victory
        case .ace, .thrifty: return Theme.gold
        case .defeat: return Theme.wasted
        }
    }

    /// Sound played after the weapon is equipped.
    var sound: SFX.Kind {
        switch self {
        case .ace: return .ace
        case .flawless: return .evaded
        case .defeat: return .wasted
        case .victory, .thrifty: return .passed
        }
    }

    /// Picks the callout for a finished job: FLAWLESS for a privacy wipe, ACE for 5+ files,
    /// THRIFTY when the result is half the size or less, VICTORY otherwise.
    static func pick(files: Int, before: Int64, after: Int64, evaded: Bool) -> Outcome {
        if evaded { return .flawless }
        if files >= 5 { return .ace }
        if before > 0, after > 0, after * 2 <= before { return .thrifty }
        return .victory
    }
}

/// VICTORY / ACE / THRIFTY / FLAWLESS / DEFEAT splash shown after an action finishes.
final class BannerView: NSView {
    static let height: CGFloat = 280

    private var outcome: Outcome = .victory
    private var title = ""
    private var detail = ""
    /// DEFEAT covers the whole screen and darkens it.
    var fullScreen = false
    /// Intro animation, 0...1.
    var progress: CGFloat = 1 { didSet { needsDisplay = true } }

    override var isOpaque: Bool { false }

    func configure(passed: Bool, title: String, detail: String, outcome: Outcome? = nil) {
        self.outcome = outcome ?? (passed ? .victory : .defeat)
        self.title = title
        self.detail = detail
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let cg = NSGraphicsContext.current?.cgContext else { return }
        let ease = 1 - pow(1 - min(max(progress, 0), 1), 3)
        let color = outcome.color
        if fullScreen {
            let whole = bounds
            let shade = CGGradient(colorsSpace: nil, colors: [Theme.red.withAlphaComponent(0.12).cgColor, NSColor(white: 0, alpha: 0.74).cgColor] as CFArray, locations: [0, 1])!
            cg.saveGState()
            cg.setAlpha(ease)
            let mid = CGPoint(x: whole.midX, y: whole.midY)
            cg.drawRadialGradient(shade, startCenter: mid, startRadius: 0, endCenter: mid, endRadius: hypot(whole.width, whole.height) / 2, options: [.drawsAfterEndLocation])
            cg.restoreGState()
        }
        // The band and text sit in a strip of `height`, centred (a little high on the full-screen variant).
        let b = fullScreen ? CGRect(x: 0, y: bounds.midY + 40 - BannerView.height / 2, width: bounds.width, height: BannerView.height) : bounds

        // Navy band that fades out towards both screen edges and softly at the top and bottom.
        let band = CGRect(x: 0, y: b.midY - 122, width: b.width, height: 244)
        let navy = Theme.navy
        cg.saveGState()
        cg.setAlpha(ease)
        cg.clip(to: band)
        cg.beginTransparencyLayer(auxiliaryInfo: nil)
        let across = CGGradient(colorsSpace: nil, colors: [
            navy.withAlphaComponent(0).cgColor, navy.withAlphaComponent(0.84).cgColor,
            navy.withAlphaComponent(0.84).cgColor, navy.withAlphaComponent(0).cgColor,
        ] as CFArray, locations: [0.06, 0.3, 0.7, 0.94])!
        cg.drawLinearGradient(across, start: CGPoint(x: 0, y: 0), end: CGPoint(x: b.width, y: 0), options: [])
        cg.setBlendMode(.destinationIn)
        let down = CGGradient(colorsSpace: nil, colors: [
            NSColor(white: 0, alpha: 0).cgColor, NSColor(white: 0, alpha: 1).cgColor,
            NSColor(white: 0, alpha: 1).cgColor, NSColor(white: 0, alpha: 0).cgColor,
        ] as CFArray, locations: [0, 0.16, 0.84, 1])!
        cg.drawLinearGradient(down, start: CGPoint(x: 0, y: band.minY), end: CGPoint(x: 0, y: band.maxY), options: [])
        cg.endTransparencyLayer()
        cg.restoreGState()

        // Two thin lines in the outcome colour, sliding out from the centre.
        let reach = b.width * 0.3 * ease
        cg.saveGState()
        cg.setAlpha(ease)
        color.withAlphaComponent(0.85).setFill()
        CGRect(x: b.midX - reach, y: band.maxY - 20, width: reach * 2, height: 2).fill()
        CGRect(x: b.midX - reach, y: band.minY + 18, width: reach * 2, height: 2).fill()
        cg.restoreGState()

        // Small kicker, then the big word punching in from slightly larger.
        cg.saveGState()
        cg.setAlpha(ease)
        Draw.text(outcome.kicker, TextStyle(font: Fonts.hud(16, .bold), color: color, shadow: 3, kern: 3),
                  center: CGPoint(x: b.midX + 1.5, y: b.midY + 89))
        cg.restoreGState()

        let kern: CGFloat = 10
        let zoom = 1 + 0.22 * (1 - ease)
        cg.saveGState()
        cg.setAlpha(ease)
        // DIN Condensed sits high in its line box, so the word is placed a little below where it should look.
        cg.translateBy(x: b.midX + kern / 2, y: b.midY + 14)
        cg.scaleBy(x: zoom, y: zoom)
        Draw.text(outcome.word, TextStyle(font: Fonts.splash(outcome == .defeat ? 116 : 110), color: color, shadow: 14, kern: kern),
                  center: .zero)
        cg.restoreGState()

        cg.saveGState()
        cg.setAlpha(ease)
        let line = CGRect(x: b.midX - 230, y: b.midY - 28, width: 460, height: 1)
        Theme.bone.withAlphaComponent(0.45).setFill()
        line.fill()
        Draw.text(title, TextStyle(font: Fonts.hud(21, .demi), color: Theme.bone, shadow: 4), center: CGPoint(x: b.midX, y: b.midY - 46), maxWidth: b.width * 0.7)
        Draw.text(detail, TextStyle(font: Fonts.hud(16, .medium), color: Theme.bone.withAlphaComponent(0.75), shadow: 3),
                  center: CGPoint(x: b.midX, y: b.midY - 69), maxWidth: b.width * 0.7)
        cg.restoreGState()
    }
}

final class BannerController {
    static let shared = BannerController()

    private var panel: NSPanel?
    private let view = BannerView(frame: .zero)
    private var timer: Timer?
    private var startedAt = Date()
    private var hideWork: DispatchWorkItem?

    func show(passed: Bool, title: String, detail: String, on screen: NSScreen?, outcome: Outcome? = nil) {
        // Failures always show; the success splash can be switched off in the menu.
        guard !passed || Settings.shared.banner, let screen = screen ?? NSScreen.main else { return }
        let frame = passed ? CGRect(x: screen.frame.minX, y: screen.frame.midY - BannerView.height / 2 + 40,
                                    width: screen.frame.width, height: BannerView.height) : screen.frame
        view.fullScreen = !passed
        let panel = self.panel ?? Overlay.makePanel()
        panel.ignoresMouseEvents = true
        panel.contentView = view
        panel.setFrame(frame, display: false)
        panel.alphaValue = 1
        self.panel = panel
        view.configure(passed: passed, title: title, detail: detail, outcome: outcome)
        view.progress = 0
        panel.orderFrontRegardless()

        startedAt = Date()
        timer?.invalidate()
        let t = Timer(timeInterval: 1 / 60, repeats: true) { [weak self] timer in
            guard let self else { timer.invalidate(); return }
            let p = CGFloat(Date().timeIntervalSince(self.startedAt) / 0.28)
            self.view.progress = min(p, 1)
            if p >= 1 { timer.invalidate() }
        }
        RunLoop.main.add(t, forMode: .common)
        timer = t

        hideWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let panel = self?.panel else { return }
            NSAnimationContext.runAnimationGroup({ ctx in
                ctx.duration = 0.35
                panel.animator().alphaValue = 0
            }, completionHandler: {
                panel.orderOut(nil)
            })
        }
        hideWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + (passed ? 2.2 : 3.2), execute: work)
    }
}

/// Bottom-right progress, styled as the spike-plant bar: a blinking spike light and a fill bar.
final class BusyView: NSView {
    var text = "" { didSet { needsDisplay = true } }
    var progress: Double = 0 { didSet { needsDisplay = true } }
    var angle: CGFloat = 0 { didSet { needsDisplay = true } }

    override var isOpaque: Bool { false }

    override func draw(_ dirtyRect: NSRect) {
        guard let cg = NSGraphicsContext.current?.cgContext else { return }
        let b = bounds
        Panel.fill(b, accent: Theme.red, flipped: false, alpha: 0.88)

        // The spike's light blinks while it is being planted.
        let blink = 0.35 + 0.65 * (0.5 + 0.5 * sin(angle * 1.6))
        cg.saveGState()
        cg.addPath(WantedView.diamond(center: CGPoint(x: 26, y: b.midY + 4), half: 10))
        cg.setFillColor(Theme.red.withAlphaComponent(blink).cgColor)
        cg.fillPath()
        cg.restoreGState()

        let style = TextStyle(font: Fonts.hud(16, .demi), color: Theme.bone, shadow: 0, alignment: .left)
        Draw.draw(text, style, in: CGRect(x: 44, y: b.midY - 6, width: b.width - 60, height: 22))

        // Plant bar: real progress when known, otherwise a sweeping segment.
        let track = CGRect(x: 44, y: 10, width: b.width - 60, height: 4)
        Theme.bone.withAlphaComponent(0.15).setFill()
        track.fill()
        Theme.red.setFill()
        if progress > 0.01 {
            CGRect(x: track.minX, y: track.minY, width: track.width * CGFloat(min(progress, 1)), height: track.height).fill()
        } else {
            let phase = (angle / 10).truncatingRemainder(dividingBy: 1)
            let w = track.width * 0.25
            let seg = CGRect(x: track.minX - w + (track.width + w) * phase, y: track.minY, width: w, height: track.height).intersection(track)
            if !seg.isNull { seg.fill() }
        }
    }
}

final class BusyHUD {
    static let shared = BusyHUD()

    private var panel: NSPanel?
    private let view = BusyView(frame: .zero)
    private var spin: Timer?
    private var jobs: [UUID: (label: String, progress: Double)] = [:]
    private var order: [UUID] = []

    func begin(_ label: String) -> UUID {
        let id = UUID()
        jobs[id] = (label, 0)
        order.append(id)
        // Quick jobs never flash the spinner.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { [weak self] in
            guard let self, self.jobs[id] != nil else { return }
            self.present()
        }
        return id
    }

    func update(_ id: UUID, progress: Double) {
        guard jobs[id] != nil else { return }
        jobs[id]?.progress = progress
        refresh()
    }

    func end(_ id: UUID) {
        jobs[id] = nil
        order.removeAll { $0 == id }
        if jobs.isEmpty {
            spin?.invalidate()
            spin = nil
            panel?.orderOut(nil)
        } else {
            refresh()
        }
    }

    private func refresh() {
        guard let first = order.first, let job = jobs[first] else { return }
        let pct = job.progress > 0.01 ? "  \(Int(job.progress * 100))%" : ""
        let more = jobs.count > 1 ? "（還有 \(jobs.count - 1) 個）" : ""
        view.text = "下包中 · \(job.label)\(pct)\(more)"
        view.progress = job.progress
    }

    private func present() {
        guard let screen = NSScreen.main else { return }
        let size = CGSize(width: 520, height: 52)
        let frame = CGRect(x: screen.visibleFrame.maxX - size.width - 16, y: screen.visibleFrame.minY + 24, width: size.width, height: size.height)
        let panel = self.panel ?? Overlay.makePanel()
        panel.ignoresMouseEvents = true
        panel.contentView = view
        panel.setFrame(frame, display: false)
        self.panel = panel
        refresh()
        panel.orderFrontRegardless()
        if spin == nil {
            SFX.shared.play(.plant)
            let t = Timer(timeInterval: 1 / 60, repeats: true) { [weak self] _ in self?.view.angle += 0.14 }
            RunLoop.main.add(t, forMode: .common)
            spin = t
        }
    }
}

/// After 去隱私: the red reveal markers blink in the top-right corner and fade out.
final class WantedHUD {
    static let shared = WantedHUD()

    private var panel: NSPanel?
    private let view = WantedView(frame: .zero)
    private var timer: Timer?
    private var started = Date()

    func evade(_ wanted: Wanted, on screen: NSScreen?) {
        guard wanted.level > 0, let screen = screen ?? NSScreen.main else { return }
        let size = WantedView.size
        let topInset = max(0, screen.frame.maxY - screen.visibleFrame.maxY)
        let frame = CGRect(x: screen.frame.maxX - size.width - 28, y: screen.frame.maxY - topInset - 28 - StatsPanelView.height(rows: 4) - 8 - size.height,
                           width: size.width, height: size.height)
        let panel = self.panel ?? Overlay.makePanel()
        panel.ignoresMouseEvents = true
        panel.contentView = view
        panel.setFrame(frame, display: false)
        panel.alphaValue = 1
        self.panel = panel
        view.show(wanted)
        view.appear = 1
        view.evading = true
        view.caption = "已解除偵查"
        panel.orderFrontRegardless()

        started = Date()
        timer?.invalidate()
        let t = Timer(timeInterval: 1 / 60, repeats: true) { [weak self] timer in
            guard let self else { timer.invalidate(); return }
            let elapsed = CGFloat(Date().timeIntervalSince(self.started))
            self.view.phase = elapsed
            self.view.needsDisplay = true
            guard elapsed > 1.9 else { return }
            timer.invalidate()
            NSAnimationContext.runAnimationGroup({ ctx in
                ctx.duration = 0.45
                panel.animator().alphaValue = 0
            }, completionHandler: {
                panel.orderOut(nil)
            })
        }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }
}
