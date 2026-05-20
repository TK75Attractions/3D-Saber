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

    public string resultSceneName = "Result";
    public float endWaitSeconds = 2.0f;
    // 最後のノーツ通過後、リザルトに遷移する前の余韻時間
    public float outroSeconds = 3.5f;

    [Header("Judge guide")]
    public bool simplifyJudgeGuide = true;
    public float judgePanelAlpha = 0.2f;

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
        if (cutJudge != null) cutJudge.autonomous = false;

        // 既存シーンを編集せずにロング音 SFX を有効化
        EnsureLongNoteCutSfx();

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
        noteSpawner.SetChart(chart);
        if (barLineSpawner != null) barLineSpawner.SetChart(chart);
        lastNoteTime = chart.notes.Count > 0
            ? chart.notes[chart.notes.Count - 1].TimeSeconds
            : 0.0;

        yield return LoadAudio(songId);

        scoreManager.songPlayer = songPlayer;
        scoreManager.Reset();
        scoreManager.Bind(noteSpawner);
        if (longNoteCutSfx != null) longNoteCutSfx.Bind(noteSpawner);
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
        // 残った JudgePanel を少しだけ濃くして「薄い面」として認識できるようにする
        var panel = guide.transform.Find("JudgePanel");
        if (panel != null)
        {
            var mr = panel.GetComponent<MeshRenderer>();
            if (mr != null && mr.sharedMaterial != null)
            {
                var m = mr.material; // インスタンス化（共有 Material を上書きしない）
                Color c = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.color;
                c.a = judgePanelAlpha;
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
                else m.color = c;
            }
        }
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
