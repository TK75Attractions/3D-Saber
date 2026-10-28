using System;
using System.Collections.Generic;

// 実績の一覧(2026-10-29、44個。b1 が Opus・Fable・Codex の案から選んだ catalog.md の通り。同日に第2弾の32個(catalog2.md)を
// 見積もりの珍しさで間に差し込み、76個)。
// 実績は一覧では見せず、取った人に結果画面で「〜した ○人目」を出すだけ(AchievementToast)。○人目は AchievementStore.Unlock の番号
// (2台合算・運営モードとふだんで別)。対象は記録された回(GameSession.ShouldRecordRun)だけで、判定調整・練習は対象外。
// 並びは表示の順(珍しい順)。同じ回にいくつも取れたら、この順に上から出す。clear はいちばん下。
// 数は下の定数にまとめる。裏譜面の曲と難易度の文字は RushESecretState の表、ランクは PlayRankHelper、サビは stage.json から取る。
// clear の番号を見る実績(ClearNumberIds)は clear を解除したあとに、ほかの実績の番号を見る実績(TallyIds)は最後に決める(AchievementTracker.Record)。
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
    // ---- 第2弾の数 ----
    public const int DateScoreMinScore = 10000, DateScoreModulus = 10000;
    public const int PricePerPlay = 300;
    public const double RestartMinStopSeconds = .75;
    public const double UpsideNearSeconds = .1;
    public const int UpsideAfterGroups = 2;
    public const int SebangoMinScore = 1000, SebangoModulus = 1000;
    public const int VanishKindSuccesses = 3;
    public const int TripleMinNotes = 3;
    public const int SlideInMargin = 100;
    public const string ModulationLabel = "転調後の主題";
    public const double ModulationLeadSeconds = .05;
    public const float EclipseMin = .99f;
    public const int DuoScore = 150000;
    public const double Two23Seconds = 143, Two23WindowSeconds = 1;
    public const int RelayNotes = 12;
    public const double LastSecondsLeft = 5;
    public const int HarvestCount = 8;
    public const int LeanMinJudged = 30, LeanMinOff = 5;
    // 1オクターブ(ロングの途中の鐘が G5 から G6 へ上がりきって、最後にもう1回)。
    public static int OctaveCuts => FlourishSfx.LongStepCount + 1;
    public const int TwinPrimeMin = 5;
    public const double StormSeconds = 2;
    public const int StormMinNotes = 8;
    public const double CenterMs = 10;
    public const int CenterCount = 50;
    public const int LunchFromHour = 12, LunchToHour = 13;
    public const double SilenceSeconds = 2;
    public const string ClearId = "clear", KiribanId = "kiriban", IchibanId = "ichiban";
    public const string TwinPrimeId = "twinprime", SebangoId = "sebango", FirstEverId = "firstever", HarvestId = "harvest";
    // clear の番号(ClearNumber)を見る実績と、この回でほかに解除した実績(Unlocked)を見る実績。
    public static readonly IReadOnlyList<string> ClearNumberIds = new[] { KiribanId, TwinPrimeId, SebangoId };
    public static readonly IReadOnlyList<string> TallyIds = new[] { FirstEverId, HarvestId };
    // 初物が番号を見ない実績と、大収穫が数えない実績。
    static readonly string[] firstEverIgnores = { ClearId, KiribanId, FirstEverId, HarvestId };
    static readonly string[] harvestIgnores = { ClearId, HarvestId, FirstEverId };
    // 消える(V)の種類(fade・sudden・blink・hide)。
    static readonly VanishMode[] vanishKinds = Array.FindAll((VanishMode[])Enum.GetValues(typeof(VanishMode)), m => m != VanishMode.None);

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
        new Definition("datescore", "今日の日付を点に刻んだ", "本日付の点", Fun,
            c => c.Score >= DateScoreMinScore && c.Score % DateScoreModulus == 100 * c.StartedLocal.Month + c.StartedLocal.Day),
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
        new Definition("ichien", "300円ぶん、ちょうど300回斬った", "一斬り一円", Fun, c => c.CutCount == PricePerPlay),
        new Definition("restart", "止まった流れの再開を全部決めた", "時を動かす剣", Gimmick,
            c => c.Is(RushESecretState.LogicSpeed) && c.NoteStats && c.Stats.AllPerfect(c.Stats.FirstGroupsAfterStops(RestartMinStopSeconds))),
        new Definition("upside", "逆さの世界へ着地した", "天地着地", Gimmick,
            c => c.Is(RushESecretState.ManicSonicReverse) && c.NoteStats
                && c.Stats.AllPerfect(c.Stats.AroundFirstTurn(UpsideNearSeconds, UpsideAfterGroups))),
        new Definition("clock", "14:23に2:23 AMを遊んだ", "2時23分", Fun,
            c => c.SongId == RushESecretState.TwoTwentyThreeLight.SongId && c.Spans(ClockHour, ClockMinute)),
        new Definition(SebangoId, "点に自分の番号を刻んだ", "背番号", Fun,
            c => c.ClearNumber > 0 && c.Score >= SebangoMinScore && c.Score % SebangoModulus == c.ClearNumber % SebangoModulus),
        new Definition("onearm", "Tでしびれても金を斬った", "片腕の闘牛士", Gimmick,
            c => c.Is(RushESecretState.AndalusiaBull) && c.NoteStats && c.Stats.Count(OneArmGold) >= 1),
        new Definition("vanish_four", "四つの消え方を見届けて斬った", "消失見本市", Gimmick,
            c => c.Is(RushESecretState.ZeroVanish) && c.NoteStats
                && Array.TrueForAll(vanishKinds, kind => c.Stats.Count(n => n.Vanish == kind && n.Success) >= VanishKindSuccesses)),
        new Definition("lastpiece", "今日の全曲をそろえる1曲を遊んだ", "最後のピース", Fun, c => c.CompletesTodaysSongs()),
        new Definition("sottoji", "裏の扉を開けて、そっと閉じた", "そっ閉じ", Fun, c => c.OpenedSecretHere && !c.Secret),
        new Definition("triple", "3つ同時をPERFECTで斬った", "三刀流", Rare, c => c.NoteStats && HasPerfectGroup(c.Stats, TripleMinNotes)),
        new Definition("slidein", "ランクに100点差以内で滑り込んだ", "滑り込み", Fun,
            c => c.Score >= PlayRankHelper.ScoreLowerBound(PlayRank.B) && c.Score - PlayRankHelper.ScoreLowerBound(c.Rank) < SlideInMargin),
        new Definition("hardsplus", "HardでS+をとった", "剣の名手", Rare, c => c.IsHard && c.Rank >= PlayRank.SPlus),
        new Definition("secret_s", "裏譜面でS以上をとった", "秘密の実力者", Gimmick, c => c.Secret && c.Rank >= PlayRank.S),
        new Definition("wind", "Wの風のノーツを50個斬った", "風読み", Gimmick,
            c => c.Is(RushESecretState.AobasouWind) && c.NoteStats && c.Stats.CutCount(n => n.Wind) >= WindCuts),
        new Definition("overtake", "Pの追い越しを20個見抜いた", "順番を見抜く", Gimmick,
            c => c.Is(RushESecretState.YurikagoOvertake) && c.NoteStats && c.Stats.CutCount(n => n.Overtake) >= OvertakeCuts),
        new Definition("dark", "暗闇のYを精度60%で抜けた", "夜を照らす剣", Gimmick,
            c => c.Is(RushESecretState.TwoTwentyThreeLight) && c.Accuracy >= DarkAccuracy),
        new Definition(IchibanId, "その日の一番乗りで遊んだ", "一番乗り", Fun, c => c.ClearsToday == 0),
        new Definition("modulate", "校歌の転調の瞬間を決めた", "転調の合図", Fun,
            c => c.SongId == RushESecretState.EpilogueFall.SongId && c.NoteStats
                && c.Stats.AllPerfect(c.Stats.GroupAfterSection(ModulationLabel, ModulationLeadSeconds))),
        new Definition("eclipse", "皆既日食の瞬間をPERFECTで斬った", "皆既の一太刀", Fun,
            c => c.NoteStats && c.Stats.AllPerfect(n => n.Eclipse >= EclipseMin, 1)),
        new Definition("streak50", "PERFECTを50回続けた", "乱れぬ五十拍", Rare, c => c.NoteStats && c.Stats.MaxPerfectStreak >= LongStreak),
        new Definition("fc", "フルコンボを達成した", "FULL COMBO", Rare, c => c.FullCombo),
        new Definition(KiribanId, "キリ番(100人ごと)を踏んだ", "キリ番", Fun, c => c.ClearNumber > 0 && c.ClearNumber % KiribanEvery == 0),
        new Definition("zorome", "点の下3桁をゾロ目にした", "ゾロ目", Fun,
            c => c.Score >= ZoromeMinScore && c.Score % ZoromeModulus % ZoromeStep == 0),
        new Definition("jihou", "時報の瞬間にノーツを斬った", "時報の剣", Fun, c => c.NoteStats && c.Stats.Count(OnTheHour) >= 1),
        new Definition("duo150k", "となりと合わせて15万点を出した", "ふたりで15万", Fun, c => NeighborTotal(c) >= DuoScore),
        new Definition(FirstEverId, "まだ誰も取っていない実績を取った", "初物", Fun, FirstEver),
        new Definition("two23", "2:23 AMの2分23秒を決めた", "2分23秒", Fun,
            c => c.SongId == RushESecretState.TwoTwentyThreeLight.SongId && c.NoteStats
                && c.Stats.AllPerfect(c.Stats.NearestGroup(Two23Seconds, Two23WindowSeconds))),
        new Definition("splus", "最高ランクS+をとった", "S+ランク", Skill, c => c.Rank >= PlayRank.SPlus),
        new Definition("combo100", "100コンボをつないだ", "100コンボ", Skill, c => c.MaxCombo >= BigCombo),
        new Definition("redblue", "赤青のバトンを交互につないだ", "紅青リレー", Skill, c => c.NoteStats && c.Stats.LongestRedBlueRelay() >= RelayNotes),
        new Definition("lastsec", "残り5秒で曲を決めた", "駆け込み", Fun,
            c => c.SelectionSeconds.HasValue && c.SelectionSeconds.Value >= AchievementRunInfo.SelectionStartSeconds - LastSecondsLeft
                && c.SelectionSeconds.Value < AchievementRunInfo.SelectionStartSeconds),
        new Definition("buzzer", "最後の一太刀でランクを上げた", "土壇場の一太刀", Fun, RankUpAtTheEnd),
        new Definition(HarvestId, "1曲で8個の実績を取った", "大収穫", Fun, c => Harvested(c) >= HarvestCount),
        new Definition("early", "ずれた判定が全部早めだった", "前のめり", Fun, c => c.NoteStats && c.Stats.OffBeatsLean(-1, LeanMinJudged, LeanMinOff)),
        new Definition("octave", "ロングで1オクターブ鳴らしきった", "ソからソへ", Skill,
            c => c.NoteStats && c.Stats.Count(n => n.Long && n.Cuts >= OctaveCuts && n.Perfect) >= 1),
        new Definition("streak20", "PERFECTを20回続けた", "20連PERFECT", Skill, c => c.NoteStats && c.Stats.MaxPerfectStreak >= ShortStreak),
        new Definition("top", "この譜面の通算1位に立った", "頂点", Skill,
            c => c.RankingAvailable && c.DailyRank == 1 && c.TiedPlays == 1 && c.TotalPlays >= TopMinPlays),
        new Definition("secret", "ジャケットを10回斬り裏に挑んだ", "裏の扉", Gimmick, c => c.Secret),
        new Definition("pioneer", "まだ誰も遊んでいない譜面を遊んだ", "開拓者", Fun, c => c.RankingAvailable && c.TotalPlays == 1),
        new Definition("jackpot", "コンボのくじで大当たりを引いた", "大当たり", Fun, c => c.Stats != null && c.Stats.Jackpots >= 1),
        new Definition("comeback", "立て直して30コンボで締めた", "立て直し", Skill,
            c => c.Bad + c.Miss >= 1 && c.Stats != null && c.Stats.FinalCombo >= ComebackCombo),
        new Definition("duet", "となりの台と同じ曲を遊んだ", "となりと同じ曲", Fun, c => c.NeighborFinishedSameSong(DuetSeconds)),
        new Definition(TwinPrimeId, "双子の素数番で遊んだ", "双子の素数", Fun,
            c => c.ClearNumber >= TwinPrimeMin && IsPrime(c.ClearNumber) && IsPrime(c.ClearNumber - 2)),
        new Definition("storm", "いちばん密な2秒をノーミスで抜けた", "嵐の2秒", Skill, c => c.NoteStats && StormCleared(c.Stats)),
        new Definition("center10", "誤差10ms以内で50回斬った", "ど真ん中", Skill,
            c => c.NoteStats && c.Stats.Count(n => n.Cut && n.ErrorMs.HasValue && Math.Abs(n.ErrorMs.Value) <= CenterMs) >= CenterCount),
        new Definition("school", "校歌を最後まで斬った", "文化祭の主役", Fun, c => c.SongId == RushESecretState.EpilogueFall.SongId),
        new Definition("late", "ずれた判定が全部遅めだった", "ためて斬る", Fun, c => c.NoteStats && c.Stats.OffBeatsLean(1, LeanMinJudged, LeanMinOff)),
        new Definition("reswing", "矢印を振り直してPERFECTにした", "仕切り直し", Skill,
            c => c.NoteStats && c.Stats.Count(n => n.Arrow && n.WrongSwing && n.Perfect) >= 1),
        new Definition("lunchtime", "お昼の一曲を演奏した", "昼休み公演", Fun,
            c => c.StartedLocal.Hour >= LunchFromHour && c.StartedLocal.Hour < LunchToHour),
        new Definition("ranks", "Sランク以上をとった", "Sランク", Skill, c => c.Rank >= PlayRank.S),
        new Definition("hard", "Hardをクリアした", "Hardクリア", Skill, c => c.IsHard && c.Rank >= PlayRank.B),
        new Definition("chorus", "最後のサビをノーミスで駆け抜けた", "サビの主役", Skill,
            c => c.NoteStats && c.Stats.AllClean(n => n.Chorus, ChorusMinNotes)),
        new Definition("quick", "15秒以内に曲を決めた", "即決", Fun, c => c.SelectionSeconds.HasValue && c.SelectionSeconds.Value <= QuickSeconds),
        new Definition("lefthand", "タイトルを青の剣で斬って始めた", "左手で幕開け", Entry, c => c.TitleHand == SaberHand.Left),
        new Definition("combo50", "50コンボをつないだ", "50コンボ", Skill, c => c.MaxCombo >= SmallCombo),
        new Definition("goldall", "金のノーツを1つ残らず斬った", "黄金の手", Skill, c => c.NoteStats && c.Stats.AllCut(n => n.Gold, GoldMinNotes)),
        new Definition("dual", "赤も青もPERFECT6割に届いた", "二刀流", Skill, c => c.BothHandsPerfectRate(DualMinNotes, DualPerfectRate)),
        new Definition("arrows", "矢印のノーツを全部斬った", "矢印マスター", Skill, c => c.NoteStats && c.Stats.AllCut(n => n.Arrow, ArrowMinNotes)),
        new Definition("silence", "長い休みのあとの一太刀を決めた", "静寂明け", Entry, c => c.NoteStats && AfterSilence(c.Stats)),
        new Definition("fivekinds", "5種類のノーツを1つずつ決めた", "五目斬り", Entry, c => c.NoteStats && FiveKinds(c.Stats)),
        new Definition("rainbow", "ゲートの枠を虹色に光らせた", "ボルテージMAX", Entry,
            c => c.Stats != null && c.Stats.VoltageMax >= VoltageMeter.MaximumLevel),
        new Definition("firstcut", "最初のノーツをPERFECTで斬った", "初太刀", Entry, c => c.NoteStats && c.Stats.AllPerfect(c.Stats.FirstGroup)),
        new Definition("finale", "最後の一太刀をPERFECTで決めた", "締めの一太刀", Entry, c => c.NoteStats && c.Stats.AllPerfect(c.Stats.FinalGroup)),
        new Definition("xcut", "赤と青で同時にX斬りを決めた", "X斬り", Entry, c => c.Stats != null && c.Stats.CrossCuts >= 1),
        new Definition("practice", "練習をやりとげて本番に挑んだ", "基本に忠実", Entry, c => c.PracticedBefore),
        new Definition(ClearId, "1曲を最後まで斬りきった", "完走", Entry, c => true),
    };

    // ---- 第2弾の条件の部品 ----

    // 片腕の闘牛士: 反対の剣がしびれている間に切った金で、切った剣の側にあるもの(右の剣は x ≥ 0、左の剣は x ≤ 0。腕を交差させない)。
    static bool OneArmGold(PlayStats.Note n) =>
        n.Gold && n.Cut && n.OtherStunned && (n.Hand == SaberHand.Right ? n.X >= 0 : n.Hand == SaberHand.Left && n.X <= 0);

    // 時報: 結果が出た台の時刻が、どれかの ○時00分00秒台(MISS には時刻が無い)。
    static bool OnTheHour(PlayStats.Note n) => n.Cut && n.JudgedAt.HasValue && n.JudgedAt.Value.Minute == 0 && n.JudgedAt.Value.Second == 0;

    // 三刀流: minNotes 個以上の組のどれかが、すべて PERFECT。
    static bool HasPerfectGroup(PlayStats stats, int minNotes)
    {
        foreach (var group in stats.Groups)
            if (group.Count >= minNotes && stats.AllPerfect(group)) return true;
        return false;
    }

    // ふたりで15万: となりの回(AchievementContext.Neighbor)の点との合計。となりの回が無ければ -1。
    static long NeighborTotal(AchievementContext c)
    {
        var play = c.Neighbor(DuetSeconds);
        return play != null && play.score >= 0 ? (long)c.Score + play.score : -1;
    }

    // 土壇場の一太刀: 最後の組の判定で HUD のランクが上がり、曲の終わりの点のランクがそれ以上。
    static bool RankUpAtTheEnd(AchievementContext c)
    {
        if (!c.NoteStats || !c.Stats.FinalScoreBefore.HasValue || !c.Stats.FinalScoreAfter.HasValue) return false;
        var after = PlayRankHelper.FromScore(c.Stats.FinalScoreAfter.Value);
        return after > PlayRankHelper.FromScore(c.Stats.FinalScoreBefore.Value) && c.Rank >= after;
    }

    // 嵐の2秒: いちばん密な窓のノーツが StormMinNotes 個以上で、どれもクリーン。
    static bool StormCleared(PlayStats stats)
    {
        var window = stats.DensestWindow(StormSeconds);
        return window.Count >= StormMinNotes && stats.AllClean(window);
    }

    // 静寂明け: いちばん長い休みが SilenceSeconds 以上で、そのあとの組がすべて PERFECT。
    static bool AfterSilence(PlayStats stats)
    {
        var group = stats.AfterLongestRest(out double rest);
        return rest >= SilenceSeconds && stats.AllPerfect(group);
    }

    // 五目斬り: 赤・青・金・矢印・ロングの種類ごとに PERFECT が1個以上(金の矢印は金と矢印の両方に数える)。
    static readonly Func<PlayStats.Note, bool>[] fiveKinds = { n => n.Red, n => n.Blue, n => n.Gold, n => n.Arrow, n => n.Long };

    static bool FiveKinds(PlayStats stats)
    {
        foreach (var kind in fiveKinds)
            if (stats.Count(n => kind(n) && n.Perfect) < 1) return false;
        return true;
    }

    // 初物: この回でほかに解除した実績(clear・キリ番・初物・大収穫を除く)のどれかが、2台合わせて初めて(番号 1)。
    static bool FirstEver(AchievementContext c)
    {
        if (c.Unlocked == null) return false;
        foreach (var unlock in c.Unlocked)
            if (unlock.Number == 1 && Array.IndexOf(firstEverIgnores, unlock.Id) < 0) return true;
        return false;
    }

    // 大収穫: この回でほかに解除した実績(clear・大収穫・初物を除く)の数。
    static int Harvested(AchievementContext c)
    {
        int count = 0;
        if (c.Unlocked != null)
            foreach (var unlock in c.Unlocked)
                if (unlock.Definition != null && Array.IndexOf(harvestIgnores, unlock.Id) < 0) count++;
        return count;
    }

    // 素数か(番号は来場の数なので、割り算で見る)。
    public static bool IsPrime(int n)
    {
        if (n < 2) return false;
        if (n % 2 == 0) return n == 2;
        for (int d = 3; (long)d * d <= n; d += 2)
            if (n % d == 0) return false;
        return true;
    }

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

    // 条件を満たす実績(表示の順)。ClearNumberIds は context.ClearNumber(clear の番号)を、TallyIds は context.Unlocked を入れてから見る。
    public static List<Definition> Earned(AchievementContext context)
    {
        var earned = new List<Definition>();
        if (context == null) return earned;
        foreach (var definition in all)
            if (definition.Met(context)) earned.Add(definition);
        return earned;
    }
}
