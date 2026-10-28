using System;
using System.Collections.Generic;

// 実績の一覧(2026-10-29、44個。b1 が Opus・Fable・Codex の案から選んだ catalog.md の通り)。
// 実績は一覧では見せず、取った人に結果画面で「〜した ○人目」を出すだけ(AchievementToast)。○人目は AchievementStore.Unlock の番号
// (2台合算・運営モードとふだんで別)。対象は記録された回(GameSession.ShouldRecordRun)だけで、判定調整・練習は対象外。
// 並びは表示の順(珍しい順)。同じ回にいくつも取れたら、この順に上から出す。clear はいちばん下。
// 数は下の定数にまとめる。裏譜面の曲と難易度の文字は RushESecretState の表、ランクは PlayRankHelper、サビは stage.json から取る。
public static class AchievementCatalog
{
    // ---- 条件の数 ----
    public const int SenbonCuts = 1000;
    public const int MatadorMaxStuns = 5;
    public const int ClockHour = 14, ClockMinute = 23;
    public const int WindCuts = 50;
    public const int OvertakeCuts = 20;
    public const float DarkAccuracy = .60f;
    public const int LongStreak = 50, ShortStreak = 20;
    public const int KiribanEvery = 100;
    public const int ZoromeMinScore = 1000, ZoromeModulus = 1000, ZoromeStep = 111;
    public const int BigCombo = 100, SmallCombo = 50;
    public const int TopMinPlays = 5;
    public const int ComebackCombo = 30;
    public const double DuetSeconds = 60;
    public const int ChorusMinNotes = 5;
    public const double QuickSeconds = 15;
    public const int GoldMinNotes = 3;
    public const int DualMinNotes = 10;
    public const double DualPerfectRate = .6;
    public const int ArrowMinNotes = 3;
    public const string HardDifficulty = "Hard";
    public const string ClearId = "clear", KiribanId = "kiriban", IchibanId = "ichiban";

    public sealed class Definition
    {
        public readonly string Id, Text, Name, Category;
        public readonly Func<AchievementContext, bool> Condition;
        // 表示の順(0 がいちばん珍しい)。
        public int Order { get; internal set; }

        public Definition(string id, string text, string name, string category, Func<AchievementContext, bool> condition)
        {
            Id = id; Text = text; Name = name; Category = category; Condition = condition;
        }

        public bool Met(AchievementContext context) => context != null && Condition(context);
    }

    const string Rare = "珍しい", Gimmick = "仕掛け", Fun = "遊び心", Skill = "腕前", Entry = "入口";

    static readonly Definition[] all =
    {
        new Definition("ap", "全部PERFECTで斬りきった", "オールパーフェクト", Rare, c => c.AllPerfect),
        new Definition("senbon", "1曲で1000回斬った", "千本斬り", Rare, c => c.CutCount >= SenbonCuts),
        new Definition("quiz", "Qの試験で全問正解した", "満点答案", Gimmick,
            c => c.Is(RushESecretState.BenkyouQuiz) && c.NoteStats && c.Stats.AllCut(n => n.Quiz, 1)),
        new Definition("bullseye", "Dの的を全部射抜いた", "全弾命中", Gimmick,
            c => c.Is(RushESecretState.PhobiaAvoid) && c.AimMode && c.Hit > 0 && c.Miss == 0),
        new Definition("fubuki", "Fで降るノーツを全部斬った", "花吹雪", Gimmick,
            c => c.Is(RushESecretState.EpilogueFall) && c.NoteStats && c.Stats.AllCut(n => n.Fall, 1)),
        new Definition("mirror", "Rで鏡の中のノーツを全部斬った", "鏡破り", Gimmick,
            c => c.Is(RushESecretState.ManicSonicReverse) && c.NoteStats && c.Stats.AllCut(n => n.Mirror, 1)),
        new Definition("shingan", "Vの消えるノーツを全部斬った", "心眼", Gimmick,
            c => c.Is(RushESecretState.ZeroVanish) && c.NoteStats && c.Stats.AllCut(n => n.Hide, 1)),
        new Definition("memory", "Mのノイズの中を全部斬った", "記憶力", Gimmick,
            c => c.Is(RushESecretState.MorningMemory) && c.NoteStats && c.Stats.AllCut(n => n.Veiled, 1)),
        new Definition("matador", "Tでしびれを5回以内に抑えた", "マタドール", Gimmick,
            c => c.Is(RushESecretState.AndalusiaBull) && c.Stats != null && c.Stats.HasBull && c.Stats.Stuns <= MatadorMaxStuns),
        new Definition("clock", "14:23に2:23 AMを遊んだ", "2時23分", Fun,
            c => c.SongId == RushESecretState.TwoTwentyThreeLight.SongId && c.Spans(ClockHour, ClockMinute)),
        new Definition("hardsplus", "HardでS+をとった", "剣の名手", Rare, c => c.IsHard && c.Rank >= PlayRank.SPlus),
        new Definition("secret_s", "裏譜面でS以上をとった", "秘密の実力者", Gimmick, c => c.Secret && c.Rank >= PlayRank.S),
        new Definition("wind", "Wの風のノーツを50個斬った", "風読み", Gimmick,
            c => c.Is(RushESecretState.AobasouWind) && c.NoteStats && c.Stats.CutCount(n => n.Wind) >= WindCuts),
        new Definition("overtake", "Pの追い越しを20個見抜いた", "順番を見抜く", Gimmick,
            c => c.Is(RushESecretState.YurikagoOvertake) && c.NoteStats && c.Stats.CutCount(n => n.Overtake) >= OvertakeCuts),
        new Definition("dark", "暗闇のYを精度60%で抜けた", "夜を照らす剣", Gimmick,
            c => c.Is(RushESecretState.TwoTwentyThreeLight) && c.Accuracy >= DarkAccuracy),
        new Definition(IchibanId, "その日の一番乗りで遊んだ", "一番乗り", Fun, c => c.ClearsToday == 0),
        new Definition("streak50", "PERFECTを50回続けた", "乱れぬ五十拍", Rare, c => c.NoteStats && c.Stats.MaxPerfectStreak >= LongStreak),
        new Definition("fc", "フルコンボを達成した", "FULL COMBO", Rare, c => c.FullCombo),
        new Definition(KiribanId, "キリ番(100人ごと)を踏んだ", "キリ番", Fun, c => c.ClearNumber > 0 && c.ClearNumber % KiribanEvery == 0),
        new Definition("zorome", "点の下3桁をゾロ目にした", "ゾロ目", Fun,
            c => c.Score >= ZoromeMinScore && c.Score % ZoromeModulus % ZoromeStep == 0),
        new Definition("splus", "最高ランクS+をとった", "S+ランク", Skill, c => c.Rank >= PlayRank.SPlus),
        new Definition("combo100", "100コンボをつないだ", "100コンボ", Skill, c => c.MaxCombo >= BigCombo),
        new Definition("streak20", "PERFECTを20回続けた", "20連PERFECT", Skill, c => c.NoteStats && c.Stats.MaxPerfectStreak >= ShortStreak),
        new Definition("top", "この譜面の通算1位に立った", "頂点", Skill,
            c => c.RankingAvailable && c.DailyRank == 1 && c.TiedPlays == 1 && c.TotalPlays >= TopMinPlays),
        new Definition("secret", "ジャケットを10回斬り裏に挑んだ", "裏の扉", Gimmick, c => c.Secret),
        new Definition("pioneer", "まだ誰も遊んでいない譜面を遊んだ", "開拓者", Fun, c => c.RankingAvailable && c.TotalPlays == 1),
        new Definition("jackpot", "コンボのくじで大当たりを引いた", "大当たり", Fun, c => c.Stats != null && c.Stats.Jackpots >= 1),
        new Definition("comeback", "立て直して30コンボで締めた", "立て直し", Skill,
            c => c.Bad + c.Miss >= 1 && c.Stats != null && c.Stats.FinalCombo >= ComebackCombo),
        new Definition("duet", "となりの台と同じ曲を遊んだ", "となりと同じ曲", Fun, c => c.NeighborFinishedSameSong(DuetSeconds)),
        new Definition("school", "校歌を最後まで斬った", "文化祭の主役", Fun, c => c.SongId == RushESecretState.EpilogueFall.SongId),
        new Definition("ranks", "Sランク以上をとった", "Sランク", Skill, c => c.Rank >= PlayRank.S),
        new Definition("hard", "Hardをクリアした", "Hardクリア", Skill, c => c.IsHard && c.Rank >= PlayRank.B),
        new Definition("chorus", "最後のサビをノーミスで駆け抜けた", "サビの主役", Skill,
            c => c.NoteStats && c.Stats.AllClean(n => n.Chorus, ChorusMinNotes)),
        new Definition("quick", "15秒以内に曲を決めた", "即決", Fun, c => c.SelectionSeconds.HasValue && c.SelectionSeconds.Value <= QuickSeconds),
        new Definition("combo50", "50コンボをつないだ", "50コンボ", Skill, c => c.MaxCombo >= SmallCombo),
        new Definition("goldall", "金のノーツを1つ残らず斬った", "黄金の手", Skill, c => c.NoteStats && c.Stats.AllCut(n => n.Gold, GoldMinNotes)),
        new Definition("dual", "赤も青もPERFECT6割に届いた", "二刀流", Skill, c => c.BothHandsPerfectRate(DualMinNotes, DualPerfectRate)),
        new Definition("arrows", "矢印のノーツを全部斬った", "矢印マスター", Skill, c => c.NoteStats && c.Stats.AllCut(n => n.Arrow, ArrowMinNotes)),
        new Definition("rainbow", "ゲートの枠を虹色に光らせた", "ボルテージMAX", Entry,
            c => c.Stats != null && c.Stats.VoltageMax >= VoltageMeter.MaximumLevel),
        new Definition("firstcut", "最初のノーツをPERFECTで斬った", "初太刀", Entry, c => c.NoteStats && c.Stats.AllPerfect(c.Stats.FirstGroup)),
        new Definition("finale", "最後の一太刀をPERFECTで決めた", "締めの一太刀", Entry, c => c.NoteStats && c.Stats.AllPerfect(c.Stats.FinalGroup)),
        new Definition("xcut", "赤と青で同時にX斬りを決めた", "X斬り", Entry, c => c.Stats != null && c.Stats.CrossCuts >= 1),
        new Definition("practice", "練習をやりとげて本番に挑んだ", "基本に忠実", Entry, c => c.PracticedBefore),
        new Definition(ClearId, "1曲を最後まで斬りきった", "完走", Entry, c => true),
    };

    static readonly Dictionary<string, Definition> byId = BuildIndex();

    static Dictionary<string, Definition> BuildIndex()
    {
        var index = new Dictionary<string, Definition>(StringComparer.Ordinal);
        for (int i = 0; i < all.Length; i++)
        {
            all[i].Order = i;
            index.Add(all[i].Id, all[i]);
        }
        return index;
    }

    // 表示の順(珍しい順、clear が最後)。
    public static IReadOnlyList<Definition> All => all;

    public static Definition Find(string id) => id != null && byId.TryGetValue(id, out var definition) ? definition : null;

    // 条件を満たす実績(表示の順)。キリ番は context.ClearNumber(clear の番号)を入れてから見る。
    public static List<Definition> Earned(AchievementContext context)
    {
        var earned = new List<Definition>();
        if (context == null) return earned;
        foreach (var definition in all)
            if (definition.Met(context)) earned.Add(definition);
        return earned;
    }
}
