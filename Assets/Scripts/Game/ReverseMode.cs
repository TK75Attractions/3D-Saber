using System;
using System.Collections.Generic;
using UnityEngine;

// Manic Sonic の裏譜面「R(反転)」(2026-10-28)。HeatMode と同じ生き方(Create / Tick / EndPlay)。
// 譜面に reverse.enabled があるときだけ GamePlayManager が作る。GamePlayManager の Update で、フレームの最初に ApplyView(角度と投影。
// 一時停止中も)、判定の前に Advance(門・音・光。曲が進んでいる間だけ)を呼ぶ。どちらも曲の時計。
//   ・天地返しの絵(WorldTurn)、鏡門の絵と床の塗り分け(MirrorGateView)、音(ReverseSfx)をまとめる。
//   ・ノーツの鏡の見た目は NoteSpawner(NoteSpawner.Reverse)が毎コマ当てる。判定・採点・満点 100000 は変えない。
//   ・鏡のノーツが門を通った瞬間に筋と波紋を出す(残り時間で見るので、コマ飛びで飛び越しても少し遅れて出る)。
//   ・大きな光は PlayExcitement.TryLargeFlash の予算に乗せる(縁の光だけ。ゲートの内側は光らせない。F3 では出ない):
//     ちょうつがいの金を切った瞬間、門が割れるとき、最後の一回転が着いたとき。
//   ・F3 で天地返しが段で切り替わる瞬間(剣の入力の回転が跳ぶ)は、剣の速さの履歴とカメラの履歴を捨てる(架空の振りを作らない)。
// 止まっている間(剣の見失い・音の立て直し)は Advance が呼ばれず、角度は止まった曲の時刻のまま(投影だけ作り直す)。
// 予約した音は ResetSounds で取り消し、動き出したら今の曲の時刻で予約し直す。
public sealed class ReverseMode : MonoBehaviour
{
    // 剣の入力の回転がこれより大きく跳んだら、F3 の段の切り替え(または見失いからの戻り)として履歴を捨てる。
    public const float InputJumpDegrees = 20f;

    public ReverseChartData Data { get; private set; }
    public WorldTurn Turn { get; private set; }
    public MirrorGateView Gates { get; private set; }
    public ReverseSfx Sfx { get; private set; }
    public double OffsetSeconds { get; private set; }
    // ちょうつがいの金(最初の回転の頭の金)と、最後の一回転が着く時刻(曲の時計の秒。無ければ NaN)。
    public double HingeSeconds { get; private set; } = double.NaN;
    public double FinalSeconds { get; private set; } = double.NaN;
    public bool Ended => ended;
    // テスト用: 大きな光を頼んだ回数(受け付けられたかは問わない)、剣の履歴を捨てた回数、筋を出したノーツの数。
    public int FlashRequests { get; private set; }
    public int SaberResets { get; private set; }
    public int StreaksStarted { get; private set; }

    NoteSpawner spawner;
    readonly SaberCutJudge[] judges = new SaberCutJudge[2];
    readonly float[] lastInput = { float.NaN, float.NaN };
    CameraImuJudgment imu;
    readonly Dictionary<CuttableNote, uint> streaked = new Dictionary<CuttableNote, uint>(64);
    readonly List<CuttableNote> stale = new List<CuttableNote>(16);
    Action<CuttableNote> onSpawned;
    Action onChartReset;
    Action<CuttableNote, Vector3, Vector3> onHingeCut;
    double previousTime = double.NaN;
    bool ended;

    // clock は曲の時計(秒。止まっている・始まっていないときは NaN。剣の入力の回転に使う)。camera を省くと Camera.main
    // (テストは自分のカメラを渡す。ほかのテストが残した MainCamera を拾わないため)。sounds が false なら音を作らない。
    public static ReverseMode Create(Transform parent, ChartData chart, NoteSpawner spawner, Func<double> clock,
        SaberCutJudge first, SaberCutJudge second, CameraImuJudgment imu, Camera camera = null, bool sounds = true)
    {
        if (chart == null || !ReverseRules.Enabled(chart.reverse)) return null;
        var go = new GameObject("ReverseMode");
        go.transform.SetParent(parent, false);
        var mode = go.AddComponent<ReverseMode>();
        mode.Initialize(chart, spawner, clock, first, second, imu, camera, sounds);
        return mode;
    }

    void Initialize(ChartData chart, NoteSpawner noteSpawner, Func<double> clock, SaberCutJudge first, SaberCutJudge second,
        CameraImuJudgment imuJudgment, Camera camera, bool sounds)
    {
        Data = chart.reverse;
        spawner = noteSpawner;
        imu = imuJudgment;
        judges[0] = first;
        judges[1] = second;
        OffsetSeconds = spawner != null ? spawner.TotalOffsetSeconds : chart.offsetMs / 1000.0;
        float judgeZ = spawner != null ? spawner.judgeZ : 0f;
        Turn = new WorldTurn(Data, clock, OffsetSeconds, judgeZ, camera);
        Gates = new MirrorGateView(transform, Data, spawner != null ? spawner.FloorGuide : null);
        if (sounds) Sfx = new ReverseSfx(transform, ReverseSfx.BuildEvents(chart, OffsetSeconds));

        if (Data.turns != null && Data.turns.Count > 0)
        {
            var tempo = new ChartTempoMap(chart.bpm, chart.beatZeroMs, chart.editorTempoMap);
            var firstTurn = Data.turns[0];
            if (chart.notes != null)
                foreach (var note in chart.notes)
                    if (note != null && Math.Abs(note.time - firstTurn.time) <= 1f
                        && string.Equals(note.color, "gold", StringComparison.OrdinalIgnoreCase))
                    {
                        HingeSeconds = note.time / 1000.0 + OffsetSeconds;
                        break;
                    }
            var last = Data.turns[Data.turns.Count - 1];
            if (!ReverseRules.UpsideDown(last.degrees) && Data.turns.Count > 1)
                FinalSeconds = ReverseRules.FinalHitMs(last, tempo) / 1000.0 + OffsetSeconds;
        }

        onSpawned = HandleSpawned;
        onChartReset = EndPlay;
        onHingeCut = HandleHingeCut;
        if (spawner != null)
        {
            spawner.OnNoteSpawned += onSpawned;
            spawner.OnChartReset += onChartReset;
            foreach (var note in spawner.LiveNotes) HandleSpawned(note);
        }
    }

    void HandleSpawned(CuttableNote note)
    {
        if (ended || note == null || !ReverseRules.Finite(HingeSeconds)) return;
        // 購読は出現ごとに ResetForSpawn が外す。
        if (note.IsGold && Math.Abs(note.HitTime - HingeSeconds) < .002) note.OnCut += onHingeCut;
    }

    // ちょうつがいの金を切った瞬間に、画面の縁の光を1回(世界が回り始める合図)。切りそこねても時刻どおりに回る。
    void HandleHingeCut(CuttableNote note, Vector3 point, Vector3 velocity)
    {
        if (ended || note == null || !note.IsCut || note.IsMissed) return;
        Flash(LargeFlashTint.Gold, .35f, .3f);
    }

    void Flash(LargeFlashTint tint, float alpha, float seconds)
    {
        FlashRequests++;
        var excitement = PlayExcitement.Current;
        if (excitement != null) excitement.TryLargeFlash(LargeFlashKind.Edge, tint, alpha, seconds);
    }

    // 角度と投影(と F3 の暗転)。GamePlayManager がフレームの最初に呼ぶ(大きな光の描画と保護の矩形・復帰の表示・判定が、
    // このコマの同じ投影を使う)。一時停止中(剣の見失い・音の鳴らし直しを待つ間)も止まった曲の時刻で呼び、画面の比が変わっても
    // 投影を作り直す。剣の入力の回転が跳んだら(F3 の段の切り替え・見失った剣の戻り)、剣とカメラの履歴を捨てる。
    public void ApplyView(double songTime)
    {
        if (ended || !ReverseRules.Finite(songTime)) return;
        Turn.Apply(songTime);
        ResetSabersOnInputJump();
    }

    // 門・筋・音・光(曲が進んでいる間だけ、GamePlayManager の判定の前)。
    public void Advance(double songTime)
    {
        if (ended || !ReverseRules.Finite(songTime)) return;
        double seconds = songTime - OffsetSeconds;
        TrackCrossings(songTime);
        float speed = spawner != null ? spawner.Speed : 0f;
        double approach = spawner != null ? spawner.approachTime : ReverseRules.DefaultApproach;
        float judgeZ = spawner != null ? spawner.judgeZ : 0f;
        Gates.Draw(seconds, approach, speed, judgeZ);
        Sfx?.Tick(songTime);
        FireTimedFlashes(songTime);
        previousTime = songTime;
    }

    // 角度と投影のあとに、門・音・光を進める(テストなど、1か所から進めるとき)。
    public void Tick(double songTime)
    {
        ApplyView(songTime);
        Advance(songTime);
    }

    // F3 で天地返しが段で切り替わる(または見失った剣が逆さの間に戻る)と、剣の位置が一瞬で軸の反対側へ跳ぶ。
    // その動きを振りと数えないよう、剣の速さの履歴(SaberTracker)とカメラの履歴(IMU の照合)を捨てる。
    // Bridge は GamePlayManager より先に動くので、このコマの transform.position はもう回したあとの位置。
    void ResetSabersOnInputJump()
    {
        for (int i = 0; i < judges.Length; i++)
        {
            var judge = judges[i];
            var bridge = judge != null ? judge.bladeProvider : null;
            if (bridge == null) { lastInput[i] = float.NaN; continue; }
            float applied = bridge.AppliedTurnDegrees;
            if (!float.IsNaN(lastInput[i]) && Mathf.Abs(Mathf.DeltaAngle(lastInput[i], applied)) > InputJumpDegrees)
            {
                if (judge.saber != null) judge.saber.ResetTo(judge.saber.transform.position);
                if (imu != null) imu.ResetSession();
                // 刃の残光も跳んだ所から描き直す(画面を横切る筋を引かない)。
                if (bridge.BladeVisual != null) bridge.BladeVisual.Clear();
                SaberResets++;
            }
            lastInput[i] = applied;
        }
    }

    // 鏡のノーツが門を通った(残り時間が 0.5 秒を切った)ら、通った所(鏡像の x)から出た所(本当の x)へ筋を引く。
    void TrackCrossings(double songTime)
    {
        if (spawner == null) return;
        var live = spawner.LiveNotes;
        for (int i = 0; i < live.Count; i++)
        {
            var note = live[i];
            if (note == null || !note.IsMirror || note.IsCut || note.IsMissed) continue;
            double remaining = note.HitTime - songTime;
            if (remaining > ReverseRules.CrossSeconds || remaining <= ReverseRules.CrossSeconds - ReverseRules.StreakSeconds) continue;
            if (streaked.TryGetValue(note, out uint version) && version == note.SpawnVersion) continue;
            streaked[note] = note.SpawnVersion;
            StreaksStarted++;
            var visuals = note.Visuals;
            Color color = visuals != null ? visuals.DisplayColor : SaberHandHelper.HandColor(note.RequiredHand);
            if (note.IsGold) color = UISkinPalette.NoteGold;
            Vector2 landing = note.LandingPosition;
            Gates.AddStreak(note.HitTime - ReverseRules.CrossSeconds - OffsetSeconds, -landing.x, landing.x, landing.y, color);
        }
        if (streaked.Count > 192)
        {
            stale.Clear();
            foreach (var pair in streaked) if (pair.Key == null || !pair.Key.isActiveAndEnabled) stale.Add(pair.Key);
            foreach (var note in stale) streaked.Remove(note);
            stale.Clear();
        }
    }

    // 門が割れるときの縁の光と、最後の一回転が着いたときの大きな光(どちらも予算の中)。
    void FireTimedFlashes(double songTime)
    {
        if (!ReverseRules.Finite(previousTime) || songTime <= previousTime || songTime - previousTime > .5) return;
        if (Data?.gates != null)
            foreach (var gate in Data.gates)
                if (Crossed(gate.endTime / 1000.0 + OffsetSeconds, songTime)) Flash(LargeFlashTint.White, .25f, .3f);
        if (ReverseRules.Finite(FinalSeconds) && Crossed(FinalSeconds, songTime)) Flash(LargeFlashTint.White, .5f, .35f);
    }

    bool Crossed(double at, double songTime) => previousTime < at && songTime >= at;

    // 曲が止まった・音の出力先が変わったとき(GamePlayManager)。予約した音を取り消す(再開のあと今の時刻から予約し直す)。
    public void ResetSounds()
    {
        if (!ended) Sfx?.ResetSounds();
    }

    // 終わり・中断・破棄・譜面の差し替え。投影と入力の写し・床の塗り分け・音を必ず戻す。
    public void EndPlay()
    {
        if (ended) return;
        ended = true;
        if (spawner != null)
        {
            if (onSpawned != null) spawner.OnNoteSpawned -= onSpawned;
            if (onChartReset != null) spawner.OnChartReset -= onChartReset;
            foreach (var note in spawner.LiveNotes) if (note != null && onHingeCut != null) note.OnCut -= onHingeCut;
        }
        Turn?.Dispose();
        Gates?.Dispose();
        Sfx?.Dispose();
        Sfx = null;
        streaked.Clear();
        spawner = null;
        judges[0] = judges[1] = null;
        imu = null;
    }

    void OnDisable() => EndPlay();
    void OnDestroy() => EndPlay();
}
