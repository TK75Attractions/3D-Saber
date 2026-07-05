using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class GameHUDSkinTests
{
    private readonly List<GameObject> created = new List<GameObject>();
    private string savedTitle;
    private string savedDifficulty;

    [SetUp]
    public void SaveSession()
    {
        savedTitle = GameSession.SelectedSongTitle;
        savedDifficulty = GameSession.SelectedDifficulty;
    }

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in created)
        {
            if (go != null) Object.DestroyImmediate(go);
        }
        created.Clear();
        var skin = Object.FindFirstObjectByType<GameHUDSkin>();
        if (skin != null) Object.DestroyImmediate(skin.gameObject);
        GameSession.SelectedSongTitle = savedTitle;
        GameSession.SelectedDifficulty = savedDifficulty;
    }

    // ---- 純関数 ----

    [Test]
    public void Progress01_ZeroDuration_ReturnsZero()
    {
        Assert.AreEqual(0f, GameHUDSkin.Progress01(10.0, 0.0), 0.001f);
        Assert.AreEqual(0f, GameHUDSkin.Progress01(10.0, -5.0), 0.001f);
    }

    [Test]
    public void Progress01_Halfway()
    {
        Assert.AreEqual(0.5f, GameHUDSkin.Progress01(30.0, 60.0), 0.001f);
    }

    [Test]
    public void Progress01_Clamps()
    {
        Assert.AreEqual(1f, GameHUDSkin.Progress01(90.0, 60.0), 0.001f);
        Assert.AreEqual(0f, GameHUDSkin.Progress01(-3.0, 60.0), 0.001f);
    }

    [Test]
    public void ComboColor_MatchesLegacyTiers()
    {
        // 旧 ScoreHUD.ColorByComboTier と同じ階調を保つ
        var c0 = GameHUDSkin.ComboColor(0);
        Assert.That(c0.r, Is.GreaterThan(0.8f));
        Assert.That(c0.g, Is.GreaterThan(0.8f));

        var c10 = GameHUDSkin.ComboColor(10);
        Assert.That(c10.r, Is.LessThan(0.5f));
        Assert.That(c10.g, Is.GreaterThan(0.8f));

        var c100 = GameHUDSkin.ComboColor(100);
        Assert.That(c100.r, Is.GreaterThan(0.8f));
        Assert.That(c100.g, Is.LessThan(0.5f));

        Assert.That(GameHUDSkin.ComboColor(9), Is.Not.EqualTo(GameHUDSkin.ComboColor(10)));
        Assert.That(GameHUDSkin.ComboColor(29), Is.Not.EqualTo(GameHUDSkin.ComboColor(30)));
        Assert.That(GameHUDSkin.ComboColor(59), Is.Not.EqualTo(GameHUDSkin.ComboColor(60)));
        Assert.That(GameHUDSkin.ComboColor(99), Is.Not.EqualTo(GameHUDSkin.ComboColor(100)));
    }

    [Test]
    public void FormatTimingHint_HiddenForPerfectMissAndInvalid()
    {
        Assert.AreEqual("", GameHUDSkin.FormatTimingHint(JudgmentTier.Great, false, -50.0), "無効な誤差は非表示");
        Assert.AreEqual("", GameHUDSkin.FormatTimingHint(JudgmentTier.Perfect, true, -10.0), "Perfect は非表示");
        Assert.AreEqual("", GameHUDSkin.FormatTimingHint(JudgmentTier.Miss, true, 300.0), "Miss は非表示");
    }

    [Test]
    public void FormatTimingHint_EarlyAndLate()
    {
        Assert.AreEqual("EARLY 42ms", GameHUDSkin.FormatTimingHint(JudgmentTier.Great, true, -42.3));
        Assert.AreEqual("LATE 85ms", GameHUDSkin.FormatTimingHint(JudgmentTier.Good, true, 85.4));
        Assert.AreEqual("LATE 120ms", GameHUDSkin.FormatTimingHint(JudgmentTier.Bad, true, 120.0));
    }

    [Test]
    public void TimingHintColor_EarlyIsBlueish_LateIsOrange()
    {
        var early = GameHUDSkin.TimingHintColor(-30.0);
        Assert.That(early.b, Is.GreaterThan(0.8f), "EARLY は青系");
        var late = GameHUDSkin.TimingHintColor(30.0);
        Assert.That(late.r, Is.GreaterThan(0.8f), "LATE はオレンジ系");
        Assert.That(late.b, Is.LessThan(0.5f));
    }

    [Test]
    public void TierColor_PerfectIsCyan_MissIsGray()
    {
        var perfect = GameHUDSkin.TierColor(JudgmentTier.Perfect);
        Assert.That(perfect.g, Is.GreaterThan(0.8f));
        Assert.That(perfect.b, Is.GreaterThan(0.8f));
        Assert.That(perfect.r, Is.LessThan(0.5f));

        var miss = GameHUDSkin.TierColor(JudgmentTier.Miss);
        Assert.That(miss.r, Is.LessThan(0.7f));
        Assert.That(miss.g, Is.LessThan(0.7f));
    }

    [Test]
    public void DifficultyColor_MapsEasyNormalHard()
    {
        var easy = GameHUDSkin.DifficultyColor("easy");
        Assert.That(easy.g, Is.GreaterThan(0.8f), "easy は緑系");
        var hard = GameHUDSkin.DifficultyColor("Hard");
        Assert.That(hard.r, Is.GreaterThan(0.8f), "hard は赤系(大文字小文字無視)");
        var normal = GameHUDSkin.DifficultyColor("normal");
        Assert.That(normal.b, Is.GreaterThan(0.8f), "normal は青系");
        var unknown = GameHUDSkin.DifficultyColor(null);
        Assert.That(unknown.b, Is.GreaterThan(0.8f), "不明は normal と同じ青系");
    }

    // ---- 構築 ----

    [Test]
    public void Ensure_CreatesAndBuilds()
    {
        var skin = GameHUDSkin.Ensure();
        Assert.IsNotNull(skin);
        Assert.IsTrue(skin.IsBuilt);
    }

    [Test]
    public void Ensure_IsIdempotent()
    {
        var a = GameHUDSkin.Ensure();
        var b = GameHUDSkin.Ensure();
        Assert.AreSame(a, b);
    }

    [Test]
    public void Build_CreatesCoreElements()
    {
        GameSession.SelectedSongTitle = "テスト曲";
        GameSession.SelectedDifficulty = "Normal";
        var skin = GameHUDSkin.Ensure();
        var t = skin.transform;
        Assert.IsNotNull(t.Find("ScoreLabel"), "スコアラベル");
        Assert.IsNotNull(t.Find("ScoreValue"), "スコア数値");
        Assert.IsNotNull(t.Find("ComboValue"), "コンボ数値");
        Assert.IsNotNull(t.Find("TierText"), "判定演出テキスト");
        Assert.IsNotNull(t.Find("TimingHint"), "EARLY/LATE表示");
        Assert.IsNotNull(t.Find("SongTitle"), "曲名");
        Assert.IsNotNull(t.Find("Difficulty"), "難易度");
        Assert.IsNotNull(t.Find("ProgressBg"), "進行バー背景");
        Assert.IsNotNull(t.Find("ProgressBg/ProgressFill"), "進行バー本体");
    }

    [Test]
    public void Build_SongTitleShowsJapaneseTitle_WithLegacyText()
    {
        // 日本語曲名は TMP(Chakra Petch は ASCII のみ)ではなく legacy Text で表示する
        GameSession.SelectedSongTitle = "黄金の太陽";
        var skin = GameHUDSkin.Ensure();
        var title = skin.transform.Find("SongTitle");
        Assert.IsNotNull(title);
        var text = title.GetComponent<Text>();
        Assert.IsNotNull(text, "曲名は legacy Text コンポーネント");
        Assert.AreEqual("黄金の太陽", text.text);
    }

    [Test]
    public void Build_DisablesLegacyScoreHud()
    {
        // 旧 HUD(ScoreHUD + legacy Text)を再現
        var hudGo = new GameObject("hud");
        created.Add(hudGo);
        var legacy = hudGo.AddComponent<ScoreHUD>();
        var scoreGo = new GameObject("ScoreText", typeof(RectTransform));
        scoreGo.transform.SetParent(hudGo.transform, false);
        legacy.scoreText = scoreGo.AddComponent<Text>();
        var comboGo = new GameObject("ComboText", typeof(RectTransform));
        comboGo.transform.SetParent(hudGo.transform, false);
        legacy.comboText = comboGo.AddComponent<Text>();

        GameHUDSkin.Ensure();

        Assert.IsFalse(legacy.enabled, "旧 ScoreHUD は無効化される");
        Assert.IsFalse(scoreGo.activeSelf, "旧スコアテキストは非表示");
        Assert.IsFalse(comboGo.activeSelf, "旧コンボテキストは非表示");
    }
}
