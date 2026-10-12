using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// 選曲画面の平面ボタン。ゲームのノーツ・斬撃判定から独立した照準の的。
public sealed class SongSelectDiscTarget : MonoBehaviour, ICanvasRaycastFilter, IPointerEnterHandler, IPointerExitHandler
{
    public float HoldSeconds = 1;
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
    public bool Available => isActiveAndEnabled && Button != null && Button.isActiveAndEnabled && Button.IsInteractable();

    public static SongSelectDiscTarget Attach(Button button, float seconds, bool circle)
    {
        var target = button.gameObject.AddComponent<SongSelectDiscTarget>();
        target.Button = button; target.HoldSeconds = seconds; target.Circle = circle;
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
    public bool TryShoot()
    {
        if (!Available || ScreenTransition.IsBusy) return false;
        Button.onClick.Invoke(); return true;
    }
    public void OnPointerEnter(PointerEventData e) { Hovered = true; }
    public void OnPointerExit(PointerEventData e) { Hovered = false; Progress = 0; }
    void OnDisable() { Hovered = false; Progress = 0; }
}
