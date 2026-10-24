using System;
using System.Collections.Generic;
using UnityEngine;

// チュートリアル(タイトルの問いかけで「はい」)の進行表。
// 文・出す型・合格の条件だけを持つ純粋なデータで、EditMode テストから直接叩ける。
// 位置は判定調整と同じ x=±1.65、y は Easy 譜面の範囲(−0.65〜1.1)。青は画面の左、赤は右から出し、交差はさせない。
// Range は振る範囲を合わせる段(スマホの「範囲を覚える」を送り、返事が来るまで待つ)。
public enum TutorialStepKind { Explain, Practice, Ready, Range }

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
    public float TimeoutSeconds;                      // 自動送り秒。Practice の 0 は共通の制限時間
    public TutorialPatternNote[] Pattern = Array.Empty<TutorialPatternNote>();
    public double PeriodSeconds;                      // 型を繰り返す間隔(秒)
    public int Goal;                                  // 成功回数
    public bool NeedDirection;                        // 向きが合った切りだけを数える
    public CutDirection[] RequiredDirections = Array.Empty<CutDirection>();
    public bool Simultaneous;
    public double PatternLengthSeconds => Pattern.Length == 0 ? 0 : Pattern[Pattern.Length - 1].dt;
}

public static class TutorialProgram
{
    public const double HandsMinimumSeconds = 2.0;
    public const float Bpm = 120f;                    // 音源・ゲート発光・練習ノーツで共通の拍
    public const double BeatSeconds = 60.0 / Bpm;
    public const double BarSeconds = 4 * BeatSeconds;
    public const double LoopSeconds = 2 * BarSeconds;
    public const double ReadDelaySeconds = .9;        // 文を読む間
    public const double StepEndSeconds = 1.2;          // 「OK!」を見せる間
    public const double TimeoutEndSeconds = 1.0;      // 「次に進むよ」を見せる間
    public const float DefaultStepTimeoutSeconds = 15f;
    public const float HandsTimeoutSeconds = 6f;
    public const float ReadyTimeoutSeconds = 1.4f;
    public const float SwingSpeedToAdvance = 2.5f;    // 持ち方の説明を抜ける振りの速さ(タイトルの開始ノーツと同じ)
    // 本編は 2026-10-20 から、ロングを1回でも残すと残りが MISS でコンボが切れる。練習でも全部切ったら成功にする。
    public const float LongPracticeTimeoutSeconds = 30f;
    public const float RightX = 1.65f, LeftX = -1.65f, BaseY = .5f, LowY = -.4f;

    // 難易度を選ぶ前に挟まるので、Easy / Normal に共通の型。左右・斜めの矢印は曲の中で覚える。
    public static readonly string[] CommonFlow = { "hands", "tapRed", "tapBlue", "tapMix", "gold", "simul", "arrowEasy", "long", "ready" };

    // 振る範囲を合わせる段(2026-10-24 ユーザー指示「チュートリアルの最初に、剣をふる範囲の設定」)。
    // 台の準備(F8)の「範囲を覚える」と同じ指示(learn-area=10)をスマホへ送る。限界まででなく、振りやすい範囲で振ってもらう
    // (同日ユーザー「振れる限界までじゃなくて振りやすいとこを振る想定」)。覚えた枠は F8 と共通の倍率で広げて使う。
    // スマホの座標を受けているときだけ最初に入れる(マウスで遊ぶとき・テストでは入れない)。
    public const string RangeStepId = "range";
    public const double RangeAnswerSeconds = 3.0;      // 学習の開始が返ってこなければ、この秒数でそのまま進む
    public const double RangeResultWaitSeconds = 5.0;  // 学習の秒数のあと完了の返事を待つ秒数(PC の再送も学習秒数 +5 秒まで)
    public const double RangeCheerSeconds = 5.0;       // 残りがこの秒数になったら、そのまま続けてと声をかける
    public const double RangeEndSeconds = 1.6;         // 結果の文を見せる間
    public const string RangeCheer = "その調子！ そのまま振ってね";
    public const string RangeLearnedTitle = "OK! 振る範囲を合わせたよ";
    public const string RangeKeptTitle = "今の範囲のまま進むよ";

    public static string[] FlowFor(bool phoneInput)
    {
        if (!phoneInput) return CommonFlow;
        var flow = new string[CommonFlow.Length + 1];
        flow[0] = RangeStepId;
        Array.Copy(CommonFlow, 0, flow, 1, CommonFlow.Length);
        return flow;
    }

    // 学習中の残り秒(切り上げ)。0 以下は出さない。
    public static string RangeTimerText(int seconds) => seconds > 0 ? "あと " + seconds + " 秒" : "";

    static TutorialPatternNote Tap(float x, float y, string color, float dt) => new TutorialPatternNote { dt = dt, x = x, y = y, color = color, type = "tap" };
    static TutorialPatternNote Arrow(float x, float y, string color, string direction, float dt) => new TutorialPatternNote { dt = dt, x = x, y = y, color = color, type = "direction", direction = direction };
    static TutorialPatternNote Long(float x, float y, string color, int count, float lengthMs, float dt) => new TutorialPatternNote { dt = dt, x = x, y = y, color = color, type = "long", count = count, lengthMs = lengthMs };

    // 説明や成功の時刻が拍の途中でも、次の練習は音源の小節頭から始める。
    // 引数・戻り値は判定調整を除いた譜面時刻。調整分は NoteSpawner が一度だけ加える。
    public static double NextBarAtOrAfter(double chartTime) => Math.Ceiling(chartTime / BarSeconds) * BarSeconds;

    public static TutorialStep Get(string id)
    {
        switch (id)
        {
            case RangeStepId: return new TutorialStep { Id = id, Kind = TutorialStepKind.Range, Title = "両手の剣を、振りやすい範囲で上下左右に振ろう", Sub = "無理せず、いつも振るくらいの大きさで" };
            case "hands": return new TutorialStep { Id = id, Kind = TutorialStepKind.Explain, Title = "赤い剣は右手、青い剣は左手", Sub = "どちらかを振ると始まります", TimeoutSeconds = HandsTimeoutSeconds };
            case "tapRed": return new TutorialStep { Id = id, Kind = TutorialStepKind.Practice, Title = "赤い箱を右手で切ろう", Sub = "手前の枠に重なった瞬間に振る",
                Pattern = new[] { Tap(RightX, BaseY, "red", 0f), Tap(RightX, BaseY, "red", 2f) }, PeriodSeconds = LoopSeconds, Goal = 2 };
            case "tapBlue": return new TutorialStep { Id = id, Kind = TutorialStepKind.Practice, Title = "青い箱は左手で切ろう", Sub = "色と手を合わせる",
                Pattern = new[] { Tap(LeftX, BaseY, "blue", 0f), Tap(LeftX, BaseY, "blue", 2f) }, PeriodSeconds = LoopSeconds, Goal = 2 };
            case "tapMix": return new TutorialStep { Id = id, Kind = TutorialStepKind.Practice, Title = "来た色と同じ手で切ろう", Sub = "赤は右、青は左",
                Pattern = new[] { Tap(RightX, BaseY, "red", 0f), Tap(LeftX, BaseY, "blue", 1f), Tap(RightX, LowY, "red", 2f), Tap(LeftX, LowY, "blue", 3f) }, PeriodSeconds = LoopSeconds, Goal = 3 };
            case "arrowEasy": return new TutorialStep { Id = id, Kind = TutorialStepKind.Practice, Title = "矢印の向きに振ろう", Sub = "上向きなら下から上へ",
                Pattern = new[] { Arrow(RightX, BaseY, "red", "up", 0f), Arrow(LeftX, BaseY, "blue", "down", 2f) }, PeriodSeconds = LoopSeconds, Goal = 2, NeedDirection = true,
                RequiredDirections = new[] { CutDirection.Up, CutDirection.Down } };
            case "arrowNormal": return new TutorialStep { Id = id, Kind = TutorialStepKind.Practice, Title = "矢印の向きに振ろう", Sub = "上・下・左・右",
                Pattern = new[] { Arrow(RightX, BaseY, "red", "up", 0f), Arrow(LeftX, BaseY, "blue", "down", 2f), Arrow(RightX, BaseY, "red", "left", 4f), Arrow(LeftX, BaseY, "blue", "right", 6f) }, PeriodSeconds = 2 * LoopSeconds, Goal = 3, NeedDirection = true,
                RequiredDirections = new[] { CutDirection.Up, CutDirection.Down } };
            case "long": return new TutorialStep { Id = id, Kind = TutorialStepKind.Practice, Title = "長いノーツは、数字の回数だけ振ろう", Sub = "数字がなくなるまで切り切ろう",
                Pattern = new[] { Long(RightX, BaseY, "red", 2, 500f, 0f), Long(LeftX, BaseY, "blue", 3, 1000f, 3f), Long(RightX, BaseY, "red", 4, 1500f, 6f) },
                PeriodSeconds = 3 * LoopSeconds, Goal = 3, TimeoutSeconds = LongPracticeTimeoutSeconds };
            case "gold": return new TutorialStep { Id = id, Kind = TutorialStepKind.Practice, Title = "金色はどちらの手でもOK", Sub = "大事な一撃。好きな手で",
                Pattern = new[] { Tap(0f, BaseY, "gold", 0f) }, PeriodSeconds = LoopSeconds, Goal = 1 };
            case "simul": return new TutorialStep { Id = id, Kind = TutorialStepKind.Practice, Title = "赤と青がいっしょに来たら、両手で同時に", Sub = "青は左手、赤は右手",
                Pattern = new[] { Tap(LeftX, BaseY, "blue", 0f), Tap(RightX, BaseY, "red", 0f) }, PeriodSeconds = LoopSeconds, Goal = 2, Simultaneous = true };
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

    // 1 回の判定が「成功」に数えられるか。矢印の練習は向きが合った切りだけを数える
    // (2026-10-22 から向きの合わない振りではそもそも切れないので、切れた矢印はいつも向きが合っている)。
    // ロングは必要回数を切り切ったときだけ成功。割合による緩和はしない。
    public static bool CountsAsSuccess(TutorialStep step, JudgmentTier tier, bool correctDirection, int requiredCuts, int achievedCuts)
    {
        if (step == null || step.Kind != TutorialStepKind.Practice) return false;
        if (requiredCuts > 1)
        {
            return tier != JudgmentTier.Miss && achievedCuts >= requiredCuts;
        }
        if (tier == JudgmentTier.Miss) return false;
        return !step.NeedDirection || correctDirection;
    }

    internal static int RemainingDirections(TutorialStep step, ISet<CutDirection> completed)
    {
        int remaining = 0;
        if (step != null)
            foreach (var direction in step.RequiredDirections)
                if (!completed.Contains(direction)) remaining++;
        return remaining;
    }

    internal static bool PracticeGoalReached(TutorialStep step, int successes, ISet<CutDirection> completed)
        => step != null && step.Kind == TutorialStepKind.Practice && successes >= step.Goal
            && RemainingDirections(step, completed) == 0;

    internal static string RemainingText(TutorialStep step, int successes, int directions, bool done)
    {
        if (step == null || step.Kind != TutorialStepKind.Practice || step.Goal <= 1 || done) return "";
        if (directions > 0) return "あと " + directions + " 方向";
        int left = Mathf.Max(0, step.Goal - successes);
        return step.Id == "long" ? "あと " + left + " 種類"
            : step.Simultaneous ? "あと " + ((left + 1) / 2) + " 組" : "あと " + left + " 回";
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
        return (color == "blue" ? "青は左手" : "赤は右手") + "で、手前の枠に重なった瞬間に振ろう";
    }
}

// 同じ到着時刻の左右を一組として数える。別の回の片手同士や二重通知では合格にしない。
public sealed class TutorialPairProgress
{
    readonly Dictionary<long, int> handsByTime = new Dictionary<long, int>();
    public void Clear() => handsByTime.Clear();
    public bool Register(double hitTime, SaberHand hand)
    {
        if (double.IsNaN(hitTime) || double.IsInfinity(hitTime) || hand == SaberHand.Any) return false;
        long key = (long)Math.Round(hitTime * 1000.0);
        handsByTime.TryGetValue(key, out int before);
        int after = before | (hand == SaberHand.Left ? 1 : 2);
        handsByTime[key] = after;
        return before != 3 && after == 3;
    }
}

// 範囲を合わせる段の進み方。送った指示へのスマホの返事だけを見る(送信・受信・補正の適用には触れない)。
// 補正の適用は今までどおり InputPoint が完了の返事から行う(F8 の「範囲を覚える」と同じ)。
public sealed class TutorialRangeLearning
{
    public enum Phase { Waiting, Learning, Learned, Failed, NoAnswer }

    public Phase State { get; private set; }
    public int CommandId { get; }
    public double LearnSeconds { get; }
    readonly double begunAt;
    double learningSince = double.NaN;

    public TutorialRangeLearning(int commandId, double now, double learnSeconds = PhoneSaberDiscoveryResponder.LearnAreaSeconds)
    {
        CommandId = commandId; begunAt = now; LearnSeconds = learnSeconds;
    }

    public bool Finished => State == Phase.Learned || State == Phase.Failed || State == Phase.NoAnswer;

    // 学習の開始を受けるまでは学習の秒数そのまま。開始からはスマホと同じ秒数で減らす。
    public double SecondsLeft(double now) => State == Phase.Waiting ? LearnSeconds
        : State == Phase.Learning ? Math.Max(0, learningSince + LearnSeconds - now) : 0;

    public Phase Update(double now, IReadOnlyList<PhoneSaberAcknowledgement> acks, string station)
    {
        if (Finished) return State;
        string result = Result(acks, station);
        if (result == "learned") State = Phase.Learned;
        else if (result == "failed") State = Phase.Failed;
        else if (result == "learning" && State == Phase.Waiting) { State = Phase.Learning; learningSince = now; }
        if (State == Phase.Waiting && now - begunAt >= TutorialProgram.RangeAnswerSeconds) State = Phase.NoAnswer;
        else if (State == Phase.Learning && now - learningSince >= LearnSeconds + TutorialProgram.RangeResultWaitSeconds) State = Phase.Failed;
        return State;
    }

    // この PC の台の返事を優先し、無ければ台を決めていないスマホの返事を見る(InputPoint が補正に使う返事と同じ選び方)。
    // 台名のない PC では全部を見る。完了 > 失敗 > 学習中 の順。
    public static string Result(IReadOnlyList<PhoneSaberAcknowledgement> acks, string station)
    {
        if (acks == null) return "";
        bool byStation = !string.IsNullOrEmpty(station), same = false;
        if (byStation) foreach (var a in acks) same |= a != null && a.Station == station;
        bool learned = false, failed = false, learning = false;
        foreach (var a in acks)
        {
            if (a == null || byStation && a.Station != (same ? station : "none")) continue;
            learned |= a.Result == "learned"; failed |= a.Result == "failed"; learning |= a.Result == "learning";
        }
        return learned ? "learned" : failed ? "failed" : learning ? "learning" : "";
    }

    // 完了の返事に書かれた枠(カメラ画像の 0..1)。返事の選び方は InputPoint が補正に使うものと同じ
    // (同じ台を優先、無ければ台を決めていないスマホが1台だけのとき)。枠の無い返事(Android など)は false。
    public static bool TryLearnedBounds(IReadOnlyList<PhoneSaberAcknowledgement> acks, string station, out Rect bounds)
    {
        bounds = default;
        if (acks == null) return false;
        var same = new List<PhoneSaberAcknowledgement>();
        var none = new List<PhoneSaberAcknowledgement>();
        foreach (var a in acks)
        {
            if (a == null || a.Platform != "ios") continue;
            if (!string.IsNullOrEmpty(station) && a.Station == station) same.Add(a);
            else if (a.Station == "none") none.Add(a);
        }
        var candidates = same.Count > 0 ? same : none.Count == 1 ? none : null;
        if (candidates == null) return false;
        candidates.Sort((x, y) => string.CompareOrdinal(x.Sender, y.Sender));
        foreach (var a in candidates)
            if (a.Result == "learned" && a.TryLearnedBounds(out var learned))
            {
                bounds = Rect.MinMaxRect(learned.xMin, learned.yMin, learned.xMax, learned.yMax);
                return true;
            }
        return false;
    }
}

// チュートリアルで合わせた振る範囲は、その人の回だけ(2026-10-24 ユーザー「B にして」: タイトルに戻ったら台の準備の範囲に戻す)。
// 範囲の段の前に、この台の位置補正(台の準備・F8 で決めたもの)を PlayerPrefs の文字のまま覚えておき、タイトルに戻ったら元へ戻す。
// スマホの探す範囲も、戻した位置補正が画面いっぱいに写すカメラの範囲(＋認識の余白)へ送り直す。補正の無かった台は消す。
// 間にスタッフが台の準備をし直していたら(補正がチュートリアルの残したものと違う)、そちらを残す。覚え書きはアプリを閉じても残る。
// InputPoint と同じく台 A・B だけ。テスト(自動起動を止めている)では何もしない。
public static class TutorialRangeBaseline
{
    public const string PrefsKey = "Tutorial.RangeBaseline.v1";

    [Serializable]
    public sealed class Snapshot
    {
        public string station = "";
        public bool hadData, hadEnabled, enabled;
        public string data = "";         // 範囲の段の前の位置補正(PlayerPrefs の文字そのまま)
        public bool applied;             // 範囲を覚えて、チュートリアルの補正が入った
        public string appliedData = "";  // そのときの位置補正(これと違っていたらスタッフが合わせ直した)
    }

    static bool Station(string station) => station == "A" || station == "B";
    static bool Enabled => PhoneSaberP2PBridgeProcess.AutoStartEnabled;

    public static Snapshot Load()
    {
        if (!PlayerPrefs.HasKey(PrefsKey)) return null;
        try { var s = JsonUtility.FromJson<Snapshot>(PlayerPrefs.GetString(PrefsKey)); return s != null && Station(s.station) ? s : null; }
        catch (ArgumentException) { return null; }
    }
    static void Store(Snapshot s) { PlayerPrefs.SetString(PrefsKey, JsonUtility.ToJson(s)); PlayerPrefs.Save(); }
    public static void Forget() { PlayerPrefs.DeleteKey(PrefsKey); PlayerPrefs.Save(); }

    // 範囲の段の前。まだ戻していない覚え書きがあれば(前の人のあとタイトルを通らなかった)、古いほう=台の準備のほうを残す。
    public static void Remember(string station)
    {
        if (!Enabled || !Station(station) || Load() != null) return;
        string dataKey = PhoneSaberPositionCalibrationStore.DataKey(station), enabledKey = PhoneSaberPositionCalibrationStore.EnabledKey(station);
        Store(new Snapshot
        {
            station = station, hadData = PlayerPrefs.HasKey(dataKey), data = PlayerPrefs.GetString(dataKey, ""),
            hadEnabled = PlayerPrefs.HasKey(enabledKey), enabled = PlayerPrefs.GetInt(enabledKey, 0) == 1,
        });
    }

    // 範囲の段の終わり。そのときの補正を印にする(タイトルで、これと違っていたらスタッフが合わせ直したとみなす)。
    // 覚えなかったとき(返事なし・失敗)も覚え書きは残す: スマホが遅れて覚えると探す範囲だけ変わるので、タイトルで送り直す。
    // 遅れて届いた完了の返事で PC の補正が変わらないよう、この台の学習の返事は使い終わり扱いにする。
    public static void Finish(string station, bool learned)
    {
        var s = Load();
        if (s == null || s.station != station) return;
        if (!learned) PhoneSaberDiscoveryResponder.Acknowledgements.DiscardLearnedAreas(station);
        s.applied = true; s.appliedData = PlayerPrefs.GetString(PhoneSaberPositionCalibrationStore.DataKey(station), "");
        Store(s);
    }

    // 戻すか(純関数)。チュートリアルの補正が入ったあと、別の補正に変わっていたらスタッフが合わせ直したので戻さない。
    public static bool ShouldRestore(Snapshot s, string currentData) => s != null && (!s.applied || s.appliedData == currentData);

    // タイトルに戻ったとき(TitleMenuController)。戻したら true。
    public static bool RestoreAtTitle()
    {
        var s = Load();
        if (s == null) return false;
        if (!Enabled) return false;
        var input = InputPoint.EnsureInstance();
        if (input == null || input.StationLabel != s.station) return false;
        string dataKey = PhoneSaberPositionCalibrationStore.DataKey(s.station), enabledKey = PhoneSaberPositionCalibrationStore.EnabledKey(s.station);
        bool restore = ShouldRestore(s, PlayerPrefs.GetString(dataKey, ""));
        Forget();
        if (!restore) { PhoneSaberEventLog.Record(s.station, "SYSTEM", "tutorial-range-kept", "staff-changed"); return false; }
        var calibration = s.hadData ? DecodeCalibration(s.data) : null;
        if (calibration != null)
        {
            // 補正を作り直してから(学習の返事は使い終わり扱いになる)、PlayerPrefs を前の文字そのままに戻す。
            var corners = calibration.CopyCorners();
            input.SavePositionCalibration(corners, out _);
            input.SetPositionCalibrationEnabled(s.enabled);
            PlayerPrefs.SetString(dataKey, s.data);
            if (!s.hadEnabled) PlayerPrefs.DeleteKey(enabledKey);
            PlayerPrefs.Save();
            SendPhoneArea(PhoneAreaFor(corners, input.camWidth, input.camHeight));
        }
        else
        {
            input.ResetPositionCalibration();
            PhoneSaberDiscoveryResponder.QueueCommand(PhoneSaberDiscoveryResponder.ClearAreaCommand, automatic: true);
        }
        PhoneSaberEventLog.Record(s.station, "SYSTEM", "tutorial-range-restored", calibration != null ? "baseline" : "none");
        return true;
    }

    // PlayerPrefs の文字から位置補正を読む(PhoneSaberPositionCalibrationStore と同じ形)。読めなければ null。
    [Serializable] sealed class StoredCalibration { public int version; public Vector2[] corners; }
    static PhoneSaberPositionCalibration DecodeCalibration(string data)
    {
        try
        {
            var stored = JsonUtility.FromJson<StoredCalibration>(data);
            return stored != null && stored.version == 1 && PhoneSaberPositionCalibration.TryCreate(stored.corners, out var c, out _) ? c : null;
        }
        catch (ArgumentException) { return null; }
    }

    // スマホの探す範囲(自動の指示。スタッフ向けのトーストは出さない)。
    public static void SendPhoneArea(Rect area)
    {
        if (!Enabled || !PhoneSaberCalibrationPlayArea.Valid(area)) return;
        PhoneSaberDiscoveryResponder.QueueCommand(PhoneSaberCalibrationPlayArea.Command(area), automatic: true);
    }

    // 位置補正が画面いっぱいに写すカメラの範囲(4隅の外接矩形、カメラ画像の 0..1)に、認識の余白を足し、最小幅をそろえて 0..1 に収める。純関数。
    public static Rect PhoneAreaFor(Vector2[] corners, float cameraWidth, float cameraHeight)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var corner in corners)
        {
            float x = corner.x / (cameraWidth - 1), y = corner.y / (cameraHeight - 1);
            minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x); minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
        }
        Fit(ref minX, ref maxX); Fit(ref minY, ref maxY);
        return Rect.MinMaxRect(minX, minY, maxX, maxY);
    }

    static void Fit(ref float low, ref float high)
    {
        low -= PhoneSaberCalibrationPlayArea.Margin; high += PhoneSaberCalibrationPlayArea.Margin;
        if (high - low < PhoneSaberCalibrationPlayArea.MinimumSize)
        {
            float center = (low + high) * .5f;
            low = center - PhoneSaberCalibrationPlayArea.MinimumSize * .5f; high = center + PhoneSaberCalibrationPlayArea.MinimumSize * .5f;
        }
        if (low < 0) { high -= low; low = 0; }
        if (high > 1) { low -= high - 1; high = 1; }
        low = Mathf.Max(0, low); high = Mathf.Min(1, high);
    }
}
