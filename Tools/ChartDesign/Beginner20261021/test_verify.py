import unittest
from verify import audit,hierarchy_issues,pulse_map_hash,AUDIO_CHOICES,HAND_CHOICES,audio_choice,hand_choice,cuts
import json
from pathlib import Path

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
    def test_saved_pulse_hash_accepts_line_endings_but_rejects_changed_content(self):
        root=Path(__file__).parent
        data=(root/"pulse-map.json").read_bytes().replace(b"\r\n", b"\n")
        expected=json.loads((root/"design.json").read_text(encoding="utf-8"))["pulseMapSha256"]
        self.assertEqual(expected,pulse_map_hash(data))
        self.assertEqual(expected,pulse_map_hash(data.replace(b"\n", b"\r\n")))
        self.assertNotEqual(expected,pulse_map_hash(data+b" "))
        changed=data.replace(b'"pulseTimesMs"',b'"changedTimesMs"',1)
        self.assertNotEqual(data,changed)
        self.assertNotEqual(expected,pulse_map_hash(changed))

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

class AudioChoiceRegressionTests(unittest.TestCase):
    def note(self, row, offset=0):
        _, _, _, t, color, side, _ = row
        return dict(time=t-offset, beat=0, x=-1 if side=="blue" else 1, y=0,
                    color=color, type="tap", direction="none", count=1, lengthMs=0)

    def test_exact_catalog_and_reason(self):
        # 採用していない難易度や片方だけの同時ノーツを混ぜない。
        specs = [
            ("h06", "Andalusia", 3949.193115234375, {"easy":"blue"}),
            ("h15", "logic", 46434.25, {"normal":"blue"}),
            ("h16", "logic", 129403.0, {"normal":"red"}),
            ("h17", "logic", 138403.0, {"normal":"red"}),
            ("z01", "2_23_AM", 8075.0, {"easy":"blue","normal":"red","hard":"blue"}),
            ("z02", "logic", 111453.0, {"easy":"red","normal":"red","hard":"red blue"}),
            ("z03", "logic", 113454.0, {"easy":"red","normal":"red","hard":"red blue"}),
            ("z04", "BenkyouRemaster", 24805.0, {"easy":"red","hard":"red"}),
            ("z05", "BenkyouRemaster", 64325.0, {"normal":"blue","hard":"blue"}),
            ("z06", "RushE", 118382.0, {"easy":"gold:red","normal":"gold:blue gold:red",
                                      "hard":"gold:blue gold:red","e":"gold:blue gold:red"}),
            ("z07", "RushE", 50138.0, {"easy":"blue","normal":"blue","hard":"blue","e":"red blue"}),
            ("z08", "RushE", 134770.0, {"easy":"gold:red","normal":"gold:red",
                                      "hard":"gold:red","e":"gold:red"}),
        ]
        expected=set()
        for tag,song,t,diffs in specs:
            for diff,identities in diffs.items():
                for identity in identities.split():
                    color,side=identity.split(":") if ":" in identity else (identity,identity)
                    expected.add((tag,song,diff,t,color,side,
                                  "2026-10-23 ユーザーが音に合わせることを選んだ"))
        self.assertEqual(35,len(AUDIO_CHOICES))
        self.assertEqual(expected,set(AUDIO_CHOICES))
        self.assertEqual((("h15","logic","normal","blue",46153.0,46434.25,281.25,
                           "2026-10-23 ユーザーが音に合わせることを選んだ"),),HAND_CHOICES)

    def test_choice_identity_and_audio_offset_are_required(self):
        for row in AUDIO_CHOICES:
            song,diff=row[1:3]
            n=self.note(row,16)
            c={"offsetMs":16,"notes":[n]}
            self.assertEqual(row,audio_choice(song,diff,c,n))
            self.assertIsNone(audio_choice("Other",diff,c,n))
            self.assertIsNone(audio_choice(song,"other",c,n))
            self.assertIsNone(audio_choice(song,diff,{"offsetMs":0},n))
            for delta in (-.001,.001):
                self.assertIsNone(audio_choice(song,diff,c,dict(n,time=n["time"]+delta)))
            self.assertIsNone(audio_choice(song,diff,c,dict(n,color="default")))
            self.assertIsNone(audio_choice(song,diff,c,dict(n,count=2,lengthMs=1000,type="long")))
            self.assertIsNone(audio_choice(song,diff,c,dict(n,direction="up",type="direction")))
        row=next(r for r in AUDIO_CHOICES if r[:3]==("z08","RushE","easy"))
        n=self.note(row)
        self.assertIsNone(audio_choice("RushE","easy",{},dict(n,x=-1)))

    def test_every_lower_choice_waives_only_its_own_offbeat(self):
        for row in AUDIO_CHOICES:
            tag,song,diff,t,_,_,_=row
            if diff not in ("easy","normal"):continue
            with self.subTest(tag=tag,song=song,diff=diff):
                raw=t-16
                c=chart([raw-3000,raw,raw+1000,raw+4000,raw+7000],flick_at=raw+4000)
                c["offsetMs"]=16
                c["notes"][1]=self.note(row,16)
                pulses=[raw-3000,raw+1000,raw+4000,raw+7000]
                before=pulses[:]
                allowed=audit(c,diff,pulses,lambda _:1000,song=song)
                denied=audit(c,diff,pulses,lambda _:1000,song="Other")
                prefix="Easy offbeat" if diff=="easy" else "unanchored Normal offbeat"
                message=f"{prefix} at {raw:.3f}"
                self.assertNotIn(message,allowed["issues"])
                self.assertIn(message,denied["issues"])
                self.assertEqual(1,allowed["offbeats"])
                self.assertEqual([raw],allowed["offbeatTimesMs"])
                self.assertEqual(before,pulses)
                self.assertIn({"rule":"fixed-pulse","choice":row},allowed["exceptions"])

    def test_unapproved_partner_at_same_time_is_not_exempt(self):
        row=next(r for r in AUDIO_CHOICES if r[:3]==("z08","RushE","easy"))
        n=self.note(row)
        c=chart([134000,134770,136000,139000],flick_at=139000)
        c["notes"][1]=n
        c["notes"].insert(2,dict(n,x=-1))
        issues=audit(c,"easy",[134000,136000,139000],lambda _:1000,song="RushE")["issues"]
        self.assertIn("Easy offbeat at 134770.000",issues)

    def hand_chart(self, a=46153.0, b=46434.25, side="blue", offset=0):
        c=chart([a-offset,b-offset],flick_at=None)
        c["offsetMs"]=offset
        for n in c["notes"]:n.update(color=side,x=-1 if side=="blue" else 1)
        return c

    def test_h15_is_the_only_same_hand_pair(self):
        c=self.hand_chart(offset=16)
        events=cuts(c)
        self.assertEqual(HAND_CHOICES[0],hand_choice("logic","normal",c,"blue",*events))
        result=audit(c,"normal",[n["time"] for n in c["notes"]],lambda _:1000,song="logic")
        self.assertFalse(any("same hand too fast" in x for x in result["issues"]))
        self.assertIn({"rule":"same-hand","choice":HAND_CHOICES[0]},result["exceptions"])
        for song,diff,a,b,side in (
            ("Other","normal",46153,46434.25,"blue"),
            ("logic","easy",46153,46434.25,"blue"),
            ("logic","normal",46153,46434.25,"red"),
            ("logic","normal",40153,40434.25,"red"),
            ("logic","normal",46152,46434.25,"blue"),
            ("logic","normal",46153,46434.251,"blue"),
            ("logic","normal",46154,46435.25,"blue"),
        ):
            c=self.hand_chart(a,b,side)
            events=cuts(c)
            self.assertIsNone(hand_choice(song,diff,c,side,*events))
            issues=audit(c,diff,[a,b],lambda _:1000,song=song)["issues"]
            self.assertTrue(any("same hand too fast" in x for x in issues))

    def test_h15_does_not_exempt_long_cuts_or_same_hand_chords(self):
        c=self.hand_chart()
        c["notes"][0].update(type="long",count=2,lengthMs=100)
        events=cuts(c)
        self.assertIsNone(hand_choice("logic","normal",c,"blue",events[1],events[2]))
        c=self.hand_chart()
        c["notes"].insert(2,dict(c["notes"][1]))
        issues=audit(c,"normal",[46153,46434.25],lambda _:1000,song="logic")["issues"]
        self.assertIn("same-hand chord at 46434.250",issues)
        self.assertIn("same hand too fast: blue 46434.250 -> 46434.250",issues)

    def test_off_on_off_requires_both_offbeats_to_be_selected(self):
        c=chart([108347,110000,111453,112000,113454,116000],flick_at=116000)
        c["notes"][2]["color"]=c["notes"][4]["color"]="red"
        pulses=[110000,112000,116000]
        result=audit(c,"easy",pulses,lambda _:1000,song="logic")
        self.assertIn("Easy offbeat at 108347.000",result["issues"])
        self.assertIn("off-on-off at 108347.000",result["issues"])
        self.assertNotIn("off-on-off at 111453.000",result["issues"])
        self.assertEqual({"z02","z03"},{
            x["choice"][0] for x in result["exceptions"] if x["rule"]=="off-on-off"})
        # 左だけ採用でも右の未採用ノーツの違反は残す。
        c["notes"][4]["time"]=113600
        result=audit(c,"easy",pulses,lambda _:1000,song="logic")
        self.assertIn("off-on-off at 113600.000",result["issues"])

    def test_selected_note_does_not_become_an_anchor(self):
        c=chart([110453,111453,112000,115000],flick_at=115000)
        c["notes"][1]["color"]="red"
        issues=audit(c,"normal",[110000,112000,115000],lambda _:1000,song="logic")["issues"]
        self.assertIn("unanchored Normal offbeat at 110453.000",issues)
        self.assertNotIn("unanchored Normal offbeat at 111453.000",issues)

    def test_onset_choice_does_not_waive_other_guards(self):
        c=chart([111353,111453,113000,119000,120000],flick_at=119000)
        c["notes"][0]["color"]=c["notes"][1]["color"]="red"
        c["notes"][-1].update(type="direction",direction="up")
        issues=audit(c,"easy",[111353,113000,119000,120000],lambda _:1000,song="logic")["issues"]
        self.assertNotIn("Easy offbeat at 111453.000",issues)
        self.assertIn("same hand too fast: red 111353.000 -> 111453.000",issues)
        self.assertIn("Easy real-time interval 100.000 at 111353.000",issues)
        self.assertIn("Easy faster than quarter at 111353.000: 100.000 < 1000.000",issues)
        self.assertIn("gap >= 5000ms",issues)
        self.assertIn("flicks too close",issues)


if __name__=="__main__":
    unittest.main()
