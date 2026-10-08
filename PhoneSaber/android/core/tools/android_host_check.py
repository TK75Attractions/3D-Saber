#!/usr/bin/env python3
"""Mac/Linux host JNI + JVM checks; no Android framework, Gradle daemon or emulator.

Uses the Android Studio JBR and locally cached Kotlin/JUnit jars. The benchmark
includes RotationHelper/native rotation, JNI, recognition and result creation,
but excludes CameraX and UDP. Example (from the repository root):
  python3 PhoneSaber/android/core/tools/android_host_check.py --benchmark PNG [PNG ...]
"""
import argparse
import os
from pathlib import Path
import subprocess
import shutil
import sys
import tempfile

ANDROID = Path(__file__).resolve().parents[2]
JAVA = ANDROID / "app/src/main/java/jp/phonesaber/sender"
CORE = ANDROID / "core"


def run(args):
    subprocess.run(list(map(str, args)), check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--benchmark", nargs="*", type=Path, default=[])
    args = parser.parse_args()
    jbr = Path(os.environ.get("JAVA_HOME", "/Applications/Android Studio.app/Contents/jbr/Contents/Home"))
    cache = Path(os.environ.get("GRADLE_USER_HOME", Path.home() / ".gradle")) / "caches/modules-2/files-2.1"

    def jar(group, name, version):
        matches = list((cache / group / name / version).glob(f"*/{name}-{version}.jar"))
        if len(matches) != 1:
            raise RuntimeError(f"Need locally cached {group}:{name}:{version} under {cache}")
        return matches[0]

    stdlib = jar("org.jetbrains.kotlin", "kotlin-stdlib", "2.2.10")
    junit = jar("junit", "junit", "4.13.2")
    hamcrest = jar("org.hamcrest", "hamcrest-core", "1.3")
    annotations = jar("org.jetbrains", "annotations", "13.0")
    compiler = [jar("org.jetbrains.kotlin", name, "2.2.10") for name in
                ("kotlin-compiler-embeddable", "kotlin-reflect", "kotlin-script-runtime", "kotlin-daemon-embeddable")]
    compiler += [stdlib, annotations, jar("org.jetbrains.kotlinx", "kotlinx-coroutines-core-jvm", "1.8.0")]
    with tempfile.TemporaryDirectory(prefix="android-perf-host-") as work:
        work = Path(work)
        lib = work / ("libphonesaber_jni.dylib" if sys.platform == "darwin" else "libphonesaber_jni.so")
        jni_include = jbr / "include"
        if not (jni_include / "jni.h").exists():
            # JBR は JNI 開発ヘッダを含まない。VM 非依存の NDK jni.h だけを借りる。
            sdk = Path(os.environ.get("ANDROID_HOME", Path.home() / "Library/Android/sdk"))
            headers = sorted((sdk / "ndk").glob("*/toolchains/llvm/prebuilt/*/sysroot/usr/include/jni.h"))
            if not headers:
                raise RuntimeError("Need JDK JNI headers or an installed Android NDK")
            jni_include = work / "jni"
            jni_include.mkdir()
            shutil.copyfile(headers[-1], jni_include / "jni.h")
        run(["clang++", "-std=c++17", "-O2", "-Wall", "-Wextra", "-Werror", "-ffp-contract=off", "-shared", "-fPIC",
             "-I" + str(jni_include), "-I" + str(jni_include / ("darwin" if sys.platform == "darwin" else "linux")),
             "-I" + str(CORE / "include"), *(CORE / "src" / name for name in ("detection.cpp", "pipeline.cpp", "frame_processor.cpp")),
             ANDROID / "app/src/main/cpp/phonesaber_jni.cpp", ANDROID / "app/src/androidTest/cpp/rotation_probe.cpp", "-o", lib])
        tests = sorted((ANDROID / "app/src/test/java/jp/phonesaber/sender").glob("*Test.kt"))
        tests.append(ANDROID / "app/src/androidTest/java/jp/phonesaber/sender/NativeRotationTest.kt")
        sources = [JAVA / (name + ".kt") for name in ("RotationHelper", "NativeCore", "Protocol", "MirrorSettings",
                   "DeviceHealth", "EventRecovery", "SendingNetworkPolicy")]
        cp = os.pathsep.join(map(str, [stdlib, junit, hamcrest, annotations]))
        run([jbr / "bin/java", "-cp", os.pathsep.join(map(str, compiler)), "org.jetbrains.kotlin.cli.jvm.K2JVMCompiler",
             "-no-stdlib", "-no-reflect", "-jvm-target", "17", "-classpath", cp, "-d", work / "classes",
             *sources, *tests, CORE / "tools/rotation_benchmark.kt"])
        java = [jbr / "bin/java", "-Djava.library.path=" + str(work), "-cp", str(work / "classes") + os.pathsep + cp]
        run([*java, "org.junit.runner.JUnitCore", *("jp.phonesaber.sender." + t.stem for t in tests)])
        if args.benchmark:
            run(["make", "-C", CORE, "all"])
            for png in args.benchmark:
                # Raw lossless fixture as a 640x480 camera plane whose 90-degree result is the original.
                # Use the existing decoder without color management. Padding and decoding are outside timing.
                import struct
                png = png.resolve()
                width, height = struct.unpack(">II", png.read_bytes()[16:24])
                bgra = subprocess.check_output(["/tmp/phonesaber-cpp-core/phonesaber-png", "--decode-bgra", str(png)])
                camera_width, camera_height = height, width
                stride = camera_width * 4 + 16
                raw = bytearray(stride * camera_height)
                for y in range(camera_height):
                    for x in range(camera_width):
                        src = (x * width + width - 1 - y) * 4
                        dst = y * stride + x * 4
                        b, g, r, a = bgra[src:src + 4]
                        raw[dst:dst + 4] = bytes((r, g, b, a))
                plane = work / "camera.rgba"
                plane.write_bytes(raw)
                print(f"Benchmark fixture={png.name}", flush=True)
                run([*java, "jp.phonesaber.sender.Rotation_benchmarkKt", plane, camera_width, camera_height, stride])


if __name__ == "__main__":
    main()
