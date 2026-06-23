using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using TMPro;
using System.IO;

public class BeatNoteRecorder : MonoBehaviour
{
    [Header("Beat Management")]
    public float currentBeat = 0f;   // a
    public float bpm = 120f;         // BPM
    public int beatsPerMeasure = 4;  // c（1小節が何拍か）

    [Header("UI Elements")]
    public Button pPrevButton;       // -c
    public Button prevButton;        // -1
    public Button nextButton;        // +1
    public Button nNextButton;       // +c
    public TMP_InputField beatInputField;

    public RectTransform boardRect;
    public Transform boardParent;
    public GameObject notePrefab;

    [Header("Audio")]
    public AudioSource audioSource;
    public TMP_Dropdown speedDropdown;  // speed 選択

    [Header("Buttons")]
    public Button saveButton;
    public Button startStopButton;

    private bool isPlaying = false;
    private float playStartBeat = 0f;

    private float speed = 1f;

    private List<NoteData> notes = new List<NoteData>();


    void Start()
    {
        audioSource.Stop();

        // Navigation
        pPrevButton.onClick.AddListener(() => MoveBeat(-beatsPerMeasure));
        prevButton.onClick.AddListener(() => MoveBeat(-1));
        nextButton.onClick.AddListener(() => MoveBeat(1));
        nNextButton.onClick.AddListener(() => MoveBeat(beatsPerMeasure));

        beatInputField.onEndEdit.AddListener(SetBeatFromInput);

        saveButton.onClick.AddListener(SaveNotes);
        startStopButton.onClick.AddListener(TogglePlay);

        speedDropdown.onValueChanged.AddListener(OnSpeedChanged);
        OnSpeedChanged(speedDropdown.value);

        RefreshUI();
    }


    void Update()
    {
        // 再生中 → currentBeat を前進
        if (isPlaying)
        {
            currentBeat += (bpm / 60f) * speed * Time.deltaTime;
            RefreshUI();
        }

        // Board クリック
        if (Mouse.current.leftButton.wasPressedThisFrame)
        {
            Vector2 mousePos = Mouse.current.position.ReadValue();

            if (RectTransformUtility.RectangleContainsScreenPoint(boardRect, mousePos))
            {
                Vector2 localPos;
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    boardRect,
                    mousePos,
                    null,
                    out localPos
                );

                AddNote(localPos);
            }
        }
    }


    //------------------------------------------------------------
    // Beat 操作
    //------------------------------------------------------------
    void MoveBeat(int delta)
    {
        currentBeat += delta;

        if (currentBeat < 0) currentBeat = 0;

        if (isPlaying)
        {
            // 再生中は同期し直す
            JumpAudioToCurrentBeat();
        }

        RefreshUI();
    }


    void SetBeatFromInput(string input)
    {
        if (float.TryParse(input, out float d))
        {
            currentBeat = Mathf.Max(0, d);

            if (isPlaying)
                JumpAudioToCurrentBeat();
        }

        RefreshUI();
    }


    //------------------------------------------------------------
    // Speed（倍速）
    //------------------------------------------------------------
    void OnSpeedChanged(int index)
    {
        float[] table = { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f };
        speed = table[index];

        if (audioSource != null)
        {
            audioSource.pitch = speed;
        }
    }


    //------------------------------------------------------------
    // 再生制御
    //------------------------------------------------------------
    void TogglePlay()
    {
        if (!isPlaying)
        {
            // 再生開始
            JumpAudioToCurrentBeat();
            audioSource.Play();
        }
        else
        {
            // 停止
            audioSource.Pause();
        }

        isPlaying = !isPlaying;
    }


    void JumpAudioToCurrentBeat()
    {
        if (audioSource.clip == null) return;

        float secsPerBeat = 60f / bpm;
        float jumpTime = currentBeat * secsPerBeat;

        audioSource.time = Mathf.Clamp(jumpTime, 0, audioSource.clip.length);
        audioSource.pitch = speed;
    }


    //------------------------------------------------------------
    // ノーツ追加
    //------------------------------------------------------------
    void AddNote(Vector2 pos)
    {
        int beatInt = Mathf.FloorToInt(currentBeat);

        NoteData nd = new NoteData
        {
            beat = beatInt,
            x = pos.x,
            y = pos.y
        };

        notes.Add(nd);
        RefreshNotes();
    }


    //------------------------------------------------------------
    // 表示更新
    //------------------------------------------------------------
    void RefreshUI()
    {
        int beatInt = Mathf.FloorToInt(currentBeat);
        beatInputField.text = beatInt.ToString();

        RefreshNotes();
    }


    void RefreshNotes()
    {
        int beatInt = Mathf.FloorToInt(currentBeat);

        foreach (Transform child in boardParent)
            Destroy(child.gameObject);

        foreach (var note in notes)
        {
            if (note.beat == beatInt)
            {
                GameObject obj = Instantiate(notePrefab, boardParent);
                RectTransform rt = obj.GetComponent<RectTransform>();
                rt.anchoredPosition = new Vector2(note.x, note.y);
            }
        }
    }


    //------------------------------------------------------------
    // 保存
    //------------------------------------------------------------
    void SaveNotes()
    {
        string jsonPath = Application.dataPath + "/notes.json";
        string csvPath = Application.dataPath + "/notes.csv";

        string json = JsonUtility.ToJson(new NoteCollection { notes = notes }, true);
        File.WriteAllText(jsonPath, json);

        using (StreamWriter w = new StreamWriter(csvPath))
        {
            w.WriteLine("beat,x,y");
            foreach (var n in notes)
            {
                w.WriteLine($"{n.beat},{n.x},{n.y}");
            }
        }

        Debug.Log("Saved to JSON and CSV");
    }
}


[System.Serializable]
public class NoteData
{
    public int beat;
    public float x;
    public float y;
}

[System.Serializable]
public class NoteCollection
{
    public List<NoteData> notes;
}