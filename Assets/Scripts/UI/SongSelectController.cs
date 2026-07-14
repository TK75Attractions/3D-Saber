using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// スクロール可能な曲リスト + 右側に難易度・ジャケット・スタートボタン。
// PC キー入力（↑↓で曲選択、←→で難易度、Enter/Space で開始）。
// 選択中の曲を 10 秒間プレビュー再生する。
public class SongSelectController : MonoBehaviour
{
    public string gameSceneName = "Game";

    [Header("List")]
    public RectTransform scrollContent;
    public ScrollRect scrollRect;
    public GameObject buttonPrefab;
    public Color normalLabelColor = new Color(0.85f, 0.85f, 0.9f);
    public Color selectedLabelColor = new Color(1f, 1f, 1f);
    // ロック曲(譜面未制作)の行の色。グレーで「選べない」ことを示す
    public Color lockedLabelColor = new Color(0.42f, 0.44f, 0.52f);

    [Header("Right panel")]
    public Image jacketImage;
    public Text difficultyDisplay;
    public Button[] difficultyButtons;
    public string[] difficultyNames = { "Easy", "Normal", "Hard" };
    public Button startButton;

    [Header("Preview")]
    public AudioSource previewSource;
    public float previewDuration = 10f;

    [Header("Skin hooks (スキン連携)")]
    // 選択行の先頭に付ける記号。スキンがカード装飾で選択を示す場合は "" にする。
    public string selectedPrefix = "→ ";
    public string normalPrefix = "    ";
    // true ならスキン側が難易度ボタンの配色を管理する(SetDifficulty の既定色上書きを止める)。
    public bool suppressDefaultDifficultyTint = false;

    private readonly List<string> songIds = new List<string>();
    private readonly List<Text> songLabels = new List<Text>();
    private int selectedIndex = -1;
    private int selectedDifficulty = 1; // Normal
    private Coroutine previewCoroutine;
    // 難易度レベル(1〜10)のキャッシュ。キー = songId::難易度名。0 = 譜面なし(数値非表示)
    private readonly Dictionary<string, int> levelCache = new Dictionary<string, int>();
    // ロック中(譜面が1つも無い)曲のインデックス。譜面を作れば次回から自動で解禁される
    private readonly HashSet<int> lockedIndices = new HashSet<int>();

    public int SelectedIndex => selectedIndex;
    public int SelectedDifficultyIndex => selectedDifficulty;
    public int SongCount => songIds.Count;
    public event System.Action<int> OnSelectionChanged;
    public event System.Action<int> OnDifficultyChanged;

    void Start()
    {
        Populate();
        BindStartButton();
        BindDifficultyButtons();
        if (songIds.Count > 0) Select(0);
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.upArrowKey.wasPressedThisFrame || kb.wKey.wasPressedThisFrame) Move(-1);
        if (kb.downArrowKey.wasPressedThisFrame || kb.sKey.wasPressedThisFrame) Move(1);
        if (kb.leftArrowKey.wasPressedThisFrame || kb.aKey.wasPressedThisFrame) ChangeDifficulty(-1);
        if (kb.rightArrowKey.wasPressedThisFrame || kb.dKey.wasPressedThisFrame) ChangeDifficulty(1);
        if (kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame) StartGame();
    }

    // 曲一覧に出す標準難易度(HasPlayableChart の探索にも使う)
    public static readonly string[] StandardDifficulties = { "Easy", "Normal", "Hard" };

    // 曲選択に並べる曲ID一覧。実譜面(ノーツ入り)が1つも無い曲は一覧から完全に除外する
    // (譜面を作って保存すれば次回から自動で現れる)。
    public static List<string> EnumerateSongIds()
    {
        return EnumerateSongIds(playableOnly: true);
    }

    public static List<string> EnumerateSongIds(bool playableOnly)
    {
        string root = Path.Combine(Application.streamingAssetsPath, "Songs");
        if (!Directory.Exists(root)) return new List<string>();
        var result = new List<string>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            string chart = Path.Combine(dir, "chart.json");
            if (!File.Exists(chart)) continue;
            string songId = Path.GetFileName(dir);
            if (playableOnly && !HasPlayableChart(songId, StandardDifficulties)) continue;
            result.Add(songId);
        }
        result.Sort();
        return result;
    }

    // 曲が1つでも実譜面(ノーツ入り)を持つか。無い曲はロック扱いで選べない。
    public static bool HasPlayableChart(string songId, string[] difficulties)
    {
        try
        {
            ChartData baseChart = ChartLoader.LoadFromStreamingAssets(songId);
            if (baseChart != null && baseChart.notes != null && baseChart.notes.Count > 0) return true;
            if (difficulties != null)
            {
                foreach (string d in difficulties)
                {
                    ChartData c = ChartLoader.LoadFromStreamingAssets(songId, d);
                    if (c != null && c.notes != null && c.notes.Count > 0) return true;
                }
            }
        }
        catch (System.Exception)
        {
            // 読めない譜面はロック扱い
        }
        return false;
    }

    public bool IsLocked(int index) => lockedIndices.Contains(index);
    public bool SelectedSongLocked => IsLocked(selectedIndex);

    // テストからも呼べるように公開(Start から呼ばれる一覧構築)
    public void Populate()
    {
        songIds.Clear();
        songLabels.Clear();
        lockedIndices.Clear();
        songIds.AddRange(EnumerateSongIds());
        for (int i = 0; i < songIds.Count; i++)
        {
            if (!HasPlayableChart(songIds[i], difficultyNames)) lockedIndices.Add(i);
        }
        if (scrollContent == null || buttonPrefab == null) return;

        for (int i = 0; i < songIds.Count; i++)
        {
            int captured = i;
            GameObject go = Instantiate(buttonPrefab, scrollContent);
            Button btn = go.GetComponent<Button>();
            Text label = go.GetComponentInChildren<Text>();
            songLabels.Add(label);
            if (btn != null) btn.onClick.AddListener(() => Select(captured));
        }
    }

    private void BindStartButton()
    {
        if (startButton != null) startButton.onClick.AddListener(StartGame);
    }

    private void BindDifficultyButtons()
    {
        if (difficultyButtons == null) return;
        for (int i = 0; i < difficultyButtons.Length && i < difficultyNames.Length; i++)
        {
            int captured = i;
            if (difficultyButtons[i] != null)
                difficultyButtons[i].onClick.AddListener(() => SetDifficulty(captured));
        }
        SetDifficulty(selectedDifficulty);
    }

    public void Select(int idx)
    {
        if (idx < 0 || idx >= songIds.Count) return;
        selectedIndex = idx;
        RefreshLabels();
        ScrollToSelected();
        LoadJacket(songIds[idx]);
        StartPreview(songIds[idx]);
        RefreshDifficultyDisplay(); // 曲が変わるとレベル数値も変わる
        if (startButton != null) startButton.interactable = !SelectedSongLocked; // ロック曲はSTART不可
        OnSelectionChanged?.Invoke(idx);
    }

    // 選択中の曲×難易度のレベル(1〜10)。譜面なし/空は 0。
    public int CurrentDifficultyLevel()
    {
        if (selectedIndex < 0 || selectedIndex >= songIds.Count) return 0;
        string name = difficultyNames[Mathf.Clamp(selectedDifficulty, 0, difficultyNames.Length - 1)];
        return LevelFor(songIds[selectedIndex], name);
    }

    private int LevelFor(string songId, string difficultyName)
    {
        string key = songId + "::" + difficultyName;
        if (levelCache.TryGetValue(key, out int cached)) return cached;
        int level = 0;
        try
        {
            level = ChartDifficultyRater.Rate(ChartLoader.LoadFromStreamingAssets(songId, difficultyName));
        }
        catch (System.Exception)
        {
            level = 0; // 読めない譜面は数値なし扱い
        }
        levelCache[key] = level;
        return level;
    }

    private void RefreshDifficultyDisplay()
    {
        if (difficultyDisplay == null) return;
        string name = difficultyNames[Mathf.Clamp(selectedDifficulty, 0, difficultyNames.Length - 1)];
        int level = CurrentDifficultyLevel();
        difficultyDisplay.text = level > 0 ? $"{name}  {level}" : name;
    }

    private void Move(int delta)
    {
        if (songIds.Count == 0) return;
        int next = (selectedIndex + delta + songIds.Count) % songIds.Count;
        Select(next);
    }

    private void RefreshLabels()
    {
        for (int i = 0; i < songLabels.Count; i++)
        {
            if (songLabels[i] == null) continue;
            bool sel = (i == selectedIndex);
            songLabels[i].text = (sel ? selectedPrefix : normalPrefix) + songIds[i];
            // ロック曲はグレー(選択中でもグレーのまま=遊べないサイン)
            songLabels[i].color = IsLocked(i) ? lockedLabelColor : (sel ? selectedLabelColor : normalLabelColor);
        }
    }

    private void ScrollToSelected()
    {
        if (scrollRect == null || songIds.Count <= 1) return;
        scrollRect.verticalNormalizedPosition = 1f - (float)selectedIndex / (songIds.Count - 1);
    }

    private void LoadJacket(string songId)
    {
        if (jacketImage == null) return;
        string path = Path.Combine(Application.streamingAssetsPath, "Songs", songId, "cover.png");
        if (File.Exists(path))
        {
            byte[] data = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2);
            tex.LoadImage(data);
            jacketImage.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), Vector2.one * 0.5f);
            jacketImage.color = Color.white;
        }
        else
        {
            jacketImage.sprite = null;
            jacketImage.color = ColorFromHash(songId);
        }
    }

    private static Color ColorFromHash(string s)
    {
        int hash = s == null ? 0 : s.GetHashCode();
        float h = (float)((hash & 0xFFFF) / 65535.0);
        return Color.HSVToRGB(h, 0.4f, 0.6f);
    }

    private void StartPreview(string songId)
    {
        if (previewSource == null) return;
        previewSource.Stop();
        if (previewCoroutine != null) StopCoroutine(previewCoroutine);
        previewCoroutine = StartCoroutine(LoadAndPlayPreview(songId));
    }

    private IEnumerator LoadAndPlayPreview(string songId)
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
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
                    previewSource.clip = clip;
                    previewSource.time = 0f;
                    previewSource.loop = false;
                    previewSource.Play();
                    yield return new WaitForSeconds(previewDuration);
                    if (previewSource != null && previewSource.clip == clip) previewSource.Stop();
                    yield break;
                }
            }
        }
    }

    private AudioType GuessType(string n)
    {
        n = n.ToLowerInvariant();
        if (n.EndsWith(".ogg")) return AudioType.OGGVORBIS;
        if (n.EndsWith(".wav")) return AudioType.WAV;
        if (n.EndsWith(".mp3")) return AudioType.MPEG;
        return AudioType.UNKNOWN;
    }

    public void ChangeDifficulty(int delta)
    {
        if (difficultyNames == null || difficultyNames.Length == 0) return;
        SetDifficulty((selectedDifficulty + delta + difficultyNames.Length) % difficultyNames.Length);
    }

    public void SetDifficulty(int idx)
    {
        if (difficultyNames == null || difficultyNames.Length == 0) return;
        idx = Mathf.Clamp(idx, 0, difficultyNames.Length - 1);
        selectedDifficulty = idx;
        RefreshDifficultyDisplay(); // 「難易度名  レベル数値」(譜面なしは名前のみ)
        if (difficultyButtons != null && !suppressDefaultDifficultyTint)
        {
            for (int i = 0; i < difficultyButtons.Length; i++)
            {
                if (difficultyButtons[i] == null) continue;
                var img = difficultyButtons[i].GetComponent<Image>();
                if (img != null) img.color = (i == idx)
                    ? new Color(0.25f, 0.6f, 1f, 1f)
                    : new Color(0.15f, 0.2f, 0.3f, 1f);
            }
        }
        OnDifficultyChanged?.Invoke(idx);
    }

    public void StartGame()
    {
        if (selectedIndex < 0 || selectedIndex >= songIds.Count) return;
        if (SelectedSongLocked) return; // ロック曲(譜面未制作)は開始できない
        if (previewSource != null) previewSource.Stop();
        GameSession.SelectedSongId = songIds[selectedIndex];
        GameSession.SelectedSongTitle = songIds[selectedIndex];
        GameSession.SelectedDifficulty = (selectedDifficulty < difficultyNames.Length)
            ? difficultyNames[selectedDifficulty]
            : "Normal";
        GameSession.ResetResult();
        SceneManager.LoadScene(gameSceneName);
    }
}
