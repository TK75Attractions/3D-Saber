import contextlib
import io
import tempfile
import unittest
from pathlib import Path

import install_workspace_files as kit


class InstallWorkspaceFilesTests(unittest.TestCase):
    def run_tool(self, *args: str) -> tuple[int, str]:
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            code = kit.main(list(args))
        return code, output.getvalue()

    def test_installs_missing_files_and_then_reports_them_unchanged(self):
        with tempfile.TemporaryDirectory() as tmp:
            workspace, claude = Path(tmp, "ws"), Path(tmp, "claude")
            code, _ = self.run_tool("--workspace", str(workspace), "--claude-home", str(claude))
            self.assertEqual(code, 0)
            for name in kit.WORKSPACE_FILES:
                self.assertEqual((workspace / name).read_bytes(), (kit.KIT / name).read_bytes())
            self.assertIn("日本語", (claude / "CLAUDE.md").read_text(encoding="utf-8"))
            code, out = self.run_tool("--workspace", str(workspace), "--claude-home", str(claude))
            self.assertEqual(code, 0)
            self.assertEqual(out.count(" same "), 4)

    def test_never_overwrites_a_different_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            workspace, claude = Path(tmp, "ws"), Path(tmp, "claude")
            claude.mkdir()
            (claude / "CLAUDE.md").write_text("my own rules\n", encoding="utf-8")
            code, out = self.run_tool("--workspace", str(workspace), "--claude-home", str(claude))
            self.assertEqual(code, 2)
            self.assertEqual((claude / "CLAUDE.md").read_text(encoding="utf-8"), "my own rules\n")
            self.assertTrue((claude / "CLAUDE.md.from-repo").exists())
            self.assertIn("differs", out)

    def test_check_writes_nothing(self):
        with tempfile.TemporaryDirectory() as tmp:
            workspace, claude = Path(tmp, "ws"), Path(tmp, "claude")
            code, out = self.run_tool("--workspace", str(workspace), "--claude-home", str(claude), "--check")
            self.assertEqual(code, 2)
            self.assertIn("missing", out)
            self.assertFalse(workspace.exists())
            self.assertFalse(claude.exists())


if __name__ == "__main__":
    unittest.main()
