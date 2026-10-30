using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// チュートリアルの進行表(純粋データ)と、タイトルの問いかけ・Game シーンのフラグの約束事。
public class TutorialProgramTests
{
    // 基準復元テスト用の、拡張前のカメラ矩形。
    static Vector2[] CameraCorners(Rect area, float width, float height) => new[] {
        new Vector2(area.xMin * (width - 1), area.yMin * (height - 1)),
        new Vector2(area.xMax * (width - 1), area.yMin * (height - 1)),
        new Vector2(area.xMax * (width - 1), area.yMax * (height - 1)),
        new Vector2(area.xMin * (width - 1), area.yMax * (height - 1)) };

    [TearDown]
    public void ResetState()
    {
        GameSession.TutorialPending = false;
    }

    [Test]
    public void TutorialPending_DefaultFalseAndSettable()
    {
        Assert.IsFalse(GameSession.TutorialPending);
        GameSession.TutorialPending = true;
        Assert.IsTrue(GameSession.TutorialPending);
    }

    [Test]
    public void CommonFlow_StartsWithHandsAndEndsWithReady()
    {
        var steps = TutorialProgram.Build(TutorialProgram.CommonFlow, true);
        Assert.AreEqual(TutorialProgram.CommonFlow.Length, steps.Length, "全部の id が定義されている");
        Assert.AreEqual("hands", steps[0].Id);
        Assert.AreEqual(TutorialStepKind.Explain, steps[0].Kind);
        Assert.AreEqual("ready", steps[steps.Length - 1].Id);
        Assert.AreEqual(TutorialStepKind.Ready, steps[steps.Length - 1].Kind);
        CollectionAssert.AreEqual(
            new[] { "hands", "tapRed", "tapBlue", "tapMix", "gold", "simul", "arrowEasy", "long", "all", "ready" },
            steps.Select(s => s.Id));
    }

    // 2026-10-24: スマホの座標を受けているときだけ、最初に振る範囲を合わせる。ほかの段の並びは変えない。
    [Test]
    public void RangeStep_LeadsTheFlowOnlyWhenThePhoneIsSending()
    {
        CollectionAssert.AreEqual(TutorialProgram.CommonFlow, TutorialProgram.FlowFor(false));
        var flow = TutorialProgram.FlowFor(true);
        Assert.AreEqual(TutorialProgram.RangeStepId, flow[0]);
        CollectionAssert.AreEqual(TutorialProgram.CommonFlow, flow.Skip(1));
        var steps = TutorialProgram.Build(flow, true);
        Assert.AreEqual(flow.Length, steps.Length, "範囲の段も定義されている");
        Assert.AreEqual(TutorialStepKind.Range, steps[0].Kind);
        Assert.AreEqual(0, steps[0].Pattern.Length, "範囲を合わせる間はノーツを出さない");
        StringAssert.Contains("両手", steps[0].Title);
        StringAssert.Contains("振りやすい", steps[0].Title, "限界までではなく、振りやすい範囲で振る(2026-10-24)");
        StringAssert.DoesNotContain("いっぱい", steps[0].Title);
        StringAssert.DoesNotContain("遠く", TutorialProgram.RangeCheer);
        Assert.AreEqual("hands", steps[1].Id);
        Assert.AreEqual(TutorialProgram.RangeStepId, TutorialProgram.Build(flow, false)[0].Id, "持ち方の説明を省いても範囲は合わせる");
    }

    static PhoneSaberAcknowledgement RangeAck(string result, string detail, string station = "B") =>
        new PhoneSaberAcknowledgement(7, station, "ios", result, detail, "phone");

    [TestCase("A", false, false)]
    [TestCase("B", false, false)]
    [TestCase("A", true, false)]
    [TestCase("B", true, false)]
    [TestCase("A", false, true)]
    [TestCase("B", false, true)]
    public void RangeStep_OnlyReportsOkWhenThePcAppliedTheRange(string station, bool rejected, bool missingInput)
    {
        using var commands = new PhoneSaberPositionCalibrationTests.PhoneCommandsScope();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var savedRead = TutorialController.ReadLearnArea;
        var savedApply = TutorialController.ApplyWidenedRange;
        var savedInput = InputPoint.Instance;
        var singleton = typeof(InputPoint).GetProperty(nameof(InputPoint.Instance));
        string key = PhoneSaberPositionCalibrationStore.DataKey(station);
        string enabledKey = PhoneSaberPositionCalibrationStore.EnabledKey(station);
        bool hadData = PlayerPrefs.HasKey(key), hadEnabled = PlayerPrefs.HasKey(enabledKey);
        string savedData = PlayerPrefs.GetString(key); int savedEnabled = PlayerPrefs.GetInt(enabledKey);
        bool hadBaseline = PlayerPrefs.HasKey(TutorialRangeBaseline.PrefsKey);
        string savedBaseline = PlayerPrefs.GetString(TutorialRangeBaseline.PrefsKey);
        var root = new GameObject("TutorialRangeApply");
        try
        {
            // 実運用の A/B 設定と受信機を退避し、指示・ソケットを使わず本番の適用経路を通す。
            singleton.SetValue(null, null);
            TutorialRangeBaseline.Forget();
            var input = missingInput ? null : root.AddComponent<InputPoint>();
            if (input != null)
            {
                input.enabled = false;
                typeof(InputPoint).GetField("phoneSaberStation", flags).SetValue(input, station);
                singleton.SetValue(null, input);
                PhoneSaberPositionCalibrationTests.QueueLearnedRect(station, Rect.MinMaxRect(.1f, .1f, .9f, .9f));
                Assert.IsTrue(input.ApplyLearnedPositionCalibration(Rect.MinMaxRect(.1f, .1f, .9f, .9f), "before", out _));
            }
            string before = PlayerPrefs.GetString(key);
            var learned = rejected ? new Rect(.5f, .5f, .001f, .001f) : Rect.MinMaxRect(.2f, .1f, .8f, .9f);
            string detail = rejected ? "samples300-area0.5_0.5_0.501_0.501" : "samples300-area0.2_0.1_0.8_0.9";
            PhoneSaberDiscoveryResponder.Acknowledgements.Queue(7, "learn-area=10", 100);
            PhoneSaberDiscoveryResponder.Acknowledgements.Receive(RangeAck("learned", detail, station), "phone");
            TutorialController.ReadLearnArea = _ => PhoneSaberDiscoveryResponder.Acknowledgements.Read(7);
            TutorialController.ApplyWidenedRange = (System.Func<Rect, bool>)System.Delegate.CreateDelegate(
                typeof(System.Func<Rect, bool>), typeof(TutorialController).GetMethod("DefaultApplyWidenedRange", BindingFlags.Static | BindingFlags.NonPublic));
            var controller = root.AddComponent<TutorialController>();
            typeof(TutorialController).GetField("steps", flags).SetValue(controller, TutorialProgram.Build(TutorialProgram.FlowFor(true), true));
            typeof(TutorialController).GetField("stepIndex", flags).SetValue(controller, 0);
            typeof(TutorialController).GetField("station", flags).SetValue(controller, station);
            typeof(TutorialController).GetField("stepStart", flags).SetValue(controller, 100d);
            typeof(TutorialController).GetField("range", flags).SetValue(controller, new TutorialRangeLearning(7, 100));
            if (missingInput) LogAssert.Expect(LogType.Warning, "Tutorial: 範囲を適用できませんでした: InputPoint がありません");
            else if (rejected)
            {
                PhoneSaberPositionCalibration.TryFromLearnedArea(learned, input.camWidth, input.camHeight, out _, out string error);
                LogAssert.Expect(LogType.Warning, "Tutorial: 広げた範囲を保存できませんでした: " + error);
                LogAssert.Expect(LogType.Warning, "Tutorial: 範囲を適用できませんでした: " + PhoneSaberPositionCalibration.LearnedAreaResult(learned, false, error));
            }

            typeof(TutorialController).GetMethod("TickRange", flags).Invoke(controller, new object[] { 101d });

            bool applied = !rejected && !missingInput;
            Assert.IsTrue(controller.StepDone);
            Assert.AreEqual(TutorialRangeLearning.Phase.Learned, controller.Range.State, "iPhone はどの場合も学習完了");
            Assert.AreEqual(applied ? TutorialProgram.RangeLearnedTitle : TutorialProgram.RangeKeptTitle, controller.BannerTitle);
            Assert.AreEqual("", controller.BannerSub);
            Assert.AreEqual(applied ? "ok" : "failed", controller.Records.Single().Reason);
            Assert.AreEqual(1d, controller.Records.Single().Seconds);
            Assert.AreEqual(101d + TutorialProgram.RangeEndSeconds, typeof(TutorialController).GetField("stepEndAt", flags).GetValue(controller));
            Assert.AreEqual(applied, controller.AppliedRange.HasValue);
            if (applied)
            {
                Assert.AreEqual(PhoneSaberPositionCalibration.WidenLearnedArea(learned), controller.AppliedRange.Value);
                Assert.AreNotEqual(before, PlayerPrefs.GetString(key));
                StringAssert.Contains("PC適用: 成功", input.LearnedPositionCalibrationResult);
            }
            else Assert.AreEqual(before, PlayerPrefs.GetString(key), "適用できなければ保存済みの補正を維持する");
        }
        finally
        {
            TutorialController.ReadLearnArea = savedRead; TutorialController.ApplyWidenedRange = savedApply;
            Object.DestroyImmediate(root); singleton.SetValue(null, savedInput);
            if (hadData) PlayerPrefs.SetString(key, savedData); else PlayerPrefs.DeleteKey(key);
            if (hadEnabled) PlayerPrefs.SetInt(enabledKey, savedEnabled); else PlayerPrefs.DeleteKey(enabledKey);
            if (hadBaseline) PlayerPrefs.SetString(TutorialRangeBaseline.PrefsKey, savedBaseline); else PlayerPrefs.DeleteKey(TutorialRangeBaseline.PrefsKey);
            PlayerPrefs.Save();
        }
    }

    [Test]
    public void RangeLearning_CountsDownFromThePhonesStartAndEndsOnItsAnswer()
    {
        var range = new TutorialRangeLearning(7, 100);
        Assert.AreEqual(10.0, range.LearnSeconds, 1e-9, "F8 の「範囲を覚える」と同じ10秒");
        Assert.AreEqual(TutorialRangeLearning.Phase.Waiting, range.Update(100.5, new PhoneSaberAcknowledgement[0], "B"));
        Assert.AreEqual(10, range.SecondsLeft(100.5), 1e-9, "学習の開始が返るまでは10秒のまま");
        Assert.AreEqual(TutorialRangeLearning.Phase.Learning, range.Update(101, new[] { RangeAck("learning", "10s") }, "B"));
        Assert.AreEqual(6, range.SecondsLeft(105), 1e-9, "スマホが学習を始めた時刻から数える");
        Assert.AreEqual(0, range.SecondsLeft(112), 1e-9);
        Assert.AreEqual(TutorialRangeLearning.Phase.Learning, range.Update(111.5, new[] { RangeAck("learning", "10s") }, "B"));
        Assert.AreEqual(TutorialRangeLearning.Phase.Learned,
            range.Update(111.6, new[] { RangeAck("learned", "samples300-area0.2_0.1_0.8_0.9") }, "B"));
        Assert.IsTrue(range.Finished);
        Assert.AreEqual(TutorialRangeLearning.Phase.Learned, range.Update(130, null, "B"), "終わったら変わらない");
    }

    [Test]
    public void RangeLearning_MovesOnWithoutAnAnswerOnAFailureAndWhenTheCompletionIsLost()
    {
        var silent = new TutorialRangeLearning(7, 100);
        Assert.AreEqual(TutorialRangeLearning.Phase.Waiting, silent.Update(102.9, null, "B"));
        Assert.AreEqual(TutorialRangeLearning.Phase.NoAnswer, silent.Update(103, null, "B"), "3秒返事がなければ今の範囲で進む");
        var failed = new TutorialRangeLearning(7, 100);
        failed.Update(100.4, new[] { RangeAck("learning", "10s") }, "B");
        Assert.AreEqual(TutorialRangeLearning.Phase.Failed, failed.Update(110.5, new[] { RangeAck("failed", "samples12") }, "B"));
        var lost = new TutorialRangeLearning(7, 100);
        lost.Update(100.4, new[] { RangeAck("learning", "10s") }, "B");
        Assert.AreEqual(TutorialRangeLearning.Phase.Learning, lost.Update(115.3, new[] { RangeAck("learning", "10s") }, "B"));
        Assert.AreEqual(TutorialRangeLearning.Phase.Failed, lost.Update(115.5, new[] { RangeAck("learning", "10s") }, "B"),
            "完了の返事は学習の10秒+5秒まで待つ");
        var direct = new TutorialRangeLearning(7, 100);
        Assert.AreEqual(TutorialRangeLearning.Phase.Learned, direct.Update(100.2, new[] { RangeAck("learned", "samples300") }, "B"),
            "開始の返事を取りこぼしても完了で終わる");
    }

    // 振りやすい範囲の枠をそのまま画面いっぱいにすると剣が大きすぎるので、中心を保って縦横1.5倍に広げてから位置補正にする(2026-10-24)。
    [Test]
    public void RangeLearning_WidensTheEasySwingAreaBeforeCalibrating()
    {
        var learned = Rect.MinMaxRect(.2f, .1f, .8f, .9f);
        var widened = PhoneSaberPositionCalibration.WidenLearnedArea(learned);
        Assert.AreEqual(1.5f, PhoneSaberPositionCalibration.LearnedRangeWidenFactor);
        Assert.AreEqual(.05f, widened.xMin, 1e-5f); Assert.AreEqual(.95f, widened.xMax, 1e-5f);
        Assert.AreEqual(-.1f, widened.yMin, 1e-5f); Assert.AreEqual(1.1f, widened.yMax, 1e-5f);
        Assert.IsTrue(PhoneSaberPositionCalibration.TryFromLearnedArea(learned, 1920, 1080, out var calibration, out _));
        var corners = calibration.CopyCorners();
        var expected = new[] { new Vector2(.05f * 1919, -.1f * 1079), new Vector2(.95f * 1919, -.1f * 1079),
            new Vector2(.95f * 1919, 1.1f * 1079), new Vector2(.05f * 1919, 1.1f * 1079) };
        for (int i = 0; i < 4; i++)
            Assert.Less(Vector2.Distance(expected[i], corners[i]), .01f, "左上→右上→右下→左下 / " + i);
        Assert.IsTrue(PhoneSaberPositionCalibration.TryCreate(corners, out _, out _), "そのまま位置補正にできる");
        const string area = "samples300-area0.200000_0.100000_0.800000_0.900000";
        var mine = new PhoneSaberAcknowledgement(7, "B", "ios", "learned", area, "phone-b");
        var other = new PhoneSaberAcknowledgement(7, "A", "ios", "learned", "samples300-area0.0_0.0_1.0_1.0", "phone-a");
        Assert.IsTrue(TutorialRangeLearning.TryLearnedBounds(new[] { other, mine }, "B", out var bounds));
        Assert.Less(Vector2.Distance(learned.min, bounds.min) + Vector2.Distance(learned.max, bounds.max), 1e-5f, "この台のスマホの枠");
        Assert.IsFalse(TutorialRangeLearning.TryLearnedBounds(new[] { other }, "B", out _), "別の台の枠は使わない");
        var android = new PhoneSaberAcknowledgement(7, "B", "android", "learned", "samples300", "phone-android");
        Assert.IsFalse(TutorialRangeLearning.TryLearnedBounds(new[] { android }, "B", out _), "枠の無い返事では補正しない");
        var loneNone = new PhoneSaberAcknowledgement(7, "none", "ios", "learned", area, "phone-none");
        Assert.IsTrue(TutorialRangeLearning.TryLearnedBounds(new[] { loneNone }, "B", out _), "台を決めていないスマホが1台なら使う");
        Assert.IsFalse(TutorialRangeLearning.TryLearnedBounds(new[] { loneNone, new PhoneSaberAcknowledgement(7, "none", "ios", "learned", area, "phone-2") }, "B", out _),
            "台を決めていないスマホが2台なら選べない");
    }

    // 2026-10-24 ユーザー「B にして」: チュートリアルで合わせた範囲はその人の回だけ。タイトルで台の準備の範囲へ戻す。
    [Test]
    public void RangeBaseline_PhoneAreaFollowsTheMappedCameraRegionWithTheRecognitionMargin()
    {
        var corners = CameraCorners(Rect.MinMaxRect(.2f, .1f, .8f, .9f), 1920, 1080);
        var area = TutorialRangeBaseline.PhoneAreaFor(corners, 1920, 1080);
        Assert.AreEqual(.2f - PhoneSaberCalibrationPlayArea.Margin, area.xMin, 1e-4f, "画面いっぱいに写る範囲＋認識の余白");
        Assert.AreEqual(.8f + PhoneSaberCalibrationPlayArea.Margin, area.xMax, 1e-4f);
        Assert.AreEqual(.1f - PhoneSaberCalibrationPlayArea.Margin, area.yMin, 1e-4f);
        Assert.AreEqual(.9f + PhoneSaberCalibrationPlayArea.Margin, area.yMax, 1e-4f);
        Assert.IsTrue(PhoneSaberCalibrationPlayArea.Valid(area));
        StringAssert.StartsWith("play-area=", PhoneSaberCalibrationPlayArea.Command(area));
        var widened = TutorialRangeBaseline.PhoneAreaFor(
            CameraCorners(Rect.MinMaxRect(.05f, -.1f, .95f, 1.1f), 1920, 1080), 1920, 1080);
        Assert.AreEqual(Rect.MinMaxRect(0, 0, 1, 1), widened, "カメラ画像の外は 0..1 に収める");
        var tiny = TutorialRangeBaseline.PhoneAreaFor(
            CameraCorners(Rect.MinMaxRect(.5f, .5f, .52f, .52f), 1920, 1080), 1920, 1080);
        Assert.AreEqual(PhoneSaberCalibrationPlayArea.MinimumSize, tiny.width, 1e-4f, "最小の幅");
    }

    [Test]
    public void RangeBaseline_RestoresUnlessStaffSetTheStationUpAgain()
    {
        Assert.IsFalse(TutorialRangeBaseline.ShouldRestore(null, "x"));
        var snapshot = new TutorialRangeBaseline.Snapshot { station = "B", hadData = true, data = "base" };
        Assert.IsTrue(TutorialRangeBaseline.ShouldRestore(snapshot, "base"), "範囲の段が終わる前なら戻す");
        snapshot.applied = true; snapshot.appliedData = "tutorial";
        Assert.IsTrue(TutorialRangeBaseline.ShouldRestore(snapshot, "tutorial"), "チュートリアルの補正のままなら戻す");
        Assert.IsFalse(TutorialRangeBaseline.ShouldRestore(snapshot, "staff"), "スタッフが合わせ直していたら残す");

        bool autoStart = PhoneSaberP2PBridgeProcess.AutoStartEnabled;
        bool had = PlayerPrefs.HasKey(TutorialRangeBaseline.PrefsKey);
        string saved = PlayerPrefs.GetString(TutorialRangeBaseline.PrefsKey, "");
        try
        {
            TutorialRangeBaseline.Forget();
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = false;
            TutorialRangeBaseline.Remember("B");
            Assert.IsNull(TutorialRangeBaseline.Load(), "テスト(自動起動を止めている)では覚えない");
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = true;
            TutorialRangeBaseline.Remember("");
            Assert.IsNull(TutorialRangeBaseline.Load(), "台を決めていない PC では覚えない");
            TutorialRangeBaseline.Remember("B");
            var remembered = TutorialRangeBaseline.Load();
            Assert.IsNotNull(remembered); Assert.AreEqual("B", remembered.station); Assert.IsFalse(remembered.applied);
            Assert.AreEqual(PlayerPrefs.GetString(PhoneSaberPositionCalibrationStore.DataKey("B"), ""), remembered.data, "変える前の補正の文字そのまま");
            TutorialRangeBaseline.Remember("B");
            Assert.IsFalse(TutorialRangeBaseline.Load().applied, "まだ戻していない覚え書きは上書きしない");
            TutorialRangeBaseline.Finish("A", true);
            Assert.IsFalse(TutorialRangeBaseline.Load().applied, "別の台の終わりでは印を付けない");
            TutorialRangeBaseline.Finish("B", false);
            var finished = TutorialRangeBaseline.Load();
            Assert.IsNotNull(finished, "覚えなかったときも残す(遅れて覚えたスマホの範囲をタイトルで送り直す)");
            Assert.IsTrue(finished.applied);
            Assert.AreEqual(PlayerPrefs.GetString(PhoneSaberPositionCalibrationStore.DataKey("B"), ""), finished.appliedData);
            TutorialRangeBaseline.Forget();
            Assert.IsNull(TutorialRangeBaseline.Load());
        }
        finally
        {
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = autoStart;
            if (had) PlayerPrefs.SetString(TutorialRangeBaseline.PrefsKey, saved); else PlayerPrefs.DeleteKey(TutorialRangeBaseline.PrefsKey);
            PlayerPrefs.Save();
        }
    }

    [TestCase("A", 0)]
    [TestCase("B", 0)]
    [TestCase("A", 1)]
    [TestCase("B", 1)]
    [TestCase("A", 2)]
    [TestCase("B", 2)]
    public void RangeBaseline_TitleRestoresF8MappingAndPhoneAreaUnlessStaffRelearned(string station, int setup)
    {
        using var commandScope = new PhoneSaberPositionCalibrationTests.PhoneCommandsScope();
        string key = PhoneSaberPositionCalibrationStore.DataKey(station), enabledKey = PhoneSaberPositionCalibrationStore.EnabledKey(station);
        bool hadData = PlayerPrefs.HasKey(key), hadEnabled = PlayerPrefs.HasKey(enabledKey);
        string savedData = PlayerPrefs.GetString(key); int savedEnabled = PlayerPrefs.GetInt(enabledKey);
        bool hadBaseline = PlayerPrefs.HasKey(TutorialRangeBaseline.PrefsKey);
        string savedBaseline = PlayerPrefs.GetString(TutorialRangeBaseline.PrefsKey);
        bool autoStart = PhoneSaberP2PBridgeProcess.AutoStartEnabled;
        var savedInput = InputPoint.Instance;
        var singleton = typeof(InputPoint).GetProperty(nameof(InputPoint.Instance));
        var root = new GameObject("タイトルで台の範囲へ戻すテスト");
        try
        {
            singleton.SetValue(null, null);
            var input = root.AddComponent<InputPoint>(); input.enabled = false;
            typeof(InputPoint).GetField("phoneSaberStation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(input, station);
            singleton.SetValue(null, input);
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = true;
            TutorialRangeBaseline.Forget();
            input.ResetPositionCalibration();
            if (setup != 0)
            {
                PhoneSaberPositionCalibrationTests.QueueLearnedRect(station, Rect.MinMaxRect(.4f, .5f, .6f, .7f));
                Assert.IsTrue(input.ApplyLearnedPositionCalibration(Rect.MinMaxRect(.4f, .5f, .6f, .7f), "F8", out _));
            }
            string baseline = PlayerPrefs.GetString(key, "");
            TutorialRangeBaseline.Remember(station);
            PhoneSaberPositionCalibrationTests.QueueLearnedRect(station, Rect.MinMaxRect(.2f, .1f, .8f, .9f));
            Assert.IsTrue(TutorialController.ApplyLearnedRange(input, Rect.MinMaxRect(.2f, .1f, .8f, .9f)));
            TutorialRangeBaseline.Finish(station, true);
            if (setup == 2)
            {
                PhoneSaberPositionCalibrationTests.QueueLearnedRect(station, Rect.MinMaxRect(.3f, .3f, .7f, .7f));
                Assert.IsTrue(input.ApplyLearnedPositionCalibration(Rect.MinMaxRect(.3f, .3f, .7f, .7f), "staff", out _));
            }
            string beforeTitle = PlayerPrefs.GetString(key, "");
            var commands = PhoneSaberDiscoveryResponder.Acknowledgements;
            commands.Latest(out int before, out _, out _);

            Assert.AreEqual(setup != 2, TutorialRangeBaseline.RestoreAtTitle());

            commands.Latest(out int after, out string fields, out _, out bool automatic);
            Assert.IsNull(TutorialRangeBaseline.Load());
            Assert.AreEqual(setup == 2 ? beforeTitle : baseline, PlayerPrefs.GetString(key, ""));
            Assert.AreEqual(setup != 0, input.PositionCalibrationEnabled);
            Assert.AreEqual(before + (setup == 2 ? 0 : 1), after);
            if (setup != 2)
            {
                Assert.IsTrue(automatic);
                if (setup == 0) Assert.AreEqual(PhoneSaberDiscoveryResponder.ClearAreaCommand, fields);
                else
                {
                    StringAssert.StartsWith("play-area=", fields);
                    var coordinates = System.Array.ConvertAll(fields.Substring("play-area=".Length).Split(','),
                        value => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
                    Assert.That(coordinates, Is.EqualTo(new[] { .27f, .37f, .73f, .83f }).Within(1e-5f));
                }
            }
        }
        finally
        {
            PhoneSaberP2PBridgeProcess.AutoStartEnabled = autoStart;
            Object.DestroyImmediate(root); singleton.SetValue(null, savedInput);
            if (hadData) PlayerPrefs.SetString(key, savedData); else PlayerPrefs.DeleteKey(key);
            if (hadEnabled) PlayerPrefs.SetInt(enabledKey, savedEnabled); else PlayerPrefs.DeleteKey(enabledKey);
            if (hadBaseline) PlayerPrefs.SetString(TutorialRangeBaseline.PrefsKey, savedBaseline); else PlayerPrefs.DeleteKey(TutorialRangeBaseline.PrefsKey);
            PlayerPrefs.Save();
        }
    }

    [Test]
    public void RangeLearning_ReadsThisStationsPhoneFirst()
    {
        var other = RangeAck("learned", "samples300", "A");
        var mine = RangeAck("learning", "10s", "B");
        var none = RangeAck("failed", "samples3", "none");
        Assert.AreEqual("learning", TutorialRangeLearning.Result(new[] { other, mine, none }, "B"), "同じ台の返事だけを見る");
        Assert.AreEqual("failed", TutorialRangeLearning.Result(new[] { other, none }, "B"), "同じ台が無ければ台を決めていないスマホ");
        Assert.AreEqual("", TutorialRangeLearning.Result(new[] { other }, "B"), "別の台の返事は使わない");
        Assert.AreEqual("learned", TutorialRangeLearning.Result(new[] { other, mine, none }, ""), "台名のない PC は全部を見る");
        Assert.AreEqual("", TutorialRangeLearning.Result(null, "B"));
        Assert.AreEqual("あと 10 秒", TutorialProgram.RangeTimerText(10));
        Assert.AreEqual("", TutorialProgram.RangeTimerText(0));
    }

    [Test]
    public void Build_WithoutHands_SkipsTheExplainStep()
    {
        var steps = TutorialProgram.Build(TutorialProgram.CommonFlow, false);
        Assert.IsFalse(steps.Any(s => s.Kind == TutorialStepKind.Explain));
        Assert.AreEqual("tapRed", steps[0].Id);
        Assert.AreEqual(TutorialProgram.CommonFlow.Length - 1, steps.Length);
        Assert.AreEqual(9, steps.Length);
    }

    [TestCase(true, 4)]
    [TestCase(false, 3)]
    public void ReorderedSteps_KeepTheirGoalsDirectionsAndTimings(bool includeHands, int firstIndex)
    {
        var steps = TutorialProgram.Build(TutorialProgram.CommonFlow, includeHands).Skip(firstIndex).Take(4).ToArray();
        CollectionAssert.AreEqual(new[] { "gold", "simul", "arrowEasy", "long" }, steps.Select(s => s.Id));
        Assert.IsTrue(steps.All(s => s.Kind == TutorialStepKind.Practice));
        CollectionAssert.AreEqual(new[] { 1, 2, 2, 3 }, steps.Select(s => s.Goal));
        CollectionAssert.AreEqual(new[] { false, true, false, false }, steps.Select(s => s.Simultaneous));
        CollectionAssert.AreEqual(new[] { false, false, true, false }, steps.Select(s => s.NeedDirection));
        CollectionAssert.AreEqual(new[] { CutDirection.Up, CutDirection.Down }, steps[2].RequiredDirections);
        Assert.IsTrue(steps.Where(s => s.Id != "arrowEasy").All(s => s.RequiredDirections.Length == 0));
        CollectionAssert.AreEqual(new[] { 4.0, 4.0, 4.0, 12.0 }, steps.Select(s => s.PeriodSeconds));
        CollectionAssert.AreEqual(new[] { 15f, 15f, 15f, 30f },
            steps.Select(s => s.TimeoutSeconds > 0 ? s.TimeoutSeconds : TutorialProgram.DefaultStepTimeoutSeconds));
    }

    [Test]
    public void Get_UnknownId_ReturnsNull()
    {
        Assert.IsNull(TutorialProgram.Get("nothing"));
        Assert.IsNull(TutorialProgram.Get(""));
    }

    [Test]
    public void PracticeSteps_AreAchievableInOneLoopAndOrderedInTime()
    {
        foreach (var step in TutorialProgram.Build(TutorialProgram.CommonFlow, true).Where(s => s.Kind == TutorialStepKind.Practice))
        {
            Assert.Greater(step.Pattern.Length, 0, step.Id);
            Assert.Greater(step.Goal, 0, step.Id);
            Assert.LessOrEqual(step.Goal, step.Pattern.Length, step.Id + ": 1 周で合格できる");
            Assert.GreaterOrEqual(step.PeriodSeconds, step.PatternLengthSeconds + 1.0, step.Id + ": 次の周まで最低 1 秒空ける");
            for (int i = 1; i < step.Pattern.Length; i++)
                Assert.GreaterOrEqual(step.Pattern[i].dt, step.Pattern[i - 1].dt, step.Id + ": 時刻順");
            Assert.IsTrue(string.IsNullOrEmpty(step.Title) == false, step.Id + ": 文がある");
        }
    }

    [Test]
    public void MirrorRule_BlueOnTheLeftRedOnTheRightInsideEasyRange()
    {
        foreach (var step in TutorialProgram.Build(TutorialProgram.CommonFlow, true))
        foreach (var n in step.Pattern)
        {
            if (n.color == "red") Assert.Greater(n.x, 0f, step.Id + ": 赤は画面の右");
            else if (n.color == "blue") Assert.Less(n.x, 0f, step.Id + ": 青は画面の左");
            else Assert.AreEqual("gold", n.color, step.Id);
            Assert.That(n.y, Is.InRange(-0.65f, 1.1f), step.Id + ": Easy 譜面の y の範囲");
        }
    }

    [Test]
    public void ToNote_ConvertsSecondsToMillisecondsAndKeepsKind()
    {
        var longNote = TutorialProgram.Get("long").Pattern[0].ToNote(12.5);
        Assert.AreEqual(12500f, longNote.time, .001f);
        Assert.AreEqual(2, longNote.count);
        Assert.AreEqual(500f, longNote.lengthMs, .001f);
        Assert.AreEqual("red", longNote.color);
        Assert.IsTrue(longNote.IsLong);

        var arrow = TutorialProgram.Get("arrowEasy").Pattern[1].ToNote(0);
        Assert.AreEqual(2000f, arrow.time, .001f);
        Assert.AreEqual("down", arrow.direction);
        Assert.AreEqual("blue", arrow.color);
        Assert.IsTrue(arrow.IsDirection);
        Assert.IsFalse(arrow.IsLong);

        var pair = TutorialProgram.Get("simul").Pattern;
        Assert.AreEqual(pair[0].ToNote(3).time, pair[1].ToNote(3).time, .001f, "同時は同じ時刻");
    }

    [Test]
    public void CountsAsSuccess_FollowsEachStepsRule()
    {
        var tap = TutorialProgram.Get("tapRed");
        var arrow = TutorialProgram.Get("arrowEasy");
        var longStep = TutorialProgram.Get("long");
        var hands = TutorialProgram.Get("hands");
        // 2026-10-30: 残り回数が減るのは GREAT 以上(10/28〜10/30 は PERFECT だけ)。切れても GOOD 以下は数えない。
        Assert.IsTrue(TutorialProgram.CountsAsSuccess(tap, JudgmentTier.Perfect, true, 1, 1));
        Assert.IsTrue(TutorialProgram.CountsAsSuccess(tap, JudgmentTier.Perfect, false, 1, 1), "タップは向きを問わない");
        Assert.IsTrue(TutorialProgram.CountsAsSuccess(tap, JudgmentTier.Great, true, 1, 1), "GREAT も数える(2026-10-30 から)");
        Assert.IsFalse(TutorialProgram.CountsAsSuccess(tap, JudgmentTier.Good, true, 1, 1));
        Assert.IsFalse(TutorialProgram.CountsAsSuccess(tap, JudgmentTier.Bad, true, 1, 1));
        Assert.IsFalse(TutorialProgram.CountsAsSuccess(tap, JudgmentTier.Miss, true, 1, 0));
        Assert.IsFalse(TutorialProgram.CountsAsSuccess(arrow, JudgmentTier.Perfect, false, 1, 1), "矢印は向きが違えば数えない");
        Assert.IsTrue(TutorialProgram.CountsAsSuccess(arrow, JudgmentTier.Perfect, true, 1, 1));
        Assert.IsTrue(TutorialProgram.CountsAsSuccess(arrow, JudgmentTier.Great, true, 1, 1));
        // 本編は 2026-10-20 から、ロングを1回でも残すと残りが MISS。練習も全部切ったら成功。
        Assert.IsFalse(TutorialProgram.CountsAsSuccess(longStep, JudgmentTier.Miss, true, 3, 2), "2/3 は残しがあるので数えない");
        Assert.IsFalse(TutorialProgram.CountsAsSuccess(longStep, JudgmentTier.Miss, true, 3, 1));
        Assert.IsTrue(TutorialProgram.CountsAsSuccess(longStep, JudgmentTier.Perfect, true, 3, 3), "全回数を切り切ったら成功");
        Assert.IsFalse(TutorialProgram.CountsAsSuccess(longStep, JudgmentTier.Miss, true, 3, 3), "MISS は成功にしない");
        Assert.IsFalse(TutorialProgram.CountsAsSuccess(hands, JudgmentTier.Perfect, true, 1, 1));
        Assert.IsFalse(TutorialProgram.CountsAsSuccess(null, JudgmentTier.Perfect, true, 1, 1));
    }

    // 2026-10-28: 最後の「練習してみよう」に、今まで練習した7種類が1つずつ出る。種類ごとに1回 PERFECT で合格。
    [Test]
    public void AllStep_ShowsEveryPracticedKindOnceBeforeReady()
    {
        var flow = TutorialProgram.CommonFlow;
        Assert.AreEqual("all", flow[flow.Length - 2], "準備OKの直前");
        var step = TutorialProgram.Get("all");
        Assert.AreEqual(TutorialStepKind.Practice, step.Kind);
        StringAssert.Contains("練習してみよう", step.Title);
        CollectionAssert.AreEquivalent(new[] { "red", "blue", "gold", "simul", "up", "down", "long" }, step.Kinds);
        Assert.AreEqual(step.Kinds.Length, step.Goal);
        CollectionAssert.AreEquivalent(step.Kinds, step.Pattern.Select(n => n.kind).Distinct(), "どの種類も型に出る");
        Assert.IsTrue(step.Pattern.Any(n => n.color == "gold"));
        Assert.IsTrue(step.Pattern.Any(n => n.count > 1), "ロング");
        var arrows = step.Pattern.Where(n => n.type == "direction").Select(n => n.direction).ToArray();
        CollectionAssert.AreEquivalent(new[] { "up", "down" }, arrows);
        var pair = step.Pattern.Where(n => n.kind == "simul").ToArray();
        Assert.AreEqual(2, pair.Length); Assert.AreEqual(pair[0].dt, pair[1].dt, "両手は同じ時刻");
        Assert.AreNotEqual(pair[0].color, pair[1].color);
        Assert.AreEqual(30f, step.TimeoutSeconds, .001f, "型2周で次へ(今までどおり時間でも進む)");
        Assert.GreaterOrEqual(step.TimeoutSeconds, 2 * step.PeriodSeconds);
        Assert.AreEqual("GREAT 以上であと 7 種類", TutorialProgram.RemainingText(step, 0, 0, false));
        Assert.AreEqual("GREAT 以上であと 1 種類", TutorialProgram.RemainingText(step, 6, 0, false));
    }

    // 判定の通知から種類の数へ。型の届く時刻で種類を引き、GREAT 以上だけ・種類ごとに1回・両手は組で数える。
    [Test]
    public void AllStep_CountsEachKindOnceOnlyOnPerfectAndPairsNeedBothHands()
    {
        var root = new GameObject("TutorialAll");
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var controller = root.AddComponent<TutorialController>();
            var step = TutorialProgram.Get("all");
            typeof(TutorialController).GetField("steps", flags).SetValue(controller, new[] { step });
            typeof(TutorialController).GetField("stepIndex", flags).SetValue(controller, 0);
            // ExtendPattern が spawner.EffectiveTime で入れるのと同じ、届く時刻(ミリ秒)→種類
            var kinds = (System.Collections.Generic.Dictionary<long, string>)typeof(TutorialController).GetField("kindByHit", flags).GetValue(controller);
            foreach (double start in new[] { 10.0, 10.0 + step.PeriodSeconds })
                foreach (var p in step.Pattern) kinds[(long)System.Math.Round((start + p.dt) * 1000.0)] = p.kind;
            void Judge(double hit, SaberHand hand, JudgmentTier tier, int count = 1)
            {
                var go = new GameObject("Note");
                go.transform.SetParent(root.transform);
                var note = go.AddComponent<CuttableNote>();
                note.HitTime = hit; note.RequiredHand = hand;
                note.RequiredCutCount = count; note.RemainingCuts = 0;
                typeof(TutorialController).GetMethod("WatchNote", flags).Invoke(controller, new object[] { note });
                typeof(TutorialController).GetMethod("Judged", flags).Invoke(controller, new object[] { note, tier, Vector3.zero, Vector3.up * 8 });
            }

            Judge(10, SaberHand.Right, JudgmentTier.Good);
            Assert.AreEqual(0, controller.Successes, "GOOD は数えない");
            Judge(10, SaberHand.Right, JudgmentTier.Perfect);
            Judge(22, SaberHand.Right, JudgmentTier.Perfect);
            Assert.AreEqual(1, controller.Successes, "赤を2周取っても1種類");
            Judge(14, SaberHand.Left, JudgmentTier.Perfect);
            Assert.AreEqual(1, controller.Successes, "両手の片方だけでは数えない");
            Judge(14, SaberHand.Right, JudgmentTier.Perfect);
            Assert.AreEqual(2, controller.Successes);
            Judge(11, SaberHand.Left, JudgmentTier.Great);
            Judge(12, SaberHand.Right, JudgmentTier.Perfect);
            Judge(16, SaberHand.Right, JudgmentTier.Perfect);
            Judge(17, SaberHand.Left, JudgmentTier.Good);
            Assert.AreEqual(5, controller.Successes, "下矢印は GOOD なので残る");
            Assert.AreEqual("GREAT 以上であと 2 種類", TutorialProgram.RemainingText(step, controller.Successes, 0, false));
            Judge(17, SaberHand.Left, JudgmentTier.Perfect);
            Judge(19, SaberHand.Right, JudgmentTier.Perfect, 3);
            Assert.AreEqual(7, controller.Successes);
            Assert.IsTrue(TutorialProgram.PracticeGoalReached(step, controller.Successes, new System.Collections.Generic.HashSet<CutDirection>()));
        }
        finally { Object.DestroyImmediate(root); }
    }

    [Test]
    public void RemainingText_SaysOnlyPerfectCounts()
    {
        Assert.AreEqual("GREAT 以上であと 2 回", TutorialProgram.RemainingText(TutorialProgram.Get("tapRed"), 0, 0, false));
        Assert.AreEqual("GREAT 以上であと 1 回", TutorialProgram.RemainingText(TutorialProgram.Get("gold"), 0, 0, false), "1回の段も出す");
        Assert.AreEqual("GREAT 以上であと 1 組", TutorialProgram.RemainingText(TutorialProgram.Get("simul"), 0, 0, false));
        Assert.AreEqual("あと 3 種類", TutorialProgram.RemainingText(TutorialProgram.Get("long"), 0, 0, false), "ロングは切り切れば PERFECT");
        Assert.AreEqual("", TutorialProgram.RemainingText(TutorialProgram.Get("tapRed"), 2, 0, true));
        StringAssert.Contains("早い", TutorialProgram.NotPerfectHint(-60));
        StringAssert.Contains("遅い", TutorialProgram.NotPerfectHint(100));
    }

    [TestCase(0, 0)]
    [TestCase(.001, 2)]
    [TestCase(1.999, 2)]
    [TestCase(2, 2)]
    [TestCase(2.001, 4)]
    [TestCase(127.8, 128)]
    public void PracticeStartsOnTheNextDownbeatWithoutShorteningReadingTime(double earliest, double expected)
    {
        Assert.AreEqual(expected, TutorialProgram.NextBarAtOrAfter(earliest), 1e-9);
        Assert.GreaterOrEqual(TutorialProgram.NextBarAtOrAfter(earliest), earliest);
    }

    [Test]
    public void EveryPracticeAndLongStrokeStaysOnThe120BpmGridAcrossLoops()
    {
        Assert.AreEqual(120, TutorialProgram.Bpm);
        foreach (var id in TutorialProgram.CommonFlow.Concat(new[] { "arrowNormal" }))
        {
            var step = TutorialProgram.Get(id);
            if (step.Kind != TutorialStepKind.Practice) continue;
            for (int loop = 0; loop < 50; loop++)
            foreach (var pattern in step.Pattern)
            {
                var note = pattern.ToNote(8 + loop * step.PeriodSeconds);
                for (int cut = 0; cut < pattern.count; cut++)
                {
                    double time = note.TimeSeconds + (pattern.count > 1 ? cut * pattern.lengthMs / (pattern.count - 1) / 1000.0 : 0);
                    double beats = time / TutorialProgram.BeatSeconds;
                    Assert.AreEqual(System.Math.Round(beats), beats, 1e-6, id + ": 周回後も各カットが表拍に合う");
                }
            }
        }
    }

    [Test]
    public void Hints_NameTheHandAndTheDirection()
    {
        StringAssert.Contains("左手", TutorialProgram.MissHint("blue", false));
        StringAssert.Contains("右手", TutorialProgram.MissHint("red", false));
        StringAssert.Contains("どちらの手", TutorialProgram.MissHint("gold", false));
        StringAssert.Contains("矢印", TutorialProgram.MissHint("red", true));
        Assert.AreEqual("左上", TutorialProgram.DirectionJapanese(CutDirection.UpLeft));
        Assert.AreEqual("下", TutorialProgram.DirectionJapanese(CutDirection.Down));
        Assert.AreEqual("", TutorialProgram.DirectionJapanese(CutDirection.None));
    }

    [Test]
    public void Timings_MatchTheWebPrototype()
    {
        Assert.AreEqual(6f, TutorialProgram.HandsTimeoutSeconds, .001f);
        Assert.AreEqual(15f, TutorialProgram.DefaultStepTimeoutSeconds, .001f);
        Assert.AreEqual(.9, TutorialProgram.ReadDelaySeconds, .0001);
        Assert.AreEqual(10f, TitleTutorialPrompt.DefaultTimeoutSeconds, .001f);
        Assert.AreEqual(TitlePresentationMotion.DepartureDuration, TitleTutorialPrompt.RevealDelaySeconds, .001f);
        Assert.Less(TitleTutorialPrompt.YesPosition.x, 0f, "はい は画面の左");
        Assert.Greater(TitleTutorialPrompt.NoPosition.x, 0f, "いいえ は画面の右");
        // 2026-10-27: 間を大きく空ける(中心どうし 12)。どちらも剣が届く画面の中央91%に収まる(z=0 で約±9.3)。
        Assert.GreaterOrEqual(TitleTutorialPrompt.NoPosition.x - TitleTutorialPrompt.YesPosition.x, 12f - .001f);
        float reach = .91f * 10f * Mathf.Tan(30f * Mathf.Deg2Rad) * 16f / 9f;
        Assert.Less(Mathf.Abs(TitleTutorialPrompt.YesPosition.x) + .6f, reach);
        Assert.Less(Mathf.Abs(TitleTutorialPrompt.NoPosition.x) + .6f, reach);
        Assert.AreEqual("Game", TitleSceneSkin.TutorialSceneName);
    }

    [Test]
    public void SimultaneousPracticeRequiresBothHandsInTheSamePair()
    {
        var pairs = new TutorialPairProgress();
        Assert.False(pairs.Register(4, SaberHand.Right));
        Assert.False(pairs.Register(8, SaberHand.Right), "別の回の赤を切っても合格にしない");
        Assert.False(pairs.Register(4, SaberHand.Right), "同じ手の重複通知も数えない");
        Assert.True(pairs.Register(4, SaberHand.Left));
        Assert.False(pairs.Register(4, SaberHand.Left), "完成した組を二度数えない");
        Assert.False(pairs.Register(8, SaberHand.Any));
        pairs.Clear();
        Assert.False(pairs.Register(8, SaberHand.Left), "前の段の成功を持ち越さない");
    }
}
