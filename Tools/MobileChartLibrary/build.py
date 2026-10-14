"""現在の収録譜面を、スマホで再生できる静的ライブラリに書き出す。"""
import argparse
import hashlib
import json
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
RECENT = ['ManicSonic', 'escapade', 'logic', 'BenkyouRemaster', 'Aobasou']
ORDER = ['easy', 'normal', 'hard', 'e']


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(args):
    result = subprocess.run([str(a) for a in args], capture_output=True)
    if result.returncode:
        raise RuntimeError(result.stderr.decode('utf-8', errors='replace'))
    return result.stdout.decode('utf-8')


def build(repo, output, ffmpeg):
    dist = output / 'dist'
    dist.mkdir(parents=True, exist_ok=True)
    for source in (HERE / 'web').iterdir():
        shutil.copy2(source, dist / source.name)
    ffprobe = ffmpeg.with_name('ffprobe' + ffmpeg.suffix)
    root = repo / 'Assets/StreamingAssets/Songs'
    folders = sorted(root.iterdir(), key=lambda p: (RECENT.index(p.name) if p.name in RECENT else 5, p.name.casefold()))
    songs = []
    for folder in folders:
        if not folder.is_dir():
            continue
        charts = sorted(folder.glob('chart_*.json'), key=lambda p: ORDER.index(p.stem[6:]) if p.stem[6:] in ORDER else 99)
        # chart.json は既定難易度の別名。固有の内容がある場合だけ追加する。
        legacy = folder / 'chart.json'
        if legacy.exists() and not any(read(p) == read(legacy) for p in charts):
            charts.append(legacy)
        if not charts:
            continue
        stage = read(folder / 'stage.json') if (folder / 'stage.json').exists() else {}
        loudness = read(folder / 'loudness.json') if (folder / 'loudness.json').exists() else {}
        audio = folder / (loudness.get('playbackFile') or loudness.get('sourceFile') or '')
        if not audio.is_file():
            audio = next(p for p in folder.glob('audio.*') if p.suffix.lower() in ['.wav', '.ogg', '.mp3', '.m4a'])
        sid = 's-' + hashlib.sha256(folder.name.encode()).hexdigest()[:10]
        target = dist / 'songs' / sid
        target.mkdir(parents=True, exist_ok=True)
        duration = float(read_json_output(run([ffprobe, '-v', 'error', '-show_entries', 'format=duration', '-of', 'json', audio]))['format']['duration'])
        gain = float(loudness.get('gainDb', 0))
        fingerprint = digest(audio) + f':aac128k:gain{gain}'
        cache = target / 'audio-source.txt'
        encoded = target / 'audio.m4a'
        if not encoded.exists() or not cache.exists() or cache.read_text() != fingerprint:
            run([ffmpeg, '-y', '-v', 'error', '-i', audio, '-map', '0:a:0', '-vn', '-af', f'volume={gain}dB', '-c:a', 'aac', '-b:a', '128k', '-ar', '44100', '-movflags', '+faststart', encoded])
            cache.write_text(fingerprint)
        title = stage.get('displayName') or folder.name
        song = {'id': sid, 'sourceId': folder.name, 'title': title, 'composer': stage.get('composer', ''), 'recent': folder.name in RECENT,
                'duration': duration, 'audio': f'songs/{sid}/audio.m4a', 'chapters': stage.get('sections', []),
                'previewTime': stage.get('previewStartSeconds', 0), 'charts': []}
        covers = list(folder.glob('cover.*'))
        cover = next((p for p in covers if p.suffix.lower() in ['.jpg', '.png', '.webp']), None)
        if cover:
            shutil.copy2(cover, target / cover.name)
            song['cover'] = f'songs/{sid}/{cover.name}'
        if (folder / 'CREDITS.txt').exists():
            shutil.copy2(folder / 'CREDITS.txt', target / 'credits.txt')
            song['credits'] = f'songs/{sid}/credits.txt'
        for chart in charts:
            data = read(chart)
            notes = data.get('notes', [])
            if not notes:
                continue
            difficulty = chart.stem[6:] if chart.stem.startswith('chart_') else 'original'
            shutil.copy2(chart, target / chart.name)
            song['charts'].append({'difficulty': difficulty, 'label': {'e': 'E', 'original': 'Original'}.get(difficulty, difficulty.title()),
                'path': f'songs/{sid}/{chart.name}', 'sha256': digest(chart), 'level': data.get('displayLevel', 0), 'bpm': data.get('bpm', 120),
                'count': len(notes), 'flicks': sum(n.get('direction', 'none') != 'none' for n in notes),
                'longs': sum(n.get('count', 1) > 1 for n in notes), 'gold': sum(n.get('color') == 'gold' for n in notes)})
        if song['charts']:
            songs.append(song)
        print(f'{title}: {len(song["charts"])} charts / {duration:.1f}s', flush=True)
    result = {'schemaVersion': 1, 'updated': '2026-10-14', 'songs': songs, 'chartCount': sum(len(s['charts']) for s in songs)}
    (dist / 'catalog.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(f'{len(songs)} songs / {result["chartCount"]} charts / {sum(p.stat().st_size for p in dist.rglob("*") if p.is_file())/1e6:.1f} MB')


def read_json_output(text):
    return json.loads(text)


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo', type=Path, default=HERE.parents[1])
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--ffmpeg', type=Path, required=True)
    args = parser.parse_args()
    build(args.repo.resolve(), args.output.resolve(), args.ffmpeg.resolve())
