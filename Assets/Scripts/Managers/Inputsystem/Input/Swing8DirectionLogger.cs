using UnityEngine;

// 既存のゲーム判定向け互換窓口。判定は行わず、XIAO/Virtual IMU の
// SwingStart eventを購読してCutDirectionへ変換する。
public class Swing8DirectionLogger : MonoBehaviour
{
    public static Swing8DirectionLogger Instance { get; private set; }

    private int lastDirectionIndex = -1;
    private float lastEventTime = -999f;
    private long lastReceiveTimestampTicks;
    private long minimumReceiveTimestampTicks;
    private UdpImuBridge subscribedBridge;

    public int LastDirectionIndex => lastDirectionIndex;
    // 旧表示用のUnity配送時刻。実際の判定ではTryGetRecentを使う。
    public float LastDirectionTime => lastEventTime;
    public CutDirection LastDirection => ToCutDirection(lastDirectionIndex);

    public static bool TryGetLatest(out CutDirection direction, out float time)
    {
        if (Instance == null || !Instance.isActiveAndEnabled || Instance.lastDirectionIndex < 0 ||
            (Instance.subscribedBridge != null && !Instance.subscribedBridge.isActiveAndEnabled))
        {
            direction = CutDirection.None;
            time = -999f;
            return false;
        }
        direction = Instance.LastDirection;
        time = Instance.lastEventTime;
        return Instance.lastDirectionIndex >= 0;
    }

    public static bool TryGetRecent(double maximumAgeSeconds, out CutDirection direction)
    {
        return TryGetRecent(maximumAgeSeconds, SwingMonotonicClock.Timestamp, out direction);
    }

    // 時刻を明示できる入口。同じ判定パスでの共有と待機なしの境界検証に使う。
    public static bool TryGetRecent(double maximumAgeSeconds, long nowTicks, out CutDirection direction)
    {
        if (TryGetLatest(out direction, out _))
        {
            double ageMs = SwingMonotonicClock.ElapsedMilliseconds(
                Instance.lastReceiveTimestampTicks, nowTicks);
            // 上限0は期限なしではなく、受信と同時刻だけを許可する。
            if (ageMs >= 0.0 && ageMs <= maximumAgeSeconds * 1000.0) return true;
        }
        direction = CutDirection.None;
        return false;
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
        // 初回起動では受信済みのキューを許可し、再有効化では停止中の入力を持ち越さない。
        if (minimumReceiveTimestampTicks != 0)
            minimumReceiveTimestampTicks = SwingMonotonicClock.Timestamp;
        if (UdpImuBridge.Instance != null)
        {
            Subscribe(UdpImuBridge.Instance);
        }
    }

    private void OnDisable()
    {
        HandleSessionReset(SwingMonotonicClock.Timestamp);
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
        subscribedBridge.OnSwingSessionReset += HandleSessionReset;
    }

    private void Unsubscribe()
    {
        if (subscribedBridge != null)
        {
            subscribedBridge.OnSwingReceived -= HandleSwing;
            subscribedBridge.OnSwingSessionReset -= HandleSessionReset;
            subscribedBridge = null;
        }
        ClearDirection();
    }

    private void ClearDirection()
    {
        lastDirectionIndex = -1;
        lastEventTime = -999f;
        lastReceiveTimestampTicks = 0;
    }

    private void HandleSessionReset(long resetReceiveTimestampTicks)
    {
        minimumReceiveTimestampTicks = System.Math.Max(minimumReceiveTimestampTicks, resetReceiveTimestampTicks);
        ClearDirection();
    }

    private void HandleSwing(SwingEvent swing)
    {
        if (!isActiveAndEnabled || swing.LocalReceiveTimestampTicks < minimumReceiveTimestampTicks ||
            (subscribedBridge != null && !subscribedBridge.isActiveAndEnabled)) return;
        lastDirectionIndex = ToLegacyDirectionIndex(swing.Direction);
        lastEventTime = Time.time;
        lastReceiveTimestampTicks = swing.LocalReceiveTimestampTicks;
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
