using UnityEngine;

// プレイ全体の成績ランク(S+ / S / A / B / C)。
// 精度 = 獲得基礎点 ÷ 理論最大基礎点(コンボボーナスは含めない)で決める。
// しきい値は暫定(docs/DECISIONS.md 参照)。ランクバッジは暫定で文字表示、画像素材の導入予定あり。
public enum PlayRank
{
    C,
    B,
    A,
    S,
    SPlus
}

public static class PlayRankHelper
{
    // 精度の下限しきい値(この値以上でそのランク)
    public const float ThresholdB = 0.65f;
    public const float ThresholdA = 0.80f;
    public const float ThresholdS = 0.90f;
    public const float ThresholdSPlus = 0.95f;

    // 判定カウント → 精度(0..1)。まだ何も判定していないときは 1(満点スタート)。
    public static float Accuracy(int perfect, int great, int good, int bad, int miss)
    {
        int judged = perfect + great + good + bad + miss;
        if (judged <= 0) return 1f;
        long earned =
            (long)perfect * JudgmentTierHelper.BasePoints(JudgmentTier.Perfect) +
            (long)great * JudgmentTierHelper.BasePoints(JudgmentTier.Great) +
            (long)good * JudgmentTierHelper.BasePoints(JudgmentTier.Good) +
            (long)bad * JudgmentTierHelper.BasePoints(JudgmentTier.Bad);
        long max = (long)judged * JudgmentTierHelper.BasePoints(JudgmentTier.Perfect);
        return Mathf.Clamp01((float)((double)earned / max));
    }

    // 曲全体に対する合計割合(0..1)。分母は「総ノーツ数 × Perfect点」固定。
    // プレイ中は 0 から始まり、ヒットするほど単調に増える(だんだん上がっていく)。
    // 曲終了時は全ノーツが判定済みになるので Accuracy(miss込み) と同じ値に収束する。
    public static float TotalAccuracy(int perfect, int great, int good, int bad, int totalNotes)
    {
        if (totalNotes <= 0) return 0f;
        long earned =
            (long)perfect * JudgmentTierHelper.BasePoints(JudgmentTier.Perfect) +
            (long)great * JudgmentTierHelper.BasePoints(JudgmentTier.Great) +
            (long)good * JudgmentTierHelper.BasePoints(JudgmentTier.Good) +
            (long)bad * JudgmentTierHelper.BasePoints(JudgmentTier.Bad);
        long max = (long)totalNotes * JudgmentTierHelper.BasePoints(JudgmentTier.Perfect);
        return Mathf.Clamp01((float)((double)earned / max));
    }

    public static PlayRank FromAccuracy(float accuracy)
    {
        if (accuracy >= ThresholdSPlus) return PlayRank.SPlus;
        if (accuracy >= ThresholdS) return PlayRank.S;
        if (accuracy >= ThresholdA) return PlayRank.A;
        if (accuracy >= ThresholdB) return PlayRank.B;
        return PlayRank.C;
    }

    public static string Label(PlayRank rank)
    {
        switch (rank)
        {
            case PlayRank.SPlus: return "S+";
            case PlayRank.S: return "S";
            case PlayRank.A: return "A";
            case PlayRank.B: return "B";
            default: return "C";
        }
    }

    // ランクの精度下限。C は 0。
    public static float LowerBound(PlayRank rank)
    {
        switch (rank)
        {
            case PlayRank.SPlus: return ThresholdSPlus;
            case PlayRank.S: return ThresholdS;
            case PlayRank.A: return ThresholdA;
            case PlayRank.B: return ThresholdB;
            default: return 0f;
        }
    }

    // 1つ上のランク。S+ には無い(false)。
    public static bool TryNextRank(PlayRank rank, out PlayRank next)
    {
        switch (rank)
        {
            case PlayRank.C: next = PlayRank.B; return true;
            case PlayRank.B: next = PlayRank.A; return true;
            case PlayRank.A: next = PlayRank.S; return true;
            case PlayRank.S: next = PlayRank.SPlus; return true;
            default: next = PlayRank.SPlus; return false;
        }
    }

    // 現在ランク帯の中でどこまで来たか(0..1)。次ランクの下限に届いたら 1。S+ は常に 1。
    public static float ProgressToNext(float accuracy)
    {
        PlayRank rank = FromAccuracy(accuracy);
        if (!TryNextRank(rank, out PlayRank next)) return 1f;
        float lo = LowerBound(rank);
        float hi = LowerBound(next);
        if (hi <= lo) return 1f;
        return Mathf.Clamp01((accuracy - lo) / (hi - lo));
    }

    // ランク色。バッジ画像の色味に合わせる(S+=虹→金 / S=青 / A=赤 / B=緑 / C=紫)。
    // 進捗バーと文字フォールバックで共用。
    public static Color RankColor(PlayRank rank)
    {
        switch (rank)
        {
            case PlayRank.SPlus: return UISkinPalette.NoteGold;
            case PlayRank.S: return UISkinPalette.LogoBlue;
            case PlayRank.A: return UISkinPalette.LogoRed;
            case PlayRank.B: return UISkinPalette.LogoGreen;
            default: return UISkinPalette.NoteFlick;
        }
    }

    // ランクバッジ画像(Assets/Resources/Ranks)のリソースパス。UISkinKit.LoadSprite で読む。
    public static string SpriteResourceName(PlayRank rank)
    {
        switch (rank)
        {
            case PlayRank.SPlus: return "Ranks/Rank_SPlus";
            case PlayRank.S: return "Ranks/Rank_S";
            case PlayRank.A: return "Ranks/Rank_A";
            case PlayRank.B: return "Ranks/Rank_B";
            default: return "Ranks/Rank_C";
        }
    }
}
