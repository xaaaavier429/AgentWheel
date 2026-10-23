import AppKit

/// Synthesized sounds (no audio files shipped): buy-menu ticks, a weapon-handling sound per weapon class,
/// kill-confirm dings for a finished job (five for ACE), radar pings for a reveal and a spike-style
/// detonation for a failure. Any of them can be replaced by a file in `customFolder`.
final class SFX {
    static let shared = SFX()

    enum Kind: CaseIterable {
        case open, tick, page, passed, ace, wasted, denied, wanted, evaded, plant
        case rackPistol, rackRifle, rackBolt, rackPump, rackHeavy, pinPull, bladeDraw, meleeSwing
    }

    /// Drop `<kind>.wav` (or .mp3 / .m4a / .aiff / .caf) here to replace a synthesized sound,
    /// e.g. `passed.wav`, `ace.wav`, `wasted.wav`, `open.wav`.
    static let customFolder = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/AgentWheel/Sounds")

    private let rate = 44_100.0
    private var synthesized: [Kind: Data] = [:]
    private var clips: [Kind: Data] = [:]
    private var pool: [Kind: [NSSound]] = [:]
    private var seed: UInt32 = 0x9E37_79B9

    private init() {
        for kind in Kind.allCases { synthesized[kind] = wav(render(kind)) }
        reload()
    }

    /// Re-reads the custom sound folder; anything not found there falls back to the synthesized sound.
    func reload() {
        for kind in Kind.allCases {
            var data = synthesized[kind]
            for ext in ["wav", "mp3", "m4a", "aiff", "caf"] {
                let url = SFX.customFolder.appendingPathComponent("\(kind).\(ext)")
                if let custom = try? Data(contentsOf: url) {
                    data = custom
                    break
                }
            }
            if clips[kind] != data {
                clips[kind] = data
                pool[kind] = nil
            }
        }
    }

    func play(_ kind: Kind) {
        guard Settings.shared.sound, let clip = clips[kind] else { return }
        var sounds = pool[kind] ?? []
        if let idle = sounds.first(where: { !$0.isPlaying }) {
            idle.currentTime = 0
            idle.play()
            return
        }
        guard sounds.count < 4, let sound = NSSound(data: clip) else { return }
        sound.volume = volume(kind)
        sounds.append(sound)
        pool[kind] = sounds
        sound.play()
    }

    func play(_ kind: Kind, after delay: Double) {
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) { self.play(kind) }
    }

    private func volume(_ kind: Kind) -> Float {
        switch kind {
        case .tick: return 0.35
        case .page: return 0.4
        case .open: return 0.5
        case .passed: return 0.5
        case .ace: return 0.5
        case .plant: return 0.3
        case .wasted: return 0.75
        case .denied: return 0.35
        case .wanted: return 0.32
        case .evaded: return 0.4
        case .rackPistol, .rackRifle, .rackBolt, .rackPump, .rackHeavy, .pinPull, .bladeDraw, .meleeSwing: return 0.65
        }
    }

    // MARK: - Synthesis kit

    private func noise() -> Double {
        seed = seed &* 1_664_525 &+ 1_013_904_223
        return Double(seed) / Double(UInt32.max) * 2 - 1
    }

    /// State-variable band-pass: shapes noise into metal friction, swooshes and spray.
    private struct BandPass {
        var low = 0.0, band = 0.0
        mutating func run(_ x: Double, cutoff: Double, damping: Double, rate: Double) -> Double {
            let f = 2 * sin(.pi * min(cutoff, rate / 6.5) / rate)
            low += f * band
            let high = x - low - damping * band
            band += f * high
            return band
        }
    }

    private func samples(_ seconds: Double, _ f: (Double) -> Double) -> [Double] {
        (0..<max(1, Int(seconds * rate))).map { f(Double($0) / rate) }
    }

    private func clip(_ seconds: Double, _ f: (Double) -> Double) -> [Float] {
        samples(seconds, f).map(Float.init)
    }

    /// Lays parts down at their start times.
    private func mix(_ seconds: Double, _ parts: [(at: Double, part: [Double])]) -> [Float] {
        var out = [Double](repeating: 0, count: Int(seconds * rate))
        for (at, part) in parts {
            let offset = Int(at * rate)
            for (i, v) in part.enumerated() where offset + i < out.count { out[offset + i] += v }
        }
        return out.map { Float(tanh($0 * 1.1) / 1.1) }
    }

    private func click(_ t: Double, pitch: Double) -> Double {
        guard t >= 0 else { return 0 }
        let body = exp(-t * 170) * (0.6 * sin(2 * .pi * pitch * t) + 0.22 * sin(2 * .pi * pitch * 2.01 * t))
        return body + noise() * exp(-t * 1100) * 0.3
    }

    /// A metal-on-metal clack: band-passed transient plus inharmonic ringing.
    private func clack(_ tone: Double, _ gain: Double) -> [Double] {
        var bp = BandPass()
        return samples(0.16) { (u: Double) -> Double in
            let w: Double = 2 * .pi * tone * u
            let hit: Double = bp.run(self.noise(), cutoff: 3300, damping: 0.6, rate: self.rate) * exp(-u * 380) * 1.3
            let p1: Double = sin(w)
            let p2: Double = 0.5 * exp(-u * 30) * sin(w * 2.76)
            let p3: Double = 0.25 * exp(-u * 60) * sin(w * 5.4)
            let ring: Double = (p1 + p2 + p3) * exp(-u * 55) * 0.32
            let tick: Double = self.noise() * exp(-u * 2500) * 0.45
            return (hit + ring + tick) * gain
        }
    }

    /// Low body knock with a quick pitch drop.
    private func thump(_ freq: Double, decay: Double, _ gain: Double) -> [Double] {
        var phase = 0.0
        return samples(min(2.6, 6 / decay)) { u in
            phase += 2 * .pi * freq * (1 + 0.8 * exp(-u * 50)) / self.rate
            return sin(phase) * exp(-u * decay) * gain
        }
    }

    /// Sliding friction (a slide or bolt travelling), noise swept between two pitches.
    private func scrape(_ duration: Double, from f0: Double, to f1: Double, _ gain: Double, damping: Double = 0.9) -> [Double] {
        var bp = BandPass()
        return samples(duration) { u in
            let k = u / duration
            let env = pow(sin(.pi * k), 0.7)
            return bp.run(self.noise(), cutoff: f0 + (f1 - f0) * k, damping: damping, rate: self.rate) * env * gain
        }
    }

    /// A bell-like metal ring (blade, pin, spoon).
    private func ring(_ freq: Double, decay: Double, _ gain: Double) -> [Double] {
        let partials: [(Double, Double, Double)] = [(1, 1, 1), (2.41, 0.5, 1.4), (3.93, 0.3, 2), (5.6, 0.18, 3)]
        return samples(min(2, 7 / decay)) { u in
            partials.reduce(0.0) { sum, p in sum + sin(2 * .pi * freq * p.0 * u) * p.1 * exp(-u * decay * p.2) } * min(1, u / 0.002) * gain
        }
    }

    private func whoosh(_ duration: Double, from f0: Double, to f1: Double, _ gain: Double) -> [Double] {
        var bp = BandPass()
        return samples(duration) { u in
            let k = u / duration
            return bp.run(self.noise(), cutoff: f0 + (f1 - f0) * k, damping: 1.2, rate: self.rate) * pow(sin(.pi * k), 1.5) * gain
        }
    }

    /// Soft saw-like synth voice: higher harmonics die away faster than the fundamental.
    private func voice(_ freq: Double, _ u: Double, bright: Double) -> Double {
        var sum: Double = 0
        for k in 1...6 {
            let h = Double(k)
            sum += sin(2 * .pi * freq * h * u) / h * exp(-u * bright * h)
        }
        return sum
    }

    /// A short clean digital blip, like a menu UI sound.
    private func blip(_ t: Double, _ freq: Double, length: Double) -> Double {
        guard t >= 0, t < length else { return 0 }
        let k: Double = 1 - t / length
        let env: Double = min(1, t / 0.002) * k * k
        let tone: Double = sin(2 * .pi * freq * t) + 0.25 * sin(2 * .pi * freq * 2 * t)
        return tone * env
    }

    /// A glassy, crisp "ding" with a tiny click on the front: the kill-confirm style hit.
    private func ding(_ freq: Double, _ gain: Double) -> [Double] {
        samples(0.6) { u in
            let attack: Double = min(1, u / 0.0015)
            let p1: Double = sin(2 * .pi * freq * u) * exp(-u * 7)
            let p2: Double = 0.45 * sin(2 * .pi * freq * 2.0 * u) * exp(-u * 12)
            let p3: Double = 0.25 * sin(2 * .pi * freq * 3.01 * u) * exp(-u * 20)
            let p4: Double = 0.12 * sin(2 * .pi * freq * 4.2 * u) * exp(-u * 30)
            let click: Double = self.noise() * exp(-u * 1800) * 0.4
            return ((p1 + p2 + p3 + p4) * attack + click) * gain
        }
    }

    /// A muted, filtered UI click.
    private func tk(_ gain: Double) -> [Double] {
        var bp = BandPass()
        return samples(0.03) { u in
            let air: Double = bp.run(self.noise(), cutoff: 4200, damping: 0.5, rate: self.rate) * exp(-u * 260) * 0.5
            let body: Double = sin(2 * .pi * 1250 * u) * exp(-u * 180) * 0.14
            return (air + body) * gain
        }
    }

    /// One synth note as a sample buffer.
    private func note(_ freq: Double, length: Double, attack: Double, decay: Double, bright: Double, _ gain: Double) -> [Double] {
        samples(length) { u in
            let env: Double = min(1, u / attack) * exp(-u * decay)
            return self.voice(freq, u, bright: bright) * env * gain
        }
    }

    // MARK: - Sounds

    func render(_ kind: Kind) -> [Float] {
        switch kind {
        case .tick:
            // Buy-menu hover: a soft, muted tick.
            return mix(0.035, [(0, tk(0.8))])

        case .page:
            return mix(0.12, [(0, tk(1.0)), (0.05, tk(0.8))])

        case .denied:
            // Not enough credits: two short muted bonks.
            return mix(0.25, [(0, thump(150, decay: 40, 0.5)), (0.1, thump(130, decay: 40, 0.45))])

        case .open:
            // Buy menu opening: a sub "fwump", a falling breath of air and a small glassy glint.
            var phase = 0.0
            let fwump = samples(0.35) { u in
                phase += 2 * .pi * (140 - 90 * min(u / 0.25, 1)) / self.rate
                return sin(phase) * min(1, u / 0.01) * exp(-u * 9) * 0.6
            }
            var bp = BandPass()
            let air = samples(0.25) { u in
                let k: Double = u / 0.25
                return bp.run(self.noise(), cutoff: 5000 - 3000 * k, damping: 0.8, rate: self.rate) * pow(1 - k, 2) * 0.25
            }
            let glint = samples(0.4) { u in
                let tone: Double = sin(2 * .pi * 2637 * u) + 0.6 * sin(2 * .pi * 3951 * u)
                return tone * min(1, u / 0.002) * exp(-u * 14) * 0.08
            }
            return mix(0.45, [(0, fwump), (0, air), (0.03, glint)])

        case .passed:
            // One kill-confirm ding over a punchy sub.
            return mix(0.8, [(0, ding(1760, 0.32)), (0.004, ding(2637, 0.1)), (0, thump(70, decay: 14, 0.55))])

        case .ace:
            // ACE: five dings stepping up in pitch, one per "kill", then a bright glint.
            var parts: [(at: Double, part: [Double])] = []
            let freqs: [Double] = [1568, 1760, 1976, 2349, 2637]
            for (i, f) in freqs.enumerated() {
                let at = Double(i) * 0.1
                parts.append((at, ding(f, 0.26)))
                parts.append((at, thump(70, decay: 16, 0.3)))
            }
            parts.append((0.45, ding(3520, 0.1)))
            return mix(1.3, parts)

        case .wanted:
            // Revealed: a quick upward radar chirp and a high ping with a faint echo.
            var phase = 0.0
            let chirp = samples(0.09) { u in
                phase += 2 * .pi * (700 + 1900 * u / 0.09) / self.rate
                return sin(phase) * sin(.pi * u / 0.09) * 0.22
            }
            let ping = samples(0.7) { u in
                sin(2 * .pi * 2400 * u) * min(1, u / 0.002) * exp(-u * 8) * 0.22
            }
            return mix(1.0, [(0, chirp), (0.09, ping), (0.3, ping.map { $0 * 0.3 })])

        case .evaded:
            // Reveal cleared, like a defuse: rapid dits, then a glassy two-note resolve.
            let dits = samples(0.3) { u in
                var sum: Double = 0
                for i in 0..<6 { sum += self.blip(u - Double(i) * 0.045, 2000, length: 0.03) }
                return sum * 0.18
            }
            return mix(1.1, [(0, dits), (0.3, ding(1319, 0.2)), (0.3, ding(1976, 0.14)), (0.3, thump(60, decay: 12, 0.4))])

        case .plant:
            // Work started: a rising charge-up whine and one confirming beep.
            var phase = 0.0
            let whine = samples(0.5) { u in
                phase += 2 * .pi * (500 + 900 * u / 0.5) / self.rate
                return (sin(phase) + 0.3 * sin(phase * 2)) * min(1, u / 0.05) * 0.12
            }
            let beep = samples(0.2) { u in self.blip(u, 1900, length: 0.12) * 0.3 }
            return mix(0.75, [(0, whine), (0.5, beep)])

        case .wasted:
            // Round lost, spike-style: beeps that speed up, then a heavy detonation.
            var beepTimes: [Double] = []
            var at = 0.0, gap = 0.18
            while at < 0.75 {
                beepTimes.append(at)
                at += gap
                gap = max(0.04, gap * 0.78)
            }
            let beeps = samples(0.8) { u in
                var sum: Double = 0
                for start in beepTimes { sum += self.blip(u - start, 2100, length: 0.035) }
                return sum * 0.2
            }
            var impact = thump(42, decay: 2.2, 1.2)
            var rumble = 0.0
            for i in 0..<impact.count {
                let u = Double(i) / rate
                rumble += 0.04 * (noise() - rumble)
                impact[i] += rumble * exp(-u * 3.5) * 2.4
            }
            var bp = BandPass()
            let blast = samples(1.2) { u in
                bp.run(self.noise(), cutoff: 3000 - 2200 * min(u, 1), damping: 1.0, rate: self.rate) * exp(-u * 3) * 0.5
            }
            let boom = 0.82
            return mix(3.2, [(0, beeps), (boom, impact), (boom, blast),
                             (boom + 0.12, impact.map { $0 * 0.3 }), (boom + 0.26, impact.map { $0 * 0.15 })])

        case .rackPistol:
            return mix(0.45, [(0, clack(2400, 0.7)), (0.015, scrape(0.07, from: 1800, to: 2600, 0.45)),
                              (0.12, clack(1900, 1.0)), (0.12, thump(130, decay: 30, 0.6))])

        case .rackRifle:
            return mix(0.55, [(0, scrape(0.09, from: 1200, to: 2000, 0.5)), (0, clack(2100, 0.6)),
                              (0.2, clack(1500, 1.1)), (0.2, thump(95, decay: 22, 0.8)), (0.2, ring(1800, decay: 14, 0.22))])

        case .rackBolt:
            return mix(0.75, [(0, clack(3000, 0.5)), (0.07, scrape(0.1, from: 1500, to: 2400, 0.45)), (0.18, clack(2200, 0.6)),
                              (0.3, scrape(0.08, from: 2400, to: 1500, 0.45)), (0.4, clack(1700, 1.0)), (0.4, thump(110, decay: 28, 0.5))])

        case .rackPump:
            // The classic "chk-chk".
            return mix(0.6, [(0, scrape(0.06, from: 900, to: 1600, 0.8, damping: 0.7)), (0.05, clack(1300, 0.9)), (0.05, thump(90, decay: 25, 0.7)),
                             (0.2, scrape(0.05, from: 1600, to: 900, 0.8, damping: 0.7)), (0.24, clack(1100, 1.25)), (0.24, thump(80, decay: 22, 0.9))])

        case .rackHeavy:
            var phase = 0.0
            let whir = samples(0.75) { u in
                phase += 2 * .pi * (180 + 340 * min(u / 0.6, 1)) / self.rate
                let motor = 0.6 + 0.4 * sin(2 * .pi * 38 * u)
                return (sin(phase) + 0.3 * sin(phase * 2)) * motor * min(1, u / 0.1) * exp(-max(0, u - 0.55) * 12) * 0.16
            }
            return mix(0.95, [(0, thump(65, decay: 10, 1.0)), (0, clack(900, 1.0)), (0, ring(600, decay: 8, 0.28)), (0.12, whir)])

        case .pinPull:
            return mix(0.7, [(0, clack(4200, 0.3)), (0.02, ring(3400, decay: 9, 0.45)), (0.22, ring(5200, decay: 18, 0.25)),
                             (0.22, clack(2600, 0.25))])

        case .bladeDraw:
            return mix(0.75, [(0, scrape(0.28, from: 2500, to: 7500, 0.9, damping: 0.5)), (0.22, ring(4100, decay: 6, 0.33)),
                              (0.22, ring(6200, decay: 9, 0.18))])

        case .meleeSwing:
            return mix(0.5, [(0, whoosh(0.22, from: 500, to: 2200, 0.9)), (0.22, thump(110, decay: 26, 0.7)), (0.22, clack(800, 0.4))])
        }
    }

    private func wav(_ samples: [Float]) -> Data {
        var data = Data()
        func u32(_ v: UInt32) { withUnsafeBytes(of: v.littleEndian) { data.append(contentsOf: $0) } }
        func u16(_ v: UInt16) { withUnsafeBytes(of: v.littleEndian) { data.append(contentsOf: $0) } }
        let bytes = UInt32(samples.count * 2)
        data.append(contentsOf: Array("RIFF".utf8)); u32(36 + bytes)
        data.append(contentsOf: Array("WAVE".utf8))
        data.append(contentsOf: Array("fmt ".utf8)); u32(16); u16(1); u16(1); u32(UInt32(rate)); u32(UInt32(rate) * 2); u16(2); u16(16)
        data.append(contentsOf: Array("data".utf8)); u32(bytes)
        for sample in samples {
            u16(UInt16(bitPattern: Int16(max(-1, min(1, sample)) * 32_000)))
        }
        return data
    }

    /// Writes every sound to a folder as .wav (used to audition them).
    func export(to folder: URL) throws {
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        for kind in Kind.allCases {
            try wav(render(kind)).write(to: folder.appendingPathComponent("\(kind).wav"))
        }
    }
}
