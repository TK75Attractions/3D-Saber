"""UIKitを使わないFrameProcessorホストに、露出フレームと共通診断の実定義を渡す。"""
from pathlib import Path


def _block(text: str, marker: str) -> str:
    start = text.index(marker)
    end = text.index("{", start) + 1
    depth = 1
    while depth:
        depth += (text[end] == "{") - (text[end] == "}")
        end += 1
    return text[start:end]


def auto_iso_support(source: Path) -> str:
    camera = (source / "CameraViewModel.swift").read_text()
    clips = (source / "AutoClip.swift").read_text()
    # 録画・転送ホストはフレームの型と距離定数だけを使い、未使用の判定本体の最適化を省く。
    # 判定本体はrun_autoclip_host_testsの同じXCTestで検証する。
    exposure_frame = "struct AutoExposureController {\n" + "\n".join([
        next(line for line in camera.splitlines() if "static let distantEndpointDiagonalFraction" in line),
        _block(camera, "struct Color {"),
        _block(camera, "struct Frame {"),
    ]) + "\n}\n"
    return "\n".join([
        exposure_frame,
        _block(clips, "enum AutoClipTriggerPolicy"),
        _block(clips, "struct AutoClipCompetition"),
    ]) + "\n"
