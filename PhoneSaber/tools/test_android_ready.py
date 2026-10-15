import contextlib
from datetime import datetime, timezone
import io
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import android_ready


class AndroidReadyTests(unittest.TestCase):
    @staticmethod
    def events(at, age='0.1'):
        stamp = datetime.fromtimestamp(at, timezone.utc).isoformat(timespec='milliseconds').replace('+00:00', 'Z')
        return [f'{stamp} event=latency-estimate colour={colour} sender=phone platform=android '
                f'phone-age-s={age} pc-coordinate-age-s={age}' for colour in ('RED', 'BLUE')]

    def test_new_event_lines_cannot_certify_packets_cached_before_launch(self):
        # 新しい行でも受信は起動前。age の表示丸め境界も安全側に倒す。
        for lines in (self.events(101, '2.0'), self.events(102, '2.0'),
                      self.events(102.04, '2.0'), self.events(99, '0.1'),
                      [line.split(' ', 1)[1] for line in self.events(102)]):
            with self.subTest(lines=lines), mock.patch('time.time', return_value=100), \
                    mock.patch('time.monotonic', side_effect=[0, 1, 21]), mock.patch('time.sleep'), \
                    mock.patch('android_ready.new_events', return_value=lines):
                self.assertEqual(android_ready.wait_events(Path('/unused'), {}, {'phone'}, 20), set())

    def test_fresh_metrics_cannot_certify_prelaunch_coordinates(self):
        lines = [line.replace('pc-coordinate-age-s=0.1', 'pc-coordinate-age-s=2.0')
                 for line in self.events(101)]
        with mock.patch('time.time', return_value=100), \
                mock.patch('android_ready.new_events', return_value=lines), \
                mock.patch('time.monotonic', side_effect=[0, 1, 21]), mock.patch('time.sleep'):
            self.assertEqual(android_ready.wait_events(Path('/unused'), {}, {'phone'}, 20), {5007})

    def test_missing_or_incomplete_logcat_measurements_fail_after_bounded_wait(self):
        for log in ('', 'capture_fps=60', 'capture_fps=NaN'):
            with self.subTest(log=log), \
                    mock.patch('subprocess.run', side_effect=[mock.Mock(returncode=0, stdout='1792047620'),
                                                             mock.Mock(returncode=0, stdout=log)]), \
                    mock.patch('time.monotonic', side_effect=[0, 0, 13]), mock.patch('time.sleep'), \
                    contextlib.redirect_stdout(io.StringIO()):
                self.assertFalse(android_ready.print_logcat('adb', 'aquos'))

    def test_adb_prefers_android_home_and_falls_back_to_path(self):
        with tempfile.TemporaryDirectory() as temporary:
            adb = Path(temporary) / 'platform-tools/adb'
            adb.parent.mkdir()
            with mock.patch.dict(os.environ, {'ANDROID_HOME': temporary}):
                self.assertEqual(android_ready.adb_path(), 'adb')
                adb.touch()
                self.assertEqual(android_ready.adb_path(), str(adb))

    def test_device_requires_exactly_one_authorized_device(self):
        for listing, expected in [('List of devices attached\naquos\tdevice\n', 'aquos'),
                                  ('List of devices attached\naquos\tunauthorized\n', None),
                                  ('List of devices attached\na\tdevice\nb\tdevice\n', None),
                                  ('List of devices attached\na\toffline\n', None)]:
            with mock.patch('subprocess.run', return_value=mock.Mock(returncode=0, stdout=listing)):
                self.assertEqual(android_ready.device_id('adb'), expected)

    def test_coordinate_source_is_the_selected_phones_wifi_address(self):
        with mock.patch('subprocess.run', return_value=mock.Mock(returncode=0, stdout=
                        '24: wlan0    inet 192.168.1.42/24 brd 192.168.1.255 scope global wlan0\n')) as run:
            self.assertEqual(android_ready.phone_addresses('adb', 'aquos'), {'192.168.1.42'})
        run.assert_called_once_with(['adb', '-s', 'aquos', 'shell', 'ip', '-o', '-4', 'addr', 'show', 'wlan0'],
                                    capture_output=True, text=True)

    def test_running_game_is_detected_even_before_its_ports_are_bound(self):
        with mock.patch('subprocess.run', return_value=mock.Mock(returncode=0, stdout='123\n')) as run:
            self.assertTrue(android_ready.ports_in_use())
        self.assertEqual(run.call_count, 1)
        for stdout, expected in (('UDP listener', True), ('', False)):
            with mock.patch('subprocess.run', side_effect=[mock.Mock(returncode=1, stdout=''),
                                                         mock.Mock(returncode=0 if stdout else 1, stdout=stdout)]):
                self.assertEqual(android_ready.ports_in_use(), expected)
        with mock.patch('subprocess.run', return_value=mock.Mock(returncode=2, stderr='Cannot get process list')):
            with self.assertRaises(OSError):
                android_ready.ports_in_use()

    def test_udp_accepts_only_valid_packets_from_selected_phone_and_answers_discovery(self):
        sockets = {port: mock.Mock() for port in (5005, 5006, 5007)}
        packets = [(5005, b'1,2,3,4', 'other'), (5005, b'NaN,2,3,4', 'phone'),
                   (5007, b'PHONESABER_DISCOVER 1', 'phone'),
                   (5007, b'PHONESABER_METRICS 1 platform=ios', 'phone'),
                   (5005, b'1,2,3,4', 'phone'), (5006, b'5,6,7,8', 'phone'),
                   (5007, b'PHONESABER_METRICS 1 platform=android', 'phone')]
        def ready(readers, writers, errors, timeout):
            port, payload, host = packets.pop(0)
            sockets[port].recvfrom.return_value = (payload, (host, 1234))
            return [sockets[port]], [], []
        with mock.patch('android_ready.select.select', side_effect=ready):
            self.assertEqual(android_ready.wait_udp(sockets, {'phone'}, 20, 'A'), {5005, 5006, 5007})
        sockets[5007].sendto.assert_called_once()
        self.assertIn(b'station=A', sockets[5007].sendto.call_args.args[0])

    def test_udp_timeout_and_partial_bind_close_every_socket(self):
        first, second = mock.Mock(), mock.Mock()
        second.bind.side_effect = OSError('busy')
        with mock.patch('socket.socket', side_effect=[first, second]):
            with self.assertRaises(OSError):
                with android_ready.udp_receivers():
                    pass
        first.close.assert_called_once()
        second.close.assert_called_once()
        with mock.patch('time.monotonic', side_effect=[0, 21]):
            self.assertEqual(android_ready.wait_udp({}, {'phone'}, 20, 'A'), set())

    def test_events_require_fresh_coordinates_and_metrics_from_phone(self):
        lines = self.events(102)
        with mock.patch('time.time', return_value=100), mock.patch('android_ready.new_events', return_value=lines):
            self.assertEqual(android_ready.wait_events(Path('/unused'), {}, {'phone'}, 20), {5005, 5006, 5007})
        stale = [line.replace('phone-age-s=0.1', 'phone-age-s=30').replace('pc-coordinate-age-s=0.1',
                                                                                   'pc-coordinate-age-s=30') for line in lines]
        for bad in (stale, [line.replace('sender=phone', 'sender=other') for line in lines]):
            with mock.patch('time.time', return_value=100), mock.patch('android_ready.new_events', return_value=bad), \
                    mock.patch('time.monotonic', side_effect=[0, 1, 21]), mock.patch('time.sleep'):
                self.assertEqual(android_ready.wait_events(Path('/unused'), {}, {'phone'}, 20), set())

    def test_missing_metrics_or_either_colour_is_not_ready(self):
        for missing in (5005, 5006, 5007):
            present = {5005, 5006, 5007} - {missing}
            with mock.patch('android_ready.adb_path', return_value='adb'), \
                    mock.patch('android_ready.device_id', return_value='aquos'), \
                    mock.patch.object(Path, 'is_file', return_value=True), \
                    mock.patch('android_ready.phone_addresses', return_value={'phone'}), \
                    mock.patch('android_ready.ports_in_use', return_value=False), \
                    mock.patch('android_ready.udp_receivers') as udp, \
                    mock.patch('android_ready.wait_udp', return_value=present), \
                    mock.patch('subprocess.run', return_value=mock.Mock(returncode=0, stdout='Success', stderr='')), \
                    mock.patch('android_ready.print_logcat', return_value=True), \
                    mock.patch('sys.argv', ['android_ready.py']), contextlib.redirect_stdout(io.StringIO()) as output:
                self.assertEqual(android_ready.main(), 1)
            udp.assert_called_once()
            self.assertIn(f'UDP {missing} が20秒以内に届きません', output.getvalue())

    def test_events_ignore_existing_lines_and_follow_log_rotation(self):
        lines = '\n'.join(self.events(102)) + '\n'
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            events = folder / 'events.log'
            events.write_text(lines)
            cursor = android_ready.event_cursor(folder)
            with mock.patch('time.monotonic', side_effect=[0, 1, 21]), mock.patch('time.sleep'):
                self.assertEqual(android_ready.wait_events(folder, cursor, {'phone'}, 20), set())
            events.rename(folder / 'events.log.1')
            events.write_text(lines)
            with mock.patch('time.time', return_value=100):
                self.assertEqual(android_ready.wait_events(folder, cursor, {'phone'}, 20), {5005, 5006, 5007})

    def test_logcat_waits_for_postcheck_measurement_and_prints_stage_medians(self):
        log = ('capture_fps=59.5 analysis_fps=59.0 send_red_fps=59.0 send_blue_fps=59.0 '
               'capture_to_send_median_ms=12.6 sensor_to_analyzer_median_ms=8.1 '
               'analyzer_to_jni_end_median_ms=4.2 jni_end_to_send_median_ms=0.3 jni_median_ms=4.0')
        with mock.patch('subprocess.run', side_effect=[mock.Mock(returncode=0, stdout='1792047620'),
                                                     mock.Mock(returncode=0, stdout=''),
                                                     mock.Mock(returncode=0, stdout=log)]) as run, \
                mock.patch('time.sleep') as sleep, \
                contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertTrue(android_ready.print_logcat('adb', 'aquos'))
        sleep.assert_called_once_with(.5)
        self.assertEqual(run.call_args.args[0], ['adb', '-s', 'aquos', 'logcat', '-d', '-v', 'raw',
                                               '-T', '1792047621.000', 'PhoneSaberLatency:I', '*:S'])
        self.assertIn('capture_fps=59.5', output.getvalue())
        self.assertIn('analyzer_to_jni_end_median_ms=4.2', output.getvalue())

    def test_unknown_sensor_clock_still_accepts_complete_stream_and_processing_metrics(self):
        log = ('capture_fps=30.0 analysis_fps=30.0 send_red_fps=30.0 send_blue_fps=30.0 '
               'capture_to_send_median_ms=UNKNOWN sensor_to_analyzer_median_ms=UNKNOWN '
               'analyzer_to_jni_end_median_ms=4.2 jni_end_to_send_median_ms=0.3 jni_median_ms=4.0')
        with mock.patch('subprocess.run', side_effect=[mock.Mock(returncode=0, stdout='1792047620'),
                                                     mock.Mock(returncode=0, stdout=log)]), \
                mock.patch('time.monotonic', side_effect=[0, 0, 13]), mock.patch('time.sleep'), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertTrue(android_ready.print_logcat('adb', 'aquos'))
        self.assertIn('sensor_to_analyzer_median_ms=UNKNOWN', output.getvalue())

    def test_unknown_does_not_hide_missing_or_invalid_required_processing_metrics(self):
        log = ('capture_fps=30.0 analysis_fps=30.0 send_red_fps=30.0 send_blue_fps=30.0 '
               'capture_to_send_median_ms=UNKNOWN sensor_to_analyzer_median_ms=UNKNOWN '
               'analyzer_to_jni_end_median_ms=4.2 jni_end_to_send_median_ms=0.3 jni_median_ms=4.0')
        for key in ('capture_fps', 'analysis_fps', 'send_red_fps', 'send_blue_fps',
                    'analyzer_to_jni_end_median_ms', 'jni_end_to_send_median_ms', 'jni_median_ms',
                    'capture_to_send_median_ms', 'sensor_to_analyzer_median_ms'):
            original = android_ready.field(log, key)
            for bad in ('', 'NaN', '-1', 'inf', 'UNKNOWN'):
                if original == bad:
                    continue
                broken = log.replace(f'{key}={original}', f'{key}={bad}')
                with self.subTest(key=key, bad=bad), \
                        mock.patch('subprocess.run', side_effect=[mock.Mock(returncode=0, stdout='1792047620'),
                                                                 mock.Mock(returncode=0, stdout=broken)]), \
                        mock.patch('time.monotonic', side_effect=[0, 0, 13]), mock.patch('time.sleep'), \
                        contextlib.redirect_stdout(io.StringIO()):
                    self.assertFalse(android_ready.print_logcat('adb', 'aquos'))

    def test_game_path_never_opens_sockets_and_installs_before_launch(self):
        with tempfile.TemporaryDirectory() as temporary:
            apk = Path(temporary) / 'app-debug.apk'
            apk.touch()
            with mock.patch('android_ready.adb_path', return_value='adb'), \
                    mock.patch('android_ready.device_id', return_value='aquos'), \
                    mock.patch('android_ready.phone_addresses', return_value={'phone'}), \
                    mock.patch('android_ready.ports_in_use', return_value=True), \
                    mock.patch('android_ready.event_cursor', return_value={}), \
                    mock.patch('android_ready.wait_events', return_value={5005, 5006, 5007}), \
                    mock.patch('android_ready.udp_receivers') as udp, \
                    mock.patch('subprocess.run', return_value=mock.Mock(returncode=0, stdout='Success', stderr='')) as run, \
                    mock.patch('android_ready.print_logcat', return_value=True) as logcat, \
                    mock.patch('sys.argv', ['android_ready.py', '--apk', str(apk)]), \
                    contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(android_ready.main(), 0)
            udp.assert_not_called()
            commands = [call.args[0] for call in run.call_args_list]
            self.assertEqual(commands[0], ['adb', '-s', 'aquos', 'install', '-r', str(apk)])
            self.assertIn('--ez', commands[2])
            logcat.assert_called_once_with('adb', 'aquos')

    def test_failed_install_stops_before_launch(self):
        with tempfile.TemporaryDirectory() as temporary:
            apk = Path(temporary) / 'app-debug.apk'
            apk.touch()
            with mock.patch('android_ready.adb_path', return_value='adb'), \
                    mock.patch('android_ready.device_id', return_value='aquos'), \
                    mock.patch('subprocess.run', return_value=mock.Mock(returncode=1, stdout='', stderr='install failed')) as run, \
                    mock.patch('sys.argv', ['android_ready.py', '--apk', str(apk)]), \
                    contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(android_ready.main(), 1)
            self.assertEqual(run.call_count, 1)

    def test_temporary_receivers_open_after_launch_so_old_datagrams_are_not_queued(self):
        launched = False
        def launch(*args):
            nonlocal launched
            launched = True
            return True
        @contextlib.contextmanager
        def receivers():
            self.assertTrue(launched)
            yield {}
        with mock.patch('android_ready.adb_path', return_value='adb'), \
                mock.patch('android_ready.device_id', return_value='aquos'), \
                mock.patch.object(Path, 'is_file', return_value=True), \
                mock.patch('android_ready.phone_addresses', return_value={'phone'}), \
                mock.patch('android_ready.ports_in_use', return_value=False), \
                mock.patch('android_ready.launch', side_effect=launch), \
                mock.patch('android_ready.udp_receivers', side_effect=receivers), \
                mock.patch('android_ready.wait_udp', return_value={5005, 5006, 5007}), \
                mock.patch('android_ready.print_logcat', return_value=False), \
                mock.patch('subprocess.run', return_value=mock.Mock(returncode=0, stdout='Success')), \
                mock.patch('sys.argv', ['android_ready.py']), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(android_ready.main(), 1)

    def test_logcat_failure_is_reported(self):
        with mock.patch('subprocess.run', side_effect=[mock.Mock(returncode=0, stdout='1792047620'),
                                                     mock.Mock(returncode=1, stderr='permission denied')]), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertFalse(android_ready.print_logcat('adb', 'aquos'))
        self.assertIn('NG ログ', output.getvalue())


if __name__ == '__main__':
    unittest.main()
