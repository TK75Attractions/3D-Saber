using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 照準で遊ぶ裏譜面(Phobia の「避」、2026-10-24)の画面と配線。決まりと進み具合は AimDodgeRules / AimDodgeSession。
// ・剣の刃は描かず(SaberInputBridge.DrawBlade)、刃の両端の中点に照準を描く(赤=右手、青=左手)。
// ・的は判定面に現れ、外の輪が縮んで閉じる瞬間に同じ色の照準を真ん中へ合わせる。
// ・トゲは奥から飛んでくる。見えた時から着地点に当たりの輪を薄く出し、届く0.3秒前から輪を縮めて知らせる。
// ・点は ScoreManager(BeginAimScoring / RegisterAimDart / RegisterAimDamage)へ渡すので、HUD・結果画面・記録はふだんどおり動く。
// 曲の時計で動くもの(的・トゲ・判定)は本編の GamePlayManager.Update が Tick で進める。照準と光の名残は、
// 曲が止まっている間(カウントイン・剣の見失い)も LateUpdate で描く。ノーツの近くに文字は出さない。
public sealed class AimDodgeMode : MonoBehaviour
{
    // 描く順(どれも Saber/Note Guide の半透明)。照準をいちばん手前に。
    const int QueueTarget = 3020, QueueSpike = 3022, QueueFx = 3026, QueueCrosshair = 3032;
    const float CrosshairScale = .3f, PlaneZ = -.03f, CrosshairZ = -.16f, SpawnZ = 20f;
    const float TargetFadeIn = .2f, ResultSeconds = .42f, DisarmSeconds = .25f, ShatterSeconds = .3f;
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly Color SpikeEdge = new Color(1f, .36f, .84f);
    static readonly Color GrazeColor = new Color(1f, .92f, .55f);

    public AimDodgeSession Session { get; private set; }
    public bool Completed => Session == null || Session.Completed;
    public double FirstTime => Session != null ? Session.FirstTime : double.NaN;
    public double LastTime => Session != null ? Session.LastTime : 0;
    public int TargetCount => Session != null ? Session.Targets.Count : 0;

    ScoreManager score;
    SaberInputBridge redBridge, blueBridge;
    readonly List<SaberInputBridge> hiddenBlades = new List<SaberInputBridge>();
    readonly List<AimEvent> events = new List<AimEvent>();
    Material material;
    MaterialPropertyBlock block;
    AudioSource audioSource;
    AudioClip damageClip, grazeClip;
    bool ended;

    sealed class Crosshair { public Transform root; public MeshRenderer ring, dot, glow; public Color color; public float flash = 9, shake = 9; public bool valid; public double invulnerableUntil = double.NegativeInfinity; }
    readonly Crosshair[] crosshairs = new Crosshair[2];
    sealed class TargetView { public AimTarget target; public Transform root, ring; public MeshRenderer board, ringRenderer; public float resultAge = -1; }
    sealed class SpikeView { public AimSpike spike; public Transform body, marker, arming; public MeshRenderer bodyRenderer, markerRenderer, armingRenderer; public float endAge = -1; public bool shattered; }
    sealed class Fx { public Transform t; public MeshRenderer r; public float age, life, from, to; public Color color; public Vector3 velocity; }
    readonly Dictionary<AimTarget, TargetView> targetViews = new Dictionary<AimTarget, TargetView>();
    readonly Dictionary<AimSpike, SpikeView> spikeViews = new Dictionary<AimSpike, SpikeView>();
    // 外側の壁(2026-10-26)は1回に約60個のトゲが来るので、終わったトゲの絵は捨てずに使い回す。
    readonly Stack<SpikeView> spikePool = new Stack<SpikeView>();
    public int PooledSpikeViews => spikePool.Count;
    readonly List<Fx> effects = new List<Fx>();
    int nextTarget, nextSpike;

    // chart の的とトゲで始める。offsetSeconds は NoteSpawner.TotalOffsetSeconds(譜面・本人の補正込み)、endSeconds は音源の長さ。
    public static AimDodgeMode Create(Transform parent, AimChartData chart, double offsetSeconds, double endSeconds,
        ScoreManager score, SaberCutJudge judgeA, SaberCutJudge judgeB)
    {
        var go = new GameObject("AimDodgeMode");
        go.transform.SetParent(parent, false);
        var mode = go.AddComponent<AimDodgeMode>();
        mode.Initialize(chart, offsetSeconds, endSeconds, score, judgeA, judgeB);
        return mode;
    }

    public void Initialize(AimChartData chart, double offsetSeconds, double endSeconds, ScoreManager scoreManager, SaberCutJudge judgeA, SaberCutJudge judgeB)
    {
        Session = new AimDodgeSession(chart, offsetSeconds, endSeconds > 0 ? endSeconds : double.PositiveInfinity);
        score = scoreManager;
        if (score != null) score.BeginAimScoring(Session.Targets.Count);
        foreach (var judge in new[] { judgeA, judgeB })
        {
            if (judge == null || judge.bladeProvider == null) continue;
            // 決めてある手(SaberRig が棒1・棒2に付ける)。マウスで試している間の Any に引きずられない。
            if (judge.hand == SaberHand.Left) blueBridge = judge.bladeProvider; else redBridge = judge.bladeProvider;
            judge.bladeProvider.DrawBlade = false;
            hiddenBlades.Add(judge.bladeProvider);
        }
        var shader = Resources.Load<Shader>("Effects/NoteGuide") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
        material = new Material(shader) { name = "AimDodge", hideFlags = HideFlags.DontSave };
        block = new MaterialPropertyBlock();
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false; audioSource.spatialBlend = 0;
        crosshairs[0] = BuildCrosshair("RedCrosshair", SaberHandHelper.HandColor(SaberHand.Right));
        crosshairs[1] = BuildCrosshair("BlueCrosshair", SaberHandHelper.HandColor(SaberHand.Left));
    }

    // その手(Any は両方)の的・トゲが来ているか(剣を見失ったときの一時停止)。
    public bool Needs(double songTime, SaberHand hand) => Session != null && Session.Needs(songTime, hand);

    // テストから照準の位置を差し替える口(PlayMode テストはカメラの座標を送れないため)。null を返した手はふだんどおり剣から読む。
    public System.Func<SaberHand, AimHandSample?> SampleOverride { get; set; }

    AimHandSample SampleFor(SaberHand hand)
    {
        var forced = SampleOverride?.Invoke(hand);
        if (forced.HasValue) return forced.Value;
        return Sample(hand == SaberHand.Left ? blueBridge : redBridge);
    }

    // 曲の時計 songTime(見た目)と判定の時計 judgeTime(カメラの遅れを引いた時刻)で進める。
    public void Tick(double songTime, double judgeTime)
    {
        if (Session == null || ended) return;
        var red = SampleFor(SaberHand.Right);
        var blue = SampleFor(SaberHand.Left);
        Session.PlaceFollowers(songTime, red, blue);
        SpawnViews(songTime);
        events.Clear();
        Session.Tick(judgeTime, red, blue, events);
        foreach (var e in events) Handle(e, judgeTime);
        UpdateTargets(songTime);
        UpdateSpikes(songTime);
    }

    // 曲が終わったら照準を消す(終わりの演出と結果へ)。
    public void EndPlay()
    {
        ended = true;
        foreach (var c in crosshairs) if (c != null && c.root != null) c.root.gameObject.SetActive(false);
    }

    AimHandSample Sample(SaberInputBridge bridge)
    {
        if (bridge == null || !bridge.isActiveAndEnabled || !bridge.HasBlade) return default;
        bool fresh = bridge.UsingMouseFallback;
        var input = InputPoint.Instance;
        if (!fresh && input != null)
            fresh = bridge.stickIndex == 2 ? input.IsRecentlyActive2(AimFreshSeconds) : input.IsRecentlyActive(AimFreshSeconds);
        Vector3 p = bridge.transform.position;
        return new AimHandSample(fresh, new Vector2(p.x, p.y));
    }

    // この秒数より古い座標の手は、当たりにも的にも数えない(見失いを当たりにしない)。
    public const double AimFreshSeconds = .15;

    void Handle(AimEvent e, double judgeTime)
    {
        switch (e.Kind)
        {
            case AimEventKind.Dart:
                if (score != null) score.RegisterAimDart(e.Target.Tier, e.Target.Points, e.Hand);
                if (targetViews.TryGetValue(e.Target, out var tv)) tv.resultAge = 0;
                DartBurst(e.Target);
                break;
            case AimEventKind.Damage:
                if (score != null) score.RegisterAimDamage();
                if (spikeViews.TryGetValue(e.Spike, out var sv)) { sv.shattered = true; sv.endAge = 0; }
                Shatter(e.Spike.Position, e.Spike.Size);
                var c = crosshairs[e.Hand == SaberHand.Left ? 1 : 0];
                c.flash = 0; c.shake = 0; c.invulnerableUntil = Time.unscaledTimeAsDouble + AimDodgeRules.InvulnerableSeconds;
                Play(ref damageClip, CreateDamageClip, .9f);
                break;
            case AimEventKind.Graze:
                Sparks(e.Position);
                Play(ref grazeClip, () => ProceduralSfx.Clip("AimGraze", ProceduralSfx.Tick(.03f)), .35f);
                break;
            case AimEventKind.Avoided:
                if (spikeViews.TryGetValue(e.Spike, out var av)) av.endAge = 0;
                break;
        }
    }

    void Play(ref AudioClip clip, System.Func<AudioClip> make, float volume)
    {
        if (audioSource == null) return;
        if (clip == null) clip = make();
        var live = ProceduralSfx.Revive(ref clip);
        if (live != null) audioSource.PlayOneShot(live, volume);
    }

    // ---- 的 ----

    void SpawnViews(double songTime)
    {
        var targets = Session.Targets;
        while (nextTarget < targets.Count && targets[nextTarget].Time - AimDodgeRules.LeadSeconds <= songTime)
        {
            var target = targets[nextTarget++];
            if (target.Done) continue;
            targetViews[target] = BuildTarget(target);
        }
        var spikes = Session.Spikes;
        while (nextSpike < spikes.Count && spikes[nextSpike].Time - AimDodgeRules.LeadSeconds <= songTime)
        {
            var spike = spikes[nextSpike++];
            if (spike.Done) continue;
            spikeViews[spike] = BuildSpike(spike);
        }
    }

    TargetView BuildTarget(AimTarget target)
    {
        var view = new TargetView { target = target };
        view.root = new GameObject("AimTarget_" + target.Index).transform;
        view.root.SetParent(transform, false);
        view.root.position = new Vector3(target.Position.x, target.Position.y, PlaneZ);
        view.board = Renderer(view.root, "Board", AimMeshes.Board(), QueueTarget);
        view.board.transform.localScale = Vector3.one * AimDodgeRules.TargetRadius;
        view.ring = new GameObject("Closing").transform;
        view.ring.SetParent(view.root, false);
        view.ringRenderer = Renderer(view.ring, "Ring", AimMeshes.Ring(.9f), QueueTarget + 1);
        return view;
    }

    void UpdateTargets(double songTime)
    {
        List<AimTarget> finished = null;
        foreach (var pair in targetViews)
        {
            var view = pair.Value;
            var target = view.target;
            Color hand = SaberHandHelper.HandColor(target.Hand);
            float remaining = (float)(target.Time - songTime);
            float appear = Mathf.Clamp01((float)(AimDodgeRules.LeadSeconds - remaining) / TargetFadeIn);
            if (view.resultAge < 0)
            {
                // 外の輪は LeadSeconds かけて 2.6 倍から的の縁へ縮み、閉じた瞬間が狙う時刻。
                float u = Mathf.Clamp01(1f - remaining / (float)AimDodgeRules.LeadSeconds);
                float radius = AimDodgeRules.TargetRadius * Mathf.Lerp(2.6f, 1f, u);
                view.ring.localScale = Vector3.one * radius;
                Tint(view.board, hand, .9f * appear);
                Tint(view.ringRenderer, Color.Lerp(Color.white, hand, u), (.35f + .6f * u) * appear);
                continue;
            }
            view.resultAge += Time.deltaTime;
            float k = Mathf.Clamp01(view.resultAge / ResultSeconds);
            bool miss = target.Tier == JudgmentTier.Miss;
            Color resultColor = miss ? new Color(.45f, .47f, .55f) : target.Tier == JudgmentTier.Perfect ? Color.Lerp(Color.white, hand, .3f) : hand;
            view.board.transform.localScale = Vector3.one * AimDodgeRules.TargetRadius * (miss ? 1f : 1f + .18f * Mathf.Sin(k * Mathf.PI));
            Tint(view.board, resultColor, (1f - k) * (miss ? .5f : 1f));
            Tint(view.ringRenderer, resultColor, 0);
            if (k >= 1f) (finished ??= new List<AimTarget>()).Add(target);
        }
        if (finished == null) return;
        foreach (var target in finished) { Destroy(targetViews[target].root.gameObject); targetViews.Remove(target); }
    }

    void DartBurst(AimTarget target)
    {
        if (target.Tier == JudgmentTier.Miss) return;
        Color hand = SaberHandHelper.HandColor(target.Hand);
        Vector3 at = new Vector3(target.Position.x, target.Position.y, PlaneZ - .02f);
        float size = target.Tier == JudgmentTier.Perfect ? 2.4f : target.Tier == JudgmentTier.Great ? 1.8f : 1.4f;
        AddFx(AimMeshes.Ring(.86f), at, AimDodgeRules.TargetRadius, AimDodgeRules.TargetRadius * size, .4f, Color.Lerp(hand, Color.white, .35f), Vector3.zero);
        if (target.Tier == JudgmentTier.Perfect)
            AddFx(AimMeshes.Glow(), at, AimDodgeRules.TargetRadius * .8f, AimDodgeRules.TargetRadius * 2.2f, .35f, Color.Lerp(hand, Color.white, .6f), Vector3.zero);
    }

    // ---- トゲ ----

    SpikeView BuildSpike(AimSpike spike)
    {
        float contact = AimDodgeRules.ContactDistance(spike.Size);
        if (spikePool.Count > 0)
        {
            var reused = spikePool.Pop();
            reused.spike = spike; reused.endAge = -1; reused.shattered = false;
            reused.marker.position = new Vector3(spike.Position.x, spike.Position.y, PlaneZ + .01f);
            reused.marker.localScale = Vector3.one * contact;
            reused.body.localScale = Vector3.zero;
            reused.marker.gameObject.SetActive(true);
            reused.body.gameObject.SetActive(true);
            return reused;
        }
        var view = new SpikeView { spike = spike };
        view.marker = new GameObject("AimSpikeMarker_" + spike.Index).transform;
        view.marker.SetParent(transform, false);
        view.marker.position = new Vector3(spike.Position.x, spike.Position.y, PlaneZ + .01f);
        view.markerRenderer = Renderer(view.marker, "Zone", AimMeshes.Ring(.84f), QueueSpike - 1);
        view.marker.localScale = Vector3.one * contact;
        view.arming = new GameObject("Arming").transform;
        view.arming.SetParent(view.marker, false);
        view.arming.localPosition = new Vector3(0, 0, -.01f);
        view.armingRenderer = Renderer(view.arming, "Ring", AimMeshes.Ring(.8f), QueueSpike);
        view.body = new GameObject("AimSpike_" + spike.Index).transform;
        view.body.SetParent(transform, false);
        view.bodyRenderer = Renderer(view.body, "Body", AimMeshes.Spike(), QueueSpike + 1);
        return view;
    }

    void UpdateSpikes(double songTime)
    {
        List<AimSpike> finished = null;
        foreach (var pair in spikeViews)
        {
            var view = pair.Value;
            var spike = view.spike;
            float remaining = (float)(spike.Time - songTime);
            float visualRadius = AimDodgeRules.SpikeVisualRadius * spike.Size;
            float spin = (float)songTime * 140f + spike.Index * 37f;
            if (view.endAge < 0)
            {
                float z = Mathf.Max(0f, SpawnZ * remaining / (float)AimDodgeRules.LeadSeconds);
                view.body.position = new Vector3(spike.Position.x, spike.Position.y, PlaneZ + z);
                view.body.localRotation = Quaternion.Euler(0, 0, spin);
                float pulse = remaining < 0 ? 1f + .08f * Mathf.Sin((float)songTime * 40f) : 1f;
                view.body.localScale = Vector3.one * visualRadius * pulse;
                float appear = Mathf.Clamp01((float)(AimDodgeRules.LeadSeconds - remaining) / .25f);
                Tint(view.bodyRenderer, Color.white, appear);
                // 着地点の当たりの輪は見えた時から薄く、届く0.3秒前から縮む輪で「もうすぐ」を知らせる。
                Tint(view.markerRenderer, SpikeEdge, (.18f + (remaining < .3f ? .35f : 0f)) * appear);
                float arm = Mathf.Clamp01(1f - remaining / .3f);
                view.arming.localScale = Vector3.one * Mathf.Lerp(1.8f, 1f, arm);
                Tint(view.armingRenderer, SpikeEdge, remaining <= .3f ? .9f * arm : 0f);
                continue;
            }
            view.endAge += Time.deltaTime;
            if (view.shattered)
            {
                float k = Mathf.Clamp01(view.endAge / ShatterSeconds);
                view.body.position = new Vector3(spike.Position.x, spike.Position.y, PlaneZ);
                view.body.localScale = Vector3.one * visualRadius * (1f + .5f * k);
                Tint(view.bodyRenderer, Color.white, 1f - k);
                Tint(view.markerRenderer, SpikeEdge, .8f * (1f - k));
                Tint(view.armingRenderer, SpikeEdge, 0);
                if (k >= 1f) (finished ??= new List<AimSpike>()).Add(spike);
            }
            else
            {
                // よけきったトゲは縮んで消える(「解除」)。
                float k = Mathf.Clamp01(view.endAge / DisarmSeconds);
                view.body.position = new Vector3(spike.Position.x, spike.Position.y, PlaneZ);
                view.body.localRotation = Quaternion.Euler(0, 0, spin);
                view.body.localScale = Vector3.one * visualRadius * (1f - k);
                Tint(view.bodyRenderer, Color.white, 1f - k);
                Tint(view.markerRenderer, SpikeEdge, .3f * (1f - k));
                Tint(view.armingRenderer, SpikeEdge, 0);
                if (k >= 1f) (finished ??= new List<AimSpike>()).Add(spike);
            }
        }
        if (finished == null) return;
        foreach (var spike in finished)
        {
            var view = spikeViews[spike];
            view.body.gameObject.SetActive(false); view.marker.gameObject.SetActive(false);
            view.spike = null;
            spikePool.Push(view);
            spikeViews.Remove(spike);
        }
    }

    void Shatter(Vector2 at, float size)
    {
        Vector3 p = new Vector3(at.x, at.y, PlaneZ - .03f);
        AddFx(AimMeshes.Ring(.8f), p, .3f * size, 1.4f * size, .3f, SpikeEdge, Vector3.zero);
        for (int i = 0; i < 7; i++)
        {
            float a = i * Mathf.PI * 2f / 7f + .4f;
            var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
            AddFx(AimMeshes.Spike(), p + dir * .1f, .14f * size, .05f, .35f, new Color(.35f, .1f, .4f), dir * 2.6f);
        }
    }

    void Sparks(Vector2 at)
    {
        Vector3 p = new Vector3(at.x, at.y, CrosshairZ + .02f);
        for (int i = 0; i < 5; i++)
        {
            float a = i * 1.26f + Random.value;
            AddFx(AimMeshes.Disc(), p, .05f, .01f, .25f, GrazeColor, new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0) * 1.8f);
        }
    }

    // ---- 照準 ----

    Crosshair BuildCrosshair(string name, Color color)
    {
        var c = new Crosshair { color = color };
        c.root = new GameObject(name).transform;
        c.root.SetParent(transform, false);
        c.glow = Renderer(c.root, "Glow", AimMeshes.Glow(), QueueCrosshair - 1);
        c.glow.transform.localScale = Vector3.one * CrosshairScale * 1.9f;
        c.ring = Renderer(c.root, "Reticle", AimMeshes.Reticle(), QueueCrosshair);
        c.ring.transform.localScale = Vector3.one * CrosshairScale;
        c.dot = Renderer(c.root, "Core", AimMeshes.Disc(), QueueCrosshair + 1);
        c.dot.transform.localScale = Vector3.one * AimDodgeRules.CrosshairCore * .7f;
        c.root.gameObject.SetActive(false);
        return c;
    }

    void LateUpdate()
    {
        if (Session == null) return;
        if (!ended)
        {
            UpdateCrosshair(crosshairs[0], SaberHand.Right, redBridge);
            UpdateCrosshair(crosshairs[1], SaberHand.Left, blueBridge);
        }
        float dt = Time.unscaledDeltaTime;
        for (int i = effects.Count - 1; i >= 0; i--)
        {
            var fx = effects[i];
            fx.age += dt;
            float k = Mathf.Clamp01(fx.age / fx.life);
            if (fx.t == null || k >= 1f) { if (fx.t != null) Destroy(fx.t.gameObject); effects.RemoveAt(i); continue; }
            fx.t.position += fx.velocity * dt;
            fx.t.localScale = Vector3.one * Mathf.Lerp(fx.from, fx.to, 1f - (1f - k) * (1f - k));
            Tint(fx.r, fx.color, 1f - k);
        }
    }

    void UpdateCrosshair(Crosshair c, SaberHand hand, SaberInputBridge bridge)
    {
        if (c == null) return;
        var forced = SampleOverride?.Invoke(hand);
        var sample = forced ?? Sample(bridge);
        bool seen = forced.HasValue ? forced.Value.Valid : bridge != null && bridge.isActiveAndEnabled && bridge.HasBlade;
        if (!seen) { if (c.root.gameObject.activeSelf) c.root.gameObject.SetActive(false); return; }
        if (!c.root.gameObject.activeSelf) c.root.gameObject.SetActive(true);
        float dt = Time.unscaledDeltaTime;
        c.flash += dt; c.shake += dt;
        Vector3 p = forced.HasValue ? (Vector3)forced.Value.Position : bridge.transform.position;
        float shake = c.shake < .3f ? .06f * (1f - c.shake / .3f) * Mathf.Sin(c.shake * 90f) : 0f;
        c.root.position = new Vector3(p.x + shake, p.y, CrosshairZ);
        // 見えていない(古い座標)ときは薄く、当たったあとの守られている間は点滅させる。
        float alpha = sample.Valid ? 1f : .3f;
        if (Time.unscaledTimeAsDouble < c.invulnerableUntil) alpha *= .55f + .45f * Mathf.Abs(Mathf.Sin((float)Time.unscaledTimeAsDouble * 18f));
        Color ring = c.flash < .15f ? Color.white : c.color;
        Tint(c.ring, ring, alpha);
        Tint(c.dot, Color.white, alpha);
        Tint(c.glow, c.color, .45f * alpha);
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
        return r;
    }

    readonly Dictionary<int, Material> materials = new Dictionary<int, Material>();
    Material MaterialFor(int queue)
    {
        if (materials.TryGetValue(queue, out var m)) return m;
        m = new Material(material) { name = "AimDodge" + queue, hideFlags = HideFlags.DontSave, renderQueue = queue };
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

    void AddFx(Mesh mesh, Vector3 at, float from, float to, float life, Color color, Vector3 velocity)
    {
        var holder = new GameObject("AimFx").transform;
        holder.SetParent(transform, false);
        holder.position = at;
        var r = Renderer(holder, "Shape", mesh, QueueFx);
        var fx = new Fx { t = holder, r = r, life = life, from = from, to = to, color = color, velocity = velocity };
        holder.localScale = Vector3.one * from;
        Tint(r, color, 1f);
        effects.Add(fx);
    }

    // トゲに当たった音: 低い「ドッ」と、割れる短い雑音(0.32 秒)。
    static AudioClip CreateDamageClip()
    {
        const int rate = 44100; const float seconds = .32f;
        var data = new float[Mathf.CeilToInt(rate * seconds)];
        uint noise = 22222;
        float lp = 0;
        for (int i = 0; i < data.Length; i++)
        {
            float t = i / (float)rate;
            noise = noise * 1664525u + 1013904223u;
            float white = (noise >> 9) / 4194304f - 1f;
            lp += (white - lp) * .25f;
            float thud = Mathf.Sin(2 * Mathf.PI * (72f - 30f * t) * t) * Mathf.Exp(-t * 14f);
            float crack = (white - lp) * Mathf.Exp(-t * 26f);
            float buzz = Mathf.Sign(Mathf.Sin(2 * Mathf.PI * 233f * t)) * Mathf.Exp(-t * 30f) * .25f;
            data[i] = Mathf.Clamp(thud * .7f + crack * .5f + buzz, -.8f, .8f);
        }
        return ProceduralSfx.Clip("AimDamage", data, rate);
    }

    void OnDestroy()
    {
        foreach (var bridge in hiddenBlades) if (bridge != null) bridge.DrawBlade = true;
        foreach (var m in materials.Values) if (m != null) Destroy(m);
        if (material != null) Destroy(material);
        if (damageClip != null) Destroy(damageClip);
        if (grazeClip != null) Destroy(grazeClip);
    }
}

// 照準・的・トゲの形(頂点の色で塗る。Saber/Note Guide の材質で描く)。半径 1 で作り、大きさは Transform で変える。
public static class AimMeshes
{
    static readonly Dictionary<string, Mesh> cache = new Dictionary<string, Mesh>();
    const int Segments = 64;

    static Mesh Cached(string key, System.Action<List<Vector3>, List<Color>, List<int>> build)
    {
        if (cache.TryGetValue(key, out var mesh) && mesh != null) return mesh;
        var v = new List<Vector3>(); var c = new List<Color>(); var t = new List<int>();
        build(v, c, t);
        mesh = new Mesh { name = "Aim/" + key, hideFlags = HideFlags.DontSave };
        mesh.SetVertices(v); mesh.SetColors(c); mesh.SetTriangles(t, 0); mesh.RecalculateBounds();
        cache[key] = mesh;
        return mesh;
    }

    static void Annulus(List<Vector3> v, List<Color> c, List<int> t, float inner, float outer, Color color)
    {
        int start = v.Count;
        for (int i = 0; i <= Segments; i++)
        {
            float a = i * Mathf.PI * 2f / Segments;
            var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
            v.Add(d * inner); c.Add(color);
            v.Add(d * outer); c.Add(color);
        }
        for (int i = 0; i < Segments; i++)
        {
            int a = start + i * 2;
            t.Add(a); t.Add(a + 1); t.Add(a + 3);
            t.Add(a); t.Add(a + 3); t.Add(a + 2);
        }
    }

    static void Fan(List<Vector3> v, List<Color> c, List<int> t, float radius, Color center, Color rim, Vector3 offset = default)
    {
        int start = v.Count;
        v.Add(offset); c.Add(center);
        for (int i = 0; i <= Segments; i++)
        {
            float a = i * Mathf.PI * 2f / Segments;
            v.Add(offset + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0) * radius); c.Add(rim);
        }
        for (int i = 0; i < Segments; i++) { t.Add(start); t.Add(start + 1 + i); t.Add(start + 2 + i); }
    }

    static void Quad(List<Vector3> v, List<Color> c, List<int> t, Vector3 a, Vector3 b, float halfWidth, Color color)
    {
        Vector3 n = Vector3.Cross(b - a, Vector3.forward).normalized * halfWidth;
        int s = v.Count;
        v.Add(a - n); v.Add(a + n); v.Add(b + n); v.Add(b - n);
        for (int i = 0; i < 4; i++) c.Add(color);
        t.Add(s); t.Add(s + 1); t.Add(s + 2); t.Add(s); t.Add(s + 2); t.Add(s + 3);
    }

    public static Mesh Ring(float inner) => Cached("Ring" + inner.ToString("F2"), (v, c, t) => Annulus(v, c, t, inner, 1f, Color.white));
    public static Mesh Disc() => Cached("Disc", (v, c, t) => Fan(v, c, t, 1f, Color.white, Color.white));
    public static Mesh Glow() => Cached("Glow", (v, c, t) => Fan(v, c, t, 1f, Color.white, new Color(1, 1, 1, 0)));

    // ダーツの的: 薄い塗り、外の縁、INNER の境目、真ん中の BULL。半径 1 = 的の外の縁。
    public static Mesh Board() => Cached("Board", (v, c, t) =>
    {
        // 中に入れば当たり(2026-10-26 精度の段をやめた)。中の面を少し濃くし、BULL・INNER の輪は描かない。
        Fan(v, c, t, 1f, new Color(1, 1, 1, .26f), new Color(1, 1, 1, .18f));
        Annulus(v, c, t, .88f, 1f, Color.white);
    });

    // 照準: 輪と、外へ伸びる4本の目盛り。
    public static Mesh Reticle() => Cached("Reticle", (v, c, t) =>
    {
        Annulus(v, c, t, .66f, .82f, Color.white);
        foreach (var d in new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down })
            Quad(v, c, t, d * .9f, d * 1.18f, .07f, Color.white);
    });

    // トゲ: 白い16本の棘・暗い芯・赤紫の輪。色だけに頼らず形で「危ない」と分かるように。
    public static Mesh Spike() => Cached("Spike", (v, c, t) =>
    {
        int start = v.Count;
        v.Add(Vector3.zero); c.Add(Color.white);
        const int points = 16;
        for (int i = 0; i <= points * 2; i++)
        {
            float a = i * Mathf.PI / points;
            float r = i % 2 == 0 ? 1f : .6f;
            v.Add(new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0) * r); c.Add(Color.white);
        }
        for (int i = 0; i < points * 2; i++) { t.Add(start); t.Add(start + 1 + i); t.Add(start + 2 + i); }
        Fan(v, c, t, .55f, new Color(.10f, .02f, .12f), new Color(.17f, .04f, .2f));
        Annulus(v, c, t, .28f, .38f, new Color(1f, .36f, .84f));
    });
}
