"""対象を絞っても過去の曲や並行編集を再出力しない。"""
import copy,json,tempfile,unittest
from pathlib import Path
from scope_guard import ALLOWED,authorize,authorize_specs,select_allowed_specs,protected_repo
from export_web import application_key,frozen_applications

class ScopePolicyTests(unittest.TestCase):
 def test_only_two_songs_are_editable(self):
  self.assertEqual(ALLOWED,{'escapade','logic'})
  for song in ALLOWED:
   for diff in ('easy','normal','hard'):authorize(song,diff)
  for song in ('ManicSonic','BenkyouRemaster','Aobasou','Zero','Phobia','RushE','2_23_AM'):
   with self.assertRaises(ValueError):authorize(song,'hard')
  with self.assertRaises(ValueError):authorize('logic','expert')

 def test_history_is_not_an_edit_allowlist(self):
  specs=[{'id':s} for s in ('ManicSonic','escapade','logic','BenkyouRemaster','Aobasou')]
  self.assertEqual([s['id'] for s in select_allowed_specs(specs)],['escapade','logic'])
  with self.assertRaises(ValueError):authorize_specs(specs)
  with self.assertRaises(ValueError):select_allowed_specs([{'id':'logic'}])
  with self.assertRaises(ValueError):select_allowed_specs(specs+[{'id':'logic'}])

 def test_newly_frozen_charts_are_hashed(self):
  with tempfile.TemporaryDirectory() as temp:
   repo=Path(temp)
   for sid in ('ManicSonic','BenkyouRemaster','Aobasou','escapade','logic'):
    p=repo/'Assets/StreamingAssets/Songs'/sid/'chart_hard.json';p.parent.mkdir(parents=True);p.write_text('{}')
   hashed=protected_repo(repo)
   self.assertEqual({Path(p).parent.name for p in hashed},{'ManicSonic','BenkyouRemaster','Aobasou'})

 def test_frozen_and_excluded_applications_use_published_data(self):
  with tempfile.TemporaryDirectory() as temp:
   dist=Path(temp);(dist/'applied').mkdir()
   apps=[]
   for i,sid in enumerate(('ManicSonic','BenkyouRemaster','Aobasou','escapade','logic')):
    a=dict(patternId=str(i),songId=sid,difficulty='hard',startBeat=8,endBeat=16,chartFile=f'applied/{i}.json',hands=['blue'],chart={'notes':[{'time':0,'x':-1}]})
    (dist/a['chartFile']).write_text(json.dumps(a['chart']),encoding='utf8');apps.append(a)
   (dist/'converted-patterns.json').write_text(json.dumps({'patterns':[{'applications':apps}]}),encoding='utf8')
   changed=copy.deepcopy(apps);changed[0]['chart']['notes'][0]['x']=99;changed[0]['hands']=['red']
   frozen,hashes=frozen_applications(dist,changed,{'logic'})
   self.assertEqual(len(frozen),4);self.assertEqual(len(hashes),4)
   self.assertEqual(frozen[application_key(apps[0])],apps[0])
   with self.assertRaises(ValueError):frozen_applications(dist,changed[1:],{'logic'})
   (dist/'applied/0.json').write_text('{}')
   with self.assertRaises(ValueError):frozen_applications(dist,apps,{'logic'})

if __name__=='__main__':unittest.main()
