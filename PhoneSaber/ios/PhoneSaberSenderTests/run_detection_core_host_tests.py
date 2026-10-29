#!/usr/bin/env python3
"""DetectionCoreTestsの認識部分を実ソース・既存のassertionでmacOS上で実行する。

UIKit/カメラ/通信を含むiOS全suiteの代替ではない。テスト本体は変更せずに抽出し、
fixtureの読取だけをImageIOへ置き換える。認識のmockや期待値の変更は行わない。
"""
from pathlib import Path
import os
import re
import subprocess
import tempfile

TESTS = Path(__file__).resolve().parent
SOURCE = TESTS.parent / "PhoneSaberSender/PhoneSaberSender"
TEST_NAMES = """
testEndOnContinuitySurvivesUnrelatedEligibleOutsideTrackingGate
testEndOnContinuityUsesCurrentCandidateAndExpiresWithoutRenewal
testEndOnContinuityPreservesColorSafetyAndRejectsDistantObjects
testEndOnContinuityDoesNotBorrowShrinkingHistoryAcrossObjects
testCoreLineCandidateScoringIsIndependentOfPointOrder
testLowISOAcquisitionRetainsUnsaturatedDimAndShortBlades
testNewAcquisitionKeepsProtectedWhiteCoreRedBlade
testProductionVideoFixturesKeepBladeAxesAndRejectFalseCoreLines
testBrightRedAndBluePixels
testBrightColorDoesNotOverflowWithHighOtherChannels
testColorNeedsSaturationSoWhiteHighlightsAreIgnored
testDetectionFindsPrincipalAxisEndpoints
testMediumBrightnessElongatedReflectionIsRejectedForBothColors
testBrightGappedEmitterWinsAgainstLongerReflectionForBothColors
testBlueDiagonalBarSurvivesBlurAndShortNoise
testLargestColorClusterConnectsHorizontalNeighbors
testLargestColorClusterDoesNotWrapAtImageEdges
testBestColorClusterPrefersLongThinBarOverShortBlob
testProposalWorkspaceReusePreservesEveryCandidateField
testCandidateEndpointsUseDenseContinuousBodyInsteadOfAxialSpillAndReflection
testLongCoreLineGeometryPenaltyPrefersCompleteBladeEvidence
testForensicDiffuserFramesUseContinuousRobustBodyInsteadOfRawSpan
testPointLEDFixtureFallbackCharacteristics
testSecondForensicFallback
testScaleAndPayload
testTimestampedPayloadHasExplicitPrefix
testDiagonalBarSurvivesShortNoiseAndWhiteReflection
testAspectFillCentersAndCropsSource
testAspectFillCropsTopAndBottomForWideView
testRedAndBlueBarsWithBlurReflectionNoiseUseProductionSampleStep
testAspectFillCoordinatesIncreaseRightAndDown
testNormalDetectionDoesNotBuildRejectionSiteDiagnostics
testSparseBlueMaskSurvivesDestructiveOpeningThroughRawFallback
testNormalMaskDoesNotEnterSparseRawFallback
testBlueDiffuserColorModelRecoversMeasuredPaleAndBlurredPixels
testBlueDiffuserSupportExpandsOnlyNextToStrictBlue
testDiffusedBlueHaloIncreasesMaskWithoutChangingRedDetection
testRelaxedBlueBackgroundWithoutStrictAnchorIsNotDetected
testShutterEmitterKeepsDimShortAndPointingBladesAtNightAndDay
testShutterEmitterKeepsShortBlueBodyNearPixelFloor
testShutterEmitterPeakFloorKeepsNightDayAndRatioBoundaries
""".split()


def method(source, name):
    # 既存Swiftファイルの4-spaceインデントのメソッド境界を使う。見つからなければ失敗する。
    matches = re.findall(r"^    (?:private )?func " + re.escape(name) + r"\(.*?^    }", source, re.M | re.S)
    if len(matches) != 1:
        raise ValueError(f"expected exactly one method: {name}")
    return matches[0]


FIXTURE_ADAPTER = r'''
    private func fixtureBGRA(_ name: String, subdirectory: String? = nil) throws -> (bytes: [UInt8], width: Int, height: Int, bytesPerRow: Int) {
        var url = URL(fileURLWithPath: ProcessInfo.processInfo.environment["PHONESABER_TEST_FIXTURES"]!)
        if let subdirectory { url.appendPathComponent(subdirectory) }
        url.appendPathComponent(name + ".png")
        let source = try XCTUnwrap(CGImageSourceCreateWithURL(url as CFURL, nil), name)
        let image = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil), name)
        let width = image.width, height = image.height, bytesPerRow = width * 4
        var bytes = Array(repeating: UInt8(0), count: bytesPerRow * height)
        try bytes.withUnsafeMutableBytes { raw in
            let context = try XCTUnwrap(CGContext(data: raw.baseAddress!, width: width, height: height,
                bitsPerComponent: 8, bytesPerRow: bytesPerRow, space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue))
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        }
        return (bytes, width, height, bytesPerRow)
    }
'''


def main():
    if os.environ.get("PHONESABER_CORELINE_GUARD", "1") != "1":
        raise ValueError("受入gateと同じCoreLineGuard有効条件で実行する")
    source = (TESTS / "DetectionCoreTests.swift").read_text()
    methods = [method(source, name) for name in [*TEST_NAMES, "axisDistance", "endOnTestCandidate"]]
    developer = Path(subprocess.check_output(["xcode-select", "-p"], text=True).strip()) / "Platforms/MacOSX.platform/Developer"
    frameworks = developer / "Library/Frameworks"
    with tempfile.TemporaryDirectory(prefix="phonesaber-detection-core-") as directory:
        tmp = Path(directory)
        (tmp / "DetectionCoreTests.swift").write_text(
            "import XCTest\nimport Foundation\nimport CoreGraphics\nimport ImageIO\n"
            + "final class DetectionCoreTests: XCTestCase {\n" + "\n".join(methods) + FIXTURE_ADAPTER + "\n}\n")
        (tmp / "main.swift").write_text('''import XCTest
import Darwin
let suite = DetectionCoreTests.defaultTestSuite
suite.run()
guard let run = suite.testRun, run.executionCount == EXPECTED, run.hasSucceeded else { exit(1) }
'''.replace("EXPECTED", str(len(TEST_NAMES))))
        subprocess.run(["xcrun", "swiftc", "-O", str(SOURCE / "DetectionCore.swift"),
                        str(SOURCE / "BGRADetection.swift"), str(tmp / "DetectionCoreTests.swift"), str(tmp / "main.swift"),
                        "-I", str(developer / "usr/lib"), "-L", str(developer / "usr/lib"),
                        "-F", str(frameworks), "-Xlinker", "-rpath", "-Xlinker", str(frameworks),
                        "-Xlinker", "-rpath", "-Xlinker", str(developer / "usr/lib"),
                        "-module-cache-path", str(tmp / "module-cache"), "-o", str(tmp / "tests")], check=True)
        subprocess.run([str(tmp / "tests")], check=True,
                       env={**os.environ, "PHONESABER_TEST_FIXTURES": str(TESTS / "Fixtures")})


if __name__ == "__main__":
    main()
