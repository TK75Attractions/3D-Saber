using UnityEngine;
using UnityEngine.InputSystem;

// RUSH Eのジャケットの裏に、描画もレイキャストも持たない切断判定を重ねる。
// 10回目までは回数・輪・打音を出さず、通常の選曲画面に秘密のヒントを残さない。
// カメラ両手・ボタン不要のマウス移動・クリックが同じコントローラーの解除状態を使う。
[DefaultExecutionOrder(90)]
public sealed class RushESecret : MonoBehaviour
{
    SongSelectController controller;
    SongSelectSkin skin;
    RushETransitionGraphic curtain;
    AudioSource sound;
    AudioClip revealClip;
    readonly RushECameraStroke red = new RushECameraStroke(), blue = new RushECameraStroke();
    readonly RushEJacketStroke mouse = new RushEJacketStroke();
    double transitionAt = -1, suppressClickUntil;
    public RushETransitionGraphic Curtain => curtain;
    public static RushESecret Build(SongSelectController ctl, SongSelectSkin view, RectTransform layout)
    {
        var go = new GameObject("RushESecret"); go.transform.SetParent(layout, false);
        var secret = go.AddComponent<RushESecret>(); secret.controller = ctl; secret.skin = view;
        var rect = SongSelectVisuals.Rect(layout, "RushETileTransition", Vector2.zero, new Vector2(1920, 1080));
        secret.curtain = rect.gameObject.AddComponent<RushETransitionGraphic>(); secret.curtain.raycastTarget = true; rect.gameObject.SetActive(false);
        secret.sound = go.AddComponent<AudioSource>(); secret.sound.playOnAwake = false; secret.sound.spatialBlend = 0;
        secret.revealClip = CreateRevealSound();
        ctl.OnRushESecretChanged += secret.Changed; ctl.OnSelectionChanged += secret.SelectionChanged;
        return secret;
    }
    public bool Click()
    {
        if (Time.unscaledTimeAsDouble < suppressClickUntil) return false;
        return controller.TryRushEHit(Time.unscaledTimeAsDouble);
    }
    void Changed()
    {
        var state = controller.RushESecret;
        if (state.Hits > 0 && !state.Unlocked && transitionAt < 0)
            FindFirstObjectByType<SongSelectIdleGuide>()?.RegisterActivity();
        if (state.Transitioning && transitionAt < 0)
        {
            transitionAt = Time.unscaledTimeAsDouble;
            curtain.gameObject.SetActive(true); curtain.Show(0);
            sound.PlayOneShot(revealClip, .65f);
        }
    }
    void SelectionChanged(int index)
    {
        transitionAt = -1; suppressClickUntil = 0; red.Reset(); blue.Reset(); mouse.Reset();
        curtain.Show(-1); curtain.gameObject.SetActive(false); sound.Stop();
    }
    void Update()
    {
        if (controller == null || !skin.IsReady) return;
        double now = Time.unscaledTimeAsDouble;
        if (transitionAt >= 0)
        {
            float elapsed = (float)(now - transitionAt); curtain.Show(elapsed);
            if (elapsed >= RushESecretState.RevealSeconds) controller.RevealRushESecret();
            if (elapsed >= RushESecretState.TransitionSeconds)
            {
                transitionAt = -1; curtain.gameObject.SetActive(false);
                controller.FinishRushESecret(); FindFirstObjectByType<SongSelectAimPointer>()?.BeginArrival();
            }
            return;
        }
        bool eligible = !ScreenTransition.IsBusy && !controller.RushESecret.Unlocked
            && controller.SongIdAt(controller.SelectedIndex) == RushESecretState.SongId && skin.DiscsSettled
            && (Application.isFocused || Application.isBatchMode);
        Rect area = skin.SelectedDiscScreenRect;
        var input = InputPoint.Instance;
        bool r = eligible && input != null && input.IsRecentlyActive(.15), b = eligible && input != null && input.IsRecentlyActive2(.15);
        Vector2 pixels = new Vector2(Screen.width, Screen.height);
        bool redCut = red.Tick(Unit(r ? Vector2.Scale(input.NormalizedPosition, pixels) : Vector2.zero, area), r ? input.LastReceivedTime : 0, r);
        bool blueCut = blue.Tick(Unit(b ? Vector2.Scale(input.NormalizedPosition2, pixels) : Vector2.zero, area), b ? input.LastReceivedTime2 : 0, b);
        var pointer = Mouse.current;
        // 本編と同じく、マウスは移動だけで切る。押下を要求するとかざす操作しかできない。
        bool mouseCut = mouse.Tick(Unit(pointer != null ? pointer.position.ReadValue() : Vector2.zero, area), now,
            eligible && !r && !b && pointer != null);
        if (redCut || blueCut || mouseCut)
        {
            suppressClickUntil = now + .20;
            controller.TryRushEHit(now);
            // 斬撃の途中で、かざす操作の2秒が成立して通常譜面を開始しない。照準を動かす赤・マウスで切ったときだけ、
            // スタートのためを少し止める。入場扱いにすると照準が灰色にロックされ、隣の盤の曲送りまで遅れる。青は照準に触れない。
            if (redCut || mouseCut) FindFirstObjectByType<SongSelectAimPointer>()?.HoldOffStart();
        }
    }
    static Vector2 Unit(Vector2 point, Rect rect) => (point - rect.center) / Mathf.Max(1, Mathf.Min(rect.width, rect.height) * .5f);
    // 10回目にだけ鳴らす3段の膨らむ和音。途中の打音は作らない。
    static AudioClip CreateRevealSound()
    {
        const int rate = 44100; const float duration = 1.85f;
        var data = new float[Mathf.CeilToInt(rate * duration)];
        for (int i = 0; i < data.Length; i++)
        {
            float time = i / (float)rate, sample = 0;
            for (int k = 0; k < 3; k++)
            {
                float t = time - k * .29f; if (t < 0) continue;
                float env = Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / .82f)) * Mathf.Exp(-t * 1.25f);
                if (t > .82f) env = 0;
                float f = 329.6276f * Mathf.Pow(2, k * .5f);
                sample += env * (Mathf.Sin(2 * Mathf.PI * f * t + 4 * t * t) * .45f + Mathf.Sin(2 * Mathf.PI * f * 2 * t) * .12f);
            }
            data[i] = Mathf.Clamp(sample * .72f, -.75f, .75f);
        }
        var clip = AudioClip.Create("Rush E secret swell", data.Length, 1, rate, false);
        clip.SetData(data, 0); return clip;
    }
    void OnDestroy()
    {
        if (controller != null) { controller.OnRushESecretChanged -= Changed; controller.OnSelectionChanged -= SelectionChanged; }
        if (revealClip != null) Destroy(revealClip);
    }
}
