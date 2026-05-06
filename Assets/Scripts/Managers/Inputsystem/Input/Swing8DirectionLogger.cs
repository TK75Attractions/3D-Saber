using UnityEngine;

public class Swing8DirectionLogger : MonoBehaviour
{
    public static Swing8DirectionLogger Instance { get; private set; }

    [Header("Source")]
    [SerializeField] private bool useAcceleration = true;

    [Header("Detection")]
    [SerializeField] private float threshold = 0.35f;
    [SerializeField] private float cooldownSeconds = 0.20f;
    [SerializeField] private bool logOnlyOnChange = true;

    private int lastDirectionIndex = -1;
    private float lastLogTime = -999f;

    // 外部から最新の振り検知方向を読む。-1 = 未検知。
    public int LastDirectionIndex => lastDirectionIndex;
    public float LastDirectionTime => lastLogTime;
    public CutDirection LastDirection =>
        lastDirectionIndex >= 0 ? CutDirectionHelper.FromSwing8Index(lastDirectionIndex) : CutDirection.None;

    // 静的アクセサ：シングルトンが無くても安全に呼べる。
    public static bool TryGetLatest(out CutDirection direction, out float time)
    {
        if (Instance == null)
        {
            direction = CutDirection.None;
            time = -999f;
            return false;
        }
        direction = Instance.LastDirection;
        time = Instance.lastLogTime;
        return Instance.lastDirectionIndex >= 0;
    }

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        if (!UdpImuBridge.TryGetLatest(out Vector3 acceleration, out Vector3 gyro, out bool connected) || !connected)
        {
            return;
        }

        Vector2 plane = useAcceleration
            ? new Vector2(acceleration.x, acceleration.y)
            : new Vector2(gyro.x, gyro.y);

        if (plane.magnitude < threshold)
        {
            return;
        }

        int directionIndex = Get8DirectionIndex(plane);
        if (directionIndex < 0)
        {
            return;
        }

        if (Time.time - lastLogTime < cooldownSeconds)
        {
            return;
        }

        if (logOnlyOnChange && directionIndex == lastDirectionIndex)
        {
            return;
        }

        lastDirectionIndex = directionIndex;
        lastLogTime = Time.time;

        Debug.Log($"Swing8Direction: {GetDirectionName(directionIndex)}  raw=({plane.x:F3}, {plane.y:F3})");
    }

    public static int Get8DirectionIndex(Vector2 vector)
    {
        float angle = Mathf.Atan2(vector.y, vector.x) * Mathf.Rad2Deg;
        if (angle < 0f)
        {
            angle += 360f;
        }

        return Mathf.FloorToInt((angle + 22.5f) / 45f) % 8;
    }

    private static string GetDirectionName(int index)
    {
        switch (index)
        {
            case 0: return "Right";
            case 1: return "UpRight";
            case 2: return "Up";
            case 3: return "UpLeft";
            case 4: return "Left";
            case 5: return "DownLeft";
            case 6: return "Down";
            case 7: return "DownRight";
            default: return "Unknown";
        }
    }
}
