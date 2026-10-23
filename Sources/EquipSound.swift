import Foundation

/// The handling sound played when a slot's action succeeds (slide rack, bolt, pump, pin pull…),
/// picked per action so each format has its own feel.
enum EquipSound {
    static func of(_ id: String) -> SFX.Kind {
        if let hit = table[id] { return hit }
        if id.hasSuffix(".strip") { return .rackPistol }
        if id.hasSuffix(".compress") { return .pinPull }
        return .rackPistol
    }

    private static let table: [String: SFX.Kind] = [
        "img.to.png": .rackRifle, "img.to.jpg": .rackPistol, "img.to.heic": .rackPistol, "img.to.webp": .rackPistol,
        "img.to.pdf": .rackHeavy, "img.to.avif": .rackBolt, "img.to.gif": .rackPump, "img.to.tiff": .rackHeavy,
        "img.half": .meleeSwing, "img.cutout": .bladeDraw, "img.ocr": .meleeSwing, "img.mono": .meleeSwing,
        "img.rotate": .meleeSwing, "img.backdrop": .meleeSwing, "pdf.to.png": .rackRifle, "pdf.to.jpg": .rackPistol,
        "pdf.to.long": .rackBolt, "pdf.to.docx": .rackPump, "pdf.to.txt": .meleeSwing, "pdf.to.md": .rackPistol,
        "pdf.to.rtf": .rackPistol, "pdf.to.html": .rackHeavy, "pdf.split": .bladeDraw, "pdf.merge": .pinPull,
        "pdf.rotate": .meleeSwing, "pdf.mono": .meleeSwing, "pdf.ocr": .meleeSwing, "vid.to.mp4": .rackRifle,
        "vid.to.mov": .rackPistol, "vid.to.hevc": .rackBolt, "vid.to.webm": .rackPistol, "vid.to.gif": .rackPump,
        "vid.to.mp3": .rackPistol, "vid.to.m4a": .pinPull, "vid.to.wav": .rackHeavy, "vid.mute": .rackPistol,
        "vid.720p": .meleeSwing, "vid.cover": .meleeSwing, "vid.fast": .rackPistol, "vid.slow": .rackBolt,
        "vid.strip": .pinPull, "aud.to.mp3": .rackPistol, "aud.to.m4a": .rackPistol, "aud.to.wav": .rackHeavy,
        "aud.to.flac": .rackRifle, "aud.to.aiff": .rackPump, "aud.to.opus": .rackBolt, "aud.to.alac": .rackPistol,
        "aud.to.m4r": .pinPull, "aud.mono": .bladeDraw, "aud.loud": .meleeSwing, "aud.fast": .rackPistol,
        "doc.to.pdf": .rackHeavy, "doc.to.docx": .rackRifle, "doc.to.txt": .meleeSwing, "doc.to.md": .rackPistol,
        "doc.to.html": .pinPull, "doc.to.rtf": .rackPistol, "doc.to.odt": .rackPump, "doc.to.doc": .rackPistol,
        "doc.count": .meleeSwing, "doc.copy": .bladeDraw, "gen.zip": .rackHeavy, "gen.path": .meleeSwing,
        "gen.name": .bladeDraw, "gen.sha": .meleeSwing,
    ]
}
