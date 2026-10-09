#!/usr/bin/env python3
"""Count process syscalls and explicit sender dispatch hops with the Mac harness."""
import argparse
import pathlib
import shutil
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=pathlib.Path, default=ROOT)
    parser.add_argument('--harness-root', type=pathlib.Path, default=ROOT / 'PhoneSaber/tools/ios-hot-path')
    parser.add_argument('--revision')
    parser.add_argument('--frames', type=int, default=240)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='phonesaber-syscalls-') as directory:
        temporary = pathlib.Path(directory)
        for name in ['run.py', 'Harness.swift', 'allocations.c']:
            shutil.copyfile(args.harness_root / name, temporary / name)
        counters = temporary / 'allocations.c'
        with counters.open('a') as output:
            output.write('''
#include <mach/mach.h>
static _Atomic uint64_t hops;
static _Atomic uint64_t delayed;
void ps_count_delayed(void) { atomic_fetch_add_explicit(&delayed, 1, memory_order_relaxed); }
uint64_t ps_delayed(void) { return atomic_load_explicit(&delayed, memory_order_relaxed); }
void ps_count_hop(void) { atomic_fetch_add_explicit(&hops, 1, memory_order_relaxed); }
uint64_t ps_hops(void) { return atomic_load_explicit(&hops, memory_order_relaxed); }
static struct task_events_info events(void) {
    struct task_events_info info = {0};
    mach_msg_type_number_t size = TASK_EVENTS_INFO_COUNT;
    if (task_info(mach_task_self(), TASK_EVENTS_INFO, (task_info_t)&info, &size) != KERN_SUCCESS) __builtin_trap();
    return info;
}
uint64_t ps_unix_calls(void) { return events().syscalls_unix; }
uint64_t ps_mach_calls(void) { return events().syscalls_mach; }
''')
        harness = temporary / 'Harness.swift'
        text = harness.read_text()
        text = text.replace('@main\nstruct HotPathHarness', '''
@_silgen_name("ps_count_hop") func countHop()
@_silgen_name("ps_hops") func hopCount() -> UInt64
@_silgen_name("ps_count_delayed") func countDelayed()
@_silgen_name("ps_delayed") func delayedCount() -> UInt64
@_silgen_name("ps_unix_calls") func unixCalls() -> UInt64
@_silgen_name("ps_mach_calls") func machCalls() -> UInt64
extension DispatchQueue {
    func countedAsyncAfter(deadline: DispatchTime, execute item: DispatchWorkItem) {
        countDelayed()
        asyncAfter(deadline: deadline, execute: item)
    }
    func countedAsync(_ work: @escaping @Sendable () -> Void) {
        countHop()
        async(execute: work)
    }
}
@main
struct HotPathHarness''')
        text = text.replace('        let allocations = allocationCount()', '        let allocations = allocationCount()\n        let unix = unixCalls(), mach = machCalls(), hops = hopCount(), delayed = delayedCount()')
        text = text.replace('        let count = allocationCount() - allocations', '        let callsUnix = unixCalls() - unix, callsMach = machCalls() - mach, hopsUsed = hopCount() - hops, delayedUsed = delayedCount() - delayed\n        let count = allocationCount() - allocations')
        text = text.replace('        print(String(format:', '        print(String(format: "%@ UNIX/frame=%.2f Mach/frame=%.2f senderAsync/frame=%.2f delayedWork/frame=%.2f", name, Double(callsUnix) / Double(frames), Double(callsMach) / Double(frames), Double(hopsUsed) / Double(frames), Double(delayedUsed) / Double(frames)))\n        print(String(format:', 1)
        text = text.replace('        allocationStart()', '''        let calibration = unixCalls()
        for _ in 0..<100 { _ = getpgid(0) }
        precondition(unixCalls() - calibration >= 100, "kernel syscall counter is unavailable")
        print("TASK_EVENTS_INFO calibration: 100 getpgid calls observed")
        allocationStart()''')
        text = text.replace('        measure("LAN-route+NW-send"', '        measure("idle-with-receivers+pings", frames: frames) { _ in }\n        measure("LAN-route+NW-send"', 1)
        harness.write_text(text)
        runner = temporary / 'run.py'
        text = runner.read_text()
        text = text.replace('copied.write_text(read_source(name))', '''instrumented = read_source(name)
            if name in ["UDPSender.swift", "P2PSender.swift"]:
                instrumented = instrumented.replace(".async {", ".countedAsync {").replace(".asyncAfter(", ".countedAsyncAfter(")
            copied.write_text(instrumented)''')
        runner.write_text(text)
        command = ['python3', str(runner), '--source-root', str(args.source_root), '--frames', str(args.frames)]
        if args.revision:
            command += ['--revision', args.revision]
        subprocess.run(command, check=True)


if __name__ == '__main__':
    main()
