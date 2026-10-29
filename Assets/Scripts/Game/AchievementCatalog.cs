using System;
using System.Collections.Generic;

// 実績の段(2026-10-29 ユーザー「高難易度と思われる実績はふちを銅、銀、金、虹に光らせて効果音も変えて」)。取りにくさの目安で、
// 知らせのカード(AchievementToast)のふちの色と光・音を変える。None は光らない控えめなカード。
// 段の決め方(b1 が Codex・Fable・Opus の見立てから決めた。目安は なし 10% 以上・銅 3〜10%・銀 1〜3%・金 0.1〜1%・虹 0.1% 未満):
// 腕前の実績は取れる見込み(曲・裏譜面・Hard のものは、その譜面を遊んだ人の中での見込み。選んだ珍しさは数えない)、
// 運のもの(キリ番・点の数字など)は珍しさどおり、選ぶだけ・その場にいるだけのもの(一番乗り・となりと同じ曲など)は銅まで。
// 同日夜に見直した(ユーザー「実績の難易度を再度考え直す　簡単なのに虹なやつとかないか」): Codex が譜面の数(対象のノーツ・切れる回数)と
// 台B の作り手の記録32回から、初見の来場者(Easy・Normal が多い)での見込みを数え直し、36個の段を変えた(虹は AP と本日付の点だけ)。
public enum AchievementTier { None, Bronze, Silver, Gold, Rainbow }

// 実績の一覧(2026-10-29、44個。b1 が Opus・Fable・Codex の案から選んだ catalog.md の通り。同日に第2弾の32個(catalog2.md)、
// 第3弾の26個(catalog3、v2.2。Codex・Fable・Opus の案を Codex と話し合って選んだ)を足して102個。同日夜に第4弾を陣ごとに足す
// (ユーザー「実績を増やす」。1陣目は3人の票の多い16個)。
// 実績は一覧では見せず、取った人に結果画面で「〜した ○人目」を出すだけ(AchievementToast)。○人目は AchievementStore.Unlock の番号
// (2台合算・運営モードとふだんで別)。対象は記録された回(GameSession.ShouldRecordRun)だけで、判定調整・練習は対象外。
// 並びは表示の順(v2.2 ユーザー「表示の順番は簡単なものから」): 段の低い順、同じ段の中は見込みの高い順。clear がいちばん上。
// 同じ回にいくつも取れたら、この順に出す(最後にいちばん難しいもの)。
// 数は下の定数にまとめる。裏譜面の曲と難易度の文字は RushESecretState の表、ランクは PlayRankHelper、サビは stage.json から取る。
// clear の番号を見る実績(ClearNumberIds)は clear を解除したあとに、ほかの実績の番号を見る実績(TallyIds)は最後に決める(AchievementTracker.Record)。
public static partial class AchievementCatalog
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
    // ---- 第4弾の数 ----
    public const int HoldCutRequiredNotes = 3, MochitsukiMinCuts = 6, SweepRequiredGroups = 3;
    public const double SeishoFinishWindowSeconds = 60, MochitsukiAmbiguousSeconds = .05;
    public const int QuizPassNumerator = 3, QuizPassDenominator = 5;
    public const int PalindromeMinScore = 10000, PalindromeMaxScore = 99999, PalindromeDigits = 5;
    public const int GridColumns = 3, GridRows = 3, RevengeRequiredCount = 3;
    public const float GridHalfWidth = 5.5f, GridHalfHeight = 3f;
    public const int NothornHitNumerator = 1, NothornHitDenominator = 2;
    public const double NothornVisibleRate = .90;
    public const int OneHandPerfectNotes = 8, RisingMinNotes = 50, RisingNumerator = 4, RisingDenominator = 5;
    public const float RisingMaxAngleDegrees = 70;
    public const int SlowStartOpeningNotes = 10, SlowStartFailures = 5, SceneMasterMinSections = 4, HandPerfectMinNotes = 20;
    public const PlayRank SlowStartRequiredRank = PlayRank.S;
    public const double SceneMasterLeadSeconds = .05, SceneMasterEntrySeconds = 1, BackpickPreviewSeconds = 3;

    // ---- 第3弾の数 ----
    public const int PairNotes = 2, RegainReaches = 2;
    public const int PracticeLongMinCuts = 2, PracticeLongMaxCuts = 4;
    public const int BalanceMinCuts = 20, CrossCutTen = 10;
    public const int TrendPreviousPlays = 2;
    public const double TrendSeconds = 30 * 60;
    public const int HintMinShown = 1, GoldCoinCount = 40, CradleHandCuts = 10;
    public const string EasyDifficulty = "Easy", NormalDifficulty = "Normal";
    public const double SceneLeadSeconds = .05;
    public const string MorningFirstSection = "第1周", MorningSecondSection = "第2周";
    public const double MorningThemeSeconds = 2.035;
    public const int MorningEasyNotes = 3, MorningNormalNotes = 6, MorningHardNotes = 7;
    public const string ManicQuietMarker = "BPM60", ManicReturnMarker = "再加速";
    public const double ManicQuietSeconds = 127.5, ManicReturnSeconds = 143.5;
    public static readonly IReadOnlyList<double> ManicQuietOffsets = new[] { 0d, 3, 6, 10, 13 };
    public const double RushEIntroSeconds = 7.2;
    public const int RushEIntroNotes = 4;
    public const string PhobiaMainSection = "主部", EscapadePeakSection = "高揚部";
    public const int EscapadePeaks = 5, TwinStarMinCuts = 20, HundredCuts = 100;
    // 名場面の入口の組は、区間の始まりからこの秒以内(escapade Easy の高揚部3は 0.63 秒後)。入口が欠けた譜面で、あとの組を代わりに使わない。
    public const double SceneEntrySeconds = 1.0;
    // Phobia の主部の入口は区間の始まりの 0.01 秒後(次の組は Hard で 0.45 秒後なので、窓はそれより狭く)。
    public const double PhobiaEntrySeconds = .1;
    // 青葉騒の金の双葉(高揚部3の始まり)の時刻と、ずれてよい幅(offsetMs の直しで少し動く)。
    public const double AobasouGoldSeconds = 82.286, SceneTimeTolerance = .3;
    // B 案の6音は、現在の M では2組目(ノーツを初めて隠す再演)。
    public const double MemoryEchoRecallSeconds = 20.878983;
    public const int MemoryEchoHalfNotes = 3;
    public static readonly IReadOnlyList<double> EChordSeconds =
        new[] { 47.4372421, 57.0934921, 65.4372421, 95.5622421, 112.2184921, 118.382 };
    public const int MeteorRequiredCuts = 1000, MeteorCuts = 500;

    // ---- 第2弾の数 ----
    public const int DateScoreMinScore = 10000, DateScoreModulus = 10000;
    // 1プレイの値段(円。一斬り一円はこの回数ちょうど)。2026-10-29 ユーザー「一プレイ200円になったので実績のあの300のやつ直してね」で 300 → 200。
    public const int PricePerPlay = 200;
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
        public readonly AchievementTier Tier;
        public readonly Func<AchievementContext, bool> Condition;
        // 表示の順(0 がいちばん簡単 = clear)。
        public int Order { get; internal set; }

        public Definition(string id, string text, string name, string category, Func<AchievementContext, bool> condition)
            : this(id, text, name, category, AchievementTier.None, condition) { }

        public Definition(string id, string text, string name, string category, AchievementTier tier, Func<AchievementContext, bool> condition)
        {
            Id = id; Text = text; Name = name; Category = category; Tier = tier; Condition = condition;
        }

        // 1つの実績の条件が例外を出しても、ほかの実績と結果画面を止めない(取れない扱い。2026-10-30 9a)。起動の間に実績ごと1回だけ記録する。
        public bool Met(AchievementContext context)
        {
            if (context == null) return false;
            try { return Condition(context); }
            catch (Exception e)
            {
                if (!warned) { warned = true; UnityEngine.Debug.LogWarning("AchievementCatalog: " + Id + " の条件で例外が出たので取れない扱いにします: " + e.Message); }
                return false;
            }
        }

        bool warned;
    }

    const string Rare = "珍しい", Gimmick = "仕掛け", Fun = "遊び心", Skill = "腕前", Entry = "入口";

    static readonly Definition[] all =
    {
        new Definition(ClearId, "1曲を最後まで斬りきった", "完走", Entry, AchievementTier.None, c => true),
        new Definition("overtake", "Pの追い越しを20個見抜いた", "順番を見抜く", Gimmick, AchievementTier.None,
            c => c.Is(RushESecretState.YurikagoOvertake) && c.NoteStats && c.Stats.CutCount(n => n.Overtake) >= OvertakeCuts),
        new Definition("wind", "Wの風のノーツを50個斬った", "風読み", Gimmick, AchievementTier.None,
            c => c.Is(RushESecretState.AobasouWind) && c.NoteStats && c.Stats.CutCount(n => n.Wind) >= WindCuts),
        new Definition("hard", "Hardをクリアした", "Hardクリア", Skill, AchievementTier.None, c => c.IsHard && c.Rank >= PlayRank.B),
        new Definition("xcut", "赤と青で同時にX斬りを決めた", "X斬り", Entry, AchievementTier.None, c => c.Stats != null && c.Stats.CrossCuts >= 1),
        new Definition("firstcut", "最初のノーツをPERFECTで斬った", "初太刀", Entry, AchievementTier.None, c => c.NoteStats && c.Stats.AllPerfect(c.Stats.FirstGroup)),
        new Definition("rainbow", "ゲートの枠を虹色に光らせた", "ボルテージMAX", Entry, AchievementTier.None,
            c => c.Stats != null && c.Stats.VoltageMax >= VoltageMeter.MaximumLevel),
        new Definition("vanish_four", "四つの消え方を見届けて斬った", "消失見本市", Gimmick, AchievementTier.None,
            c => c.Is(RushESecretState.ZeroVanish) && c.NoteStats
                && Array.TrueForAll(vanishKinds, kind => c.Stats.Count(n => n.Vanish == kind && n.Success) >= VanishKindSuccesses)),
        new Definition("practice", "練習をやりとげて本番に挑んだ", "基本に忠実", Entry, AchievementTier.None, c => c.PracticedBefore),
        new Definition("fivekinds", "5種類のノーツを1つずつ決めた", "五目斬り", Entry, AchievementTier.None, c => c.NoteStats && FiveKinds(c.Stats)),
        new Definition(HarvestId, "1曲で8個の実績を取った", "大収穫", Fun, AchievementTier.None, c => Harvested(c) >= HarvestCount),
        new Definition("silence", "長い休みのあとの一太刀を決めた", "静寂明け", Entry, AchievementTier.None, c => c.NoteStats && AfterSilence(c.Stats)),
        new Definition("finale", "最後の一太刀をPERFECTで決めた", "締めの一太刀", Entry, AchievementTier.None, c => c.NoteStats && c.Stats.AllPerfect(c.Stats.FinalGroup)),
        new Definition("eclipse", "皆既日食の瞬間をPERFECTで斬った", "皆既の一太刀", Fun, AchievementTier.None,
            c => c.NoteStats && c.Stats.AllPerfect(n => n.Eclipse >= EclipseMin, 1)),
        new Definition("passed", "Qの試験で6割以上正解した", "合格点", Gimmick, AchievementTier.None,
            c => c.Is(RushESecretState.BenkyouQuiz) && c.NoteStats && c.Stats.QuizSuccessRate(QuizPassNumerator, QuizPassDenominator)),
        new Definition("goldcoin", "黄金郷で金貨を40枚拾った", "黄金郷の財宝", Fun, AchievementTier.None,
            c => c.SongId == RushESecretState.ElDoradoG.SongId && !c.Secret && c.NoteStats
                && c.Stats.CutCount(n => n.Gold) >= GoldCoinCount),
        new Definition("cradle", "揺籠を両手で揺らした", "ゆりかご揺らし", Fun, AchievementTier.None,
            c => c.SongId == RushESecretState.YurikagoOvertake.SongId && (!c.Secret || c.Is(RushESecretState.YurikagoOvertake))
                && c.NoteStats
                && c.Stats.Count(n => n.Gold && !n.Long && n.Cut && n.Hand == SaberHand.Left && n.X <= 0) >= CradleHandCuts
                && c.Stats.Count(n => n.Gold && !n.Long && n.Cut && n.Hand == SaberHand.Right && n.X >= 0) >= CradleHandCuts),
        new Definition("two23", "2:23 AMの2分23秒を決めた", "2分23秒", Fun, AchievementTier.None,
            c => c.SongId == RushESecretState.TwoTwentyThreeLight.SongId && c.NoteStats
                && c.Stats.AllPerfect(c.Stats.NearestGroup(Two23Seconds, Two23WindowSeconds))),
        new Definition("shubu", "Phobiaの主部へ斬りこんだ", "踏み込み", Fun, AchievementTier.None,
            c => c.SongId == RushESecretState.PhobiaAvoid.SongId && !c.Secret && c.NoteStats
                && c.Stats.AllPerfect(c.Stats.GroupAfterSection(PhobiaMainSection, SceneLeadSeconds, PhobiaEntrySeconds), PairNotes)),
        new Definition("goldpair", "金の和音を両手で演奏した", "金の二重奏", Fun, AchievementTier.None,
            c => c.NoteStats && c.Stats.HasGoldPair()),
        new Definition("gohoubi", "勉強のごほうびの金を決めた", "ごほうびの金", Fun, AchievementTier.None,
            c => c.SongId == RushESecretState.BenkyouQuiz.SongId && !c.Secret && c.NoteStats
                && c.Stats.AllPerfect(c.Stats.LastGroupWith(n => n.Gold), PairNotes)),
        new Definition("aobakin", "青葉騒の金の双葉を決めた", "金の双葉", Fun, AchievementTier.None,
            c => c.SongId == RushESecretState.AobasouWind.SongId && c.NoteStats
                && c.Stats.AllPerfect(c.Stats.NearTime(c.Stats.FirstGroupWith(n => n.Gold), AobasouGoldSeconds, SceneTimeTolerance), PairNotes)),
        new Definition("lefthand", "タイトルを青の剣で斬って始めた", "左手で幕開け", Entry, AchievementTier.None, c => c.TitleHand == SaberHand.Left),
        new Definition("regain", "虹の舞台をもう一度点灯した", "虹の再点灯", Skill, AchievementTier.None,
            c => c.Stats != null && c.Stats.VoltageMaxReaches >= RegainReaches),
        new Definition("modulate", "校歌の転調の瞬間を決めた", "転調の合図", Fun, AchievementTier.None,
            c => c.SongId == RushESecretState.EpilogueFall.SongId && c.NoteStats
                && c.Stats.AllPerfect(c.Stats.GroupAfterSection(ModulationLabel, ModulationLeadSeconds))),
        new Definition("arrows", "矢印のノーツを全部斬った", "矢印マスター", Skill, AchievementTier.None, c => c.NoteStats && c.Stats.AllCut(n => n.Arrow, ArrowMinNotes)),
        new Definition("dark", "暗闇のYを精度60%で抜けた", "夜を照らす剣", Gimmick, AchievementTier.None,
            c => c.Is(RushESecretState.TwoTwentyThreeLight) && c.Accuracy >= DarkAccuracy),
        new Definition("m_echo", "見本と記憶の六音を再現した", "記憶のこだま", Gimmick, AchievementTier.None,
            c => c.Is(RushESecretState.MorningMemory) && c.NoteStats
                && c.Stats.AllSuccess(c.Stats.MemoryPair(MemoryEchoRecallSeconds, MemoryEchoHalfNotes))),
        new Definition("dual", "赤も青もPERFECT6割に届いた", "二刀流", Skill, AchievementTier.None, c => c.BothHandsPerfectRate(DualMinNotes, DualPerfectRate)),
        new Definition("combo50", "50コンボをつないだ", "50コンボ", Skill, AchievementTier.None, c => c.MaxCombo >= SmallCombo),
        new Definition("reswing", "矢印を振り直してPERFECTにした", "仕切り直し", Skill, AchievementTier.None,
            c => c.NoteStats && c.Stats.Count(n => n.Arrow && n.WrongSwing && n.Perfect) >= 1),
        new Definition("octave", "ロングで1オクターブ鳴らしきった", "ソからソへ", Skill, AchievementTier.None,
            c => c.NoteStats && c.Stats.Count(n => n.Long && n.Cuts >= OctaveCuts && n.Perfect) >= 1),
        new Definition("onearm", "Tでしびれても金を斬った", "片腕の闘牛士", Gimmick, AchievementTier.None,
            c => c.Is(RushESecretState.AndalusiaBull) && c.NoteStats && c.Stats.Count(OneArmGold) >= 1),
        new Definition("manicbreath", "静かな五打から再加速した", "五打の深呼吸", Fun, AchievementTier.None,
            c => c.SongId == RushESecretState.ManicSonicReverse.SongId && c.NoteStats
                && (string.Equals(c.Difficulty, EasyDifficulty, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.Difficulty, NormalDifficulty, StringComparison.OrdinalIgnoreCase))
                && c.Stats.AllSuccess(c.Stats.ManicBreathNotes())),
        new Definition("learnpair", "練習の二つの技を本番で実践した", "予習の成果", Entry, AchievementTier.None,
            c => c.PracticePairBefore && c.NoteStats && c.Stats.PracticePairSucceeded(PracticeLongMinCuts, PracticeLongMaxCuts)),
        new Definition("goldall", "金のノーツを1つ残らず斬った", "黄金の手", Skill, AchievementTier.None, c => c.NoteStats && c.Stats.AllCut(n => n.Gold, GoldMinNotes)),
        new Definition("storm", "いちばん密な2秒をノーミスで抜けた", "嵐の2秒", Skill, AchievementTier.None, c => c.NoteStats && StormCleared(c.Stats)),
        new Definition("goodstart", "最初の10個を一つも落とさなかった", "好発進", Entry, AchievementTier.None, c => c.NoteStats && c.Stats.Batch5aGoodStart()),
        new Definition("morningecho", "2周目の朝で上達を見せた", "朝のこだま", Fun, AchievementTier.None,
            c => c.SongId == RushESecretState.MorningMemory.SongId && !c.Secret && c.NoteStats
                && c.Stats.MorningImproved(MorningNoteCount(c.Difficulty))),
        new Definition("chorus", "最後のサビをノーミスで駆け抜けた", "サビの主役", Skill, AchievementTier.None,
            c => c.NoteStats && c.Stats.AllClean(n => n.Chorus, ChorusMinNotes)),
        new Definition("kiriage", "タイトルを斬り上げて始めた", "斬り上げ", Entry, AchievementTier.None,
            c => c.TitleUpward && (c.TitleHand == SaberHand.Left || c.TitleHand == SaberHand.Right)),
        new Definition("quick", "15秒以内に曲を決めた", "即決", Fun, AchievementTier.None, c => c.SelectionSeconds.HasValue && c.SelectionSeconds.Value <= QuickSeconds),
        new Definition("triple", "3つ同時をPERFECTで斬った", "三刀流", Rare, AchievementTier.None, c => c.NoteStats && HasPerfectGroup(c.Stats, TripleMinNotes)),
        new Definition("redblue", "赤青のバトンを交互につないだ", "紅青リレー", Skill, AchievementTier.None, c => c.NoteStats && c.Stats.LongestRedBlueRelay() >= RelayNotes),
        new Definition("setsujoku", "同じ場所のミスを3回取り返した", "雪辱の一太刀", Skill, AchievementTier.None,
            c => c.NoteStats && c.Stats.RevengeCount() >= RevengeRequiredCount),
        new Definition("twinstar", "Zeroの双星ロングを切りきった", "双星の軌道", Skill, AchievementTier.None,
            c => c.SongId == RushESecretState.ZeroVanish.SongId && c.NoteStats
                && c.Stats.AllPerfect(c.Stats.MaximumLongs(TwinStarMinCuts), PairNotes)),
        new Definition("jackpot", "コンボのくじで大当たりを引いた", "大当たり", Fun, AchievementTier.None, c => c.Stats != null && c.Stats.Jackpots >= 1),
        new Definition("upside", "逆さの世界へ着地した", "天地着地", Gimmick, AchievementTier.None,
            c => c.Is(RushESecretState.ManicSonicReverse) && c.NoteStats
                && c.Stats.AllPerfect(c.Stats.AroundFirstTurn(UpsideNearSeconds, UpsideAfterGroups))),
        new Definition("matador", "Tでしびれを5回以内に抑えた", "マタドール", Gimmick, AchievementTier.None,
            c => c.Is(RushESecretState.AndalusiaBull) && c.Stats != null && c.Stats.HasBull && c.Stats.Stuns <= MatadorMaxStuns),
        new Definition("comeback", "立て直して30コンボで締めた", "立て直し", Skill, AchievementTier.None,
            c => c.Bad + c.Miss >= 1 && c.Stats != null && c.Stats.FinalCombo >= ComebackCombo),
        new Definition("kangaii", "説明なしで初めての矢印を読み切った", "勘がいい", Entry, AchievementTier.None, c => c.NoteStats && c.PracticeHistoryKnown && !c.PracticeSeen && !c.PracticedBefore && c.Stats.Batch5aFirstArrow()),
        new Definition("lunchtime", "お昼の一曲を演奏した", "昼休み公演", Fun, AchievementTier.None,
            c => c.StartedLocal.Hour >= LunchFromHour && c.StartedLocal.Hour < LunchToHour),
        new Definition("school", "校歌を最後まで斬った", "文化祭の主役", Fun, AchievementTier.None, c => c.SongId == RushESecretState.EpilogueFall.SongId),
        new Definition("ranks", "Sランク以上をとった", "Sランク", Skill, AchievementTier.None, c => c.Rank >= PlayRank.S),
        new Definition("secret_s", "裏譜面でS以上をとった", "秘密の実力者", Gimmick, AchievementTier.None, c => c.Secret && c.Rank >= PlayRank.S),
        new Definition("rushe4", "RUSH Eの4つのEを決めた", "4つのE", Fun, AchievementTier.None,
            c => c.SongId == RushESecretState.RushE.SongId && c.NoteStats
                && c.Stats.AllPerfect(n => n.Time < RushEIntroSeconds, RushEIntroNotes)),
        new Definition("souage", "練習の総仕上げを1周で決めた", "総仕上げ", Entry, AchievementTier.None,
            c => c.PracticedBefore && c.PracticeAllFirstLoop),
        new Definition("xcut10", "X斬りを10回決めた", "十字の達人", Skill, AchievementTier.None,
            c => c.NoteStats && c.Stats.CrossCuts >= CrossCutTen),
        new Definition("restart", "止まった流れの再開を全部決めた", "時を動かす剣", Gimmick, AchievementTier.None,
            c => c.Is(RushESecretState.LogicSpeed) && c.NoteStats && c.Stats.AllPerfect(c.Stats.FirstGroupsAfterStops(RestartMinStopSeconds))),
        new Definition("owariyoshi", "出だしを落としても最後を決めた", "終わりよければ", Entry, AchievementTier.None, c => c.NoteStats && c.Stats.Batch5aGoodEnding()),
        new Definition("mochitsuki", "金ロングを両手交互に6打つないだ", "餅つき", Skill, AchievementTier.Bronze,
            c => c.NoteStats && c.Stats.HasMochitsuki()),
        new Definition("yosumi", "ゲートの四すみでPERFECTを決めた", "四すみ制覇", Skill, AchievementTier.Bronze, c => c.NoteStats && c.Stats.Batch5aFourCorners()),
        new Definition("kinou_koe", "昨日の人数を今日で超えた", "昨日越え", Fun, AchievementTier.Bronze, c => c.KinouKoeBatch5b()),
        new Definition("holdcut", "ロング中に反対の手で3個斬った", "押さえ斬り", Skill, AchievementTier.Bronze,
            c => c.NoteStats && c.Stats.HeldOppositeNotes() >= HoldCutRequiredNotes),
        new Definition("streak20", "PERFECTを20回続けた", "20連PERFECT", Skill, AchievementTier.Bronze, c => c.NoteStats && c.Stats.MaxPerfectStreak >= ShortStreak),
        new Definition("kouyou5", "escapadeの5つの高揚を決めた", "五つの高揚", Skill, AchievementTier.Bronze,
            c => c.SongId == RushESecretState.EscapadeLong.SongId && c.NoteStats
                && c.Stats.AllPerfect(c.Stats.GroupsAfterSections(EscapadePeakSection, SceneLeadSeconds, EscapadePeaks, SceneEntrySeconds))),
        new Definition("senbon", "1曲で1000回斬った", "千本斬り", Rare, AchievementTier.Bronze, c => c.CutCount >= SenbonCuts),
        new Definition("hitoributai", "ひとりきりの舞台で演奏した", "独り舞台", Fun, AchievementTier.Bronze, c => c.HitoributaiBatch5b()),
        new Definition("pioneer", "まだ誰も遊んでいない譜面を遊んだ", "開拓者", Fun, AchievementTier.Bronze, c => c.RankingAvailable && c.TotalPlays == 1),
        new Definition("nothorn", "Dで的を半分以上射抜き無傷だった", "トゲ知らず", Gimmick, AchievementTier.Bronze,
            c => c.Is(RushESecretState.PhobiaAvoid) && c.AimMode && c.AimDamage == 0
                && c.Stats != null && c.Stats.AimObserved != null && c.Stats.AimObserved.Nothorn(c.Hit, c.Miss)),
        new Definition("backpick", "聴き比べて最初の曲を選んだ", "やっぱりこの曲", Fun, AchievementTier.Bronze, c => c.Backpick),
        new Definition("pairrainbow", "となりと二つの虹をかけた", "ふたつの虹", Fun, AchievementTier.Bronze,
            c => c.Stats != null && c.Stats.VoltageMax >= VoltageMeter.MaximumLevel
                && c.Neighbor(DuetSeconds)?.ach?.Contains("rainbow") == true),
        new Definition(FirstEverId, "まだ誰も取っていない実績を取った", "初物", Fun, AchievementTier.Bronze, FirstEver),
        new Definition("combo100", "100コンボをつないだ", "100コンボ", Skill, AchievementTier.Bronze, c => c.MaxCombo >= BigCombo),
        new Definition("e_chords", "Eの六つの金の和音を決めた", "六つの金鐘", Gimmick, AchievementTier.Bronze,
            c => c.Is(RushESecretState.RushE) && c.NoteStats
                && c.Stats.AllSuccess(c.Stats.GroupsAtTimes(EChordSeconds, PairNotes, n => n.Gold && !n.Long))),
        new Definition("ittoryu", "一本の剣で8連PERFECTした", "一刀流", Skill, AchievementTier.Bronze,
            c => c.NoteStats && c.Stats.HasOneHandPerfect()),
        new Definition("sasayaki", "静かな場面を全部PERFECTで斬った", "ささやき", Skill, AchievementTier.Bronze, c => c.NoteStats && c.Stats.Batch5aQuiet()),
        new Definition("sweep3", "一振りで3つ続けて斬った", "一閃三断", Skill, AchievementTier.Bronze,
            c => c.NoteStats && c.Stats.HasSweep()),
        new Definition("meteor", "Lの流星を500回斬った", "流星を追う", Gimmick, AchievementTier.Bronze,
            c => c.Is(RushESecretState.EscapadeLong) && c.NoteStats
                && c.Stats.Count(n => n.Long && n.Cuts >= MeteorRequiredCuts && n.CutsDone >= MeteorCuts) >= 1),
        new Definition("balance", "赤と青を同じ数だけ斬った", "赤青の天秤", Fun, AchievementTier.Bronze,
            c => c.NoteStats && c.Stats.EqualRedBlueCuts(BalanceMinCuts)),
        new Definition("top", "この譜面の通算1位に立った", "頂点", Skill, AchievementTier.Bronze,
            c => c.RankingAvailable && c.DailyRank == 1 && c.TiedPlays == 1 && c.TotalPlays >= TopMinPlays),
        new Definition("lastsec", "残り5秒で曲を決めた", "駆け込み", Fun, AchievementTier.Bronze,
            c => c.SelectionSeconds.HasValue && c.SelectionSeconds.Value >= AchievementRunInfo.SelectionStartSeconds - LastSecondsLeft
                && c.SelectionSeconds.Value < AchievementRunInfo.SelectionStartSeconds),
        new Definition("duo150k", "となりと合わせて15万点を出した", "ふたりで15万", Fun, AchievementTier.Bronze, c => NeighborTotal(c) >= DuoScore),
        new Definition("bullseye", "Dの的を全部射抜いた", "全弾命中", Gimmick, AchievementTier.Bronze,
            c => c.Is(RushESecretState.PhobiaAvoid) && c.AimMode && c.Hit > 0 && c.Miss == 0),
        new Definition("mirror", "Rで鏡の中のノーツを全部斬った", "鏡破り", Gimmick, AchievementTier.Bronze,
            c => c.Is(RushESecretState.ManicSonicReverse) && c.NoteStats && c.Stats.AllCut(n => n.Mirror, 1)),
        new Definition("top5", "この譜面の五傑に入った", "五傑", Skill, AchievementTier.Bronze, c => c.RankingAvailable && c.TotalPlays >= TopFiveMinPlays && c.DailyRank >= 1 && c.DailyRank <= TopFiveRank),
        new Definition(TwinPrimeId, "双子の素数番で遊んだ", "双子の素数", Fun, AchievementTier.Bronze,
            c => c.ClearNumber >= TwinPrimeMin && IsPrime(c.ClearNumber) && IsPrime(c.ClearNumber - 2)),
        new Definition("buzzer", "最後の一太刀でランクを上げた", "土壇場の一太刀", Fun, AchievementTier.Bronze, RankUpAtTheEnd),
        new Definition("nakami", "点より中身で三つの実績を取った", "点より中身", Fun, AchievementTier.Bronze, c => c.Batch5aContents()),
        new Definition("zanshin", "ロングのあとも気を抜かず斬った", "残心", Skill, AchievementTier.Bronze, c => c.NoteStats && c.Stats.Batch5aZanshin()),
        new Definition("kannon", "両腕を外へ開いて斬った", "観音開き", Skill, AchievementTier.Bronze, c => c.NoteStats && c.Stats.Batch5aKannon()),
        new Definition("bounceback", "ミスのたびすぐPERFECTで返した", "切り替え上手", Skill, AchievementTier.Bronze, c => c.NoteStats && c.Stats.Batch5aBounceBack()),
        new Definition("sync", "となりと曲も難易度もランクもそろった", "シンクロ", Fun, AchievementTier.Bronze, c => c.SyncBatch5b()),
        new Definition("kinou_ima", "昨日の今ごろと同じ曲を遊んだ", "昨日の今ごろ", Fun, AchievementTier.Bronze, c => c.KinouImaBatch5b()),
        new Definition("rising", "矢印以外の8割を斬り上げた", "昇り竜", Skill, AchievementTier.Bronze,
            c => c.NoteStats && c.Stats.HasRising()),
        new Definition("scenemaster", "盛り上がりの入口を全て決めた", "場面の主", Skill, AchievementTier.Bronze,
            c => c.NoteStats && c.Stats.AllPerfect(c.Stats.StrongSectionEntries())),
        new Definition("secret", "ジャケットを10回斬り裏に挑んだ", "裏の扉", Gimmick, AchievementTier.Bronze, c => c.Secret),
        new Definition("duet", "となりの台と同じ曲を遊んだ", "となりと同じ曲", Fun, AchievementTier.Bronze, c => c.NeighborFinishedSameSong(DuetSeconds)),
        new Definition("seisho", "となりと校歌を最後まで遊んだ", "校歌斉唱", Fun, AchievementTier.Bronze, c => c.SchoolDuet()),
        new Definition("hintkey", "ヒントから裏譜面を見つけた", "秘密の手がかり", Gimmick, AchievementTier.Bronze,
            c => c.OpenedSecretHere && c.HintsBeforeOpen >= HintMinShown),
        new Definition("honjitsu", "今日いちばんにこの曲を演奏した", "本日の初演", Fun, AchievementTier.Bronze, c => c.HonjitsuBatch5b()),
        new Definition("trend", "3人続けて同じ曲を遊んだ", "続く人気曲", Fun, AchievementTier.Bronze,
            c => c.PreviousSameSong(TrendPreviousPlays, TrendSeconds)),
        new Definition("sottoji", "裏の扉を開けて、そっと閉じた", "そっ閉じ", Fun, AchievementTier.Bronze, c => c.OpenedSecretHere && !c.Secret),
        new Definition("daynight", "となりと朝と夜を演奏した", "朝夜の共演", Fun, AchievementTier.Bronze, c => c.DaynightBatch5b()),
        new Definition("fushime", "みんなの節目の太刀を斬った", "節目の太刀", Fun, AchievementTier.Bronze, c => c.FushimeBatch5b()),
        new Definition("shushifu", "となりと同時に最後を斬った", "ふたりの終止符", Fun, AchievementTier.Bronze, c => c.ShushifuBatch5b()),
        new Definition(IchibanId, "その日の一番乗りで遊んだ", "一番乗り", Fun, AchievementTier.Bronze, c => c.ClearsToday == 0),
        new Definition("songswap", "二つの台で曲を交換した", "曲の交換会", Fun, AchievementTier.Bronze, c => c.SongswapBatch5b()),
        new Definition("omoteura", "となりと同じ曲を表と裏で遊んだ", "表裏一体", Fun, AchievementTier.Bronze, c => c.OmoteuraBatch5b()),
        new Definition("tachiai", "となりのキリ番に立ち会った", "キリ番の立会人", Fun, AchievementTier.Bronze, c => c.NeighborBatch5b()?.ach?.Contains("kiriban") == true),
        new Definition("lastpiece", "今日の全曲をそろえる1曲を遊んだ", "最後のピース", Fun, AchievementTier.Bronze, c => c.CompletesTodaysSongs()),
        new Definition("urafutari", "となりと裏譜面に挑んだ", "ふたりで裏へ", Fun, AchievementTier.Bronze,
            c => c.Secret && RushESecretState.IsSecretDifficulty(c.Neighbor(DuetSeconds)?.diff)),
        new Definition("clock", "14:23に2:23 AMを遊んだ", "2時23分", Fun, AchievementTier.Bronze,
            c => c.SongId == RushESecretState.TwoTwentyThreeLight.SongId && c.Spans(ClockHour, ClockMinute)),
        new Definition("secretall", "今日の裏譜面14種を締めくくった", "裏の十四夜", Fun, AchievementTier.Bronze,
            c => c.CompletesTodaysSecrets()),
        new Definition("soroiban", "ふたつの実績の番号がそろった", "そろい番号", Fun, AchievementTier.Silver, c => c.Batch5aMatchingOrdinals()),
        new Definition("medal3", "銅・銀・金の実績を1曲でそろえた", "三色メダル", Fun, AchievementTier.Silver, c => c.Batch5aThreeMedals()),
        new Definition("jihou", "時報の瞬間にノーツを斬った", "時報の剣", Fun, AchievementTier.Silver, c => c.NoteStats && c.Stats.Count(OnTheHour) >= 1),
        new Definition("kaikin", "ノーツを一つも素通りさせなかった", "皆勤賞", Skill, AchievementTier.Silver, c => c.NoteStats && c.Stats.Batch5aAttendance()),
        new Definition("late", "ずれた判定が全部遅めだった", "ためて斬る", Fun, AchievementTier.Silver, c => c.NoteStats && c.Stats.OffBeatsLean(1, LeanMinJudged, LeanMinOff)),
        new Definition("center10", "誤差10ms以内で50回斬った", "ど真ん中", Skill, AchievementTier.Silver,
            c => c.NoteStats && c.Stats.Count(n => n.Cut && n.ErrorMs.HasValue && Math.Abs(n.ErrorMs.Value) <= CenterMs) >= CenterCount),
        new Definition("early", "ずれた判定が全部早めだった", "前のめり", Fun, AchievementTier.Silver, c => c.NoteStats && c.Stats.OffBeatsLean(-1, LeanMinJudged, LeanMinOff)),
        new Definition("splus", "最高ランクS+をとった", "S+ランク", Skill, AchievementTier.Silver, c => c.Rank >= PlayRank.SPlus),
        // 1ミスで逃した人への称号(2026-10-29 ユーザー「1ミスでフルコンとかAP逃した人への称号も出しといて」)。D は対象外。
        new Definition("almostfc", "あと1つでフルコンボだった", "惜しいフルコン", Rare, AchievementTier.Silver,
            c => !c.AimMode && c.Hit > 0 && c.Bad + c.Miss == 1),
        new Definition("hardsplus", "HardでS+をとった", "剣の名手", Rare, AchievementTier.Silver, c => c.IsHard && c.Rank >= PlayRank.SPlus),
        new Definition("streak50", "PERFECTを50回続けた", "乱れぬ五十拍", Rare, AchievementTier.Silver, c => c.NoteStats && c.Stats.MaxPerfectStreak >= LongStreak),
        new Definition("goldperfect", "金のノーツを全部PERFECTで斬った", "純金", Skill, AchievementTier.Silver, c => c.NoteStats && c.Stats.AllPerfect(n => n.Gold, PureGoldMinNotes)),
        new Definition("yatsugi", "続く矢印を全部PERFECTにした", "矢継ぎ早", Skill, AchievementTier.Silver, c => c.NoteStats && c.Stats.Batch5aArrowRun()),
        new Definition("evenscore", "点数を偶数の数字だけで完成した", "偶数一色", Fun, AchievementTier.Silver, c => c.Batch5aEvenScore()),
        new Definition("memory", "Mのノイズの中を全部斬った", "記憶力", Gimmick, AchievementTier.Silver,
            c => c.Is(RushESecretState.MorningMemory) && c.NoteStats && c.Stats.AllCut(n => n.Veiled, 1)),
        new Definition("rhythm_ok", "当てたぶんは八割PERFECTだった", "リズムは完璧", Skill, AchievementTier.Silver, c => c.NoteStats && c.Stats.Batch5aRhythm()),
        new Definition("gobugobu", "PERFECTとGREATが同数だった", "五分五分", Fun, AchievementTier.Silver, c => c.NoteStats && c.Perfect >= EqualJudgmentMinCount && c.Perfect == c.Great),
        new Definition("slowstart", "出だしでつまずいてからSをとった", "遅咲き", Skill, AchievementTier.Silver,
            c => c.NoteStats && c.Rank >= SlowStartRequiredRank && c.Stats.OpeningFailures() >= SlowStartFailures),
        new Definition(KiribanId, "キリ番(100人ごと)を踏んだ", "キリ番", Fun, AchievementTier.Silver, c => c.ClearNumber > 0 && c.ClearNumber % KiribanEvery == 0),
        // ゾロ目・滑り込み・FULL COMBO・オールパーフェクトは D(照準の裏譜面)を外す(2026-10-29 段の見直し: D の点は 1000 点刻みで、
        // ゾロ目は必ず、滑り込みも当たりやすい。D の命中はどれも PERFECT なので、フルコンボとオールパーフェクトが同じ条件になる)。
        new Definition("zorome", "点の下3桁をゾロ目にした", "ゾロ目", Fun, AchievementTier.Silver,
            c => !c.AimMode && c.Score >= ZoromeMinScore && c.Score % ZoromeModulus % ZoromeStep == 0),
        new Definition("douten", "通算の表でだれかと同点になった", "運命の同点", Fun, AchievementTier.Gold, c => c.Batch5aSameScore()),
        new Definition("kaibun", "逆から読んでも同じ点を出した", "回文の点", Fun, AchievementTier.Gold, c => Palindrome(c.Score)),
        new Definition("chords", "同時のノーツを全部PERFECTにした", "和音の名手", Skill, AchievementTier.Gold, c => c.NoteStats && c.Stats.Batch5aAllChords()),
        new Definition("yushu", "最後の一太刀が一番正確だった", "有終の美", Skill, AchievementTier.Gold, c => c.NoteStats && c.Stats.Batch5aFinalBest()),
        new Definition("fullhouse", "点数でフルハウスを完成した", "数字の手札", Fun, AchievementTier.Gold, c => c.Batch5aFullHouse()),
        new Definition("hyakuren", "Gの百連ロングを切りきった", "百連の黄金", Skill, AchievementTier.Gold,
            c => c.Is(RushESecretState.ElDoradoG) && c.NoteStats
                && c.Stats.Count(n => n.Long && n.Cuts >= HundredCuts && n.Perfect) >= 1),
        new Definition("lastrainbow", "最後の一太刀で初めて虹を灯した", "虹の幕切れ", Skill, AchievementTier.Gold,
            c => c.NoteStats && c.Stats.HasLastRainbow()),
        new Definition("fc", "フルコンボを達成した", "FULL COMBO", Rare, AchievementTier.Gold, c => !c.AimMode && c.FullCombo),
        new Definition("ichien", "200円ぶん、ちょうど200回斬った", "一斬り一円", Fun, AchievementTier.Gold, c => c.CutCount == PricePerPlay),
        new Definition("slidein", "ランクに100点差以内で滑り込んだ", "滑り込み", Fun, AchievementTier.Gold,
            c => !c.AimMode && c.Score >= PlayRankHelper.ScoreLowerBound(PlayRank.B) && c.Score - PlayRankHelper.ScoreLowerBound(c.Rank) < SlideInMargin),
        new Definition("handperfect", "片方の色を全部PERFECTで斬った", "片翼の完璧", Skill, AchievementTier.Gold,
            c => c.NoteStats && (c.Stats.AllPerfect(n => n.Red && !n.Gold, HandPerfectMinNotes)
                || c.Stats.AllPerfect(n => n.Blue && !n.Gold, HandPerfectMinNotes))),
        new Definition("fubuki", "Fで降るノーツを全部斬った", "花吹雪", Gimmick, AchievementTier.Gold,
            c => c.Is(RushESecretState.EpilogueFall) && c.NoteStats && c.Stats.AllCut(n => n.Fall, 1)),
        new Definition("sanshu", "三種のノーツを全部PERFECTにした", "三種の神器", Skill, AchievementTier.Gold, c => c.NoteStats && c.Stats.AllPerfect(n => n.Gold, SacredKindMinNotes) && c.Stats.AllPerfect(n => n.Arrow, SacredKindMinNotes) && c.Stats.AllPerfect(n => n.Long, SacredKindMinNotes)),
        new Definition("kengou", "一刀・二刀・三刀流をそろえた", "剣豪", Skill, AchievementTier.Gold, c => c.Batch5aKengou()),
        new Definition("achsteps", "三つの実績で連番を完成した", "実績の階段", Fun, AchievementTier.Gold, c => c.Batch5aOrdinalSteps()),
        new Definition("neckneck", "となりの台と100点差以内で並んだ", "肩を並べる", Fun, AchievementTier.Gold, c => c.NeighborBatch5b() is RankingPlay p && p.score >= 0 && c.Score >= 0 && Math.Abs((long)c.Score - p.score) <= NeckneckScoreGap),
        new Definition("quiz", "Qの試験で全問正解した", "満点答案", Gimmick, AchievementTier.Gold,
            c => c.Is(RushESecretState.BenkyouQuiz) && c.NoteStats && c.Stats.QuizSuccessRate(1, 1)),
        new Definition("shingan", "Vの消えるノーツを全部斬った", "心眼", Gimmick, AchievementTier.Gold,
            c => c.Is(RushESecretState.ZeroVanish) && c.NoteStats && c.Stats.AllCut(n => n.Hide, 1)),
        // フルコンボで、PERFECT でないのがちょうど1つ(AP まであと1つ)。
        new Definition("almostap", "あと1つで全部PERFECTだった", "惜しいAP", Rare, AchievementTier.Gold,
            c => !c.AimMode && c.FullCombo && c.Perfect == c.Hit - 1),
        new Definition("fivekeys", "点数に一から五が勢ぞろいした", "数字の五人組", Fun, AchievementTier.Gold, c => c.Batch5aFiveKeys()),
        new Definition("hardfc", "Hardでフルコンボを決めた", "剣聖", Skill, AchievementTier.Gold,
            c => !c.Secret && !c.AimMode && c.IsHard && c.FullCombo),
        new Definition(SebangoId, "点に自分の番号を刻んだ", "背番号", Fun, AchievementTier.Gold,
            c => c.ClearNumber > 0 && c.Score >= SebangoMinScore && c.Score % SebangoModulus == c.ClearNumber % SebangoModulus),
        new Definition("steady", "ずれ幅を15ms以内に収めきった", "ぶれない剣", Skill, AchievementTier.Rainbow, c => c.NoteStats && c.Stats.Batch5aSteady()),
        new Definition("allgood", "全部GOODで斬りきった", "GOOD一色", Fun, AchievementTier.Rainbow, c => c.NoteStats && c.Stats.Batch5aAllGood()),
        new Definition("triplegold", "金の実績を3つ同時に取った", "金三つ", Fun, AchievementTier.Rainbow, c => c.Batch5aTripleGold()),
        new Definition("pairfc", "となりとそろってフルコンボを決めた", "ふたりで完全", Fun, AchievementTier.Rainbow, c => c.NoteStats && c.FullCombo && c.NeighborBatch5b()?.ach?.Contains("fc") == true),
        new Definition("ap", "全部PERFECTで斬りきった", "オールパーフェクト", Rare, AchievementTier.Rainbow, c => !c.AimMode && c.AllPerfect),
        new Definition("datescore", "今日の日付を点に刻んだ", "本日付の点", Fun, AchievementTier.Rainbow,
            c => c.Score >= DateScoreMinScore && c.Score % DateScoreModulus == 100 * c.StartedLocal.Month + c.StartedLocal.Day),
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

    static bool Palindrome(int score)
    {
        if (score < PalindromeMinScore || score > PalindromeMaxScore) return false;
        string digits = score.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (digits.Length != PalindromeDigits) return false;
        bool different = false;
        for (int i = 0; i < digits.Length; i++)
        {
            if (digits[i] != digits[digits.Length - 1 - i]) return false;
            different |= digits[i] != digits[0];
        }
        return different;
    }

    static int MorningNoteCount(string difficulty)
    {
        if (string.Equals(difficulty, EasyDifficulty, StringComparison.OrdinalIgnoreCase)) return MorningEasyNotes;
        if (string.Equals(difficulty, NormalDifficulty, StringComparison.OrdinalIgnoreCase)) return MorningNormalNotes;
        if (string.Equals(difficulty, HardDifficulty, StringComparison.OrdinalIgnoreCase)) return MorningHardNotes;
        return 0;
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

    // 表示の順(簡単なものから、clear が最初)。
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
