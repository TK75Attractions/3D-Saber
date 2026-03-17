using UnityEngine;

public class PlayerController : MonoBehaviour
{
    private Transform trans;
    private float speed = 4f;

    public void Init()
    {
        Debug.Log("PlayerController init");

        //this は、現在のクラスのインスタンスを指すキーワードです。
        trans = this.transform;
    }

    public void UpdatePlayer(float dt)
    {
        //GManager クラスの Control 変数を通じて、InputManager クラスのインスタンスにアクセスし、入力状態を取得します。
        float x = (GManager.Control.IManager.RightPressed ? 1 : 0) - (GManager.Control.IManager.LeftPressed ? 1 : 0);
        float y = (GManager.Control.IManager.UpPressed ? 1 : 0) - (GManager.Control.IManager.DownPressed ? 1 : 0);
        Move(new Vector2(x, y), dt);
    }

    public void Move(Vector2 direction, float dt)
    {
        float magnitude = direction.magnitude;
        if (magnitude == 0) return;
        Vector3 move = new Vector3(direction.x, direction.y, 0) * speed / magnitude * dt;
        trans.position += move;
    }
}
