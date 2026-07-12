using NUnit.Framework;
using UnityEngine;

// PlayRankHelper(精度→ランク→進捗)と、リザルト用フォント/ランク素材の存在確認。
public class PlayRankTests
{
    // ---- Accuracy ----

    [Test]
    public void Accuracy_NoJudgments_IsFull()
    {
        Assert.AreEqual(1f, PlayRankHelper.Accuracy(0, 0, 0, 0, 0), 1e-5f);
    }

    [Test]
    public void Accuracy_AllPerfect_IsFull()
    {
        Assert.AreEqual(1f, PlayRankHelper.Accuracy(10, 0, 0, 0, 0), 1e-5f);
    }

    [Test]
    public void Accuracy_AllMiss_IsZero()
    {
        Assert.AreEqual(0f, PlayRankHelper.Accuracy(0, 0, 0, 0, 10), 1e-5f);
    }

    [Test]
    public void Accuracy_Mixed_MatchesBasePointRatio()
    {
        // (300+200+100+50+0) / (5*300) = 650/1500
        Assert.AreEqual(650f / 1500f, PlayRankHelper.Accuracy(1, 1, 1, 1, 1), 1e-5f);
    }

    // ---- TotalAccuracy(曲中の合計割合。分母=総ノーツ数固定でだんだん上がる) ----

    [Test]
    public void TotalAccuracy_NoNotes_IsZero()
    {
        Assert.AreEqual(0f, PlayRankHelper.TotalAccuracy(0, 0, 0, 0, 0), 1e-5f);
        Assert.AreEqual(0f, PlayRankHelper.TotalAccuracy(5, 0, 0, 0, 0), 1e-5f, "総数不明は 0");
    }

    [Test]
    public void TotalAccuracy_StartsAtZero_AndGrows()
    {
        Assert.AreEqual(0f, PlayRankHelper.TotalAccuracy(0, 0, 0, 0, 10), 1e-5f, "曲開始時は 0");
        Assert.AreEqual(0.1f, PlayRankHelper.TotalAccuracy(1, 0, 0, 0, 10), 1e-5f, "Perfect 1/10 = 10%");
        Assert.AreEqual(0.5f, PlayRankHelper.TotalAccuracy(5, 0, 0, 0, 10), 1e-5f, "単調に増える");
        Assert.AreEqual(1f, PlayRankHelper.TotalAccuracy(10, 0, 0, 0, 10), 1e-5f, "全 Perfect で 100%");
    }

    [Test]
    public void TotalAccuracy_MissDoesNotAdd_ButLimitsMax()
    {
        // Miss は加点ゼロ(引数にすら不要)。10ノーツ中 9 Perfect + 1 Miss なら 90% 止まり。
        Assert.AreEqual(0.9f, PlayRankHelper.TotalAccuracy(9, 0, 0, 0, 10), 1e-5f);
    }

    [Test]
    public void TotalAccuracy_AtSongEnd_MatchesAccuracy()
    {
        // 全ノーツ判定済みなら従来の Accuracy(その時点精度)と一致する
        int p = 3, g = 2, gd = 1, b = 1, m = 3;
        float atEnd = PlayRankHelper.TotalAccuracy(p, g, gd, b, p + g + gd + b + m);
        float acc = PlayRankHelper.Accuracy(p, g, gd, b, m);
        Assert.AreEqual(acc, atEnd, 1e-5f);
    }

    // ---- FromAccuracy ----

    [Test]
    public void FromAccuracy_Boundaries()
    {
        Assert.AreEqual(PlayRank.SPlus, PlayRankHelper.FromAccuracy(1.00f));
        Assert.AreEqual(PlayRank.SPlus, PlayRankHelper.FromAccuracy(0.95f));
        Assert.AreEqual(PlayRank.S, PlayRankHelper.FromAccuracy(0.949f));
        Assert.AreEqual(PlayRank.S, PlayRankHelper.FromAccuracy(0.90f));
        Assert.AreEqual(PlayRank.A, PlayRankHelper.FromAccuracy(0.899f));
        Assert.AreEqual(PlayRank.A, PlayRankHelper.FromAccuracy(0.80f));
        Assert.AreEqual(PlayRank.B, PlayRankHelper.FromAccuracy(0.799f));
        Assert.AreEqual(PlayRank.B, PlayRankHelper.FromAccuracy(0.65f));
        Assert.AreEqual(PlayRank.C, PlayRankHelper.FromAccuracy(0.649f));
        Assert.AreEqual(PlayRank.C, PlayRankHelper.FromAccuracy(0f));
    }

    // ---- Label / NextRank / Progress ----

    [Test]
    public void Label_Mapping()
    {
        Assert.AreEqual("S+", PlayRankHelper.Label(PlayRank.SPlus));
        Assert.AreEqual("S", PlayRankHelper.Label(PlayRank.S));
        Assert.AreEqual("A", PlayRankHelper.Label(PlayRank.A));
        Assert.AreEqual("B", PlayRankHelper.Label(PlayRank.B));
        Assert.AreEqual("C", PlayRankHelper.Label(PlayRank.C));
    }

    [Test]
    public void TryNextRank_ChainAndTerminal()
    {
        Assert.IsTrue(PlayRankHelper.TryNextRank(PlayRank.C, out var next1));
        Assert.AreEqual(PlayRank.B, next1);
        Assert.IsTrue(PlayRankHelper.TryNextRank(PlayRank.B, out var next2));
        Assert.AreEqual(PlayRank.A, next2);
        Assert.IsTrue(PlayRankHelper.TryNextRank(PlayRank.A, out var next3));
        Assert.AreEqual(PlayRank.S, next3);
        Assert.IsTrue(PlayRankHelper.TryNextRank(PlayRank.S, out var next4));
        Assert.AreEqual(PlayRank.SPlus, next4);
        Assert.IsFalse(PlayRankHelper.TryNextRank(PlayRank.SPlus, out _), "S+ の上は無い");
    }

    [Test]
    public void ProgressToNext_MidBand_IsHalf()
    {
        // B 帯 [0.65, 0.80) の中間
        Assert.AreEqual(0.5f, PlayRankHelper.ProgressToNext(0.725f), 1e-4f);
        // C 帯 [0, 0.65) の中間
        Assert.AreEqual(0.5f, PlayRankHelper.ProgressToNext(0.325f), 1e-4f);
    }

    [Test]
    public void ProgressToNext_BandEdges()
    {
        Assert.AreEqual(0f, PlayRankHelper.ProgressToNext(0.65f), 1e-4f, "ランク下限では 0");
        Assert.AreEqual(1f, PlayRankHelper.ProgressToNext(0.96f), 1e-4f, "S+ は常に 1");
        Assert.AreEqual(1f, PlayRankHelper.ProgressToNext(1f), 1e-4f);
    }

    [Test]
    public void RankColor_DiffersPerRank()
    {
        Assert.AreNotEqual(PlayRankHelper.RankColor(PlayRank.SPlus), PlayRankHelper.RankColor(PlayRank.S));
        Assert.AreNotEqual(PlayRankHelper.RankColor(PlayRank.S), PlayRankHelper.RankColor(PlayRank.A));
        Assert.AreNotEqual(PlayRankHelper.RankColor(PlayRank.A), PlayRankHelper.RankColor(PlayRank.B));
        Assert.AreNotEqual(PlayRankHelper.RankColor(PlayRank.B), PlayRankHelper.RankColor(PlayRank.C));
    }

    // ---- リザルトの精度表記 ----

    [Test]
    public void FormatAccuracy_OneDecimalPercent()
    {
        Assert.AreEqual("93.2%", ResultSkin.FormatAccuracy(0.932f));
        Assert.AreEqual("100.0%", ResultSkin.FormatAccuracy(1.2f), "1超はクランプ");
        Assert.AreEqual("0.0%", ResultSkin.FormatAccuracy(-0.5f), "負もクランプ");
    }

    // ---- 素材の存在(フォント/ランク画像が消えたら気付けるように) ----

    [Test]
    public void RequiredFonts_ExistInResources()
    {
        foreach (var name in new[] {
            "Oxanium-ExtraBold", "Oxanium-Bold", "Rajdhani-Bold", "Rajdhani-SemiBold", "Bangers-Regular" })
        {
            Assert.IsNotNull(Resources.Load<Font>("Fonts/" + name), $"Fonts/{name} が見つからない");
        }
    }

    [Test]
    public void RankSprites_ExistInResources()
    {
        foreach (PlayRank rank in System.Enum.GetValues(typeof(PlayRank)))
        {
            string path = PlayRankHelper.SpriteResourceName(rank);
            Assert.IsNotNull(UISkinKit.LoadSprite(path), $"{path} が見つからない");
        }
        // RANK ロゴはユーザー支給の透過版(2026-07-12)
        Assert.IsNotNull(UISkinKit.LoadSprite("Ranks/Rank_Title"), "RANK ロゴが見つからない");
    }
}
