import AppKit
import PDFKit

// PDF → Word / ODT / RTF / HTML / Markdown that keeps the layout: run formatting (bold, italic, size, colour),
// alignment and indents, pictures where they sit, simple tables, and one PDF page per document page.

// MARK: - Document model

final class DocRun {
    var text: String
    var bold: Bool
    var italic: Bool
    /// Font size in points; 0 keeps the document default.
    var size: Double
    /// Text colour as "RRGGBB"; nil keeps the default (black).
    var color: String?

    init(_ text: String, bold: Bool = false, italic: Bool = false, size: Double = 0, color: String? = nil) {
        self.text = text
        self.bold = bold
        self.italic = italic
        self.size = size
        self.color = color
    }

    func sameStyle(_ o: DocRun) -> Bool {
        bold == o.bold && italic == o.italic && abs(size - o.size) < 0.01 && color == o.color
    }

    func copy(text: String? = nil) -> DocRun {
        DocRun(text ?? self.text, bold: bold, italic: italic, size: size, color: color)
    }
}

enum DocAlign {
    case left, center, right
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
    /// Starts on a new page (one PDF page → one page of the document).
    var pageBreak = false
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

    var plainText: String {
        paras.filter { $0.image == nil }.map { $0.table?.plainText ?? $0.text }.joined(separator: "\n")
    }
}

// MARK: - Reading the PDF

enum PDFLayout {
    private final class LineInfo {
        var runs: [DocRun] = []
        var text = ""
        var size = 0.0
        var bullet = false
        var inTable = false
        var box = CGRect.zero
    }

    /// One paragraph, picture or table on a page, with where it sits (PDF coordinates: y grows upwards).
    private final class Item {
        let para = DocPara()
        var top = 0.0
        var size = 0.0
        var lines: [CGRect] = []
        var imageBox: CGRect?
        var page = 0
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

    private static func nameHas(_ name: String, _ parts: [String]) -> Bool {
        parts.contains { name.range(of: $0, options: .caseInsensitive) != nil }
    }

    /// The look of one character: weight, slant, size (to half a point) and colour.
    private static func style(_ attributes: [NSAttributedString.Key: Any]) -> DocRun {
        var bold = false, italic = false, size = 0.0
        if let font = attributes[.font] as? NSFont {
            let traits = font.fontDescriptor.symbolicTraits
            bold = traits.contains(.bold) || nameHas(font.fontName, ["Bold", "Black", "Heavy", "Semibold", "Demi"])
            italic = traits.contains(.italic) || nameHas(font.fontName, ["Italic", "Oblique"])
            size = (Double(font.pointSize) * 2).rounded() / 2
        }
        var color: String?
        if let c = (attributes[.foregroundColor] as? NSColor)?.usingColorSpace(.sRGB) {
            let r = Double(c.redComponent), g = Double(c.greenComponent), b = Double(c.blueComponent)
            if max(r, g, b) > 0.18 {
                color = String(format: "%02X%02X%02X", Int((r * 255).rounded()), Int((g * 255).rounded()), Int((b * 255).rounded()))
            }
        }
        return DocRun("", bold: bold, italic: italic, size: size, color: color)
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

    /// The text lines of a page. Each PDFKit line is split further wherever a wide gap separates its
    /// characters, so table cells and side-by-side columns come out as separate pieces.
    private static func pageLines(_ page: PDFPage, marks: inout [CGRect]) -> [LineInfo] {
        guard let attributed = page.attributedString, attributed.length > 0, let plain = page.string,
              let selection = page.selection(for: page.bounds(for: .mediaBox)) else { return [] }
        // Character positions and selection ranges count characters of page.string; the attributed string
        // can differ in its line breaks, so its attributes are looked up through an alignment of the two.
        let string = plain as NSString
        let styled = attributed.string as NSString
        var styleIndex = [Int](repeating: -1, count: string.length)
        do {
            var i = 0, j = 0
            let space = CharacterSet.whitespacesAndNewlines
            func blank(_ unit: unichar) -> Bool { Unicode.Scalar(unit).map { space.contains($0) } ?? false }
            while i < string.length && j < styled.length {
                let a = string.character(at: i), b = styled.character(at: j)
                if a == b {
                    styleIndex[i] = j
                    i += 1
                    j += 1
                } else if blank(b) {
                    j += 1
                } else if blank(a) {
                    i += 1
                } else {
                    styleIndex[i] = j
                    i += 1
                    j += 1
                }
            }
        }
        var result: [LineInfo] = []
        var glyphMarks: [CGRect] = []
        // AGENTWHEEL_PDF_DEBUG=<file> dumps what PDFKit reports for the first lines (used by the CI smoke test).
        let debugPath = ProcessInfo.processInfo.environment["AGENTWHEEL_PDF_DEBUG"]
        var debug = "string \(string.length) attributed \(styled.length) same=\(plain == attributed.string)\n"
        var debugLines = 0
        defer {
            if let debugPath, let data = debug.data(using: .utf8) {
                if let handle = FileHandle(forWritingAtPath: debugPath) {
                    handle.seekToEndOfFile()
                    handle.write(data)
                    handle.closeFile()
                } else {
                    FileManager.default.createFile(atPath: debugPath, contents: data)
                }
            }
        }
        defer { marks += glyphMarks }
        // PDFKit's characterBounds(at:) may count only real glyphs, leaving out the line breaks it adds to
        // page.string, so indices drift by one per line. Measure both ways on the first lines and keep the
        // mapping whose boxes actually land on their own line.
        var breaksBefore = [Int](repeating: 0, count: string.length + 1)
        for i in 0..<string.length {
            let unit = string.character(at: i)
            breaksBefore[i + 1] = breaksBefore[i] + (unit == 0x0A || unit == 0x0D || unit == 0x2028 ? 1 : 0)
        }
        let lineSelections = selection.selectionsByLine()
        var hitsRaw = 0, hitsShifted = 0
        for line in lineSelections.prefix(12) {
            let bounds = line.bounds(for: page).insetBy(dx: -2, dy: -2)
            for k in 0..<line.numberOfTextRanges(on: page) {
                let range = line.range(at: k, on: page)
                guard range.location != NSNotFound else { continue }
                for i in range.location..<min(range.location + range.length, string.length) {
                    let unit = string.character(at: i)
                    if unit == 0x0A || unit == 0x0D || unit == 0x20 { continue }
                    if bounds.contains(CGPoint(x: page.characterBounds(at: i).midX, y: page.characterBounds(at: i).midY)) { hitsRaw += 1 }
                    let j = i - breaksBefore[i]
                    if j >= 0, bounds.contains(CGPoint(x: page.characterBounds(at: j).midX, y: page.characterBounds(at: j).midY)) { hitsShifted += 1 }
                }
            }
        }
        let shifted = hitsShifted > hitsRaw
        debug += "index mapping: \(shifted ? "skip line breaks" : "direct") (direct \(hitsRaw), skipping \(hitsShifted))\n"
        func characterRect(_ i: Int) -> CGRect { page.characterBounds(at: shifted ? max(0, i - breaksBefore[i]) : i) }

        for line in lineSelections {
            var indices: [Int] = []
            for k in 0..<line.numberOfTextRanges(on: page) {
                let range = line.range(at: k, on: page)
                guard range.location != NSNotFound else { continue }
                for i in range.location..<min(range.location + range.length, string.length) { indices.append(i) }
            }
            if debugPath != nil && debugLines < 4 {
                debugLines += 1
                var ranges: [String] = []
                for k in 0..<line.numberOfTextRanges(on: page) {
                    let r = line.range(at: k, on: page)
                    ranges.append("\(r.location)+\(r.length)")
                }
                let b = line.bounds(for: page)
                debug += "LINE '\(line.string ?? "")' ranges \(ranges) bounds \(Int(b.minX)),\(Int(b.minY)) \(Int(b.width))x\(Int(b.height))\n"
                for i in indices.prefix(60) {
                    let r = characterRect(i)
                    debug += "  \(i) '\(string.substring(with: NSRange(location: i, length: 1)))' \(Int(r.minX)),\(Int(r.minY)) \(Int(r.width))x\(Int(r.height))\n"
                }
            }
            var segment: LineInfo?
            var segmentText = ""
            var sizes: [Double] = []
            var lastRect = CGRect.null
            var pendingSpace = false

            func finish() {
                guard let s = segment else { return }
                var bullet = false
                let rest = stripBullet(segmentText, &bullet)
                if bullet { s.runs = removePrefix(s.runs, segmentText.count - rest.count) }
                if rest.trimmingCharacters(in: .whitespaces).isEmpty {
                    // A bullet glyph on its own belongs to the line just right of it.
                    if bullet { glyphMarks.append(s.box) }
                } else {
                    s.text = rest
                    s.bullet = bullet
                    let sorted = sizes.sorted()
                    s.size = sorted.isEmpty ? 0 : sorted[sorted.count / 2]
                    result.append(s)
                }
                segment = nil
                segmentText = ""
                sizes = []
            }

            for i in indices {
                let unit = string.character(at: i)
                // Surrogate halves are measured with their first half.
                if UTF16.isTrailSurrogate(unit) { continue }
                let length = UTF16.isLeadSurrogate(unit) && i + 1 < string.length ? 2 : 1
                let ch = string.substring(with: NSRange(location: i, length: length))
                if ch.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    pendingSpace = true
                    continue
                }
                let rect = characterRect(i)
                guard rect.width > 0 || rect.height > 0 else { continue }
                let st = styleIndex[i] >= 0 ? style(attributed.attributes(at: styleIndex[i], effectiveRange: nil)) : DocRun("")
                let em = max(4, st.size > 0 ? st.size : Double(rect.height))
                if let s = segment {
                    let gap = Double(rect.minX - lastRect.maxX)
                    if gap > max(8, em * 1.2) || Double(rect.minX) < Double(lastRect.minX) - em * 2 {
                        finish()
                    } else if pendingSpace || gap > em * 0.15 {
                        append(&s.runs, " ", st)
                        segmentText += " "
                    }
                }
                if segment == nil {
                    segment = LineInfo()
                    segment!.box = rect
                }
                let s = segment!
                append(&s.runs, ch, st)
                segmentText += ch
                s.box = s.box.union(rect)
                if st.size > 0 { sizes.append(st.size) }
                lastRect = rect
                pendingSpace = false
            }
            finish()
        }
        return result
    }

    /// Top to bottom, except where pieces sit side by side in columns: between two full-width pieces,
    /// the left column is read before the right one.
    private static func readingOrder(_ lines: [LineInfo]) -> [LineInfo] {
        guard lines.count > 1 else { return lines }
        let sorted = lines.sorted { $0.box.maxY > $1.box.maxY }
        let mid = Double((sorted.map(\.box.minX).min()! + sorted.map(\.box.maxX).max()!) / 2)
        func spans(_ r: CGRect) -> Bool { Double(r.minX) < mid - 10 && Double(r.maxX) > mid + 10 }
        func overlap(_ a: CGRect, _ b: CGRect) -> Bool { a.minY < b.maxY && b.minY < a.maxY }
        var result: [LineInfo] = []
        var band: [LineInfo] = []
        func flush() {
            let columns = band.contains { a in
                band.contains { b in Double(a.box.maxX) <= mid + 10 && Double(b.box.minX) >= mid - 10 && overlap(a.box, b.box) }
            }
            if columns {
                band.sort { a, b in
                    let ca = Double(a.box.minX) < mid - 10 ? 0 : 1, cb = Double(b.box.minX) < mid - 10 ? 0 : 1
                    return ca != cb ? ca < cb : a.box.maxY > b.box.maxY
                }
            }
            result += band
            band = []
        }
        for line in sorted {
            if spans(line.box) {
                flush()
                result.append(line)
            } else {
                band.append(line)
            }
        }
        flush()
        return result
    }

    /// Tables: two or more consecutive rows that each hold the same number (2+) of separate, short pieces of
    /// text lined up under one another.
    private static func tables(_ lines: [LineInfo]) -> [Item] {
        var rows: [[LineInfo]] = []
        for line in lines.sorted(by: { $0.box.midY > $1.box.midY }) {
            if let first = rows.last?.first, abs(line.box.midY - first.box.midY) < max(first.box.height, line.box.height) * 0.5 {
                rows[rows.count - 1].append(line)
            } else {
                rows.append([line])
            }
        }
        rows = rows.map { $0.sorted { $0.box.minX < $1.box.minX } }

        func cells(_ row: [LineInfo]) -> Bool {
            guard row.count >= 2 else { return false }
            for i in 1..<row.count where Double(row[i].box.minX - row[i - 1].box.maxX) < max(8, max(row[i].size, row[i - 1].size) * 1.2) {
                return false
            }
            return true
        }

        var result: [Item] = []
        var r = 0
        while r < rows.count {
            guard cells(rows[r]) else { r += 1; continue }
            let n = rows[r].count
            var columns = rows[r].map { (left: Double($0.box.minX), right: Double($0.box.maxX)) }
            var end = r + 1
            while end < rows.count, cells(rows[end]), rows[end].count == n {
                let previous = rows[end - 1]
                let pitch = Double(previous[0].box.midY - rows[end][0].box.midY)
                if pitch > max(Double(previous[0].box.height), 6) * 4 { break }
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
            // Real tables hold short entries; two columns of running text are a page layout, not a table.
            if group.count >= 2, averageLength < 40, widest < max(span * 0.6, 120) {
                let table = DocTable()
                let em = max(8, all.map(\.size).reduce(0, +) / Double(max(1, all.count)))
                for k in 0..<n {
                    let raw = k + 1 < n ? columns[k + 1].left - columns[k].left : columns[k].right - columns[k].left + em * 1.5
                    table.widths.append((max(em * 3, raw) + 8).rounded())
                }
                for row in group {
                    table.rows.append(row.map { line in line.runs.map { $0.copy(text: fixRadicals($0.text)) } })
                    for line in row { line.inTable = true }
                }
                let item = Item()
                item.top = Double(group[0].map(\.box.maxY).max() ?? 0)
                item.size = group[0][0].size
                item.para.table = table
                item.lines = all.map(\.box)
                result.append(item)
                r = end
            } else {
                r += 1
            }
        }
        return result
    }

    /// Lines in reading order → paragraphs: a new one starts at a bullet, a size change, a bigger gap,
    /// an indent change, a short line before, or a switch between bold and regular.
    private static func paragraphs(_ lines: [LineInfo]) -> [Item] {
        var gaps: [Double] = []
        for (a, b) in zip(lines, lines.dropFirst()) where abs(a.size - b.size) < 0.6 {
            let gap = Double(a.box.minY - b.box.maxY)
            if gap > -2 { gaps.append(gap) }
        }
        gaps.sort()
        let typicalGap = gaps.isEmpty ? 0 : gaps[gaps.count / 2]

        var result: [Item] = []
        var current: Item?
        var previous: LineInfo?
        for line in lines {
            var split = current == nil || previous == nil || line.bullet
            if !split, let prev = previous, let cur = current {
                let gap = Double(prev.box.minY - line.box.maxY)
                let em = max(4, max(line.size, prev.size))
                let limit = max(em * 0.85, typicalGap * 1.6 + 1)
                // The right edge of this column: the widest line that shares the previous line's horizontal span.
                let columnRight = lines.filter { $0.box.minX < prev.box.maxX && $0.box.maxX > prev.box.minX }.map { Double($0.box.maxX) }.max() ?? Double(prev.box.maxX)
                let columnLeft = lines.filter { $0.box.minX < prev.box.maxX && $0.box.maxX > prev.box.minX }.map { Double($0.box.minX) }.min() ?? Double(prev.box.minX)
                let previousShort = Double(prev.box.maxX) < columnRight - max((columnRight - columnLeft) * 0.18, prev.size * 3)
                let boldNow = line.runs.allSatisfy(\.bold), boldBefore = prev.runs.allSatisfy(\.bold)
                if abs(line.size - prev.size) > max(line.size, prev.size) * 0.12 { split = true }
                else if gap > limit || gap < -em * 1.5 { split = true }
                else if Double(line.box.minX - prev.box.minX) > em { split = true }
                else if Double(prev.box.minX - line.box.minX) > em && cur.lines.count > 1 { split = true }
                else if previousShort { split = true }
                else if boldNow != boldBefore && (boldNow || cur.para.text.count < 60) { split = true }
            }
            if split {
                let item = Item()
                item.top = Double(line.box.maxY)
                item.size = line.size
                item.para.bullet = line.bullet
                for r in line.runs { append(&item.para.runs, r.text, r) }
                item.lines.append(line.box)
                result.append(item)
                current = item
            } else if let cur = current {
                if let last = cur.para.runs.last, !joinsTight(cur.para.text, line.text) { append(&cur.para.runs, " ", last) }
                for r in line.runs { append(&cur.para.runs, r.text, r) }
                cur.lines.append(line.box)
            }
            previous = line
        }
        return result
    }

    // MARK: Pictures

    private final class ImageScan {
        var stack: [CGAffineTransform] = []
        var ctm = CGAffineTransform.identity
        var rects: [CGRect] = []
        var depth = 0
        var table: CGPDFOperatorTableRef?
    }

    /// Where the page paints its pictures, found by following the content stream's transforms.
    private static func imageRects(_ page: CGPDFPage) -> [CGRect] {
        guard let table = CGPDFOperatorTableCreate() else { return [] }
        defer { CGPDFOperatorTableRelease(table) }
        let scan = ImageScan()
        scan.table = table
        CGPDFOperatorTableSetCallback(table, "q") { _, info in
            guard let info else { return }
            let s = Unmanaged<ImageScan>.fromOpaque(info).takeUnretainedValue()
            s.stack.append(s.ctm)
        }
        CGPDFOperatorTableSetCallback(table, "Q") { _, info in
            guard let info else { return }
            let s = Unmanaged<ImageScan>.fromOpaque(info).takeUnretainedValue()
            if let last = s.stack.popLast() { s.ctm = last }
        }
        CGPDFOperatorTableSetCallback(table, "cm") { scanner, info in
            guard let info else { return }
            let s = Unmanaged<ImageScan>.fromOpaque(info).takeUnretainedValue()
            var v = [CGPDFReal](repeating: 0, count: 6)
            for i in (0..<6).reversed() { if !CGPDFScannerPopNumber(scanner, &v[i]) { return } }
            s.ctm = CGAffineTransform(a: v[0], b: v[1], c: v[2], d: v[3], tx: v[4], ty: v[5]).concatenating(s.ctm)
        }
        CGPDFOperatorTableSetCallback(table, "Do") { scanner, info in
            guard let info else { return }
            let s = Unmanaged<ImageScan>.fromOpaque(info).takeUnretainedValue()
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
                s.rects.append(CGRect(x: 0, y: 0, width: 1, height: 1).applying(s.ctm).standardized)
            } else if kind == "Form", s.depth < 4, let opTable = s.table {
                var resources: CGPDFDictionaryRef?
                guard CGPDFDictionaryGetDictionary(dict, "Resources", &resources), let resources else { return }
                let saved = s.ctm
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
                s.ctm = saved
            }
        }
        let content = CGPDFContentStreamCreateWithPage(page)
        let scanner = CGPDFScannerCreate(content, table, Unmanaged.passUnretained(scan).toOpaque())
        _ = CGPDFScannerScan(scanner)
        CGPDFScannerRelease(scanner)
        CGPDFContentStreamRelease(content)
        return scan.rects
    }

    /// The picture as it appears on the page, rendered at 2× (JPEG for photos, PNG for small graphics).
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
        let photo = w * h > 250_000
        guard let data = photo ? rep.representation(using: .jpeg, properties: [.compressionFactor: 0.85]) : rep.representation(using: .png, properties: [:]) else { return nil }
        return DocImage(data: data, format: photo ? "jpeg" : "png", width: Double(rect.width), height: Double(rect.height))
    }

    // MARK: Whole document

    static func extract(_ url: URL) throws -> DocModel {
        guard let doc = PDFDocument(url: url) else { throw FWError("打不開這個 PDF") }
        if doc.isLocked { throw FWError("這個 PDF 有密碼保護") }
        var items: [Item] = []
        var pageSizes: [CGSize] = []
        for index in 0..<doc.pageCount {
            guard let page = doc.page(at: index) else { continue }
            let box = page.bounds(for: .mediaBox)
            pageSizes.append(box.size)
            var marks: [CGRect] = []
            let lines = pageLines(page, marks: &marks)
            for mark in marks {
                let owner = lines.filter { $0.box.minX >= mark.minX && $0.box.minX - mark.maxX < 40 && mark.midY <= $0.box.maxY + 2 && mark.midY >= $0.box.minY - 2 }
                    .min { ($0.box.minX - mark.maxX) < ($1.box.minX - mark.maxX) }
                owner?.bullet = true
            }
            let found = tables(lines)
            var pageItems = paragraphs(readingOrder(lines.filter { !$0.inTable }))
            for table in found {
                if let at = pageItems.firstIndex(where: { $0.top <= table.top + 1 }) { pageItems.insert(table, at: at) } else { pageItems.append(table) }
            }
            // Pictures, slotted in among the paragraphs by height on the page.
            if let ref = page.pageRef {
                let area = Double(box.width * box.height)
                for rect in imageRects(ref).sorted(by: { $0.maxY > $1.maxY }) {
                    let r = rect.intersection(box)
                    guard !r.isNull, r.width >= 12, r.height >= 12 else { continue }
                    // A picture behind the whole page with text on it is a background, not content.
                    if !lines.isEmpty, Double(r.width * r.height) > area * 0.8 { continue }
                    guard let image = renderRegion(ref, r) else { continue }
                    let item = Item()
                    item.top = Double(r.maxY)
                    item.imageBox = r
                    item.para.image = image
                    if let at = pageItems.firstIndex(where: { $0.imageBox == nil && $0.top <= Double(r.maxY) + 1 }) {
                        pageItems.insert(item, at: at)
                    } else {
                        pageItems.append(item)
                    }
                }
            }
            for item in pageItems { item.page = index }
            items += pageItems
        }
        return layout(items, pageSizes)
    }

    /// Margins, headings, alignment and page breaks for the collected items.
    private static func layout(_ items: [Item], _ pageSizes: [CGSize]) -> DocModel {
        let model = DocModel()
        let allLines = items.flatMap(\.lines)
        let pageWidth = Double(pageSizes.first?.width ?? 595)
        var body = allLines.filter { Double($0.width) > pageWidth * 0.3 }
        if body.isEmpty { body = allLines }
        let contentLeft = body.map { Double($0.minX) }.min() ?? 56
        let contentRight = body.map { Double($0.maxX) }.max() ?? pageWidth - 56
        let contentWidth = max(50, contentRight - contentLeft)

        // Headings: noticeably larger than the most common text size.
        var weight: [Double: Int] = [:]
        for item in items where item.size > 0 && item.para.image == nil && item.para.table == nil {
            weight[item.size.rounded(), default: 0] += item.para.text.count
        }
        var bodySize = weight.max { $0.value < $1.value }?.key ?? 12
        if bodySize <= 0 { bodySize = 12 }

        var lastPage = -1
        for item in items {
            let p = item.para
            if item.page != lastPage {
                p.pageBreak = lastPage >= 0
                lastPage = item.page
            }
            if let table = p.table {
                let l = item.lines.map { Double($0.minX) }.min() ?? contentLeft, r = item.lines.map { Double($0.maxX) }.max() ?? contentRight
                let tolerance = max(6, contentWidth * 0.04)
                if abs((l - contentLeft) - (contentRight - r)) < tolerance * 2 && l - contentLeft > tolerance * 2 { table.align = .center }
                else if l - contentLeft > 8 { table.indent = (l - contentLeft).rounded() }
                model.paras.append(p)
                continue
            }
            if let b = item.imageBox {
                let mid = Double(b.midX)
                if abs(mid - (contentLeft + contentRight) / 2) < contentWidth * 0.08 && Double(b.width) < contentWidth * 0.9 { p.align = .center }
                else if Double(b.minX) - contentLeft > 8 { p.indent = min(Double(b.minX) - contentLeft, contentWidth * 0.6) }
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
                let allBold = p.runs.allSatisfy { $0.bold || $0.text.trimmingCharacters(in: .whitespaces).isEmpty }
                if item.size >= bodySize * 1.55 { p.heading = 1 }
                else if item.size >= bodySize * 1.25 { p.heading = 2 }
                else if item.size >= bodySize * 1.1 || (allBold && text.count <= 40 && item.lines.count == 1) { p.heading = 3 }
            }
            // Alignment and indent, measured against the text margins.
            let tol = max(4, contentWidth * 0.03)
            let minLeft = item.lines.map { Double($0.minX) }.min() ?? contentLeft
            let maxRight = item.lines.map { Double($0.maxX) }.max() ?? contentRight
            let centered = item.lines.allSatisfy { abs((Double($0.minX) - contentLeft) - (contentRight - Double($0.maxX))) < tol * 2 } && minLeft - contentLeft > tol * 2
            if centered { p.align = .center }
            else if contentRight - maxRight < tol && minLeft - contentLeft > contentWidth * 0.35 { p.align = .right }
            else if minLeft - contentLeft > 8 { p.indent = min(minLeft - contentLeft, contentWidth * 0.6).rounded() }
            model.paras.append(p)
        }

        if let first = pageSizes.first {
            let firstPage = items.first?.page ?? 0
            let firstLines = items.filter { $0.page == firstPage }.flatMap(\.lines)
            let top = firstLines.isEmpty ? 56 : Double(first.height) - Double(firstLines.map(\.maxY).max()!)
            model.pageWidth = (Double(first.width) * 10).rounded() / 10
            model.pageHeight = (Double(first.height) * 10).rounded() / 10
            model.marginLeft = min(108, max(18, contentLeft.rounded()))
            model.marginRight = min(108, max(18, (Double(first.width) - contentRight).rounded()))
            model.marginTop = min(90, max(18, top.rounded()))
            model.marginBottom = min(72, max(18, (model.marginTop * 0.9).rounded()))
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

    private static func fit(_ model: DocModel, _ image: DocImage) -> (Double, Double) {
        let maxWidth = contentWidth(model)
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
        if p.indent > 1 { style.append("margin-left:\(num(p.indent))pt") }
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
            if p.align == .center { sb += "\\qc" } else if p.align == .right { sb += "\\qr" }
            if p.indent > 1 { sb += "\\li\(twips(p.indent))" }
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
        func docxRuns(_ runs: [DocRun], _ heading: Int) {
            for r in runs {
                for (k, piece) in r.text.components(separatedBy: "\n").enumerated() {
                    body += "<w:r>"
                    if r.bold || r.italic || r.color != nil || r.size > 0 {
                        body += "<w:rPr>"
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
                for k in 0..<cols { body += "<w:gridCol w:w=\"\(twips(table.width(k)))\"/>" }
                body += "</w:tblGrid>"
                for row in table.rows {
                    body += "<w:tr>"
                    for k in 0..<cols {
                        body += "<w:tc><w:tcPr><w:tcW w:w=\"\(twips(table.width(k)))\" w:type=\"dxa\"/></w:tcPr><w:p><w:pPr><w:spacing w:after=\"0\"/></w:pPr>"
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
            if p.indent > 1 { body += "<w:ind w:left=\"\(twips(p.indent))\"/>" }
            if p.align == .center { body += "<w:jc w:val=\"center\"/>" } else if p.align == .right { body += "<w:jc w:val=\"right\"/>" }
            body += "</w:pPr>"
            if let image = p.image {
                let n = media.count + 1
                let name = "image\(n).\(ext(image))"
                media.append((name, image))
                let (w, h) = fit(model, image)
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

        let pageW = twips(model.pageWidth > 0 ? model.pageWidth : 595.3), pageH = twips(model.pageHeight > 0 ? model.pageHeight : 841.9)
        let ml = twips(model.marginLeft > 0 ? model.marginLeft : 56.7), mr = twips(model.marginRight > 0 ? model.marginRight : 56.7)
        let mt = twips(model.marginTop > 0 ? model.marginTop : 56.7), mb = twips(model.marginBottom > 0 ? model.marginBottom : 56.7)
        let document = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" "
            + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" "
            + "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" "
            + "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" "
            + "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><w:body>" + body
            + "<w:sectPr><w:pgSz w:w=\"\(pageW)\" w:h=\"\(pageH)\"/><w:pgMar w:top=\"\(mt)\" w:right=\"\(mr)\" w:bottom=\"\(mb)\" w:left=\"\(ml)\" "
            + "w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>"
        func headingStyle(_ level: Int, _ halfPoints: Int) -> String {
            "<w:style w:type=\"paragraph\" w:styleId=\"Heading\(level)\"><w:name w:val=\"heading \(level)\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/>"
                + "<w:pPr><w:keepNext/><w:spacing w:before=\"120\" w:after=\"120\"/><w:outlineLvl w:val=\"\(level - 1)\"/></w:pPr>"
                + "<w:rPr><w:b/><w:bCs/><w:sz w:val=\"\(halfPoints)\"/><w:szCs w:val=\"\(halfPoints)\"/></w:rPr></w:style>"
        }
        let styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
            + "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Helvetica Neue\" w:hAnsi=\"Helvetica Neue\" w:eastAsia=\"PingFang TC\" w:cs=\"Helvetica Neue\"/>"
            + "<w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"en-US\" w:eastAsia=\"zh-TW\"/></w:rPr></w:rPrDefault>"
            + "<w:pPrDefault><w:pPr><w:spacing w:after=\"100\" w:line=\"252\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>"
            + "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>"
            + headingStyle(1, 40) + headingStyle(2, 32) + headingStyle(3, 27) + "</w:styles>"
        let contentTypes = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
            + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
            + "<Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>"
            + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>"
            + "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>"
        let rels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>"
        var documentRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
        for (i, item) in media.enumerated() {
            documentRels += "<Relationship Id=\"rIdImg\(i + 1)\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/\(item.0)\"/>"
        }
        documentRels += "</Relationships>"
        var files: [(String, Data)] = [
            ("[Content_Types].xml", Data(contentTypes.utf8)), ("_rels/.rels", Data(rels.utf8)),
            ("word/document.xml", Data(document.utf8)), ("word/styles.xml", Data(styles.utf8)),
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
            let key = "\(p.heading)|\(explicit)|\(p.align)|\(num(p.indent))|\(p.pageBreak)"
            if let name = paraStyles[key] { return name }
            let name = "P\(paraStyles.count + 1)"
            paraStyles[key] = name
            styleXML += "<style:style style:name=\"\(name)\" style:family=\"paragraph\"><style:paragraph-properties fo:margin-bottom=\"0.1cm\""
            if p.align != .left { styleXML += " fo:text-align=\"\(p.align == .center ? "center" : "end")\"" }
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
            guard r.bold || r.italic || r.size > 0 || r.color != nil else { return nil }
            let key = "\(r.bold)|\(r.italic)|\(num(r.size))|\(r.color ?? "")"
            if let name = textStyles[key] { return name }
            let name = "T\(textStyles.count + 1)"
            textStyles[key] = name
            styleXML += "<style:style style:name=\"\(name)\" style:family=\"text\"><style:text-properties"
            if r.bold { styleXML += " fo:font-weight=\"bold\" style:font-weight-asian=\"bold\"" }
            if r.italic { styleXML += " fo:font-style=\"italic\" style:font-style-asian=\"italic\"" }
            if r.size > 0 { styleXML += " fo:font-size=\"\(num(r.size))pt\" style:font-size-asian=\"\(num(r.size))pt\"" }
            if let c = r.color { styleXML += " fo:color=\"#\(c)\"" }
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
