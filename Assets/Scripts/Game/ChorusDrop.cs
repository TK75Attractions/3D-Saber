using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// サビ入りの「ドロップ」(爽快感カタログ 山1)。stage.json の強い区間(強度 .65 以上)の入口で、
//   ・直前の1拍だけ曲を 2dB 下げ、入口で戻す
//   ・入口ちょうどに低い一撃の音を曲の時計で予約して鳴らす
//   ・ゲート枠に重ねたリングを広げ、舞台(背景)を奥へ一瞬押して戻す
// サビの間は刃の軌跡を長くし(SaberBladeVisual.ChorusBoost)、成功したカットに明るい余韻を足す(JudgmentSfx.ChorusLevel)。
// 成績に関係なく全員に出す。独自の Update は持たず、GamePlayManager の曲時計から Tick する。
// ノーツ・ゲート・床の接近ガイド・HUD・セーバーの表示・曲の時刻は動かさない。演出控えめ(LOW)では押しとリングを出さない。
public sealed class ChorusDrop : MonoBehaviour
{
    public const float DuckDb = -2f;
    public static float DuckFactor => Mathf.Pow(10f, DuckDb / 20f);
    public const float LookAheadSeconds = .15f;
    public const float RingSeconds = .55f;
    public const float RingGrow = .35f;
    public const float KickDistance = .12f;
    public const float KickSeconds = .14f;
    public const float DonVolume = .6f;

    StagePerformanceTimeline timeline;
    AudioSource songSource;
    JudgmentSfx sfx;
    Transform stage;
    Vector3 stageBase;
    Transform gate;
    float gateHalfWidth, gateHalfHeight;
    readonly List<double> entries = new List<double>();
    float beatSeconds = .5f;
    int next;
    double scheduledFor = double.NaN, lastTime = double.NaN;
    bool ducking;
    float duckBase = 1f;
    float ringAge = -1f, kickAge = -1f;
    AudioSource dropSource;
    AudioClip donClip;
    Mesh ringMesh;
    Material ringMaterial;
    MeshRenderer ringRenderer;
    readonly List<Vector3> vertices = new List<Vector3>(16);
    readonly List<Color> colors = new List<Color>(16);
    readonly List<Vector3> uvs = new List<Vector3>(16);
    readonly List<int> indices = new List<int>(24);

    public IReadOnlyList<double> Entries => entries;
    public float BeatSeconds => beatSeconds;
    public float Chorus { get; private set; }
    public bool IsDucking => ducking;
    public int ScheduledCount { get; private set; }
    public int FiredCount { get; private set; }
    public float RingAge => ringAge;
    public float KickOffset { get; private set; }
    public bool HasRing => ringMesh != null;

    public static ChorusDrop Create(Transform parent, StagePerformanceTimeline timeline, SongPlayer song, float bpm,
        Transform stage, JudgmentSfx sfx)
    {
        var go = new GameObject("ChorusDrop");
        if (parent != null) go.transform.SetParent(parent, false);
        var drop = go.AddComponent<ChorusDrop>();
        drop.Setup(timeline, song != null ? song.GetComponent<AudioSource>() : null, bpm, stage, sfx);
        return drop;
    }

    // テストからも呼ぶ。曲の AudioSource を直接渡せる。
    public void Setup(StagePerformanceTimeline song, AudioSource music, float bpm, Transform stageRoot, JudgmentSfx judgmentSfx)
    {
        ResetEffects();
        ReleaseRing();
        ScheduledCount = FiredCount = 0;
        gate = null;
        timeline = song ?? new StagePerformanceTimeline();
        songSource = music;
        sfx = judgmentSfx;
        stage = stageRoot;
        if (stage != null) stageBase = stage.position;
        beatSeconds = bpm > 0 && !float.IsInfinity(bpm) ? Mathf.Clamp(60f / bpm, .25f, .75f) : .5f;
        entries.Clear();
        if (timeline.sections != null)
            foreach (var s in timeline.sections)
            {
                if (s == null || !Finite(s.startSeconds) || !Finite(s.endSeconds) || !Finite(s.intensity) ||
                    s.startSeconds < 0 || s.endSeconds <= s.startSeconds || s.intensity < StagePerformanceTimeline.StrongIntensity) continue;
                entries.Add(s.startSeconds);
            }
        entries.Sort();
        for (int i = entries.Count - 1; i > 0; i--) if (entries[i] - entries[i - 1] < .05) entries.RemoveAt(i);
        next = 0; lastTime = double.NaN; scheduledFor = double.NaN;
        var gateObject = GameObject.Find("JudgeGate");
        var frame = gateObject != null ? gateObject.GetComponent<JudgeGateFrame>() : null;
        if (frame != null && frame.HalfWidth > 0 && frame.HalfHeight > 0)
        {
            gate = frame.transform; gateHalfWidth = frame.HalfWidth; gateHalfHeight = frame.HalfHeight;
            BuildRing();
        }
        if (Application.isPlaying && dropSource == null)
        {
            dropSource = gameObject.AddComponent<AudioSource>();
            dropSource.playOnAwake = false; dropSource.spatialBlend = 0; dropSource.volume = DonVolume;
            donClip = ProceduralSfx.Clip("ChorusDropDon", ProceduralSfx.Don());
            dropSource.clip = donClip;
        }
    }

    void BuildRing()
    {
        var shader = Resources.Load<Shader>("Effects/GameplayCutAccent");
        if (shader == null || gate == null) return;
        var go = new GameObject("ChorusDropRing");
        go.transform.SetParent(gate, false);
        ringMesh = new Mesh { name = "ChorusDropRing", hideFlags = HideFlags.DontSave };
        ringMesh.MarkDynamic();
        ringMaterial = new Material(shader) { name = "ChorusDropRing", hideFlags = HideFlags.DontSave };
        go.AddComponent<MeshFilter>().sharedMesh = ringMesh;
        ringRenderer = go.AddComponent<MeshRenderer>();
        ringRenderer.sharedMaterial = ringMaterial;
        ringRenderer.shadowCastingMode = ShadowCastingMode.Off; ringRenderer.receiveShadows = false;
        ringRenderer.lightProbeUsage = LightProbeUsage.Off; ringRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        ringRenderer.enabled = false;
    }

    public void Tick(double songTime, float deltaTime)
    {
        if (!isActiveAndEnabled || !Finite(songTime) || timeline == null) return;
        double elapsed = Finite(lastTime) ? songTime - lastTime : 0;
        // 初回とシーク・巻き戻しでは、過ぎた入口を発火しない。
        if (!Finite(lastTime) || songTime < lastTime - .05 || elapsed > .5)
        {
            if (Finite(lastTime)) ResetEffects();
            next = 0;
            while (next < entries.Count && entries[next] < songTime - .05) next++;
            scheduledFor = double.NaN;
            elapsed = 0;
        }
        lastTime = songTime;

        Chorus = timeline.EvaluateStrong(songTime);
        SaberBladeVisual.ChorusBoost = Chorus;
        if (sfx != null) sfx.ChorusLevel = Chorus;

        if (next < entries.Count)
        {
            double entry = entries[next];
            double until = entry - songTime;
            if (until > 0 && until <= beatSeconds) BeginDuck();
            if (until > 0 && until <= LookAheadSeconds && !SameTime(scheduledFor, entry))
            {
                ScheduleDon(until);
                scheduledFor = entry;
            }
            if (until <= 0)
            {
                EndDuck();
                // 処理落ちで予約できなかったときは、遅れすぎていなければすぐ鳴らす。
                if (!SameTime(scheduledFor, entry) && until > -.1) ScheduleDon(0);
                if (until >= -.1) Fire();
                next++;
            }
        }
        // 一時停止中は実時間でリングだけ進めない。LOWへの切り替えはすぐ反映する。
        if (DisplaySettings.ReducedEffects) ClearVisualEffects();
        else Animate((float)System.Math.Max(0, elapsed));
    }

    void ScheduleDon(double delay)
    {
        ScheduledCount++;
        if (dropSource == null || donClip == null) return;
        if (delay <= 0) dropSource.PlayOneShot(donClip, 1f);
        else dropSource.PlayScheduled(AudioSettings.dspTime + delay);
    }

    void Fire()
    {
        FiredCount++;
        if (DisplaySettings.ReducedEffects) return;
        if (ringMesh != null) ringAge = 0f;
        if (stage != null) { stageBase = kickAge >= 0 ? stageBase : stage.position; kickAge = 0f; }
    }

    void BeginDuck()
    {
        if (ducking || songSource == null) return;
        duckBase = songSource.volume;
        songSource.volume = duckBase * DuckFactor;
        ducking = true;
    }

    void EndDuck()
    {
        if (!ducking) return;
        if (songSource != null) songSource.volume = duckBase;
        ducking = false;
    }

    void Animate(float dt)
    {
        if (ringAge >= 0)
        {
            ringAge += dt;
            float p = ringAge / RingSeconds;
            if (p >= 1f) { ringAge = -1f; if (ringRenderer != null) ringRenderer.enabled = false; if (ringMesh != null) ringMesh.Clear(); }
            else DrawRing(p);
        }
        if (kickAge >= 0 && stage != null)
        {
            kickAge += dt;
            float p = kickAge / KickSeconds;
            if (p >= 1f) { kickAge = -1f; KickOffset = 0; stage.position = stageBase; }
            else
            {
                // 奥へ押して戻す(ランダムな揺れにはしない)。
                KickOffset = KickDistance * Mathf.Sin(Mathf.PI * p);
                stage.position = stageBase + Vector3.forward * KickOffset;
            }
        }
    }

    void DrawRing(float p)
    {
        if (ringMesh == null) return;
        float ease = 1f - (1f - p) * (1f - p) * (1f - p);
        float grow = 1f + RingGrow * ease;
        float hw = gateHalfWidth * grow, hh = gateHalfHeight * grow;
        float thick = (DisplaySettings.ProjectorMode ? .07f : .045f) * (1f + .6f * p);
        Color c = Color.Lerp(GameStageSkin.GateColor, Color.white, .4f);
        c.a = Mathf.Pow(1f - p, 2) * .85f;
        vertices.Clear(); colors.Clear(); uvs.Clear(); indices.Clear();
        const float z = -.09f;
        Band(new Vector3(-hw - thick, hh, z), new Vector3(hw + thick, hh, z), Vector3.up * thick, c);
        Band(new Vector3(-hw - thick, -hh, z), new Vector3(hw + thick, -hh, z), Vector3.up * thick, c);
        Band(new Vector3(-hw, -hh, z), new Vector3(-hw, hh, z), Vector3.right * thick, c);
        Band(new Vector3(hw, -hh, z), new Vector3(hw, hh, z), Vector3.right * thick, c);
        ringMesh.Clear();
        ringMesh.SetVertices(vertices); ringMesh.SetColors(colors); ringMesh.SetUVs(0, uvs);
        ringMesh.SetTriangles(indices, 0, true);
        ringRenderer.enabled = true;
    }

    // 線分 a→b を、side 方向へ ±thick の帯にする。uv.x を .5 に固定して角を欠けさせず、uv.y で縁をやわらかくする。
    void Band(Vector3 a, Vector3 b, Vector3 side, Color color)
    {
        int start = vertices.Count;
        vertices.Add(a - side); vertices.Add(b - side); vertices.Add(b + side); vertices.Add(a + side);
        for (int i = 0; i < 4; i++) colors.Add(color);
        uvs.Add(new Vector3(.5f, 0, 0)); uvs.Add(new Vector3(.5f, 0, 0)); uvs.Add(new Vector3(.5f, 1, 0)); uvs.Add(new Vector3(.5f, 1, 0));
        indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
        indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
    }

    // 曲の停止・終了・離脱で、音量・舞台の位置・リング・サビ中の強調を元に戻す。
    public void ResetEffects()
    {
        EndDuck();
        if (dropSource != null) dropSource.Stop();
        scheduledFor = double.NaN;
        ClearVisualEffects();
        Chorus = 0;
        SaberBladeVisual.ChorusBoost = 0;
        if (sfx != null) sfx.ChorusLevel = 0;
    }

    void ClearVisualEffects()
    {
        ringAge = -1f;
        if (ringRenderer != null) ringRenderer.enabled = false;
        if (ringMesh != null) ringMesh.Clear();
        if (kickAge >= 0 && stage != null) stage.position = stageBase;
        kickAge = -1f; KickOffset = 0;
    }

    void OnDisable() { ResetEffects(); }

    void OnDestroy()
    {
        ResetEffects();
        ReleaseRing();
        UISkinKit.SafeDestroy(donClip);
    }

    void ReleaseRing()
    {
        if (ringRenderer != null) UISkinKit.SafeDestroy(ringRenderer.gameObject);
        UISkinKit.SafeDestroy(ringMesh);
        UISkinKit.SafeDestroy(ringMaterial);
        ringRenderer = null; ringMesh = null; ringMaterial = null;
    }

    static bool SameTime(double a, double b) => Finite(a) && System.Math.Abs(a - b) < 1e-6;
    static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}
