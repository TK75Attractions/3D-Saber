using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 闘牛の裏譜面(アンダルシアの「T」、2026-10-26)の画面と配線。決まりと進み具合は BullRules / BullSession。
// ・突進: warn 秒前から道を赤い帯と向きの山形で示し、横切るあいだは角2本の赤い彗星が走る。
// ・弾: ノーツと同じく奥から飛んでくる橙の玉(暗い芯・明るい縁)。着地点に薄い輪を出し、届く0.3秒前から縮めて知らせる。
// ・しびれ: 刃が灰色になり、刃の真ん中のまわりを黄色い星3つが回る。外側の16個の点が2秒で1つずつ消える(残り時間)。
//   頭の上を回る星はしびれの定番の印(ストリートファイターの「ピヨり」、スマブラ・モンハンの気絶)。解けたら白い輪と短い音。
// 文字は出さない。曲の時計で動くもの(突進・弾・当たり)は本編の GamePlayManager.Update が Tick で進め、
// 星と点は剣について LateUpdate で描く(曲が止まっている間はしびれの残りも止まる)。
public sealed class BullfightMode : MonoBehaviour
{
    const int QueueBand = 3016, QueueShot = 3022, QueueBull = 3024, QueueFx = 3026, QueueStun = 3034;
    const float PlaneZ = -.03f, StunZ = -.2f, SpawnZ = 20f, MarkerTighten = .3f;
    const int StunDots = 16;
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly Color BullColor = new Color(1f, .32f, .18f), BullCore = new Color(1f, .9f, .8f);
    static readonly Color ShotColor = new Color(1f, .56f, .14f), ShotCore = new Color(.16f, .05f, .02f);
    static readonly Color StarColor = new Color(1f, .9f, .25f), StunBlade = new Color(.45f, .45f, .5f);

    public BullSession Session { get; private set; }
    NoteSpawner spawner;
    readonly SaberCutJudge[] judges = new SaberCutJudge[2];       // 0 = 右手(赤)、1 = 左手(青)
    readonly SaberInputBridge[] bridges = new SaberInputBridge[2];
    readonly Color[] bladeColors = new Color[2];
    readonly bool[] stunShown = new bool[2];
    readonly List<BullEvent> events = new List<BullEvent>();
    Material material;
    MaterialPropertyBlock block;
    AudioSource audioSource;
    AudioClip stunClip, recoverClip, splitClip;
    // 大玉は弾と同じ橙の仲間で濃く(赤ノーツと見分けるため、赤には寄せない)。
    static readonly Color BigColor = new Color(1f, .4f, .06f);
    readonly HashSet<BullSplitGroup> splitBursts = new HashSet<BullSplitGroup>();
    public int SplitBurstCount { get; private set; }
    double lastJudgeTime = double.NegativeInfinity;
    bool ended;

    sealed class ChargeView { public BullCharge charge; public Transform root, band, bull, trail; public MeshRenderer bandR, glowR, coreR, hornR, trailR; public MeshRenderer[] chevrons; }
    sealed class ShotView { public BullShot shot; public Transform body, marker; public MeshRenderer glowR, coreR, rimR, markerR; public MeshRenderer[] papers; public bool burst; }
    sealed class StunView { public Transform root; public Transform[] stars; public MeshRenderer[] starR, dotR; }
    sealed class Fx { public Transform t; public MeshRenderer r; public float age, life, from, to; public Color color; }
    readonly List<ChargeView> chargeViews = new List<ChargeView>();
    readonly List<ShotView> shotViews = new List<ShotView>();
    readonly StunView[] stunViews = new StunView[2];
    readonly List<Fx> effects = new List<Fx>();
    int nextCharge, nextShot;

    public int StunCount => Session != null ? Session.StunCount : 0;
    public double LastTime => Session != null ? Session.LastTime : double.NegativeInfinity;

    // テストから刃の位置を差し替える口(PlayMode テストはカメラの座標を送れないため)。null を返した手はふだんどおり剣から読む。
    public System.Func<SaberHand, BladeSample?> BladeOverride { get; set; }

    public static BullfightMode Create(Transform parent, BullChartData chart, NoteSpawner spawner, SaberCutJudge judgeA, SaberCutJudge judgeB)
    {
        var go = new GameObject("BullfightMode");
        go.transform.SetParent(parent, false);
        var mode = go.AddComponent<BullfightMode>();
        mode.Initialize(chart, spawner, judgeA, judgeB);
        return mode;
    }

    public void Initialize(BullChartData chart, NoteSpawner noteSpawner, SaberCutJudge judgeA, SaberCutJudge judgeB)
    {
        spawner = noteSpawner;
        Session = new BullSession(chart, noteSpawner != null ? noteSpawner.TotalOffsetSeconds : 0);
        foreach (var judge in new[] { judgeA, judgeB })
        {
            if (judge == null) continue;
            int i = judge.hand == SaberHand.Left ? 1 : 0;
            judges[i] = judge;
            bridges[i] = judge.bladeProvider;
            if (bridges[i] != null) bladeColors[i] = bridges[i].bladeColor;
        }
        var shader = Resources.Load<Shader>("Effects/NoteGuide") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
        material = new Material(shader) { name = "Bullfight", hideFlags = HideFlags.DontSave };
        block = new MaterialPropertyBlock();
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false; audioSource.spatialBlend = 0;
        stunViews[0] = BuildStun("RightStun");
        stunViews[1] = BuildStun("LeftStun");
    }

    public bool IsStunned(SaberHand hand) => Session != null && Session.IsStunned(hand, lastJudgeTime);

    BladeSample SampleFor(SaberHand hand)
    {
        var forced = BladeOverride?.Invoke(hand);
        if (forced.HasValue) return forced.Value;
        var bridge = bridges[hand == SaberHand.Left ? 1 : 0];
        if (bridge == null || !bridge.isActiveAndEnabled || !bridge.HasBlade) return default;
        bool fresh = bridge.UsingMouseFallback;
        var input = InputPoint.Instance;
        if (!fresh && input != null)
            fresh = bridge.stickIndex == 2 ? input.IsRecentlyActive2(AimDodgeMode.AimFreshSeconds) : input.IsRecentlyActive(AimDodgeMode.AimFreshSeconds);
        if (!fresh) return default;
        Vector3 a = bridge.WorldEndA, b = bridge.WorldEndB;
        return new BladeSample(new Vector2(a.x, a.y), new Vector2(b.x, b.y));
    }

    // 曲の時計 songTime(見た目)と判定の時計 judgeTime(カメラの遅れを引いた時刻)で進める。
    public void Tick(double songTime, double judgeTime)
    {
        if (Session == null || ended) return;
        var red = SampleFor(SaberHand.Right);
        var blue = SampleFor(SaberHand.Left);
        double approach = spawner != null ? spawner.approachTime : 1;
        Session.PlaceFollowers(songTime, approach, red, blue);
        SpawnViews(songTime, approach);
        events.Clear();
        Session.Tick(judgeTime, red, blue, events);
        lastJudgeTime = judgeTime;
        foreach (var e in events) Handle(e);
        UpdateCharges(songTime);
        UpdateShots(songTime, approach);
    }

    // 曲が終わったら、しびれを解いて刃の色を戻す。
    public void EndPlay()
    {
        ended = true;
        for (int i = 0; i < 2; i++) SetStunned(i, false);
        foreach (var v in chargeViews) if (v.root != null) Discard(v.root.gameObject);
        foreach (var v in shotViews) { if (v.body != null) Discard(v.body.gameObject); if (v.marker != null) Discard(v.marker.gameObject); }
        chargeViews.Clear(); shotViews.Clear();
    }

    void Handle(BullEvent e)
    {
        int i = e.Hand == SaberHand.Left ? 1 : 0;
        if (e.Kind == BullEventKind.Stun)
        {
            SetStunned(i, true);
            var at = new Vector3(e.Position.x, e.Position.y, PlaneZ);
            AddFx(AimMeshes.Ring(.7f), at, .2f, 1.1f, .35f, BullColor);
            AddFx(BullMeshes.Star(), at, .05f, .55f, .3f, StarColor);
            Play(ref stunClip, CreateStunClip, .9f);
        }
        else
        {
            SetStunned(i, false);
            var bridge = bridges[i];
            if (bridge != null)
            {
                Vector3 m = (bridge.WorldEndA + bridge.WorldEndB) * .5f;
                AddFx(AimMeshes.Ring(.8f), new Vector3(m.x, m.y, StunZ), .2f, .9f, .3f, Color.white);
            }
            Play(ref recoverClip, CreateRecoverClip, .6f);
        }
    }

    void SetStunned(int i, bool stunned)
    {
        if (judges[i] != null) judges[i].Stunned = stunned;
        if (bridges[i] != null) bridges[i].SetBladeColor(stunned ? StunBlade : bladeColors[i]);
        stunShown[i] = stunned;
        if (!stunned && stunViews[i] != null) stunViews[i].root.gameObject.SetActive(false);
    }

    void Play(ref AudioClip clip, System.Func<AudioClip> make, float volume)
    {
        if (audioSource == null) return;
        if (clip == null) clip = make();
        var live = ProceduralSfx.Revive(ref clip);
        if (live != null) audioSource.PlayOneShot(live, volume);
    }

    // ---- 突進 ----

    void SpawnViews(double songTime, double approach)
    {
        var charges = Session.Charges;
        while (nextCharge < charges.Count && charges[nextCharge].WarnFrom <= songTime)
            chargeViews.Add(BuildCharge(charges[nextCharge++]));
        var shots = Session.Shots;
        while (nextShot < shots.Count && shots[nextShot].Time - approach <= songTime)
            shotViews.Add(BuildShot(shots[nextShot++]));
    }

    ChargeView BuildCharge(BullCharge charge)
    {
        var v = new ChargeView { charge = charge };
        v.root = new GameObject("Charge").transform;
        v.root.SetParent(transform, false);
        v.band = new GameObject("Lane").transform;
        v.band.SetParent(v.root, false);
        v.band.position = new Vector3(0, charge.Y, PlaneZ + .01f);
        v.band.localScale = new Vector3(BullRules.ChargeSpan * 2f, BullRules.ChargeRadius * 2f, 1);
        v.bandR = Renderer(v.band, "Band", BullMeshes.Rect(), QueueBand);
        v.chevrons = new MeshRenderer[3];
        for (int k = 0; k < 3; k++)
        {
            var c = new GameObject("Chevron").transform;
            c.SetParent(v.root, false);
            c.localScale = new Vector3(charge.Dir * BullRules.ChargeRadius * .9f, BullRules.ChargeRadius * .9f, 1);
            v.chevrons[k] = Renderer(c, "Shape", BullMeshes.Chevron(), QueueBand + 1);
        }
        v.trail = new GameObject("Trail").transform;
        v.trail.SetParent(v.root, false);
        v.trailR = Renderer(v.trail, "Shape", BullMeshes.Trail(), QueueBull - 1);
        v.bull = new GameObject("Bull").transform;
        v.bull.SetParent(v.root, false);
        v.glowR = Renderer(v.bull, "Glow", AimMeshes.Glow(), QueueBull);
        v.glowR.transform.localScale = Vector3.one * 2.1f;
        v.coreR = Renderer(v.bull, "Core", AimMeshes.Disc(), QueueBull + 1);
        v.hornR = Renderer(v.bull, "Horns", BullMeshes.Horns(), QueueBull + 1);
        v.hornR.transform.localScale = new Vector3(charge.Dir, 1, 1);
        return v;
    }

    void UpdateCharges(double songTime)
    {
        for (int i = chargeViews.Count - 1; i >= 0; i--)
        {
            var v = chargeViews[i];
            var c = v.charge;
            if (songTime > c.End + .3)
            {
                Discard(v.root.gameObject);
                chargeViews.RemoveAt(i);
                continue;
            }
            bool crossing = songTime >= c.Start && songTime <= c.End;
            // 道: 知らせの間は脈打つ薄い赤、横切る間は少し濃く、抜けたら消えていく。
            float warn = (float)System.Math.Max(0, System.Math.Min(1, (songTime - c.WarnFrom) / System.Math.Max(.01, c.Start - c.WarnFrom)));
            float pulse = .5f + .5f * Mathf.Sin((float)songTime * 12f);
            float bandAlpha = songTime < c.Start ? (.10f + .08f * pulse) * Mathf.Min(1, warn * 4) : crossing ? .22f : .22f * (1f - (float)((songTime - c.End) / .3));
            Tint(v.bandR, BullColor, bandAlpha);
            for (int k = 0; k < v.chevrons.Length; k++)
            {
                // 山形は進む向きへ流れる(知らせの間だけ)。
                float phase = Mathf.Repeat((float)songTime * 1.6f + k / 3f, 1f);
                float x = c.Dir * (-2.4f + 4.8f * phase);
                v.chevrons[k].transform.parent.position = new Vector3(x, c.Y, PlaneZ);
                Tint(v.chevrons[k], BullColor, songTime < c.Start ? .55f * Mathf.Sin(phase * Mathf.PI) * Mathf.Min(1, warn * 3) : 0);
            }
            if (crossing)
            {
                Vector2 p = BullRules.ChargePosition(c, songTime);
                v.bull.position = new Vector3(p.x, p.y, PlaneZ - .02f);
                v.bull.localScale = Vector3.one * BullRules.ChargeRadius;
                Tint(v.glowR, BullColor, .85f);
                Tint(v.coreR, BullCore, 1f);
                Tint(v.hornR, Color.white, 1f);
                v.trail.position = new Vector3(p.x, p.y, PlaneZ - .01f);
                v.trail.localScale = new Vector3(c.Dir * BullRules.ChargeRadius * 4f, BullRules.ChargeRadius, 1);
                Tint(v.trailR, BullColor, .7f);
            }
            else
            {
                Tint(v.glowR, BullColor, 0); Tint(v.coreR, BullCore, 0); Tint(v.hornR, Color.white, 0); Tint(v.trailR, BullColor, 0);
            }
        }
    }

    // ---- 弾 ----

    ShotView BuildShot(BullShot shot)
    {
        var v = new ShotView { shot = shot };
        v.body = new GameObject("Shot").transform;
        v.body.SetParent(transform, false);
        if (shot.Kind == "spear")
        {
            // バンデリージャ: 明るい柄、先端、片側の端に赤黄の飾り紙。向きは固定する。
            v.body.localRotation = Quaternion.Euler(0, 0, shot.Angle % 360f);
            // 投影でも細い柄を見失わないよう、柄に沿った細長い橙の光を下に敷く。柄の太さは当たりと同じ。
            v.glowR = Renderer(v.body, "Glow", AimMeshes.Glow(), QueueShot);
            v.glowR.transform.localScale = new Vector3(BullRules.SpearHalf * 2.4f, BullRules.SpearRadius * 6, 1);
            v.rimR = Renderer(v.body, "Shaft", BullMeshes.Rect(), QueueShot + 1);
            v.rimR.transform.localScale = new Vector3(BullRules.SpearHalf * 2, BullRules.SpearRadius * 2, 1);
            v.coreR = Renderer(v.body, "Point", BullMeshes.Pennant(), QueueShot + 2);
            v.coreR.transform.localPosition = new Vector3(-BullRules.SpearHalf + .11f, 0, 0);
            v.coreR.transform.localRotation = Quaternion.Euler(0, 0, -90);
            v.coreR.transform.localScale = Vector3.one * .5f;
            v.papers = new MeshRenderer[3];
            for (int k = 0; k < v.papers.Length; k++)
            {
                v.papers[k] = Renderer(v.body, "Paper" + k, BullMeshes.Pennant(), QueueShot + 2);
                v.papers[k].transform.localPosition = new Vector3(.36f - k * .14f, 0, 0);
                v.papers[k].transform.localRotation = Quaternion.Euler(0, 0, k % 2 == 0 ? 0 : 180);
            }
        }
        else
        {
            v.glowR = Renderer(v.body, "Glow", AimMeshes.Glow(), QueueShot);
            v.glowR.transform.localScale = Vector3.one * 1.8f;
            v.rimR = Renderer(v.body, "Spike", AimMeshes.Spike(), QueueShot + 1);
            v.coreR = Renderer(v.body, "Core", AimMeshes.Disc(), QueueShot + 2);
        }
        v.marker = new GameObject("ShotMarker").transform;
        v.marker.SetParent(transform, false);
        v.markerR = Renderer(v.marker, shot.Kind == "spear" ? "Band" : "Ring",
            shot.Kind == "spear" ? BullMeshes.Rect() : AimMeshes.Ring(.82f), QueueShot - 1);
        return v;
    }

    void UpdateShots(double songTime, double approach)
    {
        for (int i = shotViews.Count - 1; i >= 0; i--)
        {
            var v = shotViews[i];
            var s = v.shot;
            double remaining = s.Time - songTime;
            float radius = BullRules.Radius(s);
            Color color = s.Kind == "big" ? BigColor : ShotColor;
            if (remaining < -.12)
            {
                if (!v.burst)
                {
                    v.burst = true;
                    AddFx(AimMeshes.Ring(.75f), new Vector3(s.Position.x, s.Position.y, PlaneZ),
                        radius, radius * 3f, .25f, color);
                }
                Discard(v.body.gameObject);
                Discard(v.marker.gameObject);
                shotViews.RemoveAt(i);
                continue;
            }
            float z = (float)(SpawnZ * System.Math.Max(0, remaining) / System.Math.Max(.05, approach));
            Vector2 p = BullRules.ShotPosition(s, songTime);
            v.body.position = new Vector3(p.x, p.y, z + PlaneZ);
            bool splitting = s.Split != null && s.Lead > 0;
            bool joined = splitting && remaining > BullRules.CurveLead(s);
            // 子は同じ所に重ね、代表だけ描く。半透明の光が人数分重なって明るくなるのを防ぐ。
            v.body.gameObject.SetActive(!joined || s.Split.Leader == s);
            v.marker.gameObject.SetActive(!joined);
            if (splitting && !joined && splitBursts.Add(s.Split))
            {
                SplitBurstCount++;
                AddFx(AimMeshes.Ring(.78f), new Vector3(s.From.x, s.From.y, z + PlaneZ), .08f, .38f, .16f, ShotColor);
                Play(ref splitClip, CreateSplitClip, .28f);
            }
            if (s.Kind == "spear")
            {
                Tint(v.glowR, ShotColor, .45f);
                Tint(v.rimR, new Color(1f, .92f, .65f), 1);
                Tint(v.coreR, Color.white, 1);
                for (int k = 0; k < v.papers.Length; k++)
                    Tint(v.papers[k], k % 2 == 0 ? BullColor : StarColor, 1);
            }
            else
            {
                v.body.localScale = Vector3.one * radius * 1.4f * (joined ? 1.6f : 1f);
                v.rimR.transform.localRotation = Quaternion.Euler(0, 0, (float)songTime * (s.Kind == "big" ? 55f : 160f));
                float pulse = joined ? .5f + .5f * Mathf.Sin((float)songTime * 40f) : 0;
                v.coreR.transform.localScale = Vector3.one * (joined ? Mathf.Lerp(.28f, .48f, pulse) : .3f);
                Tint(v.glowR, color, .5f);
                Tint(v.rimR, color, 1f);
                Tint(v.coreR, joined ? Color.Lerp(ShotCore, ShotColor, pulse * .7f) : ShotCore, 1f);
            }
            float tighten = Mathf.Clamp01((float)(1 - remaining / MarkerTighten));
            v.marker.position = new Vector3(s.Position.x, s.Position.y, PlaneZ);
            if (s.Kind == "spear")
            {
                // 当たりの線分と同じ向きの帯。長さは変えず、最後の0.3秒で濃くする。
                v.marker.localRotation = Quaternion.Euler(0, 0, s.Angle % 360f);
                v.marker.localScale = new Vector3((BullRules.SpearHalf + BullRules.SpearRadius) * 2,
                    BullRules.SpearRadius * 2, 1);
                Tint(v.markerR, color, Mathf.Lerp(.08f, .65f, tighten));
            }
            else
            {
                v.marker.localScale = Vector3.one * Mathf.Lerp(radius * 2.2f, radius * 1.1f, tighten);
                Tint(v.markerR, color, Mathf.Lerp(.2f, .65f, tighten));
            }
        }
    }

    // ---- しびれの星と残り時間 ----

    StunView BuildStun(string name)
    {
        var v = new StunView { root = new GameObject(name).transform };
        v.root.SetParent(transform, false);
        v.stars = new Transform[3];
        v.starR = new MeshRenderer[3];
        for (int k = 0; k < 3; k++)
        {
            v.stars[k] = new GameObject("Star").transform;
            v.stars[k].SetParent(v.root, false);
            v.stars[k].localScale = Vector3.one * .14f;
            v.starR[k] = Renderer(v.stars[k], "Shape", BullMeshes.Star(), QueueStun + 1);
        }
        v.dotR = new MeshRenderer[StunDots];
        for (int k = 0; k < StunDots; k++)
        {
            var d = new GameObject("Dot").transform;
            d.SetParent(v.root, false);
            float a = Mathf.PI / 2 - k * Mathf.PI * 2f / StunDots;
            d.localPosition = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0) * .55f;
            d.localScale = Vector3.one * .045f;
            v.dotR[k] = Renderer(d, "Shape", AimMeshes.Disc(), QueueStun);
        }
        v.root.gameObject.SetActive(false);
        return v;
    }

    void LateUpdate()
    {
        for (int i = 0; i < 2; i++)
        {
            var v = stunViews[i];
            if (v == null) continue;
            var bridge = bridges[i];
            bool show = stunShown[i] && bridge != null && bridge.HasBlade;
            if (v.root.gameObject.activeSelf != show) v.root.gameObject.SetActive(show);
            if (!show) continue;
            Vector3 m = (bridge.WorldEndA + bridge.WorldEndB) * .5f;
            v.root.position = new Vector3(m.x, m.y, StunZ);
            float spin = Time.unscaledTime * Mathf.PI * 2f * 1.1f;
            for (int k = 0; k < 3; k++)
            {
                float a = spin + k * Mathf.PI * 2f / 3f;
                // 少しつぶれた楕円で回すと、頭の上を回る星に見える。
                v.stars[k].localPosition = new Vector3(Mathf.Cos(a) * .36f, Mathf.Sin(a) * .16f + .05f, 0);
                v.stars[k].localRotation = Quaternion.Euler(0, 0, -a * Mathf.Rad2Deg * .5f);
                Tint(v.starR[k], StarColor, 1f);
            }
            double remaining = Session != null ? Session.StunRemaining(i == 1 ? SaberHand.Left : SaberHand.Right, lastJudgeTime) : 0;
            int lit = Mathf.CeilToInt((float)(StunDots * remaining / BullRules.StunSeconds));
            for (int k = 0; k < StunDots; k++) Tint(v.dotR[k], StarColor, k < lit ? .9f : 0f);
        }
        for (int i = effects.Count - 1; i >= 0; i--)
        {
            var fx = effects[i];
            fx.age += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(fx.age / fx.life);
            if (k >= 1f) { Discard(fx.t.gameObject); effects.RemoveAt(i); continue; }
            fx.t.localScale = Vector3.one * Mathf.Lerp(fx.from, fx.to, 1 - (1 - k) * (1 - k));
            Tint(fx.r, fx.color, 1 - k);
        }
    }

    // ---- 小道具 ----

    MeshRenderer Renderer(Transform parent, string name, Mesh mesh, int queue)
    {
        var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(parent, false);
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = MaterialFor(queue);
        r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
        r.lightProbeUsage = LightProbeUsage.Off; r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        r.enabled = false;
        return r;
    }

    readonly Dictionary<int, Material> materials = new Dictionary<int, Material>();
    Material MaterialFor(int queue)
    {
        if (materials.TryGetValue(queue, out var m)) return m;
        m = new Material(material) { name = "Bullfight" + queue, hideFlags = HideFlags.DontSave, renderQueue = queue };
        materials[queue] = m;
        return m;
    }

    void Tint(MeshRenderer r, Color color, float alpha)
    {
        if (r == null) return;
        bool visible = alpha > .003f;
        if (r.enabled != visible) r.enabled = visible;
        if (!visible) return;
        color.a = Mathf.Clamp01(alpha);
        block.SetColor(BaseColorId, color);
        r.SetPropertyBlock(block);
    }

    void AddFx(Mesh mesh, Vector3 at, float from, float to, float life, Color color)
    {
        var holder = new GameObject("BullFx").transform;
        holder.SetParent(transform, false);
        holder.position = at;
        var r = Renderer(holder, "Shape", mesh, QueueFx);
        holder.localScale = Vector3.one * from;
        Tint(r, color, 1f);
        effects.Add(new Fx { t = holder, r = r, life = life, from = from, to = to, color = color });
    }

    // 分裂の音: 小さく短い「ポン」。組ごとに1回だけ鳴らす。
    static AudioClip CreateSplitClip()
    {
        const int rate = 44100;
        var data = new float[Mathf.CeilToInt(rate * .10f)];
        for (int i = 0; i < data.Length; i++)
        {
            float t = i / (float)rate;
            data[i] = .5f * Mathf.Sin(2 * Mathf.PI * (380f * t - 700f * t * t))
                * Mathf.Exp(-t * 50f) * Mathf.Clamp01(t / .003f) * Mathf.Clamp01((.10f - t) / .01f);
        }
        return ProceduralSfx.Clip("BullSplit", data, rate);
    }

    // しびれた音: 低い「ゴン」と、星が回る高い音のさざ波(0.45 秒)。
    static AudioClip CreateStunClip()
    {
        const int rate = 44100; const float seconds = .45f;
        var data = new float[Mathf.CeilToInt(rate * seconds)];
        for (int i = 0; i < data.Length; i++)
        {
            float t = i / (float)rate;
            float bonk = Mathf.Sin(2 * Mathf.PI * (190f - 120f * t) * t) * Mathf.Exp(-t * 11f);
            float twinkle = Mathf.Sin(2 * Mathf.PI * (1900f + 260f * Mathf.Sin(2 * Mathf.PI * 9f * t)) * t) * Mathf.Exp(-t * 5f) * .18f;
            data[i] = Mathf.Clamp(bonk * .75f + twinkle, -.8f, .8f);
        }
        return ProceduralSfx.Clip("BullStun", data, rate);
    }

    // しびれが解けた音: 短く明るい「チン」(0.18 秒)。
    static AudioClip CreateRecoverClip()
    {
        const int rate = 44100; const float seconds = .18f;
        var data = new float[Mathf.CeilToInt(rate * seconds)];
        for (int i = 0; i < data.Length; i++)
        {
            float t = i / (float)rate;
            data[i] = (Mathf.Sin(2 * Mathf.PI * 1568f * t) * .5f + Mathf.Sin(2 * Mathf.PI * 2349f * t) * .3f) * Mathf.Exp(-t * 22f);
        }
        return ProceduralSfx.Clip("BullRecover", data, rate);
    }

    // 再生中は Destroy、エディターのテストでは DestroyImmediate(Destroy は再生中しか使えない)。
    static void Discard(Object target)
    {
        if (target == null) return;
        if (Application.isPlaying) Destroy(target); else DestroyImmediate(target);
    }

    void OnDestroy()
    {
        for (int i = 0; i < 2; i++)
        {
            if (judges[i] != null) judges[i].Stunned = false;
            if (bridges[i] != null && stunShown[i]) bridges[i].SetBladeColor(bladeColors[i]);
        }
        foreach (var m in materials.Values) if (m != null) Discard(m);
        if (material != null) Discard(material);
        if (stunClip != null) Discard(stunClip);
        if (recoverClip != null) Discard(recoverClip);
        if (splitClip != null) Discard(splitClip);
    }
}

// 闘牛の形(頂点の色は白。Saber/Note Guide の材質で色を付ける)。大きさ 1 で作り、Transform で変える。
public static class BullMeshes
{
    static readonly Dictionary<string, Mesh> cache = new Dictionary<string, Mesh>();

    static Mesh Cached(string key, System.Action<List<Vector3>, List<Color>, List<int>> build)
    {
        if (cache.TryGetValue(key, out var mesh) && mesh != null) return mesh;
        var v = new List<Vector3>(); var c = new List<Color>(); var t = new List<int>();
        build(v, c, t);
        mesh = new Mesh { name = "Bull/" + key, hideFlags = HideFlags.DontSave };
        mesh.SetVertices(v); mesh.SetColors(c); mesh.SetTriangles(t, 0); mesh.RecalculateBounds();
        cache[key] = mesh;
        return mesh;
    }

    static void Tri(List<Vector3> v, List<Color> c, List<int> t, Vector3 a, Vector3 b, Vector3 d, Color ca, Color cb, Color cd)
    {
        int s = v.Count;
        v.Add(a); v.Add(b); v.Add(d); c.Add(ca); c.Add(cb); c.Add(cd);
        t.Add(s); t.Add(s + 1); t.Add(s + 2);
        t.Add(s); t.Add(s + 2); t.Add(s + 1);
    }

    // 道の帯: 幅1・高さ1の四角。上下の縁を少し濃く。
    public static Mesh Rect() => Cached("Rect", (v, c, t) =>
    {
        Color edge = Color.white, mid = new Color(1, 1, 1, .55f);
        int s = v.Count;
        v.Add(new Vector3(-.5f, -.5f)); v.Add(new Vector3(.5f, -.5f)); v.Add(new Vector3(.5f, 0)); v.Add(new Vector3(-.5f, 0));
        v.Add(new Vector3(-.5f, .5f)); v.Add(new Vector3(.5f, .5f));
        c.Add(edge); c.Add(edge); c.Add(mid); c.Add(mid); c.Add(edge); c.Add(edge);
        t.Add(s); t.Add(s + 2); t.Add(s + 1); t.Add(s); t.Add(s + 3); t.Add(s + 2);
        t.Add(s + 3); t.Add(s + 5); t.Add(s + 2); t.Add(s + 3); t.Add(s + 4); t.Add(s + 5);
    });

    // 向きの山形(右向き。左向きは Transform の x を反転)。
    public static Mesh Chevron() => Cached("Chevron", (v, c, t) =>
    {
        var w = Color.white;
        Tri(v, c, t, new Vector3(-.35f, .5f), new Vector3(.35f, 0), new Vector3(-.05f, .5f), w, w, w);
        Tri(v, c, t, new Vector3(-.05f, .5f), new Vector3(.35f, 0), new Vector3(.65f, 0), w, w, w);
        Tri(v, c, t, new Vector3(-.35f, -.5f), new Vector3(-.05f, -.5f), new Vector3(.35f, 0), w, w, w);
        Tri(v, c, t, new Vector3(-.05f, -.5f), new Vector3(.65f, 0), new Vector3(.35f, 0), w, w, w);
    });

    // 角2本(右向きに進む牛の前。左向きは Transform の x を反転)。
    public static Mesh Horns() => Cached("Horns", (v, c, t) =>
    {
        var w = Color.white;
        Tri(v, c, t, new Vector3(.55f, .35f), new Vector3(.75f, .55f), new Vector3(1.25f, 1.05f), w, w, w);
        Tri(v, c, t, new Vector3(.55f, .35f), new Vector3(1.25f, 1.05f), new Vector3(.9f, .3f), w, w, w);
        Tri(v, c, t, new Vector3(.55f, -.35f), new Vector3(1.25f, -1.05f), new Vector3(.75f, -.55f), w, w, w);
        Tri(v, c, t, new Vector3(.55f, -.35f), new Vector3(.9f, -.3f), new Vector3(1.25f, -1.05f), w, w, w);
    });

    // 彗星の尾: 進む向きの後ろへ細くなって消える(右向きに進むとき左へ伸びる)。
    public static Mesh Trail() => Cached("Trail", (v, c, t) =>
    {
        Color head = Color.white, tail = new Color(1, 1, 1, 0);
        int s = v.Count;
        v.Add(new Vector3(0, .8f)); v.Add(new Vector3(0, -.8f)); v.Add(new Vector3(-1f, .15f)); v.Add(new Vector3(-1f, -.15f));
        c.Add(head); c.Add(head); c.Add(tail); c.Add(tail);
        t.Add(s); t.Add(s + 2); t.Add(s + 1); t.Add(s + 1); t.Add(s + 2); t.Add(s + 3);
        t.Add(s); t.Add(s + 1); t.Add(s + 2); t.Add(s + 1); t.Add(s + 3); t.Add(s + 2);
    });

    // 槍の飾り紙と先端。柄に沿う辺から小さな三角を下げる。
    public static Mesh Pennant() => Cached("Pennant", (v, c, t) =>
    {
        Tri(v, c, t, new Vector3(-.10f, 0), new Vector3(.10f, 0), new Vector3(0, -.22f),
            Color.white, Color.white, Color.white);
    });

    // 5つの角の星(しびれの印)。
    public static Mesh Star() => Cached("Star", (v, c, t) =>
    {
        int s = v.Count;
        v.Add(Vector3.zero); c.Add(Color.white);
        for (int i = 0; i <= 10; i++)
        {
            float a = Mathf.PI / 2 + i * Mathf.PI / 5;
            float r = i % 2 == 0 ? 1f : .45f;
            v.Add(new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0) * r); c.Add(Color.white);
        }
        for (int i = 0; i < 10; i++) { t.Add(s); t.Add(s + 1 + i); t.Add(s + 2 + i); t.Add(s); t.Add(s + 2 + i); t.Add(s + 1 + i); }
    });
}
