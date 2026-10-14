"""定期更新の対象を5曲に限定し、対象外の譜面と目録を保護する。"""
import hashlib,json
from pathlib import Path
HERE=Path(__file__).resolve().parent
POLICY=json.loads((HERE/'scope-policy.json').read_text(encoding='utf8'))
ALLOWED=frozenset(POLICY['allowedSongIds'])
DIFFICULTIES=frozenset(POLICY['allowedDifficulties'])
def authorize(song,difficulty=None):
 if song not in ALLOWED or (difficulty is not None and difficulty not in DIFFICULTIES):
  raise ValueError('定時更新の対象外: '+song+' / '+str(difficulty))
def authorize_specs(specs):
 for s in specs:authorize(s['id'])
 if {s['id'] for s in specs}!=ALLOWED:raise ValueError('対象5曲の定義が不完全')
def digest(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def protected_repo(repo):
 return {p.relative_to(repo).as_posix():digest(p) for p in (repo/'Assets/StreamingAssets/Songs').glob('*/chart*.json') if p.parent.name not in ALLOWED}
def protected_web(dist):
 catalog=json.loads((dist/'catalog.json').read_text(encoding='utf8'))
 songs=[s for s in catalog['songs'] if s['sourceId'] not in ALLOWED]
 return {'songs':songs,'hashes':{e['path']:digest(dist/e['path']) for s in songs for e in s['charts']}}
def assert_unchanged(before,after):
 if before!=after:raise AssertionError('対象外の譜面または目録に変更があるため反映を中止')
