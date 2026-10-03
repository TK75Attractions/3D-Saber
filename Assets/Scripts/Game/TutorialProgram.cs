using System;
using System.Collections.Generic;
using UnityEngine;

// チュートリアル(タイトルの問いかけで「はい」)の進行表。
// 文・出す型・合格の条件だけを持つ純粋なデータで、EditMode テストから直接叩ける。
// 位置は判定調整と同じ x=±1.65、y は Easy 譜面の範囲(−0.65〜1.1)。青は画面の左、赤は右から出し、交差はさせない。
public enum TutorialStepKind { Explain, Practice, Ready }

public sealed class TutorialPatternNote
{
    public float dt;              // 型の先頭からの秒
    public float x, y;
    public string color = "red";
    public string type = "tap";
    public string direction = "none";
    public int count = 1;
    public float lengthMs;

    public NoteData ToNote(double patternStartSeconds) => new NoteData
    {
        time = (float)((patternStartSeconds + dt) * 1000.0), x = x, y = y, type = type, color = color,
        direction = direction, count = count, lengthMs = lengthMs,
    };
}

public sealed class TutorialStep
{
    public string Id, Title, Sub;
    public TutorialStepKind Kind;
    public float TimeoutSeconds;                      // Explain / Ready の自動送り秒。Practice は TutorialProgram.DefaultStepTimeoutSeconds
    public TutorialPatternNote[] Pattern = Array.Empty<TutorialPatternNote>();
    public double PeriodSeconds;                      // 型を繰り返す間隔(秒)
    public int Goal;                                  // 成功回数
    public bool NeedDirection;                        // 向きが合った切りだけを数える
    public float LongRate;                            // ロングの合格達成率(0 なら 0.6)
    public bool Simultaneous;
    public double PatternLengthSeconds => Pattern.Length == 0 ? 0 : Pattern[Pattern.Length - 1].dt;
}

public static class TutorialProgram
{
    public const float Bpm = 100f;                    // ゲート発光の拍。ノーツの時刻は拍に縛らない
    public const double ReadDelaySeconds = .9;        // 文を読む間
    public const double StepEndSeconds = .7;          // 「OK!」を見せる間
    public const double TimeoutEndSeconds = 1.0;      // 「次に進むよ」を見せる間
    public const float DefaultStepTimeoutSeconds = 15f;
    public const float HandsTimeoutSeconds = 6f;
    public const float ReadyTimeoutSeconds = 1.4f;
    public const float SwingSpeedToAdvance = 2.5f;    // 持ち方の説明を抜ける振りの速さ(タイトルの開始ノーツと同じ)
    public const float DefaultLongRate = .6f;
    public const float RightX = 1.65f, LeftX = -1.65f, BaseY = .5f, LowY = -.4f;

    // 難易度を選ぶ前に挟まるので、Easy / Normal に共通の型。左右・斜めの矢印は曲の中で覚える。
    public static readonly string[] CommonFlow = { "hands", "tapRed", "tapBlue", "tapMix", "arrowEasy", "long", "gold", "simul", "ready" };

    static TutorialPatternNote Tap(float x, float y, string color, float dt) => new TutorialPatternNote { dt = dt, x = x, y = y, color = color, type = "tap" };
    static TutorialPatternNote Arrow(float x, float y, string color, string direction, float dt) => new TutorialPatternNote { dt = dt, x = x, y = y, color = color, type = "direction", direction = direction };
    static TutorialPatternNote Long(float x, float y, string color, int count, float lengthMs, float dt) => new TutorialPatternNote { dt = dt, x = x, y = y, color = color, type = "long", count = count, lengthMs = lengthMs };

    public static TutorialStep Get(string id)
    {
        switch (id)
        {
            case "hands": return new TutorialStep { Id = id, Kind = TutorialStepKind.Explain, Title = "赤いセーバーは右手、青いセーバーは左手", Sub = "どちらかを振ると始まります", TimeoutSeconds = HandsTimeoutSeconds };
            case "tapRed": return new TutorialStep { Id = id, Kind = TutorialStepKind.Practice, Title = "赤いノーツを右手で切ろう", Sub = "枠に重なった瞬間に振る",
                Pattern = new[] { Tap(RightX, BaseY, "red", 0f), Tap(RightX, BaseY, "red", 1.6f) }, PeriodSeconds = 3.4, Goal = 2 };
            case "tapBlue": return new TutorialStep { Id = id, Kind = TutorialStepKind.Practice, Title = "青いノーツは左手で", Sub = "色と手を合わせる",
                Pattern = new[] { Tap(LeftX, BaseY, "blue", 0f), Tap(LeftX, BaseY, "blue", 1.6f) }, PeriodSeconds = 3.4, Goal = 2 };
            case "tapMix": return new TutorialStep { Id = id, Kind = TutorialStepKind.Practice, Title = "来た色と同じ手で切ろう", Sub = "赤は右、青は左",
                Pattern = new[] { Tap(RightX, BaseY, "red", 0f), Tap(LeftX, BaseY, "blue", 1.2f), Tap(RightX, LowY, "red", 2.4f), Tap(LeftX, LowY, "blue", 3.6f) }, PeriodSeconds = 5.2, Goal = 3 };
            case "arrowEasy": return new TutorialStep { Id = id, Kind = TutorialStepKind.Practice, Title = "矢印の向きに振ろう", Sub = "上向きなら下から上へ",
                Pattern = new[] { Arrow(RightX, BaseY, "red", "up", 0f), Arrow(LeftX, BaseY, "blue", "down", 1.8f) }, PeriodSeconds = 3.8, Goal = 2, NeedDirection = true };
            case "arrowNormal": return new TutorialStep { Id = id, Kind = TutorialStepKind.Practice, Title = "矢印の向きに振ろう", Sub = "上・下・左・右",
                Pattern = new[] { Arrow(RightX, BaseY, "red", "up", 0f), Arrow(LeftX, BaseY, "blue", "down", 1.6f), Arrow(RightX, BaseY, "red", "left", 3.2f), Arrow(LeftX, BaseY, "blue", "right", 4.8f) }, PeriodSeconds = 6.6, Goal = 3, NeedDirection = true };
            case "long": return new TutorialStep { Id = id, Kind = TutorialStepKind.Practice, Title = "ロングは、止まっている間に 3 回切ろう", Sub = "上の数字が残り回数",
                Pattern = new[] { Long(RightX, BaseY, "red", 3, 1400f, 0f) }, PeriodSeconds = 4.2, Goal = 1, LongRate = DefaultLongRate };
            case "gold": return new TutorialStep { Id = id, Kind = TutorialStepKind.Practice, Title = "金色はどちらの手でもOK", Sub = "大事な一撃。好きな手で",
                Pattern = new[] { Tap(0f, BaseY, "gold", 0f) }, PeriodSeconds = 2.8, Goal = 1 };
            case "simul": return new TutorialStep { Id = id, Kind = TutorialStepKind.Practice, Title = "赤と青がいっしょに来たら、両手で同時に", Sub = "青は左手、赤は右手",
                Pattern = new[] { Tap(LeftX, BaseY, "blue", 0f), Tap(RightX, BaseY, "red", 0f) }, PeriodSeconds = 3.4, Goal = 2, Simultaneous = true };
            case "ready": return new TutorialStep { Id = id, Kind = TutorialStepKind.Ready, Title = "準備OK!", Sub = "曲を選びに行こう", TimeoutSeconds = ReadyTimeoutSeconds };
        }
        return null;
    }

    public static TutorialStep[] Build(string[] flow, bool includeHands)
    {
        var steps = new List<TutorialStep>();
        foreach (var id in flow)
        {
            if (!includeHands && id == "hands") continue;
            var step = Get(id);
            if (step != null) steps.Add(step);
        }
        return steps.ToArray();
    }

    // 1 回の判定が「成功」に数えられるか。向きの違いは 1 段降格で切れるが、矢印の練習では数えない。
    // ロングは達成率で判定する(本編と同じ割合判定)。
    public static bool CountsAsSuccess(TutorialStep step, JudgmentTier tier, bool correctDirection, int requiredCuts, int achievedCuts)
    {
        if (step == null || step.Kind != TutorialStepKind.Practice) return false;
        if (requiredCuts > 1)
        {
            float rate = achievedCuts / (float)Mathf.Max(1, requiredCuts);
            return rate >= (step.LongRate > 0f ? step.LongRate : DefaultLongRate);
        }
        if (tier == JudgmentTier.Miss) return false;
        return !step.NeedDirection || correctDirection;
    }

    public static string DirectionJapanese(CutDirection direction)
    {
        switch (direction)
        {
            case CutDirection.Up: return "上";
            case CutDirection.Down: return "下";
            case CutDirection.Left: return "左";
            case CutDirection.Right: return "右";
            case CutDirection.UpLeft: return "左上";
            case CutDirection.UpRight: return "右上";
            case CutDirection.DownLeft: return "左下";
            case CutDirection.DownRight: return "右下";
        }
        return "";
    }

    // 見逃したときに上の行へ出す一言(ノーツの近くには出さない)。
    public static string MissHint(string color, bool hasDirection)
    {
        if (hasDirection) return "矢印の向きに、速く振ろう";
        if (color == "gold") return "どちらの手でもいいので振ろう";
        return (color == "blue" ? "青は左手" : "赤は右手") + "で、枠に重なった瞬間に振ろう";
    }
}
