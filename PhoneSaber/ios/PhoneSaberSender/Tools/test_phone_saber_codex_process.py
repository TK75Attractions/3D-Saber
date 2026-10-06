"""Failure captures use real fake executables, including timeout partial output."""
from __future__ import annotations

import contextlib
import io
import json
import os
import random
import re
import stat
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import phone_saber_codex_process as process
from phone_saber_triage_codex import _output_schema, analyze_bundle, CodexFailed, CodexUnavailable
from test_phone_saber_triage_codex import fake_codex, write_codex_bundle


class CodexProcessTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.logs = self.root / "logs"
        self.addCleanup(patch.stopall)
        patch.object(process, "LOG_DIR", self.logs).start()
        # These tests read captures from self.logs, so the test-wide override must not apply.
        patch.dict(os.environ).start()
        os.environ.pop(process.LOG_DIR_ENV, None)

    def executable(self, body: str) -> Path:
        binary = self.root / "codex"
        binary.write_text(f"#!{sys.executable}\nimport sys, json, time\n"
                          "if sys.argv[1:] == ['--version']: print('codex-cli 0.test'); raise SystemExit(0)\n" + body)
        binary.chmod(0o700)
        return binary

    def run_cli(self, body: str, *, timeout: float = 5):
        binary = self.executable(body)
        return process.run_codex([str(binary), "exec", "--json", "--model", "gpt-6-luna",
                                  "-c", 'model_reasoning_effort="max"', "--sandbox", "read-only", "-"],
                                 cwd=self.root, prompt="Reply with OK.", model="gpt-6-luna",
                                 effort="max", timeout=timeout)

    def failure(self, body: str, code: str = "CLI_FAILED", **kwargs):
        with self.assertRaises(process.CodexProcessError) as raised:
            self.run_cli(body, **kwargs)
        error = raised.exception
        self.assertEqual(error.code, code)
        self.assertNotEqual(str(error).strip().splitlines()[-1], "}")
        record = json.loads(error.record.log_path.read_text())
        for field in ("exit_code", "command", "stdout", "stderr", "errors", "cli_version",
                      "model", "reasoning_effort", "working_directory", "timestamp", "elapsed_seconds"):
            self.assertIn(field, record)
        self.assertEqual(stat.S_IMODE(error.record.log_path.stat().st_mode), 0o600)
        self.assertEqual(record["cli_version"], "codex-cli 0.test")
        return error, record

    def test_cli_success(self):
        run = self.run_cli("print('OK')\n")
        self.assertEqual(run.completed.returncode, 0)
        self.assertEqual(run.diagnostic["stdout"], "OK\n")
        self.assertEqual(json.loads(run.log_path.read_text())["error_code"], "")

    def test_exit_one_stderr_preserved(self):
        error, record = self.failure("print('connection refused\\n}', file=sys.stderr)\nraise SystemExit(1)\n")
        self.assertIn("connection refused", str(error))
        self.assertEqual(record["exit_code"], 1)
        self.assertIn("connection refused", record["stderr"])

    def test_pretty_json_error_with_noise_and_duplicate(self):
        event = {"type": "error", "error": {"message": "Missing tracking_assessment", "type": "invalid_request_error", "code": "invalid_json_schema"}}
        error, record = self.failure(f"print('warning\\nERROR: ' + json.dumps({event!r}, indent=2), file=sys.stderr)\nraise SystemExit(1)\n", "INVALID_JSON_SCHEMA")
        self.assertEqual(record["errors"], [event["error"]])
        self.assertIn("Missing tracking_assessment", str(error))

    def test_jsonl_event_survives_large_tail_and_stderr_warning(self):
        event = {"type": "turn.failed", "error": {"message": "service exploded", "type": "server_error", "code": "server_error"}}
        error, record = self.failure(f"print(json.dumps({event!r}))\nprint(json.dumps({{'type':'turn.completed'}}))\nprint('x' * 20000, file=sys.stderr)\nraise SystemExit(1)\n")
        self.assertIn("service exploded", str(error))
        self.assertEqual(record["errors"][0]["code"], "server_error")
        self.assertEqual(len(record["stderr"]), 20001)

    def test_jsonl_message_wraps_pretty_json_api_error(self):
        payload = {"type": "error", "error": {"type": "invalid_request_error",
                   "code": "invalid_json_schema", "message": "Missing tracking_assessment"}}
        event = {"type": "error", "message": json.dumps(payload, indent=2)}
        error, record = self.failure(f"print(json.dumps({event!r}))\nraise SystemExit(1)\n", "INVALID_JSON_SCHEMA")
        self.assertEqual(record["errors"], [payload["error"]])
        self.assertIn("Missing tracking_assessment", str(error))

    def test_analysis_missing_executable_saves_failure(self):
        bundle = self.root / "bundle"
        write_codex_bundle(bundle)
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaisesRegex(CodexUnavailable, "EXECUTABLE_MISSING"):
            analyze_bundle(bundle, codex_path=str(self.root / "missing"))
        record = json.loads(next(self.logs.glob("*.json")).read_text())
        self.assertEqual(record["error_code"], "EXECUTABLE_MISSING")

    def test_malformed_output_human_readable(self):
        error, record = self.failure("print('{ broken error payload')\nraise SystemExit(1)\n")
        self.assertIn("broken error payload", str(error))
        self.assertEqual(record["errors"], [])

    def test_model_unavailable_no_retry(self):
        error, record = self.failure("print(json.dumps({'type':'error','message':'model gpt-6-luna is not available','code':'model_not_found'}))\nraise SystemExit(1)\n", "MODEL_UNAVAILABLE")
        self.assertIn("MODEL_UNAVAILABLE", str(error))
        self.assertEqual(record["model"], "gpt-6-luna")
        self.assertEqual(record["reasoning_effort"], "max")

    def test_auth_usage_permission_and_effort_codes(self):
        for message, expected in (("invalid_api_key 401", "AUTHENTICATION_FAILED"),
                                  ("usage limit reached", "USAGE_LIMIT"),
                                  ("permission_denied 403", "PERMISSION_DENIED"),
                                  ("unsupported reasoning effort max", "MODEL_UNAVAILABLE")):
            with self.subTest(message=message):
                self.failure(f"print({message!r}, file=sys.stderr)\nraise SystemExit(1)\n", expected)

    def test_timeout_keeps_partial_streams(self):
        # The fake CLI must start and flush its partial output before the
        # timeout fires; 0.1 s was shorter than a Python start-up on a loaded
        # host. 2 s still times out well before the 10 s sleep ends.
        error, record = self.failure("print('partial stdout', flush=True)\nprint('partial stderr', file=sys.stderr, flush=True)\ntime.sleep(10)\n", "CLI_TIMEOUT", timeout=2)
        self.assertIsNone(record["exit_code"])
        self.assertIn("partial stdout", str(error))
        self.assertIn("partial stderr", str(error))

    def test_executable_missing(self):
        with self.assertRaises(process.CodexProcessError) as raised:
            process.run_codex([str(self.root / "missing"), "exec"], cwd=self.root,
                              prompt="OK", model="gpt-6-luna", effort="max", timeout=1)
        self.assertEqual(raised.exception.code, "EXECUTABLE_MISSING")
        self.assertIn("No such file", str(raised.exception))
        self.assertTrue(raised.exception.record.log_path.is_file())

    def test_secrets_removed_before_display_and_persistence(self):
        secret = "private-secret-from-env-123"
        auth_secret = "private-auth-value-456"
        home = self.root / "home"
        home.mkdir()
        (home / "auth.json").write_text(json.dumps({"tokens": {"access_token": auth_secret}}))
        with patch.dict(os.environ, {"OPENAI_API_KEY": secret, "CODEX_HOME": str(home)}):
            error, record = self.failure(f"print({secret!r})\nprint({auth_secret!r})\nprint('Authorization: Bearer abcdef\\nCookie: sid=abc; other=def\\napi_key=literal-key-789\\nsk-fake123456\\neyJfake.jwt.signature', file=sys.stderr)\nraise SystemExit(1)\n")
        combined = str(error) + error.record.log_path.read_text()
        for value in (secret, auth_secret, "abcdef", "sid=abc", "other=def", "literal-key-789", "sk-fake123456", "eyJfake.jwt.signature"):
            self.assertNotIn(value, combined)
        self.assertIn("REDACTED", combined)

    def test_command_literal_credential_removed(self):
        binary = self.executable("print('failed', file=sys.stderr)\nraise SystemExit(1)\n")
        with self.assertRaises(process.CodexProcessError) as raised:
            process.run_codex([str(binary), "exec", "--api-key", "literal-private-command-key"],
                              cwd=self.root, prompt="OK", model="gpt-6-luna", effort="max", timeout=5)
        capture = raised.exception.record
        self.assertNotIn("literal-private-command-key", str(raised.exception) + capture.log_path.read_text())

    def test_every_wire_object_requires_all_properties(self):
        def check(value):
            if isinstance(value, dict):
                if value.get("type") == "object":
                    self.assertEqual(set(value["required"]), set(value["properties"]))
                    self.assertFalse(value["additionalProperties"])
                for child in value.values():
                    check(child)
            elif isinstance(value, list):
                for child in value:
                    check(child)
        check(_output_schema(("image_001",)))

    def test_invalid_final_report_keeps_subprocess_capture(self):
        bundle = self.root / "bundle"
        write_codex_bundle(bundle)
        binary = fake_codex(self.root, self.root / "spy.json", analysis={"not": "a report"})
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaisesRegex(CodexFailed, "MALFORMED_OUTPUT"):
            analyze_bundle(bundle, codex_path=str(binary))
        records = [json.loads(path.read_text()) for path in self.logs.glob("*.json")]
        self.assertEqual(records[0]["error_code"], "MALFORMED_OUTPUT")
        self.assertIn("invalid structured output", records[0]["output_error"])

    # Decodable JSON deeper than Python's recursion limit must still leave a saved log.
    def test_failed_run_with_deeply_nested_output_still_saves_its_log(self):
        for depth in (1000, 3000, 8000):
            for shape in ("error_chain", "message_list"):
                with self.subTest(depth=depth, shape=shape):
                    body = (f"d = {depth}\n"
                            "chain = '{\"error\":' * d + '1' + '}' * d\n"
                            "listy = json.dumps({'type': 'error', 'message': 'x'})[:-1] + "
                            "', \"param\": ' + '[' * d + ']' * d + '}'\n"
                            f"print(chain if '{shape}' == 'error_chain' else listy)\n"
                            "raise SystemExit(1)\n")
                    _, record = self.failure(body)
                    self.assertIn("errors", record)


# The pre-2026-10 credential-assignment pattern, kept only as an equivalence
# oracle. It is cubic on long [\w-] runs, so feed it short inputs only.
LEGACY_CREDENTIAL_ASSIGNMENT = re.compile(
    r'(?i)((?:[\w-]*(?:api[_-]?key|token|cookie|authorization|password|secret|credential)[\w-]*)["\x27]?\s*(?:[:=]|\s)\s*)'
    r'(?:"[^"\n]*"|\x27[^\x27\n]*\x27|[^\s,;&}\n]+)')



class ErrorEventRobustnessTests(unittest.TestCase):
    def test_deeply_nested_output_is_skipped_not_raised(self):
        from phone_saber_codex_process import error_events
        deep = '{"a":[' * 50_000
        self.assertEqual(error_events(deep), [])
        wrapped = json.dumps({"type": "error", "message": deep})
        self.assertEqual(error_events(wrapped)[0]["type"], "error")
        tail = deep + '\n{"type": "error", "message": "after the deep run"}'
        self.assertIn("after the deep run", json.dumps(error_events(tail)))


class RedactionComplexityTests(unittest.TestCase):
    """Regression for the 20 KB stderr that took ~25 s in Redactor.__call__."""

    def redactor(self) -> process.Redactor:
        redactor = process.Redactor.__new__(process.Redactor)
        redactor.secrets = set()  # host-independent; literal secrets are a separate pass
        return redactor

    def assert_equivalent(self, text: str) -> None:
        expected = LEGACY_CREDENTIAL_ASSIGNMENT.sub(r'\1"[REDACTED]"', text)
        self.assertEqual(process.CREDENTIAL_ASSIGNMENT.sub(r'\1"[REDACTED]"', text), expected, repr(text))

    def test_matches_legacy_pattern_on_representative_and_adversarial_inputs(self):
        cases = [
            'OPENAI_API_KEY=sk-live123 next', '{"api_key": "abc", "token":"t", "x": 1}', "token = 'abc def'\n",
            "--password hunter2 --secret=xyz", "?access_token=abc&cookie=1;", "Cookie: a=b; c=d",
            'password="unterminated\nnext', "token:", "token:,", "token  :  ,", "token  ,", "token   \n  :  v",
            'token"  =  "quoted"', "token'\t:\t'x'", "mytoken_value: x", "tokentoken=1", "xtokenx-y z",
            "api-key:=v", "apikey\n\nv", "token=a}token=b,token=c;", "TOKEN : 'a\n'", "\u212aey token \u00e9",
            "x" * 300, "token" * 60 + "=v", "token" + " " * 300 + ",", "secret" + " \n" * 150 + ";",
            "api_key=abc, " * 30, 'password "' * 30, "token:" * 40, "-" * 200 + "token" + "-" * 200 + " v",
        ]
        for text in cases:
            with self.subTest(text=text[:40]):
                self.assert_equivalent(text)

    def test_matches_legacy_pattern_on_seeded_random_inputs(self):
        tokens = ["token", "TOKEN", "api_key", "api-key", "ApiKey", "secret", "password", "cookie", "authorization",
                  "credential", "tok", "ken", "api", "key", "x", "-", "_", "1", " ", "\n", "\t", ":", "=", '"', "'",
                  ",", ";", "&", "}", "{", "/", "@", "\u212a", "\u00e9", "\u00a0", "\u2028"]
        rng = random.Random(20261002)
        for _ in range(5000):
            if rng.random() < 0.3:
                text = "".join(rng.choice("tokenapi_-k: =\"',;\n\t}") for _ in range(rng.randint(1, 60)))
            else:
                text = "".join(rng.choice(tokens) for _ in range(rng.randint(1, 40)))
            self.assert_equivalent(text)

    def test_large_adversarial_inputs_redact_quickly(self):
        size = 200_000
        cases = {
            "long word line": "x" * size,
            "keyword run": "token" * (size // 5),
            "whitespace before delimiter": "token" + " " * size + ",",
            "multiline whitespace": "secret" + " \n" * (size // 2) + ";",
            "many partial secrets": "api_key=abc, " * (size // 13),
            "unterminated quotes": 'password "' * (size // 10),
            "empty assignments": "token:" * (size // 6),
            "mixed stderr": ("Authorization: Bearer sk-abc tokenxx=" + "y" * 50 + "\n") * (size // 90),
        }
        redactor = self.redactor()
        for name, text in cases.items():
            with self.subTest(name=name):
                started = time.perf_counter()
                redactor(text)
                # Linear form takes ~10 ms here; the legacy pattern needed hours
                # for "keyword run". Generous bound so a loaded host cannot flake.
                self.assertLess(time.perf_counter() - started, 5.0)
        self.assertEqual(redactor("token=abc " + "x" * size)[:22], 'token="[REDACTED]" xxx')


class LogDirectoryIsolationTests(unittest.TestCase):
    """Unit tests must never write fake-CLI captures into ~/Library/Logs/PhoneSaber/codex.

    2026-10-03 audit: about 3,200 of the 3,300 files there came from fake CLIs in tests.
    """

    # Anything that can reach run_codex: fake CLIs, analysis, repair, or a receiver.
    REACHES_CODEX = ("fake_codex(", "codex_path=", "analyze_bundle(", "repair_bundle(",
                     "TriageHTTPServer(", "run_codex(")

    def test_environment_override_wins_over_the_default_directory(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            override = Path(temporary) / "override"
            with patch.object(process, "LOG_DIR", Path(temporary) / "default"), \
                    patch.dict(os.environ, {process.LOG_DIR_ENV: str(override)}):
                self.assertEqual(process.default_log_dir(), override)
                binary = Path(temporary) / "codex"
                binary.write_text(f"#!{sys.executable}\nprint('ok')\n")
                binary.chmod(0o700)
                run = process.run_codex([str(binary), "exec"], cwd=Path(temporary), prompt="",
                                        model="m", effort="e", timeout=10)
            self.assertEqual(run.log_path.parent, override)
            self.assertFalse((Path(temporary) / "default").exists())

    def test_every_test_module_that_can_reach_codex_isolates_its_logs(self) -> None:
        tools = Path(__file__).resolve().parent
        missing = []
        for module in sorted(tools.glob("test_*.py")):
            source = module.read_text(encoding="utf-8")
            if module.name == Path(__file__).name or not any(term in source for term in self.REACHES_CODEX):
                continue
            if "isolate_codex_logs as setUpModule" not in source \
                    or "restore_codex_logs as tearDownModule" not in source:
                missing.append(module.name)
        self.assertEqual(missing, [], "add the phone_saber_test_isolation module fixtures")

    def test_isolation_fixture_sets_and_restores_the_variable(self) -> None:
        from phone_saber_test_isolation import isolate_codex_logs, restore_codex_logs
        with patch.dict(os.environ):
            os.environ.pop(process.LOG_DIR_ENV, None)
            isolate_codex_logs()
            directory = Path(os.environ[process.LOG_DIR_ENV])
            self.assertTrue(directory.is_dir())
            self.assertNotEqual(process.default_log_dir(), process.LOG_DIR)
            restore_codex_logs()
            self.assertNotIn(process.LOG_DIR_ENV, os.environ)
            self.assertFalse(directory.exists())


if __name__ == "__main__":
    unittest.main()
