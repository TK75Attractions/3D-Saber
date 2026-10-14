using UnityEngine;
using UnityEngine.InputSystem;

// RUSH Eの盤だけ、照準をかざす操作とは別に「切る」遊びを用意する。
// カメラ両手・マウスドラッグ・クリックが同じコントローラーの解除状態を使う。
[DefaultExecutionOrder(90)]
public sealed class RushESecret : MonoBehaviour
{
    SongSelectController controller;
    SongSelectSkin skin;
    RushETransitionGraphic curtain;
    SongSelectDiscGraphic hitRing;
    AudioSource sound;
    AudioClip hitClip, revealClip;
    readonly RushEJacketStroke red = new RushEJacketStroke(), blue = new RushEJacketStroke(), mouse = new RushEJacketStroke();
    double transitionAt = -1, hitAt = -10, suppressClickUntil;
    public RushETransitionGraphic Curtain => curtain;
    public static RushESecret Build(SongSelectController ctl, SongSelectSkin view, RectTransform layout)
    {
        var go = new GameObject("RushESecret"); go.transform.SetParent(layout, false);
        var secret = go.AddComponent<RushESecret>(); secret.controller = ctl; secret.skin = view;
        secret.hitRing = SongSelectSkin.Graphic(layout, "RushEHitRing", new Vector2(0, 88), new Vector2(560, 560), SongSelectDiscGraphic.Shape.Ring, RushESecretState.Purple);
        secret.hitRing.Width = 6; secret.hitRing.gameObject.SetActive(false);
        var rect = SongSelectVisuals.Rect(layout, "RushETileTransition", Vector2.zero, new Vector2(1920, 1080));
        secret.curtain = rect.gameObject.AddComponent<RushETransitionGraphic>(); secret.curtain.raycastTarget = true; rect.gameObject.SetActive(false);
        secret.sound = go.AddComponent<AudioSource>(); secret.sound.playOnAwake = false; secret.sound.spatialBlend = 0;
        secret.hitClip = CreateSound(false); secret.revealClip = CreateSound(true);
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
        {
            hitAt = Time.unscaledTimeAsDouble;
            hitRing.gameObject.SetActive(true);
            sound.PlayOneShot(hitClip, .38f);
            FindFirstObjectByType<SongSelectAimPointer>()?.BeginArrival();
            FindFirstObjectByType<SongSelectIdleGuide>()?.RegisterActivity();
        }
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
        curtain.Show(-1); curtain.gameObject.SetActive(false); hitRing.gameObject.SetActive(false); sound.Stop();
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
                transitionAt = -1; curtain.gameObject.SetActive(false); hitRing.gameObject.SetActive(false);
                controller.FinishRushESecret(); FindFirstObjectByType<SongSelectAimPointer>()?.BeginArrival();
            }
            return;
        }
        if (hitRing.gameObject.activeSelf)
        {
            float age = (float)(now - hitAt);
            hitRing.rectTransform.sizeDelta = Vector2.one * (560 + 130 * age);
            var c = RushESecretState.Purple; c.a = Mathf.Clamp01(1 - age / .30f); hitRing.color = c;
            if (age >= .30f) hitRing.gameObject.SetActive(false);
        }
        bool eligible = !ScreenTransition.IsBusy && !controller.RushESecret.Unlocked
            && controller.SongIdAt(controller.SelectedIndex) == RushESecretState.SongId && skin.DiscsSettled
            && (Application.isFocused || Application.isBatchMode);
        Rect area = skin.SelectedDiscScreenRect;
        var input = InputPoint.Instance;
        bool r = eligible && input != null && input.IsRecentlyActive(.15), b = eligible && input != null && input.IsRecentlyActive2(.15);
        Vector2 pixels = new Vector2(Screen.width, Screen.height);
        bool cut = red.Tick(Unit(r ? Vector2.Scale(input.NormalizedPosition, pixels) : Vector2.zero, area), now, r);
        cut |= blue.Tick(Unit(b ? Vector2.Scale(input.NormalizedPosition2, pixels) : Vector2.zero, area), now, b);
        var pointer = Mouse.current;
        cut |= mouse.Tick(Unit(pointer != null ? pointer.position.ReadValue() : Vector2.zero, area), now,
            eligible && !r && !b && pointer != null && pointer.leftButton.isPressed);
        if (cut)
        {
            suppressClickUntil = now + .20;
            controller.TryRushEHit(now);
        }
    }
    static Vector2 Unit(Vector2 point, Rect rect) => (point - rect.center) / Mathf.Max(1, Mathf.Min(rect.width, rect.height) * .5f);
    // 楽曲音源から切り出さず、E音の打音と3段の膨らむ和音を合成する。
    public static AudioClip CreateSound(bool reveal)
    {
        const int rate = 44100; float duration = reveal ? 1.85f : .19f;
        var data = new float[Mathf.CeilToInt(rate * duration)];
        for (int i = 0; i < data.Length; i++)
        {
            float time = i / (float)rate, sample = 0;
            for (int k = 0; k < (reveal ? 3 : 1); k++)
            {
                float t = time - k * .29f; if (t < 0) continue;
                float env = reveal ? Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / .82f)) * Mathf.Exp(-t * 1.25f) : Mathf.Min(t / .004f, 1) * Mathf.Exp(-t * 26);
                if (reveal && t > .82f) env = 0;
                float f = reveal ? 329.6276f * Mathf.Pow(2, k * .5f) : 659.2551f;
                sample += env * (Mathf.Sin(2 * Mathf.PI * f * t + (reveal ? 4 * t * t : 0)) * .45f + Mathf.Sin(2 * Mathf.PI * f * 2 * t) * .12f);
            }
            data[i] = Mathf.Clamp(sample * (reveal ? .72f : 1), -.75f, .75f);
        }
        var clip = AudioClip.Create(reveal ? "Rush E secret swell" : "Rush E jacket hit", data.Length, 1, rate, false);
        clip.SetData(data, 0); return clip;
    }
    void OnDestroy()
    {
        if (controller != null) { controller.OnRushESecretChanged -= Changed; controller.OnSelectionChanged -= SelectionChanged; }
        if (hitClip != null) Destroy(hitClip); if (revealClip != null) Destroy(revealClip);
    }
}
