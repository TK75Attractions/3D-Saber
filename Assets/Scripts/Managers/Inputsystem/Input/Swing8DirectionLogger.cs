using UnityEngine;

// 既存のゲーム判定向け互換窓口。判定は行わず、XIAO/Virtual IMU の
// SwingStart eventを購読してCutDirectionへ変換する。
public class Swing8DirectionLogger : MonoBehaviour
{
    public static Swing8DirectionLogger Instance { get; private set; }

    private int lastDirectionIndex = -1;
    private float lastEventTime = -999f;
    private UdpImuBridge subscribedBridge;

    public int LastDirectionIndex => lastDirectionIndex;
    public float LastDirectionTime => lastEventTime;
    public CutDirection LastDirection => ToCutDirection(lastDirectionIndex);

    public static bool TryGetLatest(out CutDirection direction, out float time)
    {
        if (Instance == null)
        {
            direction = CutDirection.None;
            time = -999f;
            return false;
        }
        direction = Instance.LastDirection;
        time = Instance.lastEventTime;
        return Instance.lastDirectionIndex >= 0;
    }

    private void Awake()
    {
        Instance = this;
    }

    private void Start()
    {
        Subscribe(UdpImuBridge.Instance);
    }

    private void OnEnable()
    {
        if (UdpImuBridge.Instance != null)
        {
            Subscribe(UdpImuBridge.Instance);
        }
    }

    private void OnDisable()
    {
        Unsubscribe();
    }

    private void OnDestroy()
    {
        Unsubscribe();
        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void Subscribe(UdpImuBridge bridge)
    {
        if (bridge == null || subscribedBridge == bridge)
        {
            return;
        }
        Unsubscribe();
        subscribedBridge = bridge;
        subscribedBridge.OnSwingReceived += HandleSwing;
    }

    private void Unsubscribe()
    {
        if (subscribedBridge != null)
        {
            subscribedBridge.OnSwingReceived -= HandleSwing;
            subscribedBridge = null;
        }
    }

    private void HandleSwing(SwingEvent swing)
    {
        lastDirectionIndex = ToLegacyDirectionIndex(swing.Direction);
        lastEventTime = Time.time;
    }

    public static int ToLegacyDirectionIndex(SwingDirection direction)
    {
        switch (direction)
        {
            case SwingDirection.Right: return 0;
            case SwingDirection.Up: return 2;
            case SwingDirection.Left: return 4;
            case SwingDirection.Down: return 6;
            default: return -1;
        }
    }

    private static CutDirection ToCutDirection(int directionIndex)
    {
        return directionIndex >= 0
            ? CutDirectionHelper.FromSwing8Index(directionIndex)
            : CutDirection.None;
    }

    // 旧テストと方向ユーティリティ利用箇所の互換用。実入力判定には使わない。
    public static int Get8DirectionIndex(Vector2 vector)
    {
        float angle = Mathf.Atan2(vector.y, vector.x) * Mathf.Rad2Deg;
        if (angle < 0f)
        {
            angle += 360f;
        }
        return Mathf.FloorToInt((angle + 22.5f) / 45f) % 8;
    }
}
