import AppKit
import PDFKit

// PDF → Word / ODT / RTF / HTML / Markdown that keeps the layout. Per page:
// words → line pieces (cut at wide gaps and at the column gutter, so columns and table cells stay apart) →
// tables → figures (clusters of drawings and pictures, rendered as one image with their labels) → display
// equations (rendered as images) → reading order (left column before right between full-width parts) →
// paragraphs with their fonts, sizes, colours, alignment, indents and spacing.

// MARK: - Document model

final class DocRun {
    var text: String
    var bold: Bool
    var italic: Bool
    /// Font size in points; 0 keeps the document default.
    var size: Double
    /// Text colour as "RRGGBB"; nil keeps the default (black).
    var color: String?
    /// Font family; nil keeps the document default.
    var font: String?

    init(_ text: String, bold: Bool = false, italic: Bool = false, size: Double = 0, color: String? = nil, font: String? = nil) {
        self.text = text
        self.bold = bold
        self.italic = italic
        self.size = size
        self.color = color
        self.font = font
    }

    func sameStyle(_ o: DocRun) -> Bool {
        bold == o.bold && italic == o.italic && abs(size - o.size) < 0.01 && color == o.color && font == o.font
    }

    func copy(text: String? = nil) -> DocRun {
        DocRun(text ?? self.text, bold: bold, italic: italic, size: size, color: color, font: font)
    }
}

enum DocAlign {
    case left, center, right, justify
}

/// A picture placed in the text flow, sized in points.
struct DocImage {
    var data: Data
    /// "png" or "jpeg".
    var format: String
    var width: Double
    var height: Double
}

/// A grid of cells; each cell is a list of runs. Column widths are in points.
final class DocTable {
    var rows: [[[DocRun]]] = []
    var widths: [Double] = []
    var align = DocAlign.left
    var indent = 0.0

    static func cellText(_ cell: [DocRun]) -> String {
        cell.map(\.text).joined().trimmingCharacters(in: .whitespaces)
    }

    var plainText: String {
        rows.map { $0.map(DocTable.cellText).joined(separator: "\t") }.joined(separator: "\n")
    }

    var columns: Int { rows.map(\.count).max() ?? 0 }

    func width(_ k: Int) -> Double { k < widths.count ? widths[k] : 90 }
}

final class DocPara {
    var runs: [DocRun] = []
    /// 0 for body text, 1–3 for headings.
    var heading = 0
    var bullet = false
    var align = DocAlign.left
    /// Left indent in points.
    var indent = 0.0
    /// Extra indent of the first line in points.
    var firstIndent = 0.0
    /// Space after the paragraph in points; negative keeps the default.
    var spaceAfter = -1.0
    /// Exact distance between lines in points; 0 keeps the default.
    var lineSpacing = 0.0
    /// Starts on a new page.
    var pageBreak = false
    /// 2 when the paragraph sits in a two-column part of the page.
    var columns = 1
    var image: DocImage?
    var table: DocTable?

    var text: String { runs.map(\.text).joined() }
}

final class DocModel {
    var paras: [DocPara] = []
    /// Page size and margins in points; 0 means A4 with the default margins.
    var pageWidth = 0.0
    var pageHeight = 0.0
    var marginLeft = 0.0
    var marginRight = 0.0
    var marginTop = 0.0
    var marginBottom = 0.0
    /// Space between the two columns of two-column parts, in points.
    var columnGap = 0.0

    var plainText: String {
        paras.filter { $0.image == nil }.map { $0.table?.plainText ?? $0.text }.joined(separator: "\n")
    }
}

// MARK: - Reading the PDF

enum PDFLayout {
    private final class Word {
        var runs: [DocRun] = []
        var text = ""
        var box = CGRect.zero
        var size = 0.0
        var letters = 0
        var mathLetters = 0
    }

    private final class Seg {
        var runs: [DocRun] = []
        var text = ""
        var size = 0.0
        var baseline = 0.0
        var bullet = false
        var used = false
        var letters = 0
        var mathLetters = 0
        var box = CGRect.zero
        var column = -1
        /// A figure or table standing in the reading order in place of text.
        var block: Item?
    }

    private final class Item {
        let para = DocPara()
        var top = 0.0
        var size = 0.0
        var lines: [CGRect] = []
        var baselines: [Double] = []
        var box: CGRect?
        var page = 0
        var column = -1
        var twoColumns = false
        var pitch = 0.0
    }

    // MARK: Styles

    private static func nameHas(_ name: String, _ parts: [String]) -> Bool {
        parts.contains { name.range(of: $0, options: .caseInsensitive) != nil }
    }

    private static func isMathFont(_ name: String) -> Bool {
        let lower = name.lowercased()
        return ["cmmi", "cmsy", "cmex", "msam", "msbm", "math", "symbol", "stix", "cmbsy", "cmmib", "eufm", "rsfs", "dsrom", "bbold"].contains { lower.contains($0) }
    }

    private static var familyCache: [String: String?] = [:]
    private static let familyLock = NSLock()

    /// A font family Word will have for a PDF font name: "ABCDEF+NimbusRomNo9L-Medi" → "Times New Roman",
    /// "Calibri-Bold" → "Calibri". Chinese and maths fonts give nil (the document default handles them).
    static func fontFamily(_ pdfName: String) -> String? {
        familyLock.lock()
        defer { familyLock.unlock() }
        if let hit = familyCache[pdfName] { return hit }
        var name = pdfName
        if let plus = name.firstIndex(of: "+"), name.distance(from: name.startIndex, to: plus) == 6 { name = String(name[name.index(after: plus)...]) }
        let lower = name.lowercased()
        var family: String?
        let cjk = ["ming", "song", "hei", "kai", "pingfang", "jhenghei", "yahei", "cjk", "sourcehan", "gothic", "mincho", "wenquanyi", "simsun", "dfkai", "biaukai", "hiragino", "notosanstc", "notoserif", "msung", "stsong"]
        let math = ["cmsy", "cmmi", "cmex", "msam", "msbm", "symbol", "math", "dingbat", "wingding", "stix"]
        if cjk.contains(where: { lower.contains($0) }) || math.contains(where: { lower.contains($0) }) {
            family = nil
        } else if ["times", "nimbusrom", "termes", "cmr", "cmbx", "cmti", "cmsl", "lmroman", "dejavuserif", "liberationserif", "tinos"].contains(where: { lower.contains($0) }) {
            family = "Times New Roman"
        } else if ["helvetica", "arial", "nimbussan", "heros", "liberationsans", "arimo", "cmss"].contains(where: { lower.contains($0) }) {
            family = "Arial"
        } else if ["courier", "cmtt", "nimbusmon", "cousine", "liberationmono", "mono"].contains(where: { lower.contains($0) }) {
            family = "Courier New"
        } else {
            var head = String(name.split(whereSeparator: { $0 == "-" || $0 == "," }).first ?? "")
            for suffix in ["PSMT", "MT", "PS", "Std", "Pro"] where head.hasSuffix(suffix) && head.count > suffix.count + 2 {
                head = String(head.dropLast(suffix.count))
                break
            }
            family = head.count >= 3 && head.allSatisfy({ $0.isLetter || $0 == " " }) ? head : nil
        }
        familyCache[pdfName] = family
        return family
    }

    /// The look of one character: weight, slant, size (to half a point), colour and font family.
    private static func style(_ attributes: [NSAttributedString.Key: Any]) -> (run: DocRun, math: Bool) {
        var bold = false, italic = false, size = 0.0, family: String?, math = false
        if let font = attributes[.font] as? NSFont {
            let traits = font.fontDescriptor.symbolicTraits
            bold = traits.contains(.bold) || nameHas(font.fontName, ["Bold", "Black", "Heavy", "Semibold", "Demi"])
            italic = traits.contains(.italic) || nameHas(font.fontName, ["Italic", "Oblique"])
            size = (Double(font.pointSize) * 2).rounded() / 2
            family = fontFamily(font.fontName)
            math = isMathFont(font.fontName)
        }
        var color: String?
        if let c = (attributes[.foregroundColor] as? NSColor)?.usingColorSpace(.sRGB) {
            let r = Double(c.redComponent), g = Double(c.greenComponent), b = Double(c.blueComponent)
            if max(r, g, b) > 0.18 {
                color = String(format: "%02X%02X%02X", Int((r * 255).rounded()), Int((g * 255).rounded()), Int((b * 255).rounded()))
            }
        }
        return (DocRun("", bold: bold, italic: italic, size: size, color: color, font: family), math)
    }

    /// Appends text to the last run when the style matches (spaces always join), otherwise starts a new run.
    private static func append(_ runs: inout [DocRun], _ text: String, _ style: DocRun) {
        guard !text.isEmpty else { return }
        if let last = runs.last, last.sameStyle(style) || text.trimmingCharacters(in: .whitespaces).isEmpty {
            last.text += text
            return
        }
        runs.append(style.copy(text: text))
    }

    private static func removePrefix(_ runs: [DocRun], _ count: Int) -> [DocRun] {
        var left = count
        var result: [DocRun] = []
        for r in runs {
            if left >= r.text.count {
                left -= r.text.count
                continue
            }
            result.append(r.copy(text: String(r.text.dropFirst(left))))
            left = 0
        }
        if let first = result.first { first.text = String(first.text.drop(while: { $0 == " " })) }
        return result
    }

    private static func joinsTight(_ before: String, _ after: String) -> Bool {
        guard let a = before.unicodeScalars.last, let b = after.unicodeScalars.first else { return true }
        if TextFix.isCJK(a) && TextFix.isCJK(b) { return true }
        let punctuation = CharacterSet.punctuationCharacters
        if (TextFix.isCJK(a) && punctuation.contains(a)) || (TextFix.isCJK(b) && punctuation.contains(b)) { return true }
        return ",.;:!?)]%".unicodeScalars.contains(b)
    }

    private static func fixRadicals(_ text: String) -> String {
        let fixed = NSMutableAttributedString(string: text)
        TextFix.radicals(in: fixed)
        return fixed.string
    }

    private static let bulletMarks = ["•", "●", "▪", "■", "◦", "○", "·", "\u{F0B7}", "\u{F0A7}", "\u{F076}", "\u{F0D8}", "–", "-"]

    private static func stripBullet(_ text: String, _ bullet: inout Bool) -> String {
        bullet = false
        for mark in bulletMarks where text.hasPrefix(mark) {
            let rest = String(text.dropFirst(mark.count))
            // "-" and "–" only count with a space after them ("- item"), never in "-5" or "–2020".
            if ["-", "–", "·"].contains(mark), !rest.hasPrefix(" ") { return text }
            bullet = true
            return String(rest.drop(while: { $0 == " " }))
        }
        return text
    }

    // MARK: Words

    /// AGENTWHEEL_PDF_DEBUG=<file> dumps the first words PDFKit reports (used by the CI smoke test).
    private static func debugDump(_ text: String) {
        guard let path = ProcessInfo.processInfo.environment["AGENTWHEEL_PDF_DEBUG"], let data = text.data(using: .utf8) else { return }
        if let handle = FileHandle(forWritingAtPath: path) {
            handle.seekToEndOfFile()
            handle.write(data)
            handle.closeFile()
        } else {
            FileManager.default.createFile(atPath: path, contents: data)
        }
    }

    /// Words of a page: runs of non-blank characters of page.string, each measured with its own selection
    /// (PDFKit's selections and page.string share one index space).
    private static func words(_ page: PDFPage, _ index: Int) -> [Word] {
        guard let plain = page.string, !plain.isEmpty else { return [] }
        let attributed = page.attributedString
        let string = plain as NSString
        let sameText = attributed?.string == plain
        let blank = CharacterSet.whitespacesAndNewlines
        func isBlank(_ unit: unichar) -> Bool {
            guard let scalar = Unicode.Scalar(unit) else { return false }
            return blank.contains(scalar)
        }
        var result: [Word] = []
        var i = 0
        while i < string.length {
            if isBlank(string.character(at: i)) {
                i += 1
                continue
            }
            var j = i
            while j < string.length, !isBlank(string.character(at: j)) { j += 1 }
            let range = NSRange(location: i, length: j - i)
            i = j
            guard let selection = page.selection(for: range) else { continue }
            let box = selection.bounds(for: page)
            guard box.width > 0, box.height > 0, box.width < 2000 else { continue }
            var fallback: [NSAttributedString.Key: Any] = [:]
            if !sameText, let own = selection.attributedString, own.length > 0 { fallback = own.attributes(at: 0, effectiveRange: nil) }
            let word = Word()
            word.box = box
            var sizes: [Double] = []
            var k = range.location
            let end = range.location + range.length
            while k < end {
                let length = UTF16.isLeadSurrogate(string.character(at: k)) && k + 1 < end ? 2 : 1
                let ch = string.substring(with: NSRange(location: k, length: length))
                let attributes = sameText ? attributed!.attributes(at: k, effectiveRange: nil) : fallback
                let (st, math) = style(attributes)
                append(&word.runs, ch, st)
                word.text += ch
                word.letters += 1
                if math { word.mathLetters += 1 }
                if st.size > 0 { sizes.append(st.size) }
                k += length
            }
            sizes.sort()
            word.size = sizes.isEmpty ? Double(box.height) * 0.8 : sizes[sizes.count / 2]
            result.append(word)
        }
        if index == 0 {
            var dump = "page 1: \(result.count) words, string \(string.length), same=\(sameText)\n"
            for w in result.prefix(30) {
                dump += String(format: "%@ @ %.1f,%.1f %.1fx%.1f size %.1f\n", w.text, w.box.minX, w.box.minY, w.box.width, w.box.height, w.size)
            }
            debugDump(dump)
        }
        return result
    }

    // MARK: Gutter

    /// The gutter between two columns: a vertical strip near the middle of the page that (almost) no text row
    /// crosses, with plenty of text on both sides. Titles and full-width captions may cross it.
    private static func gutter(_ words: [Word]) -> (Double, Double)? {
        guard words.count >= 40 else { return nil }
        let left = Double(words.map(\.box.minX).min()!), right = Double(words.map(\.box.maxX).max()!), width = right - left
        guard width >= 200 else { return nil }
        var rowMap: [Int: [CGRect]] = [:]
        for w in words { rowMap[Int((Double(w.box.midY) / 3).rounded()), default: []].append(w.box) }
        let rows = Array(rowMap.values)
        let from = Int(left + width * 0.3), to = Int(left + width * 0.7)
        let allowed = max(2, Int(Double(rows.count) * 0.15))
        var bestStart = -1, bestLength = 0, runStart = -1
        if from <= to {
            for x in from...to {
                let xx = CGFloat(x)
                let crossing = rows.filter { $0.contains { $0.minX < xx && $0.maxX > xx } }.count
                if crossing <= allowed {
                    if runStart < 0 { runStart = x }
                    if x - runStart + 1 > bestLength {
                        bestLength = x - runStart + 1
                        bestStart = runStart
                    }
                } else {
                    runStart = -1
                }
            }
        }
        guard bestLength >= 4 else { return nil }
        let gl = Double(bestStart), gr = Double(bestStart + bestLength - 1)
        let leftRows = rows.filter { $0.contains { Double($0.maxX) <= gl + 1 && Double($0.minX) < gl - width * 0.2 } }.count
        let rightRows = rows.filter { $0.contains { Double($0.minX) >= gr - 1 && Double($0.maxX) > gr + width * 0.2 } }.count
        guard leftRows >= 8, rightRows >= 8 else { return nil }
        return (gl, gr)
    }

    private static func columnOf(_ box: CGRect, _ gutter: (Double, Double)?) -> Int {
        guard let (gl, gr) = gutter else { return -1 }
        let mid = (gl + gr) / 2
        if Double(box.maxX) <= mid + 2 { return 0 }
        if Double(box.minX) >= mid - 2 { return 1 }
        return -1
    }

    // MARK: Line pieces

    /// Words sharing a line form a row; a row is cut wherever a gap is much wider than a space or crosses the
    /// gutter, so the two columns of a page, or the cells of a table, become separate pieces.
    private static func segments(_ words: [Word], _ gutter: (Double, Double)?) -> [Seg] {
        var rows: [(mid: Double, size: Double, members: [Word])] = []
        for word in words.sorted(by: { $0.box.midY > $1.box.midY }) {
            let mid = Double(word.box.midY)
            var found = -1
            var r = rows.count - 1
            while r >= 0 && r >= rows.count - 4 {
                if abs(rows[r].mid - mid) <= max(rows[r].size, word.size) * 0.35 { found = r }
                r -= 1
            }
            if found < 0 { rows.append((mid, word.size, [word])) } else { rows[found].members.append(word) }
        }

        var result: [Seg] = []
        for row in rows {
            var seg: Seg?
            var lastRight = 0.0
            for word in row.members.sorted(by: { $0.box.minX < $1.box.minX }) {
                let em = max(4, word.size)
                let gap = Double(word.box.minX) - lastRight
                var acrossGutter = false
                if let (gl, gr) = gutter {
                    let mid = (gl + gr) / 2
                    acrossGutter = lastRight <= mid + 1 && Double(word.box.minX) >= mid - 1 && gap > max(4, (gr - gl) * 0.6)
                }
                if let s = seg, gap > max(7, em * 1.15) || acrossGutter {
                    result.append(s)
                    seg = nil
                }
                let s: Seg
                if let open = seg {
                    s = open
                    if gap > em * 0.15, let first = word.runs.first {
                        append(&s.runs, " ", first)
                        s.text += " "
                    }
                    s.box = s.box.union(word.box)
                } else {
                    s = Seg()
                    s.box = word.box
                    s.baseline = row.mid
                    seg = s
                }
                for r in word.runs { append(&s.runs, r.text, r) }
                s.text += word.text
                s.letters += word.letters
                s.mathLetters += word.mathLetters
                lastRight = Double(word.box.maxX)
            }
            if let s = seg { result.append(s) }
        }
        for s in result {
            var sizes: [Double] = []
            for r in s.runs where r.size > 0 && !r.text.trimmingCharacters(in: .whitespaces).isEmpty {
                sizes += Array(repeating: r.size, count: r.text.count)
            }
            sizes.sort()
            s.size = sizes.isEmpty ? max(6, Double(s.box.height) * 0.8) : sizes[sizes.count / 2]
        }

        // Bullets: strip the mark; a mark standing on its own belongs to the piece just right of it.
        var marks: [Seg] = []
        for s in result {
            var bullet = false
            let rest = stripBullet(s.text, &bullet)
            guard bullet else { continue }
            if rest.trimmingCharacters(in: .whitespaces).isEmpty {
                marks.append(s)
                continue
            }
            s.runs = removePrefix(s.runs, s.text.count - rest.count)
            s.text = rest
            s.bullet = true
        }
        for mark in marks {
            result.removeAll { $0 === mark }
            let owner = result.filter { $0.box.minX >= mark.box.minX && $0.box.minX - mark.box.maxX < 40 && mark.box.midY <= $0.box.maxY + 2 && mark.box.midY >= $0.box.minY - 2 }
                .min { ($0.box.minX - mark.box.maxX) < ($1.box.minX - mark.box.maxX) }
            owner?.bullet = true
        }
        return result
    }

    // MARK: Tables

    /// Tables: two or more consecutive rows that each hold the same number (2+) of separate, short pieces of
    /// text lined up under one another.
    private static func tables(_ segs: [Seg]) -> [Item] {
        var rows: [[Seg]] = []
        for seg in segs.filter({ !$0.used }).sorted(by: { $0.baseline > $1.baseline }) {
            if let first = rows.last?.first, abs(first.baseline - seg.baseline) < max(first.size, seg.size) * 0.4 {
                rows[rows.count - 1].append(seg)
            } else {
                rows.append([seg])
            }
        }
        rows = rows.map { $0.sorted { $0.box.minX < $1.box.minX } }

        var result: [Item] = []
        var r = 0
        while r < rows.count {
            guard rows[r].count >= 2 else { r += 1; continue }
            let n = rows[r].count
            var columns = rows[r].map { (left: Double($0.box.minX), right: Double($0.box.maxX)) }
            var end = r + 1
            while end < rows.count, rows[end].count == n {
                let previous = rows[end - 1]
                if previous[0].baseline - rows[end][0].baseline > max(previous[0].size, 6) * 4 { break }
                var aligned = true
                for k in 0..<n {
                    let c = rows[end][k].box
                    if !(Double(c.minX) < columns[k].right + 4 && Double(c.maxX) > columns[k].left - 4) { aligned = false; break }
                }
                if !aligned { break }
                for k in 0..<n {
                    columns[k] = (min(columns[k].left, Double(rows[end][k].box.minX)), max(columns[k].right, Double(rows[end][k].box.maxX)))
                }
                end += 1
            }
            let group = Array(rows[r..<end])
            let all = group.flatMap { $0 }
            let span = columns[n - 1].right - columns[0].left
            let averageLength = Double(all.map(\.text.count).reduce(0, +)) / Double(max(1, all.count))
            let widest = all.map { Double($0.box.width) }.max() ?? 0
            // Real tables hold short entries; a line of running text from the other column is never a cell.
            let shortCells = averageLength < 40 && widest < max(span * 0.6, 120) && all.allSatisfy { $0.text.count <= 60 && $0.box.width < 190 }
            if group.count >= 2, shortCells {
                let table = DocTable()
                let em = max(8, all.map(\.size).reduce(0, +) / Double(max(1, all.count)))
                for k in 0..<n {
                    let raw = k + 1 < n ? columns[k + 1].left - columns[k].left : columns[k].right - columns[k].left + em * 1.5
                    table.widths.append((max(em * 3, raw) + 8).rounded())
                }
                for row in group {
                    table.rows.append(row.map { s in s.runs.map { $0.copy(text: fixRadicals($0.text)) } })
                    for s in row { s.used = true }
                }
                let item = Item()
                item.top = Double(group[0].map(\.box.maxY).max() ?? 0)
                item.size = group[0][0].size
                item.para.table = table
                item.lines = all.map(\.box)
                item.box = all.dropFirst().reduce(all[0].box) { $0.union($1.box) }
                result.append(item)
                r = end
            } else {
                r += 1
            }
        }
        return result
    }

    // MARK: Drawings

    private struct Shape {
        var rect: CGRect
        var image: Bool
    }

    private final class Scan {
        var stack: [(CGAffineTransform, CGRect, Bool)] = []
        var ctm = CGAffineTransform.identity
        var clip = CGRect.infinite
        var whiteFill = false
        var path = CGRect.null
        var pendingClip = false
        var shapes: [Shape] = []
        var depth = 0
        var table: CGPDFOperatorTableRef?

        func save() { stack.append((ctm, clip, whiteFill)) }

        func restore() {
            guard let last = stack.popLast() else { return }
            (ctm, clip, whiteFill) = last
        }

        func point(_ x: CGFloat, _ y: CGFloat) {
            let p = CGRect(origin: CGPoint(x: x, y: y).applying(ctm), size: .zero)
            path = path.isNull ? p : path.union(p)
        }

        func paint(fill: Bool, stroke: Bool) {
            defer {
                if pendingClip, !path.isNull { clip = clip.intersection(path) }
                pendingClip = false
                path = .null
            }
            guard !path.isNull, fill || stroke else { return }
            // White fills are usually just the paper colour behind something else.
            if fill && !stroke && whiteFill { return }
            let visible = path.intersection(clip)
            if !visible.isNull && (visible.width > 0 || visible.height > 0) { shapes.append(Shape(rect: visible, image: false)) }
        }
    }

    private static func scan(_ info: UnsafeMutableRawPointer?) -> Scan? {
        guard let info else { return nil }
        return Unmanaged<Scan>.fromOpaque(info).takeUnretainedValue()
    }

    private static func pop(_ scanner: CGPDFScannerRef, _ count: Int) -> [CGFloat]? {
        var v = [CGPDFReal](repeating: 0, count: count)
        for i in (0..<count).reversed() { if !CGPDFScannerPopNumber(scanner, &v[i]) { return nil } }
        return v
    }

    /// Where the page draws shapes and pictures (clipping applied), by following its content stream.
    private static func shapes(_ page: CGPDFPage) -> [Shape] {
        guard let table = CGPDFOperatorTableCreate() else { return [] }
        defer { CGPDFOperatorTableRelease(table) }
        let state = Scan()
        state.table = table
        CGPDFOperatorTableSetCallback(table, "q") { _, info in PDFLayout.scan(info)?.save() }
        CGPDFOperatorTableSetCallback(table, "Q") { _, info in PDFLayout.scan(info)?.restore() }
        CGPDFOperatorTableSetCallback(table, "cm") { scanner, info in
            guard let s = PDFLayout.scan(info), let v = PDFLayout.pop(scanner, 6) else { return }
            s.ctm = CGAffineTransform(a: v[0], b: v[1], c: v[2], d: v[3], tx: v[4], ty: v[5]).concatenating(s.ctm)
        }
        CGPDFOperatorTableSetCallback(table, "m") { scanner, info in
            guard let s = PDFLayout.scan(info), let v = PDFLayout.pop(scanner, 2) else { return }
            s.point(v[0], v[1])
        }
        CGPDFOperatorTableSetCallback(table, "l") { scanner, info in
            guard let s = PDFLayout.scan(info), let v = PDFLayout.pop(scanner, 2) else { return }
            s.point(v[0], v[1])
        }
        CGPDFOperatorTableSetCallback(table, "c") { scanner, info in
            guard let s = PDFLayout.scan(info), let v = PDFLayout.pop(scanner, 6) else { return }
            s.point(v[0], v[1])
            s.point(v[2], v[3])
            s.point(v[4], v[5])
        }
        CGPDFOperatorTableSetCallback(table, "v") { scanner, info in
            guard let s = PDFLayout.scan(info), let v = PDFLayout.pop(scanner, 4) else { return }
            s.point(v[0], v[1])
            s.point(v[2], v[3])
        }
        CGPDFOperatorTableSetCallback(table, "y") { scanner, info in
            guard let s = PDFLayout.scan(info), let v = PDFLayout.pop(scanner, 4) else { return }
            s.point(v[0], v[1])
            s.point(v[2], v[3])
        }
        CGPDFOperatorTableSetCallback(table, "re") { scanner, info in
            guard let s = PDFLayout.scan(info), let v = PDFLayout.pop(scanner, 4) else { return }
            s.point(v[0], v[1])
            s.point(v[0] + v[2], v[1])
            s.point(v[0], v[1] + v[3])
            s.point(v[0] + v[2], v[1] + v[3])
        }
        CGPDFOperatorTableSetCallback(table, "W") { _, info in PDFLayout.scan(info)?.pendingClip = true }
        CGPDFOperatorTableSetCallback(table, "W*") { _, info in PDFLayout.scan(info)?.pendingClip = true }
        CGPDFOperatorTableSetCallback(table, "n") { _, info in PDFLayout.scan(info)?.paint(fill: false, stroke: false) }
        CGPDFOperatorTableSetCallback(table, "S") { _, info in PDFLayout.scan(info)?.paint(fill: false, stroke: true) }
        CGPDFOperatorTableSetCallback(table, "s") { _, info in PDFLayout.scan(info)?.paint(fill: false, stroke: true) }
        CGPDFOperatorTableSetCallback(table, "f") { _, info in PDFLayout.scan(info)?.paint(fill: true, stroke: false) }
        CGPDFOperatorTableSetCallback(table, "F") { _, info in PDFLayout.scan(info)?.paint(fill: true, stroke: false) }
        CGPDFOperatorTableSetCallback(table, "f*") { _, info in PDFLayout.scan(info)?.paint(fill: true, stroke: false) }
        CGPDFOperatorTableSetCallback(table, "B") { _, info in PDFLayout.scan(info)?.paint(fill: true, stroke: true) }
        CGPDFOperatorTableSetCallback(table, "B*") { _, info in PDFLayout.scan(info)?.paint(fill: true, stroke: true) }
        CGPDFOperatorTableSetCallback(table, "b") { _, info in PDFLayout.scan(info)?.paint(fill: true, stroke: true) }
        CGPDFOperatorTableSetCallback(table, "b*") { _, info in PDFLayout.scan(info)?.paint(fill: true, stroke: true) }
        CGPDFOperatorTableSetCallback(table, "g") { scanner, info in
            guard let s = PDFLayout.scan(info), let v = PDFLayout.pop(scanner, 1) else { return }
            s.whiteFill = v[0] > 0.97
        }
        CGPDFOperatorTableSetCallback(table, "rg") { scanner, info in
            guard let s = PDFLayout.scan(info), let v = PDFLayout.pop(scanner, 3) else { return }
            s.whiteFill = v[0] > 0.97 && v[1] > 0.97 && v[2] > 0.97
        }
        CGPDFOperatorTableSetCallback(table, "k") { scanner, info in
            guard let s = PDFLayout.scan(info), let v = PDFLayout.pop(scanner, 4) else { return }
            s.whiteFill = v[0] < 0.03 && v[1] < 0.03 && v[2] < 0.03 && v[3] < 0.03
        }
        CGPDFOperatorTableSetCallback(table, "cs") { _, info in PDFLayout.scan(info)?.whiteFill = false }
        CGPDFOperatorTableSetCallback(table, "sc") { _, info in PDFLayout.scan(info)?.whiteFill = false }
        CGPDFOperatorTableSetCallback(table, "scn") { _, info in PDFLayout.scan(info)?.whiteFill = false }
        CGPDFOperatorTableSetCallback(table, "Do") { scanner, info in
            guard let s = PDFLayout.scan(info) else { return }
            var name: UnsafePointer<Int8>?
            guard CGPDFScannerPopName(scanner, &name), let name else { return }
            let content = CGPDFScannerGetContentStream(scanner)
            guard let object = CGPDFContentStreamGetResource(content, "XObject", name) else { return }
            var stream: CGPDFStreamRef?
            guard CGPDFObjectGetValue(object, .stream, &stream), let stream, let dict = CGPDFStreamGetDictionary(stream) else { return }
            var subtype: UnsafePointer<Int8>?
            guard CGPDFDictionaryGetName(dict, "Subtype", &subtype), let subtype else { return }
            let kind = String(cString: subtype)
            if kind == "Image" {
                let rect = CGRect(x: 0, y: 0, width: 1, height: 1).applying(s.ctm).standardized.intersection(s.clip)
                if !rect.isNull { s.shapes.append(Shape(rect: rect, image: true)) }
            } else if kind == "Form", s.depth < 4, let opTable = s.table {
                var resources: CGPDFDictionaryRef?
                guard CGPDFDictionaryGetDictionary(dict, "Resources", &resources), let resources else { return }
                s.save()
                var matrix: CGPDFArrayRef?
                if CGPDFDictionaryGetArray(dict, "Matrix", &matrix), let matrix, CGPDFArrayGetCount(matrix) == 6 {
                    var m = [CGPDFReal](repeating: 0, count: 6)
                    for i in 0..<6 { _ = CGPDFArrayGetNumber(matrix, i, &m[i]) }
                    s.ctm = CGAffineTransform(a: m[0], b: m[1], c: m[2], d: m[3], tx: m[4], ty: m[5]).concatenating(s.ctm)
                }
                s.depth += 1
                let inner = CGPDFContentStreamCreateWithStream(stream, resources, content)
                let innerScanner = CGPDFScannerCreate(inner, opTable, info)
                _ = CGPDFScannerScan(innerScanner)
                CGPDFScannerRelease(innerScanner)
                CGPDFContentStreamRelease(inner)
                s.depth -= 1
                s.restore()
            }
        }
        let content = CGPDFContentStreamCreateWithPage(page)
        let scanner = CGPDFScannerCreate(content, table, Unmanaged.passUnretained(state).toOpaque())
        _ = CGPDFScannerScan(scanner)
        CGPDFScannerRelease(scanner)
        CGPDFContentStreamRelease(content)
        return state.shapes
    }

    /// The region as it appears on the page, rendered at 2× (JPEG for photos, PNG otherwise).
    private static func renderRegion(_ page: CGPDFPage, _ rect: CGRect) -> DocImage? {
        var scale: CGFloat = 2
        let longest = max(rect.width, rect.height)
        if longest * scale > 3000 { scale = 3000 / longest }
        let w = max(1, Int(rect.width * scale)), h = max(1, Int(rect.height * scale))
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return nil }
        ctx.setFillColor(CGColor(gray: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
        ctx.interpolationQuality = .high
        ctx.scaleBy(x: scale, y: scale)
        ctx.translateBy(x: -rect.minX, y: -rect.minY)
        ctx.drawPDFPage(page)
        guard let image = ctx.makeImage() else { return nil }
        let rep = NSBitmapImageRep(cgImage: image)
        let photo = w * h > 600_000
        guard let data = photo ? rep.representation(using: .jpeg, properties: [.compressionFactor: 0.88]) : rep.representation(using: .png, properties: [:]) else { return nil }
        return DocImage(data: data, format: photo ? "jpeg" : "png", width: Double(rect.width), height: Double(rect.height))
    }

    // MARK: Figures

    private static let captionStarts = ["Fig", "FIG", "Figure", "FIGURE", "Table", "TABLE", "圖", "表", "Algorithm"]

    /// A short piece of text that can be part of a figure: not a caption, not a sentence of the body.
    private static func isLabel(_ s: Seg) -> Bool {
        s.text.count <= 40 && !captionStarts.contains { s.text.hasPrefix($0) }
    }

    private static func area(_ r: CGRect) -> Double { r.isNull ? 0 : Double(max(0, r.width) * max(0, r.height)) }

    private static func near(_ a: CGRect, _ b: CGRect, _ pad: CGFloat) -> Bool { a.insetBy(dx: -pad, dy: -pad).intersects(b) }

    private static func center(_ r: CGRect) -> CGPoint { CGPoint(x: r.midX, y: r.midY) }

    /// Panels of one figure: next to each other, mostly at the same height, a small gap apart.
    private static func sideBySide(_ a: CGRect, _ b: CGRect) -> Bool {
        let overlap = min(a.maxY, b.maxY) - max(a.minY, b.minY)
        let gap = max(a.minX, b.minX) - min(a.maxX, b.maxX)
        return overlap > min(a.height, b.height) * 0.5 && gap < 40
    }

    /// Figures: clusters of drawn shapes and pictures that touch each other, with the short labels in and
    /// around them. A lone shape, a thin rule, a page background or a box full of text is not a figure.
    private static func figures(_ shapes: [Shape], _ segs: [Seg], _ tables: [CGRect], _ gutter: (Double, Double)?, _ page: CGRect) -> [CGRect] {
        let pageArea = area(page)
        let items = shapes.filter { s in
            if s.image { return s.rect.width >= 6 && s.rect.height >= 6 && !(!segs.isEmpty && area(s.rect) > pageArea * 0.8) }
            return area(s.rect) <= pageArea * 0.8
        }
        guard !items.isEmpty else { return [] }
        // Two figures side by side in different columns stay two figures.
        func sameSide(_ a: CGRect, _ b: CGRect) -> Bool {
            guard let (gl, gr) = gutter else { return true }
            let mid = CGFloat((gl + gr) / 2)
            let aLeft = a.maxX <= mid + 2, aRight = a.minX >= mid - 2, bLeft = b.maxX <= mid + 2, bRight = b.minX >= mid - 2
            return !((aLeft && bRight) || (aRight && bLeft))
        }
        // Union-find over shapes that touch (within 4 pt).
        var parent = Array(0..<items.count)
        func find(_ x: Int) -> Int {
            var x = x
            while parent[x] != x {
                parent[x] = parent[parent[x]]
                x = parent[x]
            }
            return x
        }
        let order = (0..<items.count).sorted { items[$0].rect.minX < items[$1].rect.minX }
        for a in 0..<order.count {
            let ra = items[order[a]].rect
            for b in (a + 1)..<order.count {
                let rb = items[order[b]].rect
                if rb.minX - 4 > ra.maxX { break }
                if near(ra, rb, 4) && sameSide(ra, rb) { parent[find(order[a])] = find(order[b]) }
            }
        }
        var groups: [Int: [Shape]] = [:]
        for i in 0..<items.count { groups[find(i), default: []].append(items[i]) }

        var result: [CGRect] = []
        for members in groups.values {
            let rect = members.dropFirst().reduce(members[0].rect) { $0.union($1.rect) }
            let hasImage = members.contains { $0.image }
            if rect.width < 24 || rect.height < 18 || area(rect) > pageArea * 0.85 { continue }
            if !hasImage && members.count < 3 { continue }
            if !hasImage {
                let inside = segs.filter { !$0.used && isLabel($0) && rect.insetBy(dx: -2, dy: -2).contains(center($0.box)) }
                let textArea = inside.reduce(0.0) { $0 + area($1.box) }
                let chars = inside.reduce(0) { $0 + $1.text.count }
                // A frame or table grid full of text stays text …
                if chars > 220 || textArea > area(rect) * 0.3 { continue }
                // … and so does a ruled table, unless the drawing has real shapes (bars, curves, boxes).
                let realShapes = members.filter { $0.rect.width > 2.5 && $0.rect.height > 2.5 }.count
                if realShapes < 3 && tables.contains(where: { $0.intersects(rect) && area($0.intersection(rect)) > area($0) * 0.4 }) { continue }
            }
            result.append(rect)
        }

        // Grow each figure by the labels around it, and join figures that end up next to each other.
        var changed = true
        while changed {
            changed = false
            for i in 0..<result.count {
                for s in segs where !s.used && isLabel(s) && near(result[i], s.box, 8) {
                    s.used = true
                    result[i] = result[i].union(s.box)
                    changed = true
                }
            }
            if changed { continue }
            search: for i in 0..<result.count {
                for j in (i + 1)..<result.count where (near(result[i], result[j], 12) || sideBySide(result[i], result[j])) && sameSide(result[i], result[j]) {
                    result[i] = result[i].union(result[j])
                    result.remove(at: j)
                    changed = true
                    break search
                }
            }
        }
        return result
    }

    // MARK: Equations

    private static func isEquationNumber(_ text: String) -> Bool {
        text.trimmingCharacters(in: .whitespaces).range(of: #"^\(\d{1,3}[a-z]?\)$"#, options: .regularExpression) != nil
    }

    /// Display equations: lines set mostly in maths fonts, with the bits around them (indices, fraction parts,
    /// the equation number). Word can't rebuild them from a PDF, so they become pictures.
    private static func equations(_ segs: [Seg], _ gutter: (Double, Double)?) -> [CGRect] {
        let mathy = segs.filter { !$0.used && $0.letters > 0 && $0.mathLetters * 2 >= $0.letters && !isEquationNumber($0.text) }
        var groups: [(rect: CGRect, column: Int)] = []
        for seg in mathy.sorted(by: { $0.box.maxY > $1.box.maxY }) {
            let column = columnOf(seg.box, gutter)
            let em = CGFloat(max(6, seg.size))
            if let at = groups.firstIndex(where: { $0.column == column && seg.box.maxY >= $0.rect.minY - em * 1.6 && seg.box.minY <= $0.rect.maxY + em }) {
                groups[at].rect = groups[at].rect.union(seg.box)
            } else {
                groups.append((seg.box, column))
            }
        }
        var result: [CGRect] = []
        for (start, column) in groups {
            var rect = start
            // Everything small inside or beside the formula on its rows belongs to it: indices, fraction parts, "(3)".
            var grew = true
            while grew {
                grew = false
                for s in segs where !s.used && columnOf(s.box, gutter) == column && s.box.maxY > rect.minY - 1 && s.box.minY < rect.maxY + 1 {
                    let small = s.text.count <= 12 || s.mathLetters * 2 >= s.letters || isEquationNumber(s.text)
                    if !small || s.box.midY < rect.minY - 1 || s.box.midY > rect.maxY + 1 { continue }
                    s.used = true
                    rect = rect.union(s.box)
                    grew = true
                }
            }
            // A lone symbol inside running text is not a display equation.
            if rect.width < 30 && rect.height < 14 { continue }
            let zone = rect.insetBy(dx: -1, dy: -1)
            for s in segs where !s.used && s.mathLetters * 2 >= s.letters && zone.contains(center(s.box)) { s.used = true }
            for s in mathy where zone.contains(center(s.box)) { s.used = true }
            result.append(rect.insetBy(dx: -1, dy: -2))
        }
        return result
    }

    // MARK: Reading order and paragraphs

    private static func topFirst(_ a: Seg, _ b: Seg) -> Bool {
        a.box.maxY != b.box.maxY ? a.box.maxY > b.box.maxY : a.box.minX < b.box.minX
    }

    /// Top to bottom; between two full-width pieces, the whole left column before the right one.
    private static func readingOrder(_ segs: [Seg]) -> [Seg] {
        var result: [Seg] = []
        var band: [Seg] = []
        func flush() {
            result += band.filter { $0.column == 0 }.sorted(by: topFirst)
            result += band.filter { $0.column == 1 }.sorted(by: topFirst)
            band = []
        }
        for s in segs.sorted(by: topFirst) {
            if s.column < 0 {
                flush()
                result.append(s)
            } else {
                band.append(s)
            }
        }
        flush()
        return result
    }

    /// First halves of words usually written with a hyphen, kept when a line break falls after them.
    private static let compoundHeads: Set<String> = [
        "self", "data", "fine", "coarse", "state", "real", "high", "low", "well", "non", "pre", "post", "co", "cross", "multi", "semi",
        "end", "open", "user", "time", "long", "short", "large", "small", "one", "two", "three", "deep", "top", "first", "second",
        "third", "full", "half", "rule", "learning", "zero", "few", "many", "anti", "inter", "intra", "out", "in", "on", "off",
        "e", "x", "t", "k", "n", "re", "de", "un", "over", "under", "mid", "peer", "ground", "black", "white", "light", "heavy",
    ]

    private static func allBold(_ runs: [DocRun]) -> Bool {
        runs.allSatisfy { $0.bold || $0.text.trimmingCharacters(in: .whitespaces).isEmpty }
    }

    /// Pieces in reading order → paragraphs. A new paragraph starts at a bullet, a column change, a size
    /// change, a clearly bigger gap, an indent change, a short line before, or a switch between bold and
    /// regular. A word broken with a hyphen at the end of a line is joined again.
    private static func paragraphs(_ ordered: [Seg], _ extent: (Int) -> (Double, Double)) -> [Item] {
        var gapByColumn: [Int: Double] = [:]
        var pitchByColumn: [Int: Double] = [:]
        let text = ordered.filter { $0.block == nil }
        for column in Set(text.map(\.column)) {
            let list = text.filter { $0.column == column }
            let limit = list.isEmpty ? 30 : list.map(\.size).reduce(0, +) / Double(list.count) * 2.2
            var gaps: [Double] = [], pitches: [Double] = []
            for (a, b) in zip(list, list.dropFirst()) where abs(a.size - b.size) < 0.6 {
                let gap = Double(a.box.minY - b.box.maxY)
                if gap > -2 { gaps.append(gap) }
                let pitch = a.baseline - b.baseline
                if pitch > 0 && pitch < limit { pitches.append(pitch) }
            }
            gaps.sort()
            pitches.sort()
            gapByColumn[column] = gaps.isEmpty ? 0 : gaps[gaps.count / 2]
            if !pitches.isEmpty { pitchByColumn[column] = pitches[pitches.count / 2] }
        }

        var result: [Item] = []
        var current: Item?
        var previous: Seg?
        for seg in ordered {
            if let block = seg.block {
                result.append(block)
                current = nil
                previous = nil
                continue
            }
            var split = true
            if let prev = previous, let cur = current, !seg.bullet, seg.column == prev.column {
                split = false
                let gap = Double(prev.box.minY - seg.box.maxY)
                let em = max(4, max(seg.size, prev.size))
                let limit = max(em * 0.85, (gapByColumn[seg.column] ?? 0) * 1.6 + 1)
                let (colLeft, colRight) = extent(seg.column)
                let previousShort = Double(prev.box.maxX) < colRight - max((colRight - colLeft) * 0.15, em * 3)
                let boldNow = allBold(seg.runs), boldBefore = allBold(prev.runs)
                if abs(seg.size - prev.size) > max(seg.size, prev.size) * 0.12 { split = true }
                else if gap > limit || gap < -em * 1.5 { split = true }
                else if Double(seg.box.minX - prev.box.minX) > em { split = true }
                else if Double(prev.box.minX - seg.box.minX) > em && cur.lines.count > 1 { split = true }
                else if previousShort { split = true }
                else if boldNow != boldBefore && (boldNow || cur.para.text.count < 60) { split = true }
            }
            if split {
                // The space left above this paragraph, beyond ordinary line spacing, is the previous one's spacing after.
                if let cur = current, let prev = previous, prev.column == seg.column {
                    let gapAbove = Double(prev.box.minY - seg.box.maxY)
                    if gapAbove >= -1 { cur.para.spaceAfter = min(30, max(0, gapAbove - (gapByColumn[seg.column] ?? 0))).rounded() }
                }
                let item = Item()
                item.top = Double(seg.box.maxY)
                item.size = seg.size
                item.column = seg.column
                if let pitch = pitchByColumn[seg.column], pitch > seg.size * 0.9, pitch < seg.size * 1.8 { item.pitch = pitch }
                item.para.bullet = seg.bullet
                for r in seg.runs { append(&item.para.runs, r.text, r) }
                item.lines.append(seg.box)
                item.baselines.append(seg.baseline)
                result.append(item)
                current = item
            } else if let cur = current, let last = cur.para.runs.last {
                let joined = cur.para.text
                let chars = Array(joined)
                // "net-" + "work" → "network", but "data-" + "driven" stays "data-driven".
                if chars.count >= 2, chars[chars.count - 1] == "-", chars[chars.count - 2].isLetter, let next = seg.text.first, next.isLowercase {
                    let head = joined.dropLast().split(whereSeparator: { " ([/—".contains($0) }).last.map { String($0).lowercased() } ?? ""
                    if !compoundHeads.contains(head), last.text.hasSuffix("-") { last.text.removeLast() }
                } else if !joinsTight(joined, seg.text) {
                    append(&cur.para.runs, " ", last)
                }
                for r in seg.runs { append(&cur.para.runs, r.text, r) }
                cur.lines.append(seg.box)
                cur.baselines.append(seg.baseline)
            }
            previous = seg
        }
        return result
    }

    /// Alignment and indents of one item, measured against its column.
    private static func align(_ item: Item, _ left: Double, _ right: Double) {
        let width = max(50, right - left)
        let p = item.para
        if let table = p.table {
            let l = item.lines.map { Double($0.minX) }.min() ?? left, r = item.lines.map { Double($0.maxX) }.max() ?? right
            let tolerance = max(6, width * 0.04)
            if abs((l - left) - (right - r)) < tolerance * 2 && l - left > tolerance * 2 { table.align = .center }
            else if l - left > 8 { table.indent = (l - left).rounded() }
            return
        }
        if let b = item.box, p.image != nil {
            if abs(Double(b.midX) - (left + right) / 2) < width * 0.1 && Double(b.width) < width * 0.92 { p.align = .center }
            else if Double(b.minX) - left > 8 { p.indent = min(Double(b.minX) - left, width * 0.6) }
            return
        }
        guard !item.lines.isEmpty else { return }
        // First-line indent: the opening line starts further right than the rest.
        if item.lines.count >= 2 && Double(item.lines[0].minX - item.lines[1].minX) > max(3, item.size * 0.5) {
            p.firstIndent = Double(item.lines[0].minX - item.lines[1].minX).rounded()
        }
        let tol = max(4, width * 0.03)
        let rest = item.lines.count >= 2 ? Array(item.lines.dropFirst()) : item.lines
        let minLeft = Double(rest.map(\.minX).min()!), maxRight = Double(item.lines.map(\.maxX).max()!)
        let centered = item.lines.allSatisfy { abs((Double($0.minX) - left) - (right - Double($0.maxX))) < tol * 2 } && minLeft - left > tol * 2
        if centered { p.align = .center }
        else if right - maxRight < tol && minLeft - left > width * 0.35 { p.align = .right }
        // Justified: every line but the last runs to the right edge of the column.
        else if item.lines.count >= 3 && item.lines.dropLast().allSatisfy({ right - Double($0.maxX) < max(2.5, item.size * 0.3) }) { p.align = .justify }
        else if minLeft - left > 8 { p.indent = min(minLeft - left, width * 0.6).rounded() }
    }

    // MARK: Whole document

    private static func pageItems(_ page: PDFPage, _ index: Int) -> (items: [Item], gap: Double?) {
        let box = page.bounds(for: .mediaBox)
        let pageWords = words(page, index)
        let gut = gutter(pageWords)
        let segs = segments(pageWords, gut)

        // Tables first (text grids stay editable), then figures with their labels, then display equations.
        var blocks: [Item] = []
        var found = tables(segs)
        func inTable(_ s: Seg) -> Bool { found.contains { ($0.box ?? .null).insetBy(dx: -1, dy: -1).contains(center(s.box)) } }
        func picture(_ ref: CGPDFPage, _ rect: CGRect) -> Bool {
            let r = rect.intersection(box)
            guard !r.isNull, r.width >= 1, r.height >= 1, let image = renderRegion(ref, r) else {
                // Can't draw it: give its text back.
                for s in segs where s.used && s.block == nil && rect.insetBy(dx: -1, dy: -1).contains(center(s.box)) && !inTable(s) { s.used = false }
                return false
            }
            let item = Item()
            item.top = Double(r.maxY)
            item.box = r
            item.para.image = image
            blocks.append(item)
            return true
        }
        if let ref = page.pageRef {
            for rect in figures(shapes(ref), segs, found.compactMap(\.box), gut, box) { _ = picture(ref, rect) }
            for rect in equations(segs, gut) { _ = picture(ref, rect) }
        }
        // A table that turned out to sit inside a figure is part of the picture.
        let pictures = blocks.compactMap(\.box)
        found.removeAll { t in pictures.contains { area($0.intersection(t.box ?? .null)) > area(t.box ?? .null) * 0.6 } }
        blocks += found

        var text = segs.filter { !$0.used }
        for s in text { s.column = columnOf(s.box, gut) }
        for block in blocks {
            block.column = columnOf(block.box ?? .zero, gut)
            let s = Seg()
            s.box = block.box ?? .zero
            s.block = block
            s.column = block.column
            text.append(s)
        }
        let ordered = readingOrder(text)

        // Column extents, for alignment and short-line checks.
        let all = text.filter { $0.block == nil }
        let contentLeft = all.map { Double($0.box.minX) }.min() ?? 0, contentRight = all.map { Double($0.box.maxX) }.max() ?? Double(box.width)
        func extent(_ column: Int) -> (Double, Double) {
            guard column >= 0, let (gl, gr) = gut else { return (contentLeft, contentRight) }
            let inColumn = all.filter { $0.column == column }
            if inColumn.isEmpty { return column == 0 ? (contentLeft, gl) : (gr, contentRight) }
            return (Double(inColumn.map(\.box.minX).min()!), Double(inColumn.map(\.box.maxX).max()!))
        }
        let items = paragraphs(ordered, extent)
        for item in items {
            item.page = index
            item.twoColumns = gut != nil && item.column >= 0
            let (l, r) = extent(item.column)
            align(item, l, r)
        }
        return (items, gut.map { $0.1 - $0.0 })
    }

    static func extract(_ url: URL) throws -> DocModel {
        guard let doc = PDFDocument(url: url) else { throw FWError("打不開這個 PDF") }
        if doc.isLocked { throw FWError("這個 PDF 有密碼保護") }
        var items: [Item] = []
        var pageSizes: [CGSize] = []
        var gaps: [Double] = []
        for index in 0..<doc.pageCount {
            guard let page = doc.page(at: index) else { continue }
            pageSizes.append(page.bounds(for: .mediaBox).size)
            let (pageItems, gap) = pageItems(page, index)
            if let gap { gaps.append(gap) }
            items += pageItems
        }
        return layout(items, pageSizes, gaps)
    }

    /// Headers and footers, headings, page breaks, line spacing and page geometry for the collected items.
    private static func layout(_ input: [Item], _ pageSizes: [CGSize], _ gaps: [Double]) -> DocModel {
        let model = DocModel()
        func bottom(_ i: Item) -> Double { Double(i.box?.minY ?? i.lines.map(\.minY).min() ?? 0) }
        func topOf(_ i: Item) -> Double { Double(i.box?.maxY ?? i.lines.map(\.maxY).max() ?? 0) }
        func trimmed(_ i: Item) -> String { i.para.text.trimmingCharacters(in: .whitespaces) }
        func key(_ i: Item) -> String { trimmed(i).replacingOccurrences(of: #"\d+"#, with: "#", options: .regularExpression) }
        // Running headers and footers (page numbers, a repeated title) are page furniture, not content.
        func inMargin(_ i: Item) -> Bool {
            guard i.para.image == nil, i.para.table == nil, i.page < pageSizes.count, trimmed(i).count <= 80 else { return false }
            let h = Double(pageSizes[i.page].height)
            return bottom(i) > h * 0.93 || topOf(i) < h * 0.07
        }
        var pagesByKey: [String: Set<Int>] = [:]
        for i in input where inMargin(i) { pagesByKey[key(i), default: []].insert(i.page) }
        let needed = max(3, Int((Double(pageSizes.count) * 0.4).rounded(.up)))
        let repeated = Set(pagesByKey.filter { $0.value.count >= needed }.keys)
        let pageNumber = #"^(\d{1,4}|[ivxlcIVXLC]{1,6}|- ?\d{1,4} ?-|\d{1,4} ?/ ?\d{1,4})$"#
        let items = input.filter { i in
            !(inMargin(i) && (repeated.contains(key(i)) || trimmed(i).range(of: pageNumber, options: .regularExpression) != nil))
        }

        var weight: [Double: Int] = [:]
        for item in items where item.size > 0 && item.para.image == nil && item.para.table == nil {
            weight[item.size.rounded(), default: 0] += item.para.text.count
        }
        var bodySize = weight.max { $0.value < $1.value }?.key ?? 12
        if bodySize <= 0 { bodySize = 12 }

        // A new page only where the PDF page ended early (a title page, the end of a chapter); otherwise the
        // text flows on, so a few lines that wrap differently in Word never leave a half-empty page behind.
        var lowest: [Int: Double] = [:]
        for i in items { lowest[i.page] = min(lowest[i.page] ?? .infinity, bottom(i)) }
        var lastPage = -1
        for item in items {
            let p = item.para
            if item.page != lastPage {
                if lastPage >= 0, lastPage < pageSizes.count, let low = lowest[lastPage] { p.pageBreak = low > Double(pageSizes[lastPage].height) * 0.3 }
                lastPage = item.page
            }
            p.columns = item.twoColumns ? 2 : 1
            // Exact line spacing as in the PDF: the paragraph's own line pitch, or the column's for one-liners.
            if item.baselines.count >= 2 {
                var pitches: [Double] = []
                for (a, b) in zip(item.baselines, item.baselines.dropFirst()) where a > b { pitches.append(a - b) }
                pitches.sort()
                if !pitches.isEmpty, pitches[pitches.count / 2] < item.size * 1.8 { p.lineSpacing = (pitches[pitches.count / 2] * 2).rounded() / 2 }
            } else if item.pitch > 0 {
                p.lineSpacing = (item.pitch * 2).rounded() / 2
            }
            if p.image != nil || p.table != nil {
                model.paras.append(p)
                continue
            }
            for r in p.runs { r.text = fixRadicals(r.text) }
            if let first = p.runs.first { first.text = String(first.text.drop(while: { $0 == " " })) }
            if let last = p.runs.last { while last.text.hasSuffix(" ") { last.text.removeLast() } }
            p.runs.removeAll { $0.text.isEmpty }
            let text = p.text
            if text.trimmingCharacters(in: .whitespaces).isEmpty { continue }
            if !p.bullet && text.count <= 120 {
                if item.size >= bodySize * 1.55 { p.heading = 1 }
                else if item.size >= bodySize * 1.25 { p.heading = 2 }
                else if item.size >= bodySize * 1.1 || (allBold(p.runs) && text.count <= 40 && item.lines.count == 1) { p.heading = 3 }
            }
            model.paras.append(p)
        }

        if let first = pageSizes.first {
            let lines = items.flatMap(\.lines)
            let contentLeft = lines.map { Double($0.minX) }.min() ?? 56
            let contentRight = lines.map { Double($0.maxX) }.max() ?? Double(first.width) - 56
            let firstPage = items.first?.page ?? 0
            let firstLines = items.filter { $0.page == firstPage }.flatMap(\.lines)
            let top = firstLines.isEmpty ? 56 : Double(first.height) - Double(firstLines.map(\.maxY).max()!)
            model.pageWidth = (Double(first.width) * 10).rounded() / 10
            model.pageHeight = (Double(first.height) * 10).rounded() / 10
            model.marginLeft = min(108, max(18, contentLeft.rounded()))
            model.marginRight = min(108, max(18, (Double(first.width) - contentRight).rounded()))
            model.marginTop = min(90, max(18, top.rounded()))
            model.marginBottom = min(72, max(18, (model.marginTop * 0.9).rounded()))
            if !gaps.isEmpty { model.columnGap = gaps.sorted()[gaps.count / 2].rounded() }
        }
        return model
    }
}

// MARK: - Writing

enum DocWriters {
    static func write(_ model: DocModel, to url: URL, fmt: String) async throws {
        switch fmt {
        case "txt":
            try model.plainText.write(to: url, atomically: true, encoding: .utf8)
        case "md":
            try markdown(model).write(to: url, atomically: true, encoding: .utf8)
        case "html":
            try html(model).write(to: url, atomically: true, encoding: .utf8)
        case "rtf":
            try rtf(model).write(to: url, atomically: true, encoding: .ascii)
        case "docx":
            try await docx(model, to: url)
        case "odt":
            try await odt(model, to: url)
        default:
            throw FWError("不支援寫入 \(fmt.uppercased())")
        }
    }

    private static func esc(_ s: String) -> String {
        s.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;").replacingOccurrences(of: "\"", with: "&quot;")
    }

    private static func num(_ v: Double) -> String {
        let r = (v * 100).rounded() / 100
        return r == r.rounded() ? String(Int(r)) : String(r)
    }

    private static func twips(_ points: Double) -> Int { Int((points * 20).rounded()) }

    private static func headingSize(_ level: Int) -> Double { level == 1 ? 20 : level == 2 ? 16 : 13.5 }

    private static func contentWidth(_ model: DocModel) -> Double {
        let page = model.pageWidth > 0 ? model.pageWidth : 595
        let left = model.marginLeft > 0 ? model.marginLeft : 57, right = model.marginRight > 0 ? model.marginRight : 57
        return max(100, page - left - right)
    }

    private static func fit(_ model: DocModel, _ image: DocImage) -> (Double, Double) { fit(contentWidth(model), image) }

    private static func fit(_ maxWidth: Double, _ image: DocImage) -> (Double, Double) {
        let k = image.width > maxWidth ? maxWidth / image.width : 1
        return (max(1, image.width * k), max(1, image.height * k))
    }

    private static func ext(_ image: DocImage) -> String { image.format == "png" ? "png" : "jpeg" }

    /// Pixel size from a PNG or JPEG header (RTF wants it); falls back to 1 pixel per point.
    private static func pixelSize(_ image: DocImage) -> (Int, Int) {
        let d = [UInt8](image.data)
        if d.count > 24, d[0] == 0x89, d[1] == 0x50 {
            let w = Int(d[16]) << 24 | Int(d[17]) << 16 | Int(d[18]) << 8 | Int(d[19])
            let h = Int(d[20]) << 24 | Int(d[21]) << 16 | Int(d[22]) << 8 | Int(d[23])
            return (w, h)
        }
        if d.count > 4, d[0] == 0xFF, d[1] == 0xD8 {
            var i = 2
            while i + 9 < d.count {
                if d[i] != 0xFF { i += 1; continue }
                let marker = d[i + 1]
                let length = Int(d[i + 2]) << 8 | Int(d[i + 3])
                if (0xC0...0xCF).contains(marker), ![0xC4, 0xC8, 0xCC].contains(marker) {
                    return (Int(d[i + 7]) << 8 | Int(d[i + 8]), Int(d[i + 5]) << 8 | Int(d[i + 6]))
                }
                i += 2 + length
            }
        }
        return (max(1, Int(image.width)), max(1, Int(image.height)))
    }

    // MARK: Markdown

    static func markdown(_ model: DocModel) -> String {
        var lines: [String] = []
        func gap() { if let last = lines.last, !last.isEmpty { lines.append("") } }
        var last: DocPara?
        for p in model.paras {
            if let table = p.table, !table.rows.isEmpty {
                gap()
                let cols = table.columns
                func cell(_ c: [DocRun]) -> String {
                    DocTable.cellText(c).replacingOccurrences(of: "|", with: "\\|").replacingOccurrences(of: "\n", with: " ")
                }
                for (i, row) in table.rows.enumerated() {
                    lines.append("| " + (0..<cols).map { $0 < row.count ? cell(row[$0]) : "" }.joined(separator: " | ") + " |")
                    if i == 0 { lines.append("|" + String(repeating: " --- |", count: cols)) }
                }
                lines.append("")
                last = nil
                continue
            }
            let plain = p.text.trimmingCharacters(in: .whitespaces)
            if plain.isEmpty {
                gap()
                last = nil
                continue
            }
            if p.heading > 0 {
                gap()
                lines.append(String(repeating: "#", count: p.heading) + " " + plain)
                lines.append("")
                last = p
                continue
            }
            var md = ""
            for r in p.runs {
                let core = r.text.trimmingCharacters(in: .whitespaces)
                if core.isEmpty || (!r.bold && !r.italic) {
                    md += r.text
                    continue
                }
                let mark = r.bold ? "**" : "*"
                let lead = String(r.text.prefix { $0 == " " }), tail = String(r.text.reversed().prefix { $0 == " " })
                md += lead + mark + core + mark + tail
            }
            md = md.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: "\n", with: "  \n")
            if !(p.bullet && last?.bullet == true) { gap() }
            lines.append(p.bullet ? "- " + md : md)
            last = p
        }
        while lines.last == "" { lines.removeLast() }
        return lines.joined(separator: "\n") + "\n"
    }

    // MARK: HTML

    private static func inlineHTML(_ runs: [DocRun]) -> String {
        var out = ""
        for r in runs {
            var t = esc(r.text).replacingOccurrences(of: "\n", with: "<br>")
            var style: [String] = []
            if r.size > 0 { style.append("font-size:\(num(r.size))pt") }
            if let c = r.color { style.append("color:#\(c)") }
            if let f = r.font { style.append("font-family:'\(esc(f))'") }
            if !style.isEmpty { t = "<span style=\"\(style.joined(separator: ";"))\">\(t)</span>" }
            if r.bold { t = "<b>\(t)</b>" }
            if r.italic { t = "<i>\(t)</i>" }
            out += t
        }
        return out
    }

    private static func blockStyle(_ p: DocPara) -> String {
        var style: [String] = []
        if p.align == .center { style.append("text-align:center") } else if p.align == .right { style.append("text-align:right") }
        else if p.align == .justify { style.append("text-align:justify") }
        if p.indent > 1 { style.append("margin-left:\(num(p.indent))pt") }
        if p.firstIndent > 1 { style.append("text-indent:\(num(p.firstIndent))pt") }
        if p.spaceAfter >= 0 { style.append("margin-bottom:\(num(p.spaceAfter))pt") }
        if p.lineSpacing > 0 { style.append("line-height:\(num(p.lineSpacing))pt") }
        return style.isEmpty ? "" : " style=\"\(style.joined(separator: ";"))\""
    }

    static func html(_ model: DocModel) -> String {
        var html: [String] = []
        var inList = false
        func closeList() { if inList { html.append("</ul>"); inList = false } }
        for p in model.paras {
            if p.pageBreak && !html.isEmpty {
                closeList()
                html.append("<hr class=\"page\">")
            }
            if let table = p.table {
                closeList()
                let open = table.align == .center ? "<table style=\"margin-left:auto;margin-right:auto\">"
                    : table.indent > 1 ? "<table style=\"margin-left:\(num(table.indent))pt\">" : "<table>"
                var t = open
                for row in table.rows { t += "<tr>" + row.map { "<td>\(inlineHTML($0))</td>" }.joined() + "</tr>" }
                html.append(t + "</table>")
                continue
            }
            if let image = p.image {
                closeList()
                let (w, h) = fit(model, image)
                html.append("<p\(blockStyle(p))><img src=\"data:image/\(image.format);base64,\(image.data.base64EncodedString())\" style=\"width:\(num(w))pt;height:\(num(h))pt\" alt=\"\"></p>")
                continue
            }
            if p.text.trimmingCharacters(in: .whitespaces).isEmpty { closeList(); continue }
            if p.heading > 0 {
                closeList()
                html.append("<h\(p.heading)\(blockStyle(p))>\(inlineHTML(p.runs))</h\(p.heading)>")
            } else if p.bullet {
                if !inList { html.append("<ul>"); inList = true }
                html.append("<li\(blockStyle(p))>\(inlineHTML(p.runs))</li>")
            } else {
                closeList()
                html.append("<p\(blockStyle(p))>\(inlineHTML(p.runs))</p>")
            }
        }
        closeList()
        let width = model.pageWidth > 0 ? "\(num(contentWidth(model)))pt" : "760px"
        let style = "body{font-family:-apple-system,'PingFang TC',sans-serif;font-size:15px;line-height:1.6;color:#111;max-width:\(width);margin:40px auto;padding:0 16px}"
            + "h1,h2,h3{margin:18px 0 8px}p{margin:0 0 10px}img{max-width:100%}"
            + "hr.page{border:0;border-top:1px dashed #bbb;margin:28px 0;page-break-after:always}"
            + "table{border-collapse:collapse;margin:0 0 12px}td{border:1px solid #999;padding:4px 8px;vertical-align:top}"
        return "<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><style>\(style)</style></head><body>\n" + html.joined(separator: "\n") + "\n</body></html>\n"
    }

    // MARK: RTF

    private static func rtfEscape(_ s: String) -> String {
        var out = ""
        for unit in s.utf16 {
            switch unit {
            case 0x5C: out += "\\\\"
            case 0x7B: out += "\\{"
            case 0x7D: out += "\\}"
            case 0x0A: out += "\\line "
            case 0x09: out += "\\tab "
            case 0..<0x80: out += String(UnicodeScalar(UInt8(unit)))
            default: out += "\\u\(Int16(bitPattern: unit))?"
            }
        }
        return out
    }

    private static func hex(_ data: Data) -> String {
        var out = ""
        out.reserveCapacity(data.count * 2 + data.count / 64)
        for (i, byte) in data.enumerated() {
            out += String(format: "%02x", byte)
            if i % 64 == 63 { out += "\n" }
        }
        return out
    }

    static func rtf(_ model: DocModel) -> String {
        var colors: [String] = []
        for p in model.paras {
            let runs = p.runs + (p.table?.rows.flatMap { $0.flatMap { $0 } } ?? [])
            for r in runs { if let c = r.color, !colors.contains(c) { colors.append(c) } }
        }
        var sb = "{\\rtf1\\ansi\\ansicpg1252\\deff0\\uc1{\\fonttbl{\\f0\\fnil\\fcharset136 PingFang TC;}}\n{\\colortbl;"
        for c in colors {
            let v = Int(c, radix: 16) ?? 0
            sb += "\\red\((v >> 16) & 255)\\green\((v >> 8) & 255)\\blue\(v & 255);"
        }
        sb += "}\n"
        if model.pageWidth > 0 {
            sb += "\\paperw\(twips(model.pageWidth))\\paperh\(twips(model.pageHeight))\\margl\(twips(model.marginLeft))"
                + "\\margr\(twips(model.marginRight))\\margt\(twips(model.marginTop))\\margb\(twips(model.marginBottom))\n"
        }
        func runs(_ list: [DocRun], _ heading: Int) {
            for r in list {
                let size = r.size > 0 ? r.size : heading > 0 ? headingSize(heading) : 11
                sb += "{"
                if r.bold || (heading > 0 && r.size <= 0) { sb += "\\b" }
                if r.italic { sb += "\\i" }
                sb += "\\fs\(Int((size * 2).rounded()))"
                if let c = r.color, let index = colors.firstIndex(of: c) { sb += "\\cf\(index + 1)" }
                sb += " " + rtfEscape(r.text) + "}"
            }
        }
        for p in model.paras {
            if let table = p.table {
                if p.pageBreak { sb += "\\page\n" }
                let cols = table.columns
                for row in table.rows {
                    sb += "\\trowd\\trgaph80" + (table.align == .center ? "\\trqc" : table.align == .right ? "\\trqr" : "")
                    if table.align == .left && table.indent > 1 { sb += "\\trleft\(twips(table.indent))" }
                    var x = 0
                    for k in 0..<cols {
                        x += twips(table.width(k))
                        sb += "\\clbrdrt\\brdrs\\brdrw10\\clbrdrl\\brdrs\\brdrw10\\clbrdrb\\brdrs\\brdrw10\\clbrdrr\\brdrs\\brdrw10\\cellx\(x)"
                    }
                    sb += "\n"
                    for k in 0..<cols {
                        sb += "\\pard\\intbl "
                        if k < row.count { runs(row[k], 0) }
                        sb += "\\cell\n"
                    }
                    sb += "\\row\n"
                }
                sb += "\\pard\\par\n"
                continue
            }
            sb += "\\pard\\sa120"
            if p.pageBreak { sb += "\\pagebb" }
            if p.align == .center { sb += "\\qc" } else if p.align == .right { sb += "\\qr" } else if p.align == .justify { sb += "\\qj" }
            if p.indent > 1 { sb += "\\li\(twips(p.indent))" }
            if p.firstIndent > 1 { sb += "\\fi\(twips(p.firstIndent))" }
            if p.lineSpacing > 0 { sb += "\\sl-\(twips(p.lineSpacing))\\slmult0" }
            sb += " "
            if let image = p.image {
                let (w, h) = fit(model, image)
                let (pw, ph) = pixelSize(image)
                sb += "{\\pict" + (image.format == "png" ? "\\pngblip" : "\\jpegblip") + "\\picw\(pw)\\pich\(ph)\\picwgoal\(twips(w))\\pichgoal\(twips(h))\n"
                sb += hex(image.data) + "}\\par\n"
                continue
            }
            if p.bullet { sb += "\\u8226? " }
            runs(p.runs, p.heading)
            sb += "\\par\n"
        }
        return sb + "}"
    }

    // MARK: Zip packages

    /// Writes the files into a temporary folder and zips them with the system's zip tool.
    private static func package(_ files: [(String, Data)], to url: URL, mimetype: String? = nil) async throws {
        let tmp = try Out.tempDir()
        defer { try? FileManager.default.removeItem(at: tmp) }
        for (name, data) in files {
            let target = tmp.appendingPathComponent(name)
            try FileManager.default.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
            try data.write(to: target)
        }
        let zipped = tmp.appendingPathComponent("out.zip")
        if let mimetype {
            // ODF: the mimetype entry must come first and uncompressed.
            try Data(mimetype.utf8).write(to: tmp.appendingPathComponent("mimetype"))
            try await Shell.run("/usr/bin/zip", ["-X", "-0", "-q", zipped.path, "mimetype"], cwd: tmp)
        }
        try await Shell.run("/usr/bin/zip", ["-X", "-r", "-q", "-nw", zipped.path] + files.map(\.0), cwd: tmp)
        try FileManager.default.moveItem(at: zipped, to: url)
    }

    // MARK: DOCX

    static func docx(_ model: DocModel, to url: URL) async throws {
        var body = ""
        var media: [(String, DocImage)] = []
        let pageW = twips(model.pageWidth > 0 ? model.pageWidth : 595.3), pageH = twips(model.pageHeight > 0 ? model.pageHeight : 841.9)
        let ml = twips(model.marginLeft > 0 ? model.marginLeft : 56.7), mr = twips(model.marginRight > 0 ? model.marginRight : 56.7)
        let mt = twips(model.marginTop > 0 ? model.marginTop : 56.7), mb = twips(model.marginBottom > 0 ? model.marginBottom : 56.7)
        let gap = model.columnGap > 0 ? model.columnGap : 18
        // Two-column parts of the page become two-column sections, joined by continuous section breaks.
        var sectionColumns = model.paras.first?.columns ?? 1
        var sections = 0
        func sectPr(_ columns: Int) -> String {
            "<w:sectPr>" + (sections > 0 ? "<w:type w:val=\"continuous\"/>" : "") + "<w:pgSz w:w=\"\(pageW)\" w:h=\"\(pageH)\"/>"
                + "<w:pgMar w:top=\"\(mt)\" w:right=\"\(mr)\" w:bottom=\"\(mb)\" w:left=\"\(ml)\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/>"
                + (columns == 2 ? "<w:cols w:num=\"2\" w:space=\"\(twips(gap))\"/>" : "<w:cols w:space=\"708\"/>") + "</w:sectPr>"
        }
        func maxWidth(_ p: DocPara) -> Double { p.columns == 2 ? (contentWidth(model) - gap) / 2 : contentWidth(model) }
        func docxRuns(_ runs: [DocRun], _ heading: Int) {
            for r in runs {
                for (k, piece) in r.text.components(separatedBy: "\n").enumerated() {
                    body += "<w:r>"
                    if r.bold || r.italic || r.color != nil || r.size > 0 || r.font != nil {
                        body += "<w:rPr>"
                        if let f = r.font { body += "<w:rFonts w:ascii=\"\(esc(f))\" w:hAnsi=\"\(esc(f))\" w:cs=\"\(esc(f))\"/>" }
                        if r.bold { body += "<w:b/><w:bCs/>" } else if heading > 0 && r.size > 0 { body += "<w:b w:val=\"0\"/><w:bCs w:val=\"0\"/>" }
                        if r.italic { body += "<w:i/><w:iCs/>" }
                        if let c = r.color { body += "<w:color w:val=\"\(c)\"/>" }
                        if r.size > 0 {
                            let half = Int((r.size * 2).rounded())
                            body += "<w:sz w:val=\"\(half)\"/><w:szCs w:val=\"\(half)\"/>"
                        }
                        body += "</w:rPr>"
                    }
                    if k > 0 { body += "<w:br/>" }
                    body += "<w:t xml:space=\"preserve\">\(esc(piece))</w:t></w:r>"
                }
            }
        }
        for p in model.paras {
            if p.columns != sectionColumns {
                body += "<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"20\" w:lineRule=\"exact\"/>" + sectPr(sectionColumns) + "</w:pPr></w:p>"
                sections += 1
                sectionColumns = p.columns
            }
            if let table = p.table {
                if p.pageBreak { body += "<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>" }
                let cols = table.columns
                let border = "w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"808080\""
                body += "<w:tbl><w:tblPr><w:tblW w:w=\"0\" w:type=\"auto\"/>"
                if table.align == .center { body += "<w:jc w:val=\"center\"/>" }
                else if table.align == .right { body += "<w:jc w:val=\"right\"/>" }
                else if table.indent > 1 { body += "<w:tblInd w:w=\"\(twips(table.indent))\" w:type=\"dxa\"/>" }
                body += "<w:tblBorders><w:top \(border)/><w:left \(border)/><w:bottom \(border)/><w:right \(border)/>"
                    + "<w:insideH \(border)/><w:insideV \(border)/></w:tblBorders><w:tblLayout w:type=\"fixed\"/></w:tblPr><w:tblGrid>"
                let total = (0..<cols).map { table.width($0) }.reduce(0, +)
                let shrink = total > maxWidth(p) ? maxWidth(p) / total : 1
                for k in 0..<cols { body += "<w:gridCol w:w=\"\(twips(table.width(k) * shrink))\"/>" }
                body += "</w:tblGrid>"
                for row in table.rows {
                    body += "<w:tr>"
                    for k in 0..<cols {
                        body += "<w:tc><w:tcPr><w:tcW w:w=\"\(twips(table.width(k) * shrink))\" w:type=\"dxa\"/></w:tcPr><w:p><w:pPr><w:spacing w:after=\"0\"/></w:pPr>"
                        if k < row.count { docxRuns(row[k], 0) }
                        body += "</w:p></w:tc>"
                    }
                    body += "</w:tr>"
                }
                // Word wants a paragraph between a table and whatever follows.
                body += "</w:tbl><w:p/>"
                continue
            }
            body += "<w:p><w:pPr>"
            if p.heading > 0 { body += "<w:pStyle w:val=\"Heading\(p.heading)\"/>" }
            if p.pageBreak { body += "<w:pageBreakBefore/>" }
            if p.spaceAfter >= 0 || p.lineSpacing > 0 {
                body += "<w:spacing"
                if p.spaceAfter >= 0 { body += " w:after=\"\(twips(p.spaceAfter))\"" }
                if p.lineSpacing > 0 { body += " w:line=\"\(twips(p.lineSpacing))\" w:lineRule=\"exact\"" }
                body += "/>"
            }
            if p.indent > 1 || p.firstIndent > 1 {
                body += "<w:ind"
                if p.indent > 1 { body += " w:left=\"\(twips(p.indent))\"" }
                if p.firstIndent > 1 { body += " w:firstLine=\"\(twips(p.firstIndent))\"" }
                body += "/>"
            }
            if p.align == .center { body += "<w:jc w:val=\"center\"/>" } else if p.align == .right { body += "<w:jc w:val=\"right\"/>" }
            else if p.align == .justify { body += "<w:jc w:val=\"both\"/>" }
            body += "</w:pPr>"
            if let image = p.image {
                let n = media.count + 1
                let name = "image\(n).\(ext(image))"
                media.append((name, image))
                let (w, h) = fit(maxWidth(p), image)
                let cx = Int((w * 12700).rounded()), cy = Int((h * 12700).rounded())
                body += "<w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"\(cx)\" cy=\"\(cy)\"/>"
                    + "<wp:docPr id=\"\(n)\" name=\"Picture \(n)\"/><wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect=\"1\"/></wp:cNvGraphicFramePr>"
                    + "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>"
                    + "<pic:nvPicPr><pic:cNvPr id=\"\(n)\" name=\"\(name)\"/><pic:cNvPicPr/></pic:nvPicPr>"
                    + "<pic:blipFill><a:blip r:embed=\"rIdImg\(n)\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>"
                    + "<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"\(cx)\" cy=\"\(cy)\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>"
                    + "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>"
                continue
            }
            var runs = p.runs
            if p.bullet { runs.insert(DocRun("• ", size: p.runs.first?.size ?? 0), at: 0) }
            docxRuns(runs, p.heading)
            body += "</w:p>"
        }

        let document = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" "
            + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" "
            + "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" "
            + "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" "
            + "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><w:body>" + body
            + sectPr(sectionColumns) + "</w:body></w:document>"
        // Text taken from a PDF already carries its own sizes and spacing.
        let fromPdf = model.pageWidth > 0
        func headingStyle(_ level: Int, _ halfPoints: Int) -> String {
            "<w:style w:type=\"paragraph\" w:styleId=\"Heading\(level)\"><w:name w:val=\"heading \(level)\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/>"
                + "<w:pPr><w:keepNext/><w:spacing w:before=\"120\" w:after=\"120\"/><w:outlineLvl w:val=\"\(level - 1)\"/></w:pPr>"
                + "<w:rPr><w:b/><w:bCs/><w:sz w:val=\"\(halfPoints)\"/><w:szCs w:val=\"\(halfPoints)\"/></w:rPr></w:style>"
        }
        let styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
            + "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Helvetica Neue\" w:hAnsi=\"Helvetica Neue\" w:eastAsia=\"PingFang TC\" w:cs=\"Helvetica Neue\"/>"
            + "<w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"en-US\" w:eastAsia=\"zh-TW\"/></w:rPr></w:rPrDefault>"
            + "<w:pPrDefault><w:pPr><w:spacing w:after=\"100\" w:line=\"\(fromPdf ? 240 : 252)\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>"
            + "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>"
            + headingStyle(1, 40) + headingStyle(2, 32) + headingStyle(3, 27) + "</w:styles>"
        let contentTypes = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
            + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
            + "<Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>"
            + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>"
            + "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>"
            + "<Override PartName=\"/word/settings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml\"/></Types>"
        // Text from a PDF was set with hyphenation; letting Word hyphenate keeps the lines (and pages) close to the original.
        let settings = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<w:settings xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" + (fromPdf ? "<w:autoHyphenation/>" : "")
            + "<w:defaultTabStop w:val=\"720\"/><w:compat><w:compatSetting w:name=\"compatibilityMode\" w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\"/></w:compat></w:settings>"
        let rels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>"
        var documentRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
            + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings\" Target=\"settings.xml\"/>"
        for (i, item) in media.enumerated() {
            documentRels += "<Relationship Id=\"rIdImg\(i + 1)\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/\(item.0)\"/>"
        }
        documentRels += "</Relationships>"
        var files: [(String, Data)] = [
            ("[Content_Types].xml", Data(contentTypes.utf8)), ("_rels/.rels", Data(rels.utf8)),
            ("word/document.xml", Data(document.utf8)), ("word/styles.xml", Data(styles.utf8)), ("word/settings.xml", Data(settings.utf8)),
            ("word/_rels/document.xml.rels", Data(documentRels.utf8)),
        ]
        for (name, image) in media { files.append(("word/media/" + name, image.data)) }
        try await package(files, to: url)
    }

    // MARK: ODT

    static func odt(_ model: DocModel, to url: URL) async throws {
        var paraStyles: [String: String] = [:]
        var textStyles: [String: String] = [:]
        var styleXML = "<style:style style:name=\"Cell\" style:family=\"table-cell\"><style:table-cell-properties fo:padding=\"0.08cm\" fo:border=\"0.5pt solid #808080\"/></style:style>"
        var media: [(String, DocImage)] = []

        func paraStyle(_ p: DocPara) -> String {
            let explicit = p.runs.contains { $0.size > 0 }
            let key = "\(p.heading)|\(explicit)|\(p.align)|\(num(p.indent))|\(p.pageBreak)|\(num(p.firstIndent))|\(num(p.spaceAfter))|\(num(p.lineSpacing))"
            if let name = paraStyles[key] { return name }
            let name = "P\(paraStyles.count + 1)"
            paraStyles[key] = name
            styleXML += "<style:style style:name=\"\(name)\" style:family=\"paragraph\"><style:paragraph-properties fo:margin-bottom=\"\(p.spaceAfter >= 0 ? num(p.spaceAfter) + "pt" : "0.1cm")\""
            if p.firstIndent > 1 { styleXML += " fo:text-indent=\"\(num(p.firstIndent))pt\"" }
            if p.lineSpacing > 0 { styleXML += " fo:line-height=\"\(num(p.lineSpacing))pt\"" }
            if p.align != .left { styleXML += " fo:text-align=\"\(p.align == .center ? "center" : p.align == .justify ? "justify" : "end")\"" }
            if p.indent > 1 { styleXML += " fo:margin-left=\"\(num(p.indent))pt\"" }
            if p.pageBreak { styleXML += " fo:break-before=\"page\"" }
            styleXML += "/>"
            // Text taken from a PDF carries its own sizes and weights; only fall back to the heading look without them.
            if p.heading > 0 && !explicit {
                let size = num(headingSize(p.heading)) + "pt"
                styleXML += "<style:text-properties fo:font-size=\"\(size)\" fo:font-weight=\"bold\" style:font-size-asian=\"\(size)\" style:font-weight-asian=\"bold\"/>"
            }
            styleXML += "</style:style>"
            return name
        }

        func textStyle(_ r: DocRun) -> String? {
            guard r.bold || r.italic || r.size > 0 || r.color != nil || r.font != nil else { return nil }
            let key = "\(r.bold)|\(r.italic)|\(num(r.size))|\(r.color ?? "")|\(r.font ?? "")"
            if let name = textStyles[key] { return name }
            let name = "T\(textStyles.count + 1)"
            textStyles[key] = name
            styleXML += "<style:style style:name=\"\(name)\" style:family=\"text\"><style:text-properties"
            if r.bold { styleXML += " fo:font-weight=\"bold\" style:font-weight-asian=\"bold\"" }
            if r.italic { styleXML += " fo:font-style=\"italic\" style:font-style-asian=\"italic\"" }
            if r.size > 0 { styleXML += " fo:font-size=\"\(num(r.size))pt\" style:font-size-asian=\"\(num(r.size))pt\"" }
            if let c = r.color { styleXML += " fo:color=\"#\(c)\"" }
            if let f = r.font { styleXML += " fo:font-family=\"\(esc(f))\"" }
            styleXML += "/></style:style>"
            return name
        }

        func inline(_ runs: [DocRun]) -> String {
            var out = ""
            for r in runs {
                let t = esc(r.text).replacingOccurrences(of: "\n", with: "<text:line-break/>").replacingOccurrences(of: "\t", with: "<text:tab/>")
                if let style = textStyle(r) { out += "<text:span text:style-name=\"\(style)\">\(t)</text:span>" } else { out += t }
            }
            return out
        }

        var body = ""
        var inList = false
        var tableCount = 0
        for p in model.paras {
            if !p.bullet && inList {
                body += "</text:list>"
                inList = false
            }
            if let table = p.table {
                let cols = table.columns
                tableCount += 1
                let name = "Table\(tableCount)"
                let total = (0..<cols).map { table.width($0) }.reduce(0, +)
                let align = table.align == .center ? "center" : table.align == .right ? "right" : "left"
                styleXML += "<style:style style:name=\"\(name)\" style:family=\"table\"><style:table-properties style:width=\"\(num(total))pt\" table:align=\"\(align)\""
                    + (table.align == .left && table.indent > 1 ? " fo:margin-left=\"\(num(table.indent))pt\"" : "")
                    + (p.pageBreak ? " fo:break-before=\"page\"" : "") + "/></style:style>"
                body += "<table:table table:name=\"\(name)\" table:style-name=\"\(name)\">"
                for k in 0..<cols {
                    let column = "\(name).\(k + 1)"
                    styleXML += "<style:style style:name=\"\(column)\" style:family=\"table-column\"><style:table-column-properties style:column-width=\"\(num(table.width(k)))pt\"/></style:style>"
                    body += "<table:table-column table:style-name=\"\(column)\"/>"
                }
                for row in table.rows {
                    body += "<table:table-row>"
                    for k in 0..<cols {
                        body += "<table:table-cell table:style-name=\"Cell\" office:value-type=\"string\"><text:p>" + (k < row.count ? inline(row[k]) : "") + "</text:p></table:table-cell>"
                    }
                    body += "</table:table-row>"
                }
                body += "</table:table>"
                continue
            }
            let style = paraStyle(p)
            if let image = p.image {
                let name = "image\(media.count + 1).\(ext(image))"
                media.append((name, image))
                let (w, h) = fit(model, image)
                body += "<text:p text:style-name=\"\(style)\"><draw:frame draw:name=\"\(name)\" text:anchor-type=\"as-char\" svg:width=\"\(num(w))pt\" svg:height=\"\(num(h))pt\">"
                    + "<draw:image xlink:href=\"Pictures/\(name)\" xlink:type=\"simple\" xlink:show=\"embed\" xlink:actuate=\"onLoad\"/></draw:frame></text:p>"
                continue
            }
            if p.heading > 0 {
                body += "<text:h text:style-name=\"\(style)\" text:outline-level=\"\(p.heading)\">\(inline(p.runs))</text:h>"
            } else if p.bullet {
                if !inList { body += "<text:list>"; inList = true }
                body += "<text:list-item><text:p text:style-name=\"\(style)\">\(inline(p.runs))</text:p></text:list-item>"
            } else {
                body += "<text:p text:style-name=\"\(style)\">\(inline(p.runs))</text:p>"
            }
        }
        if inList { body += "</text:list>" }

        let ns = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" "
            + "xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" "
            + "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" "
            + "xmlns:xlink=\"http://www.w3.org/1999/xlink\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" office:version=\"1.2\""
        let content = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-content \(ns)><office:automatic-styles>\(styleXML)</office:automatic-styles>"
            + "<office:body><office:text>\(body)</office:text></office:body></office:document-content>"
        var files: [(String, Data)] = [("content.xml", Data(content.utf8))]
        var manifest = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\">"
            + "<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"1.2\" manifest:media-type=\"application/vnd.oasis.opendocument.text\"/>"
            + "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>"
        if model.pageWidth > 0 {
            let styles = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-styles \(ns)><office:automatic-styles>"
                + "<style:page-layout style:name=\"PL\"><style:page-layout-properties fo:page-width=\"\(num(model.pageWidth))pt\" fo:page-height=\"\(num(model.pageHeight))pt\" "
                + "fo:margin-left=\"\(num(model.marginLeft))pt\" fo:margin-right=\"\(num(model.marginRight))pt\" fo:margin-top=\"\(num(model.marginTop))pt\" fo:margin-bottom=\"\(num(model.marginBottom))pt\"/></style:page-layout>"
                + "</office:automatic-styles><office:master-styles><style:master-page style:name=\"Standard\" style:page-layout-name=\"PL\"/></office:master-styles></office:document-styles>"
            files.append(("styles.xml", Data(styles.utf8)))
            manifest += "<manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>"
        }
        for (name, image) in media {
            files.append(("Pictures/" + name, image.data))
            manifest += "<manifest:file-entry manifest:full-path=\"Pictures/\(name)\" manifest:media-type=\"image/\(ext(image))\"/>"
        }
        manifest += "</manifest:manifest>"
        files.append(("META-INF/manifest.xml", Data(manifest.utf8)))
        try await package(files, to: url, mimetype: "application/vnd.oasis.opendocument.text")
    }
}
