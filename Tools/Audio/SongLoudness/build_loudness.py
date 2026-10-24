"""原音を保持して再生用の音量情報を生成し、適用後のLUFSとピークを実測する。"""
import argparse
import concurrent.futures
import hashlib
import json
import re
import subprocess
from pathlib import Path

TARGET = -18.0
PEAK = -1.0


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(ffmpeg, path, filters, output=None):
    args = [ffmpeg, '-hide_banner', '-nostats', '-y', '-i', str(path), '-vn', '-af', filters]
    if output:
        args += ['-ar', '44100', '-c:a', 'pcm_s16le', str(output)]
    else:
        args += ['-f', 'null', '-']
    result = subprocess.run(args, capture_output=True, text=True, encoding='utf8', errors='replace', check=True)
    blocks = re.findall(r'\{[^{}]+"input_i"[^{}]+\}', result.stderr)
    return json.loads(blocks[-1]) if blocks else None


def sources(folder):
    """曲の音源(audio.*)と、難易度ごとの音源(audio_<難易度>.*、2026-10-24 から ElDorado の G)を (音源, 書き出す名前) で返す。"""
    found = []
    main = next((folder / n for n in ('audio.ogg', 'audio.wav', 'audio.mp3') if (folder / n).exists()), None)
    if main is not None:
        found.append((main, 'loudness.json'))
    variants = {}
    for path in sorted(folder.glob('audio_*.*')):
        suffix = path.stem[len('audio'):].lower()
        if path.suffix.lower() in ('.ogg', '.wav', '.mp3') and suffix not in variants:
            variants[suffix] = path
    for suffix in sorted(variants):
        found.append((variants[suffix], f'loudness{suffix}.json'))
    return found


def build(ffmpeg, folder, output):
    reports = [r for r in (build_one(ffmpeg, folder, source, name, output) for source, name in sources(folder)) if r]
    return reports or None


def build_one(ffmpeg, folder, source, metadata_name, output):
    measure_filter = f'loudnorm=I={TARGET}:TP={PEAK}:LRA=11:print_format=json'
    measured = run(ffmpeg, source, measure_filter)
    gain = round(TARGET - float(measured['input_i']), 2)
    dest = output / folder.name
    dest.mkdir(parents=True, exist_ok=True)
    profile = dict(version=1, sourceFile=source.name, sourceSha256=sha(source),
                   targetLufs=TARGET, inputLufs=float(measured['input_i']),
                   inputTruePeakDbtp=float(measured['input_tp']), gainDb=gain)
    if gain > 0:
        # AudioSource.volumeの上限を超える増幅は事前処理し、トゥルーピークも制限する。
        playback = dest / ('normalized' + metadata_name[len('loudness'):-len('.json')] + '.wav')
        offset = float(measured['target_offset'])
        for attempt in range(3):
            filters = (f'loudnorm=I={TARGET}:TP={PEAK}:LRA=11:'
                       f'measured_I={measured["input_i"]}:measured_TP={measured["input_tp"]}:'
                       f'measured_LRA={measured["input_lra"]}:measured_thresh={measured["input_thresh"]}:'
                       f'offset={offset}:linear=true:print_format=json')
            run(ffmpeg, source, filters, playback)
            final = run(ffmpeg, playback, measure_filter)
            if abs(float(final['input_i']) - TARGET) <= .1:
                break
            offset += TARGET - float(final['input_i'])
        profile.update(gainDb=0, playbackFile=playback.name, playbackSha256=sha(playback))
    else:
        final = run(ffmpeg, source, f'volume={gain}dB,' + measure_filter)
    assert abs(float(final['input_i']) - TARGET) <= .15, (folder.name, final)
    assert float(final['input_tp']) <= PEAK + .1, (folder.name, final)
    profile.update(outputLufs=float(final['input_i']), outputTruePeakDbtp=float(final['input_tp']))
    (dest / metadata_name).write_text(json.dumps(profile, ensure_ascii=False, indent=2) + '\n', encoding='utf8')
    print(f'{folder.name}/{source.name}: {profile["inputLufs"]:.2f} -> {profile["outputLufs"]:.2f} LUFS, '
          f'{profile["outputTruePeakDbtp"]:.2f} dBTP', flush=True)
    return dict(songId=folder.name, **profile)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--songs', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--ffmpeg', default='ffmpeg')
    args = parser.parse_args()
    folders = sorted(p for p in args.songs.iterdir() if p.is_dir())
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        reports = [r for p in pool.map(lambda p: build(args.ffmpeg, p, args.output), folders) if p for r in p]
    (args.output / 'report.json').write_text(json.dumps(reports, ensure_ascii=False, indent=2) + '\n', encoding='utf8')
