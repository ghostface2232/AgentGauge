import Foundation
import XCTest
@testable import GaugeCore

private func utcCalendar() -> Calendar {
    var calendar = Calendar(identifier: .gregorian)
    calendar.timeZone = TimeZone(identifier: "UTC")!
    return calendar
}

private func calendar(_ zone: TimeZone) -> Calendar {
    var calendar = Calendar(identifier: .gregorian)
    calendar.timeZone = zone
    return calendar
}

private let day: TimeInterval = 86_400
// Monday 2026-08-03 00:00 UTC → next Monday, so local weekdays are unambiguous.
private let monday = Date(timeIntervalSince1970: 1_785_715_200)
private let nextMonday = monday.addingTimeInterval(7 * day)

/// The weekday-weighted elapsed share behind the work-day and automatic pace models: a zero
/// weight flattens the curve across that day, equal weights reproduce the uniform spread, and
/// a cycle with no weight at all yields nothing rather than a division by zero.
final class WeeklyPaceTimelineTests: XCTestCase {
    private let utc = utcCalendar()

    private func ratio(_ daysIn: Double, _ weights: [Double] = WeeklyPaceTimeline.workDayWeights) -> Double? {
        WeeklyPaceTimeline.elapsedRatio(
            start: monday, now: monday.addingTimeInterval(daysIn * day), end: nextMonday, weights: weights, calendar: utc)
    }

    func testWorkDayWeightsFreezeTheWeekend() throws {
        for (daysIn, expected) in [(0.0, 0.0), (2.0, 0.4), (2.5, 0.5), (5.0, 1.0), (5.5, 1.0), (6.9, 1.0)] {
            XCTAssertEqual(try XCTUnwrap(ratio(daysIn)), expected, accuracy: 1e-6, "\(daysIn) days in")
        }
    }

    func testEqualWeightsReproduceTheUniformSpread() throws {
        for daysIn in [1.75, 3.0, 6.25] {
            XCTAssertEqual(try XCTUnwrap(ratio(daysIn, Array(repeating: 1, count: 7))), daysIn / 7, accuracy: 1e-6)
        }
    }

    func testWeightsAreRelativeNotAbsolute() throws {
        // A profile that is simply taller (more cycles accumulated) has the same shape.
        let shape: [Double] = [0, 3, 1, 1, 1, 0, 0]
        XCTAssertEqual(try XCTUnwrap(ratio(1.5, shape)), try XCTUnwrap(ratio(1.5, shape.map { $0 * 8 })), accuracy: 1e-12)
        // Monday carries half the week's weight, so half of Monday is a quarter of the cycle.
        XCTAssertEqual(try XCTUnwrap(ratio(0.5, shape)), 0.25, accuracy: 1e-6)
    }

    func testCountsDaysInTheGivenZoneNotUtc() throws {
        // UTC+9: Friday 20:00 UTC is already Saturday 05:00 in Seoul, so a work-day model in
        // that zone has finished the week, while the UTC reading has five hours to go.
        let seoul = calendar(TimeZone(secondsFromGMT: 9 * 3600)!)
        let fridayEveningUtc = monday.addingTimeInterval(4 * day + 20 * 3600)
        let start = monday.addingTimeInterval(-9 * 3600) // Monday 00:00 in Seoul
        let end = start.addingTimeInterval(7 * day)
        XCTAssertEqual(try XCTUnwrap(WeeklyPaceTimeline.elapsedRatio(
            start: start, now: fridayEveningUtc, end: end, weights: WeeklyPaceTimeline.workDayWeights, calendar: seoul)),
            1.0, accuracy: 1e-6)
        XCTAssertLessThan(try XCTUnwrap(WeeklyPaceTimeline.elapsedRatio(
            start: start, now: fridayEveningUtc, end: end, weights: WeeklyPaceTimeline.workDayWeights, calendar: utc)), 1.0)
    }

    func testAFallBackAtMidnightKeepsTheRepeatedHourOnTheNewDay() throws {
        // Cuba ends daylight time at 01:00 on Sunday 1 November 2026, so 00:00–01:00 occurs
        // twice. The first occurrence already belongs to Sunday, and Sunday is 25 local hours
        // long. Only Sunday carries weight, so half an hour into its first 00:00 must read as
        // 0.5 of 25 hours — not 0, which crediting the repeated hour to Saturday would give.
        let havana = calendar(try XCTUnwrap(TimeZone(identifier: "America/Havana")))
        let start = Date(timeIntervalSince1970: 1_792_987_200)            // Mon 26 Oct 00:00 -04:00
        let end = Date(timeIntervalSince1970: 1_793_595_600)              // Mon 2 Nov 00:00 -05:00
        let sundayFirstHalfHour = Date(timeIntervalSince1970: 1_793_507_400) // Sun 1 Nov 00:30 -04:00
        XCTAssertEqual(havana.component(.weekday, from: sundayFirstHalfHour), 1)
        let sundayOnly: [Double] = [1, 0, 0, 0, 0, 0, 0]
        XCTAssertEqual(try XCTUnwrap(WeeklyPaceTimeline.elapsedRatio(
            start: start, now: sundayFirstHalfHour, end: end, weights: sundayOnly, calendar: havana)),
            0.5 / 25, accuracy: 1e-6)
        XCTAssertEqual(try XCTUnwrap(WeeklyPaceTimeline.elapsedRatio(
            start: start, now: end, end: end, weights: sundayOnly, calendar: havana)), 1.0, accuracy: 1e-6)
    }

    func testASpringForwardAtMidnightStillEndsTheDayAtMidnight() throws {
        // Havana springs forward at 00:00 → 01:00 on Sunday 8 March 2026, so that Sunday
        // starts at 01:00 and is 23 hours long. The following Monday must still begin at its
        // own midnight, not at 01:00 carried over from Sunday's start.
        let havana = calendar(try XCTUnwrap(TimeZone(identifier: "America/Havana")))
        let start = Date(timeIntervalSince1970: 1_772_427_600)   // Mon 2 Mar 00:00 -05:00
        let end = start.addingTimeInterval(7 * day - 3600)       // Mon 9 Mar 00:00 -04:00
        let mondayHalfHour = end.addingTimeInterval(1800)        // Mon 9 Mar 00:30 -04:00
        let mondayOnly: [Double] = [0, 1, 0, 0, 0, 0, 0]
        XCTAssertEqual(havana.component(.weekday, from: mondayHalfHour), 2)
        XCTAssertEqual(try XCTUnwrap(WeeklyPaceTimeline.elapsedRatio(
            start: end, now: mondayHalfHour, end: end.addingTimeInterval(day), weights: mondayOnly, calendar: havana)),
            0.5 / 24, accuracy: 1e-6)
        // The week's Sunday is 23 hours: half of it, with only Sunday weighted, is 11.5 hours in.
        let sundayStart = Date(timeIntervalSince1970: 1_772_946_000) // Sun 8 Mar 00:00 -05:00 = 01:00 -04:00
        XCTAssertEqual(try XCTUnwrap(WeeklyPaceTimeline.elapsedRatio(
            start: start, now: sundayStart.addingTimeInterval(11.5 * 3600), end: end,
            weights: [1, 0, 0, 0, 0, 0, 0], calendar: havana)), 0.5, accuracy: 1e-6)
    }

    func testClampsNowToTheCycle() {
        XCTAssertEqual(ratio(-3), 0)
        XCTAssertEqual(ratio(9), 1)
    }

    func testNoWeightAnywhereYieldsNothing() {
        XCTAssertNil(ratio(3, Array(repeating: 0, count: 7)))
        // A weekend-only window under the work-day model is the same case.
        let saturday = monday.addingTimeInterval(5 * day)
        XCTAssertNil(WeeklyPaceTimeline.elapsedRatio(
            start: saturday, now: saturday.addingTimeInterval(day), end: saturday.addingTimeInterval(2 * day),
            weights: WeeklyPaceTimeline.workDayWeights, calendar: utc))
        // Malformed inputs never throw.
        XCTAssertNil(WeeklyPaceTimeline.elapsedRatio(
            start: monday, now: monday, end: monday, weights: WeeklyPaceTimeline.workDayWeights, calendar: utc))
        XCTAssertNil(ratio(0, [1, 1]))
    }
}

final class PaceClassifierTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_785_412_800) // 2026-07-30 12:00 UTC
    private let utc = utcCalendar()

    override func setUp() {
        L10n.configure(.korean)
    }

    private func window(_ used: Double, elapsed: Double) -> UsageWindow {
        let duration = 7 * day
        return UsageWindow(type: .weekly, usedRatio: used,
                           resetTime: now.addingTimeInterval(duration * (1 - elapsed)), duration: duration)
    }

    private func weekWindow(_ used: Double) -> UsageWindow {
        UsageWindow(type: .weekly, usedRatio: used, resetTime: nextMonday, duration: 7 * day)
    }

    func testShowsSignedDifferenceFromItsOwnCycle() {
        let cases: [(Double, Double, String?)] = [
            (0.60, 0.25, "−35% · 빠르게 소진 중"), (0.42, 0.25, "−17% · 빠르게 소진 중"),
            (0.32, 0.25, "−7% · 빠르게 소진 중"), (0.20, 0.45, "+25% · 여유 있음"),
            (0.25, 0.25, "0% · 적정"), (0.251, 0.25, "0% · 적정"), (0, 0.25, "+25% · 여유 있음"),
            (0.99, 0.25, "−74% · 빠르게 소진 중"),
            // Exhausted windows drop the caption: "burning fast" beside 100% adds nothing.
            (1, 0.25, nil), (1.04, 0.25, nil),
        ]
        for (used, elapsed, expected) in cases {
            XCTAssertEqual(PaceClassifier.caption(for: window(used, elapsed: elapsed), now: now), expected, "\(used)")
        }
    }

    func testExactlyThreePercentOpensGateForEveryDuration() {
        for hours in [5.0, 168, 720] {
            let duration = hours * 3600
            var window = UsageWindow(type: .weekly, usedRatio: 0.3,
                                     resetTime: now.addingTimeInterval(duration * 0.97), duration: duration)
            XCTAssertEqual(PaceClassifier.caption(for: window, now: now), "−27% · 빠르게 소진 중", "\(hours)h")
            window.resetTime = now.addingTimeInterval(duration * 0.97 + 0.001)
            XCTAssertNil(PaceClassifier.caption(for: window, now: now))
            window.resetTime = now.addingTimeInterval(duration)
            XCTAssertNil(PaceClassifier.caption(for: window, now: now))
        }
    }

    func testMissingOrInvalidCycleShowsNothing() {
        let base = window(0.8, elapsed: 0.25)
        var invalid: [UsageWindow] = []
        for duration in [nil, 0, -3600] as [TimeInterval?] { var w = base; w.duration = duration; invalid.append(w) }
        for reset in [nil, now, now.addingTimeInterval(-day), now.addingTimeInterval(8 * day)] as [Date?] {
            var w = base; w.resetTime = reset; invalid.append(w)
        }
        for ratio in [Double.nan, .infinity] { var w = base; w.usedRatio = ratio; invalid.append(w) }
        for window in invalid {
            for model in WeeklyPaceModel.allCases {
                XCTAssertNil(PaceClassifier.caption(for: window, now: now, model: model, calendar: utc))
            }
        }
    }

    func testWorkDayModelExpectsNothingOverTheWeekend() {
        // Saturday noon, 60% used. Evenly spread, 5.5/7 of the week is gone and the row is
        // only +19% ahead; on a Monday-to-Friday week the whole quota was expected by
        // Friday night, so the same reading is +40% in reserve.
        let window = weekWindow(0.6)
        let saturdayNoon = monday.addingTimeInterval(5.5 * day)
        XCTAssertEqual(PaceClassifier.caption(for: window, now: saturdayNoon, model: .uniform, calendar: utc), "+19% · 여유 있음")
        XCTAssertEqual(PaceClassifier.caption(for: window, now: saturdayNoon, model: .workDays, calendar: utc), "+40% · 여유 있음")
        // Sunday reads the same as Saturday: the expected curve is flat across the weekend.
        XCTAssertEqual(PaceClassifier.caption(
            for: window, now: monday.addingTimeInterval(6.5 * day), model: .workDays, calendar: utc), "+40% · 여유 있음")
    }

    func testWorkDayModelSpendsTheWeekFasterOnWeekdays() {
        // Wednesday 00:00, 50% used: two of five work days (40%) versus two of seven (29%).
        let window = weekWindow(0.5)
        let wednesday = monday.addingTimeInterval(2 * day)
        XCTAssertEqual(PaceClassifier.caption(for: window, now: wednesday, model: .uniform, calendar: utc), "−21% · 빠르게 소진 중")
        XCTAssertEqual(PaceClassifier.caption(for: window, now: wednesday, model: .workDays, calendar: utc), "−10% · 빠르게 소진 중")
    }

    func testShortWindowsIgnoreTheWeekdayModel() {
        // A 5-hour session on a Saturday afternoon: the work-day model would freeze its
        // expected curve at zero, so windows under two days always use the uniform spread.
        let start = monday.addingTimeInterval(5 * day + 13 * 3600)
        let window = UsageWindow(type: .fiveHour, usedRatio: 0.5, resetTime: start.addingTimeInterval(5 * 3600), duration: 5 * 3600)
        for model in WeeklyPaceModel.allCases {
            XCTAssertEqual(PaceClassifier.caption(
                for: window, now: start.addingTimeInterval(2.5 * 3600), model: model, calendar: utc), "0% · 적정")
        }
    }

    func testEarlySilenceGateIsWallClockUnderEveryModel() {
        // Monday 01:00 is under 3% of the week however the days are weighted, and Saturday
        // 00:00 is past it even though the work-day curve has already reached 100%.
        let window = weekWindow(0.2)
        XCTAssertNil(PaceClassifier.caption(for: window, now: monday.addingTimeInterval(3600), model: .workDays, calendar: utc))
        XCTAssertEqual(PaceClassifier.caption(
            for: window, now: monday.addingTimeInterval(5 * day), model: .workDays, calendar: utc), "+80% · 여유 있음")
    }

    func testAutomaticModelShapesTheCurveFromASufficientProfile() {
        // Monday noon, 25% used. A profile with three weeks of history that puts half the
        // week's consumption on Monday expects a quarter of the quota by Monday noon — on
        // pace — where the even spread expects 7% and calls it an 18% deficit.
        let window = weekWindow(0.25)
        let mondayNoon = monday.addingTimeInterval(12 * 3600)
        let profile = UsageWeekdayProfile(weights: [0, 3, 1, 1, 1, 0, 0], daysObserved: 21)
        XCTAssertEqual(PaceClassifier.caption(
            for: window, now: mondayNoon, model: .automatic, profile: profile, calendar: utc), "0% · 적정")
        XCTAssertEqual(PaceClassifier.caption(
            for: window, now: mondayNoon, model: .uniform, profile: profile, calendar: utc), "−18% · 빠르게 소진 중")
        // The profile is consulted by the automatic model only: the work-day model keeps its
        // own Monday-to-Friday weights (half a work day of five expects 10%).
        XCTAssertEqual(PaceClassifier.caption(
            for: window, now: mondayNoon, model: .workDays, profile: profile, calendar: utc), "−15% · 빠르게 소진 중")
    }

    func testAutomaticModelReadsAsUniformWithoutEnoughHistory() {
        let window = weekWindow(0.25)
        let mondayNoon = monday.addingTimeInterval(12 * 3600)
        let uniform = PaceClassifier.caption(for: window, now: mondayNoon, model: .uniform, calendar: utc)
        XCTAssertEqual(uniform, "−18% · 빠르게 소진 중")
        let insufficient: [UsageWeekdayProfile?] = [
            nil,
            UsageWeekdayProfile(weights: [0, 3, 1, 1, 1, 0, 0], daysObserved: UsageWeekdayProfile.minimumDaysObserved - 1),
            UsageWeekdayProfile(weights: Array(repeating: 0, count: 7), daysObserved: 30),
            UsageWeekdayProfile(weights: [1, 1], daysObserved: 30),
        ]
        for profile in insufficient {
            XCTAssertEqual(PaceClassifier.caption(
                for: window, now: mondayNoon, model: .automatic, profile: profile, calendar: utc), uniform)
        }
    }

    func testDefaultsKeepTheUniformCaption() {
        // The pre-model call shape still reads the uniform spread.
        let window = weekWindow(0.6)
        let saturdayNoon = monday.addingTimeInterval(5.5 * day)
        XCTAssertEqual(PaceClassifier.caption(for: window, now: saturdayNoon), "+19% · 여유 있음")
    }

    func testModelsHaveLocalizedNamesAndStableRawValues() {
        XCTAssertEqual(WeeklyPaceModel.allCases.map(\.rawValue), ["uniform", "workDays", "automatic"])
        XCTAssertEqual(WeeklyPaceModel.workDays.displayName, "근무일 (월–금)")
    }
}
