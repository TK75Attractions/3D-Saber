"""Japanese one-line explanations for analysis precheck failures (display only).

The precheck itself (phone_saber_tracking_diagnostics.tracking_preflight and
input_failure) is unchanged; this only turns its reason codes into text an
operator can act on. Used by the receiver log and phone_saber_status.py.
Standard library only, so the status check can import it cheaply.
"""

from __future__ import annotations

import re
from typing import Iterable

# Codes that all say "the selected frames have no usable before/peak/after history".
_TEMPORAL = {
    "temporalEvidenceMissing", "candidateHistoryMissing", "endpointHistoryMissing",
    "endpointPathHistoryMissing", "trackingTimelineMissing", "trackingAssessmentDataMissing",
    "eventCenterMissing", "temporalOrderInvalid", "timestampMissing",
}

REASON_TEXT = {
    "temporalEvidenceMissing": "動きの前後(before/peak/after)の連続フレームがない",
    "candidateHistoryMissing": "候補の履歴がない",
    "endpointHistoryMissing": "端点の履歴がない",
    "endpointPathHistoryMissing": "端点の経路の履歴がない",
    "trackingTimelineMissing": "tracking の時系列がない",
    "trackingAssessmentDataMissing": "tracking 判定のデータがない",
    "eventCenterMissing": "イベント中心のフレームがない",
    "temporalOrderInvalid": "フレームの順序がおかしい",
    "timestampMissing": "フレームの時刻がない",
    "frameMappingMissing": "フレーム context が読めない、または上限を超えている",
    "imageFileMissing": "選ばれた PNG が bundle にない",
    "imageFileInvalid": "選ばれた画像が PNG ではない",
    "inputContractInvalid": "bundle の形式が解析の入力条件を満たさない",
}

_SIZE_LIMIT = re.compile(r"frame context exceeds its size limit")


def explain_precheck(codes: Iterable[str], detail: str = "") -> str:
    """One Japanese line: what is missing, why it usually happens, what to do."""
    codes = sorted(set(codes))
    parts = [REASON_TEXT.get(code, code) for code in codes]
    what = "、".join(parts) if parts else "理由不明"
    if _SIZE_LIMIT.search(detail):
        action = ("フレーム context が Codex 入力の上限(32KB)を超えている。古い iPhone ビルドで撮った録画に多い。"
                  "iPhone アプリを最新にして撮り直す(上限は緩めない)")
    elif codes and set(codes) <= _TEMPORAL:
        action = ("録画が短すぎるか、動きのイベントが選ばれていない。saber を映して数秒以上振る録画を撮り直す")
    else:
        action = "bundle は保存済み。詳細は analysis_report.json / 受信ログの PRECHECK_FAILED 行を見る"
    return f"解析は中止(Codex は呼んでいない): {what}。{action}"
