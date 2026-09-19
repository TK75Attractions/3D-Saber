using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// 全画面の退出・読み込み・登場を一続きにする。時計と入力の所有権はこの間だけ保持する。
public sealed class ScreenTransition : MonoBehaviour
{
    public enum Style { Forward, Back, Result, Calibration }
    public const float CloseDuration = .42f;
    public const float OpenDuration = .38f;

    static ScreenTransition instance;
    readonly List<EventSystem> blockedSystems = new List<EventSystem>();
    ScreenTransitionGraphic curtain;
    bool busy;
    public static bool IsBusy => instance != null && instance.busy;
    public static bool IsArriving => IsBusy && instance.arriving;
    bool arriving;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { instance = null; }

    public static bool Load(string sceneName, Style style = Style.Forward,
        Action<float> departure = null, float duration = CloseDuration)
    {
        if (IsBusy) return false;
        if (string.IsNullOrEmpty(sceneName) || !Application.CanStreamedLevelBeLoaded(sceneName))
        {
            Debug.LogWarning("画面を読み込めません: " + sceneName);
            return false;
        }
        Ensure().Begin(sceneName, style, departure, duration);
        return true;
    }

    public static void Quit()
    {
        if (!IsBusy) Ensure().Begin(null, Style.Back, null, CloseDuration);
    }

    static ScreenTransition Ensure()
    {
        if (instance != null) return instance;
        var root = new GameObject("ScreenTransition", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        DontDestroyOnLoad(root);
        instance = root.AddComponent<ScreenTransition>();
        var canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32760;
        var scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = .5f;
        var panel = new GameObject("LightCurtain", typeof(RectTransform), typeof(ScreenTransitionGraphic));
        panel.transform.SetParent(root.transform, false);
        var rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        instance.curtain = panel.GetComponent<ScreenTransitionGraphic>();
        instance.curtain.raycastTarget = true;
        root.SetActive(false);
        return instance;
    }

    void Begin(string sceneName, Style style, Action<float> departure, float duration)
    {
        busy = true;
        arriving = false;
        gameObject.SetActive(true);
        BlockInput();
        SceneManager.sceneLoaded += OnSceneLoaded;
        StartCoroutine(Run(sceneName, style, departure, Mathf.Max(.01f, duration)));
    }

    void BlockInput()
    {
        // マウスだけでなく、選択中ボタンへのSubmitも止める。元から無効なシステムは触らない。
        foreach (var events in FindObjectsByType<EventSystem>(FindObjectsSortMode.None))
        {
            if (!events.enabled) continue;
            blockedSystems.Add(events);
            events.enabled = false;
        }
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode) { BlockInput(); }

    IEnumerator Run(string sceneName, Style style, Action<float> departure, float duration)
    {
        try
        {
            for (float elapsed = 0; elapsed < duration; elapsed += Time.unscaledDeltaTime)
            {
                float progress = Mathf.Clamp01(elapsed / duration);
                departure?.Invoke(progress);
                // タイトルの前進演出は最後まで見せ、終端だけを共通の暗転へ受け渡す。
                curtain.SetProgress(departure == null ? progress : Mathf.InverseLerp(.78f, 1f, progress), style);
                yield return null;
            }
            departure?.Invoke(1f);
            curtain.SetProgress(1f, style);
            yield return null;

            if (sceneName == null)
            {
                Application.Quit();
                // Editorなど終了しない環境では入力と画面を戻す。
            }
            else
            {
                arriving = true;
                AsyncOperation loading = null;
                try { loading = SceneManager.LoadSceneAsync(sceneName); }
                catch (Exception error) { Debug.LogException(error); }
                if (loading != null) yield return loading;
                else departure?.Invoke(0f);
                // Startで組み立てるスキンと、その一フレーム後の選曲UIを暗転中に待つ。
                yield return null;
                yield return null;
                BlockInput();
            }

            for (float elapsed = 0; elapsed < OpenDuration; elapsed += Time.unscaledDeltaTime)
            {
                curtain.SetProgress(1f - elapsed / OpenDuration, style);
                yield return null;
            }
        }
        finally
        {
            Release();
            gameObject.SetActive(false);
        }
    }

    void Release()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        foreach (var events in blockedSystems) if (events != null) events.enabled = true;
        blockedSystems.Clear();
        if (curtain != null) curtain.SetProgress(0f, Style.Forward);
        busy = arriving = false;
    }

    void OnDestroy()
    {
        Release();
        if (instance == this) instance = null;
    }
}
