using UnityEngine;
using UnityEngine.EventSystems;

// 運営の終了ボタン。クリックや短押しは取消し、押し続けたときだけ一度確定する。
public sealed class TitleQuitHold : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
{
    public const float HoldSeconds = 2f;
    System.Action confirm; System.Func<bool> allowed;
    SongSelectDiscGraphic ring;
    bool held; float elapsed;
    public void Initialize(System.Action action, System.Func<bool> canConfirm)
    {
        confirm=action; allowed=canConfirm;
        ring=SongSelectSkin.Graphic(transform,"HoldProgress",Vector2.zero,new Vector2(82,82),SongSelectDiscGraphic.Shape.Ring,Color.white);
        ring.Width=4; ring.Progress=0; ring.raycastTarget=false;
    }
    public void OnPointerDown(PointerEventData e)
    {
        if(e.button!=PointerEventData.InputButton.Left || allowed?.Invoke()!=true) return;
        held=true;elapsed=0;OperatorNotice.Show("終了するには × を２秒長押し");
    }
    public void OnPointerUp(PointerEventData e) => Cancel();
    public void OnPointerExit(PointerEventData e) => Cancel();
    void OnDisable() => Cancel();
    void Update() => Tick(Time.unscaledDeltaTime,Application.isFocused || Application.isBatchMode);
    public void Tick(float dt, bool focused)
    {
        if(!held)return;
        if(!focused || allowed?.Invoke()!=true) { Cancel();return; }
        elapsed+=Mathf.Max(0,dt);if(ring!=null)ring.Progress=Mathf.Clamp01(elapsed/HoldSeconds);
        if(elapsed<HoldSeconds)return;
        Cancel();confirm?.Invoke();
    }
    void Cancel(){held=false;elapsed=0;if(ring!=null)ring.Progress=0;}
}
