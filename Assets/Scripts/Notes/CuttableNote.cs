using System.Collections.Generic;
using UnityEngine;

// 切れるノーツ。タップ・方向指定・ロングをすべてこのコンポーネントで扱う。
// ロングノーツは Cut() を RequiredCutCount 回呼ぶことで完了する。
// 部分カット時は「ひび」を表示し、最終カットで本体をスライス＋砕け散らせる。
public class CuttableNote : MonoBehaviour
{
    public bool IsCut { get; private set; }
    private bool judgeable;
    public bool IsJudgeable { get => judgeable; set { judgeable = value; if (value && gameObject.activeInHierarchy) Register(); } }
    static readonly List<CuttableNote> activeNotes = new List<CuttableNote>();
    public static IReadOnlyList<CuttableNote> ActiveNotes => activeNotes;
    public uint SpawnVersion { get; private set; }
    internal NoteFragmentPool FragmentPool;
    internal bool IsPooled;
    internal event System.Action<CuttableNote> OnRetired;
    int registryIndex = -1;
    void Register()
    {
        if (registryIndex >= 0 && registryIndex < activeNotes.Count && activeNotes[registryIndex] == this) return;
        registryIndex = activeNotes.Count; activeNotes.Add(this);
    }
    void OnEnable() { SpawnVersion++; Register(); }
    void OnDisable()
    {
        if (registryIndex < 0 || registryIndex >= activeNotes.Count || activeNotes[registryIndex] != this) return;
        int last = activeNotes.Count - 1;
        var moved = activeNotes[last]; activeNotes[registryIndex] = moved; moved.registryIndex = registryIndex;
        activeNotes.RemoveAt(last); registryIndex = -1;
    }
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetRegistry() { activeNotes.Clear(); }
    internal void ResetForSpawn()
    {
        SpawnVersion++;
        // 前のノーツに結び付いたスコア・音・演出の購読を持ち越さない。
        OnRetired?.Invoke(this);
        OnCut = null; OnJudged = null; OnMiss = null; OnPartialCut = null; OnRetired = null;
        IsCut = IsMissed = IsFinalized = judgeable = false;
        MinimumCutSpeed = 0; RequireJudgeableOnCut = DirectionVisualOnly = false;
        LastCutCorrectDirection = true; LastCutterHand = SaberHand.Any;
        LastCutSongTime = null;
        firstPiece = secondPiece = null;
        hasPendingFlash = false;
        FlowDebris = false; FlowNoteVelocity = Vector3.zero;
        lastHitPoint = Vector3.zero; lastVelocity = Vector3.right; cracksUsed = 0;
        foreach (var crack in ownedCracks) if (crack.visual != null) crack.visual.SetActive(false);
        if (countLabel != null) countLabel.gameObject.SetActive(false);
    }
    internal void Retire()
    {
        judgeable = false; OnRetired?.Invoke(this);
        if (countLabel != null) countLabel.gameObject.SetActive(false);
        if (TimingCue != null) TimingCue.HideForPool();
        gameObject.SetActive(false);
    }
    // メニュー専用の追加条件。既定値では本編の既存判定を変更しない。
    public float MinimumCutSpeed { get; set; }
    public bool RequireJudgeableOnCut { get; set; }
    public bool IsMissed { get; private set; }
    public double HitTime { get; set; }
    // 採点に使う曲時計。IMU+Camera照合ではSwingの時刻、従来判定では刃がいちばん近づいた時刻。時計がなければnull。
    public double? LastCutSongTime { get; private set; }
    // 金ノーツ：切ったときに豪華音を鳴らすため NoteSpawner が立てる。
    public bool IsGold { get; set; }

    [Header("Note kind")]
    public CutDirection RequiredDirection = CutDirection.None;
    public int RequiredCutCount = 1;
    public int RemainingCuts = 1;
    // ロングの滞留時間（秒）を譜面 lengthMs から直接指定する場合の値。
    // 0 以下なら自動（(RequiredCutCount-1) × NoteSpawner.secondsPerLongCut）。
    public float OverrideLingerSeconds = 0f;
    // 担当ハンド(chart.json の color 由来)。Left=青 / Right=赤 / Any=どちらでも(金・無色)。
    // 誤った手のスイングは「切れない」(ペナルティなし。ロングの各カットにも同じルールを適用)。
    public SaberHand RequiredHand = SaberHand.Any;
    // 方向は見た目だけ(メニューのナビノーツ用)。true なら RequiredDirection は矢印の表示にだけ使い、
    // 判定ではどの方向のスイングでも切れる(逆方向拒否も降格もしない)。本編のノーツは既定 false。
    public bool DirectionVisualOnly = false;
    // 通算で1回でも誤方向に切ったら false。
    public bool LastCutCorrectDirection { get; private set; } = true;
    // 最後に「受理された」カットを行った手。ハプティクスの宛先ルーティング等に使う。
    public SaberHand LastCutterHand { get; private set; } = SaberHand.Any;
    // 何回切れたか（達成数）。
    public int CutsAchieved => RequiredCutCount - RemainingCuts;
    public bool IsFinalized { get; private set; }

    // ロングノーツ用の残数表示（TMP）。NoteSpawner が割り付ける。
    public TMPro.TextMeshPro countLabel;

    // タイミング視認キュー（判定面の着地ゴースト: 固定枠+収縮枠）。NoteSpawner が割り付けて毎フレーム駆動する。
    [System.NonSerialized] public NoteTimingCue TimingCue;

    [Header("Slice physics")]
    public float sliceSeparationImpulse = 2.5f;
    public float saberVelocityScale = 0.35f;
    public float pieceLife = 2.8f;
    public float pieceFadeStart = 1.8f;
    public float pieceAngularImpulse = 4f;
    public bool pieceUseGravity = false;

    [Header("Shatter (long note)")]
    public int shatterDebrisCount = 6;
    public float shatterDebrisSpeed = 3f;

    // 本編の「流れる破片」(爽快感カタログ 手5)。NoteSpawner が出すノーツだけ有効にする(選曲画面・テストのノーツは従来どおり)。
    // 割れた片にノーツの前進の勢いを残し、軽い重力と、振った向きに沿った回転で画面の手前へ抜けさせる。
    // 切断の初速(SlicePieceDecay の 1.9 倍)は変えない。
    public const float FlowMomentumKeep = .6f;
    public const float FlowGravityScale = .35f;
    public const float FlowPieceLife = 1.1f;
    public const float FlowFadeStart = .55f;
    public const float FlowSpin = 6f;
    [System.NonSerialized] public bool FlowDebris;
    // ノーツが流れている速度(ワールド)。ロングは判定面に留まるので 0。
    [System.NonSerialized] public Vector3 FlowNoteVelocity;

    // 最終カット時に発火（タップなら1回、ロングなら全部切れた瞬間に1回）。
    public event System.Action<CuttableNote, Vector3, Vector3> OnCut;
    // ScoreManagerの方向降格も含めた確定判定。成功演出はOnCutの購読順に依存させない。
    public event System.Action<CuttableNote, JudgmentTier, Vector3, Vector3> OnJudged;
    internal void NotifyJudgment(JudgmentTier tier, Vector3 point, Vector3 velocity)
    {
        OnJudged?.Invoke(this, tier, point, velocity);
    }
    // 直前の切断で飛ばした2片(Perfect の演出から呼ぶ)。再利用ノーツへ持ち越さない。
    private SlicePieceDecay firstPiece, secondPiece;
    // 判定の通知(OnCut の中)は2片を作る前に来るので、まだ片が無ければ予約して、作った直後に光らせる。
    private bool hasPendingFlash;
    private Color pendingFlashColor;
    private float pendingFlashSeconds;
    public void FlashSlices(Color color, float seconds)
    {
        if (firstPiece == null && secondPiece == null && IsCut && !IsMissed)
        {
            hasPendingFlash = true;
            pendingFlashColor = color;
            pendingFlashSeconds = seconds;
            return;
        }
        if (firstPiece != null) firstPiece.Flash(color, seconds);
        if (secondPiece != null) secondPiece.Flash(color, seconds);
    }
    // 0回も切れずタイムアウトしたときに発火。部分達成のロングは OnCut（達成率付き）で扱う。
    public event System.Action<CuttableNote> OnMiss;
    // 1カットごとに発火（cutIndex は 0 から始まる達成番号、total は必要回数）。
    // ロングの上行音 SFX 等、各打鍵に紐づく演出に使う。
    public event System.Action<CuttableNote, int, int> OnPartialCut;

    private Vector3 lastHitPoint;
    private Vector3 lastVelocity = Vector3.right;
    private readonly List<(GameObject visual, Material material)> ownedCracks = new List<(GameObject, Material)>();

    void OnDestroy()
    {
        OnDisable();
        if (countLabel != null) SafeDestroyGo(countLabel.gameObject);
        // ノーツの完了・ミス後の破棄・シーン退出のどの経路でも、ひびの材質を残さない。
        foreach (var crack in ownedCracks)
        {
            SafeDestroy(crack.material);
            SafeDestroyGo(crack.visual);
        }
        ownedCracks.Clear();
    }

    public void Cut(Vector3 hitPoint, Vector3 cutVelocity)
    {
        Cut(hitPoint, cutVelocity, CutDirection.None, SaberHand.Any);
    }

    public void Cut(Vector3 hitPoint, Vector3 cutVelocity, CutDirection imuHint)
    {
        Cut(hitPoint, cutVelocity, imuHint, SaberHand.Any);
    }

    // imuHint：IMU 由来の振り検知方向（無ければ None）。velocity か imuHint のどちらかで一致すれば OK。
    // cutterHand：切ろうとしたセーバーの手。RequiredHand と不一致なら何もしない(逆方向拒否と同じ非ペナルティ設計)。
    public void Cut(Vector3 hitPoint, Vector3 cutVelocity, CutDirection imuHint, SaberHand cutterHand)
    {
        CutCore(hitPoint, cutVelocity, imuHint, cutterHand, null, .866f);
    }

    // 時間窓・位置を外側で確認済みのCamera判定専用。逆方向も拒否せず1段階降格へ渡す。
    public bool TryCutWithCameraTiming(Vector3 hitPoint, Vector3 cutVelocity, SaberHand cutterHand,
        double songTime, float directionTolerance)
    {
        if (!CameraSaberHistory.Finite(songTime)) return false;
        return CutCore(hitPoint, cutVelocity, CutDirection.None, cutterHand, songTime, directionTolerance);
    }

    // 従来のセーバー判定用。judgedSongTime があれば、その曲時計(刃がいちばん近づいた時刻)で採点する。
    // 方向の扱いは Cut と同じ(逆方向は切らない)。切れたかどうかを返す。
    public bool CutAtSongTime(Vector3 hitPoint, Vector3 cutVelocity, CutDirection imuHint, SaberHand cutterHand,
        double? judgedSongTime)
    {
        if (judgedSongTime.HasValue && !CameraSaberHistory.Finite(judgedSongTime.Value)) judgedSongTime = null;
        return CutCore(hitPoint, cutVelocity, imuHint, cutterHand, null, .866f, judgedSongTime);
    }

    bool CutCore(Vector3 hitPoint, Vector3 cutVelocity, CutDirection imuHint, SaberHand cutterHand,
        double? swingSongTime, float directionTolerance, double? judgedSongTime = null)
    {
        if (IsCut || IsMissed || IsFinalized) return false;

        if (RequireJudgeableOnCut && !IsJudgeable && !swingSongTime.HasValue) return false;
        if (MinimumCutSpeed > 0f && !(cutVelocity.magnitude >= MinimumCutSpeed)) return false;

        // 担当ハンド不一致のスイングは切れない。ノーツは無傷のまま、正しい手での再判定が可能。
        if (!SaberHandHelper.CanCut(RequiredHand, cutterHand)) return false;

        // 逆方向（要求方向と約120°以上ズレた）スイングはそもそも切らない。
        // 「準備で間違って切ってしまう」現象を防ぐためのガード。
        // 横方向（90°前後）は dot ≈ 0 で reject されないので、従来通り降格カットで通る。
        Vector2 vXY = new Vector2(cutVelocity.x, cutVelocity.y);
        // 方向が見た目だけのノーツ(ナビノーツ)は逆方向拒否も方向判定もしない
        bool judgeDirection = RequiredDirection != CutDirection.None && !DirectionVisualOnly;
        if (!swingSongTime.HasValue && judgeDirection &&
            CutDirectionHelper.ShouldRejectOpposite(RequiredDirection, vXY, imuHint))
        {
            // 何もせず終了：ノーツは IsCut も IsMissed も変わらず、セーバーが再度関わると再判定可能。
            return false;
        }

        // 方向判定（1回でも誤方向なら以降 false 維持）
        bool dirOk = !judgeDirection || (swingSongTime.HasValue
            ? CutDirectionHelper.Matches(RequiredDirection, vXY, directionTolerance)
            : CutDirectionHelper.MatchesWithHint(RequiredDirection, vXY, imuHint));
        if (CutsAchieved == 0) LastCutCorrectDirection = dirOk;
        else LastCutCorrectDirection = LastCutCorrectDirection && dirOk;

        LastCutterHand = cutterHand;
        LastCutSongTime = swingSongTime ?? judgedSongTime;
        RemainingCuts--;
        lastHitPoint = hitPoint;
        lastVelocity = cutVelocity;

        // 各カットでひびを追加＆数字を更新
        if (RemainingCuts > 0) AddCrack();
        UpdateCountLabel();

        // 各カットごとに発火（達成番号は 0 始まり）
        OnPartialCut?.Invoke(this, CutsAchieved - 1, RequiredCutCount);

        if (RemainingCuts <= 0)
        {
            // 全カット完了：本体を砕け散らせる
            IsCut = true;
            IsFinalized = true;
            OnCut?.Invoke(this, hitPoint, cutVelocity);
            ShatterAndDestroy(hitPoint, cutVelocity);
        }
        return true;
    }

    // 判定窓を逃した時のフラグ。
    // 部分達成があった場合はスコア用に OnCut を発火（達成率は ScoreManager 側が読む）。
    public void MarkMiss()
    {
        if (IsCut || IsMissed || IsFinalized) return;
        IsMissed = true;
        IsJudgeable = false;
        IsFinalized = true;
        DimVisual();

        if (CutsAchieved > 0)
        {
            // 部分達成 → ScoreManager で完了率を見て tier を下げる
            OnCut?.Invoke(this, lastHitPoint, lastVelocity);
        }
        else
        {
            OnMiss?.Invoke(this);
        }
    }

    private void UpdateCountLabel()
    {
        if (countLabel == null) return;
        if (RemainingCuts <= 0) countLabel.gameObject.SetActive(false);
        else countLabel.text = RemainingCuts.ToString();
    }

    private void DimVisual()
    {
        var visuals = GetComponent<NoteVisuals>();
        if (visuals != null)
        {
            visuals.DimAfterMiss();
            return;
        }
        var mr = GetComponent<MeshRenderer>();
        if (mr == null) return;
        var material = mr.material;
        if (material == null) return;
        Color c;
        if (material.HasProperty("_BaseColor")) c = material.GetColor("_BaseColor");
        else c = material.color;
        c = new Color(c.r * 0.4f, c.g * 0.4f, c.b * 0.4f, c.a);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", c);
        else material.color = c;
        if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", c * 0.2f);
    }

    int cracksUsed;
    internal void WarmCracks(int count)
    {
        for (int i = ownedCracks.Count; i < count; i++)
        {
            var crack = new GameObject("Crack", typeof(MeshFilter), typeof(MeshRenderer));
            crack.transform.SetParent(transform, false);
            crack.GetComponent<MeshFilter>().sharedMesh = NoteFragmentPool.CubeMesh;
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            else mat.color = Color.white;
            if (mat.HasProperty("_EmissionColor")) { mat.EnableKeyword("_EMISSION"); mat.SetColor("_EmissionColor", Color.white * 1.4f); }
            crack.GetComponent<MeshRenderer>().sharedMaterial = mat;
            crack.SetActive(false); ownedCracks.Add((crack, mat));
        }
    }
    private void AddCrack()
    {
        WarmCracks(cracksUsed + 1);
        var crack = ownedCracks[cracksUsed++].visual;
        // 当たった位置・振った方向に傷を残し、完了が近いほど既存の傷も太く長くする。
        // 成功の光は最終判定まで出さず、ノーツ本体や判定形状は動かさない。
        Vector3 localHit = transform.InverseTransformPoint(lastHitPoint);
        Vector3 localDirection = transform.InverseTransformDirection(lastVelocity);
        float angle = localDirection.sqrMagnitude > .0001f
            ? Mathf.Atan2(localDirection.y, localDirection.x) * Mathf.Rad2Deg - 90f : 0f;
        float offset = ((cracksUsed - 1) % 3 - 1) * .09f;
        crack.transform.localPosition = new Vector3(Mathf.Clamp(localHit.x + offset, -.28f, .28f),
            Mathf.Clamp(localHit.y - offset, -.28f, .28f), -.55f);
        crack.transform.localRotation = Quaternion.Euler(0, 0, angle + ((cracksUsed & 1) == 0 ? 12f : -12f));
        float progress = Mathf.Clamp01(CutsAchieved / (float)Mathf.Max(1, RequiredCutCount - 1));
        for (int i = 0; i < cracksUsed; i++)
            ownedCracks[i].visual.transform.localScale = new Vector3(Mathf.Lerp(.025f, .055f, progress),
                Mathf.Lerp(.38f, .88f, progress) * (1f - (i % 3) * .08f), .03f);
        crack.SetActive(true);
    }

    private void ShatterAndDestroy(Vector3 hitPoint, Vector3 cutVelocity)
    {
        bool sliced = TrySpawnSlices(hitPoint, cutVelocity);
        if (RequiredCutCount > 1)
        {
            SpawnDebris(cutVelocity);
        }
        if (IsPooled) { Retire(); return; }
        if (sliced)
        {
            SafeDestroyGo(gameObject);
        }
        else
        {
            gameObject.SetActive(false);
        }
    }

    private static void SafeDestroy(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o);
        else DestroyImmediate(o);
    }

    private static void SafeDestroyGo(GameObject go)
    {
        if (go == null) return;
        if (Application.isPlaying) Destroy(go);
        else DestroyImmediate(go);
    }

    private void SpawnDebris(Vector3 cutVelocity)
    {
        var renderer = GetComponent<MeshRenderer>();
        var material = renderer != null ? renderer.sharedMaterial : null;
        Vector3 motion = new Vector3(cutVelocity.x, cutVelocity.y, 0f).normalized;
        if (motion.sqrMagnitude < .0001f) motion = Vector3.right;
        Vector3 normal = Vector3.Cross(motion, Vector3.forward);
        Vector3 carry = Vector3.ClampMagnitude(cutVelocity, 14f) * .15f;
        for (int i = 0; i < shatterDebrisCount; i++)
        {
            var piece = RentPiece(material, false);
            piece.transform.position = transform.position + Random.insideUnitSphere * .3f;
            piece.transform.rotation = Random.rotation;
            piece.transform.localScale = Vector3.one * Random.Range(.06f,.14f);
            // 最終打だけ、切断面の両側へ扇状に開いてから振った先へ抜ける。
            float side = (i & 1) == 0 ? 1f : -1f;
            Vector3 fan = (motion * .6f + normal * (side * (.55f + (i % 3) * .3f))
                + Vector3.forward * Random.Range(-.18f, .18f)).normalized;
            piece.Launch(FragmentPool, fan * shatterDebrisSpeed + carry,
                Random.insideUnitSphere * (pieceAngularImpulse * 1.5f), pieceUseGravity, PieceLife, PieceFadeStart, 0f);
            ApplyFlow(piece);
        }
    }
    float PieceLife => FlowDebris ? Mathf.Min(pieceLife, FlowPieceLife) : pieceLife;
    float PieceFadeStart => FlowDebris ? Mathf.Min(pieceFadeStart, FlowFadeStart) : pieceFadeStart;
    void ApplyFlow(SlicePieceDecay piece)
    {
        if (FlowDebris && piece != null) piece.Flow(FlowNoteVelocity * FlowMomentumKeep, FlowGravityScale);
    }
    private SlicePieceDecay RentPiece(Material material, bool sliced)
    {
        var piece = FragmentPool != null ? FragmentPool.Rent() : NoteFragmentPool.CreatePiece();
        piece.name = name + (sliced ? "_piece" : "_debris");
        piece.CopyMaterial(material);
        piece.GetComponent<MeshFilter>().sharedMesh = sliced ? piece.ReusableMesh : NoteFragmentPool.CubeMesh;
        return piece;
    }

    private bool TrySpawnSlices(Vector3 hitPoint, Vector3 cutVelocity)
    {
        MeshFilter mf = GetComponent<MeshFilter>();
        MeshRenderer mr = GetComponent<MeshRenderer>();
        if (mf == null || mr == null || mf.sharedMesh == null) return false;

        Vector3 motionDir = cutVelocity;
        motionDir.z = 0f;
        if (motionDir.sqrMagnitude < 0.0001f) return false;
        motionDir.Normalize();

        Vector3 cutNormalWorld = Vector3.Cross(motionDir, Vector3.forward).normalized;
        Vector3 hitLocal = transform.InverseTransformPoint(hitPoint);
        Vector3 normalLocal = transform.InverseTransformDirection(cutNormalWorld).normalized;

        Bounds b = mf.sharedMesh.bounds;
        float halfExtent = Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z)) * 0.7f;
        float signed = Vector3.Dot(hitLocal - b.center, normalLocal);
        signed = Mathf.Clamp(signed, -halfExtent, halfExtent);
        Vector3 planePoint = b.center + normalLocal * signed;
        Plane planeLocal = new Plane(normalLocal, planePoint);

        var first = RentPiece(mr.sharedMaterial, true);
        var second = RentPiece(mr.sharedMaterial, true);
        if (!MeshSlicer.SliceInto(mf.sharedMesh, planeLocal, first.ReusableMesh, second.ReusableMesh))
        {
            first.Release(); second.Release(); return false;
        }
        // 二片は切断面から逆向きに開き、両方とも振った先へ運ばれる。
        // メニューの切断は従来の飛び方を維持する。共通の飛散速度1.9倍はLaunch側で適用。
        Vector3 carry = Vector3.ClampMagnitude(cutVelocity, 14f) * saberVelocityScale;
        float release = RequiredCutCount > 1 ? 1.22f : 1f;
        Vector3 separationWorld = cutNormalWorld * (sliceSeparationImpulse * release);
        if (DirectionVisualOnly)
        {
            Vector3 legacy = cutNormalWorld * sliceSeparationImpulse + cutVelocity * saberVelocityScale;
            SpawnPiece(first, legacy, cutNormalWorld); SpawnPiece(second, -legacy, cutNormalWorld);
        }
        else
        {
            SpawnPiece(first, separationWorld + carry, cutNormalWorld); SpawnPiece(second, -separationWorld + carry, cutNormalWorld);
        }
        firstPiece = first; secondPiece = second;
        if (hasPendingFlash)
        {
            hasPendingFlash = false;
            first.Flash(pendingFlashColor, pendingFlashSeconds);
            second.Flash(pendingFlashColor, pendingFlashSeconds);
        }
        return true;
    }
    private void SpawnPiece(SlicePieceDecay piece, Vector3 velocity, Vector3 spinAxis)
    {
        piece.transform.position = transform.position; piece.transform.rotation = transform.rotation;
        piece.transform.localScale = transform.lossyScale;
        Vector3 spin = FlowDebris
            ? spinAxis * FlowSpin + Random.insideUnitSphere * (pieceAngularImpulse * .25f)
            : Random.insideUnitSphere * pieceAngularImpulse;
        piece.Launch(FragmentPool, velocity, spin, pieceUseGravity, PieceLife, PieceFadeStart, .1f);
        ApplyFlow(piece);
    }
}
