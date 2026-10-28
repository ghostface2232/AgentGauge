import Foundation

/// One subpath: a start point followed by cubic Bézier segments.
public struct SVGFigure: Sendable, Equatable {
    public struct Point: Sendable, Equatable {
        public var x: Double
        public var y: Double
        public init(_ x: Double, _ y: Double) { self.x = x; self.y = y }
    }

    public struct Segment: Sendable, Equatable {
        public var control1: Point
        public var control2: Point
        public var end: Point
    }

    public var start: Point
    public var segments: [Segment]
    public var closed: Bool
}

public enum SVGPathError: Error, Equatable {
    case malformed(String)
}

/// Minimal SVG path-data reader for the menu bar logos (`Assets/ToolLogos`), the Mac twin of
/// the Windows `SvgPathData`: M L H V C S Q T Z, absolute and relative, all turned into cubic
/// segments. Arcs (A) are rejected, so a logo using one is drawn as the ring alone rather
/// than as a wrong shape.
public enum SVGPathData {
    private enum Token: Equatable {
        case command(Character)
        case number(Double)
    }

    public static func parse(_ data: String) throws -> [SVGFigure] {
        let tokens = try tokenize(data)
        typealias Point = SVGFigure.Point
        var figures: [SVGFigure] = []
        var segments: [SVGFigure.Segment] = []
        var start = Point(0, 0), current = Point(0, 0), lastControl = Point(0, 0)
        var hasFigure = false
        var command: Character = " "
        var previous: Character = " "
        var index = 0

        func flush(closed: Bool) {
            if hasFigure { figures.append(SVGFigure(start: start, segments: segments, closed: closed)) }
            segments = []
            hasFigure = false
        }

        func next() throws -> Double {
            guard index < tokens.count, case let .number(value) = tokens[index] else {
                throw SVGPathError.malformed("SVG path data ended where a number was expected.")
            }
            index += 1
            return value
        }

        func reflect(_ control: Point, about: Point) -> Point {
            Point(2 * about.x - control.x, 2 * about.y - control.y)
        }

        func quadratic(_ from: Point, _ q: Point, _ to: Point) -> SVGFigure.Segment {
            SVGFigure.Segment(
                control1: Point(from.x + 2.0 / 3 * (q.x - from.x), from.y + 2.0 / 3 * (q.y - from.y)),
                control2: Point(to.x + 2.0 / 3 * (q.x - to.x), to.y + 2.0 / 3 * (q.y - to.y)),
                end: to)
        }

        while index < tokens.count {
            if case let .command(c) = tokens[index] {
                command = c
                index += 1
            } else if command == " " {
                throw SVGPathError.malformed("SVG path data has a number with no command before it.")
            }

            let relative = command.isLowercase
            let origin = relative ? current : Point(0, 0)
            func point() throws -> Point {
                let x = try next()
                let y = try next()
                return Point(origin.x + x, origin.y + y)
            }
            func line(to end: Point) {
                segments.append(.init(control1: current, control2: end, end: end))
                current = end
            }

            switch Character(command.uppercased()) {
            case "M":
                flush(closed: false)
                current = try point()
                start = current
                hasFigure = true
                // Coordinates following a moveto are implicit linetos.
                command = relative ? "l" : "L"
            case "L":
                line(to: try point())
            case "H":
                let x = try next()
                line(to: Point((relative ? current.x : 0) + x, current.y))
            case "V":
                let y = try next()
                line(to: Point(current.x, (relative ? current.y : 0) + y))
            case "C":
                let c1 = try point(), c2 = try point(), end = try point()
                segments.append(.init(control1: c1, control2: c2, end: end))
                lastControl = c2
                current = end
            case "S":
                let c1 = "CcSs".contains(previous) ? reflect(lastControl, about: current) : current
                let c2 = try point(), end = try point()
                segments.append(.init(control1: c1, control2: c2, end: end))
                lastControl = c2
                current = end
            case "Q":
                let q = try point(), end = try point()
                segments.append(quadratic(current, q, end))
                lastControl = q
                current = end
            case "T":
                let q = "QqTt".contains(previous) ? reflect(lastControl, about: current) : current
                let end = try point()
                segments.append(quadratic(current, q, end))
                lastControl = q
                current = end
            case "Z":
                flush(closed: true)
                current = start
                // Z takes no arguments, so a number after it has no command to belong to.
                previous = command
                command = " "
                continue
            default:
                throw SVGPathError.malformed("Unsupported SVG path command '\(command)'.")
            }
            previous = command
        }

        flush(closed: false)
        return figures
    }

    private static func tokenize(_ data: String) throws -> [Token] {
        let chars = Array(data)
        var tokens: [Token] = []
        var i = 0
        while i < chars.count {
            let c = chars[i]
            if c.isWhitespace || c == "," { i += 1; continue }
            if c.isLetter && c != "e" && c != "E" { tokens.append(.command(c)); i += 1; continue }

            // A number ends at a second sign (not after an exponent) or a second decimal
            // point, so "-1.5.5-2" reads as -1.5, .5, -2.
            let begin = i
            if chars[i] == "+" || chars[i] == "-" { i += 1 }
            var seenDot = false
            while i < chars.count {
                let d = chars[i]
                if d.isASCII && d.isNumber { i += 1; continue }
                if d == "." && !seenDot { seenDot = true; i += 1; continue }
                if d == "e" || d == "E" {
                    i += 1
                    if i < chars.count && (chars[i] == "+" || chars[i] == "-") { i += 1 }
                    continue
                }
                break
            }
            guard i > begin, let value = Double(String(chars[begin..<i])) else {
                throw SVGPathError.malformed("Unexpected character '\(c)' in SVG path data.")
            }
            tokens.append(.number(value))
        }
        return tokens
    }
}

/// A single-path, single-colour logo as stored in `Assets/ToolLogos`: the viewBox size (its
/// origin must be 0 0), the path's figures, and whether it fills even-odd.
public struct SVGLogo: Sendable, Equatable {
    public var width: Double
    public var height: Double
    public var figures: [SVGFigure]
    public var evenOdd: Bool

    /// Nil for a document without a `viewBox="0 0 w h"` and a path `d`, or with path data
    /// the reader does not support.
    public init?(svg: String) {
        guard let box = Self.capture(#"viewBox="0 0 ([\d.]+) ([\d.]+)""#, in: svg, groups: 2),
              let width = Double(box[0]), let height = Double(box[1]),
              let data = Self.capture(#" d="([^"]+)""#, in: svg, groups: 1),
              let figures = try? SVGPathData.parse(data[0])
        else { return nil }
        self.width = width
        self.height = height
        self.figures = figures
        self.evenOdd = svg.contains("evenodd")
    }

    private static func capture(_ pattern: String, in text: String, groups: Int) -> [String]? {
        guard let regex = try? NSRegularExpression(pattern: pattern),
              let match = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text))
        else { return nil }
        return (1...groups).compactMap { Range(match.range(at: $0), in: text).map { String(text[$0]) } }
    }
}
