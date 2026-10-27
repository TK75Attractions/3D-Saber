#!/usr/bin/env python3
"""学習適用とACK処理の実コードを.NETで計数する（Unity実行の代替ではない）。

InputPointの学習適用・保存/リセット入口、ACK store、受信ループのログ分岐、
TutorialControllerのラッパーをソースから抽出する。Unityの保存・送信・変換境界は
同ディレクトリのC#計数用スタブへ接続する。--sourcesで修正前の2ファイルを指定可能。
"""
from __future__ import annotations

import argparse
import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
INPUT = Path("Assets/Scripts/Managers/Inputsystem/Input")


def method(source: str, signature: str) -> str:
    start = source.index(signature)
    opening = source.index("{", start)
    depth = 1
    end = opening + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sources", type=Path, default=ROOT / INPUT)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--expect", choices=("before", "after"), default="after")
    parser.add_argument("--ack-tests", action="store_true", help="既存の純粋ACK NUnitテストもホスト実行")
    parser.add_argument("--unity", type=Path, default=Path("/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app"))
    args = parser.parse_args()
    output = args.output or Path(tempfile.mkdtemp(prefix="phonesaber-area-learning-"))
    output.mkdir(parents=True, exist_ok=True)
    responder = (args.sources / "PhoneSaberDiscoveryResponder.cs").read_text()
    input_point = (args.sources / "InputPoint.cs").read_text()
    tutorial = (ROOT / "Assets/Scripts/Game/TutorialController.cs").read_text()
    headers = "using System; using System.Linq; using System.Collections.Generic; using System.Threading; using System.Text; using System.Globalization;\n"
    ack = responder[responder.index("public sealed class PhoneSaberAcknowledgement\n"):responder.index("// 台設定は main thread")]
    (output / "Ack.cs").write_text(headers + ack)
    start = input_point.index("    // ACK の適用も保存も main thread")
    apply = input_point[start:input_point.index("    void Update()", start)]
    save = input_point[input_point.index("    public bool SavePositionCalibration("):input_point.index("    // スレッド同期用")]
    (output / "Input.cs").write_text("using System; using UnityEngine;\npublic partial class InputPoint {\n" + apply + save + "}\n")
    start = responder.index("if (Acknowledgements.Receive(ack, from")
    receive = responder[start:responder.index("\n                }\n                else if (length", start)]
    commands = responder[responder.index("    public const int LearnAreaSeconds"):responder.index("    // fields:")]
    commands += next(line for line in responder.splitlines() if "public const double CommandSeconds =" in line)
    summary = method(responder, "    static void RecordSuppressedAcknowledgements()") if "static void RecordSuppressedAcknowledgements()" in responder else ""
    generated = headers + "public static partial class PhoneSaberDiscoveryResponder {\n" + commands + summary
    generated += "\npublic static void ReceiveAndRecord(PhoneSaberAcknowledgement ack, string from) {\n" + receive + "\n}}\n"
    generated += "public static class TutorialController {\n" + method(tutorial, "    public static bool ApplyLearnedRange(") + "\n}"
    (output / "Generated.cs").write_text("using UnityEngine;\n" + generated)
    (output / "Harness.cs").write_text(Path(__file__).with_suffix(".cs").read_text())
    references = ""
    if args.ack_tests:
        nunit = args.unity / "Contents/Resources/PackageManager/BuiltInPackages/com.unity.ext.nunit/net40/unity-custom/nunit.framework.dll"
        references = f'<DefineConstants>ACK_TESTS</DefineConstants>'
        (output / "AckTests.cs").write_text((ROOT / "Assets/Tests/Editor/PhoneSaberAcknowledgementTests.cs").read_text())
        keys = (ROOT / INPUT / "PhoneSaberOperatorKeys.cs").read_text()
        constants = "\n".join(line for line in keys.splitlines() if "public const string" in line)
        pure = keys[keys.index("    public static string PendingLabel("):keys.index("    void RefreshToast()")]
        (output / "Operator.cs").write_text(headers + "public static class PhoneSaberOperatorKeys {" + constants + method(keys, "    public static string ShutterISOLabel(") + pure + "}")
        references += f'</PropertyGroup><ItemGroup><Reference Include="nunit.framework"><HintPath>{nunit}</HintPath></Reference></ItemGroup><PropertyGroup>'
    else:
        for name in ("AckTests.cs", "Operator.cs"):
            (output / name).unlink(missing_ok=True)
    (output / "measure.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><NoWarn>0169;0414</NoWarn>' + references + '</PropertyGroup></Project>')
    env = dict(os.environ, DOTNET_CLI_HOME=str(output), DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")
    subprocess.run(["dotnet", "run", "--project", str(output / "measure.csproj"), "-p:UseSharedCompilation=false", "--", args.expect], env=env, check=True)
    print(f"harness: {output}")


if __name__ == "__main__":
    main()
