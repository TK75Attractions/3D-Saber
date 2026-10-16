"""下位の方向練習で休符と上位のフリック方向を保つ回帰検査。"""
import unittest
from hierarchy_rework import direction_training_candidate

class DirectionTrainingTests(unittest.TestCase):
 def setUp(self):
  self.source={'time':1000,'_hand':1,'direction':'upright','_arrow':True,'_forced':None}
 def test_preserved_rest_is_not_filled(self):
  self.assertIsNone(direction_training_candidate([],self.source,True))
 def test_existing_hit_keeps_upper_direction(self):
  original={**self.source,'direction':'none','_arrow':False}
  result=direction_training_candidate([original],self.source,True)
  self.assertEqual(len(result),1);self.assertEqual(result[0]['_forced'],'upright');self.assertFalse(original['_arrow'])
 def test_legacy_added_hit_also_forces_upper_direction(self):
  result=direction_training_candidate([],self.source,False)
  self.assertEqual(result[0]['_forced'],'upright');self.assertIsNone(self.source['_forced'])

if __name__=='__main__':unittest.main()
