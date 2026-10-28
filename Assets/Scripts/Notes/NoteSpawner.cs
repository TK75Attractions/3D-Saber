using PoolKey = System.ValueTuple<UnityEngine.GameObject, CutDirection, int, SaberHand, bool, bool, System.ValueTuple<bool, bool, bool, bool>>;
using System.Collections.Generic;
using UnityEngine;

// chart.json のノーツを approachTime だけ先読みで生成し、
// spawnZ → judgeZ に向けて Z 軸で流す。判定ウィンドウも更新する。
public partial class NoteSpawner : MonoBehaviour
{
    public GameObject notePrefab;       // デフォルト（color が red/blue のどちらでもない場合）
    public GameObject notePrefabRed;    // color="red"
    public GameObject notePrefabBlue;   // color="blue"
    public Transform noteRoot;
    public float approachTime = 1.0f;
    public float spawnZ = 20f;
    public float judgeZ = 0f;
    // 遅めの判定ウィンドウ（Bad 上限）= タップ用の lateWindow ベース。
    // 本編では GamePlayManager が JudgmentTierHelper.LateBadSeconds で上書き同期する(ここは既定値)。
    public float judgeWindow = (float)JudgmentTierHelper.LateBadSeconds;
    // 早め側の判定ウィンドウ（早く切り過ぎ防止）。同じく EarlyBadSeconds と同期される。
    public float earlyJudgeWindow = (float)JudgmentTierHelper.EarlyBadSeconds;
    // lateWindow を過ぎたら自動的に miss 扱いにする猶予秒数。
    public float missGrace = 0.06f;
    // 刃がノーツの上を通っている最中(HoldMiss が true)なら、Miss の確定をさらにこの秒数まで待つ。
    // その間に刃が通り過ぎれば切れる(採点は刃がいちばん近づいた時刻)。新規フィールドなのでシーンの値は無くコード既定値が効く。
    public float passHoldSeconds = 0.15f;
    // miss 扱いになった後、ノーツを画面後方まで流してから片付けるまでの秒数。
    public float despawnAfterMissSeconds = 1.5f;
    // ロングノーツの追加カット1回あたりの判定ウィンドウ延長秒。
    // 4 回切りなら lateWindow = judgeWindow + 3 * secondsPerLongCut ≒ 2.3s。
    // 50 回切りなら ≒ 34.5s（25秒の「サビ前ロング」も余裕で表現可能）。
    public float secondsPerLongCut = DefaultSecondsPerLongCut;
    // 照明の合図など、ノーツを出す前に長さを知りたい所が使う既定値(本編のシーンも 0.7)。
    public const float DefaultSecondsPerLongCut = 0.7f;
    // ロングノーツが HitTime 経過後、判定面の少し後方に滞留する距離（プレイヤーが切り続けられるように）。
    public float longLingerDriftZ = 1.0f;
    // ロングノーツの見た目の Z スケール上限（count=50 等でも視野を埋め尽くさないようにキャップ）。
    public float longMaxVisualZScale = 6f;
    // 着地ゴースト(判定面の固定枠+収縮枠。NoteTimingCue)を各ノーツに付ける。
    public bool buildTimingCues = true;
    // 割れた破片にノーツの前進の勢いを残して手前へ流す(爽快感カタログ 手5)。新規フィールドなのでシーンの値は無くコード既定値が効く。
    public bool flowDebris = true;

    private ChartData chart;
    private ChartTempoMap windTempoMap;
    private double windBeat;
    public bool HasWindNotes { get; private set; }
    // 緩急(logic!!! の「S」)の流れる速さの表。譜面に scroll が無ければ null(いつもの一定の速さ)。
    ScrollSpeedMap scrollMap;
    public bool HasScroll => scrollMap != null;
    // 消える(Zero の「V」)ノーツがある譜面か。カウントインの一言に使う。
    public bool HasVanishNotes { get; private set; }
    private int nextIndex;
    private double totalOffsetSeconds; // chart.offsetMs/1000 + extraOffsetSeconds
    private double extraOffsetSeconds; // GamePlayManager から実行時に上書き
    private readonly List<CuttableNote> liveNotes = new List<CuttableNote>();
    private GameplayCutFeedback cutFeedback;
    [System.NonSerialized] bool memoryVeilMode;
    // 自動プロパティにせず、生成済みの演出にもその場で反映する。
    public bool MemoryVeilMode
    {
        get => memoryVeilMode;
        set
        {
            if (memoryVeilMode == value) return;
            memoryVeilMode = value;
            if (cutFeedback != null) cutFeedback.SetMemoryVeilMode(LiftsCutFeedback);
        }
    }
    // Q の試験用紙(QuizMode)の上へ、切った光を描く。M のほかの決まり(DopamineFlourish.Veiled など)は持ち込まない。
    [System.NonSerialized] bool cutFeedbackAbovePaper;
    public bool CutFeedbackAbovePaper
    {
        get => cutFeedbackAbovePaper;
        set
        {
            if (cutFeedbackAbovePaper == value) return;
            cutFeedbackAbovePaper = value;
            if (cutFeedback != null) cutFeedback.SetMemoryVeilMode(LiftsCutFeedback);
        }
    }
    // 切った光を幕(M のノイズ・Q の試験用紙)より上に描くか。
    public bool LiftsCutFeedback => memoryVeilMode || cutFeedbackAbovePaper;
    // 切った光の大きさの倍率(Q の試験用紙の間だけ QuizMode が下げる。ほかは 1)。
    public float CutFeedbackSizeScale { get; set; } = 1f;
    public FloorTimingGuide FloorGuide { get; private set; }
    public void ConfigureFloorGuide(float floorY)
    {
        if (FloorGuide != null) SafeDestroy(FloorGuide.gameObject);
        FloorGuide = FloorTimingGuide.Create(transform, floorY);
    }
    public bool reuseNotes = true;
    const int MaxIdleNotes = 128;
    readonly Dictionary<PoolKey, Stack<CuttableNote>> idleNotes = new Dictionary<PoolKey, Stack<CuttableNote>>();
    readonly Dictionary<CuttableNote, PoolKey> poolKeys = new Dictionary<CuttableNote, PoolKey>();
    Transform idleRoot;
    NoteFragmentPool fragmentPool;
    int idleCount;
    public int PooledNoteCount => idleCount;
    public int CreatedNoteCount { get; private set; }
    public NoteFragmentPool FragmentPool => fragmentPool;
    bool Pooling => reuseNotes && Application.isPlaying;

    // revealは見た目の構造を変えない。通常ノーツと同じキーで借り、出現ごとに色と矢印を戻す。
    // R の鏡のノーツ(UsesReverse)と Q の発射点のノーツ(UsesLaunch)は見た目の子を持つので、別のキーにしてほかのノーツへ持ち越さない。
    PoolKey Key(NoteData data) => (PickPrefab(data.color), CutDirectionHelper.Parse(data.direction),
        Mathf.Clamp(data.count,1,8), SaberHandHelper.FromColor(data.color),
        string.Equals(data.color,"gold",System.StringComparison.OrdinalIgnoreCase), DisplaySettings.ProjectorMode,
        (buildTimingCues, UsesFall(data), UsesReverse(data), UsesLaunch(data)));
    void EnsurePool()
    {
        if(idleRoot == null) {
            var root = new GameObject("IdleNotes"); root.transform.SetParent(transform,false); root.SetActive(false); idleRoot=root.transform;
            var fragments = new GameObject("NoteFragments");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(fragments,gameObject.scene);
            fragmentPool=fragments.AddComponent<NoteFragmentPool>();
        }
    }
    void Recycle(CuttableNote note)
    {
        if(note == null) return;
        if(!note.IsPooled || !poolKeys.TryGetValue(note,out var key)) { SafeDestroy(note.gameObject); return; }
        note.Retire();
        if(idleCount >= MaxIdleNotes) { poolKeys.Remove(note); SafeDestroy(note.gameObject); return; }
        note.transform.SetParent(idleRoot,false);
        if(!idleNotes.TryGetValue(key,out var stack)) idleNotes[key]=stack=new Stack<CuttableNote>();
        stack.Push(note); idleCount++;
    }
    // 各見た目の同時表示数をロード中に見積もる。保持は全体128個まで。
    void Prewarm()
    {
        // 停止・シーン終了時の空譜面では、破棄中のプールへ触れたり新しく確保したりしない。
        if(!Pooling || chart?.notes == null || chart.notes.Count == 0) return;
        EnsurePool();
        var groups=new Dictionary<PoolKey, (NoteData sample, List<(double time,int delta)> events)>();
        foreach(var data in chart.notes) {
            var key=Key(data); if(key.Item1 == null) continue;
            if(!groups.TryGetValue(key,out var group)) group=(data,new List<(double,int)>());
            double linger=data.LingerSeconds(secondsPerLongCut);
            group.events.Add((SpawnTimeFor(data),1));
            group.events.Add((EffectiveTime(data)+judgeWindow+linger+missGrace+despawnAfterMissSeconds+.05,-1));
            groups[key]=group;
        }
        // 前の曲にしかない種類で容量が埋まり、次の曲だけ再利用できなくなるのを防ぐ。
        foreach(var key in new List<PoolKey>(idleNotes.Keys)) {
            if(groups.ContainsKey(key)) continue;
            var old=idleNotes[key];
            while(old.Count>0) {
                var note=old.Pop(); idleCount--;
                if(note != null) { poolKeys.Remove(note); SafeDestroy(note.gameObject); }
            }
            idleNotes.Remove(key);
        }
        foreach(var pair in groups) {
            var events=pair.Value.events;
            events.Sort((a,b)=> { int c=a.time.CompareTo(b.time); return c!=0?c:a.delta.CompareTo(b.delta); });
            int live=0,peak=0; foreach(var e in events) { live+=e.delta; peak=Mathf.Max(peak,live); }
            int existing=idleNotes.TryGetValue(pair.Key,out var stack)?stack.Count:0;
            for(int i=existing;i<Mathf.Min(12,peak+1) && idleCount<MaxIdleNotes;i++) SpawnOne(pair.Value.sample,true);
        }
        Material material=null;
        foreach(var note in poolKeys.Keys) {
            if(note == null) continue;
            var renderer=note.BodyRenderer;
            if(renderer != null) material=renderer.sharedMaterial;
            if(material != null) break;
        }
        fragmentPool.Prewarm(64,material);
    }

    public float Speed => approachTime > 0.0001f ? (spawnZ - judgeZ) / approachTime : 0f;

    public int AliveCount => liveNotes.Count;
    public IReadOnlyList<CuttableNote> LiveNotes => liveNotes;
    // ゲーム判定側だけが設定する、受信待ち中のMiss確定保留。
    public System.Func<CuttableNote, double, bool> ShouldDeferMiss { get; set; }
    // セーバー判定が「刃がこのノーツの上を通っている最中」と答えたら、passHoldSeconds まで Miss を待つ(GamePlayManager が設定)。
    public System.Func<CuttableNote, bool> HoldMiss { get; set; }
    // 停電(L)の見た目だけの倍率。位置更新のあと、合図を描く前に読む。未設定は通常の明るさ。
    public System.Func<CuttableNote, float> BrightnessFor { get; set; }
    // 判定の時計の遅れ(秒)。カメラの入力の遅れを、受付窓と Miss の確定だけで差し引く。
    // ノーツの位置・着地枠などの見た目は曲の時計のまま。GamePlayManager が毎フレーム設定する(既定 0)。
    public double JudgeLatencySeconds { get; set; }
    // 判定に使う曲時計(曲の時計から遅れを引いた時刻)。
    public double JudgeClock(double songTime) => songTime - JudgeLatencySeconds;
    public int NextIndex => nextIndex;
    // 譜面の総ノーツ数(曲の終わりの確認用)。譜面未設定なら 0。
    public int TotalNoteCount => chart != null && chart.notes != null ? chart.notes.Count : 0;
    // 今流している譜面(音源の長さで切り詰めた後)。ScoreManager が満点の配分に使う。
    public ChartData CurrentChart => chart;

    public event System.Action<CuttableNote> OnNoteSpawned;
    public event System.Action<CuttableNote> OnNoteMissed;
    public event System.Action OnChartReset;

    // 同時押しノーツの間の白い連結線(プロセカの同時線)。標準機能として常時有効。
    // シーンに旧 NoteSpawner がシリアライズ済みでもコード既定値(true)が使われるよう NonSerialized。
    [System.NonSerialized] public bool simultaneousGuideEnabled = true;
    // 青(左手)ノーツの面の外側に暗い縁を付ける。背景が明るいステージだけ GamePlayManager が立てる(StageThemeCatalog.OutlinesLeftNotes)。
    [System.NonSerialized] public bool outlineLeftNotes;
    // 「同時」とみなす時刻差(秒)。譜面上同じ拍のノーツは同一値になるので余裕を持った小さい値でよい。
    public const double SimultaneousEpsilonSeconds = 0.01;

    public void SetChart(ChartData data)
    {
        OnChartReset?.Invoke();
        if (cutFeedback != null) cutFeedback.ResetState();
        if (FloorGuide != null) FloorGuide.Clear();
        if (fallPetals != null) fallPetals.Clear();
        chart = data;
        HasWindNotes = false;
        HasVanishNotes = false;
        if (chart?.notes != null)
            foreach (var note in chart.notes)
            {
                if (note == null) continue;
                // F と W は描画の親を共有できないため、プールの準備より前に風を無効にする。
                if (HasFall) note.windX = note.windY = note.windSpin = note.windSway = 0f;
                if (note.HasWind) HasWindNotes = true;
                if (VanishRules.Parse(note.vanish) != VanishMode.None) HasVanishNotes = true;
            }
        // 風の無い譜面では拍の計算用オブジェクトも確保しない。
        windTempoMap = HasWindNotes
            ? new ChartTempoMap(chart.bpm, chart.beatZeroMs, chart.editorTempoMap)
            : null;
        windBeat = 0;
        // F は実時間で等速落下する。出現時刻も通常と同じ approach 秒前。
        scrollMap = HasFall ? null : ScrollSpeedMap.Build(chart?.scroll);
        if (cutFeedback != null) cutFeedback.ReserveProtection(TotalNoteCount);
        nextIndex = 0;
        RecomputeTotalOffset();
        foreach (var n in liveNotes)
        {
            if (n != null) Recycle(n);
        }
        liveNotes.Clear();
        ResetOvertakeSchedule();
        Prewarm();
        // Q の飾りの金(NoteSpawner.Decoy)。飛んでいる飾りを片付け、新しい譜面の飾りを並べ直す。
        ResetDecoys();
    }

    public void SetExtraOffsetSeconds(double seconds)
    {
        extraOffsetSeconds = seconds;
        RecomputeTotalOffset();
    }

    public double TotalOffsetSeconds => totalOffsetSeconds;

    private void RecomputeTotalOffset()
    {
        double chartOffset = chart != null ? chart.offsetMs / 1000.0 : 0.0;
        totalOffsetSeconds = chartOffset + extraOffsetSeconds;
    }

    // 譜面ノートの「実効時刻（秒）」。chart.offsetMs と extraOffsetSeconds を加算済み。
    public double EffectiveTime(NoteData nd)
    {
        return nd.TimeSeconds + totalOffsetSeconds;
    }

    // 届く時刻 hitTime(曲の時計、ずらし込み)まで、今 songTime から見た見かけの残り時間(秒)。
    // 緩急の無い譜面では hitTime − songTime そのもの。ノーツの奥行きと出す時刻だけに使い、判定は実際の時刻のまま。
    public double VisualRemaining(double hitTime, double songTime)
    {
        if (scrollMap == null) return hitTime - songTime;
        return scrollMap.PositionAt(hitTime - totalOffsetSeconds) - scrollMap.PositionAt(songTime - totalOffsetSeconds);
    }

    // 奥行きに使う残り時間。見かけの残り時間を、その時刻の飛び方の曲がり具合で曲げたもの(緩急の無い譜面では hitTime − songTime)。
    public double ShapedRemaining(double hitTime, double songTime)
    {
        double seen = VisualRemaining(hitTime, songTime);
        return scrollMap == null ? seen : scrollMap.Shape(seen, approachTime, songTime - totalOffsetSeconds);
    }

    // 背景の可動部を動かす時計。緩急の譜面では、ノーツと同じ速さで進む(止まる間は背景も止まり、速い所は速く流れる)。
    // ほかの譜面では songTime そのもの。
    public double FlowClock(double songTime)
    {
        if (scrollMap == null) return songTime;
        return scrollMap.FlowTime(songTime - totalOffsetSeconds) + totalOffsetSeconds;
    }

    // 届く時刻 hitTime のノーツが奥に現れる曲の時刻(緩急の無い譜面では approachTime 前)。
    public double SpawnTimeFor(double hitTime)
    {
        if (scrollMap == null) return hitTime - approachTime;
        double position = scrollMap.PositionAt(hitTime - totalOffsetSeconds) - approachTime;
        return scrollMap.TimeAtPosition(position) + totalOffsetSeconds;
    }

    private static void SafeDestroy(GameObject go)
    {
        if (go == null) return;
        if (Application.isPlaying) Destroy(go);
        else DestroyImmediate(go);
    }

    // 毎フレーム呼ぶ想定。songTime は SongPlayer.SongTime（秒）を渡す。
    public void Tick(double songTime)
    {
        if (HasWindNotes)
            windBeat = windTempoMap.BeatAt((songTime - TotalOffsetSeconds) * 1000.0);
        SpawnDue(songTime);
        UpdateLive(songTime);
        // Q の飾りの金。本物と同じ時計で出して動かす(liveNotes には入れない)。
        TickDecoys(songTime);
        TickFallPetals(songTime);
        if (FloorGuide != null) FloorGuide.Tick(this, songTime);
        if (cutFeedback != null)
        {
            cutFeedback.SetProtectionTime(songTime);
            cutFeedback.Tick(Time.deltaTime);
        }
    }

    // セーバー判定の前に受付窓を今の曲時計へ合わせる(判定の時計で見る)。
    // 生成・Z移動・Miss通知は従来どおり判定後のTickで行う。
    public void RefreshJudgmentWindows(double songTime)
    {
        if (HasWindNotes)
            windBeat = windTempoMap.BeatAt((songTime - TotalOffsetSeconds) * 1000.0);
        foreach (CuttableNote note in liveNotes)
        {
            if (note == null) continue;
            // 処理落ちで静止区間へ飛び込んだコマも、判定前に着地済みのXYへ戻す。
            if (note.HasWind && songTime >= note.HitTime - WindRules.CalmSeconds)
            {
                Vector3 p = note.transform.position;
                note.transform.position = new Vector3(note.LandingPosition.x, note.LandingPosition.y, p.z);
                ApplyWindPose(note, 0);
            }
            // 判定前の飛び越しでも、揺れの位相と静止境界は今の曲時計へ進める。Zの更新順は保つ。
            if (note.HasWind) ApplyWindSway(note, songTime, note.transform.position.z);
            if (note.IsFall) UpdateFallNote(note, note.HitTime - songTime);
            UpdateSlideNote(note, songTime);
            // コマ飛びでも、刃の判定より先に共通経路へ戻す。
            UpdateOvertakeNote(note, songTime);
            // R の鏡の見た目も今の曲時計へ(根元は動かさない)。
            UpdateReverseNote(note, songTime);
            UpdateJudgmentWindow(note, note.HitTime - JudgeClock(songTime), LateWindowFor(note));
        }
    }

    void UpdateJudgmentWindow(CuttableNote note, double dt, float lateWindow)
    {
        note.IsJudgeable = !note.IsCut && !note.IsMissed && !note.IsFinalized
            && dt <= earlyJudgeWindow && dt >= -lateWindow;
    }

    void OnDisable()
    {
        if (cutFeedback != null) cutFeedback.ClearEffects();
        if (FloorGuide != null) FloorGuide.Clear();
        if (fallPetals != null) fallPetals.Clear();
    }

    void OnDestroy()
    {
        // Spawnerコンポーネントだけが取り外された場合も、表示用の所有資源を残さない。
        if (cutFeedback != null) SafeDestroy(cutFeedback.gameObject);
        cutFeedback = null;
        if (FloorGuide != null) SafeDestroy(FloorGuide.gameObject);
        if (fallPetals != null) SafeDestroy(fallPetals.gameObject);
        // 非プールノーツや外部noteRootも、このSpawnerの破棄で確実に片付ける。
        foreach (var note in liveNotes)
            if (note != null && !poolKeys.ContainsKey(note)) SafeDestroy(note.gameObject);
        liveNotes.Clear();
        DestroyDecoys();
        foreach(var note in poolKeys.Keys) if(note != null) SafeDestroy(note.gameObject);
        poolKeys.Clear(); idleNotes.Clear(); idleCount=0;
        if(idleRoot != null) SafeDestroy(idleRoot.gameObject);
        if(fragmentPool != null) SafeDestroy(fragmentPool.gameObject);
    }

    private void SpawnDue(double songTime)
    {
        if (chart == null) return;
        if (SpawnOvertakeDue(songTime)) return;
        while (nextIndex < chart.notes.Count)
        {
            NoteData nd = chart.notes[nextIndex];
            double eff = EffectiveTime(nd);
            // 緩急の譜面でも見かけの残りは届く時刻の順に並ぶ(速さが 0 以上)ので、最初に遠すぎたノーツで止めてよい。
            if (VisualRemaining(eff, songTime) > approachTime) break;
            SpawnOne(nd, songTime: songTime);
            nextIndex++;
        }
    }

    private void SpawnOne(NoteData nd, bool warming = false, double songTime = 0)
    {
        GameObject prefab = PickPrefab(nd.color);
        if (prefab == null) return;
        float scale = chart != null ? chart.coordScale : 1f;
        Vector2 landing = new Vector2(nd.x * scale, nd.y * scale);
        Vector3 pos = new Vector3(landing.x, landing.y, spawnZ);
        // 有効化・着地枠の構築・出現通知の前に、最初のコマのずれを適用する。
        if (nd.HasWind && !warming && songTime < EffectiveTime(nd) - WindRules.CalmSeconds)
        {
            double remaining = EffectiveTime(nd) - songTime;
            // このコマで UpdateLive が置く奥行きと同じ倍率にする(遅れて出たノーツも同じ見た目のずれ)。
            float depth = Mathf.Min(spawnZ, judgeZ + Speed * (float)remaining);
            Vector2 offset = WindRules.WorldOffset(nd.windX, nd.windY, nd.windSpin, 0f,
                remaining, approachTime, windBeat, depth);
            pos.x += offset.x;
            pos.y += offset.y;
        }
        PoolKey key=Key(nd);
        CuttableNote cached=null;
        if(Pooling) {
            EnsurePool();
            if(!warming && idleNotes.TryGetValue(key,out var stack))
                while(stack.Count>0 && cached==null) { cached=stack.Pop(); idleCount--; }
        }
        bool reused=cached != null;
        GameObject go;
        if(reused) go=cached.gameObject;
        else {
            go=Instantiate(prefab, pos, Quaternion.identity, Pooling?idleRoot:noteRoot); CreatedNoteCount++;
            // 設定済みVisualsを含むPrefabでも、種別を設定する前にAwakeを走らせない。
            if(Pooling) go.SetActive(false);
        }
        go.transform.SetParent(noteRoot,false); go.transform.position=pos; go.transform.rotation=Quaternion.identity;
        go.transform.localScale=prefab.transform.localScale;
        // プロジェクターモード: 正面サイズを一回り大きく(x/y のみ。ロングの z 伸長は下で別途扱う)
        if (DisplaySettings.ProjectorMode)
        {
            Vector3 s0 = go.transform.localScale;
            go.transform.localScale = new Vector3(s0.x * ProjectorMode.NoteScale, s0.y * ProjectorMode.NoteScale, s0.z);
        }
        CuttableNote note = go.GetComponent<CuttableNote>();
        if (note == null)
        {
            note = go.AddComponent<CuttableNote>();
        }
        note.ResetForSpawn(); note.IsPooled=Pooling; note.FragmentPool=Pooling?fragmentPool:null;
        note.ConfigureWind(nd, landing);
        note.ConfigureOvertake(nd, UsesOvertake(nd), overtakeApproach);
        // 消える(V)は出た瞬間の残り時間から見え方を決める(sudden は見えない状態で出る)。合図の見えにくさは矢印・枠・数字より先に決める。
        note.Vanish = VanishRules.Parse(nd.vanish);
        double vanishRemaining = warming ? approachTime : EffectiveTime(nd) - songTime;
        note.Veil = note.Vanish != VanishMode.None ? VanishRules.CueVeil(note.Vanish, vanishRemaining) : NoteVeil.Clamp(nd.veil);
        // Q の見えないノーツ(QuizRules)。合図ごと隠し(Veil 1)、消える(V)の見え方は使わない。判定・採点はふつうのノーツと同じ。
        note.IsHidden = nd.hidden;
        note.HitScale = QuizRules.HitScale(nd.hitScale);
        // Q の隠した向き(NoteSpawner.Quiz)。判定は本当の向きのまま、本体には左右の両矢印、床の印は中立の横線。
        note.SideArrowHidden = QuizRules.HidesSide(nd);
        if (note.IsHidden)
        {
            note.Vanish = VanishMode.None;
            note.Veil = 1f;
        }
        if(Pooling && !reused) poolKeys.Add(note,key);
        note.HitTime = EffectiveTime(nd);
        note.IsHeat = nd.heat;
        note.JudgmentOwner = this;
        note.RequiredDirection = CutDirectionHelper.Parse(nd.direction);
        note.RequiredCutCount = Mathf.Max(1, nd.count);
        note.RemainingCuts = note.RequiredCutCount;
        // 長さの直接指定（lengthMs）。未指定(0)なら従来の回数×secondsPerLongCut。
        note.OverrideLingerSeconds = nd.lengthMs > 0f && note.RequiredCutCount > 1 ? nd.lengthMs / 1000f : 0f;
        note.ConfigureSlide(nd, scale);
        UpdateSlideNote(note, warming ? note.HitTime - approachTime : songTime);
        // 金ノーツ判定（chart.json の color:"gold"）
        note.IsGold = !string.IsNullOrEmpty(nd.color) && nd.color.ToLowerInvariant() == "gold";
        // 担当ハンド（blue=左手 / red=右手 / gold・無色=どちらでも）。ロングにも同じルールを適用。
        note.RequiredHand = SaberHandHelper.FromColor(nd.color);
        // 流れる破片: タップはノーツと同じ向き・速さで流れている(spawnZ → judgeZ)。ロングは判定面に留まるので勢いを残さない。
        note.FlowDebris = flowDebris;
        note.FlowNoteVelocity = note.RequiredCutCount > 1 ? Vector3.zero : new Vector3(0f, 0f, -Speed);

        // 旧プレハブにある面ステッカー等を剥がして、クリスタル＋ネオン外観に置き換える。
        // NoteVisuals 自身が冪等で、既存プレハブにも安全に被せられる。
        var visuals=go.GetComponent<NoteVisuals>() ?? go.AddComponent<NoteVisuals>();
        visuals.Initialize(); visuals.ResetForReuse();
        note.Visuals = visuals;
        note.ConfigureReveal(nd, vanishRemaining);
        visuals.SetVeil(note.IsHidden ? 1f
            : note.Vanish != VanishMode.None ? VanishRules.BodyVeil(note.Vanish, vanishRemaining) : nd.veil);
        // 使い回しの組は手ごとに分かれているが、縁は出現のたびに付け外しする(ステージをまたいでも残さない)。
        visuals.SetContrastOutline(outlineLeftNotes && note.RequiredHand == SaberHand.Left);
        go.SetActive(true);

        // 方向指定なら矢印マーカー
        if (!reused && note.RequiredDirection != CutDirection.None)
        {
            BuildArrow(go.transform, note.RequiredDirection);
        }
        if (reused && note.Arrow != null) note.Arrow.Bind(note, visuals.baseColor);
        ApplySideArrow(note);
        // ロングは Z 方向に伸ばし、上に残カウント数字を浮かべる。
        // count が大きすぎる（50 など）と視野を覆い尽くすので longMaxVisualZScale でキャップする。
        if (note.RequiredCutCount > 1)
        {
            Vector3 sc = go.transform.localScale;
            // 見た目の長さは滞留時間に比例させる。「仮想カウント = 1 + 滞留/1カット秒」で、
            // 長さ未指定のロングは従来 (= count) と完全に同じ見た目になる。
            float virtualCount = secondsPerLongCut > 0.0001f
                ? 1f + LingerSecondsFor(note) / secondsPerLongCut
                : note.RequiredCutCount;
            // Pと滑るロングは長い本体で後続を隠さない。滞留時間・床・回数の数字は保つ。
            float zFactor = note.HasOvertake || note.HasSlide
                ? 1f : Mathf.Clamp(virtualCount, 1f, longMaxVisualZScale);
            go.transform.localScale = new Vector3(sc.x, sc.y, sc.z * zFactor);
            if(note.countLabel == null) BuildCountLabel(go.transform,note);
            note.countLabel.text=note.RequiredCutCount.ToString();
            note.countLabel.transform.position=go.transform.position+LongNoteCountStyle.WorldOffset;
            note.countLabel.alpha = note.CueVisibility;
            note.countLabel.gameObject.SetActive(!note.HidesCues);
            if(Pooling) note.WarmCracks(Mathf.Min(16,note.RequiredCutCount-1));
        }

        // 切る瞬間を読みやすくする着地ゴースト(固定枠+収縮枠が重なった瞬間 = 切る瞬間)
        if (buildTimingCues)
        {
            var cue = go.GetComponent<NoteTimingCue>();
            if (cue == null) cue = go.AddComponent<NoteTimingCue>();
            cue.Initialize(note, judgeZ);
            note.TimingCue = cue;
        }

        // 本体・矢印・ひびを描画用の子へまとめる。着地枠と床の印は別の着地点に残す。
        note.EnsureWindVisual();

        if (UsesFall(nd)) PrepareFallNote(note, warming ? approachTime : note.HitTime - songTime);
        // R の鏡の見た目(cutFeedback.Track より前。Track は出現時の BodyRenderer を覚える)。
        if (UsesReverse(nd)) PrepareReverseNote(note, nd, warming ? approachTime : note.HitTime - songTime);
        // Q の発射点の見た目(同じく cutFeedback.Track より前)。
        if (UsesLaunch(nd)) PrepareLaunchNote(note, nd);
        // 通知・同時線を作る前に、遅れて生成されたコマも正しい位置へ置く。
        UpdateOvertakeNote(note, warming ? SpawnTimeFor(nd) : songTime);

        // 新しく作った数字・矢印にも、通知より前に同じ残り時間の表示を当てる。
        note.TickReveal(vanishRemaining);
        if(warming) { Recycle(note); return; }

        // 同時押しガイド: 同時刻のノーツを明るい白線で結び、本体も少し光らせる。
        if (simultaneousGuideEnabled)
        {
            foreach (var other in liveNotes)
            {
                if (other == null || other.IsCut || other.IsMissed) continue;
                if (System.Math.Abs(other.HitTime - note.HitTime) <= SimultaneousEpsilonSeconds)
                {
                    SimultaneousNoteLink.Create(other, note, noteRoot);
                }
            }
        }

        // 風のノーツは本来の大きさを覚え、出た瞬間の小ささ・回り込み・傾きを最初のコマから当てる。
        if (note.HasWind)
        {
            note.WindBaseScale = go.transform.localScale;
            ApplyWindPose(note, note.HitTime - songTime);
            ApplyWindSway(note, songTime, ComputeNoteZ(note, note.HitTime - songTime, Speed));
        }

        liveNotes.Add(note);
        if (cutFeedback == null) cutFeedback = GameplayCutFeedback.Create(this);
        cutFeedback.Track(note);
        OnNoteSpawned?.Invoke(note);
    }

    // 風のノーツの姿勢(2026-10-26 W の出方の改善)。出た瞬間は小さく、回り込みながら本来の大きさになり、
    // 流れる速さに合わせて葉のように傾く。矢印のノーツは向きを読み違えないよう回さない。
    // 届く CalmSeconds 前からは本来の大きさ・まっすぐ(判定の窓では今までと同じ姿)。
    void ApplyWindPose(CuttableNote note, double remaining)
    {
        Vector3 baseScale = note.WindBaseScale;
        if (remaining <= WindRules.CalmSeconds)
        {
            note.transform.localScale = baseScale;
            note.transform.rotation = Quaternion.identity;
            return;
        }
        double sinceSpawn = approachTime - remaining;
        float grow = WindRules.GrowScale(sinceSpawn);
        note.transform.localScale = new Vector3(baseScale.x * grow, baseScale.y * grow, baseScale.z);
        if (note.RequiredDirection != CutDirection.None) { note.transform.rotation = Quaternion.identity; return; }
        double beatsPerSecond = chart != null && chart.bpm > 0 ? chart.bpm / 60.0 : 2.0;
        float bank = WindRules.BankDegrees(note.WindX, note.WindY, note.WindSpin, note.WindSway,
            remaining, approachTime, windBeat, beatsPerSecond);
        note.transform.rotation = Quaternion.Euler(0f, 0f, bank + WindRules.SpinIn(sinceSpawn));
    }

    void ApplyWindSway(CuttableNote note, double songTime, float z)
    {
        // 絶対時刻でも比較し、引き算の丸めで静止境界に微小なずれを残さない。
        float x = songTime >= note.HitTime - WindRules.StillSeconds ? 0f
            : WindRules.SwayOffset(note.WindSway, note.HitTime - songTime, approachTime, windBeat)
                * WindRules.DepthScale(z);
        note.SetWindSway(new Vector3(x, 0f, 0f));
    }

    // 消える(Zero の「V」)ノーツの今の見え方。本体は NoteVisuals、合図(矢印・枠・床の印・ロングの数字)は CuttableNote.Veil。
    static void ApplyVanish(CuttableNote note, double remaining)
    {
        float cue = VanishRules.CueVeil(note.Vanish, remaining);
        if (note.Veil != cue)
        {
            note.Veil = cue;
            if (note.Arrow != null) note.Arrow.RefreshVeil();
            if (note.countLabel != null && !note.IsMissed)
            {
                note.countLabel.alpha = note.CueVisibility;
                note.countLabel.gameObject.SetActive(!note.HidesCues);
            }
        }
        if (note.Visuals != null) note.Visuals.SetVeil(VanishRules.BodyVeil(note.Vanish, remaining));
    }

    // 本体と床でC案の二段シェブロンを共有。輪郭は拡縮せず一定幅にする。
    public static void BuildArrow(Transform parent, CutDirection dir)
    {
        var arrow = new GameObject("Arrow");
        arrow.transform.SetParent(parent, false);
        arrow.transform.localPosition = new Vector3(0, 0, -.57f);
        arrow.transform.localRotation = Quaternion.Euler(0, 0, CutDirectionHelper.ToZRotationDegrees(dir));
        var owner = arrow.AddComponent<NoteArrowMaterials>();
        var shader = Resources.Load<Shader>("Effects/NoteGuide");
        for (int layer = 0; layer < 2; layer++)
        {
            var mesh = FlickArrowShape.CreateMesh(layer == 0);
            owner.Register(mesh);
            var part = new GameObject(layer == 0 ? "ArrowBacking" : "Bars", typeof(MeshFilter), typeof(MeshRenderer));
            part.transform.SetParent(arrow.transform, false);
            part.transform.localScale = Vector3.one;
            part.transform.localPosition = new Vector3(0, 0, layer == 0 ? .008f : -.008f);
            part.GetComponent<MeshFilter>().sharedMesh = mesh;
            var material = new Material(shader) { renderQueue = 3021 + layer };
            // 縁取りは黒(投影でも矢印の白と本体色の両方から分離する)。
            material.SetColor(NoteArrowMaterials.BaseColorId, layer == 0 ? new Color(0f, 0f, .01f) : Color.white);
            owner.Register(material, layer == 1);
            var renderer = part.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
        var note = parent.GetComponent<CuttableNote>();
        var visuals = parent.GetComponent<NoteVisuals>();
        owner.Bind(note, visuals != null ? visuals.baseColor : Color.white);
    }
    private static void BuildCountLabel(Transform target, CuttableNote note)
    {
        // 親にしない：ロングノーツの Z スケール拡張に引きずられて位置や形が歪まないように。
        // 別 GameObject + FollowTransformWorldOffset で追従させる。
        var go = new GameObject("CountLabel");
        Vector3 offset = LongNoteCountStyle.WorldOffset;
        go.transform.position = target.position + offset;
        // TMP の標準正面は -Z 側。反転スケールを使わず、3D の奥行きテストを保つ。
        go.transform.rotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        var tmp = go.AddComponent<TMPro.TextMeshPro>();
        tmp.text = note.RequiredCutCount.ToString();
        LongNoteCountStyle.Apply(tmp);

        var follower = go.AddComponent<FollowTransformWorldOffset>();
        follower.target = target;
        follower.worldOffset = offset;

        note.countLabel = tmp;
    }

    private GameObject PickPrefab(string color)
    {
        if (!string.IsNullOrEmpty(color))
        {
            string c = color.ToLowerInvariant();
            if (c == "red" && notePrefabRed != null) return notePrefabRed;
            if (c == "blue" && notePrefabBlue != null) return notePrefabBlue;
        }
        return notePrefab;
    }

    private void UpdateLive(double songTime)
    {
        float speed = Speed;
        for (int i = liveNotes.Count - 1; i >= 0; i--)
        {
            CuttableNote note = liveNotes[i];
            if (note == null)
            {
                liveNotes.RemoveAt(i);
                continue;
            }

            // 判定時刻を時間差で表現し、現在の Z をそこから計算する。緩急の譜面では、奥行きを見かけの残り時間で決める
            // (届いたあとに後ろへ流れるタップも同じ速さで、止まる間は一緒に止まる。ロングのとどまりは実際の時刻のまま)。
            double dt = note.HitTime - songTime;
            double depth = dt;
            if (scrollMap != null)
                depth = dt > 0 ? ShapedRemaining(note.HitTime, songTime)
                    : note.RequiredCutCount <= 1 ? VisualRemaining(note.HitTime, songTime) : dt;
            float z = ComputeNoteZ(note, depth, speed);
            Vector3 p = note.transform.position;
            if (note.HasWind)
            {
                // 絶対時刻でも境界を比べ、引き算の丸めで静止区間に微小なずれを残さない。
                Vector2 offset = songTime >= note.HitTime - WindRules.CalmSeconds
                    ? Vector2.zero
                    : WindRules.WorldOffset(note.WindX, note.WindY, note.WindSpin, 0f,
                        dt, approachTime, windBeat, z);
                p.x = note.LandingPosition.x + offset.x;
                p.y = note.LandingPosition.y + offset.y;
                ApplyWindPose(note, songTime >= note.HitTime - WindRules.CalmSeconds ? 0 : dt);
            }
            note.transform.position = new Vector3(p.x, p.y, z);
            if (note.IsFall) UpdateFallNote(note, dt);
            UpdateSlideNote(note, songTime);
            UpdateOvertakeNote(note, songTime);
            UpdateReverseNote(note, songTime);
            // Q の発射点: 根元の奥行きが決まったあとに、見た目の子のずれを当てる。
            UpdateLaunchNote(note);
            // 緩急の譜面では、割れた破片の勢いも今の流れる速さに合わせる(止まっている間は前へ流さない)。
            if (scrollMap != null && note.RequiredCutCount <= 1)
                note.FlowNoteVelocity = new Vector3(0f, 0f, -speed * (float)scrollMap.SpeedAt(songTime - totalOffsetSeconds));

            // ロングノーツは複数回切る時間が必要なので、後方の窓を回数に応じて伸ばす。
            // 早め側は earlyJudgeWindow、遅め側は lateWindow。受付窓と Miss は判定の時計(カメラの遅れを引いた時刻)で見る。
            float lateWindow = LateWindowFor(note);
            double judgeDt = note.HitTime - JudgeClock(songTime);
            UpdateJudgmentWindow(note, judgeDt, lateWindow);

            // 消える(V): 残り時間で本体と合図の見え方を決める(切ったノーツは CuttableNote が姿を戻して割る)。
            if (note.Vanish != VanishMode.None && !note.IsCut) ApplyVanish(note, dt);
            if (note.HasWind) ApplyWindSway(note, songTime, z);

            if (!note.IsCut)
            {
                note.TickReveal(dt);
                note.SetDarkBrightness(BrightnessFor?.Invoke(note) ?? 1f);
            }

            // タイミングキュー(着地ゴースト)の駆動。見た目なので曲の時計のまま。
            if (note.TimingCue != null)
            {
                note.TimingCue.Tick(dt, approachTime, earlyJudgeWindow, lateWindow);
            }
            if (note.Arrow != null) note.Arrow.Tick(Time.deltaTime);
            note.TickCracks(Time.deltaTime);

            if (note.IsCut)
            {
                Recycle(note);
                liveNotes.RemoveAt(i);
                continue;
            }

            // 判定窓を過ぎた瞬間に Miss を1回だけ発火するが、ノーツは消さずに後ろへ流し続ける。
            // 経路付きは経路の終端で残りを確定する。カメラ遅延はJudgeClockで従来どおり扱う。
            bool expired = note.HasSlidePath
                ? judgeDt < -lateWindow
                : judgeDt < -(lateWindow + missGrace)
                    && !(ShouldDeferMiss?.Invoke(note, songTime) ?? false)
                    && !HoldsMiss(note, judgeDt, lateWindow);
            if (!note.IsMissed && expired)
            {
                note.MarkMiss();
                OnNoteMissed?.Invoke(note);
            }

            // 後方に十分流れたら回収。
            if (note.IsMissed && dt < -(lateWindow + missGrace + despawnAfterMissSeconds))
            {
                Recycle(note);
                liveNotes.RemoveAt(i);
            }
        }
    }

    // ロングの滞留時間（秒）。lengthMs 指定（OverrideLingerSeconds）があればそれを、
    // 無ければ従来の (count-1) × secondsPerLongCut を返す。タップは 0。テスト用に公開。
    public float LingerSecondsFor(CuttableNote note)
    {
        if (note == null || note.RequiredCutCount <= 1) return 0f;
        return note.OverrideLingerSeconds > 0f
            ? note.OverrideLingerSeconds
            : (note.RequiredCutCount - 1) * secondsPerLongCut;
    }

    // テスト用に公開：ロングの判定ウィンドウは秒数で何秒か。
    public float LateWindowFor(CuttableNote note)
    {
        if (note == null) return judgeWindow;
        // 経路付きロングは明示した終端まで。旧ロング・旧slideToには従来の猶予を残す。
        if (note.HasSlidePath) return LingerSecondsFor(note);
        return judgeWindow + LingerSecondsFor(note);
    }

    // 刃がノーツの上を通っている最中なら、締め切りのあとも passHoldSeconds までは Miss にしない。
    bool HoldsMiss(CuttableNote note, double judgeDt, float lateWindow)
    {
        return HoldMiss != null && judgeDt >= -(lateWindow + missGrace + passHoldSeconds) && HoldMiss(note);
    }

    // ロングノーツは HitTime 経過後に判定面の少し後方で「滞留」させて、プレイヤーが切り続けやすくする。
    // テスト用に公開（純粋関数）。
    public float ComputeNoteZ(CuttableNote note, double dt, float speed)
    {
        if (note != null && note.IsFall) return judgeZ;
        if (note == null || note.RequiredCutCount <= 1 || dt >= 0)
        {
            return judgeZ + speed * (float)dt;
        }
        double overshoot = -dt;
        float lingerDuration = LingerSecondsFor(note);
        if (lingerDuration <= 0.001f || overshoot <= lingerDuration)
        {
            float progress = lingerDuration > 0.001f ? (float)(overshoot / lingerDuration) : 1f;
            return judgeZ - longLingerDriftZ * progress;
        }
        double afterLinger = overshoot - lingerDuration;
        return judgeZ - longLingerDriftZ - speed * (float)afterLinger;
    }
}
