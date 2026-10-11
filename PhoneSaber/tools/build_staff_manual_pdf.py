#!/usr/bin/env python3
"""STAFF_MANUAL.md から配布用の STAFF_MANUAL.pdf（A4・2ページ）を作る。

    python3 PhoneSaber/tools/build_staff_manual_pdf.py            # docs/STAFF_MANUAL.pdf を更新
    python3 PhoneSaber/tools/build_staff_manual_pdf.py --check    # 作り直さず、PDF が Markdown より古くないかだけ確認

Markdown の変換は、このマニュアルが使う書き方（見出し・番号つき/箇条書き・表・太字・リンク・
`<!-- pagebreak -->`）だけに対応した最小のもの。PDF 化は Google Chrome の headless 印刷を使う。
"""
from __future__ import annotations

import argparse
import html
import re
import subprocess
import sys
import tempfile
from pathlib import Path

DOCS = Path(__file__).resolve().parents[1] / "docs"
CHROME = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
EXPECTED_PAGES = 2
CSS = """
@page { size: A4; margin: 11mm 12mm; }
body { font-family: "Hiragino Sans", "Hiragino Kaku Gothic ProN", sans-serif; font-size: 9.6pt; line-height: 1.42; color: #111; }
h1 { font-size: 15pt; margin: 0 0 4pt; }
h2 { font-size: 11.5pt; margin: 9pt 0 3pt; border-bottom: 1px solid #999; padding-bottom: 1pt; }
p { margin: 2pt 0; }
ol, ul { margin: 2pt 0; padding-left: 16pt; }
li { margin: 1pt 0; }
table { border-collapse: collapse; margin: 4pt 0; width: 100%; }
th, td { border: 1px solid #777; padding: 2.5pt 5pt; text-align: left; vertical-align: top; }
th { background: #eee; }
.pagebreak { break-after: page; }
"""


def inline(text: str) -> str:
    text = html.escape(text, quote=False)
    text = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", text)      # 印刷物なのでリンク先は書かない
    text = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", text)
    return text


def convert(markdown: str) -> str:
    out: list[str] = []
    stack: list[tuple[int, str]] = []      # (indent, tag) of open lists
    table: list[str] = []
    paragraph: list[str] = []

    def close_lists(indent: int = -1) -> None:
        while stack and stack[-1][0] > indent:
            out.append(f"</li></{stack.pop()[1]}>")

    def flush_paragraph() -> None:
        if paragraph:
            out.append("<p>" + "<br>".join(inline(line) for line in paragraph) + "</p>")
            paragraph.clear()

    def flush_table() -> None:
        if not table:
            return
        rows = [[cell.strip() for cell in row.strip().strip("|").split("|")] for row in table]
        out.append("<table>")
        for index, cells in enumerate(rows):
            if index == 1 and all(re.fullmatch(r":?-{3,}:?", cell) for cell in cells):
                continue
            tag = "th" if index == 0 else "td"
            out.append("<tr>" + "".join(f"<{tag}>{inline(cell)}</{tag}>" for cell in cells) + "</tr>")
        out.append("</table>")
        table.clear()

    for raw in markdown.splitlines():
        line = raw.rstrip()
        if line.lstrip().startswith("|"):
            flush_paragraph()
            table.append(line)
            continue
        flush_table()
        if not line.strip():
            flush_paragraph()
            continue
        if line.strip() == "<!-- pagebreak -->":
            flush_paragraph(); close_lists()
            out.append('<div class="pagebreak"></div>')
            continue
        heading = re.match(r"(#{1,3}) (.+)", line)
        if heading:
            flush_paragraph(); close_lists()
            level = len(heading.group(1))
            out.append(f"<h{level}>{inline(heading.group(2))}</h{level}>")
            continue
        item = re.match(r"( *)(\d+\.|-) (.+)", line)
        if item:
            flush_paragraph()
            indent, tag = len(item.group(1)), "ol" if item.group(2) != "-" else "ul"
            if stack and stack[-1][0] == indent:
                out.append("</li>")
            elif not stack or indent > stack[-1][0]:
                out.append(f"<{tag}>")
                stack.append((indent, tag))
            else:
                close_lists(indent)
                out.append("</li>")
            if tag == "ol":
                out.append(f'<li value="{int(item.group(2)[:-1])}">{inline(item.group(3))}')
            else:
                out.append(f"<li>{inline(item.group(3))}")
            continue
        if stack and raw.startswith(" "):                 # 項目の続きの行
            out.append("<br>" + inline(line.strip()))
            continue
        close_lists()
        paragraph.append(line)
    flush_table(); flush_paragraph(); close_lists()
    return ("<!doctype html><html lang=\"ja\"><head><meta charset=\"utf-8\"><style>" + CSS +
            "</style></head><body>" + "\n".join(out) + "</body></html>")


def page_count(pdf: bytes) -> int:
    return len(re.findall(rb"/Type\s*/Page\b(?!s)", pdf))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", type=Path, default=DOCS / "STAFF_MANUAL.md")
    parser.add_argument("--output", type=Path, default=DOCS / "STAFF_MANUAL.pdf")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args(argv)
    if args.check:
        stale = not args.output.exists() or args.output.stat().st_mtime < args.source.stat().st_mtime
        print("STAFF_MANUAL.pdf は Markdown より古い" if stale else "STAFF_MANUAL.pdf は最新")
        return 1 if stale else 0
    if not Path(CHROME).exists():
        print("Google Chrome が見つかりません: " + CHROME, file=sys.stderr)
        return 2
    with tempfile.TemporaryDirectory() as directory:
        page = Path(directory) / "manual.html"
        page.write_text(convert(args.source.read_text(encoding="utf-8")), encoding="utf-8")
        target = Path(directory) / "manual.pdf"
        subprocess.run([CHROME, "--headless=new", "--disable-gpu", "--no-pdf-header-footer",
                        f"--print-to-pdf={target}", page.as_uri()],
                       check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120)
        if not target.exists():
            print("PDF を作れませんでした（Chrome の headless 印刷が失敗）", file=sys.stderr)
            return 1
        data = target.read_bytes()
    pages = page_count(data)
    if pages != EXPECTED_PAGES:
        print(f"{pages} ページになりました（{EXPECTED_PAGES} ページに収める）。PDF は更新していません", file=sys.stderr)
        return 1
    args.output.write_bytes(data)
    print(f"{args.output} を更新しました（{pages} ページ）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
