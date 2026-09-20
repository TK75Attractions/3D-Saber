using System;
using UnityEngine;

// 物理デバイスの左右と、ノーツの色を表すSaberHandは別の情報。
public enum PhysicalSaberSide { Unknown, Left, Right }
public enum CameraSaberColor { Red, Blue }

public readonly struct GameplaySwing
{
    public readonly ushort Sequence;
    public readonly PhysicalSaberSide Side;
    public readonly double ReceiveTime;
    public readonly uint XiaoTimestampUs;

    public GameplaySwing(ushort sequence, PhysicalSaberSide side, double receiveTime, uint xiaoTimestampUs = 0)
    {
        Sequence = sequence;
        Side = side;
        ReceiveTime = receiveTime;
        XiaoTimestampUs = xiaoTimestampUs;
    }
}

// Task 3はメインスレッドからPublishを呼べる。方向情報はこの境界に持ち込まない。
public interface IGameplaySwingSource
{
    event Action<GameplaySwing> SwingReceived;
    event Action SessionReset;
}

public class GameplaySwingAdapter : MonoBehaviour, IGameplaySwingSource
{
    [Tooltip("現行の左右情報なしストリームを読む。Task 3からPublishする場合はOFFにする。")]
    public bool readLegacyStream = true;
    [Tooltip("左右のない入力の明示的な割り当て。2台構成では推測せずUnknownのままにする。")]
    public PhysicalSaberSide legacyStreamSide = PhysicalSaberSide.Unknown;

    public event Action<GameplaySwing> SwingReceived;
    public event Action SessionReset;
    UdpImuBridge bound;

    // GamePlayManagerの判定ループから呼ぶ。独立したUpdateは追加しない。
    public void RefreshBinding()
    {
        UdpImuBridge next = isActiveAndEnabled && readLegacyStream ? UdpImuBridge.Instance : null;
        if (ReferenceEquals(next, bound)) return;
        Unbind();
        bound = next;
        if (bound != null)
        {
            bound.OnSwingReceived += OnLegacySwing;
            bound.OnSwingSessionReset += OnLegacyReset;
        }
        ResetSession();
    }

    void OnLegacySwing(SwingEvent swing)
    {
        PhysicalSaberSide side = swing.Side == SaberSide.Left
            ? PhysicalSaberSide.Left
            : swing.Side == SaberSide.Right
                ? PhysicalSaberSide.Right
                : legacyStreamSide;
        Publish(swing, side);
    }
    void OnLegacyReset(long unused) => ResetSession();

    public void Publish(SwingEvent swing, PhysicalSaberSide side)
    {
        Publish(new GameplaySwing(swing.Sequence, side, swing.LocalReceiveTimeSeconds, swing.XiaoTimestampUs));
    }

    public void Publish(GameplaySwing swing)
    {
        if (isActiveAndEnabled) SwingReceived?.Invoke(swing);
    }

    public void ResetSession() => SessionReset?.Invoke();

    void Unbind()
    {
        if (!ReferenceEquals(bound, null))
        {
            bound.OnSwingReceived -= OnLegacySwing;
            bound.OnSwingSessionReset -= OnLegacyReset;
        }
        bound = null;
    }

    void OnDisable() { Unbind(); ResetSession(); }
}
