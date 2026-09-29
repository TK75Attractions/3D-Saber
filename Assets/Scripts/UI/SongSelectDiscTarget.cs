using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// 選曲画面の平面ボタン。ゲームのノーツ・斬撃判定から独立した照準の的。
public sealed class SongSelectDiscTarget : MonoBehaviour, ICanvasRaycastFilter, IPointerEnterHandler, IPointerExitHandler
{
    public float HoldSeconds = 1;
    public bool Circle;
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
    public Rect ScreenRect() => SongSelectAimPointer.RectOnScreen((RectTransform)transform);
    public bool IsRaycastLocationValid(Vector2 point, Camera camera) => SongSelectAimTracker.Contains(ScreenRect(), point, Circle);
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
