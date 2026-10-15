import unittest
from hierarchy_materials import style_core_valid

class StyleCoreTests(unittest.TestCase):
 def test_changed_core_position_is_rejected(self):
  self.assertFalse(style_core_valid([{'time':1000,'_hand':0,'x':.6,'y':-.6}],[{'time':1000,'hand':0,'x':1.9,'y':.3}]))
 def test_other_phrases_remain_available(self):
  self.assertTrue(style_core_valid([{'time':1000,'_hand':0,'x':1.9,'y':.3},{'time':2000,'_hand':1,'x':.6,'y':-.6}],[{'time':1000,'hand':0,'x':1.9,'y':.3}]))
 def test_hand_is_not_inferred_from_position(self):
  anchors=[{'time':1000,'hand':1,'x':.6,'y':.3}]
  self.assertTrue(style_core_valid([{'time':1000,'_hand':1,'color':'gold','x':.6,'y':.3}],anchors))
  self.assertFalse(style_core_valid([{'time':1000,'_hand':0,'color':'gold','x':.6,'y':.3}],anchors))

if __name__=='__main__':unittest.main()
