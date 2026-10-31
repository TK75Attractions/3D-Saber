using System.Collections.Generic;
using UnityEngine;

// 勉強の裏譜面 Q の飾りのノーツ(quiz.decoys。「答えの柱」の、正しくない3本の柱)。
// 第4版(2026-10-28 夜、ユーザー「ノーツを、選択肢ごとに配置を変えて同時押しですべてとれてしまわないようにする、赤、青、フリックも解禁」)で
//   金のタップだけでなく、赤・青・矢印の飾りも出す(QuizDecoyData の color・direction)。使い回しは色と向きごとに分ける。
// 本物のノーツと見分けがつくと答えがばれるので、SpawnOne のノーツの作り方をなぞる: 同じプレハブ・同じ大きさ(投影の拡大も)・
//   同じ色と発光(NoteVisuals)・同じ接近(ComputeNoteZ)・同じ着地の枠と判定窓の光と金の予兆(NoteTimingCue。予兆の「チリッ」も
//   GoldNoteSfx が JudgmentOwner を見て同じに鳴らす)・同じ光の保護(GameplayCutFeedback.Track)。試験用紙の上へ上げるのは
//   QuizMode(OnDecoySpawned)。床の印は飾りに無いので、紙の間は QuizMode が本物の側も止める。
// 判定には入らない: CuttableNote.IsDecoy で ActiveNotes(SaberCutJudge の候補)に登録せず、刃が通っても切れない(CutCore)。
//   liveNotes(MISS・曲の終わり・CameraImuJudgment・同時押しの線)にも入れず、OnNoteSpawned も出さない
//   (ScoreManager・GoldNoteSfx・GateAccents など十数か所が聞いている)。点・コンボ・満点の分母・リザルト・録画の自動切り・
//   譜面の lint(どれも chart.notes か ActiveNotes を見る)のどれにも関係しない。
// 届いたら判定面を少し過ぎながら 0.3 秒で薄れて消える(MISS の暗くなる見た目は出さない。届くまでは本物と同じ姿)。
// 姿は届く時刻と曲の時計だけで決めるので、止まれば止まり、時計が戻れば戻る。
// 片付け: 譜面のやり直し(SetChart)・QuizMode の終わり(ClearDecoys。曲の終わり・途中でやめたとき)・Spawner の破棄。
public partial class NoteSpawner
{
    // 使い回しの上限(色と向きの組ごと)。
    const int MaxIdleDecoys = 16;
    readonly List<CuttableNote> liveDecoys = new List<CuttableNote>();
    // 使い回しは色と向きの組ごと(プレハブの色と矢印の子を作り直さない)。
    readonly Dictionary<(string color, CutDirection direction), Stack<CuttableNote>> idleDecoys =
        new Dictionary<(string color, CutDirection direction), Stack<CuttableNote>>();
    readonly Dictionary<CuttableNote, (string color, CutDirection direction)> decoyKinds =
        new Dictionary<CuttableNote, (string color, CutDirection direction)>();
    readonly List<QuizDecoyData> decoySchedule = new List<QuizDecoyData>();
    int nextDecoy;
    // 飾りを作るときの親(有効でない。作った直後に OnEnable を走らせない)。使い回しの飾りは noteRoot の下で止めておく。
    Transform decoyIdleRoot;
    // 使い回しの飾りを作ったときの投影モード(大きさ・色が変わるので、切り替わったら作り直す)。
    bool decoyProjector;
    // 投影モードがオンのときに作った飾り(見た目は作ったときに決まる)。切り替わる前に作った飾りは、片付いても使い回さない。
    readonly HashSet<CuttableNote> projectorDecoys = new HashSet<CuttableNote>();

    // 飾りの金が出たとき(QuizMode が試験用紙の上へ上げる)。OnNoteSpawned は出さない。
    public event System.Action<CuttableNote> OnDecoySpawned;
    public IReadOnlyList<CuttableNote> LiveDecoys => liveDecoys;
    public int DecoyCount => decoySchedule.Count;
    public int NextDecoyIndex => nextDecoy;
    public int CreatedDecoyCount { get; private set; }
    public int PooledDecoyCount
    {
        get { int n = 0; foreach (var stack in idleDecoys.Values) n += stack.Count; return n; }
    }

    static (string color, CutDirection direction) DecoyKind(QuizDecoyData data) =>
        (QuizRules.DecoyColor(data.color), CutDirectionHelper.Parse(data.direction));

    // 風・流星などを持たない、その色と向きの形(ConfigureWind が読むだけで、書き換えない)。
    static NoteData DecoyShape(QuizDecoyData data, (string color, CutDirection direction) kind) => new NoteData
    {
        type = kind.direction == CutDirection.None ? "tap" : "direction", color = kind.color,
        direction = kind.direction == CutDirection.None ? "none" : data.direction,
    };

    // 譜面のやり直し(SetChart)。飛んでいる飾りを片付け、新しい譜面の飾りを時刻順に並べ、使い回す分を先に作る。
    void ResetDecoys()
    {
        RecycleLiveDecoys();
        nextDecoy = 0;
        decoySchedule.Clear();
        var source = chart?.quiz?.decoys;
        if (source != null)
            foreach (var decoy in source)
                if (decoy != null && QuizRules.Finite(decoy.ms) && QuizRules.Finite(decoy.x) && QuizRules.Finite(decoy.y))
                    decoySchedule.Add(decoy);
        decoySchedule.Sort((a, b) => a.ms.CompareTo(b.ms));
        MatchIdleDecoysToProjectorMode();
        PrewarmDecoys();
    }

    // 投影モードの大きさ・色は作ったときに決まる(NoteVisuals.Initialize)ので、切り替わっていたら使い回しを捨てる。捨てたら true。
    bool MatchIdleDecoysToProjectorMode()
    {
        if (decoyProjector == DisplaySettings.ProjectorMode) return false;
        DestroyIdleDecoys();
        decoyProjector = DisplaySettings.ProjectorMode;
        return true;
    }

    // 飛んでいる飾りを消し、残りの飾りも出さない(QuizMode の終わり。曲の終わり・途中でやめたとき・譜面のやり直し)。
    public void ClearDecoys()
    {
        RecycleLiveDecoys();
        nextDecoy = decoySchedule.Count;
    }

    double DecoyHitTime(QuizDecoyData data) => data.ms / 1000.0 + totalOffsetSeconds;

    // Tick から(UpdateLive のあと、床の印より前)。
    void TickDecoys(double songTime)
    {
        SpawnDueDecoys(songTime);
        for (int i = liveDecoys.Count - 1; i >= 0; i--)
        {
            var decoy = liveDecoys[i];
            if (decoy == null) { liveDecoys.RemoveAt(i); continue; }
            if (UpdateDecoy(decoy, songTime)) continue;
            liveDecoys.RemoveAt(i);
            RecycleDecoy(decoy);
        }
    }

    void SpawnDueDecoys(double songTime)
    {
        // 曲の途中で投影モード(F4)が切り替わったら、使い回しを今の見た目で作り直してから出す。本物は出すときの投影モードの
        // 使い回し(NoteSpawner.Key)から借りるので、古い見た目のままの飾りで正解の柱が分かっていた(2026-10-30 全体の点検で見つけた不具合)。
        // そのとき飛んでいた古い飾りは、片付くときに壊す(RecycleDecoy・RentDecoy の StaleDecoy)。
        if (nextDecoy < decoySchedule.Count && MatchIdleDecoysToProjectorMode()) PrewarmDecoys();
        while (nextDecoy < decoySchedule.Count)
        {
            var data = decoySchedule[nextDecoy];
            double hit = DecoyHitTime(data);
            // 本物と同じく、見かけの残りが接近の時間になったら出す。
            if (VisualRemaining(hit, songTime) > approachTime) break;
            nextDecoy++;
            // 先へ飛んだ時計(処理落ち・テスト)で、もう消えているはずの飾りは出さない。
            if (songTime >= hit + QuizRules.DecoyFadeSeconds) continue;
            SpawnDecoy(data, hit, songTime, warming: false);
        }
    }

    CuttableNote SpawnDecoy(QuizDecoyData data, double hit, double songTime, bool warming)
    {
        var kind = DecoyKind(data);
        GameObject prefab = PickPrefab(kind.color);
        if (prefab == null) return null;
        var note = warming ? CreateDecoy(kind, prefab) : RentDecoy(kind, prefab);
        var go = note.gameObject;
        float scale = chart != null ? chart.coordScale : 1f;
        var landing = new Vector2(data.x * scale, data.y * scale);
        go.transform.SetParent(noteRoot, false);
        go.transform.position = new Vector3(landing.x, landing.y, spawnZ);
        go.transform.rotation = Quaternion.identity;
        go.transform.localScale = prefab.transform.localScale;
        // 本物と同じく、投影モードでは正面を一回り大きく。
        if (DisplaySettings.ProjectorMode)
        {
            Vector3 s0 = go.transform.localScale;
            go.transform.localScale = new Vector3(s0.x * ProjectorMode.NoteScale, s0.y * ProjectorMode.NoteScale, s0.z);
        }
        // 前の飛行の購読と印を外してから飾りの印を立て直す(有効にする前。OnEnable の登録を IsDecoy で止める)。
        note.ResetForSpawn();
        note.IsDecoy = true;
        note.IsPooled = false;
        note.FragmentPool = null;
        note.ConfigureWind(DecoyShape(data, kind), landing);
        note.Veil = NoteVeil.Clamp(data.veil);
        note.DecoyVeil = note.Veil;
        note.HitTime = hit;
        // 金の予兆の「チリッ」は、自分の Spawner のノーツだけ鳴らす(GoldNoteSfx.HandleOmen)。本物と同じに鳴らす。
        note.JudgmentOwner = this;
        note.RequiredDirection = kind.direction;
        note.RequiredCutCount = 1;
        note.RemainingCuts = 1;
        note.OverrideLingerSeconds = 0f;
        note.IsGold = kind.color == "gold";
        note.RequiredHand = SaberHandHelper.FromColor(kind.color);
        var visuals = go.GetComponent<NoteVisuals>();
        if (visuals == null) visuals = go.AddComponent<NoteVisuals>();
        visuals.Initialize(); visuals.ResetForReuse();
        note.Visuals = visuals;
        visuals.SetVeil(note.Veil);
        visuals.SetContrastOutline(outlineLeftNotes && note.RequiredHand == SaberHand.Left);
        go.SetActive(true);
        // 矢印は本物と同じ作り(SpawnOne)。使い回しは同じ向きの組なので、矢印の子はそのまま色だけ合わせ直す。
        if (note.Arrow == null && kind.direction != CutDirection.None) BuildArrow(go.transform, kind.direction);
        else if (note.Arrow != null) note.Arrow.Bind(note, visuals.baseColor);
        if (buildTimingCues)
        {
            var cue = go.GetComponent<NoteTimingCue>();
            if (cue == null) cue = go.AddComponent<NoteTimingCue>();
            cue.Initialize(note, judgeZ);
            note.TimingCue = cue;
        }
        if (warming) { RecycleDecoy(note); return null; }
        liveDecoys.Add(note);
        if (cutFeedback == null) cutFeedback = GameplayCutFeedback.Create(this);
        cutFeedback.Track(note);
        // 最初の予兆を進める前に、紙の上への移動と消音設定を受け取る。
        OnDecoySpawned?.Invoke(note);
        UpdateDecoy(note, songTime);
        return note;
    }

    // 飾り1つの今の姿。届く時刻と曲の時計だけで決める(本物の UpdateLive と同じ奥行き・同じ着地の枠)。消え切ったら false。
    bool UpdateDecoy(CuttableNote decoy, double songTime)
    {
        double dt = decoy.HitTime - songTime;
        float visibility = QuizRules.DecoyVisibility(songTime, decoy.HitTime);
        if (visibility <= 0f) return false;
        double depth = dt;
        if (scrollMap != null) depth = dt > 0 ? ShapedRemaining(decoy.HitTime, songTime) : VisualRemaining(decoy.HitTime, songTime);
        decoy.transform.position = new Vector3(decoy.LandingPosition.x, decoy.LandingPosition.y, ComputeNoteZ(decoy, depth, Speed));
        float veil = QuizRules.DecoyVeil(decoy.DecoyVeil, visibility);
        if (decoy.Veil != veil)
        {
            decoy.Veil = veil;
            if (decoy.Visuals != null) decoy.Visuals.SetVeil(veil);
        }
        decoy.SetDarkBrightness(BrightnessFor?.Invoke(decoy) ?? 1f);
        if (decoy.TimingCue != null) decoy.TimingCue.Tick(dt, approachTime, earlyJudgeWindow, LateWindowFor(decoy));
        return true;
    }

    // 有効でない親の下に作り、飾りの印を立てる前に OnEnable(判定の候補への登録)を走らせない。
    CuttableNote CreateDecoy((string color, CutDirection direction) kind, GameObject prefab)
    {
        EnsureDecoyRoot();
        var go = Instantiate(prefab, new Vector3(0f, 0f, spawnZ), Quaternion.identity, decoyIdleRoot);
        go.SetActive(false);
        go.name = prefab.name + " (Decoy)";
        CreatedDecoyCount++;
        var note = go.GetComponent<CuttableNote>();
        if (note == null) note = go.AddComponent<CuttableNote>();
        note.IsDecoy = true;
        decoyKinds[note] = kind;
        if (DisplaySettings.ProjectorMode) projectorDecoys.Add(note);
        return note;
    }

    // 今の投影モードと違うときに作った飾りか(F4 で切り替わる前の、古い見た目)。
    bool StaleDecoy(CuttableNote decoy) => projectorDecoys.Contains(decoy) != DisplaySettings.ProjectorMode;

    // 使い回さない飾りを壊す(色と向きの印と、作ったときの投影モードも忘れる)。
    void DiscardDecoy(CuttableNote decoy)
    {
        decoyKinds.Remove(decoy);
        projectorDecoys.Remove(decoy);
        SafeDestroy(decoy.gameObject);
    }

    CuttableNote RentDecoy((string color, CutDirection direction) kind, GameObject prefab)
    {
        if (idleDecoys.TryGetValue(kind, out var stack))
            while (stack.Count > 0)
            {
                var cached = stack.Pop();
                if (cached == null) continue;
                if (StaleDecoy(cached)) { DiscardDecoy(cached); continue; }
                return cached;
            }
        return CreateDecoy(kind, prefab);
    }

    void EnsureDecoyRoot()
    {
        if (decoyIdleRoot != null) return;
        var root = new GameObject("IdleDecoys");
        root.transform.SetParent(transform, false);
        root.SetActive(false);
        decoyIdleRoot = root.transform;
    }

    // 光の保護(OnRetired で外れる)と着地の枠を片付けて止め、使い回しへ戻す(使い回さないとき・投影モードが切り替わる前に作ったときは壊す)。
    // 親は noteRoot のまま(止めてあるので描かれない)。曲の終わり・シーンを閉じる途中(QuizMode.EndPlay)でも親を付け替えない。
    void RecycleDecoy(CuttableNote decoy)
    {
        if (decoy == null) return;
        decoy.Retire();
        if (!decoyKinds.TryGetValue(decoy, out var kind)) { SafeDestroy(decoy.gameObject); return; }
        if (!idleDecoys.TryGetValue(kind, out var stack)) idleDecoys[kind] = stack = new Stack<CuttableNote>();
        if (!Pooling || stack.Count >= MaxIdleDecoys || StaleDecoy(decoy))
        {
            DiscardDecoy(decoy);
            return;
        }
        stack.Push(decoy);
    }

    void RecycleLiveDecoys()
    {
        for (int i = liveDecoys.Count - 1; i >= 0; i--) RecycleDecoy(liveDecoys[i]);
        liveDecoys.Clear();
    }

    // 同時に飛ぶ飾りの数を色と向きの組ごとに見積もり、曲の前に作っておく(問題の途中で作って処理落ちしない)。保持は組ごとに 16 個まで。
    void PrewarmDecoys()
    {
        if (!Pooling || decoySchedule.Count == 0) return;
        var byKind = new Dictionary<(string color, CutDirection direction), List<QuizDecoyData>>();
        foreach (var data in decoySchedule)
        {
            var kind = DecoyKind(data);
            if (!byKind.TryGetValue(kind, out var list)) byKind[kind] = list = new List<QuizDecoyData>();
            list.Add(data);
        }
        foreach (var pair in byKind)
        {
            var events = new List<(double time, int delta)>(pair.Value.Count * 2);
            foreach (var data in pair.Value)
            {
                double hit = DecoyHitTime(data);
                events.Add((SpawnTimeFor(hit), 1));
                events.Add((hit + QuizRules.DecoyFadeSeconds + .05, -1));
            }
            events.Sort((a, b) => { int c = a.time.CompareTo(b.time); return c != 0 ? c : a.delta.CompareTo(b.delta); });
            int live = 0, peak = 0;
            foreach (var e in events) { live += e.delta; peak = Mathf.Max(peak, live); }
            int want = Mathf.Min(MaxIdleDecoys, peak + 1);
            var sample = pair.Value[0];
            double sampleHit = DecoyHitTime(sample);
            for (int guard = 0; IdleCount(pair.Key) < want && guard < MaxIdleDecoys; guard++)
                SpawnDecoy(sample, sampleHit, sampleHit - approachTime, warming: true);
        }
    }

    int IdleCount((string color, CutDirection direction) kind) => idleDecoys.TryGetValue(kind, out var stack) ? stack.Count : 0;

    void DestroyIdleDecoys()
    {
        foreach (var stack in idleDecoys.Values)
            while (stack.Count > 0)
            {
                var decoy = stack.Pop();
                if (decoy == null) continue;
                DiscardDecoy(decoy);
            }
    }

    // Spawner の破棄(OnDestroy)。
    void DestroyDecoys()
    {
        foreach (var decoy in liveDecoys) if (decoy != null) SafeDestroy(decoy.gameObject);
        liveDecoys.Clear();
        DestroyIdleDecoys();
        decoyKinds.Clear();
        projectorDecoys.Clear();
        if (decoyIdleRoot != null) SafeDestroy(decoyIdleRoot.gameObject);
        decoyIdleRoot = null;
    }
}
