using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

// 本編シーンの「ゲーム側 GManager」。
// 単一の Update から SaberCutJudge → NoteSpawner の順に駆動する（GManager 主体パターン）。
public class GamePlayManager : MonoBehaviour
{
    public SongPlayer songPlayer;
    public NoteSpawner noteSpawner;
    public ScoreManager scoreManager;
    public SaberCutJudge cutJudge;
    public BarLineSpawner barLineSpawner;
    public LongNoteCutSfx longNoteCutSfx; // 未設定なら自前で生成
    public GoldNoteSfx goldNoteSfx;       // 未設定なら自前で生成

    public string resultSceneName = "Result";
    public float endWaitSeconds = 2.0f;
    // 最後のノーツ通過後、リザルトに遷移する前の余韻時間
    public float outroSeconds = 3.5f;

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
    // リニューアル版のセーバー判定。マウス操作での「理不尽な空振り」を減らす緩和値。
    // (旧値 blade 0.22 / hitXY 0.35 / minSpeed 4.0 は巻き込み防止に振りすぎていた)
    public float saberBladeRadiusV2 = 0.26f;
    public float saberNoteHitRadiusXYV2 = 0.45f;
    public float saberMinCutSpeedV2 = 3.0f;

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
    public Vector3 cameraPosition = new Vector3(0f, 1.6f, -7f);
    public Vector3 cameraRotationEuler = new Vector3(6f, 0f, 0f);

    [Header("Floor / Lanes")]
    // Tron 風の床 + レーンガイド + 奥行きグリッドを実行時生成（視認性向上）。
    public bool addFloor = true;

    [Header("Saber input bootstrap (実機セーバー接続)")]
    // 全部既定 true。SaberInputBridge は InputPoint の受信状態を見て、
    // UDP データが 1 秒以上止まったら自動でマウスにフォールバックする（明示フラグ不要）。
    public bool autoEnsureInputPoint = true;
    public bool autoEnsureUdpImuBridge = true;
    public bool autoEnsureSwing8DirectionLogger = true;

    // 判定面ガイドから剥がす子オブジェクトの名前接頭辞。
    private static readonly string[] JudgeGuideStripPrefixes = {
        "GridV", "GridH", "Border", "Corner", "Cross"
    };

    private bool finished;
    private bool ready;
    private double lastNoteTime;

    // --- キャリブレーション（判定調整）モード ---
    private bool inCalibration;
    private AudioClip calibClickClip;
    private int nextCalibClickIdx;
    private double lastAppliedPlayerOffsetSec;
    private const float CalibBpm = 120f;
    private const double CalibFirstNoteTime = 2.0;
    private const float CalibChartDurationSec = 600f; // 10 分のループ

    IEnumerator Start()
    {
        if (songPlayer == null || noteSpawner == null || scoreManager == null)
        {
            Debug.LogError("GamePlayManager: 依存コンポーネントが未設定です");
            yield break;
        }

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
        if (autoEnsureInputPoint) EnsureInputPoint();
        if (autoEnsureUdpImuBridge) EnsureUdpImuBridge();
        if (autoEnsureSwing8DirectionLogger) EnsureSwing8DirectionLogger();
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
        if (addFloor) FloorRenderer.Ensure(transform);

        // ユーザー設定のノーツ速度（approachTime）を NoteSpawner に反映。
        if (noteSpawner != null)
        {
            noteSpawner.approachTime = GameSession.NoteApproachTime;
        }

        // --- キャリブレーションモード ---
        if (GameSession.IsCalibrationMode)
        {
            StartCalibration();
            ready = true;
            yield break;
        }

        // 新HUD(スコア/コンボ/判定演出/曲名/進行バー)。旧 ScoreHUD は内部で無効化される。
        // キャリブレーション時は CalibrationOverlay が主役なので出さない(上の分岐で return 済み)。
        if (useOverhauledStage) GameHUDSkin.Ensure();

        string songId = GameSession.SelectedSongId;
        if (string.IsNullOrEmpty(songId))
        {
            Debug.LogWarning("GamePlayManager: SelectedSongId 未設定。デフォルト曲を試します");
            songId = "TestSong";
        }

        ChartData chart = ChartLoader.LoadFromStreamingAssets(songId, GameSession.SelectedDifficulty);

        // 音源を先にロードして、譜面長を音源に合わせて切り詰められるようにする。
        yield return LoadAudio(songId);

        // 譜面オフセット計算：
        //   chart.offsetMs（曲固有）+ extraOffsetSeconds（Inspector）+ GameSession.JudgmentOffsetMs（プレイヤー調整）
        double playerOffsetSec = GameSession.JudgmentOffsetMs / 1000.0;
        double effectiveExtraOffset = extraOffsetSeconds + playerOffsetSec;
        double offsetSec = chart.offsetMs / 1000.0 + effectiveExtraOffset;

        // 音源より後ろにあるノーツは「曲が終わった後も続く」原因なので、自動で削除。
        // ロングは後方の判定窓 (count-1) * secondsPerLongCut も延長するので、その分も考慮する。
        double audioLen = songPlayer.Duration;
        if (trimChartToAudioLength && audioLen > 0.0)
        {
            float perCut = noteSpawner != null ? noteSpawner.secondsPerLongCut : 0.7f;
            int before = chart.notes.Count;
            chart.notes.RemoveAll(n =>
            {
                double eff = n.TimeSeconds + offsetSec;
                double endTime = eff;
                if (n.count > 1) endTime += (n.count - 1) * perCut;
                return endTime > audioLen + trimGraceSeconds;
            });
            int removed = before - chart.notes.Count;
            if (removed > 0)
            {
                Debug.Log($"GamePlayManager: 音源長({audioLen:F1}s) を超えるノーツ {removed} 個を切り詰めました (ロング後方判定窓を考慮)");
            }
        }

        // 切り詰め後の lastNoteTime を計算（ロングの後方延長を含む「実際に最後にカットが必要な時刻」）
        lastNoteTime = 0.0;
        {
            float perCut = noteSpawner != null ? noteSpawner.secondsPerLongCut : 0.7f;
            foreach (var n in chart.notes)
            {
                double eff = n.TimeSeconds + offsetSec;
                double endTime = eff + (n.count > 1 ? (n.count - 1) * perCut : 0);
                if (endTime > lastNoteTime) lastNoteTime = endTime;
            }
        }

        // オフセットを先に NoteSpawner に渡してから SetChart
        noteSpawner.SetExtraOffsetSeconds(effectiveExtraOffset);
        noteSpawner.SetChart(chart);

        // 判定ゲートの拍パルス(視覚メトロノーム)。ノーツと同じトータルオフセットで拍を刻む。
        if (useOverhauledStage && chart.bpm > 0f)
        {
            GateBeatPulse.Ensure(chart.bpm, noteSpawner.TotalOffsetSeconds, songPlayer);
        }

        // 小節線：BPM ガイドとして薄く流す
        if (disableBarLines && barLineSpawner != null)
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
            barLineSpawner.SetChart(chart);
        }

        scoreManager.songPlayer = songPlayer;
        scoreManager.Reset();
        scoreManager.Bind(noteSpawner);
        if (longNoteCutSfx != null) longNoteCutSfx.Bind(noteSpawner);
        if (goldNoteSfx != null) goldNoteSfx.Bind(noteSpawner);
        songPlayer.Play();
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
        var ip = Object.FindFirstObjectByType<InputPoint>();
        if (ip == null)
        {
            var go = new GameObject("InputPoint");
            go.transform.SetParent(transform, false);
            ip = go.AddComponent<InputPoint>();
        }
        // 送信側仕様：中央原点 -1..+1 正規化、y は上が+、16:9 ボード
        ip.useDirectWorldMapping = true;
        ip.worldScale = new Vector2(5.5f, 3.0f);
        ip.worldOffset = Vector2.zero;

        // SaberInputBridge は world 座標がそのまま入る前提に切り替え。
        // 受信状態に応じて UDP / マウス を SaberInputBridge.Update が自動で切り替える。
        var bridge = Object.FindFirstObjectByType<SaberInputBridge>();
        if (bridge != null)
        {
            bridge.pixelsToWorld = 1.0f;
            bridge.useInputPoint = true;
            bridge.fallbackToMouse = true; // UDP 無音時はマウスに戻る
        }
    }

    private void EnsureUdpImuBridge()
    {
        if (Object.FindFirstObjectByType<UdpImuBridge>() != null) return;
        var go = new GameObject("UdpImuBridge");
        go.AddComponent<UdpImuBridge>();
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
        cam.transform.position = cameraPosition;
        cam.transform.rotation = Quaternion.Euler(cameraRotationEuler);
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
        string dir = Path.Combine(Application.streamingAssetsPath, "Songs", songId);
        string[] candidates = { "audio.ogg", "audio.wav", "audio.mp3" };
        foreach (var name in candidates)
        {
            string full = Path.Combine(dir, name);
            if (!File.Exists(full)) continue;
            AudioType type = GuessType(name);
            using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip("file://" + full, type))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    songPlayer.Clip = DownloadHandlerAudioClip.GetContent(req);
                    yield break;
                }
                Debug.LogWarning($"Failed to load audio {full}: {req.error}");
            }
        }
        Debug.LogWarning($"No audio found under {dir}");
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
    // 順序：判定 → 譜面進行 → 終了チェック。
    void Update()
    {
        if (finished || !ready) return;

        // 1. セーバー判定
        if (cutJudge != null) cutJudge.RunJudge();

        // 2a. キャリブレーション分岐：時計は AudioSettings.dspTime ベース、終了せずループ
        if (inCalibration)
        {
            UpdateCalibration();
            return;
        }

        // 2b. 通常の譜面進行
        if (songPlayer.IsPlaying)
        {
            noteSpawner.Tick(songPlayer.SongTime);
            if (barLineSpawner != null) barLineSpawner.Tick(songPlayer.SongTime);
        }

        // 3. 終了判定：最終ノーツ通過＋余韻、かつアクティブなノーツがゼロ
        double endThreshold = System.Math.Max(songPlayer.Duration + endWaitSeconds,
                                              lastNoteTime + outroSeconds);
        if (songPlayer.IsPlaying && songPlayer.SongTime >= endThreshold
            && noteSpawner.AliveCount == 0)
        {
            finished = true;
            FinishGame();
        }
    }

    // --- キャリブレーションモード実装 ---

    void StartCalibration()
    {
        inCalibration = true;

        // キャリブレーションではセーバー判定を緩めに（本編の「巻き込み防止」設定は外す）。
        // 軽くマウスを払うだけでカットが入るように：noteHitRadiusXY を広く、minCutSpeed を低く。
        if (cutJudge != null)
        {
            cutJudge.bladeRadius = 0.35f;
            cutJudge.noteHitRadiusXY = 0.65f;
            cutJudge.minCutSpeed = 2.0f;
        }

        // 小節線は不要
        if (barLineSpawner != null)
        {
            barLineSpawner.gameObject.SetActive(false);
            barLineSpawner = null;
        }

        // 合成譜面：BPM 120、画面中央 (0, 0.5) に等間隔タップ
        var chart = SynthesizeCalibrationChart(CalibBpm, CalibChartDurationSec, CalibFirstNoteTime);

        // 現在の player offset を即時反映
        double playerOffsetSec = GameSession.JudgmentOffsetMs / 1000.0;
        lastAppliedPlayerOffsetSec = playerOffsetSec;
        noteSpawner.SetExtraOffsetSeconds(extraOffsetSeconds + playerOffsetSec);
        noteSpawner.SetChart(chart);

        scoreManager.songPlayer = songPlayer;
        scoreManager.Reset();
        scoreManager.Bind(noteSpawner);
        if (longNoteCutSfx != null) longNoteCutSfx.Bind(noteSpawner);
        if (goldNoteSfx != null) goldNoteSfx.Bind(noteSpawner);

        // 音源は使わない。AudioSource を流用してメトロノームを鳴らす。
        var src = songPlayer != null ? songPlayer.GetComponent<AudioSource>() : null;
        if (src != null)
        {
            src.Stop();
            src.clip = null;
        }

        calibClickClip = JudgmentSfx.Beep(880f, 0.05f);
        nextCalibClickIdx = 0;

        // 重要：songPlayer.Play() を呼んで時計を進める。
        // SongTime は ScoreManager.HandleCut が時間誤差計算に使うので、これを呼ばないと
        // SongTime は 0 のままで error 計算が破綻し、全 note が Miss になる。
        // clip は null のままで OK（無音で時計だけ進む）。
        songPlayer.startDelay = 1.0f; // 1 秒プリロール
        songPlayer.Play();

        // UI: オフセット調整＋BACK ボタンの「キャリブレーション オーバーレイ」を表示
        CalibrationOverlay.Ensure();
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
        // 時計は songPlayer.SongTime に一本化する。ScoreManager もこれを参照するので、
        // NoteSpawner.Tick と ScoreManager の時間軸が完全一致する。
        double calibTime = songPlayer.SongTime;

        // ユーザーが UI で offset を動かしたら、次のノーツから反映
        double currentPlayerOffset = GameSession.JudgmentOffsetMs / 1000.0;
        if (System.Math.Abs(currentPlayerOffset - lastAppliedPlayerOffsetSec) > 0.0001)
        {
            lastAppliedPlayerOffsetSec = currentPlayerOffset;
            noteSpawner.SetExtraOffsetSeconds(extraOffsetSeconds + currentPlayerOffset);
        }

        // ノーツ速度（approachTime）の即時反映
        float wantedApproach = GameSession.NoteApproachTime;
        if (!Mathf.Approximately(noteSpawner.approachTime, wantedApproach))
        {
            noteSpawner.approachTime = wantedApproach;
        }

        noteSpawner.Tick(calibTime);

        // メトロノーム：ビート時刻になったらクリックを鳴らす（オフセットなし＝音楽が基準）
        double beatInterval = 60.0 / CalibBpm;
        var src = songPlayer != null ? songPlayer.GetComponent<AudioSource>() : null;
        while (true)
        {
            double clickTime = CalibFirstNoteTime + nextCalibClickIdx * beatInterval;
            if (clickTime > calibTime) break;
            // 「大幅に遅れた」クリックは飛ばす（フレ落ち時の連打を防ぐ）
            if (calibTime - clickTime < 0.10)
            {
                if (src != null && calibClickClip != null)
                {
                    src.PlayOneShot(calibClickClip, 0.45f);
                }
            }
            nextCalibClickIdx++;
        }
    }

    public static void ExitCalibration(string returnSceneName = "SongSelect")
    {
        GameSession.IsCalibrationMode = false;
        UnityEngine.SceneManagement.SceneManager.LoadScene(returnSceneName);
    }

    private void FinishGame()
    {
        songPlayer.Stop();
        GameSession.FinalScore = scoreManager.Score;
        GameSession.FinalMaxCombo = scoreManager.MaxCombo;
        GameSession.FinalHit = scoreManager.HitCount;
        GameSession.FinalMiss = scoreManager.MissCount;
        GameSession.FinalPerfect = scoreManager.PerfectCount;
        GameSession.FinalGreat = scoreManager.GreatCount;
        GameSession.FinalGood = scoreManager.GoodCount;
        GameSession.FinalBad = scoreManager.BadCount;
        if (!string.IsNullOrEmpty(resultSceneName))
        {
            SceneManager.LoadScene(resultSceneName);
        }
    }
}
