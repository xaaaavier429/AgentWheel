import AppKit

extension Array {
    subscript(safe index: Int) -> Element? {
        indices.contains(index) ? self[index] : nil
    }
}

/// The weapon wheel itself: eight ring slots, dividers, highlight and the hub read-out.
final class WheelView: NSView {
    var context: WheelContext?
    var page = 0
    var hover: Int?
    /// Animated highlight amount per slot (0...1).
    var glow = [CGFloat](repeating: 0, count: 8)
    var radius: CGFloat = 245
    /// Fades slot contents in after a page flip.
    var contentFade: CGFloat = 1
    /// Open animation, 0...1: the wheel grows into place.
    var pop: CGFloat = 1
    var accent: NSColor = Theme.accent

    override var isFlipped: Bool { false }
    override var isOpaque: Bool { false }

    var inner: CGFloat { radius * 0.56 }
    private var hub: CGPoint { CGPoint(x: bounds.midX, y: bounds.midY) }
    private var scale: CGFloat { radius / 245 }
    private let step = CGFloat.pi / 4

    /// The octagon's corners reach a little past `radius`, so the view is a bit larger than the ring.
    static func side(for radius: CGFloat) -> CGFloat { (radius / cos(.pi / 8) + 32) * 2 }

    override func draw(_ dirtyRect: NSRect) {
        guard let cg = NSGraphicsContext.current?.cgContext, let context, !context.pages.isEmpty else { return }
        let page = context.pages[min(max(self.page, 0), context.pages.count - 1)]
        let eased = 1 - pow(1 - min(max(pop, 0), 1), 3)
        let k = 0.9 + 0.1 * eased
        cg.saveGState()
        cg.translateBy(x: bounds.midX, y: bounds.midY)
        cg.scaleBy(x: k, y: k)
        cg.translateBy(x: -bounds.midX, y: -bounds.midY)
        drawRing(cg)
        drawSlots(cg, page)
        drawHub(context, page)
        cg.restoreGState()
    }

    private func drawRing(_ cg: CGContext) {
        let c = hub, R = radius, r = inner
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        let corner = 1 / cos(step / 2)

        // Hub: a dark navy octagon so the read-out stays legible on any wallpaper.
        cg.saveGState()
        cg.addPath(Geo.octagon(center: c, apothem: r))
        cg.setFillColor(Theme.navy.withAlphaComponent(0.62).cgColor)
        cg.fillPath()
        cg.restoreGState()

        // Octagonal ring body with a soft shadow so it floats above the blurred desktop.
        let ring = CGMutablePath()
        ring.addPath(Geo.octagon(center: c, apothem: R))
        ring.addPath(Geo.octagon(center: c, apothem: r))
        cg.saveGState()
        cg.setShadow(offset: CGSize(width: 0, height: -4 * scale), blur: 28 * scale, color: NSColor(white: 0, alpha: 0.55).cgColor)
        cg.addPath(ring)
        cg.setFillColor(Theme.navy.withAlphaComponent(0.78).cgColor)
        cg.fillPath(using: .evenOdd)
        cg.restoreGState()

        // Each slot gets a faint lift towards the rim; the highlighted one fills with the agent colour.
        let lift = CGGradient(colorsSpace: space, colors: [NSColor(white: 1, alpha: 0.02).cgColor, NSColor(white: 1, alpha: 0.08).cgColor] as CFArray, locations: [0, 1])!
        for i in 0..<8 {
            let t = CGFloat(i) * step
            let from = Geo.point(c, r, t), to = Geo.point(c, R, t)
            cg.saveGState()
            cg.addPath(Geo.facet(center: c, r0: r, r1: R, from: t - step / 2, to: t + step / 2))
            cg.clip()
            cg.drawLinearGradient(lift, start: from, end: to, options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
            let h = glow[i]
            if h > 0.002 {
                let lit = CGGradient(colorsSpace: space, colors: [
                    accent.withAlphaComponent(0.16 * h).cgColor,
                    accent.withAlphaComponent(0.5 * h).cgColor,
                    accent.withAlphaComponent(0.88 * h).cgColor,
                ] as CFArray, locations: [0, 0.6, 1])!
                cg.drawLinearGradient(lit, start: from, end: to, options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
            }
            cg.restoreGState()
        }

        // Solid bar along the outer edge of the selected slot, and a thin one along its inner edge.
        let rim = max(4, 7 * scale)
        for i in 0..<8 where glow[i] > 0.002 {
            let t = CGFloat(i) * step
            cg.saveGState()
            cg.setFillColor(accent.withAlphaComponent(glow[i]).cgColor)
            cg.addPath(Geo.facet(center: c, r0: R - rim, r1: R, from: t - step / 2, to: t + step / 2))
            cg.fillPath()
            cg.setFillColor(accent.withAlphaComponent(0.8 * glow[i]).cgColor)
            cg.addPath(Geo.facet(center: c, r0: r, r1: r + max(1.5, 2.5 * scale), from: t - step / 2, to: t + step / 2))
            cg.fillPath()
            cg.restoreGState()
        }

        // Thin bone-white dividers and edges.
        cg.saveGState()
        cg.setStrokeColor(Theme.bone.withAlphaComponent(0.32).cgColor)
        cg.setLineWidth(max(1, 1.2 * scale))
        for i in 0..<8 {
            let t = CGFloat(i) * step + step / 2
            cg.move(to: Geo.point(c, r * corner, t))
            cg.addLine(to: Geo.point(c, R * corner, t))
        }
        cg.strokePath()
        cg.setStrokeColor(Theme.bone.withAlphaComponent(0.55).cgColor)
        cg.setLineWidth(max(1, 1.3 * scale))
        cg.addPath(Geo.octagon(center: c, apothem: R - 0.5))
        cg.strokePath()
        cg.setStrokeColor(Theme.bone.withAlphaComponent(0.3).cgColor)
        cg.setLineWidth(1)
        cg.addPath(Geo.octagon(center: c, apothem: r))
        cg.strokePath()
        // Small tactical ticks pointing out from each outer corner.
        cg.setStrokeColor(Theme.bone.withAlphaComponent(0.5).cgColor)
        cg.setLineWidth(max(1, 1.5 * scale))
        for i in 0..<8 {
            let t = CGFloat(i) * step + step / 2
            cg.move(to: Geo.point(c, R * corner + 6 * scale, t))
            cg.addLine(to: Geo.point(c, R * corner + 13 * scale, t))
        }
        cg.strokePath()
        cg.restoreGState()
    }

    private func drawSlots(_ cg: CGContext, _ page: WheelPage) {
        let c = hub, s = scale
        let mid = (inner + radius) / 2
        for i in 0..<8 {
            guard let option = page.slots[safe: i] ?? nil else { continue }
            let p = Geo.point(c, mid, CGFloat(i) * step)
            cg.saveGState()
            // The source's own format stays readable ("目前"); missing tools fade further.
            let isCurrent = option.ammo == "目前"
            cg.setAlpha((option.enabled ? 1 : (isCurrent ? 0.72 : 0.38)) * contentFade)
            // Icon that shows what the format is, its name, and the one-word trait under it.
            Glyph.of(option.id).draw(in: CGRect(x: p.x - 29 * s, y: p.y - 2 * s, width: 58 * s, height: 58 * s))
            let name = option.big ?? option.title
            Draw.text(name, TextStyle(font: Fonts.badge((name.count > 4 ? 19 : 23) * s), outline: 2 * s, shadow: 4, kern: 0.4),
                      center: CGPoint(x: p.x, y: p.y - 16 * s))
            if option.big != nil || !option.enabled {
                Draw.text(option.ammo, TextStyle(font: Fonts.hud(13 * s, .bold), outline: 1.4 * s, outlineColor: NSColor(white: 0, alpha: 0.7), shadow: 2),
                          center: CGPoint(x: p.x, y: p.y - 35 * s))
            }
            cg.restoreGState()
        }
    }

    private func drawHub(_ context: WheelContext, _ page: WheelPage) {
        let c = hub, r = inner, s = scale
        let option = hover.flatMap { page.slots[safe: $0] ?? nil }

        // Which wheel this is: file kind and page.
        let kicker = "\(context.kind.title) · \(page.name)"
        Draw.text(kicker, TextStyle(font: Fonts.hud(15 * s, .bold), color: Theme.bone.withAlphaComponent(0.9), shadow: 3, kern: 1.2),
                  center: CGPoint(x: c.x, y: c.y + r * 0.5), maxWidth: r * 1.5)
        accent.setFill()
        CGRect(x: c.x - 14 * s, y: c.y + r * 0.5 - 15 * s, width: 28 * s, height: 2 * s).fill()

        let title = option?.title ?? context.headline
        let titleSize: CGFloat = option == nil ? 23 : 30
        Draw.text(title, TextStyle(font: Fonts.hud(titleSize * s, .bold), outline: 1.4 * s, outlineColor: NSColor(white: 0, alpha: 0.5), shadow: 6),
                  center: CGPoint(x: c.x, y: c.y + r * 0.21), maxWidth: r * 1.62)

        var detail = context.summary
        var color = NSColor(white: 1, alpha: 0.86)
        if let option {
            if option.enabled {
                detail = option.detail
            } else {
                detail = option.note ?? "現在用不了"
                color = Theme.loss.mixed(with: .white, 0.45)
            }
        }
        Draw.text(detail, TextStyle(font: Fonts.hud(14.5 * s, .medium), color: color, shadow: 3, wraps: true),
                  center: CGPoint(x: c.x, y: c.y - r * 0.13), maxWidth: r * 1.42)

        if context.pages.count > 1 {
            drawPager(count: context.pages.count, at: CGPoint(x: c.x, y: c.y - r * 0.5))
        }
    }

    private func drawPager(count: Int, at p: CGPoint) {
        let s = scale
        let label = "\(page + 1) / \(count)"
        let style = TextStyle(font: Fonts.hud(18 * s, .demi), shadow: 3)
        let width = Draw.size(label, style).width
        Draw.text(label, style, center: p)
        let gap = width / 2 + 15 * s
        if let left = Symbols.outlined("chevron.left", pointSize: 15 * s, weight: .black, color: accent, rim: 0),
           let right = Symbols.outlined("chevron.right", pointSize: 15 * s, weight: .black, color: accent, rim: 0) {
            Symbols.draw(left, center: CGPoint(x: p.x - gap, y: p.y))
            Symbols.draw(right, center: CGPoint(x: p.x + gap, y: p.y))
        }
    }
}

/// Top-right stat panel. Bars compare the dragged file with the highlighted result:
/// white is what stays, blue is an upgrade, red is what you give up.
final class StatsPanelView: NSView {
    var title = ""
    var caption = ""
    var labels: [String] = []
    var base: [Double] = []
    var target: [Double] = []
    var shown: [Double] = []
    var accent = Theme.accent

    override var isFlipped: Bool { true }
    override var isOpaque: Bool { false }

    static let width: CGFloat = 316
    static func height(rows: Int) -> CGFloat { 72 + CGFloat(rows) * 27 + 12 }

    override func draw(_ dirtyRect: NSRect) {
        let b = bounds
        Panel.fill(b, accent: accent, flipped: true)

        Draw.draw(title.uppercased(), TextStyle(font: Fonts.hud(20, .bold), color: Theme.bone, shadow: 0, kern: 0.6, alignment: .left),
                  in: CGRect(x: 20, y: 12, width: b.width - 40, height: 28))
        Draw.draw(caption, TextStyle(font: Fonts.hud(13, .medium), color: Theme.bone.withAlphaComponent(0.6), shadow: 0, alignment: .left),
                  in: CGRect(x: 20, y: 40, width: b.width - 40, height: 20))
        Theme.bone.withAlphaComponent(0.14).setFill()
        NSRect(x: 20, y: 62, width: b.width - 36, height: 1).fill()

        for (i, label) in labels.enumerated() {
            let y = 72 + CGFloat(i) * 27
            Draw.draw(label, TextStyle(font: Fonts.hud(14, .demi), color: Theme.bone.withAlphaComponent(0.92), shadow: 0, alignment: .left),
                      in: CGRect(x: 20, y: y, width: 84, height: 20))
            let bar = CGRect(x: 104, y: y + 6, width: b.width - 120, height: 8)
            let start = base[safe: i] ?? 0
            drawBar(bar, base: start, value: shown[safe: i] ?? start)
        }
    }

    private func drawBar(_ r: CGRect, base: Double, value: Double) {
        Theme.bone.withAlphaComponent(0.15).setFill()
        r.fill()
        let lo = CGFloat(min(base, value)), hi = CGFloat(max(base, value))
        Theme.bone.setFill()
        CGRect(x: r.minX, y: r.minY, width: r.width * lo, height: r.height).fill()
        if hi - lo > 0.004 {
            (value > base ? Theme.gain : Theme.loss).setFill()
            CGRect(x: r.minX + r.width * lo, y: r.minY, width: r.width * (hi - lo), height: r.height).fill()
        }
        Theme.navy.setFill()
        for k in 1..<5 {
            CGRect(x: r.minX + r.width * CGFloat(k) / 5 - 1, y: r.minY, width: 2, height: r.height).fill()
        }
    }
}

/// Top-left help box, in the style of GTA's "Press ~INPUT~ to …" prompts.
final class HelpPanelView: NSView {
    enum Piece {
        case text(String)
        case key(String)
    }

    var lines: [[Piece]] = [
        [.text("按"), .key("⇧ Shift"), .text("切換  武器 / 技能")],
        [.text("朝格子方向拖，"), .key("鬆開滑鼠"), .text("執行")],
        [.text("移回中心再鬆開 = 取消")],
    ]

    override var isFlipped: Bool { true }
    override var isOpaque: Bool { false }

    static let size = CGSize(width: 290, height: 104)

    var accent = Theme.accent

    override func draw(_ dirtyRect: NSRect) {
        Panel.fill(bounds, accent: accent, flipped: true)
        let textStyle = TextStyle(font: Fonts.hud(15, .demi), color: Theme.bone, shadow: 0, alignment: .left)
        for (i, line) in lines.enumerated() {
            var x: CGFloat = 18
            let y = 16 + CGFloat(i) * 28
            for piece in line {
                switch piece {
                case .text(let s):
                    let w = Draw.size(s, textStyle).width
                    Draw.draw(s, textStyle, in: CGRect(x: x, y: y + 1, width: w + 2, height: 22))
                    x += w + 4
                case .key(let s):
                    x += drawKey(s, at: CGPoint(x: x, y: y + 1)) + 4
                }
            }
        }
    }

    private func drawKey(_ key: String, at p: CGPoint) -> CGFloat {
        let style = TextStyle(font: Fonts.hud(13, .bold), color: Theme.navy, shadow: 0)
        let w = max(22, Draw.size(key, style).width + 12)
        let r = CGRect(x: p.x, y: p.y, width: w, height: 21)
        Theme.bone.setFill()
        NSBezierPath(roundedRect: r, xRadius: 2, yRadius: 2).fill()
        Draw.text(key, style, center: CGPoint(x: r.midX, y: r.midY))
        return w
    }
}

/// Darkens the desktop with a vignette centred on the wheel, like GTA's slow-mo screen.
final class DimView: NSView {
    var focus: CGPoint = .zero

    override var isOpaque: Bool { false }

    override func draw(_ dirtyRect: NSRect) {
        guard let cg = NSGraphicsContext.current?.cgContext else { return }
        cg.setFillColor(Theme.navy.withAlphaComponent(0.3).cgColor)
        cg.fill(bounds)
        let reach = hypot(bounds.width, bounds.height) * 0.7
        let g = CGGradient(colorsSpace: nil, colors: [NSColor(white: 0, alpha: 0).cgColor, NSColor(white: 0, alpha: 0.5).cgColor] as CFArray, locations: [0.15, 1])!
        cg.drawRadialGradient(g, startCenter: focus, startRadius: 0, endCenter: focus, endRadius: reach, options: [.drawsAfterEndLocation])
    }
}

/// Everything drawn on top of the (blurred) desktop. Shared by the live overlay and `--snapshot`.
final class OverlayContent: NSView {
    let dim = DimView()
    let wheel = WheelView()
    let stats = StatsPanelView()
    let help = HelpPanelView()
    let wanted = WantedView()

    override var isFlipped: Bool { false }

    override init(frame: NSRect) {
        super.init(frame: frame)
        for v in [dim, wheel, stats, help, wanted] as [NSView] { addSubview(v) }
    }

    required init?(coder: NSCoder) { fatalError() }

    func arrange(wheelCenter: CGPoint, radius: CGFloat, topInset: CGFloat, rows: Int) {
        dim.frame = bounds
        dim.focus = wheelCenter
        let side = WheelView.side(for: radius)
        wheel.radius = radius
        wheel.frame = CGRect(x: wheelCenter.x - side / 2, y: wheelCenter.y - side / 2, width: side, height: side)

        let margin: CGFloat = 28
        let statsHeight = StatsPanelView.height(rows: rows)
        let starsSize = WantedView.size
        // Top-right stack: the stat panel, with the wanted stars hanging underneath it.
        var statsFrame = CGRect(x: bounds.maxX - StatsPanelView.width - margin, y: bounds.maxY - topInset - margin - statsHeight,
                                width: StatsPanelView.width, height: statsHeight)
        var starsFrame = CGRect(x: bounds.maxX - starsSize.width - margin, y: statsFrame.minY - 8 - starsSize.height,
                                width: starsSize.width, height: starsSize.height)
        var helpFrame = CGRect(x: margin, y: bounds.maxY - topInset - margin - HelpPanelView.size.height,
                               width: HelpPanelView.size.width, height: HelpPanelView.size.height)
        let ringRect = CGRect(x: wheelCenter.x - radius, y: wheelCenter.y - radius, width: radius * 2, height: radius * 2)
        if statsFrame.intersects(ringRect) || starsFrame.intersects(ringRect) {
            starsFrame.origin.y = margin
            statsFrame.origin.y = starsFrame.maxY + 8
        }
        if helpFrame.intersects(ringRect) { helpFrame.origin.y = margin }
        stats.frame = statsFrame
        help.frame = helpFrame
        wanted.frame = starsFrame
    }

    func apply(context: WheelContext, page: Int, hover: Int?) {
        wheel.context = context
        wheel.page = page
        wheel.hover = hover
        let slots = context.pages[safe: page]?.slots ?? []
        let option = hover.flatMap { slots[safe: $0] ?? nil }
        stats.labels = context.statLabels
        stats.base = context.base
        if let option {
            stats.title = option.title
            stats.caption = option.enabled ? "\(context.headline)  →  \(option.title)" : (option.note ?? "")
            stats.target = option.enabled ? (option.stats ?? context.base) : context.base
        } else {
            stats.title = context.headline
            stats.caption = context.summary
            stats.target = context.base
        }
        if stats.shown.count != stats.target.count { stats.shown = context.base }
        stats.isHidden = context.statLabels.isEmpty
        wheel.needsDisplay = true
        stats.needsDisplay = true
    }

    func setAccent(_ color: NSColor) {
        wheel.accent = color
        stats.accent = color
        help.accent = color
    }
}

/// GTA wanted level, top right: five stars, earned ones lit, red/blue police flash behind them.
final class WantedView: NSView {
    static let size = CGSize(width: 380, height: 70)

    private(set) var level = 0
    private var reasons: [String] = []
    /// 0...1 while the stars light up one by one.
    var appear: CGFloat = 1
    /// Seconds since shown; drives the police flash and the blink.
    var phase: CGFloat = 0
    /// Losing the cops: earned stars blink between white and grey.
    var evading = false
    var caption: String?

    override var isOpaque: Bool { false }

    func show(_ wanted: Wanted) {
        level = wanted.level
        reasons = wanted.reasons
        appear = 0
        phase = 0
        evading = false
        caption = nil
        isHidden = level == 0
        needsDisplay = true
    }

    func reset() {
        show(Wanted())
    }

    override func draw(_ dirtyRect: NSRect) {
        guard level > 0, let cg = NSGraphicsContext.current?.cgContext else { return }
        let b = bounds
        let radius: CGFloat = 15, gap: CGFloat = 5
        let rowWidth = radius * 2 * 5 + gap * 4
        let rowMid = CGPoint(x: b.maxX - rowWidth / 2 - 4, y: b.maxY - radius - 6)

        // A red radar pulse behind the markers while you are revealed.
        if !evading {
            let pulse = 0.32 + 0.22 * sin(phase * 5)
            let light = Theme.red.withAlphaComponent(pulse)
            let glow = CGGradient(colorsSpace: nil, colors: [light.cgColor, light.withAlphaComponent(0).cgColor] as CFArray, locations: [0, 1])!
            cg.saveGState()
            cg.translateBy(x: rowMid.x, y: rowMid.y)
            cg.scaleBy(x: 2.6, y: 1)
            cg.drawRadialGradient(glow, startCenter: .zero, startRadius: 0, endCenter: .zero, endRadius: rowWidth * 0.36, options: [])
            cg.restoreGState()
        }

        for i in 0..<5 {
            // Markers fill right-to-left: the leftmost slot is the fifth one.
            let rank = 4 - i
            let center = CGPoint(x: rowMid.x - rowWidth / 2 + radius + CGFloat(i) * (radius * 2 + gap), y: rowMid.y)
            let earned = rank < level
            let start = CGFloat(rank) * 0.16
            let local = earned ? min(max((appear - start) / 0.3, 0), 1) : 1
            let pop = earned ? 1 + 0.45 * sin(local * .pi) * (1 - local) : 1
            let path = Self.diamond(center: center, half: radius * pop)

            cg.saveGState()
            if earned && local > 0 {
                let lit = !evading || Int(phase * 7) % 2 == 0
                cg.setShadow(offset: CGSize(width: 0, height: -1), blur: 6, color: Theme.red.withAlphaComponent(lit ? 0.8 : 0).cgColor)
                cg.addPath(path)
                cg.setFillColor((lit ? Theme.red : NSColor(white: 0.55, alpha: 0.9)).cgColor)
                cg.fillPath()
                cg.setShadow(offset: .zero, blur: 0, color: nil)
                cg.addPath(path)
                cg.setStrokeColor(Theme.navy.withAlphaComponent(0.9).cgColor)
                cg.setLineWidth(1.6)
                cg.setLineJoin(.miter)
                cg.strokePath()
            } else {
                cg.addPath(path)
                cg.setFillColor(Theme.navy.withAlphaComponent(0.5).cgColor)
                cg.fillPath()
                cg.addPath(path)
                cg.setStrokeColor(Theme.bone.withAlphaComponent(0.3).cgColor)
                cg.setLineWidth(1)
                cg.strokePath()
            }
            cg.restoreGState()
        }

        let text = caption ?? reasons.prefix(3).joined(separator: " · ")
        guard !text.isEmpty else { return }
        let style = TextStyle(font: Fonts.hud(13, .demi), color: NSColor(white: 1, alpha: 0.92), shadow: 0, alignment: .right)
        let width = min(Draw.size(text, style).width + 18, b.width)
        let pill = CGRect(x: b.maxX - width, y: 2, width: width, height: 20)
        Theme.navy.withAlphaComponent(0.85).setFill()
        pill.fill()
        Theme.red.setFill()
        CGRect(x: pill.minX, y: pill.minY, width: 3, height: pill.height).fill()
        Draw.draw(text, style, in: pill.insetBy(dx: 9, dy: 1.5))
    }

    /// Tall diamond, like the red "revealed" marker over an enemy.
    static func diamond(center: CGPoint, half: CGFloat) -> CGPath {
        let path = CGMutablePath()
        path.move(to: CGPoint(x: center.x, y: center.y + half))
        path.addLine(to: CGPoint(x: center.x + half * 0.7, y: center.y))
        path.addLine(to: CGPoint(x: center.x, y: center.y - half))
        path.addLine(to: CGPoint(x: center.x - half * 0.7, y: center.y))
        path.closeSubpath()
        return path
    }

    static func star(center: CGPoint, outer: CGFloat, inner: CGFloat) -> CGPath {
        let path = CGMutablePath()
        for k in 0..<10 {
            let r = k % 2 == 0 ? outer : inner
            let a = CGFloat(k) * .pi / 5
            let p = CGPoint(x: center.x + sin(a) * r, y: center.y + cos(a) * r)
            if k == 0 { path.move(to: p) } else { path.addLine(to: p) }
        }
        path.closeSubpath()
        return path
    }
}

/// Valorant-style HUD panel: deep navy body, top-right corner cut off, agent-colour bar on the left.
enum Panel {
    static func fill(_ b: CGRect, accent: NSColor, flipped: Bool, alpha: CGFloat = 0.86) {
        let cut: CGFloat = 14
        let top = flipped ? b.minY : b.maxY, bottom = flipped ? b.maxY : b.minY
        let dy: CGFloat = flipped ? cut : -cut
        let path = NSBezierPath()
        path.move(to: CGPoint(x: b.minX, y: top))
        path.line(to: CGPoint(x: b.maxX - cut, y: top))
        path.line(to: CGPoint(x: b.maxX, y: top + dy))
        path.line(to: CGPoint(x: b.maxX, y: bottom))
        path.line(to: CGPoint(x: b.minX, y: bottom))
        path.close()
        Theme.navy.withAlphaComponent(alpha).setFill()
        path.fill()
        Theme.bone.withAlphaComponent(0.14).setStroke()
        path.lineWidth = 1
        path.stroke()
        accent.setFill()
        CGRect(x: b.minX, y: b.minY, width: 4, height: b.height).fill()
    }
}
