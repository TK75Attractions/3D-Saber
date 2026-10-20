import unittest
from verify import audit,hierarchy_issues

def chart(times, long_at=None, flick_at=10000):
    rows=[]
    for i,t in enumerate(times):
        n=dict(time=t,beat=t/1000,x=-1 if i%2 else 1,y=0,
               color="blue" if i%2 else "red",type="tap",direction="none",count=1,lengthMs=0)
        if t==flick_at:n.update(type="direction",direction="up")
        if long_at and t==long_at[0]:n.update(type="long",count=long_at[1],lengthMs=long_at[2])
        rows.append(n)
    return {"bpm":60,"notes":rows}

class ValidatorRegressionTests(unittest.TestCase):
    def test_normal_keeps_at_least_easy_note_and_flick_counts(self):
        self.assertEqual([],hierarchy_issues({"notes":80,"flicks":3},{"notes":120,"flicks":3}))
        self.assertEqual(["Normal notes below Easy","Normal flicks below Easy"],
                         hierarchy_issues({"notes":80,"flicks":3},{"notes":79,"flicks":2}))

    def issues(self,data,diff="normal"):
        return audit(data,diff,list(range(0,30001,1000)),lambda _:1000)["issues"]

    def test_on_off_on_with_breathing_room_is_allowed(self):
        self.assertFalse(self.issues(chart([0,500,1000,3000,6000,10000,13000])))

    def test_off_on_off_is_rejected_even_with_outer_anchors(self):
        result=self.issues(chart([0,500,1000,1500,2000,6000,10000,13000]))
        self.assertTrue(any("off-on-off" in x for x in result))

    def test_isolated_offbeat_is_rejected(self):
        self.assertTrue(any("unanchored" in x for x in self.issues(chart([0,2500,6000,10000,13000]))))

    def test_easy_rejects_eighth_notes_even_if_hands_alternate(self):
        result=self.issues(chart([0,500,1000,4000,7000,10000,13000]),"easy")
        self.assertTrue(any("offbeat" in x for x in result))
        self.assertTrue(any("quarter" in x for x in result))

    def test_easy_rejects_hidden_fast_cuts_inside_a_long(self):
        result=self.issues(chart([0,4000,7000,10000,13000],long_at=(0,3,1000)),"easy")
        self.assertTrue(any("quarter" in x for x in result))

    def test_exactly_five_second_gap_is_rejected(self):
        self.assertTrue(any("gap >=" in x for x in self.issues(chart([0,5000,10000,13000]))))

    def test_audible_introduction_is_not_exempt_but_initial_silence_is(self):
        c=chart([6000,10000,13000])
        pulses=list(range(0,30001,1000))
        self.assertIn("audible introduction gap >= 5000ms",
                      audit(c,"easy",pulses,lambda _:1000,music_start=1000)["issues"])
        self.assertNotIn("audible introduction gap >= 5000ms",
                         audit(c,"easy",pulses,lambda _:1000,music_start=5000)["issues"])

    def test_chords_do_not_count_as_zero_interval_speed(self):
        c=chart([0,2000,4000,7000,10000,13000])
        c["notes"].insert(1,dict(c["notes"][0],color="blue",x=-1))
        self.assertFalse(self.issues(c,"easy"))

    def test_missing_flick_is_rejected(self):
        self.assertIn("missing flick",self.issues(chart([0,3000,6000,9000,12000],flick_at=None)))

    def test_long_counts_are_expanded_for_gap_detection(self):
        c=chart([0,6000,10000,13000],long_at=(0,3,4000))
        self.assertFalse(any("gap >=" in x for x in self.issues(c)))

if __name__=="__main__":
    unittest.main()
