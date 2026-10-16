"""休みを主題にした素材を、打点が詰まった区間へ誤って適用しない。"""
import unittest
from hierarchy_materials import rest_ranges_valid

class MaterialRestTests(unittest.TestCase):
 def test_two_bars_and_boundary(self):
  self.assertTrue(rest_ranges_valid([{'beat':10.5},{'beat':11},{'beat':14.5},{'beat':15}],8,0,[[0,2.5]]))
  self.assertFalse(rest_ranges_valid([{'beat':10.5},{'beat':12.25}],8,0,[[0,2.5]]))
  self.assertFalse(rest_ranges_valid([{'beat':7,'_cutBeats':[7,8.5]}],8,0,[[0,2.5]]))
 def test_shifted_rest_wraps(self):
  self.assertTrue(rest_ranges_valid([{'beat':10},{'beat':14}],8,3.5,[[0,2.5]]))
  self.assertFalse(rest_ranges_valid([{'beat':8},{'beat':14}],8,3.5,[[0,2.5]]))
 def test_unrelated_notes_and_ordinary_material(self):
  self.assertTrue(rest_ranges_valid([{'beat':7},{'beat':16}],8,0,[[0,2.5]]))
  self.assertTrue(rest_ranges_valid([{'beat':8}],8,0,[]))

if __name__=='__main__':unittest.main()
