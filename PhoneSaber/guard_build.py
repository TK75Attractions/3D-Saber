"""全 host harness 共通の guard ビルド設定（既定 ON、比較時だけ 0）。"""
import os


def enabled():
    value = os.environ.get("PHONESABER_CORELINE_GUARD", "1")
    if value not in ("0", "1"):
        raise ValueError("PHONESABER_CORELINE_GUARD must be 0 or 1")
    return value == "1"


def cpp_flags():
    return [] if enabled() else ["-DPHONESABER_DISABLE_CORELINE_GUARD"]


def swift_flags():
    return [] if enabled() else ["-D", "PHONESABER_DISABLE_CORELINE_GUARD"]
