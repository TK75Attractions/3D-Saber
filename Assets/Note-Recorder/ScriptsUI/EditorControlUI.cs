using UnityEngine;
using TMPro;
using UnityEngine.UI;
using UnityEngine.InputSystem; // InputSystemを使用

public class EditorControlUI : MonoBehaviour
{
    [Header("マネージャー参照")]
    public TimeManager timeManager;
    public ChartManager chartManager;
    public TimelineManager timelineManager;

    [Header("TMP InputField 参照")]
    public TMP_InputField beatInputField;   
    public TMP_InputField bpmInputField;    
    public TMP_InputField measureInputField;
    public TMP_InputField offsetInputField; 

    [Header("現在の設定値")]
    public float beatsPerMeasure = 4f; 

    private float lastDisplayedA = -1f; 

    void Start()
    {
        if (timeManager == null) timeManager = Object.FindFirstObjectByType<TimeManager>();
        if (chartManager == null) chartManager = Object.FindFirstObjectByType<ChartManager>();
        if (timelineManager == null) timelineManager = Object.FindFirstObjectByType<TimelineManager>();

        RefreshAllFields();
    }

    void Update()
    {
        if (timeManager == null) return;

        // --- 1. UI表示の更新 ---
        float roundedA = Mathf.Round(timeManager.a * 100f) / 100f;
        if (!Mathf.Approximately(roundedA, lastDisplayedA))
        {
            if (beatInputField && !beatInputField.isFocused)
            {
                beatInputField.text = timeManager.a.ToString("F2");
            }
            lastDisplayedA = roundedA;
        }

        // --- 2. キーボードショートカットの処理 ---
        // InputFieldにフォーカスがある時はショートカットを無効化する
        if (IsInputFieldFocused()) return;

        var kb = Keyboard.current;
        if (kb == null) return;

        // Undo: Ctrl + Z
        if (kb.zKey.wasPressedThisFrame && kb.ctrlKey.isPressed) OnUndoPressed();
        // Redo: Ctrl + Y
        if (kb.yKey.wasPressedThisFrame && kb.ctrlKey.isPressed) OnRedoPressed();
        
        // 移動ショートカット例
        if (kb.leftArrowKey.wasPressedThisFrame) OnPrevPressed();
        if (kb.rightArrowKey.wasPressedThisFrame) OnNextPressed();
        if (kb.commaKey.wasPressedThisFrame) OnPPrevPressed(); // < キー
        if (kb.periodKey.wasPressedThisFrame) OnNNextPressed(); // > キー
    }

    // UIに入力中かどうかを判定
    private bool IsInputFieldFocused()
    {
        var current = UnityEngine.EventSystems.EventSystem.current;
        return current != null && current.currentSelectedGameObject != null &&
               current.currentSelectedGameObject.GetComponent<TMP_InputField>() != null;
    }

    // --- 各ボタンに対応する関数 ---
    public void OnPPrevPressed() => JumpBeat(-beatsPerMeasure);
    public void OnPrevPressed() => JumpBeat(-1f);
    public void OnNextPressed() => JumpBeat(1f);
    public void OnNNextPressed() => JumpBeat(beatsPerMeasure);

    private void JumpBeat(float delta)
    {
        if (timeManager) timeManager.SetBeat(timeManager.a + delta);
    }

    public void OnStartStopPressed() => timeManager?.TogglePlay();
    
    public void OnUndoPressed() 
    {
        Debug.Log("Undo実行");
        chartManager?.Undo();
    }

    public void OnRedoPressed() 
    {
        Debug.Log("Redo実行");
        chartManager?.Redo();
    }

    // --- 設定変更系 ---
    public void RefreshAllFields()
    {
        if (timeManager != null)
        {
            if (bpmInputField) bpmInputField.text = timeManager.bpm.ToString();
            if (offsetInputField) offsetInputField.text = timeManager.offsetSeconds.ToString();
        }
        if (measureInputField) measureInputField.text = beatsPerMeasure.ToString();
        UpdateTotalBeats();
    }

    public void OnBeatInputChanged()
    {
        if (beatInputField && float.TryParse(beatInputField.text, out float newBeat))
            timeManager?.SetBeat(newBeat);
    }

    public void OnBPMChanged()
    {
        if (bpmInputField && float.TryParse(bpmInputField.text, out float newBPM))
        {
            if (newBPM <= 0) newBPM = 120f;
            timeManager.bpm = newBPM;
            UpdateTotalBeats();
        }
    }

    public void OnMeasureChanged()
    {
        if (measureInputField && float.TryParse(measureInputField.text, out float newC))
            beatsPerMeasure = Mathf.Max(1f, newC);
    }

    public void OnOffsetChanged()
    {
        if (offsetInputField && float.TryParse(offsetInputField.text, out float newOffset))
            timeManager.offsetSeconds = newOffset;
    }

    public void UpdateTotalBeats()
    {
        if (timeManager?.audioSource?.clip == null || timelineManager == null) return;
        float duration = timeManager.audioSource.clip.length;
        float total = (duration * timeManager.bpm) / 60f;
        timelineManager.totalBeats = total;
        timelineManager.RefreshTimelineDots();
    }

    public void OnNoteTypeChanged(int index)
    {
        if (chartManager != null)
        {
            chartManager.currentSelectedType = index;
            Debug.Log($"Selected Type: {index}");
        }
    }
}