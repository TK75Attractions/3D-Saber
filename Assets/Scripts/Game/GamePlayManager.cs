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
    // 音源長からこの秒数を足したところまでをノーツの上限時刻にする。
    public float trimGraceSeconds = 0.5f;

    [Header("Judge guide")]
    public bool simplifyJudgeGuide = true;
    public float judgePanelAlpha = 0.30f;
    public Color judgePanelTint = new Color(0.30f, 0.75f, 1.0f, 1f);
    public bool addJudgePanelOuterGlow = true;

    [Header("Bar lines")]
    // 流れる小節線が「線の集合」に見えて視覚ノイズになるので既定で OFF。
    // BPM 視認用に戻したい場合は false にする。
    public bool disableBarLines = true;

    [Header("Saber hit detection (stricter to avoid sweeping)")]
    public bool overrideSaberRadii = true;
    public float saberBladeRadius = 0.22f;       // 既存ビルド時の 0.30 から狭める
    public float saberNoteHitRadiusXY = 0.35f;   // 既存ビルド時の 0.55 から狭める
    public float saberMinCutSpeed = 4.0f;        // 3.0 → 4.0 にして「ふわっと振る」での誤発火を抑える

    // 判定面ガイドから剥がす子オブジェクトの名前接頭辞。
    private static readonly string[] JudgeGuideStripPrefixes = {
        "GridV", "GridH", "Border", "Corner", "Cross"
    };

    private bool finished;
    private bool ready;
    private double lastNoteTime;

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
                // 既存シーンのセーバー値（bladeRadius=0.3, noteHitRadiusXY=0.55）が広く、
                // 隣接ノーツを巻き込みやすいので、ここで「厳しい」値に上書き。
                cutJudge.bladeRadius = saberBladeRadius;
                cutJudge.noteHitRadiusXY = saberNoteHitRadiusXY;
                cutJudge.minCutSpeed = saberMinCutSpeed;
            }
        }

        // 既存シーンを編集せずにロング音 SFX を有効化
        EnsureLongNoteCutSfx();
        EnsureGoldNoteSfx();

        // 判定面ガイド：旧シーンに残っている大量の線（グリッド/枠/コーナー/十字）を剥がして
        // 半透明の薄い面のみ残す。ユーザーの「薄い面にして」要件に合わせた整理。
        if (simplifyJudgeGuide) SimplifyJudgeGuide();

        string songId = GameSession.SelectedSongId;
        if (string.IsNullOrEmpty(songId))
        {
            Debug.LogWarning("GamePlayManager: SelectedSongId 未設定。デフォルト曲を試します");
            songId = "TestSong";
        }

        ChartData chart = ChartLoader.LoadFromStreamingAssets(songId);

        // 音源を先にロードして、譜面長を音源に合わせて切り詰められるようにする。
        yield return LoadAudio(songId);

        // 譜面オフセット計算：
        //   chart.offsetMs（曲固有）+ extraOffsetSeconds（Inspector）+ GameSession.JudgmentOffsetMs（プレイヤー調整）
        double playerOffsetSec = GameSession.JudgmentOffsetMs / 1000.0;
        double effectiveExtraOffset = extraOffsetSeconds + playerOffsetSec;
        double offsetSec = chart.offsetMs / 1000.0 + effectiveExtraOffset;

        // 音源より後ろにあるノーツは「曲が終わった後も続く」原因なので、自動で削除。
        double audioLen = songPlayer.Duration;
        if (trimChartToAudioLength && audioLen > 0.0)
        {
            int before = chart.notes.Count;
            chart.notes.RemoveAll(n => (n.TimeSeconds + offsetSec) > audioLen + trimGraceSeconds);
            int removed = before - chart.notes.Count;
            if (removed > 0)
            {
                Debug.Log($"GamePlayManager: 音源長({audioLen:F1}s) を超えるノーツ {removed} 個を切り詰めました");
            }
        }

        // 切り詰め後の lastNoteTime を計算
        lastNoteTime = chart.notes.Count > 0
            ? chart.notes[chart.notes.Count - 1].TimeSeconds + offsetSec
            : 0.0;

        // オフセットを先に NoteSpawner に渡してから SetChart
        noteSpawner.SetExtraOffsetSeconds(effectiveExtraOffset);
        noteSpawner.SetChart(chart);

        // 既定で小節線は OFF（流れる線が「線の集合」として煩く感じる対策）。
        if (disableBarLines && barLineSpawner != null)
        {
            barLineSpawner.gameObject.SetActive(false);
            barLineSpawner = null;
        }
        if (barLineSpawner != null) barLineSpawner.SetChart(chart);

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
        }
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

        // 2. 譜面進行
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
