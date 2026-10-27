using System;
using UnityEngine;
using UnityEngine.UI;

// Manic Sonic の裏譜面 R の天地返し(U103)。回すのは画面の絵だけ: カメラの位置と向きはそのまま、投影の行列に
// ノーツの帯の中心(0, −0.15)を軸にした 2D の回転を掛ける(ReverseRules.ViewMatrix)。床・ノーツ・剣が一緒に回り、
// HUD(ScreenSpaceOverlay)だけ正立のまま。判定はワールドの XY のままで、剣の入力は SaberInputBridge のカメラの経路で写す
// (InputDegrees と MapInput。回す前の投影で画面の位置を求めて逆に回し、同じ平面へ戻すので、剣は手のとおりに画面で動く)。
// 投影は毎コマ作り直す(投影モードの切り替えで FOV が変わっても、止まっている間に画面の比が変わっても追う)。角度が 0(と 360)のときは
// 触らず、終わり・中断・破棄で ResetProjectionMatrix する。F3 では回る動きを見せず、回転の真ん中で 0.5 秒だけ画面を暗くして(70%)、
// 暗い間に向きを切り替える。
public sealed class WorldTurn : IDisposable
{
    // いま動いている天地返し(1つだけ)。無ければ入力も絵も回さない。
    public static WorldTurn Active { get; private set; }
    // 今のコマに絵へ当てた角度(PlayRecoveryView が画面の左右・上下をそろえるのに読む)。
    public static float CurrentDegrees => Active != null ? Active.Degrees : 0f;
    // 剣の入力を回す角度。SaberInputBridge が自分の Update(実行順 −1500、GamePlayManager より先)で呼ぶ。再生中は、このコマで最初に
    // 聞かれた曲の時刻にそろえるので、あとから GamePlayManager が当てる絵の角度と同じになる(止まっている間は角度もそのまま)。
    public static float InputDegrees => Active != null ? Active.AngleAt(Active.FrameTime(Active.ReadClock())) : 0f;
    // 同じコマで、最初の時刻にそろえる幅(これより離れた時刻はシーク・テストの飛びとして、聞かれた値のまま)。
    public const double FrameTimeTolerance = .1;

    // 剣の入力の写し(カメラの経路)。回す前の投影でこの点の画面の位置を求め、軸のまわりに −θ 回した画面の位置から、
    // 回す前の投影で同じ z の平面へ戻す(ReverseRules.TryTurnInput)。回した投影(M(θ)·P0)では、写した点は元の点と同じ画面の位置に見える。
    // 回す前の投影とカメラの行列は、この天地返しが投影を作り直すたびに覚えたもの(Bridge が先に動いても同じもの)。
    // カメラが無い・写せないときは、ワールドの XY で逆に回す近似(ReverseRules.InputRotate)。
    public static Vector3 MapInput(Vector3 point, float degrees)
    {
        if (ReverseRules.Wrap(degrees) == 0f) return point;
        var turn = Active;
        if (turn != null && turn.hasMatrices && ReverseRules.TryTurnInput(point, degrees, turn.baseProjection,
                turn.worldToCamera, turn.pivotNdc, turn.aspect, out Vector3 turned))
            return turned;
        return ReverseRules.InputRotate(point, degrees);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetActive() { Active = null; }

    // F3 の暗転の幕の並び順(HUD の 500 より下)。
    public const int DimSortingOrder = 450;

    readonly ReverseChartData data;
    readonly Func<double> clock;
    readonly double offset;
    readonly Vector3 pivotWorld;
    Camera view;
    bool applied, disposed;
    GameObject dimRoot;
    Image dimImage;
    // 回す前の投影・カメラの行列・縦横比・軸の NDC(剣の入力の写しに使う)。
    Matrix4x4 baseProjection = Matrix4x4.identity, worldToCamera = Matrix4x4.identity;
    Vector2 pivotNdc;
    float aspect = 1f;
    bool hasMatrices;
    // このコマの曲の時刻(再生中だけ。Bridge と GamePlayManager で同じ角度にする)。
    int frameStamp = -1;
    double frameTime = double.NaN;

    public float Degrees { get; private set; }
    public float DimAlpha { get; private set; }
    public Camera View => view;
    // 投影の行列を差し替えているか。
    public bool ProjectionApplied => applied;
    public bool Disposed => disposed;

    // clock は曲の時計(秒。止まっている・始まっていないときは NaN)。camera を省くと Camera.main(テストは自分のカメラを渡す)。
    public WorldTurn(ReverseChartData data, Func<double> clock, double offsetSeconds, float judgeZ, Camera camera = null)
    {
        this.data = data;
        this.clock = clock;
        offset = offsetSeconds;
        pivotWorld = new Vector3(ReverseRules.Pivot.x, ReverseRules.Pivot.y, judgeZ);
        view = camera != null ? camera : Camera.main;
        if (Active != null && Active != this) Active.Dispose();
        Active = this;
        // 前の天地返しが投影を戻したあとの、回す前の行列を覚えておく(最初に回すコマより前に Bridge が写せるように)。
        if (view != null) CacheMatrices();
    }

    void CacheMatrices()
    {
        baseProjection = view.projectionMatrix;
        worldToCamera = view.worldToCameraMatrix;
        aspect = view.aspect;
        pivotNdc = ReverseRules.PivotNdc(baseProjection, worldToCamera, pivotWorld);
        hasMatrices = true;
    }

    // 再生中は、このコマで最初に聞かれた曲の時刻にそろえる(DSP の時計はコマの途中でも進むため)。
    double FrameTime(double requested)
    {
        if (!Application.isPlaying || !ReverseRules.Finite(requested)) return requested;
        int frame = Time.frameCount;
        if (frame == frameStamp && ReverseRules.Finite(frameTime) && Math.Abs(requested - frameTime) <= FrameTimeTolerance)
            return frameTime;
        frameStamp = frame;
        frameTime = requested;
        return requested;
    }

    double ReadClock()
    {
        if (disposed || clock == null) return double.NaN;
        try { return clock(); }
        catch (MissingReferenceException) { return double.NaN; }
    }

    // 曲の時計 songTime の角度。時計が無いときは 0。
    public float AngleAt(double songTime)
    {
        if (disposed || !ReverseRules.Finite(songTime)) return 0f;
        return ReverseRules.TurnAngle(data, songTime - offset, DisplaySettings.ReducedEffects);
    }

    // 毎コマ(GamePlayManager のフレームの最初、ReverseMode.ApplyView)。一時停止中も止まった曲の時刻で呼ぶ。
    public void Apply(double songTime)
    {
        if (disposed) return;
        double time = FrameTime(songTime);
        bool reduced = DisplaySettings.ReducedEffects;
        Degrees = AngleAt(time);
        ApplyProjection(Degrees);
        DimAlpha = reduced && ReverseRules.Finite(time) ? ReverseRules.ReducedDim(data, time - offset) : 0f;
        ShowDim(DimAlpha);
    }

    void ApplyProjection(float degrees)
    {
        if (view == null) { applied = false; hasMatrices = false; return; }
        bool turned = ReverseRules.Wrap(degrees) != 0f;
        // 既定の投影(FOV・縦横比・near・far)から毎コマ作り直して覚え、軸の NDC のまわりに回す。
        // 0°(と 360°)で差し替えていないときは、カメラに触らず今の行列を覚えるだけ。
        if (applied || turned) view.ResetProjectionMatrix();
        applied = false;
        CacheMatrices();
        if (!turned) return;
        view.projectionMatrix = ReverseRules.ViewMatrix(baseProjection, pivotNdc, degrees, aspect);
        applied = true;
    }

    // F3 の暗転。全画面の黒い幕(HUD は上に残る)。
    void ShowDim(float alpha)
    {
        if (alpha <= 0f)
        {
            if (dimRoot != null && dimRoot.activeSelf) dimRoot.SetActive(false);
            return;
        }
        if (dimRoot == null)
        {
            dimRoot = new GameObject("ReverseTurnDim", typeof(RectTransform), typeof(Canvas));
            var canvas = dimRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = DimSortingOrder;
            var image = new GameObject("Dim", typeof(RectTransform), typeof(Image));
            image.transform.SetParent(dimRoot.transform, false);
            var rect = image.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            dimImage = image.GetComponent<Image>();
            dimImage.raycastTarget = false;
        }
        if (!dimRoot.activeSelf) dimRoot.SetActive(true);
        dimImage.color = new Color(0f, 0f, 0f, Mathf.Clamp01(alpha));
    }

    // 終わり・中断・破棄。投影と入力の写しを必ず戻す。
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (view != null && applied) view.ResetProjectionMatrix();
        applied = false;
        Degrees = 0f;
        DimAlpha = 0f;
        if (Active == this) Active = null;
        if (dimRoot != null) UISkinKit.SafeDestroy(dimRoot);
        dimRoot = null;
        dimImage = null;
    }
}
