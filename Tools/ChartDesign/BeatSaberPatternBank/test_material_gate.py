"""ロング表示の余白を、カット後の移動時間から差し引かない回帰検査。"""
import copy
import unittest
from hierarchy_materials import movement_valid

class SecondsClock:
 def t(self,beat):return beat

class MaterialMovementTests(unittest.TestCase):
 def setUp(self):
  self.notes=[dict(time=0,lengthMs=1090,x=.6,y=-.6,_hand=0,_cutBeats=[0,1]),
              dict(time=1300,lengthMs=0,x=1.9,y=.3,_hand=0,_cutBeats=[1.3])]
 def test_padding_does_not_reject_valid_last_cut_gap(self):
  self.assertTrue(movement_valid(self.notes,SecondsClock()))
 def test_short_gap_after_last_cut_is_rejected(self):
  self.notes[1]['time']=1290
  self.assertFalse(movement_valid(self.notes,SecondsClock()))
 def test_close_simultaneous_notes_are_still_rejected(self):
  n=copy.deepcopy(self.notes[1]);n.update(_hand=1,x=1.5)
  self.notes.append(n)
  self.assertFalse(movement_valid(self.notes,SecondsClock()))

if __name__=='__main__':unittest.main()
