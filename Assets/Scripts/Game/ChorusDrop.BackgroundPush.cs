using System.Collections.Generic;
using UnityEngine;

public sealed partial class ChorusDrop
{
    struct BackgroundPush { public float age, seconds, strength; }
    readonly List<BackgroundPush> backgroundPushes = new List<BackgroundPush>();

    // サビとボルテージの押し込みを一つの書き手で合成する。戻り先を上書きしない。
    public void PushBackground(float seconds, float strength)
    {
        if (stage == null || DisplaySettings.ReducedEffects || !Finite(seconds) || !Finite(strength)
            || seconds <= 0 || strength <= 0 || backgroundPushes.Count >= 8) return;
        if (backgroundPushes.Count == 0) stageBase = stage.position;
        backgroundPushes.Add(new BackgroundPush
        {
            seconds = seconds,
            strength = Mathf.Min(.3f, strength)
        });
    }

    void AnimateBackground(float dt)
    {
        if (stage == null || backgroundPushes.Count == 0) return;
        float offset = 0;
        for (int i = backgroundPushes.Count - 1; i >= 0; i--)
        {
            var push = backgroundPushes[i];
            push.age += dt;
            if (push.age >= push.seconds) backgroundPushes.RemoveAt(i);
            else
            {
                offset += push.strength * Mathf.Sin(Mathf.PI * push.age / push.seconds);
                backgroundPushes[i] = push;
            }
        }
        KickOffset = Mathf.Min(.3f, offset);
        stage.position = stageBase + Vector3.forward * KickOffset;
    }

    public void ClearBackgroundPushes()
    {
        if (backgroundPushes.Count > 0 && stage != null) stage.position = stageBase;
        backgroundPushes.Clear();
        KickOffset = 0;
    }
}
