using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// 実績(2026-10-29)を本物の結果画面と本編で確かめる。
// ・結果画面: 右上に出て、一度に2枚まで、1枚ずつ実時間5秒以内に消え、操作を止めない。曲名と判定の表に重ならず、段の順と鈴の鍵を確かめる。
//   曲の終わり(GameSession.RunRecording)で1回だけ決め、演奏の画面では出さず、ランクの札の着地か演出を飛ばしたときに出す。運営モードの回は本番の数に入る。
// ・本編: 本物の Game 場面でアンダルシアの Easy の出だし(同時の青と赤の組が2つ)を届く時刻ちょうどの振りで切り、初太刀・X 斬り・連続 PERFECT・
//   曲を始めた/終えた時刻を記録が正しく数える。
// ChartMarkPlayTests と同じく、照準の受信はテスト専用の空いたポート、入力の設定はテスト用の写し。作った常駐物はテストの後で消し、幕の終わりを待つ。
// 実績の記録は専用の鍵だけを使い、テストの後で消す。
public class AchievementToastPlayTests
{
    const string Key = "Achievements.Log.v1.__AchievementToastPlayTest";
    readonly Dictionary<FieldInfo, object> session = new Dictionary<FieldInfo, object>();
    readonly HashSet<GameObject> persistentBefore = new HashSet<GameObject>();
    string song, savedKey;
    AchievementTracker tracker;
    InputPoint input;
    InputSettings originalInputSettings, testInputSettings;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        foreach (var root in PersistentRoots()) persistentBefore.Add(root);
        foreach (var field in typeof(GameSession).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (!field.IsLiteral && !field.IsInitOnly) session[field] = field.GetValue(null);
        GameSession.SetPlayerSettingsForTest(true);
        originalInputSettings = InputSystem.settings;
        testInputSettings = Object.Instantiate(originalInputSettings);
        InputSystem.settings = testInputSettings;
        // 結果画面・本編の照準が受信を始めるので、テスト専用のポートを先に決める(既定の 5005/5006 を開かない)。
        foreach (var old in Object.FindObjectsByType<InputPoint>(FindObjectsSortMode.None)) Object.DestroyImmediate(old.gameObject);
        var receiver = new GameObject("AchievementTestInput");
        receiver.SetActive(false);
        Object.DontDestroyOnLoad(receiver);
        input = receiver.AddComponent<InputPoint>();
        input.port = FreePort();
        do { input.port2 = FreePort(); } while (input.port2 == input.port);
        receiver.SetActive(true);
        InputPoint.EnsureInstance();
        savedKey = AchievementStore.Key;
        PlayerPrefs.DeleteKey(Key);
        AchievementStore.UseKeyForTest(Key);
        song = "__AchievementToast_" + Guid.NewGuid().ToString("N");
        GameSession.SelectedSongId = song;
        GameSession.SelectedSongTitle = "Achievement toast";
        GameSession.SelectedDifficulty = "Normal";
        GameSession.IsCalibrationMode = false;
        GameSession.ResetResult();
        GameSession.FinalScore = 30123;
        GameSession.FinalHit = GameSession.FinalPerfect = 10;
        GameSession.FinalMaxCombo = 10;
        GameSession.FinalCutCount = 10;
        AchievementRunInfo.Clear();
        yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (tracker != null) Object.Destroy(tracker.gameObject);
        tracker = null;
        foreach (var manager in Object.FindObjectsByType<GamePlayManager>(FindObjectsSortMode.None))
            if (manager.songPlayer != null) manager.songPlayer.Stop();
        double deadline = Time.realtimeSinceStartupAsDouble + 10;
        while (ScreenTransition.IsBusy && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        var scene = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(SceneManager.CreateScene(song + "_cleanup"));
        if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
        if (input != null) Object.DestroyImmediate(input.gameObject);
        input = null;
        // テストの間に作られた常駐物(照準の受信・効果音の再生役・入力の橋など)を残さない。
        foreach (var root in PersistentRoots())
            if (!persistentBefore.Contains(root)) Object.DestroyImmediate(root);
        persistentBefore.Clear();
        if (originalInputSettings != null) InputSystem.settings = originalInputSettings;
        if (testInputSettings != null) Object.DestroyImmediate(testInputSettings);
        OperatorMode.OverrideForTest(false);
        foreach (string ns in new[] { OperatorMode.OrdinaryNamespace, OperatorMode.FestivalNamespace })
        {
            HighScoreStore.Clear(ns, song, "Normal");
            PlayerPrefs.DeleteKey(DailyRankingStore.Key(ns, song, "Normal"));
            PlayerPrefs.DeleteKey(SongAchievementStore.Key(ns, song, "Normal"));
        }
        PlayerPrefs.DeleteKey(Key);
        PlayerPrefs.Save();
        AchievementStore.UseKeyForTest(savedKey);
        AchievementRunInfo.Clear();
        DisplaySettings.ResetReducedEffectsCacheForTest();
        foreach (var entry in session) entry.Key.SetValue(null, entry.Value);
        session.Clear();
        GameSession.SetPlayerSettingsForTest(false);
    }

    static int FreePort()
    {
        using (var socket = new UdpClient(0)) return ((IPEndPoint)socket.Client.LocalEndPoint).Port;
    }

    // シーンをまたいで残る物(DontDestroyOnLoad)の根。
    static GameObject[] PersistentRoots()
    {
        var probe = new GameObject("AchievementPersistentProbe");
        Object.DontDestroyOnLoad(probe);
        var roots = probe.scene.GetRootGameObjects();
        Object.DestroyImmediate(probe);
        var result = new List<GameObject>();
        foreach (var root in roots) if (root != null && root != probe) result.Add(root);
        return result.ToArray();
    }

    static IEnumerable<AchievementUnlock> Unlocks(params (string id, int number)[] items)
    {
        foreach (var (id, number) in items) yield return new AchievementUnlock(AchievementCatalog.Find(id), number);
    }

    static string BodyOf(RectTransform card) => card.Find("Body").GetComponent<TMP_Text>().text;

    static IEnumerator LoadResult()
    {
        yield return SceneManager.LoadSceneAsync("Result");
        double deadline = Time.realtimeSinceStartupAsDouble + 10;
        while (Object.FindFirstObjectByType<ResultReveal>() == null && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Assert.NotNull(Object.FindFirstObjectByType<ResultReveal>());
    }

    // 曲の終わりの記録(相手の台へ送る記録を作る前に、トラッカーが実績を決める)。
    void FinishRun()
    {
        tracker = AchievementTracker.CreateForTest();
        GameSession.RecordCompletedDailyRanking();
    }

    [UnityTest]
    public IEnumerator CardsAppearAtTheTopRightTwoAtATimeAndEachLeavesWithinFiveSeconds()
    {
        // 入りと消えの動きを確かめるので、保存してある F3(演出を下げる)の設定に左右されないようにする。
        DisplaySettings.SetReducedEffectsForTest(false);
        yield return LoadResult();
        var reveal = Object.FindFirstObjectByType<ResultReveal>();
        reveal.enabled = false;
        reveal.Tick(999);
        int chimes = MenuSfx.PlayCount;
        var unlocks = new List<AchievementUnlock>(Unlocks(("fc", 3), ("combo50", 12), ("clear", 152)));
        var expected = unlocks.OrderBy(u => u.Tier).ToArray();
        AchievementToast.Show(unlocks);
        var host = AchievementToast.Instance;
        Assert.NotNull(host);
        var canvas = host.GetComponent<Canvas>();
        Assert.AreEqual(RenderMode.ScreenSpaceOverlay, canvas.renderMode);
        Assert.AreEqual(AchievementToast.SortingOrder, canvas.sortingOrder);
        Assert.IsNull(host.GetComponent<GraphicRaycaster>(), "知らせは操作を受けない");
        foreach (var graphic in host.GetComponentsInChildren<Graphic>(true))
            Assert.IsFalse(graphic.raycastTarget, graphic.name + " は raycastTarget=false(結果画面の操作を止めない)");

        var shownAt = new Dictionary<string, double>();
        var lastSeenAt = new Dictionary<string, double>();
        var goneAt = new Dictionary<string, double>();
        var previous = new HashSet<string>();
        var order = new List<string>();
        var settled = new HashSet<string>();
        int maxVisible = 0;
        double deadline = Time.realtimeSinceStartupAsDouble + 20;
        while (AchievementToast.Instance != null && Time.realtimeSinceStartupAsDouble < deadline)
        {
            var now = new HashSet<string>();
            double time = Time.realtimeSinceStartupAsDouble;
            foreach (var card in AchievementToast.Instance.VisibleCards())
            {
                string body = BodyOf(card);
                now.Add(body);
                if (!shownAt.ContainsKey(body)) { shownAt[body] = time; order.Add(body); }
                lastSeenAt[body] = time;
                Assert.AreEqual(Vector2.one, card.anchorMin);
                Assert.AreEqual(Vector2.one, card.anchorMax);
                Assert.AreEqual(Vector2.one, card.pivot);
                Assert.AreEqual(new Vector2(540f, 126f), card.sizeDelta);
                int slot = card.GetSiblingIndex();
                Assert.That(slot, Is.InRange(0, 1));
                var corners = new Vector3[4];
                card.GetWorldCorners(corners);
                float scale = canvas.scaleFactor;
                Assert.AreEqual(-AchievementToast.SlotPosition(slot).y * scale, Screen.height - corners[2].y, 2f,
                    "上の余白18px、2枚目は138px下");
                Assert.LessOrEqual(Screen.height - corners[0].y, (AchievementToast.CoveredHeight + 1) * scale,
                    "上から282pxに収まる");
                Assert.That(card.anchoredPosition.x,
                    Is.InRange(-AchievementToast.RightMargin - .01f, -AchievementToast.RightMargin + AchievementToast.EnterSlide + .01f));
                var queued = AchievementToast.Instance.Queue.At(slot);
                double age = time - queued.StartedAt;
                // 止まっている間(後ろに待ちがある段なし・銅のカードは短い寿命。約1秒のカードは入りと消えも短い。消え始めると右へ抜ける)。
                bool fast = queued.Life < AchievementToast.ShortLifeSeconds - 1e-4f;
                double enter = fast ? AchievementToast.FastEnterSeconds : AchievementToast.EnterSeconds;
                double exit = fast ? AchievementToast.FastExitSeconds : AchievementToast.ExitSeconds;
                if (age > enter + .05 && age < queued.Life - exit - .05)
                {
                    Assert.AreEqual(AchievementToast.RightMargin * scale, Screen.width - corners[2].x, 2f, "止まっている間は右24px");
                    settled.Add(body);
                }
            }
            foreach (string body in previous) if (!now.Contains(body)) goneAt[body] = time;
            maxVisible = Mathf.Max(maxVisible, now.Count);
            previous = now;
            yield return null;
        }
        // 最後の札が消えたコマで部品ごと消えるので、そのとき見えていた札はそこで消えた扱い。
        if (AchievementToast.Instance == null)
            foreach (string body in previous) if (!goneAt.ContainsKey(body)) goneAt[body] = Time.realtimeSinceStartupAsDouble;
        Assert.IsNull(AchievementToast.Instance, "全部消えたら知らせの部品も消える");
        Assert.AreEqual(3, shownAt.Count);
        Assert.AreEqual(3, settled.Count, "全カードの右の余白を確かめた");
        Assert.AreEqual(2, maxVisible, "一度に見えるのは2枚まで");
        CollectionAssert.AreEqual(expected.Select(u => AchievementToast.Message(u.Text, u.Number)).ToArray(), order,
            "渡された順のうち段の低いものから出る");
        foreach (var pair in shownAt)
        {
            Assert.IsTrue(goneAt.ContainsKey(pair.Key), pair.Key);
            // 見えていた最後のコマで測る(期限のあとの重いコマで、消えたのを見るのが遅れても失敗にしない)。
            Assert.Less(lastSeenAt[pair.Key] - pair.Value, 5, pair.Key + " は実時間5秒以内に消える");
        }
        Assert.IsTrue(shownAt.ContainsKey(AchievementToast.Message("1曲を最後まで斬りきった", 152)), "「〜した ○人目」");
        Assert.Greater(MenuSfx.PlayCount, chimes);
        Assert.AreEqual(AchievementToast.ChimeKeyFor(expected[expected.Length - 1].Tier), MenuSfx.LastPlayed, "最後のカードの段の鈴");
    }

    static AchievementUnlock TierUnlock(string text, AchievementTier tier) =>
        new AchievementUnlock(new AchievementCatalog.Definition(text, text, "出さない名前", "試験", tier, _ => true), 7);

    static Rect ScreenRect(RectTransform rect)
    {
        var canvas = rect.GetComponentInParent<Canvas>();
        Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]), max = min;
        for (int i = 1; i < corners.Length; i++)
        {
            Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, corners[i]);
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    [UnityTest]
    public IEnumerator LongestSongTitleAndEveryJudgeRowStayClearOfBothCards()
    {
        GameSession.SelectedSongId = "Aobasou";
        GameSession.SelectedSongTitle = SongDisplayInfo.Get("Aobasou").DisplayName;
        GameSession.FinalAimMode = false;
        // 本物の曲 ID で結果画面を開くので、記録しない回にする(HI-SCORE・順位に残さない。点検 E)。
        GameSession.FinalCutCount = 0;
        GameSession.FinalHit = GameSession.FinalPerfect = GameSession.FinalGreat = GameSession.FinalGood = 0;
        GameSession.FinalBad = GameSession.FinalMiss = 0;
        Assert.IsFalse(GameSession.ShouldRecordRun);
        Assert.AreEqual("青葉騒 feat.初音ミク", GameSession.SelectedSongTitle);
        yield return LoadResult();
        var reveal = Object.FindFirstObjectByType<ResultReveal>();
        reveal.enabled = false;
        reveal.Tick(999);
        var titleObject = GameObject.Find("TitleRow");
        Assert.NotNull(titleObject);
        var title = titleObject.GetComponent<RectTransform>();
        var obstacles = new List<RectTransform> { title };
        foreach (string label in ResultSkin.JudgeLabels(false))
        {
            var row = title.parent.Find("Judge" + label);
            Assert.NotNull(row, label + "の実物の行");
            obstacles.Add(row.GetComponent<RectTransform>());
        }
        Assert.AreEqual(6, obstacles.Count, "曲名と判定5行");

        bool savedReduced = DisplaySettings.ReducedEffects;
        var host = AchievementToast.Ensure();
        host.enabled = false;
        try
        {
            foreach (bool reduced in new[] { false, true })
            {
                DisplaySettings.SetReducedEffectsForTest(reduced);
                host.Queue.Clear();
                AchievementToast.Show(new[]
                {
                    TierUnlock("長い曲名の結果画面で最初の条件を達成した", AchievementTier.Rainbow),
                    TierUnlock("長い曲名の結果画面で次の条件も達成した", AchievementTier.Rainbow),
                });
                double start = Time.realtimeSinceStartupAsDouble;
                host.Step(start, true);
                host.Step(start + AchievementToast.StaggerSeconds, true);
                foreach (double age in new[] { .13, .3, .6, 1.5, 4.1, 4.49 })
                {
                    host.Step(start + age, true);
                    Canvas.ForceUpdateCanvases();
                    var cards = host.VisibleCards().ToArray();
                    Assert.AreEqual(2, cards.Length);
                    foreach (var card in cards)
                    {
                        Rect bounds = ScreenRect(card);
                        float glow = AchievementCardFrame.MaxGlowWidth * bounds.width / AchievementToast.CardWidth;
                        Rect withGlow = Rect.MinMaxRect(bounds.xMin - glow, bounds.yMin - glow, bounds.xMax + glow, bounds.yMax + glow);
                        foreach (var obstacle in obstacles)
                        {
                            Rect other = ScreenRect(obstacle);
                            Assert.Greater(other.width, 0f);
                            Assert.Greater(other.height, 0f);
                            Assert.IsFalse(bounds.Overlaps(other), card.name + "が" + obstacle.name + "に重ならない");
                            Assert.IsFalse(withGlow.Overlaps(other), "虹の光も" + obstacle.name + "に重ならない");
                        }
                    }
                }
            }
        }
        finally { DisplaySettings.SetReducedEffectsForTest(savedReduced); }
        yield return null;
    }

    [UnityTest]
    public IEnumerator EachTierPlaysItsOwnChimeKeyWhenItsCardStarts()
    {
        yield return LoadResult();
        var reveal = Object.FindFirstObjectByType<ResultReveal>();
        reveal.enabled = false;
        reveal.Tick(999);
        var host = AchievementToast.Ensure();
        host.enabled = false;
        var tiers = new[] { AchievementTier.None, AchievementTier.Bronze, AchievementTier.Silver, AchievementTier.Gold, AchievementTier.Rainbow };
        var keys = new[] { "AchievementChime", "AchievementChimeBronze", "AchievementChimeSilver", "AchievementChimeGold", "AchievementChimeRainbow" };
        AchievementToast.Show(tiers.Reverse().Select(t => TierUnlock(t + "の条件を達成した", t)));
        int count = MenuSfx.PlayCount;
        double start = Time.realtimeSinceStartupAsDouble;
        for (int i = 0; i < tiers.Length; i++)
        {
            host.Step(start + i * 5, true);
            Assert.AreEqual(tiers[i], host.Queue.At(0).Tier, "段の低いものから出る");
            Assert.AreEqual(keys[i], MenuSfx.LastPlayed);
            Assert.AreEqual(count + i + 1, MenuSfx.PlayCount);
        }
        yield return null;
    }

    [UnityTest]
    public IEnumerator AFinishedRunIsDecidedOnceAndShownWhenTheRankLands()
    {
        SceneManager.SetActiveScene(SceneManager.CreateScene(song + "_play"));
        FinishRun();
        Assert.AreEqual("clear", GameSession.FinalAchievements[0], "相手の台へ送る記録より前に決めてある");
        Assert.AreEqual(1, AchievementStore.Count("", "clear"));
        CollectionAssert.Contains(AchievementStore.UnlockedIn("", GameSession.AchievementRunId), "clear");
        var unlocks = tracker.LastUnlocks;
        Assert.AreEqual("clear", unlocks[0].Id, "完走がいちばん簡単(最初に出る)");
        Assert.AreEqual(1, unlocks[0].Number);
        // 同じ回の記録をもう一度呼んでも決め直さない
        GameSession.RecordCompletedDailyRanking();
        Assert.AreEqual(1, AchievementStore.Count("", "clear"));
        // 結果画面のほか(演奏の画面)では出さない
        for (int i = 0; i < 30; i++)
        {
            Assert.IsNull(AchievementToast.Instance, "結果画面に入る前は出さない");
            yield return null;
        }

        yield return LoadResult();
        var reveal = Object.FindFirstObjectByType<ResultReveal>();
        float revealTime = 0;
        reveal.TimeChanged += t => revealTime = t;
        RectTransform card = null;
        double deadline = Time.realtimeSinceStartupAsDouble + AchievementToast.ResultLandingSeconds + 8;
        while (card == null && Time.realtimeSinceStartupAsDouble < deadline)
        {
            if (AchievementToast.Instance != null)
                foreach (var visible in AchievementToast.Instance.VisibleCards()) { card = visible; break; }
            if (card == null) yield return null;
        }
        Assert.NotNull(card, "結果画面で出る");
        Assert.GreaterOrEqual(revealTime, AchievementToast.ResultLandingSeconds, "ランクの札が着地してから(結果の演出の時計で)");
        double shown = Time.realtimeSinceStartupAsDouble;
        var firstUnlock = unlocks.OrderBy(u => u.Tier).First();
        string first = AchievementToast.Message(firstUnlock.Text, firstUnlock.Number);
        Assert.AreEqual(first, BodyOf(card), "渡された順のうち段の低いものが先に出る");
        bool Visible()
        {
            if (AchievementToast.Instance == null) return false;
            foreach (var visible in AchievementToast.Instance.VisibleCards()) if (BodyOf(visible) == first) return true;
            return false;
        }
        double lastSeen = shown;
        while (Visible() && Time.realtimeSinceStartupAsDouble < shown + 8) { lastSeen = Time.realtimeSinceStartupAsDouble; yield return null; }
        Assert.IsFalse(Visible(), "消える");
        Assert.Less(lastSeen - shown, 5, "実時間5秒以内に消える(見えていた最後のコマで測る)");
        string lastBody = null;
        while (AchievementToast.Instance != null && Time.realtimeSinceStartupAsDouble < shown + 30)
        {
            foreach (var visible in AchievementToast.Instance.VisibleCards()) lastBody = BodyOf(visible);
            yield return null;
        }
        Assert.IsNull(AchievementToast.Instance, "全部出し終えたら消える");
        StringAssert.StartsWith(AchievementPoints.Sum(unlocks.Select(u => u.Id)) + "pt", lastBody, "最後に実績ポイントのカード(〇〇pt 〇位/〇人)");
    }

    [UnityTest]
    public IEnumerator SkippingTheResultRevealShowsTheCardsAtOnce()
    {
        SceneManager.SetActiveScene(SceneManager.CreateScene(song + "_play"));
        FinishRun();
        yield return LoadResult();
        var reveal = Object.FindFirstObjectByType<ResultReveal>();
        for (int i = 0; i < 5; i++)
        {
            Assert.IsNull(AchievementToast.Instance, "着地の前は出さない");
            yield return null;
        }
        reveal.Tick(999); // 演出を飛ばした(クリック・キー)
        yield return null;
        yield return null;
        Assert.NotNull(AchievementToast.Instance, "飛ばしたらすぐ出す");
        Assert.That(AchievementToast.Instance.VisibleCards(), Is.Not.Empty);
    }

    [UnityTest]
    public IEnumerator ARunStartedInOperatorModeCountsInTheFestivalTable()
    {
        SceneManager.SetActiveScene(SceneManager.CreateScene(song + "_play"));
        OperatorMode.OverrideForTest(true);
        GameSession.ResetResult();
        GameSession.FinalScore = 30123;
        GameSession.FinalHit = GameSession.FinalPerfect = 10;
        GameSession.FinalMaxCombo = 10;
        GameSession.FinalCutCount = 10;
        Assert.AreEqual(OperatorMode.FestivalNamespace, GameSession.RunNamespace);
        // 曲の途中で運営モードを切っても、その回は始めたときの表に残す
        OperatorMode.OverrideForTest(false);
        FinishRun();
        Assert.AreEqual(1, AchievementStore.Count(OperatorMode.FestivalNamespace, "clear"));
        Assert.AreEqual(0, AchievementStore.Count("", "clear"), "ふだんの数は増えない");
        CollectionAssert.Contains(GameSession.FinalAchievements, "clear");
        yield return null;
    }

    // 本物の Game 場面: アンダルシアの Easy の出だし(青と赤の組 → 青 → 青 → 赤 → 青と赤の組)を、届いた時刻ちょうどの振りで切る
    // (RushESongTests と同じ切り方)。曲は最後まで遊ばない(結果・記録には残さない)。
    [UnityTest]
    public IEnumerator CuttingTheOpeningInTheRealGameIsCountedForFirstcutAndXcut()
    {
        const string Song = "Andalusia", Difficulty = "Easy";
        var chart = ChartLoader.LoadFromStreamingAssets(Song, Difficulty);
        Assert.Greater(chart.notes.Count, 7);
        double lastCut = chart.notes[6].TimeSeconds; // 2つ目の組(譜面の time で 7.64秒)まで
        Assert.AreEqual(chart.notes[5].time, chart.notes[6].time, "2つ目の組");
        tracker = AchievementTracker.CreateForTest();
        GameSession.SelectedSongId = Song;
        GameSession.SelectedSongTitle = Song;
        GameSession.SelectedDifficulty = Difficulty;
        GameSession.ResetResult();
        yield return SceneManager.LoadSceneAsync("Game");
        GamePlayManager manager = null;
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        while ((manager == null || tracker.CurrentStats == null) && Time.realtimeSinceStartupAsDouble < deadline)
        {
            manager = Object.FindFirstObjectByType<GamePlayManager>();
            yield return null;
        }
        Assert.NotNull(manager);
        var stats = tracker.CurrentStats;
        Assert.NotNull(stats, "本編の曲が準備できたら記録を結び付ける");
        Assert.IsTrue(stats.Complete, "最初のノーツが出る前から記録している");
        Assert.AreEqual(GameSession.AchievementRunId, stats.RunId);
        var spawner = manager.noteSpawner;
        // 切ったノーツは使い回されるので、部品と出た回(SpawnVersion)の組で覚える。
        var cut = new HashSet<(CuttableNote, uint)>();
        int cuts = 0;
        deadline = Time.realtimeSinceStartupAsDouble + 40;
        while (cuts < 7 && Time.realtimeSinceStartupAsDouble < deadline)
        {
            yield return null;
            if (!manager.songPlayer.IsScheduled) continue;
            double judge = spawner.JudgeClock(manager.songPlayer.SongTime);
            foreach (var note in new List<CuttableNote>(spawner.LiveNotes))
            {
                if (note == null || note.IsFinalized || cut.Contains((note, note.SpawnVersion)) || note.HitTime > judge
                    || note.HitTime - spawner.TotalOffsetSeconds > lastCut + 1e-6) continue;
                var hand = note.RequiredHand != SaberHand.Any ? note.RequiredHand : note.LandingPosition.x < 0 ? SaberHand.Left : SaberHand.Right;
                var direction = note.RequiredDirection != CutDirection.None
                    ? CutDirectionHelper.ToVector(note.RequiredDirection).normalized : Vector2.up;
                Assert.IsTrue(note.TryCutWithCameraTiming(note.transform.position, new Vector3(direction.x, direction.y, 0) * 12, hand,
                    note.HitTime, .866f), "切れる: " + note.HitTime);
                cut.Add((note, note.SpawnVersion));
                cuts++;
            }
        }
        Assert.AreEqual(7, cuts, "出だしの7個を切った");
        Assert.AreSame(stats, tracker.CurrentStats, "曲の途中で作り直さない");
        var snapshot = tracker.SnapshotNow();
        for (int k = 0; k < 7; k++) Assert.AreEqual(JudgmentTier.Perfect, snapshot.Notes[k].Result, "ノーツ " + k);
        // 第2弾の記録: 届く時刻ちょうどの振りなので誤差は 0ms、結果が出た台の時刻と、切った手(青は左の剣)。
        for (int k = 0; k < 7; k++)
        {
            Assert.AreEqual(0, snapshot.Notes[k].ErrorMs.Value, 1e-6, "誤差 " + k);
            Assert.IsTrue(snapshot.Notes[k].JudgedAt.HasValue, "結果の時刻 " + k);
        }
        Assert.AreEqual(SaberHand.Left, snapshot.Notes[0].Hand, "青は左の剣");
        Assert.AreEqual(SaberHand.Right, snapshot.Notes[1].Hand);
        Assert.IsTrue(snapshot.AllPerfect(snapshot.FirstGroup), "初太刀(最初の組の青と赤)");
        Assert.AreEqual(2, snapshot.FirstGroup.Count);
        Assert.AreEqual(2, snapshot.CrossCuts, "青と赤の組を2回一緒に切った");
        Assert.GreaterOrEqual(snapshot.MaxPerfectStreak, 7);
        var context = AchievementTracker.BuildContext(snapshot, DateTime.Now);
        Assert.IsTrue(AchievementCatalog.Find("firstcut").Met(context), "初太刀");
        Assert.IsTrue(AchievementCatalog.Find("xcut").Met(context), "X斬り");
        Assert.IsTrue(snapshot.StartedAt.HasValue, "曲が鳴り始めた時刻");
        Assert.IsFalse(snapshot.EndedAt.HasValue);
        manager.songPlayer.Stop();
        yield return null;
        yield return null;
        Assert.IsTrue(stats.EndedAt.HasValue, "曲が止まったコマの時刻");
        Assert.GreaterOrEqual(stats.EndedAt.Value, stats.StartedAt.Value);
    }
}
