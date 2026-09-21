using PoolKey = System.ValueTuple<UnityEngine.GameObject, CutDirection, int, SaberHand, bool, bool, bool>;
using System.Collections.Generic;
using UnityEngine;

// chart.json のノーツを approachTime だけ先読みで生成し、
// spawnZ → judgeZ に向けて Z 軸で流す。判定ウィンドウも更新する。
public class NoteSpawner : MonoBehaviour
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
    // miss 扱いになった後、ノーツを画面後方まで流してから片付けるまでの秒数。
    public float despawnAfterMissSeconds = 1.5f;
    // ロングノーツの追加カット1回あたりの判定ウィンドウ延長秒。
    // 4 回切りなら lateWindow = judgeWindow + 3 * secondsPerLongCut ≒ 2.3s。
    // 50 回切りなら ≒ 34.5s（25秒の「サビ前ロング」も余裕で表現可能）。
    public float secondsPerLongCut = 0.7f;
    // ロングノーツが HitTime 経過後、判定面の少し後方に滞留する距離（プレイヤーが切り続けられるように）。
    public float longLingerDriftZ = 1.0f;
    // ロングノーツの見た目の Z スケール上限（count=50 等でも視野を埋め尽くさないようにキャップ）。
    public float longMaxVisualZScale = 6f;
    // 着地ゴースト(判定面の固定枠+収縮枠。NoteTimingCue)を各ノーツに付ける。
    public bool buildTimingCues = true;

    private ChartData chart;
    private int nextIndex;
    private double totalOffsetSeconds; // chart.offsetMs/1000 + extraOffsetSeconds
    private double extraOffsetSeconds; // GamePlayManager から実行時に上書き
    private readonly List<CuttableNote> liveNotes = new List<CuttableNote>();
    private GameplayCutFeedback cutFeedback;
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

    PoolKey Key(NoteData data) => (PickPrefab(data.color), CutDirectionHelper.Parse(data.direction),
        Mathf.Clamp(data.count,1,8), SaberHandHelper.FromColor(data.color),
        string.Equals(data.color,"gold",System.StringComparison.OrdinalIgnoreCase), DisplaySettings.ProjectorMode, buildTimingCues);
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
            double linger=data.count <= 1 ? 0 : data.lengthMs > 0 ? data.lengthMs/1000.0 : (data.count-1)*secondsPerLongCut;
            group.events.Add((EffectiveTime(data)-approachTime,1));
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
            var renderer=note.GetComponent<MeshRenderer>();
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
    public int NextIndex => nextIndex;
    // 譜面の総ノーツ数(ランクの合計割合計算用)。譜面未設定なら 0。
    public int TotalNoteCount => chart != null && chart.notes != null ? chart.notes.Count : 0;

    public event System.Action<CuttableNote> OnNoteSpawned;
    public event System.Action<CuttableNote> OnNoteMissed;
    public event System.Action OnChartReset;

    // 同時押しノーツの間の白い連結線(プロセカの同時線)。標準機能として常時有効。
    // シーンに旧 NoteSpawner がシリアライズ済みでもコード既定値(true)が使われるよう NonSerialized。
    [System.NonSerialized] public bool simultaneousGuideEnabled = true;
    // 「同時」とみなす時刻差(秒)。譜面上同じ拍のノーツは同一値になるので余裕を持った小さい値でよい。
    public const double SimultaneousEpsilonSeconds = 0.01;

    public void SetChart(ChartData data)
    {
        OnChartReset?.Invoke();
        if (cutFeedback != null) cutFeedback.ResetState();
        if (FloorGuide != null) FloorGuide.Clear();
        chart = data;
        nextIndex = 0;
        RecomputeTotalOffset();
        foreach (var n in liveNotes)
        {
            if (n != null) Recycle(n);
        }
        liveNotes.Clear();
        Prewarm();
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

    private static void SafeDestroy(GameObject go)
    {
        if (go == null) return;
        if (Application.isPlaying) Destroy(go);
        else DestroyImmediate(go);
    }

    // 毎フレーム呼ぶ想定。songTime は SongPlayer.SongTime（秒）を渡す。
    public void Tick(double songTime)
    {
        SpawnDue(songTime);
        UpdateLive(songTime);
        if (FloorGuide != null) FloorGuide.Tick(this, songTime);
        if (cutFeedback != null) cutFeedback.Tick(Time.deltaTime);
    }

    // セーバー判定の前に受付窓だけを今の曲時計へ合わせる。
    // 生成・移動・Miss通知は従来どおり判定後の Tick で行う。
    public void RefreshJudgmentWindows(double songTime)
    {
        foreach (CuttableNote note in liveNotes)
        {
            if (note != null)
                UpdateJudgmentWindow(note, note.HitTime - songTime, LateWindowFor(note));
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
    }

    void OnDestroy()
    {
        // Spawnerコンポーネントだけが取り外された場合も、表示用の所有資源を残さない。
        if (cutFeedback != null) SafeDestroy(cutFeedback.gameObject);
        cutFeedback = null;
        if (FloorGuide != null) SafeDestroy(FloorGuide.gameObject);
        foreach(var note in poolKeys.Keys) if(note != null) SafeDestroy(note.gameObject);
        poolKeys.Clear(); idleNotes.Clear(); idleCount=0;
        if(idleRoot != null) SafeDestroy(idleRoot.gameObject);
        if(fragmentPool != null) SafeDestroy(fragmentPool.gameObject);
    }

    private void SpawnDue(double songTime)
    {
        if (chart == null) return;
        while (nextIndex < chart.notes.Count)
        {
            NoteData nd = chart.notes[nextIndex];
            double eff = EffectiveTime(nd);
            if (songTime + approachTime < eff) break;
            SpawnOne(nd);
            nextIndex++;
        }
    }

    private void SpawnOne(NoteData nd, bool warming = false)
    {
        GameObject prefab = PickPrefab(nd.color);
        if (prefab == null) return;
        float scale = chart != null ? chart.coordScale : 1f;
        Vector3 pos = new Vector3(nd.x * scale, nd.y * scale, spawnZ);
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
        if(Pooling && !reused) poolKeys.Add(note,key);
        note.HitTime = EffectiveTime(nd);
        note.RequiredDirection = CutDirectionHelper.Parse(nd.direction);
        note.RequiredCutCount = Mathf.Max(1, nd.count);
        note.RemainingCuts = note.RequiredCutCount;
        // 長さの直接指定（lengthMs）。未指定(0)なら従来の回数×secondsPerLongCut。
        note.OverrideLingerSeconds = nd.lengthMs > 0f && note.RequiredCutCount > 1 ? nd.lengthMs / 1000f : 0f;
        // 金ノーツ判定（chart.json の color:"gold"）
        note.IsGold = !string.IsNullOrEmpty(nd.color) && nd.color.ToLowerInvariant() == "gold";
        // 担当ハンド（blue=左手 / red=右手 / gold・無色=どちらでも）。ロングにも同じルールを適用。
        note.RequiredHand = SaberHandHelper.FromColor(nd.color);

        // 旧プレハブにある面ステッカー等を剥がして、クリスタル＋ネオン外観に置き換える。
        // NoteVisuals 自身が冪等で、既存プレハブにも安全に被せられる。
        var visuals=go.GetComponent<NoteVisuals>() ?? go.AddComponent<NoteVisuals>();
        visuals.Initialize(); visuals.ResetForReuse();
        go.SetActive(true);

        // 方向指定なら矢印マーカー
        if (!reused && note.RequiredDirection != CutDirection.None)
        {
            BuildArrow(go.transform, note.RequiredDirection);
        }
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
            float zFactor = Mathf.Clamp(virtualCount, 1f, longMaxVisualZScale);
            go.transform.localScale = new Vector3(sc.x, sc.y, sc.z * zFactor);
            if(note.countLabel == null) BuildCountLabel(go.transform,note);
            note.countLabel.text=note.RequiredCutCount.ToString();
            note.countLabel.transform.position=go.transform.position+LongNoteCountStyle.WorldOffset;
            note.countLabel.gameObject.SetActive(true);
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

        if(warming) { Recycle(note); return; }

        // 同時押しガイド: 既に生きている同時刻ノーツと白線で結ぶ(小節線より薄い)
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

        liveNotes.Add(note);
        if (cutFeedback == null) cutFeedback = GameplayCutFeedback.Create(this);
        cutFeedback.Track(note);
        OnNoteSpawned?.Invoke(note);
    }

    // 本体と床の矢印は同じ形状。暗い輪郭の中へ太い白矢印を置く。
    public static void BuildArrow(Transform parent, CutDirection dir)
    {
        var arrow = new GameObject("Arrow");
        arrow.transform.SetParent(parent, false);
        arrow.transform.localPosition = new Vector3(0, 0, -.57f);
        arrow.transform.localRotation = Quaternion.Euler(0, 0, CutDirectionHelper.ToZRotationDegrees(dir));
        var owner = arrow.AddComponent<NoteArrowMaterials>();
        var mesh = FlickArrowShape.CreateMesh();
        owner.Register(mesh);
        var shader = Resources.Load<Shader>("Effects/NoteGuide");
        for (int layer = 0; layer < 2; layer++)
        {
            var part = new GameObject(layer == 0 ? "ArrowBacking" : "Bars", typeof(MeshFilter), typeof(MeshRenderer));
            part.transform.SetParent(arrow.transform, false);
            part.transform.localScale = Vector3.one * (layer == 0 ? 1.04f : .8f);
            part.transform.localPosition = new Vector3(0, 0, layer == 0 ? .008f : -.008f);
            part.GetComponent<MeshFilter>().sharedMesh = mesh;
            var material = new Material(shader) { renderQueue = 3021 + layer };
            material.SetColor("_BaseColor", layer == 0 ? new Color(.015f, .02f, .045f) : Color.white);
            owner.Register(material);
            var renderer = part.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
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

            // 判定時刻を時間差で表現し、現在の Z をそこから計算する。
            double dt = note.HitTime - songTime;
            float z = ComputeNoteZ(note, dt, speed);
            Vector3 p = note.transform.position;
            note.transform.position = new Vector3(p.x, p.y, z);

            // ロングノーツは複数回切る時間が必要なので、後方の窓を回数に応じて伸ばす。
            // 早め側は earlyJudgeWindow で別管理（小さい）、遅め側は lateWindow（大きい）で非対称化。
            float lateWindow = LateWindowFor(note);
            UpdateJudgmentWindow(note, dt, lateWindow);

            // タイミングキュー(着地ゴースト)の駆動
            if (note.TimingCue != null)
            {
                note.TimingCue.Tick(dt, approachTime, earlyJudgeWindow, lateWindow);
            }

            if (note.IsCut)
            {
                Recycle(note);
                liveNotes.RemoveAt(i);
                continue;
            }

            // 判定窓を過ぎた瞬間に Miss を1回だけ発火するが、ノーツは消さずに後ろへ流し続ける。
            if (!note.IsMissed && dt < -(lateWindow + missGrace)
                && !(ShouldDeferMiss?.Invoke(note, songTime) ?? false))
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
        return judgeWindow + LingerSecondsFor(note);
    }

    // ロングノーツは HitTime 経過後に判定面の少し後方で「滞留」させて、プレイヤーが切り続けやすくする。
    // テスト用に公開（純粋関数）。
    public float ComputeNoteZ(CuttableNote note, double dt, float speed)
    {
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
