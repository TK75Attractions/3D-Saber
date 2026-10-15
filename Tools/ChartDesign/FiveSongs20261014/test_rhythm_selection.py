"""定時の素材追加が、確定した休符を埋め戻さないことを検査する。"""
import unittest
from rhythm_selection import select_retained

class Music:
 spec={'id':'test'}
 def t(self,b):return b

def note(t,x,hand=0):
 return {'time':t*1000,'beat':t,'x':x,'y':0,'_hand':hand,'_cutBeats':[t]}

def policy(times):return {'songs':{'test':{'charts':{'normal':{'retainedHits':[[t*1000,'red'] for t in times]}}}}}

class RhythmSelectionTests(unittest.TestCase):
 def test_preserves_short_phrase_and_rest_when_extra_onsets_exist(self):
  source=[note(t,.6) for t in (0,.5,1,1.5,2,3,4)]
  result=select_retained(source,Music(),'normal',policy((0,.5,3,4)))
  self.assertEqual([n['beat'] for n in result],[0,.5,3,4])
 def test_missing_accent_does_not_fall_back_to_regular_pulse(self):
  with self.assertRaises(AssertionError):select_retained([note(0,.6)],Music(),'normal',policy((0,3)))
 def test_new_material_cannot_make_retained_large_move_too_fast(self):
  with self.assertRaises(AssertionError):select_retained([note(0,.6),note(.4,1.9)],Music(),'normal',policy((0,.4)))

if __name__=='__main__':unittest.main()
