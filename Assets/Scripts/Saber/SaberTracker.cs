using UnityEngine;

// セーバー（あるいはポインター）の位置履歴と速度を保持する。
// Pointer が Update で transform.position を書くので、こちらは LateUpdate で読む。
public class SaberTracker : MonoBehaviour
{
    public Vector3 PreviousPosition { get; private set; }
    public Vector3 CurrentPosition { get; private set; }
    public Vector3 Velocity { get; private set; }
    public float Speed { get; private set; }
    public bool HasPrevious { get; private set; }

    void OnEnable()
    {
        ResetTo(transform.position);
    }

    void LateUpdate()
    {
        Tick(transform.position, Time.deltaTime);
    }

    // テストから直接呼べるように純粋ロジックを切り出している。
    public void Tick(Vector3 newPosition, float dt)
    {
        PreviousPosition = CurrentPosition;
        CurrentPosition = newPosition;

        float safeDt = Mathf.Max(dt, 0.0001f);
        Velocity = (CurrentPosition - PreviousPosition) / safeDt;
        Speed = Velocity.magnitude;
        HasPrevious = true;
    }

    public void ResetTo(Vector3 position)
    {
        PreviousPosition = position;
        CurrentPosition = position;
        Velocity = Vector3.zero;
        Speed = 0f;
        HasPrevious = false;
    }
}
