using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

// 本編シーンの「ゲーム側 GManager」。
// 単一の Update から受付窓の更新 → SaberCutJudge → NoteSpawner の順に駆動する。
public class GamePlayManager : MonoBehaviour
{
    public SongPlayer songPlayer;
    public NoteSpawner noteSpawner;
    public ScoreManager scoreManager;
    public SaberCutJudge cutJudge;
    [Header("Camera + IMU judgment")]
    public CameraImuJudgment cameraImuJudgment;
    public BarLineSpawner barLineSpawner;
    public LongNoteCutSfx longNoteCutSfx; // 未設定なら自前で生成
    public GoldNoteSfx goldNoteSfx;       // 未設定なら自前で生成

    public string resultSceneName = "Result";
    public float endWaitSeconds = 2.0f;
    // 最後のノーツ通過後、リザルトに遷移する前の余韻時間
    public float outroSeconds = 3.5f;

    [Header("Start count-in")]
    // 本編開始前に、譜面 BPM と同期した「3, 2, 1, START!」を表示する。
    public bool enableStartCountdown = true;
    [Range(0f, 1f)] public float startCountdownVolume = 0.55f;

    [Header("Cut SFX mix")]
    // 切断系の効果音の上限(曲=1.0 に対する比)。シーンや Inspector の値がこれより大きくても起動時にここまで下げる。
    // 2026-09-22: 追加の音量調整で 0.8 / 0.75 / 0.55 の各1.25倍へ。Miss は JudgmentSfx.missVolumeScale で従来量を維持。
    public const float JudgmentSfxMaxVolume = 1f;       // 通常カット・フリック・ロング完了
    public const float GoldSfxMaxVolume = 0.9375f;      // 金ノーツ
    public const float LongTickSfxMaxVolume = 0.6875f;  // ロング途中の刻み

    [Header("Chart tuning")]
    // 譜面のリズムが曲とずれているときの追加オフセット秒。
    // +値 = ノーツが「遅れて」流れてくる、-値 = ノーツが「早く」流れてくる。
    // chart.offsetMs に上乗せされる。
    public float extraOffsetSeconds = 0f;
    // 譜面のノーツが音源より長い場合に自動で切り詰める（曲終了時に譜面も終わるように）。
    public bool trimChartToAudioLength = true;
    // 音源長 +この秒数 をノーツの上限時刻にする。負にすれば早めに切れる。
    public float trimGraceSeconds = 0f;

    [Header("Overhauled stage (Neon Focus)")]
    // プレイ画面の全面リニューアル(判定ゲート+フォグ+新HUD+判定緩和)。
    // false で旧テーマ(青パネル+グロー+ビート線+旧HUD)へ戻せる。
    // 注意:シーンに無い新規フィールドなのでコード既定値(true)がそのまま効く。
    public bool useOverhauledStage = true;
    // リニューアル版のセーバー判定。「だいぶ甘く」の要望(2026-07)でさらに緩和
    // (前値 blade 0.26 / hitXY 0.45 / minSpeed 3.0 → 当たりを広く、必要な振り速度を低く)。
    // 2026-10「切りにくすぎる」の要望で当たりを 1.3 倍(刃との距離 0.92 → 1.20)に広げた。
    // 後続ノーツの巻き込みは SaberCutJudge の「1回の振りで1つの時刻」で防ぐ(間隔どおりのなぞりだけは続けて切れる)。
    // シーンにも同じ値を保存している。
    public float saberBladeRadiusV2 = 0.32f;
    public float saberNoteHitRadiusXYV2 = 0.88f;
    public float saberMinCutSpeedV2 = 2.0f;

    [Header("Camera latency (判定だけ遅らせる)")]
    // iPhone カメラ(PhoneSaber)の値は、振ってから届くまで約0.135秒かかる(2026-10-10 学校で実測した中央値)。
    // ノーツの見た目と音は動かさず、判定の時計(受付窓・刃の判定・Miss)だけをこの秒数遅らせて差し引く。
    // カメラの値が届いているときだけ効かせる(マウスは遅れが小さく、IMU 判定は自前で補正するので効かせない)。
    // 2026-10-12 追加。新規フィールドなのでシーンの値は無くコード既定値が効く。
    public float cameraJudgeLatencySeconds = 0.12f;

    [Header("Two sabers (2本セーバー)")]
    // 棒1(port5005)=シーンのセーバー、棒2(port5006)=実行時生成の2本目。
    // 青ノーツ=左手、赤ノーツ=右手、金・無色=どちらでも。マウスフォールバック時は全色切れる。
    public bool enableTwoSabers = true;
    // 棒1の手。実機のセーバー割り当てが逆だったらここを Left に変える。
    public SaberHand stick1Hand = SaberHand.Right;

    [Header("Judge guide")]
    public bool simplifyJudgeGuide = true;
    public float judgePanelAlpha = 0.30f;
    public Color judgePanelTint = new Color(0.30f, 0.75f, 1.0f, 1f);
    public bool addJudgePanelOuterGlow = true;

    [Header("Bar lines")]
    // BPM ガイドとして薄い小節線を流す。視覚ノイズが気になる場合は true で OFF にできる。
    public bool disableBarLines = false;
    [Range(0f, 1f)] public float barLineAlpha = 0.18f;
    [Range(0.005f, 0.08f)] public float barLineThickness = 0.02f;
    // 判定面に固定して置く「ビート参照線」。流れてきた小節線がここに重なった瞬間がビートのタイミング。
    public bool addBeatReferenceLine = true;
    [Range(0f, 1f)] public float beatReferenceAlpha = 0.45f;
    [Range(0.005f, 0.08f)] public float beatReferenceThickness = 0.035f;

    [Header("Saber hit detection (stricter to avoid sweeping)")]
    public bool overrideSaberRadii = true;
    public float saberBladeRadius = 0.22f;       // 既存ビルド時の 0.30 から狭める
    public float saberNoteHitRadiusXY = 0.35f;   // 既存ビルド時の 0.55 から狭める
    public float saberMinCutSpeed = 4.0f;        // 3.0 → 4.0 にして「ふわっと振る」での誤発火を抑える

    [Header("Camera")]
    // 視点を「ほんの少し」上から俯瞰する感じ。判定ラインが傾かないよう、回転は控えめ。
    // 元シーン (0, 0.8, -7) rot(5,0,0) → Y を上げて高さで俯瞰感を出す。
    public bool overrideCameraPose = true;
    // 現行視点との A/B 比較用。判定面の中心をほぼ動かさず、奥行きの重なりだけを見やすくする。
    public bool useSlightTopDownView = false;
    public Vector3 cameraPosition = new Vector3(0f, 1.6f, -7f);
    public Vector3 cameraRotationEuler = new Vector3(6f, 0f, 0f);
    public Vector3 topDownCameraPosition = new Vector3(0f, 2.35f, -7f);
    public Vector3 topDownCameraRotationEuler = new Vector3(12f, 0f, 0f);

    [Header("Floor / Lanes")]
    // Tron 風の床 + レーンガイド + 奥行きグリッドを実行時生成（視認性向上）。
    public bool addFloor = true;

    [Header("Saber input bootstrap (実機セーバー接続)")]
    // 全部既定 true。SaberInputBridge は InputPoint の受信状態を見て、
    // UDP データが 1 秒以上止まったら自動でマウスにフォールバックする（明示フラグ不要）。
    public bool autoEnsureInputPoint = true;
    public bool autoEnsureUdpImuBridge = true;
    public bool autoEnsureSwing8DirectionLogger = true;
    [Tooltip("実機BLE bridgeをゲーム起動時から自動起動する。変更は全Scene共通設定へ反映される。")]
    public bool autoStartBleBridge = false;

    // 判定面ガイドから剥がす子オブジェクトの名前接頭辞。
    private static readonly string[] JudgeGuideStripPrefixes = {
        "GridV", "GridH", "Border", "Corner", "Cross"
    };

    private bool finished;
    private bool ready;
    private double lastNoteTime;
    private GameplayFeedbackPresenter playFeedback;
    // このシーンでデコードした音源だけを所有する。外から渡された共有音源は破棄しない。
    private AudioClip ownedSongClip;
    // 2本目のセーバー判定(enableTwoSabers 時に SaberRig が生成)
    private SaberCutJudge cutJudge2;
    private GateBeatPulse gatePerfectPulse;
    private FoundryStageMotion foundryStageMotion;
    private ScenicStageWorld scenicStageWorld;
    private FloorRenderer stageFloor;
    private StagePerformanceTimeline stagePerformance = new StagePerformanceTimeline();
    private StageReactiveEffects stageReactions;
    // サビ入りのドロップとサビ中の強調(爽快感カタログ 山1)。
    private ChorusDrop chorusDrop;
    // サビのフィーバーと遠景のライトショー(派手さカタログ 祭2・祭4)、ゲートまわりの手応え(斬1・斬2・斬3・斬5・波1)。
    private StageFeverShow feverShow;
    private GateAccents gateAccents;

    // --- キャリブレーション（判定調整）モード ---
    private bool inCalibration;
    private CalibrationController calibration;
    public CalibrationController Calibration => calibration;

    // --- チュートリアル(タイトルの問いかけで「はい」)。判定調整と同じ形のモード分岐 ---
    private bool inTutorial;
    private TutorialController tutorial;
    public TutorialController Tutorial => tutorial;

    // --- 曲の途中で剣を見失ったとき(仕様面の点検 4、2026-10-21) ---
    // カメラで遊ぶ台で、届いていた剣がすべて3秒止まり、ノーツが来ているときだけ曲を止め、戻ったら 3・2・1 で続きから再開する。
    // 止めるのは本編だけ(練習は受信の表示とスタッフ向けの1行だけ。判定調整には出さない)。
    private readonly PlayTrackingWatch trackingWatch = new PlayTrackingWatch();
    private PlayTrackingStatus trackingView;
    public PlayTrackingWatch TrackingWatch => trackingWatch;
    public PlayTrackingStatus TrackingView => trackingView;
    // カメラの位置が届いている台では、その場面でマウスを剣の代わりにしない。
    private bool mouseFallbackLocked;
    // この時刻(実時間)より後の受信だけを見る。場面の始まりの PlayTrackingWatch.CarryOverSeconds 前から。
    private double trackingSince = double.NegativeInfinity;
    // 曲(練習)が鳴り始めた実時間(一度も届いていない色の警告の起点)、再開の表示を消す時刻、いま出している 3・2・1 の数字。
    private double playingSince = double.NaN, resumedShownUntil = double.NegativeInfinity;
    private int countdownShown;
    private readonly ResumedSongWait resumedSongWait = new ResumedSongWait();
    // 音の出力先が変わって、曲を止めて立て直している(HoldForAudioChange)。曲を読み直したら(audioResumeAt から)3・2・1 で再開する。
    private bool audioChangeHold;
    public bool AudioChangeHold => audioChangeHold;
    private double audioResumeAt = double.NaN;
    private Coroutine audioReload;
    // 曲を始める前に出力先が変わって、読んだ曲の音が消えたかもしれない(予約の直前に読み直す)。
    private bool audioLostBeforeStart;
    private string loadedSongId;
    public const string AudioReloadingText = "音の出力先が変わったので、曲を読み直しています…";
    // 曲ごとの音が鳴り終わる秒(仕様面の点検 7)。0 以下は音源の長さ。
    private double musicEndSeconds = -1;
    double MusicEnd => musicEndSeconds > 0 ? musicEndSeconds : songPlayer.Duration;
    // Esc の2回(仕様面の点検 2)。戻ると決めたら、もう受けない。
    private readonly EscapeTwiceGate escapeGate = new EscapeTwiceGate();
    private bool leaving;
    // 最後まで遊んだ(終わりの演出に入った)か、その回の結果を確定して残したか。演出の間に Esc で抜けても記録は残す。
    private bool completing, resultCommitted;

    IEnumerator Start()
    {
        if (songPlayer == null || noteSpawner == null || scoreManager == null)
        {
            Debug.LogError("GamePlayManager: 依存コンポーネントが未設定です");
            yield break;
        }
        // 派手さカタログの効果音は、幕が閉じている最初のフレームでまとめて作る(曲中や幕が開く途中に処理落ちさせない)。
        FlourishSfx.PrewarmGameplay();
        trackingSince = Time.realtimeSinceStartupAsDouble - PlayTrackingWatch.CarryOverSeconds;

        // 自動探索
        if (cutJudge == null) cutJudge = Object.FindFirstObjectByType<SaberCutJudge>();
        if (cutJudge != null)
        {
            cutJudge.autonomous = false;
            if (overrideSaberRadii)
            {
                // リニューアル時は緩和値(V2)、旧テーマ時はシーンの従来値を使う。
                cutJudge.bladeRadius = useOverhauledStage ? saberBladeRadiusV2 : saberBladeRadius;
                cutJudge.noteHitRadiusXY = useOverhauledStage ? saberNoteHitRadiusXYV2 : saberNoteHitRadiusXY;
                cutJudge.minCutSpeed = useOverhauledStage ? saberMinCutSpeedV2 : saberMinCutSpeed;
            }
        }

        EnsureLongNoteCutSfx();
        EnsureGoldNoteSfx();
        // 効果音の上限を揃える(シーン側に大きい値が保存されていても上書きで下げる)。値は「Cut SFX mix」の定数。
        if (goldNoteSfx != null) goldNoteSfx.volume = Mathf.Min(goldNoteSfx.volume, GoldSfxMaxVolume);
        if (longNoteCutSfx != null) longNoteCutSfx.volume = Mathf.Min(longNoteCutSfx.volume, LongTickSfxMaxVolume);
        var judgmentSfxComp = Object.FindFirstObjectByType<JudgmentSfx>();
        if (judgmentSfxComp != null) judgmentSfxComp.volume = Mathf.Min(judgmentSfxComp.volume, JudgmentSfxMaxVolume);
        if (autoEnsureInputPoint) EnsureInputPoint();
        if (autoEnsureUdpImuBridge) EnsureUdpImuBridge();
        if (autoEnsureSwing8DirectionLogger) EnsureSwing8DirectionLogger();

        // 2本目のセーバー(棒2)。1本目の設定が確定した後に生成する。
        if (enableTwoSabers && cutJudge != null)
        {
            cutJudge2 = SaberRig.EnsureSecondSaber(cutJudge, stick1Hand);
        }
        if (cameraImuJudgment == null) cameraImuJudgment = GetComponent<CameraImuJudgment>();
        if (cameraImuJudgment == null) cameraImuJudgment = gameObject.AddComponent<CameraImuJudgment>();
        cameraImuJudgment.Configure(noteSpawner, cutJudge, cutJudge2);
        // 刃がノーツの上を通っている最中は、締め切りのあとも少しだけ Miss を待つ(通り過ぎたら切れる)。
        noteSpawner.HoldMiss = SaberCuttingThrough;
        // 切れた手の刃だけを光らせる(爽快感カタログ 手8)。
        SaberCutFlash.Attach(gameObject, scoreManager, cutJudge, cutJudge2);
        if (useOverhauledStage)
        {
            // 新テーマ:格子剥がし+判定ゲート+カメラ背景/フォグは GameStageSkin に集約。
            // 旧 SimplifyJudgeGuide(パネル着色+グロー+ビート線)は役割が被るので呼ばない。
            GameStageSkin.Apply();
        }
        else if (simplifyJudgeGuide)
        {
            SimplifyJudgeGuide();
        }
        if (overrideCameraPose) ApplyCameraPose();
        if (addFloor)
        {
            var floor = FloorRenderer.Ensure(transform);
            stageFloor = floor;
            foundryStageMotion = FoundryStageMotion.Ensure(floor);
            scenicStageWorld = floor.GetComponentInChildren<ScenicStageWorld>();
        }

        // 保存値を残し、本編の Easy だけ実効値を変える。
        bool songRun = !GameSession.IsCalibrationMode && !GameSession.TutorialPending;
        GameDifficultyRules.ConfigureJudges(GameSession.SelectedDifficulty, songRun, cutJudge, cutJudge2, cameraImuJudgment);
        if (noteSpawner != null)
        {
            noteSpawner.approachTime = GameDifficultyRules.ApproachSeconds(
                GameSession.SelectedDifficulty, GameSession.NoteApproachTime, songRun);
            if (stageFloor != null) noteSpawner.ConfigureFloorGuide(stageFloor.floorY);
            // 判定可能ウィンドウを Classify の判定窓と同期させる
            // (シーンに古い狭い窓が焼き込まれていても、甘くした定数側が常に効くように)。
            noteSpawner.judgeWindow = (float)JudgmentTierHelper.LateBadSeconds;
            noteSpawner.earlyJudgeWindow = (float)JudgmentTierHelper.EarlyBadSeconds;
            // 背景が明るいステージ(Morning)では、青ノーツに暗い縁を付ける。ノーツを並べる前(SetChart の前)に決める。
            noteSpawner.outlineLeftNotes = stageFloor != null && StageThemeCatalog.OutlinesLeftNotes(stageFloor.ActiveTheme);
        }

        // --- キャリブレーションモード ---
        if (GameSession.IsCalibrationMode)
        {
            StartCalibration();
            ready = true;
            yield break;
        }

        // --- チュートリアル(フラグは読んだら消す。終わったら選曲へ) ---
        if (GameSession.TutorialPending)
        {
            GameSession.TutorialPending = false;
            StartTutorial();
            ready = true;
            yield break;
        }

        // 新HUD(スコア/コンボ/判定演出/曲名/進行バー)。旧 ScoreHUD は内部で無効化される。
        // キャリブレーション時は CalibrationOverlay が主役なので出さない(上の分岐で return 済み)。
        if (useOverhauledStage) GameHUDSkin.Ensure();
        // 受信の表示は左上の列、RANK の下(仕様面の点検 4)。
        trackingView = PlayTrackingStatus.Create(transform, 262);
        trackingView.ConfigureRecovery(noteSpawner, cutJudge, cutJudge2);

        string songId = GameSession.SelectedSongId;
        if (string.IsNullOrEmpty(songId))
        {
            Debug.LogWarning("GamePlayManager: SelectedSongId 未設定。デフォルト曲を試します");
            songId = "ElDorado";
        }

        ChartData chart = ChartLoader.LoadFromStreamingAssets(songId, GameSession.SelectedDifficulty);
        if (chart.notes.Count == 0)
        {
            // 選曲後にファイルが変わっても、空譜面の成功結果を残さない。
            // ここは到着の幕の途中で走る(幕の間は次の Load を断る)ので、開ききってから戻す。待つ間は Esc でも動かさない。
            Debug.LogWarning("GamePlayManager: 遊べるノーツが無いため選曲へ戻ります。");
            finished = true;
            while (ScreenTransition.IsBusy) yield return null;
            ScreenTransition.Load("SongSelect", ScreenTransition.Style.Back);
            yield break;
        }
        GameSession.SelectedSongId = songId;
        GameSession.ResetResult();
        stagePerformance = StagePerformanceTimeline.Load(songId);

        // 音源を先にロードして、譜面長を音源に合わせて切り詰められるようにする。
        loadedSongId = songId;
        yield return LoadAudio(songId);
        // 曲ごとの鳴らす範囲(仕様面の点検 7)。頭の無音を飛ばす秒(escapade の 5 秒)と音が鳴り終わる秒。
        // stage.json の audioSha256 が今の音源と一致するときだけ使う。譜面の切り詰めは今までどおり音源の長さで行う。
        var playRange = stagePerformance.ResolvePlayRange(songPlayer.Duration,
            SongLoudness.SourceMatches(Path.Combine(Application.streamingAssetsPath, "Songs", songId), stagePerformance.audioSha256));
        musicEndSeconds = playRange.End;
        songPlayer.ProgressEndSeconds = playRange.End;

        // 譜面オフセット計算：
        //   chart.offsetMs（曲固有）+ extraOffsetSeconds（Inspector）+ GameSession.JudgmentOffsetMs（プレイヤー調整）
        double playerOffsetSec = GameSession.JudgmentOffsetMs / 1000.0;
        double effectiveExtraOffset = extraOffsetSeconds + playerOffsetSec;
        double offsetSec = chart.offsetMs / 1000.0 + effectiveExtraOffset;

        // 音源より後ろにあるノーツは「曲が終わった後も続く」原因なので、自動で削除。
        // ロングは後方の判定窓 (count-1) * secondsPerLongCut も延長するので、その分も考慮する。
        double audioLen = songPlayer.Duration;
        Debug.Log($"GamePlayManager: 音源ロード完了 length={audioLen:F1}s / 譜面ノーツ {chart.notes.Count} 個");
        if (trimChartToAudioLength && audioLen > 0.0)
        {
            float perCut = noteSpawner != null ? noteSpawner.secondsPerLongCut : 0.7f;
            int before = chart.notes.Count;
            int wouldRemove = 0;
            foreach (var n in chart.notes)
            {
                double eff = n.TimeSeconds + offsetSec;
                double endTime = eff + n.LingerSeconds(perCut);
                if (endTime > audioLen + trimGraceSeconds) wouldRemove++;
            }

            // 安全装置: 譜面の 1/4 超が音源長の外にある場合、音源側の異常
            // (部分同期・長さ誤検出)の可能性が高い。切り詰めると曲が途中で終わって
            // しまうため、譜面全体を残して警告だけ出す(末尾が無音でも最後まで遊べる)。
            if (before > 0 && wouldRemove > before / 4)
            {
                Debug.LogWarning($"GamePlayManager: 音源長({audioLen:F1}s)が譜面より大幅に短いため切り詰めを中止 " +
                    $"(対象 {wouldRemove}/{before} ノーツ)。音源ファイルの破損/部分同期の疑い");
            }
            else
            {
                chart.notes.RemoveAll(n =>
                {
                    double eff = n.TimeSeconds + offsetSec;
                    double endTime = eff + n.LingerSeconds(perCut);
                    return endTime > audioLen + trimGraceSeconds;
                });
                int removed = before - chart.notes.Count;
                if (removed > 0)
                {
                    Debug.Log($"GamePlayManager: 音源長({audioLen:F1}s) を超えるノーツ {removed} 個を切り詰めました (ロング後方判定窓を考慮)");
                }
            }
        }

        // 切り詰め後の lastNoteTime を計算（ロングの後方延長を含む「実際に最後にカットが必要な時刻」）
        lastNoteTime = 0.0;
        {
            float perCut = noteSpawner != null ? noteSpawner.secondsPerLongCut : 0.7f;
            foreach (var n in chart.notes)
            {
                double eff = n.TimeSeconds + offsetSec;
                double endTime = eff + n.LingerSeconds(perCut);
                if (endTime > lastNoteTime) lastNoteTime = endTime;
            }
        }

        // オフセットを先に NoteSpawner に渡してから SetChart
        noteSpawner.SetExtraOffsetSeconds(effectiveExtraOffset);
        noteSpawner.SetChart(chart);
        playFeedback = GameplayFeedbackPresenter.Create(noteSpawner, scoreManager);
        if (stageFloor != null) stageFloor.SetRhythm(chart);
        if (stageFloor != null) stageReactions = StageReactiveEffects.Create(stageFloor, noteSpawner, stagePerformance);
        chorusDrop = ChorusDrop.Create(transform, stagePerformance, songPlayer, chart.bpm,
            stageFloor != null ? stageFloor.transform : null, Object.FindFirstObjectByType<JudgmentSfx>());
        if (stageFloor != null) feverShow = StageFeverShow.Create(stageFloor, stagePerformance, chart);
        gateAccents = GateAccents.Create(noteSpawner, scoreManager, playFeedback);

        // 判定ゲートはPerfectが確定した瞬間だけ発光する。拍や単なる接触では光らせない。
        if (useOverhauledStage)
        {
            gatePerfectPulse = GateBeatPulse.Ensure(chart.bpm, noteSpawner.TotalOffsetSeconds, songPlayer, scoreManager);
        }

        // 小節線：BPM ガイドとして薄く流す
        if ((disableBarLines || stagePerformance.hideBarLines) && barLineSpawner != null)
        {
            barLineSpawner.gameObject.SetActive(false);
            barLineSpawner = null;
        }
        if (barLineSpawner != null)
        {
            barLineSpawner.overrideVisual = true;
            // 新テーマでは判定ゲートより確実に暗くする(視覚ヒエラルキー維持)
            barLineSpawner.lineAlpha = useOverhauledStage ? GameStageSkin.BarLineAlpha : barLineAlpha;
            barLineSpawner.lineThickness = useOverhauledStage ? GameStageSkin.BarLineThickness : barLineThickness;
            // 小節線はノーツと同じ速度で流す(シーンに古い値が焼き込まれていても揃うように)。
            barLineSpawner.approachTime = noteSpawner.approachTime;
            barLineSpawner.SetChart(chart, effectiveExtraOffset);
            barLineSpawner.FirstBarTime = playRange.Start;
        }

        scoreManager.songPlayer = songPlayer;
        scoreManager.Reset();
        scoreManager.Bind(noteSpawner);
        if (longNoteCutSfx != null) longNoteCutSfx.Bind(noteSpawner);
        if (goldNoteSfx != null)
        {
            goldNoteSfx.Bind(noteSpawner);
            goldNoteSfx.BindScore(scoreManager);
            // 金の会心では背景を2拍だけ金色へ寄せる(斬2)。
            float beat = chart.bpm > 0 ? 60f / chart.bpm : .5f;
            goldNoteSfx.OnCritical += (point, step) => { if (feverShow != null) feverShow.GoldFlash(beat); };
        }
        // 冒頭にも通常と同じ移動時間を確保する。譜面の時刻・オフセットは変えない。
        // 頭を飛ばす曲は、鳴らし始める位置から最初のノーツまでの秒で考える。
        double firstNoteFromPlayStart = chart.notes.Count > 0 ? noteSpawner.EffectiveTime(chart.notes[0]) - playRange.Start : double.NaN;
        double leadInSeconds = chart.notes.Count > 0 ? System.Math.Max(0.0, noteSpawner.approachTime - firstNoteFromPlayStart) : 0.0;
        // 幕が開く前に音やノーツを走らせず、カウントダウンの最初から見せる。
        while (ScreenTransition.IsBusy) yield return null;
        // 読み込みや幕の間に音の出力先が変わって曲の音が消えていたら、読み直してから始める(2026-10-22、タイトルへ戻さない)。
        // 読み直しの後から曲の予約までは待たない(予約のあとに変われば、曲を止めて読み直す側で扱う)。
        for (int attempt = 0; attempt < 3 && (ownedSongClip != null ? ownedSongClip.samples <= 0 : audioLostBeforeStart); attempt++)
        {
            audioLostBeforeStart = false;
            yield return LoadAudio(songId);
        }
        if (enableStartCountdown)
        {
            // START の演出が消えてから先頭ノーツを流す。移動時間と補正込みの到着時刻から
            // 楽曲開始を予約し、冒頭のノーツが文字や光に隠れたままゲートへ着くのを防ぐ。
            var countdown = GameStartCountdown.Ensure();
            double startDspTime = countdown.Begin(chart.bpm, GameSession.SelectedDifficulty, startCountdownVolume, noteSpawner.approachTime, firstNoteFromPlayStart);
            songPlayer.PlayScheduled(startDspTime, playRange.Start);
        }
        else
        {
            songPlayer.PlayScheduled(AudioSettings.dspTime + System.Math.Max(0.01, songPlayer.startDelay) + leadInSeconds, playRange.Start);
        }
        ready = true;
    }

    private void EnsureLongNoteCutSfx()
    {
        if (longNoteCutSfx != null) return;
        longNoteCutSfx = Object.FindFirstObjectByType<LongNoteCutSfx>();
        if (longNoteCutSfx != null) return;
        var go = new GameObject("LongNoteCutSfx", typeof(AudioSource), typeof(LongNoteCutSfx));
        go.transform.SetParent(transform, false);
        longNoteCutSfx = go.GetComponent<LongNoteCutSfx>();
    }

    private void EnsureInputPoint()
    {
        // 生成と設定は共通ヘルパーへ(タイトル/曲選択とも同じ構成を使う)
        InputPoint.EnsureInstance();

        // SaberInputBridge は world 座標がそのまま入る前提に切り替え。
        // 受信状態に応じて UDP / マウス を SaberInputBridge.Update が自動で切り替える。
        // 2本構成でも全 Bridge に適用する(マウスフォールバックは棒1のみ:2本がマウスに重ならないように)。
        foreach (var bridge in Object.FindObjectsByType<SaberInputBridge>(FindObjectsSortMode.None))
        {
            bridge.pixelsToWorld = 1.0f;
            bridge.useInputPoint = true;
            // UDP 無音時はマウスに戻る(棒1のみ)。カメラの位置が届いている台(本番)では、曲の途中でマウスを剣の代わりにしない(仕様面の点検 4)。
            bridge.fallbackToMouse = bridge.stickIndex == 1 && !CameraStation;
        }
        mouseFallbackLocked = CameraStation;
    }

    private void EnsureUdpImuBridge()
    {
        // Menuから維持されたtransportを再利用。Scene localの判定componentは別物。
        UdpImuBridge.EnsurePersistent(ImuBleServiceSettings.AutoStartBleBridge);
    }

    private void EnsureSwing8DirectionLogger()
    {
        if (Object.FindFirstObjectByType<Swing8DirectionLogger>() != null) return;
        var go = new GameObject("Swing8DirectionLogger");
        go.transform.SetParent(transform, false);
        go.AddComponent<Swing8DirectionLogger>();
    }

    private void ApplyCameraPose()
    {
        var cam = Camera.main;
        if (cam == null) return;

        Vector3 position = useSlightTopDownView
            ? topDownCameraPosition
            : cameraPosition;
        Vector3 rotation = useSlightTopDownView
            ? topDownCameraRotationEuler
            : cameraRotationEuler;

        cam.transform.SetPositionAndRotation(position, Quaternion.Euler(rotation));
    }

    private void EnsureGoldNoteSfx()
    {
        if (goldNoteSfx != null) return;
        goldNoteSfx = Object.FindFirstObjectByType<GoldNoteSfx>();
        if (goldNoteSfx != null) return;
        var go = new GameObject("GoldNoteSfx", typeof(AudioSource), typeof(GoldNoteSfx));
        go.transform.SetParent(transform, false);
        goldNoteSfx = go.GetComponent<GoldNoteSfx>();
    }

    private void SimplifyJudgeGuide()
    {
        var guide = GameObject.Find("JudgeGuide");
        if (guide == null) return;
        var toRemove = new System.Collections.Generic.List<Transform>();
        foreach (Transform child in guide.transform)
        {
            string n = child.name;
            foreach (var prefix in JudgeGuideStripPrefixes)
            {
                if (n.StartsWith(prefix)) { toRemove.Add(child); break; }
            }
        }
        foreach (var t in toRemove)
        {
            if (Application.isPlaying) Destroy(t.gameObject);
            else DestroyImmediate(t.gameObject);
        }

        // JudgePanel を「ノーツを切るとこ」と一目で分かる薄い色面に整える
        var panel = guide.transform.Find("JudgePanel");
        if (panel != null)
        {
            var mr = panel.GetComponent<MeshRenderer>();
            if (mr != null && mr.sharedMaterial != null)
            {
                var m = mr.material; // インスタンス化（共有 Material を破壊しない）
                Color c = judgePanelTint;
                c.a = judgePanelAlpha;
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
                else m.color = c;
                // ほんのり emission を入れて視認性を確保
                if (m.HasProperty("_EmissionColor"))
                {
                    m.EnableKeyword("_EMISSION");
                    Color e = judgePanelTint; e.a = 1f;
                    m.SetColor("_EmissionColor", e * 0.25f);
                }
            }

            if (addJudgePanelOuterGlow)
            {
                AddJudgePanelOuterGlow(guide.transform, panel);
            }

            if (addBeatReferenceLine)
            {
                AddBeatReferenceLine(guide.transform, panel);
            }
        }
    }

    // 判定面に「ビート参照線」を1本固定で置く。流れてきた小節線がこれに重なった瞬間がビートのタイミング。
    // 小節線と同じく端から端まで、白い細い線。
    private void AddBeatReferenceLine(Transform guideTransform, Transform panel)
    {
        if (guideTransform.Find("BeatReferenceLine") != null) return; // 冪等

        var line = GameObject.CreatePrimitive(PrimitiveType.Cube);
        line.name = "BeatReferenceLine";
        line.transform.SetParent(guideTransform, false);
        // パネルとほぼ同じ位置だが、ほんの少し前面に出して埋まらないように
        line.transform.localPosition = panel.localPosition + new Vector3(0f, 0f, -0.005f);
        // 横幅はパネルと同じ、Y方向に細い帯、Z方向は薄い板
        Vector3 ps = panel.localScale;
        line.transform.localScale = new Vector3(ps.x * 1.02f, beatReferenceThickness, 0.01f);

        var col = line.GetComponent<BoxCollider>();
        if (col != null) Destroy(col);

        var mr = line.GetComponent<MeshRenderer>();
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var m = new Material(shader);
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
        if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
        m.renderQueue = 3010; // パネルや小節線より少しだけ前
        Color c = new Color(1f, 1f, 1f, beatReferenceAlpha);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        else m.color = c;
        if (m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", new Color(1f, 1f, 1f, 1f) * 0.6f);
        }
        mr.sharedMaterial = m;
    }

    // パネルの一回り外側に、より薄い同色のパネルを置いて、エッジを柔らかく外側へ伸ばす。
    // 太い「枠」ではなく、滲んだような縁を付ける狙い。
    private void AddJudgePanelOuterGlow(Transform guideTransform, Transform panel)
    {
        if (guideTransform.Find("JudgePanelGlow") != null) return; // 冪等

        var src = panel.GetComponent<MeshRenderer>();
        if (src == null) return;

        var glow = GameObject.CreatePrimitive(PrimitiveType.Cube);
        glow.name = "JudgePanelGlow";
        glow.transform.SetParent(guideTransform, false);
        glow.transform.localPosition = panel.localPosition + new Vector3(0f, 0f, 0.01f);
        // 元パネルより一回り大きく、厚みは極薄
        Vector3 ps = panel.localScale;
        glow.transform.localScale = new Vector3(ps.x * 1.04f, ps.y * 1.08f, 0.005f);

        var col = glow.GetComponent<BoxCollider>();
        if (col != null) Destroy(col);

        var mr = glow.GetComponent<MeshRenderer>();
        // src と同じシェーダーで透明版を新規生成
        var shader = src.sharedMaterial != null ? src.sharedMaterial.shader : Shader.Find("Universal Render Pipeline/Lit");
        var m = new Material(shader);
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
        if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
        if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
        m.renderQueue = 3000;
        Color c = judgePanelTint; c.a = judgePanelAlpha * 0.35f;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        else m.color = c;
        if (m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            Color e = judgePanelTint; e.a = 1f;
            m.SetColor("_EmissionColor", e * 0.6f);
        }
        mr.sharedMaterial = m;
    }

    private IEnumerator LoadAudio(string songId)
    {
        // 読み込み失敗時にシーンや前の曲の音源を誤って鳴らさない。
        ReleaseOwnedSongClip();
        songPlayer.Stop();
        songPlayer.Clip = null;
        songPlayer.SetLoudnessGain(1);
        yield return RequestSongClip(songId, (clip, gain) =>
        {
            ReleaseOwnedSongClip();
            ownedSongClip = clip;
            songPlayer.Clip = clip;
            songPlayer.SetLoudnessGain(gain);
        });
    }

    // 音の出力先が変わったあと(AudioOutputRecovery)、曲の音を読み直して止めた位置から鳴らせるようにする。
    // 読み直せたら 3・2・1 を数え始める(剣を見失って止めているときは、そちらの見張りが戻してから再開する)。読めなければ音なしで続ける。
    IEnumerator ReloadSongAndResume()
    {
        audioResumeAt = double.NaN;
        AudioClip loaded = null;
        float loadedGain = 1;
        if (!string.IsNullOrEmpty(loadedSongId))
            yield return RequestSongClip(loadedSongId, (clip, gain) => { loaded = clip; loadedGain = gain; });
        if (loaded != null)
        {
            var dead = ownedSongClip;
            ownedSongClip = loaded;
            songPlayer.ReplaceClipWhilePaused(loaded);
            songPlayer.SetLoudnessGain(loadedGain);
            if (dead != null && dead != loaded) Destroy(dead);
        }
        else
        {
            Debug.LogWarning("GamePlayManager: 音の出力先が変わったあと、曲を読み直せませんでした。音なしで続けます");
            // スタッフが気づけるよう画面にも出す(「続きから再開します」の知らせのまま無音で続いていた。2026-10-23 点検)。
            OperatorNotice.Show(AudioOutputRecovery.ReloadFailedNotice, AudioOutputRecovery.NoticeSeconds + 2f);
        }
        audioResumeAt = Time.realtimeSinceStartupAsDouble + (trackingWatch.Paused ? 0 : PlayTrackingWatch.ResumeCountdownSeconds);
        audioReload = null;
    }

    // 曲の音を StreamingAssets から読み、読めた音と音量の係数を found に渡す。中身の無い音(読む間に出力先が変わったなど)は使わない。
    private IEnumerator RequestSongClip(string songId, System.Action<AudioClip, float> found)
    {
        string dir = Path.Combine(Application.streamingAssetsPath, "Songs", songId);
        foreach (var playback in SongLoudness.CandidatesInFolder(dir))
        {
            string full = playback.Path;
            AudioType type = GuessType(full);
            // #・%・日本語・空白を含む曲フォルダーも、選曲プレビューと同じ方法で扱う。
            using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(new System.Uri(full).AbsoluteUri, type))
            {
                pendingAudioRequest = req;
                req.timeout = 20;
                yield return req.SendWebRequest();
                pendingAudioRequest = null;
                if (req.result == UnityWebRequest.Result.Success)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
                    // 中身の無い音(読み込み中に音の出力先が変わったときなど)は使わない(2026-10-22)。
                    if (clip != null && (clip.samples <= 0 || clip.frequency <= 0))
                    {
                        Debug.LogWarning($"Audio has no samples {full}");
                        Destroy(clip);
                        clip = null;
                    }
                    if (clip != null)
                    {
                        found(clip, playback.Gain);
                        yield break;
                    }
                }
                Debug.LogWarning($"Failed to load audio {full}: {req.error}");
            }
        }
        Debug.LogWarning($"No audio found under {dir}");
    }

    private void ReleaseOwnedSongClip()
    {
        if (ownedSongClip == null) return;
        if (songPlayer != null && songPlayer.Clip == ownedSongClip)
        {
            songPlayer.Stop();
            songPlayer.Clip = null;
        }
        Destroy(ownedSongClip);
        ownedSongClip = null;
    }

    private void OnDestroy()
    {
        AbortAudioRequest();
        ReleaseOwnedSongClip();
    }

    // 最後まで遊んだあと、終わりの演出(FULL COMBO・コンボ点)の間や結果画面へ移る間にアプリを終了しても
    // (Cmd+Q・ウィンドウの×・運営の終了)、点と3つの記録(実績・通算の順位・HI-SCORE)を残す。Esc 2回と同じ後始末で、
    // どちらも同じ回を二重に残さない(Codex の点検で 2026-10-22 に追加)。強制終了は救えない。
    void OnApplicationQuit()
    {
        if (!completing || inCalibration || inTutorial) return;
        CommitFinalResult();
        GameSession.RecordCompletedHighScore();
    }

    // 読み込み中の音源の要求。途中で抜けると(StopAllCoroutines・シーンの破棄)using の後始末が走らないので、ここで止めて手放す。
    UnityWebRequest pendingAudioRequest;
    void AbortAudioRequest()
    {
        if (pendingAudioRequest == null) return;
        pendingAudioRequest.Abort();
        pendingAudioRequest.Dispose();
        pendingAudioRequest = null;
    }

    private AudioType GuessType(string name)
    {
        name = name.ToLowerInvariant();
        if (name.EndsWith(".ogg")) return AudioType.OGGVORBIS;
        if (name.EndsWith(".wav")) return AudioType.WAV;
        if (name.EndsWith(".mp3")) return AudioType.MPEG;
        return AudioType.UNKNOWN;
    }

    // 本編シーンで毎フレーム呼ばれる単一の Update。
    // 順序：Esc → 受信の見張り(止める・再開) → 現在の受付窓 → 判定 → 譜面進行 → 終了チェック。
    void Update()
    {
        if (HandleEscape()) return;
        LockMouseFallbackOnceCameraSeen();
        TickTrackingWatch();
        if (finished || !ready || (songPlayer != null && songPlayer.IsPaused))
        {
            // 一時停止中(剣の見失い・音の立て直し)・終了後も、ゲートまわりの光は最後まで消えていく(広がりかけの形で固めない)。
            if (ready && gateAccents != null) gateAccents.Tick(Time.deltaTime);
            return;
        }

        // カメラの遅れは判定の時計だけで差し引く(受付窓・刃の判定・Miss の3か所。ノーツの位置と音は曲の時計のまま)。
        if (noteSpawner != null) noteSpawner.JudgeLatencySeconds = CameraInputActive() ? cameraJudgeLatencySeconds : 0;

        // 判定と採点が同じ曲時計を見るよう、前フレームの受付可否を更新する。
        // 負の開始予約時刻にも対応し、停止時の SongTime=0 では巻き戻さない。
        if (songPlayer != null && songPlayer.IsScheduled && noteSpawner != null)
            noteSpawner.RefreshJudgmentWindows(songPlayer.SongTime);

        // 1. セーバー判定(2本構成なら両方)
        if (cameraImuJudgment != null)
            cameraImuJudgment.Tick(SwingMonotonicClock.ToSeconds(SwingMonotonicClock.Timestamp),
                songPlayer != null ? songPlayer.SongTime : 0,
                songPlayer != null && songPlayer.IsScheduled && Time.timeScale > 0f);
        // 採点は刃がノーツにいちばん近づいた時刻。受付窓と同じ判定の時計を渡す(開始予約前は従来どおり)。
        double judgeTime = songPlayer != null && songPlayer.IsScheduled
            ? (noteSpawner != null ? noteSpawner.JudgeClock(songPlayer.SongTime) : songPlayer.SongTime)
            : double.NaN;
        if (cutJudge != null) cutJudge.RunJudge(judgeTime);
        if (cutJudge2 != null) cutJudge2.RunJudge(judgeTime);
        if (gatePerfectPulse != null) gatePerfectPulse.Tick(Time.unscaledTimeAsDouble);
        // 背景の可動部もこのループで駆動。曲停止中に独立して進行させない。
        if (songPlayer != null && songPlayer.IsPlaying)
        {
            double time = songPlayer.SongTime;
            float chorus = stagePerformance.Evaluate(time);
            if (stageFloor != null) stageFloor.Tick(time,chorus,stagePerformance.EvaluateLightFormation(time),stagePerformance.EvaluateVaultCurtain(time));
            if (foundryStageMotion != null) foundryStageMotion.Tick(time,chorus);
            if (scenicStageWorld != null) scenicStageWorld.Tick(time,chorus,stagePerformance.EvaluateEclipse(time));
            if (stageReactions != null) stageReactions.Tick(time);
            if (chorusDrop != null && !inCalibration) chorusDrop.Tick(time, Time.unscaledDeltaTime);
            if (feverShow != null) feverShow.Tick(time, Time.unscaledDeltaTime);
        }
        // ゲートまわりの光は、曲の止まっている開始前・終了後も最後まで消えていくようにする。
        if (gateAccents != null) gateAccents.Tick(Time.deltaTime);

        // 2a. キャリブレーション分岐：時計は AudioSettings.dspTime ベース、終了せずループ
        if (inCalibration)
        {
            UpdateCalibration();
            return;
        }

        // 2a'. チュートリアル分岐：120 BPM のループに合わせて練習の型を流す。終わりは TutorialController が選曲へ移す。
        if (inTutorial)
        {
            if (tutorial != null) tutorial.Tick(Time.unscaledDeltaTime);
            return;
        }

        // 2b. 開始予約中の負の曲時計でも先読みする。停止後はSongTime=0へ巻き戻して進めない。
        if (songPlayer.IsScheduled)
        {
            noteSpawner.Tick(songPlayer.SongTime);
            if (barLineSpawner != null) barLineSpawner.Tick(songPlayer.SongTime);
        }

        if (playFeedback != null)
            playFeedback.Tick(Time.unscaledDeltaTime, songPlayer.SongTime, MusicEnd, lastNoteTime);

        // 3. 終了判定：最終ノーツ通過＋余韻、かつアクティブなノーツがゼロ。
        // 曲の終わりは「音が鳴り終わる秒」(曲ごと。無ければ音源の長さ)から待つ(仕様面の点検 7)。
        double endThreshold = System.Math.Max(MusicEnd + endWaitSeconds,
                                              lastNoteTime + outroSeconds);
        if (songPlayer.IsPlaying && songPlayer.SongTime >= endThreshold
            && noteSpawner.AliveCount == 0 && noteSpawner.NextIndex >= noteSpawner.TotalNoteCount)
        {
            finished = true;
            FinishGame();
        }
    }

    // カメラ(PhoneSaber)の値で従来の判定を使っている棒があるか。マウス・IMU 判定・入力なしでは false。
    bool CameraInputActive() => UsesCamera(cutJudge) || UsesCamera(cutJudge2);

    static bool UsesCamera(SaberCutJudge judge) =>
        judge != null && judge.isActiveAndEnabled && !judge.ExternalJudgment
        && judge.bladeProvider != null && judge.bladeProvider.ReceivingCamera;

    // 刃がノーツの上を通っている最中か(NoteSpawner.HoldMiss)。
    bool SaberCuttingThrough(CuttableNote note) =>
        (cutJudge != null && cutJudge.IsCuttingThrough(note)) || (cutJudge2 != null && cutJudge2.IsCuttingThrough(note));

    private void ReturnToTitle()
    {
        if (!ScreenTransition.Load("Title", ScreenTransition.Style.Back)) return;
        // 途中離脱は終了処理へ流さず、結果・実績を保存しない。開始待ちや終了演出も打ち切る。
        finished = true;
        leaving = true;
        StopAllCoroutines();
        AbortAudioRequest();
        if (songPlayer != null) songPlayer.Stop();
        if (chorusDrop != null) chorusDrop.ResetEffects();
        if (feverShow != null) feverShow.ResetState();
        if (gateAccents != null) gateAccents.ResetState();
        var countdown = Object.FindFirstObjectByType<GameStartCountdown>();
        if (countdown != null) Destroy(countdown.gameObject);
    }

    // 来場者の画面に共通の「Esc を2秒以内に2回でタイトルへ」(仕様面の点検 2)。本編・練習・判定調整・終わりの演出の間も、
    // この画面ではここだけが Esc を受ける(判定調整の覆いは Esc を使わない)。幕の間と、戻ると決めたあとは受けない。
    bool HandleEscape()
    {
        if (leaving || ScreenTransition.IsBusy || !InputManager.EscapeGetDown) return false;
        bool calibrating = inCalibration || GameSession.IsCalibrationMode;
        if (!escapeGate.PressAndNotify(Time.unscaledTimeAsDouble, calibrating ? EscapeTwiceGate.ToTitleNotice : EscapeTwiceGate.AbortNotice))
            return false;
        if (calibrating)
        {
            // 判定調整は下書きを捨て、再生を止めてからタイトルへ(選曲の残り時間・曲の引き継ぎも消える)。
            if (calibration != null) calibration.DiscardAndExitTo("Title"); else ExitCalibration("Title");
            if (ScreenTransition.IsBusy) leaving = true;
        }
        else
        {
            // 最後まで遊んだ回は、終わりの演出を飛ばしても点と3つの記録(実績・通算の順位・HI-SCORE)を残してから出る。
            if (completing) { CommitFinalResult(); GameSession.RecordCompletedHighScore(); }
            ReturnToTitle();
        }
        return true;
    }

    // カメラ(PhoneSaber)の位置が、この場面の始まりの少し前(30秒)から届いているか。
    // 届いていなければ(スマホを止めている・開発用)、今までどおりマウスで遊べる。
    bool CameraStation
    {
        get
        {
            var input = InputPoint.Instance;
            return input != null && (input.LastReceivedTime >= trackingSince || input.LastReceivedTime2 >= trackingSince);
        }
    }

    // 曲の途中で初めてカメラの値が届いたときも、そこからはマウスに戻さない(仕様面の点検 4)。
    void LockMouseFallbackOnceCameraSeen()
    {
        if (mouseFallbackLocked || !CameraStation) return;
        mouseFallbackLocked = true;
        foreach (var bridge in Object.FindObjectsByType<SaberInputBridge>(FindObjectsSortMode.None)) bridge.fallbackToMouse = false;
    }

    // 受信の表示と、見失ったときの一時停止・再開(仕様面の点検 4)。曲が止まっている間も毎フレーム動かす。
    // 2026-10-22: 片方の色だけ届かないときのスタッフ向けの1行、止まっている間の「確認中」・暗い幕・3・2・1 の輪と音、再開の表示を足した。
    void TickTrackingWatch()
    {
        // 中断・結果へ進むと決めたあと(幕が閉じる間)は、見張りも表示も止める。数えかけの 3・2・1 の刻みや再開の音を鳴らさない
        // (Codex の点検で 2026-10-22 に追加)。音の出力先が変わって曲を止めている間は、そちらの表示と再開だけを動かす。
        if (trackingView == null) return;
        if (leaving || finished || audioChangeHold)
        {
            trackingView.ShowRecovery(false, null, 0, 0);
            if (audioChangeHold && !leaving && !finished) TickAudioResume();
            return;
        }
        var input = InputPoint.Instance;
        double now = Time.realtimeSinceStartupAsDouble;
        if (input != null)
        {
            if (input.LastReceivedTime >= trackingSince && input.HasValidStickEndpoints) trackingWatch.ObserveUsable(0, input.LastReceivedTime);
            if (input.LastReceivedTime2 >= trackingSince && input.HasValidStickEndpoints2) trackingWatch.ObserveUsable(1, input.LastReceivedTime2);
        }
        bool active = ready && !finished && !leaving && !inCalibration && songPlayer != null && songPlayer.IsScheduled;
        if (active && double.IsNaN(playingSince) && songPlayer.IsPlaying) playingSince = now;
        double songTime = songPlayer != null ? songPlayer.SongTime : 0;
        bool canPause = !inTutorial && !inCalibration && ready && !finished && !leaving
            && songPlayer != null && songPlayer.IsPlaying && NotesComing(songTime);
        int inside = trackingWatch.State == PlayTrackingWatch.Phase.Waiting ? trackingView.RecoveryInsideMask() : 0;
        var request = trackingWatch.Step(now, canPause, inside);
        if (request == PlayTrackingWatch.Request.Pause) { PauseForTracking(); trackingView.PlayPauseCue(); }
        else if (request == PlayTrackingWatch.Request.Resume)
        {
            ResumeAfterTracking(); trackingView.PlayResumeCue();
            resumedShownUntil = now + PlayTrackingStatus.ResumedSeconds;
        }
        string status = input != null ? MenuSaberStatus.Status(now, input.LastReceivedTime, input.LastReceivedTime2) : "";
        bool allLost = trackingWatch.AllLost(now);
        string head = null; float ring = -1; bool dimmed = false;
        if (trackingWatch.State == PlayTrackingWatch.Phase.Waiting)
        {
            head = trackingWatch.PoseMask != 0 ? PlayTrackingStatus.ReadyText :
                trackingWatch.Receiving(now) ? PlayTrackingStatus.CheckingText : PlayTrackingStatus.PausedText;
            dimmed = true;
        }
        else if (trackingWatch.State == PlayTrackingWatch.Phase.Countdown)
        {
            double remaining = trackingWatch.CountdownRemaining(now);
            head = PlayTrackingStatus.ResumeText(remaining); dimmed = true;
            ring = (float)(1 - remaining / PlayTrackingWatch.ResumeCountdownSeconds);
            // 3・2・1 の数字が変わるたびに刻みを1回(数える間にまた途切れたら、次は 3 から)。
            int number = System.Math.Max(1, (int)System.Math.Ceiling(remaining));
            if (number != countdownShown) { countdownShown = number; trackingView.PlayCountTick(); }
        }
        else if (now < resumedShownUntil) head = PlayTrackingStatus.ResumedText;
        if (trackingWatch.State != PlayTrackingWatch.Phase.Countdown) countdownShown = 0;
        // 片方の色の警告は、止まっていないとき・全部見失ったスタッフ向けの1行が出ていないときだけ(出ていれば上の中央にまとめる)。
        string warning = active && !trackingWatch.Paused && !allLost ? ColorWarning(now, songTime) : null;
        if (active && resumedSongWait.Read(songTime, songPlayer.PlayStartSeconds,
                trackingWatch.Paused || songPlayer.IsPaused, out _, out float waitingRing))
        {
            head = resumedSongWait.Caption; ring = waitingRing;
        }
        trackingView.Show(status, head, allLost, warning, ring, dimmed);
        trackingView.ShowRecovery(trackingWatch.Paused, input, now, trackingWatch.PoseMask);
    }

    // 片方の色だけ届かないとき(2026-10-22)。その色のノーツが来ているのに 3 秒届かなければ、スタッフ向けの1行を返す(止めはしない)。
    // 使わない手を下ろしている区間でまちがって止めないよう、止めるのは今までどおり「届いていた色がすべて届かない」ときだけ。
    string ColorWarning(double now, double songTime)
    {
        bool red = false, blue = false, neverSeen = true;
        for (int feed = 0; feed < 2; feed++)
        {
            var hand = FeedHand(feed);
            if (!trackingWatch.ShouldWarn(feed, now, NotesNeed(songTime, hand), WarnNeverSeenColors, playingSince)) continue;
            if (hand == SaberHand.Left) blue = true; else red = true;
            neverSeen &= !trackingWatch.Seen(feed);
        }
        return PlayTrackingStatus.WarningText(red, blue, neverSeen);
    }

    // 受信口(0=1本目、1=2本目)と手。1本目は stick1Hand(既定は右手=赤)。
    SaberHand FeedHand(int feed) => feed == 0 ? stick1Hand : stick1Hand == SaberHand.Left ? SaberHand.Right : SaberHand.Left;

    // 一度も届いていない色も知らせるのは、会場の Player だけ(エディターはマウスで試すので出さない)。
    static bool WarnNeverSeenColors => !Application.isEditor;

    // ノーツが来ているか(まだ切られていない・MISS でもないノーツが画面にあるか、1 秒以内に流れ始めるか)。hand=Any は色を問わない。
    // MISS のノーツは後ろへ流れて 1.5 秒残るので数えない(2026-10-22、休みの区間でまちがって止まっていた)。
    bool NotesNeed(double songTime, SaberHand hand)
    {
        if (noteSpawner == null) return false;
        if (PlayTrackingWatch.LiveNeeds(noteSpawner.LiveNotes, hand)) return true;
        var chart = noteSpawner.CurrentChart;
        return chart != null && PlayTrackingWatch.UpcomingNeeds(chart.notes, noteSpawner.NextIndex, noteSpawner.EffectiveTime,
            noteSpawner.approachTime, songTime, hand);
    }

    // ノーツが画面にあるか、1 秒以内に流れ始めるか。休みの区間で剣を下ろしているだけなら曲を止めない。
    bool NotesComing(double songTime) => NotesNeed(songTime, SaberHand.Any);

    void PauseForTracking()
    {
        if (songPlayer == null || !songPlayer.IsPlaying) return;
        Debug.Log($"GamePlayManager: 剣の位置が届かないので一時停止 songTime={songPlayer.SongTime:F2}s");
        songPlayer.Pause();
        songPlayer.HeldByTracking = true;
        // 予約したサビの音や下げた音量を戻す(再開後、まだ来ていない入口は ChorusDrop が予約し直す)。
        if (chorusDrop != null) chorusDrop.ResetEffects();
        // 区間の終わりで鳴り始めた「シュー」も止める(止めたあとまで鳴り続けていた。Codex の点検で 2026-10-22 に追加)。
        if (feverShow != null) feverShow.StopEndSound();
    }

    // 止める前の刃の履歴(触れていたノーツ・振りの向き)を持ち越さず、再開してからの振りで判定する。
    // カメラと IMU の照合は、曲の時計が止まった分のずれを見て自分で履歴を消す(CameraImuJudgment.Tick)。
    void ResumeAfterTracking()
    {
        if (songPlayer == null) return;
        foreach (var judge in new[] { cutJudge, cutJudge2 })
            if (judge != null && judge.saber != null) judge.saber.ResetTo(judge.saber.transform.position);
        songPlayer.HeldByTracking = false;
        songPlayer.Resume();
        resumedSongWait.Begin(songPlayer.SongTime, songPlayer.PlayStartSeconds);
        Debug.Log($"GamePlayManager: 剣の位置が戻ったので再開 songTime={songPlayer.SongTime:F2}s");
    }

    // 音の出力先が変わった瞬間(AudioOutputRecovery が 0.5 秒後に立て直す)。本編の途中なら、その間に曲が進んだり終わって
    // 「完了」と記録されたり、剣の見張りが曲を再開したりしないよう、すぐ止めておく(Codex の点検で 2026-10-22 に追加)。
    // 曲を始める前(読み込み・幕の間)は止める曲がないので、曲を予約する直前に音の中身を確かめて読み直す(Start)。
    public void HoldForAudioChange()
    {
        if (leaving || completing || finished || inCalibration || inTutorial) return;
        if (!ready) { audioLostBeforeStart = true; return; }
        // 読み直しや 3・2・1 の途中でまた変わったら、読み直しからやり直す(落ち着いたら HandleAudioOutputChanged が始める)。
        // 出していた数字も忘れる。「3」の途中で変わると、数え直した「3」の刻みが鳴らなかった(2026-10-22)。
        if (audioReload != null) { StopCoroutine(audioReload); audioReload = null; AbortAudioRequest(); }
        audioResumeAt = double.NaN;
        countdownShown = 0;
        if (audioChangeHold) return;
        audioChangeHold = true;
        if (songPlayer != null && songPlayer.IsScheduled)
        {
            songPlayer.Pause();
            songPlayer.HeldByTracking = true;
        }
        // 開幕の 3・2・1・START の途中なら取り除く。曲は止めたのに START が「読み直しています」と重なって出ていた(2026-10-22)。
        // 再開の合図は読み直したあとの 3・2・1 にまとめる。曲は残りの待ち時間ごと止めてあるので、最初のノーツの移動時間は変わらない。
        // ゲートと床の色は OnDisable/OnDestroy が戻し、予約したクリック音もオブジェクトごと消える(Esc で中断するときと同じ)。
        var countdown = Object.FindFirstObjectByType<GameStartCountdown>();
        if (countdown != null) Destroy(countdown.gameObject);
        // 予約したサビの音や下げた音量を戻し、鳴り始めた「シュー」も止める(剣を見失って止めたときと同じ)。
        if (chorusDrop != null) chorusDrop.ResetEffects();
        if (feverShow != null) feverShow.StopEndSound();
    }

    // 音の出力先が変わった(AudioOutputRecovery、2026-10-22)。画面は戻さない(同日ユーザー「戻るのやめて」)。
    // 曲の途中(カウントイン・一時停止を含む)は、止めたまま曲を読み直し、3・2・1 で続きから再開する。記録もそのまま続く。
    // 練習は同梱の曲なので、時計はそのままに今の位置から鳴らし直す。終わりの演出の間はそのまま続ける(曲の残りは鳴らさない)。
    // 判定調整は CalibrationController がその場で中断して音を作り直す。作った効果音は鳴らすときに作り直す(ProceduralSfx.Revive)。
    public AudioOutputRecovery.Action HandleAudioOutputChanged()
    {
        var action = AudioOutputRecovery.PlanGame(inCalibration || GameSession.IsCalibrationMode, inTutorial, completing, finished, leaving, ready);
        switch (action)
        {
            case AudioOutputRecovery.Action.ResumeSong:
                if (!audioChangeHold) HoldForAudioChange();
                if (audioReload != null) StopCoroutine(audioReload);
                audioReload = StartCoroutine(ReloadSongAndResume());
                break;
            case AudioOutputRecovery.Action.InPlace:
                if (inTutorial && songPlayer != null) songPlayer.RestartSource();
                break;
        }
        return action;
    }

    // 音の出力先が変わって曲を止めている間の表示と、読み直したあとの 3・2・1(剣を見失ったときと同じ見た目と音)。
    // 数え終えたら続きから再開する。剣を見失って止めていたときは、そちらの見張りが戻ってから再開する。
    void TickAudioResume()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        var input = InputPoint.Instance;
        string status = input != null ? MenuSaberStatus.Status(now, input.LastReceivedTime, input.LastReceivedTime2) : "";
        if (double.IsNaN(audioResumeAt))
        {
            trackingView.Show(status, AudioReloadingText, false, null, -1, true);
            return;
        }
        double remaining = audioResumeAt - now;
        if (remaining > 0)
        {
            int number = System.Math.Max(1, (int)System.Math.Ceiling(remaining));
            if (number != countdownShown) { countdownShown = number; trackingView.PlayCountTick(); }
            trackingView.Show(status, PlayTrackingStatus.ResumeText(remaining), false, null,
                (float)(1 - remaining / PlayTrackingWatch.ResumeCountdownSeconds), true);
            return;
        }
        audioChangeHold = false;
        audioResumeAt = double.NaN;
        countdownShown = 0;
        // 読み直す間に剣を見失った場合は、再開音を鳴らさず既存の追跡待ちへ渡す。
        // 保留中は見張りを止めているため、完了時の受信を観測してから同じ条件で判定する。
        if (!trackingWatch.Paused && songPlayer != null && songPlayer.SongTime >= songPlayer.PlayStartSeconds)
        {
            if (input != null && input.LastReceivedTime >= trackingSince && input.HasValidStickEndpoints) trackingWatch.ObserveUsable(0, input.LastReceivedTime);
            if (input != null && input.LastReceivedTime2 >= trackingSince && input.HasValidStickEndpoints2) trackingWatch.ObserveUsable(1, input.LastReceivedTime2);
            trackingWatch.Step(now, NotesComing(songPlayer.SongTime));
        }
        if (trackingWatch.Paused) { trackingWatch.RestartCountdown(now); return; }
        ResumeAfterTracking();
        trackingView.PlayResumeCue();
        resumedShownUntil = now + PlayTrackingStatus.ResumedSeconds;
        // 音の復旧が終わった同じフレームから、残りの無音時間を表示する。
        if (resumedSongWait.Read(songPlayer.SongTime, songPlayer.PlayStartSeconds, false, out _, out float progress))
            trackingView.Show(status, resumedSongWait.Caption, false, null, progress);
    }

    // 最後まで遊んだ回の点と判定を確定し、実績・通算の順位を残す(一度だけ。HI-SCORE は結果画面が残す)。
    void CommitFinalResult()
    {
        if (resultCommitted) return;
        resultCommitted = true;
        scoreManager.FinalizeScore();
        GameSession.FinalScore = scoreManager.Score;
        GameSession.FinalMaxCombo = scoreManager.MaxCombo;
        GameSession.FinalComboBonus = scoreManager.ComboBonus;
        GameSession.FinalHit = scoreManager.HitCount;
        GameSession.FinalMiss = scoreManager.MissCount;
        GameSession.FinalPerfect = scoreManager.PerfectCount;
        GameSession.FinalGreat = scoreManager.GreatCount;
        GameSession.FinalGood = scoreManager.GoodCount;
        GameSession.FinalBad = scoreManager.BadCount;
        GameSession.FinalCutCount = scoreManager.CutCount;
        GameSession.RecordCompletedAchievements();
        GameSession.RecordCompletedDailyRanking();
    }

    // --- キャリブレーションモード実装 ---

    void StartCalibration()
    {
        inCalibration = true;
        // 本番と同じセーバー判定・位置・速度を保つ。調整画面だけの判定緩和は行わない。
        if (barLineSpawner != null)
        {
            barLineSpawner.gameObject.SetActive(false);
            barLineSpawner = null;
        }
        calibration = gameObject.AddComponent<CalibrationController>();
        calibration.Initialize(songPlayer, noteSpawner, scoreManager, extraOffsetSeconds);
        gatePerfectPulse = GateBeatPulse.Ensure(CalibrationProtocol.Bpm, 0, songPlayer, scoreManager);
    }

    public static ChartData SynthesizeCalibrationChart(float bpm, float durationSeconds, double firstNoteTimeSec)
    {
        var chart = new ChartData { bpm = bpm, offsetMs = 0f };
        double beatInterval = 60.0 / Mathf.Max(1f, bpm);
        double t = firstNoteTimeSec;
        while (t < durationSeconds)
        {
            chart.notes.Add(new NoteData
            {
                time = (float)(t * 1000.0),
                x = 0f,
                y = 0.5f,
                type = "tap",
                color = "red",
                direction = "none",
                count = 1
            });
            t += beatInterval;
        }
        return chart;
    }

    void UpdateCalibration()
    {
        if (calibration != null) calibration.Tick(Time.unscaledDeltaTime);
    }

    // --- チュートリアル実装 ---

    void StartTutorial()
    {
        inTutorial = true;
        // 舞台・刃 2 本・判定・NoteSpawner・床ガイドは上で本番と同じに用意済み。小節線だけ出さない。
        if (barLineSpawner != null)
        {
            barLineSpawner.gameObject.SetActive(false);
            barLineSpawner = null;
        }
        var sabers = new System.Collections.Generic.List<SaberTracker>();
        if (cutJudge != null && cutJudge.saber != null) sabers.Add(cutJudge.saber);
        if (cutJudge2 != null && cutJudge2.saber != null) sabers.Add(cutJudge2.saber);
        // 練習でも受信の表示とスタッフ向けの1行は出す(曲は止めない)。左上の見出しを避けて少し下に置く。
        trackingView = PlayTrackingStatus.Create(transform, 340);
        tutorial = gameObject.AddComponent<TutorialController>();
        tutorial.Initialize(songPlayer, noteSpawner, scoreManager, extraOffsetSeconds, sabers.ToArray(), longNoteCutSfx, goldNoteSfx);
        gatePerfectPulse = GateBeatPulse.Ensure(TutorialProgram.Bpm, 0, songPlayer, scoreManager);
        if (stageFloor != null) stageReactions = StageReactiveEffects.Create(stageFloor, noteSpawner, stagePerformance);
    }

    // チュートリアルの終わり(スキップ含む)。難易度はまだ決まっていないので選曲へ進む。
    public static bool ExitTutorial(string nextSceneName = "SongSelect")
    {
        return ScreenTransition.Load(nextSceneName, ScreenTransition.Style.Forward);
    }

    public static void ExitCalibration(string returnSceneName = "SongSelect")
    {
        if (ScreenTransition.Load(returnSceneName, ScreenTransition.Style.Back))
        {
            GameSession.IsCalibrationMode = false;
            if (returnSceneName != "SongSelect")
            {
                GameSession.CalibrationSelectionSeconds = null;
                GameSession.CalibrationSelectionSongId = null;
                GameSession.CalibrationSelectionDifficulty = null;
                GameSession.RushESecretCarry = false;
            }
        }
    }

    private void FinishGame()
    {
        // 「なぜ今終わったか」を後から追えるように記録する(途中終了バグの診断用)
        Debug.Log($"GamePlayManager: FinishGame songTime={songPlayer.SongTime:F1}s " +
            $"duration={songPlayer.Duration:F1}s lastNote={lastNoteTime:F1}s alive={noteSpawner.AliveCount}");
        completing = true;
        songPlayer.Stop();
        if (stageReactions != null) stageReactions.ResetState();
        if (chorusDrop != null) chorusDrop.ResetEffects();
        if (feverShow != null) feverShow.ResetState();
        if (gateAccents != null) gateAccents.ResetState();
        if (playFeedback != null) playFeedback.gameObject.SetActive(false);
        StartCoroutine(FinishWithAchievements());
    }

    private System.Collections.IEnumerator FinishWithAchievements()
    {
        // 通常終了時だけ達成を表示。ALL PERFECT は FULL COMBO より優先し、一度だけ出す。
        if (scoreManager.IsFullCombo)
        {
            var achievement = ClearAchievementPresentation.Create(scoreManager.IsAllPerfect);
            try
            {
                float achievementTime = 0f;
                while (achievementTime < ClearAchievementPresentation.Duration)
                {
                    achievement.Tick(achievementTime);
                    yield return null;
                    achievementTime += Mathf.Min(Time.unscaledDeltaTime, .1f);
                }
            }
            finally
            {
                if (achievement != null) Destroy(achievement.gameObject);
            }
        }
        var bonus = ComboBonusPresentation.Create(scoreManager.MaxCombo, scoreManager.IsFullCombo,
            scoreManager.ComboUnits, scoreManager.ComboBonus);
        // アニメと精算を同じ終了経路で管理し、途中の入力では再加算しない。
        float elapsed = 0f;
        while (elapsed < bonus.Duration)
        {
            bonus.Tick(elapsed);
            yield return null;
            // 生成前の長いフレーム時間を足さず、処理落ちでも冒頭を飛ばさない。
            elapsed += Mathf.Min(Time.unscaledDeltaTime, .1f);
            if (elapsed >= ComboBonusPresentation.LandingTime) scoreManager.FinalizeScore();
        }
        CommitFinalResult();
        if (!string.IsNullOrEmpty(resultSceneName))
        {
            ScreenTransition.Load(resultSceneName, ScreenTransition.Style.Result);
        }
        else Destroy(bonus.gameObject);
    }
}
