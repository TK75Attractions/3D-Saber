#!/usr/bin/env python3
"""全 ORIGINAL の guard ON/OFF 差分と目視用 PNG。派生物は指定 TMPDIR 内だけ。"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import csv
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile

from PIL import Image as PILImage, ImageDraw, ImageFont

import coreline_guard_evaluation as evaluation
import coreline_shadow as shadow
import recognition_benchmark as benchmark


def identity(path, manifest_by_hash, digest):
    session = re.search(r"phonesaber_\d{8}_\d{6}_\d{3}", str(path))
    frame = re.search(r"(?:frame|original)_(\d+)", path.stem) or re.search(r"_(\d+)$", path.stem)
    fixture = manifest_by_hash.get(digest)
    return ((session.group() if session else fixture["sourceSession"] if fixture else path.parent.name),
            int(frame[1]) if frame else fixture["frameID"] if fixture else path.stem)


def key(candidate):
    # 端点が偶然同じでも、別のscore/geometry候補へ移れば変更として残す。
    if candidate is None:
        return None
    # OFF の省略と ON の null 診断欄は同じ「guard snapshotなし」。
    return benchmark.signature({**candidate,'coreLineGuard':candidate.get('coreLineGuard')})


def overlay(image, endpoints, color):
    result = image.copy()
    if endpoints:
        draw = ImageDraw.Draw(result)
        points = [(p["x"], p["y"]) for p in endpoints]
        draw.line(points, fill=color, width=2)
        for x, y in points:
            draw.ellipse((x-5, y-5, x+5, y+5), outline=color, width=2)
    return result


def render(row):
    image = PILImage.open(row["path"]).convert("RGB")
    # 上段は元解像度（縮小しない）、下段は各端点を中心とする正確な3倍拡大。
    w, h = image.size
    panel_w = max(w, 480)
    width = panel_w * 3
    font = ImageFont.truetype("/System/Library/Fonts/Menlo.ttc", 18)
    small = ImageFont.truetype("/System/Library/Fonts/Menlo.ttc", 15)
    result = PILImage.new("RGB", (width, h+390), (24, 24, 28))
    draw = ImageDraw.Draw(result)
    draw.text((12, 8), f'{row["session"]}  frame {row["frame"]}  {row["colour"].upper()}  label={row["label"]}', font=font)
    draw.text((12, 34), f'{row["classification"]}: {row.get("reason", "pending pixel review")}', font=small)
    before, after = row["before"], row["after"]
    for index, (title, points, tint) in enumerate((('ORIGINAL', None, None),
            ('OFF (yellow)', before, '#ffe14a'), ('ON (green)', after, '#29ff9b'))):
        x = index*panel_w
        draw.text((x+12, 63), title, font=font)
        result.paste(image if points is None else overlay(image, points, tint), (x, 94))
    for i, (mode, points, tint) in enumerate((('OFF', before, '#ffe14a'), ('ON', after, '#29ff9b'))):
        for j, p in enumerate(points or []):
            index = i*2+j
            tile_w = width//4
            # グリッドに合わせて端点の周囲のみ切り出す。NEAREST で元画素を保持。
            cw, ch = (tile_w-12)//3, 80
            cx, cy = p['x'], p['y']
            box = (cx-cw//2, cy-ch//2, cx-cw//2+cw, cy-ch//2+ch)
            crop = image.crop(box).resize((cw*3, ch*3), PILImage.Resampling.NEAREST)
            x, y = index*tile_w+6, h+144
            draw.text((x, h+112), f'{mode} {j+1} ({cx},{cy}) 3x', font=small)
            result.paste(crop, (x, y))
            # 原画素の判読を邪魔しないよう中心は短い外側 tick だけ。
            draw.rectangle((x, y, x+cw*3, y+ch*3), outline=tint, width=2)
    return result


def write_review(root, rows):
    # 中断時も目視判定が残るよう、画像より先に保存する。
    (root/'changes.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2)+'\n')
    by_session = defaultdict(list)
    for row in rows:
        by_session[row['session']].append(row)
    for session, entries in sorted(by_session.items()):
        panels = []
        for row in entries:
            panel = render(row)
            panel.save(root / f'{session}_frame_{row["frame"]}_{row["colour"]}.png')
            panels.append(panel)
        width = max(p.width for p in panels)
        def sheet(group, path):
            out = PILImage.new('RGB', (width, sum(p.height for p in group)), (24,24,28))
            y = 0
            for panel in group:
                out.paste(panel, (0,y)); y += panel.height
            out.save(path)
        sheet(panels, root/f'{session}_contact.png')
        for i in range(0,len(panels),2):
            sheet(panels[i:i+2], root/f'{session}_page_{i//2+1:02}.png')
    with (root/'changes.csv').open('w',newline='') as stream:
        fields = ['session','frame','colour','label','label_source','benchmark_label','visual_label','before_source','before',
                  'after_source','after','classification','reason','path','sha256','aliases']
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        for row in rows:
            writer.writerow({k:json.dumps(row.get(k),ensure_ascii=False) if isinstance(row.get(k),(list,dict)) else row.get(k,'unknown') for k in fields})
    tally = Counter(r['classification'] for r in rows)
    counts = {name:tally[name] for name in ('correct-after','wrong-after','both-wrong','cannot-tell')}
    fixed_sessions = sorted({r['session'] for r in rows
                             if r['classification']=='correct-after' and r['label']=='saber'})
    summary = {'classifications':counts,'fixed_independent_sessions':fixed_sessions,
               'independent_event_count_conservative':len(fixed_sessions),
               'correct_output_made_wrong_count':counts.get('wrong-after',0),
               'pending_reviews':sum(r['reason']=='pending pixel review' for r in rows),
               'scope':'Selected candidate fingerprint changes, normalized null diagnostics; pixel review is not exact tip ground truth.'}
    (root/'review-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2)+'\n')
    lines = ['# Guard G (.6) 証拠パック','',
             f'独立修正session **{len(fixed_sessions)}**。判定内訳：`{json.dumps(counts)}`。',
             f'正しかった出力の悪化：{summary["correct_output_made_wrong_count"]}件。未レビュー：{summary["pending_reviews"]}件。',
             '正確な両端の手ラベル・速振りの保持試験ではない。判定不能と両方誤りは成功に数えない。','',
             '人物写真を含む私的な派生物。repoへコピーしない。原画像はread-only。','',
             '全入力：[inventory](inventory.json)。色別変更：[CSV](changes.csv) / [全候補JSON](changes.json)。','',
             '## Session contact sheets','']
    for session in sorted(by_session):
        pages = sorted(root.glob(session+'_page_*.png'))
        links = ' / '.join(f'[page {i+1}]({p.name})' for i,p in enumerate(pages))
        lines.append(f'- [{session}]({session}_contact.png) — {links}')
    lines += ['','## 各変更（元画像px）','',
              '| Session / frame / 色 | Label | OFF → ON | 判定 | 画像 |',
              '|---|---|---|---|---|']
    def endpoints(points):
        return 'null' if not points else '–'.join(f'[{p["x"]},{p["y"]}]' for p in points)
    for r in rows:
        filename = f'{r["session"]}_frame_{r["frame"]}_{r["colour"]}.png'
        lines.append(f'| {r["session"]} / {r["frame"]} / {r["colour"]} | {r["label"]} | '
                     f'{endpoints(r["before"])} → {endpoints(r["after"])} | {r["classification"]} | [view]({filename}) |')
    (root/'REVIEW.md').write_text('\n'.join(lines)+'\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--render-only',action='store_true',help='changes.json の目視判定を再描画')
    args = parser.parse_args()
    expected = Path(os.environ.get('TMPDIR',tempfile.gettempdir())).resolve()/'phonesaber-guard-evidence'
    if args.output.resolve() != expected:
        parser.error('output must be $TMPDIR/phonesaber-guard-evidence')
    root = args.output
    root.mkdir(parents=True,exist_ok=True)
    if args.render_only:
        write_review(root,json.loads((root/'changes.json').read_text()))
        return
    previous = {(r['sha256'],r['colour']):r for r in
                json.loads((root/'changes.json').read_text())} if (root/'changes.json').exists() else {}
    downloads = Path.home()/'Downloads'
    inbox = Path.home()/'Library/Application Support/PhoneSaber/diagnostics-inbox'
    images, excluded = benchmark.discover(downloads,inbox)
    # inbox の全 bundle を走査。images ディレクトリ外に ORIGINAL があっても拾う。
    extra_roots = [(inbox,'inbox'),(benchmark.REPO,'fixture')]
    known = {i.path.resolve() for i in images}
    for search,origin in extra_roots:
        for path in sorted(search.rglob('*')):
            if not path.is_file() or path.suffix.lower() != '.png':
                continue
            if origin=='fixture' and not any(p.lower()=='fixtures' for p in path.parts):
                continue
            if path.resolve() in known or not benchmark.original(path):
                continue
            images.append(benchmark.Image(path,benchmark.sha256(path.read_bytes()),origin,
                          benchmark.sha256((origin+':'+str(path.relative_to(search))).encode())))
            known.add(path.resolve())
    truths, truth_sources, missing = benchmark.truth_index(benchmark.LABELS,benchmark.MANIFEST,downloads,inbox)
    if missing:
        raise ValueError('missing labelled original evidence')
    # ラベルの参照元が benchmark.discover の roots 以外でも必ず含める。
    for label in json.loads(benchmark.LABELS.read_text())['labels']:
        for item in label['images']:
            base = inbox if item['path'].startswith('phone_saber_triage_') else downloads
            path = benchmark.contained(base,item['path'])
            if path.resolve() not in known:
                images.append(benchmark.Image(path,benchmark.sha256(path.read_bytes()),'label-source',
                              benchmark.sha256(str(path).encode())))
                known.add(path.resolve())
    fixtures = json.loads(benchmark.MANIFEST.read_text())['fixtures']
    manifest_by_hash = {f['sha256']:f for f in fixtures}
    binaries = {mode:evaluation.build(root/mode,enabled=(mode=='on')) for mode in ('off','on')}
    rows, inventory, changes = [], [], {}
    for i,image in enumerate(images):
        session, frame = identity(image.path,manifest_by_hash,image.digest)
        values = {mode:json.loads(subprocess.check_output([str(binary),str(image.path)]))['colors']
                  for mode,binary in binaries.items()}
        inventory.append({'path':str(image.path),'sha256':image.digest,'origin':image.origin,
                          'session':session,'frame':frame,
                          'off_sha256':benchmark.signature(values['off']),
                          'on_sha256':benchmark.signature(values['on'])})
        for colour in benchmark.COLORS:
            before,after = (benchmark.winner(values[mode][colour]) for mode in ('off','on'))
            if key(before)==key(after): continue
            change_id = (session,str(frame),colour,image.digest)
            if change_id in changes:
                changes[change_id]['aliases'].append(str(image.path));continue
            row = {'session':session,'frame':frame,'colour':colour,
                   'label':truths.get((image.digest,colour),'unknown'),
                   'label_source':'hash-verified existing label' if (image.digest,colour) in truths else 'unlabelled',
                   'before_source':before['source'] if before else None,
                   'before':before['endpoints'] if before else None,
                   'after_source':after['source'] if after else None,
                   'after':after['endpoints'] if after else None,
                   'before_score':before['score'] if before else None,
                   'after_score':after['score'] if after else None,
                   'classification':'cannot-tell','reason':'pending pixel review',
                   'path':str(image.path),'sha256':image.digest,'aliases':[],
                   'off_candidates':values['off'][colour]['candidates'],
                   'on_candidates':values['on'][colour]['candidates']}
            review = previous.get((image.digest,colour),{})
            if review.get('before') == row['before'] and review.get('after') == row['after']:
                for field in ('classification','reason','label','label_source','benchmark_label','visual_label'):
                    if field in review:
                        row[field] = review[field]
            changes[change_id]=row
        if (i+1)%100==0: print('searched',i+1,'/',len(images),flush=True)
    rows = sorted(changes.values(),key=lambda r:(r['session'],str(r['frame']).zfill(12),r['colour']))
    summary = {'files':len(images),'unique_png':len({i.digest for i in images}),
               'origins':dict(Counter(i.origin for i in images)),'excluded':excluded,
               'changed_occurrences':sum(len(r['aliases'])+1 for r in rows),
               'changed_session_frame_colours':len(rows),'changed_unique_png':len({r['sha256'] for r in rows}),
               'changed_sessions':dict(Counter(r['session'] for r in rows)),
               'inbox_bundles':sorted(p.name for p in inbox.iterdir() if p.is_dir()),
               'missing_label_evidence':missing,'truth_sources':truth_sources}
    (root/'inventory.json').write_text(json.dumps({'summary':summary,'frames':inventory},ensure_ascii=False,indent=2))
    write_review(root,rows)
    print(json.dumps(summary,ensure_ascii=False),flush=True)


if __name__=='__main__':
    main()
