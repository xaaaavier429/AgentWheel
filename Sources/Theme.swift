import AppKit

/// Highlight colour, named after the Valorant agent whose signature colour it borrows.
/// (The type keeps its old name so saved settings and the `--crew` flag still work.)
enum Crew: String, CaseIterable {
    case valorant, jett, phoenix, sage, reyna, viper

    var title: String {
        switch self {
        case .valorant: return "特戰紅（預設）"
        case .jett: return "Jett · 冰藍"
        case .phoenix: return "Phoenix · 火橘"
        case .sage: return "Sage · 青綠"
        case .reyna: return "Reyna · 紫"
        case .viper: return "Viper · 毒綠"
        }
    }

    var color: NSColor {
        switch self {
        case .valorant: return Theme.red
        case .jett: return NSColor(srgbRed: 0.55, green: 0.85, blue: 0.98, alpha: 1)
        case .phoenix: return NSColor(srgbRed: 1.00, green: 0.55, blue: 0.20, alpha: 1)
        case .sage: return NSColor(srgbRed: 0.33, green: 0.86, blue: 0.76, alpha: 1)
        case .reyna: return NSColor(srgbRed: 0.72, green: 0.36, blue: 0.96, alpha: 1)
        case .viper: return NSColor(srgbRed: 0.40, green: 0.88, blue: 0.30, alpha: 1)
        }
    }
}

enum WheelSize: String, CaseIterable {
    case small, medium, large

    var title: String {
        switch self {
        case .small: return "小"
        case .medium: return "中"
        case .large: return "大"
        }
    }

    var radius: CGFloat {
        switch self {
        case .small: return 235
        case .medium: return 285
        case .large: return 330
        }
    }
}

final class Settings {
    static let shared = Settings()
    private let defaults = UserDefaults.standard

    var crew: Crew {
        get { Crew(rawValue: defaults.string(forKey: "crew") ?? "") ?? .valorant }
        set { defaults.set(newValue.rawValue, forKey: "crew") }
    }

    var wheelSize: WheelSize {
        get { WheelSize(rawValue: defaults.string(forKey: "wheelSize") ?? "") ?? .medium }
        set { defaults.set(newValue.rawValue, forKey: "wheelSize") }
    }

    var sound: Bool {
        get { flag("sound", default: true) }
        set { defaults.set(newValue, forKey: "sound") }
    }

    var blur: Bool {
        get { flag("blur", default: true) }
        set { defaults.set(newValue, forKey: "blur") }
    }

    var banner: Bool {
        get { flag("banner", default: true) }
        set { defaults.set(newValue, forKey: "banner") }
    }

    var welcomed: Bool {
        get { flag("welcomed", default: false) }
        set { defaults.set(newValue, forKey: "welcomed") }
    }

    private func flag(_ key: String, default value: Bool) -> Bool {
        defaults.object(forKey: key) == nil ? value : defaults.bool(forKey: key)
    }
}

enum Theme {
    static var accent: NSColor { Settings.shared.crew.color }
    /// Valorant red (#FF4655).
    static let red = NSColor(srgbRed: 1.00, green: 0.275, blue: 0.333, alpha: 1)
    /// Deep navy used for panels (#0F1923).
    static let navy = NSColor(srgbRed: 0.059, green: 0.098, blue: 0.137, alpha: 1)
    /// Off-white "bone" used for text and lines (#ECE8E1).
    static let bone = NSColor(srgbRed: 0.925, green: 0.910, blue: 0.882, alpha: 1)
    /// Round-won teal for VICTORY.
    static let victory = NSColor(srgbRed: 0.33, green: 0.93, blue: 0.78, alpha: 1)
    /// ACE / THRIFTY gold.
    static let gold = NSColor(srgbRed: 0.98, green: 0.80, blue: 0.36, alpha: 1)
    /// DEFEAT red.
    static let wasted = NSColor(srgbRed: 1.00, green: 0.275, blue: 0.333, alpha: 1)
    /// Stat bar segment for an upgrade (ally teal).
    static let gain = NSColor(srgbRed: 0.33, green: 0.86, blue: 0.76, alpha: 1)
    /// Stat bar segment for a downgrade (enemy red).
    static let loss = NSColor(srgbRed: 1.00, green: 0.275, blue: 0.333, alpha: 1)
}

enum HUDWeight: Int {
    case medium = 0, demi, bold, heavy
}

enum Fonts {
    private static let latin = ["AvenirNextCondensed-Medium", "AvenirNextCondensed-DemiBold", "AvenirNextCondensed-Bold", "AvenirNextCondensed-Heavy"]
    private static let cjk = ["PingFangTC-Regular", "PingFangTC-Medium", "PingFangTC-Semibold", "PingFangTC-Semibold"]

    /// Condensed HUD face (close to GTA's Chalet Comprimé) with PingFang for Chinese.
    static func hud(_ size: CGFloat, _ weight: HUDWeight = .demi) -> NSFont {
        let fallback = NSFontDescriptor(name: cjk[weight.rawValue], size: size)
        let descriptor = NSFontDescriptor(name: latin[weight.rawValue], size: size)
            .addingAttributes([.cascadeList: [fallback]])
        return NSFont(descriptor: descriptor, size: size) ?? .systemFont(ofSize: size, weight: .semibold)
    }

    /// Chunky face used for the big format labels inside the slots.
    static func badge(_ size: CGFloat) -> NSFont {
        let fallback = NSFontDescriptor(name: "PingFangTC-Semibold", size: size)
        let descriptor = NSFontDescriptor(name: "HelveticaNeue-CondensedBlack", size: size)
            .addingAttributes([.cascadeList: [fallback]])
        return NSFont(descriptor: descriptor, size: size) ?? hud(size, .heavy)
    }

    /// Tall condensed face for the VICTORY / ACE / DEFEAT splash.
    static func splash(_ size: CGFloat) -> NSFont {
        NSFont(name: "DINCondensed-Bold", size: size) ?? NSFont(name: "HelveticaNeue-CondensedBlack", size: size) ?? hud(size, .heavy)
    }
}
