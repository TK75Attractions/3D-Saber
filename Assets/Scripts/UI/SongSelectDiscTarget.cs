using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// 選曲画面の平面ボタン。ゲームのノーツ・斬撃判定から独立した照準の的。
public sealed class SongSelectDiscTarget : MonoBehaviour, ICanvasRaycastFilter, IPointerEnterHandler, IPointerExitHandler
{
    public float HoldSeconds = 1;
    // かざして発射したときの動作。盤のクリックによる秘密の打音とは分ける。
    public System.Func<bool> ShootOverride;
    public bool Circle;
    public RectTransform HitArea;
    public float BottomInset;
    // 乗せたままでも、ため直すたびに続けて発射する(曲送りの盤)。スタートなどは一度外れるまで受け付けない。
    public bool RepeatWhileHeld;
    // 盤が滑っている間は照準をためない(止まってから1秒を数える)。クリックはそのまま受け付ける。
    public bool Sliding;
    public bool Hovered { get; private set; }
    public float Progress { get; private set; }
    public Button Button { get; private set; }
    // 2026-10-30 ユーザー「④もすべてやる」: お手本には成立した発射とクリックだけを知らせる。照準からのクリック呼び出しは二重に数えない。
    public event System.Action Fired;
    bool shooting;
    public bool Available => isActiveAndEnabled && Button != null && Button.isActiveAndEnabled && Button.IsInteractable();

    public static SongSelectDiscTarget Attach(Button button, float seconds, bool circle)
    {
        var target = button.gameObject.AddComponent<SongSelectDiscTarget>();
        target.Button = button; target.HoldSeconds = seconds; target.Circle = circle;
        button.onClick.AddListener(target.NotifyClick);
        return target;
    }
    public Rect ScreenRect() => SongSelectAimPointer.RectOnScreen(HitArea != null ? HitArea : (RectTransform)transform);
    public bool ContainsPoint(Vector2 point, float padding = 0)
    {
        Rect area = ScreenRect(), expanded = area;
        expanded.xMin -= padding; expanded.xMax += padding; expanded.yMin -= padding; expanded.yMax += padding;
        if (HitArea != null) expanded.yMax = area.yMax;
        float scale = area.height / Mathf.Max(1, ((RectTransform)transform).rect.height);
        // 下端の不感帯は、手ぶれ許容の拡張でも狭めない。
        return (BottomInset <= 0 || point.y >= area.yMin + BottomInset * scale)
            && SongSelectAimTracker.Contains(expanded, point, Circle);
    }
    public bool IsRaycastLocationValid(Vector2 point, Camera camera) => ContainsPoint(point);
    public void SetAimProgress(float progress) { Progress = progress; }
    void NotifyClick()
    {
        if (!shooting && Available && !ScreenTransition.IsBusy) Fired?.Invoke();
    }
    public bool TryShoot()
    {
        if (!Available || ScreenTransition.IsBusy) return false;
        bool accepted;
        shooting = true;
        try
        {
            if (ShootOverride != null) accepted = ShootOverride();
            else { Button.onClick.Invoke(); accepted = true; }
        }
        finally { shooting = false; }
        if (accepted) Fired?.Invoke();
        return accepted;
    }
    public void OnPointerEnter(PointerEventData e) { Hovered = true; }
    public void OnPointerExit(PointerEventData e) { Hovered = false; Progress = 0; }
    void OnDisable() { Hovered = false; Progress = 0; }
}
