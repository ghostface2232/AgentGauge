import Foundation

/// Timestamp contract for provider payloads: ISO-8601 with or without fractional seconds
/// (any precision), with or without an offset, or a bare date. A value carrying an offset
/// keeps its instant; one carrying none is read as UTC, never as the reader's local time.
/// These endpoints are undocumented, so a field may change between a full timestamp and a
/// bare date — the parse is written to be correct under either.
public enum Timestamp {
    public static func parse(_ text: String) -> Date? {
        var rest = Substring(text.trimmingCharacters(in: .whitespaces))

        func digits(_ count: Int) -> Int? {
            let head = rest.prefix(count)
            guard head.count == count, head.allSatisfy({ $0.isASCII && $0.isNumber }), let value = Int(head) else {
                return nil
            }
            rest = rest.dropFirst(count)
            return value
        }
        func consume(_ characters: Character...) -> Bool {
            guard let first = rest.first, characters.contains(first) else { return false }
            rest = rest.dropFirst()
            return true
        }

        guard let year = digits(4), consume("-"), let month = digits(2), consume("-"), let day = digits(2) else {
            return nil
        }
        var hour = 0, minute = 0, second = 0
        var fraction = 0.0
        var offset = 0

        if consume("T", "t", " ") {
            guard let h = digits(2), consume(":"), let m = digits(2) else { return nil }
            hour = h
            minute = m
            if consume(":") {
                guard let s = digits(2) else { return nil }
                second = s
                if consume(".", ",") {
                    let fractionDigits = rest.prefix { $0.isASCII && $0.isNumber }
                    guard !fractionDigits.isEmpty else { return nil }
                    fraction = Double("0." + String(fractionDigits)) ?? 0
                    rest = rest.dropFirst(fractionDigits.count)
                }
            }
            if consume("Z", "z") {
                offset = 0
            } else if let sign = rest.first, sign == "+" || sign == "-" {
                rest = rest.dropFirst()
                guard let offsetHours = digits(2) else { return nil }
                _ = consume(":")
                let offsetMinutes = digits(2) ?? 0
                offset = (offsetHours * 3600 + offsetMinutes * 60) * (sign == "-" ? -1 : 1)
            }
        }
        guard rest.isEmpty,
              (1...12).contains(month), (1...31).contains(day),
              (0...23).contains(hour), (0...59).contains(minute), (0...60).contains(second) else {
            return nil
        }

        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let components = DateComponents(year: year, month: month, day: day, hour: hour, minute: minute, second: min(second, 59))
        guard let date = calendar.date(from: components) else { return nil }
        // Calendar rolls invalid days over (Feb 30 → Mar 2); a malformed value is rejected instead.
        guard calendar.component(.day, from: date) == day else { return nil }
        return date.addingTimeInterval(fraction - TimeInterval(offset))
    }
}
