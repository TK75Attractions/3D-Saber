using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// 2026-10-30 ユーザー「④もすべてやる」: お手本の時間と発射回数を、実際の照準から独立して持つ。
public sealed class SongSelectGhostState
{
    public const float EntrySeconds = 8, IdleSeconds = 12;
    double elapsed, sinceShot;
    public int Shots { get; private set; }

    public void Tick(float seconds)
    {
        if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0) return;
        elapsed += seconds; sinceShot += seconds;
    }

    public void RegisterShot()
    {
        Shots = Mathf.Min(2, Shots + 1);
        sinceShot = 0;
    }

    public bool ShouldShow(bool card, bool confirm, bool busy, bool staff, float progress)
    {
        return Shots < 2 && !card && !confirm && !busy && !staff && progress <= 0
            && (Shots == 0 ? elapsed >= EntrySeconds : sinceShot >= IdleSeconds);
    }
}

// 2026-10-30 ユーザー「④もすべてやる」: 表示専用。的の検索・ため・発射・実照準の位置には書き込まない。
[DefaultExecutionOrder(250)]
public sealed class SongSelectGhostGuide : MonoBehaviour
{
    public const float LoopSeconds = 7, MoveSeconds = .6f;
    public static System.Func<bool> ConfirmOpen = () => false;
    public static bool ConfirmationOpen => ConfirmOpen != null && ConfirmOpen();

    readonly SongSelectGhostState state = new SongSelectGhostState();
    SongSelectController ctl;
    SongSelectSkin skin;
    SongSelectAimPointer aim;
    SongSelectIdleGuide guide;
    SongSelectDiscTarget[] targets;
    SongSelectAimGraphic reticle;
    float age;

    public bool IsVisible => reticle != null && reticle.gameObject.activeSelf;
    public int Shots => state.Shots;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetHook() { ConfirmOpen = () => false; }

    public static SongSelectGhostGuide Build(SongSelectController ctl, SongSelectSkin skin,
        SongSelectAimPointer aim, SongSelectIdleGuide guide, Transform layout,
        IEnumerable<SongSelectDiscTarget> choices)
    {
        var root = SongSelectVisuals.Rect(layout, "SongSelectGhostGuide", Vector2.zero, Vector2.zero);
        var demo = root.gameObject.AddComponent<SongSelectGhostGuide>();
        demo.ctl = ctl; demo.skin = skin; demo.aim = aim; demo.guide = guide;
        var group = root.gameObject.AddComponent<CanvasGroup>();
        group.alpha = .45f; group.blocksRaycasts = false; group.interactable = false;
        float size = (SongSelectAimGraphic.ReticleRadius + SongSelectAimGraphic.RingWidth) * 2 + 12;
        demo.reticle = SongSelectVisuals.Rect(root, "GhostReticle", Vector2.zero, Vector2.one * size)
            .gameObject.AddComponent<SongSelectAimGraphic>();
        demo.reticle.raycastTarget = false;
        demo.targets = choices.Where(t => t != null).Distinct().ToArray();
        foreach (var target in demo.targets) target.Fired += demo.OnShot;
        demo.Hide();
        return demo;
    }

    void Update() { Tick(Time.unscaledDeltaTime); }

    public void Tick(float seconds)
    {
        state.Tick(seconds);
        bool busy = ScreenTransition.IsBusy || ctl == null || skin == null || aim == null;
        if (!busy)
            busy = !skin.IsReady || !skin.DiscsSettled || ctl.StartAccepted || ctl.RushESecret.Transitioning
                || ctl.SongCount < 2 || ctl.difficultyButtons == null || ctl.difficultyButtons.Length < 2
                || ctl.difficultyButtons[1] == null || !ctl.difficultyButtons[1].gameObject.activeInHierarchy
                || !ctl.difficultyButtons[1].IsInteractable();

        // 赤を見失って保っているだけのため(2026-10-31 から保つ)は、ためている最中に数えない(腕を下ろした人にもお手本を出す)。
        if (!state.ShouldShow(guide != null && guide.IsVisible, ConfirmationOpen, busy,
            SongSelectStaffGuide.IsOpen, aim != null && !aim.HoldingLostSaber ? aim.Progress01 : 0))
        {
            Hide(); return;
        }

        if (!IsVisible) age = 0;
        else if (!float.IsNaN(seconds) && !float.IsInfinity(seconds)) age += Mathf.Max(0, seconds);
        Sample(age, out Vector2 point, out float progress);
        reticle.gameObject.SetActive(true);
        reticle.rectTransform.anchoredPosition = point;
        reticle.Show(progress, false);
    }

    public static void Sample(float seconds, out Vector2 point, out float progress)
    {
        if (float.IsNaN(seconds) || float.IsInfinity(seconds)) seconds = 0;
        float t = Mathf.Repeat(Mathf.Max(0, seconds), LoopSeconds);
        Vector2 right = SongSelectSkin.DiscSlotPosition(1);
        Vector2 normal = new Vector2(0, -288);
        Vector2 center = SongSelectSkin.DiscSlotPosition(0);
        progress = 0;
        if (t <= 1) { point = right; progress = t; }
        else if (t < 1.6f) point = Move(right, normal, (t - 1) / MoveSeconds);
        else if (t <= 2.6f) { point = normal; progress = t - 1.6f; }
        else if (t < 3.2f) point = Move(normal, center, (t - 2.6f) / MoveSeconds);
        else if (t <= 6.4f) { point = center; progress = Mathf.Clamp01((t - 3.2f) / 2); }
        else point = Move(center, right, (t - 6.4f) / MoveSeconds);
    }

    static Vector2 Move(Vector2 from, Vector2 to, float t) =>
        Vector2.Lerp(from, to, Mathf.SmoothStep(0, 1, t));

    void OnShot() { state.RegisterShot(); Hide(); }
    void Hide() { age = 0; if (reticle != null) reticle.gameObject.SetActive(false); }
    void OnDisable() { Hide(); }
    void OnDestroy()
    {
        if (targets == null) return;
        foreach (var target in targets) if (target != null) target.Fired -= OnShot;
    }
}
