import XCTest
@testable import PotionCore

final class PotionCoreTests: XCTestCase {
    func testVersionIsSemver() {
        let parts = PotionCore.version.split(separator: ".")
        XCTAssertEqual(parts.count, 3)
        XCTAssertTrue(parts.allSatisfy { Int($0) != nil })
    }
}
