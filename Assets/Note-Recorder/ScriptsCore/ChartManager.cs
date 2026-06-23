using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System.IO;
using UnityEngine.InputSystem;
using TMPro;

public class ChartManager : MonoBehaviour
{
    [Header("楽曲・難易度設定 (フォルダ名とファイル名になります)")]
    public string currentSongFolder = "001_TestSong";
    public string currentDifficulty = "normal"; // easy, normal, hard など

    [Header("データオブジェクト")]
    public ChartData chartData = new ChartData();
    public SongInfo songInfo = new SongInfo();

    [Header("システム参照")]
    public TimeManager timeManager;
    public TimelineManager timelineManager;
    
    [Header("表示UI")]
    public TextMeshProUGUI formattedTimeText; 
    public TMP_InputField beatInputField;    

    [HideInInspector] public int currentSelectedType = 0;
    [HideInInspector] public BeatNoteData selectedNote = null;

    private GridCell[,] gridMatrix = new GridCell[8, 8]; 
    private int lastProcessedTick = -1;

    // Undo/Redo 用の履歴リスト
    private List<string> undoList = new List<string>();
    private List<string> redoList = new List<string>();

    // --- 動的フォルダ・ファイルパスの定義 ---
    private string SongsRootPath => Path.Combine(Application.persistentDataPath, "Songs");
    private string CurrentSongFolderPath => Path.Combine(SongsRootPath, currentSongFolder);
    private string InfoFilePath => Path.Combine(CurrentSongFolderPath, "info.json");
    private string ChartFilePath => Path.Combine(CurrentSongFolderPath, $"{currentDifficulty}.json");

    void Start()
    {
        timeManager = Object.FindFirstObjectByType<TimeManager>();
        timelineManager = Object.FindFirstObjectByType<TimelineManager>();
        
        var allCells = Object.FindObjectsByType<GridCell>(FindObjectsSortMode.None);
        foreach (var cell in allCells)
            if (cell.x >= 0 && cell.x < 8 && cell.y >= 0 && cell.y < 8)
                gridMatrix[cell.x, cell.y] = cell;

        // 起動時に自動でフォルダ構造をチェックしてロード
        LoadChart(); 
    }

    void Update()
    {
        HandleEditorInput();
        UpdateDisplay();

        if (timeManager == null || !timeManager.isPlaying) return;
        int currentTick = timeManager.currentTick;
        if (currentTick != lastProcessedTick)
        {
            PlayNotesAtTick(currentTick);
            lastProcessedTick = currentTick;
        }
    }

    void UpdateDisplay()
    {
        if (timeManager == null) return;
        
        if (formattedTimeText != null)
            formattedTimeText.text = timeManager.GetFormattedTime();

        if (beatInputField != null && !beatInputField.isFocused)
            beatInputField.text = timeManager.a.ToString("F2");
    }

    void HandleEditorInput()
    {
        var kb = Keyboard.current;
        if (kb == null || selectedNote == null) return;

        if (kb.numpad5Key.wasPressedThisFrame) SetDirection(0);
        if (kb.numpad6Key.wasPressedThisFrame) SetDirection(1);
        if (kb.numpad9Key.wasPressedThisFrame) SetDirection(2);
        if (kb.numpad8Key.wasPressedThisFrame) SetDirection(3);
        if (kb.numpad7Key.wasPressedThisFrame) SetDirection(4);
        if (kb.numpad4Key.wasPressedThisFrame) SetDirection(5);
        if (kb.numpad1Key.wasPressedThisFrame) SetDirection(6);
        if (kb.numpad2Key.wasPressedThisFrame) SetDirection(7);
        if (kb.numpad3Key.wasPressedThisFrame) SetDirection(8);

        var mouse = Mouse.current;
        if (mouse != null)
        {
            float scrollY = mouse.scroll.ReadValue().y;
            if (scrollY != 0 && selectedNote.type == 2)
            {
                RecordHistory(); 
                int step = timeManager.resolution;
                selectedNote.duration += (scrollY > 0) ? step : -step;
                selectedNote.duration = Mathf.Max(0, selectedNote.duration);
                NotifyDataChanged();
            }
        }
    }

    void SetDirection(int dir)
    {
        RecordHistory(); 
        selectedNote.direction = dir;
        NotifyDataChanged();
    }

    void PlayNotesAtTick(int tick)
    {
        foreach (var note in chartData.notes)
            if (note.startTick == tick)
                gridMatrix[note.x, note.y]?.Flash();
    }

    public void AddNote(int x, int y, int tick)
    {
        var existing = chartData.notes.FirstOrDefault(n => n.x == x && n.y == y && n.startTick == tick);
        if (existing != null) { selectedNote = existing; NotifyDataChanged(); return; }

        RecordHistory(); 
        selectedNote = new BeatNoteData { 
            x = x, y = y, startTick = tick, 
            type = currentSelectedType, direction = 0, duration = 0 
        };
        chartData.notes.Add(selectedNote);
        NotifyDataChanged();
    }

    public void RemoveNote(int x, int y, int tick)
    {
        RecordHistory(); 
        if (chartData.notes.RemoveAll(n => n.x == x && n.y == y && n.startTick == tick) > 0)
        {
            selectedNote = null;
            NotifyDataChanged();
        }
    }

    public void NotifyDataChanged()
    {
        if (timelineManager != null) timelineManager.RefreshTimelineDots();
        foreach (var cell in Object.FindObjectsByType<GridCell>(FindObjectsSortMode.None))
            cell.CheckNotePresence();
    }

    private void RecordHistory()
    {
        undoList.Add(JsonUtility.ToJson(chartData));
        if (undoList.Count > 50) undoList.RemoveAt(0);
        redoList.Clear();
    }

    public void Undo()
    {
        if (undoList.Count == 0) return;
        redoList.Add(JsonUtility.ToJson(chartData));
        string lastState = undoList[undoList.Count - 1];
        chartData = JsonUtility.FromJson<ChartData>(lastState);
        undoList.RemoveAt(undoList.Count - 1);
        NotifyDataChanged();
    }

    public void Redo()
    {
        if (redoList.Count == 0) return;
        undoList.Add(JsonUtility.ToJson(chartData));
        string nextState = redoList[redoList.Count - 1];
        chartData = JsonUtility.FromJson<ChartData>(nextState);
        redoList.RemoveAt(redoList.Count - 1);
        NotifyDataChanged();
    }

    private void SyncTimeManagerFromInfo()
    {
        timeManager.bpm = songInfo.bpm;
        timeManager.offsetSeconds = songInfo.offset;
        timeManager.resolution = songInfo.resolution;
        timeManager.beatsPerMeasure = songInfo.beatsPerMeasure;
    }

    // --- パッケージ構造に対応した保存処理 ---
    public void SaveChart()
    {
        // 1. 必要となるフォルダ群がなければ自動作成
        if (!Directory.Exists(SongsRootPath)) Directory.CreateDirectory(SongsRootPath);
        if (!Directory.Exists(CurrentSongFolderPath)) Directory.CreateDirectory(CurrentSongFolderPath);

        // 2. 楽曲のメタ情報（info.json）を更新して保存
        songInfo.songId = currentSongFolder;
        songInfo.bpm = timeManager.bpm;
        songInfo.offset = timeManager.offsetSeconds;
        songInfo.resolution = timeManager.resolution;
        songInfo.beatsPerMeasure = timeManager.beatsPerMeasure;
        File.WriteAllText(InfoFilePath, JsonUtility.ToJson(songInfo, true));

        // 3. 難易度ごとの譜面データ（例：normal.json）を保存
        File.WriteAllText(ChartFilePath, JsonUtility.ToJson(chartData, true));
        
        Debug.Log($"[Save Complete] Saved to: {CurrentSongFolderPath}");
    }

    // --- パッケージ構造に対応した読み込み処理 ---
    public void LoadChart()
    {
        // フォルダ自体がなければ何もしない（初期状態）
        if (!Directory.Exists(CurrentSongFolderPath)) return;

        // 1. info.json があれば読み込んでTimeManagerに同期
        if (File.Exists(InfoFilePath))
        {
            songInfo = JsonUtility.FromJson<SongInfo>(File.Exists(InfoFilePath) ? File.ReadAllText(InfoFilePath) : "");
            SyncTimeManagerFromInfo();
        }

        // 2. 選択された難易度のJSON（例：normal.json）があれば読み込む
        if (File.Exists(ChartFilePath))
        {
            chartData = JsonUtility.FromJson<ChartData>(File.ReadAllText(ChartFilePath));
        }
        else
        {
            // 指定難易度のファイルがなければ新規用として空にする
            chartData = new ChartData();
        }

        NotifyDataChanged();

        // 3. 音楽波形表示のレンダラーがあれば再描画をかける
        var renderer = Object.FindFirstObjectByType<AudioWaveformRenderer>();
        if (renderer != null && timeManager != null && timeManager.audioSource != null)
        {
            renderer.RenderWaveform(timeManager.audioSource.clip);
        }
    }
}