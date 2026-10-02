"""Failure captures use real fake executables, including timeout partial output."""
from __future__ import annotations

import contextlib
import io
import json
import os
import stat
import sys
import tempfile
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


if __name__ == "__main__":
    unittest.main()
