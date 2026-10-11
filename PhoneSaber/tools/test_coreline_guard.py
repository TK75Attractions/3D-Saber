"""Swift unit と実診断の host build。C++ unit は make test で実行する。"""
from pathlib import Path
import json
import os
import subprocess
import sys
import tempfile
import unittest

PHONE = Path(__file__).resolve().parents[1]
TOOLS = PHONE / 'ios/PhoneSaberSender/Tools'
sys.path.insert(0, str(TOOLS))
sys.path.insert(0, str(PHONE))
import guard_build
from phone_saber_triage_protocol import pack_bundle


@unittest.skipUnless(sys.platform == 'darwin', 'Swift recorder host tests require macOS')
class CoreLineGuardTests(unittest.TestCase):
    def test_swift_guard_and_diagnostic_preflight(self):
        with tempfile.TemporaryDirectory(prefix='phonesaber-guard-tests-') as temp:
            root = Path(temp)
            source = PHONE / 'ios/PhoneSaberSender/PhoneSaberSender'
            camera = (source / 'CameraViewModel.swift').read_text()
            support = root / 'CameraIntervals.swift'
            support.write_text('import Foundation\n' + camera[camera.index('struct CameraFrameIntervalStatistics:'):
                camera.index('enum CameraLifecycleState:')] + '\nstruct DebugBundleTransfer {\n'
                '    static let shared = DebugBundleTransfer()\n'
                '    func enqueue(bundleURL: URL, automatic: Bool = true) {}\n}\n')
            binary = root / 'guard-test'
            command = ['xcrun', 'swiftc', *guard_build.swift_flags(), '-O', '-D', 'DEBUG',
                       '-D', 'CORELINE_GUARD_DIAGNOSTICS', '-module-cache-path', str(root/'cache')]
            command += [str(source / n) for n in ('DetectionCore.swift', 'BGRADetection.swift',
                'FrameProcessor.swift', 'DebugVideoRecorder.swift', 'DebugRecordingTriage.swift', 'GuidedRecording.swift')]
            command += [str(support), str(PHONE/'ios/PhoneSaberSenderTests/CoreLineGuardTests.swift'), '-o', str(binary)]
            compiled = subprocess.run(command, text=True, capture_output=True, timeout=180)
            self.assertEqual(compiled.returncode,0,compiled.stderr)
            result = subprocess.run([str(binary),str(root/'recording')], text=True,
                                    capture_output=True, timeout=90,
                                    env={**os.environ,'SWIFT_DETERMINISTIC_HASHING':'1'})
            self.assertEqual(result.returncode,0,result.stdout+result.stderr)
            bundle = Path(next(line.split(': ',1)[1] for line in result.stdout.splitlines()
                               if line.startswith('Guard diagnostics bundle: ')))
            # 本物の transport preflight は32 KiB超過を拒否する。上限の変更なし。
            pack_bundle(bundle,root/'envelope.bin')
            contexts = list((bundle/'frames').glob('*.json'))
            self.assertTrue(contexts)
            found = []
            sizes = []
            for path in contexts:
                sizes.append(path.stat().st_size)
                self.assertLessEqual(path.stat().st_size,32*1024)
                context = json.loads(path.read_text())
                for frame in context['frames']:
                    for trace in frame.get('red',{}).get('candidateDecisionTrace',[]):
                        if 'coreLineGuard' in trace:
                            found.append(trace['coreLineGuard'])
                            self.assertIn('core-line-overlinked',trace['rejectionReasons'])
            self.assertTrue(found,'supporting snapshot must survive candidateDecisionTrace compaction')
            self.assertTrue(all(v['supportingIndex']==1 and v['supportingSource']=='core-halo'
                                and v['supportingEndpoints']==[0,40,38,40] for v in found))
            print(f'Guard diagnostics: contexts={len(contexts)}, max_bytes={max(sizes)}, 32KB preflight PASS')
