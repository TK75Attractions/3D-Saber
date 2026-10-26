using System.Collections.Generic;
using UnityEngine;

// 闘牛の弾・消える輪・しびれFXを読み込み時に用意し、曲中は使い回す。
public sealed partial class BullfightMode
{
    const int FxCapacity = 64;
    Transform poolRoot;
    readonly Dictionary<string, Stack<ShotView>> idleShots = new Dictionary<string, Stack<ShotView>>();
    readonly Stack<Fx> idleFx = new Stack<Fx>();
    public int CreatedShotViewCount { get; private set; }
    public int CreatedFxCount { get; private set; }

    static double ShotVisualEnd(BullShot shot)
        => shot.Kind == "mine" || shot.Kind == "bomb" || shot.Kind == "wave"
            ? BullRules.HitEnd(shot) + .02 : shot.Time + .12;

    void WarmPools()
    {
        poolRoot = new GameObject("BullPool").transform;
        poolRoot.SetParent(transform, false);
        poolRoot.gameObject.SetActive(false);
        double approach = spawner != null ? spawner.approachTime : 1;
        var schedules = new Dictionary<string, List<(double time, int delta)>>();
        var samples = new Dictionary<string, BullShot>();
        foreach (var shot in Session.Shots)
        {
            if (!schedules.TryGetValue(shot.Kind, out var schedule))
            {
                schedule = new List<(double, int)>();
                schedules.Add(shot.Kind, schedule);
                samples.Add(shot.Kind, shot);
                idleShots.Add(shot.Kind, new Stack<ShotView>());
            }
            schedule.Add((shot.Time - approach, 1));
            schedule.Add((ShotVisualEnd(shot), -1));
        }
        foreach (var pair in schedules)
        {
            // 同時刻は出現を先に数え、境界を含む最大同時数を求める。
            pair.Value.Sort((a, b) => a.time != b.time
                ? a.time.CompareTo(b.time) : b.delta.CompareTo(a.delta));
            int live = 0, peak = 0;
            foreach (var edge in pair.Value) { live += edge.delta; peak = Mathf.Max(peak, live); }
            // SpawnViews が前コマの返却より先なので、新旧両コマ分を確保する。
            for (int i = 0; i < peak * 2; i++) ReturnShot(NewShot(samples[pair.Key]));
        }
        for (int i = 0; i < FxCapacity; i++)
        {
            var holder = new GameObject("BullFx").transform;
            holder.SetParent(poolRoot, false);
            var fx = new Fx { t = holder, r = Renderer(holder, "Shape", AimMeshes.Ring(.75f), QueueFx) };
            CreatedFxCount++;
            ReturnFx(fx);
        }
    }

    ShotView NewShot(BullShot shot)
    {
        CreatedShotViewCount++;
        return BuildShot(shot);
    }

    ShotView RentShot(BullShot shot)
    {
        if (!idleShots.TryGetValue(shot.Kind, out var pool))
        {
            pool = new Stack<ShotView>();
            idleShots.Add(shot.Kind, pool);
        }
        var view = pool.Count > 0 ? pool.Pop() : NewShot(shot);
        view.shot = shot;
        view.burst = false;
        ResetShotRoot(view.body);
        ResetShotRoot(view.marker);
        if (shot.Kind == "spear")
            view.body.localRotation = Quaternion.Euler(0, 0, shot.Angle % 360f);
        return view;
    }

    void ResetShotRoot(Transform root)
    {
        root.SetParent(transform, false);
        root.localPosition = Vector3.zero;
        root.localRotation = Quaternion.identity;
        root.localScale = Vector3.one;
        root.gameObject.SetActive(true);
    }

    void ReturnShot(ShotView view)
    {
        string kind = view.shot.Kind;
        view.body.gameObject.SetActive(false);
        view.marker.gameObject.SetActive(false);
        view.body.SetParent(poolRoot, false);
        view.marker.SetParent(poolRoot, false);
        view.shot = null;
        view.burst = false;
        idleShots[kind].Push(view);
    }

    void ReturnFx(Fx fx)
    {
        fx.t.gameObject.SetActive(false);
        fx.t.SetParent(poolRoot, false);
        fx.age = 0;
        idleFx.Push(fx);
    }

    void AdvanceEffects(float delta)
    {
        for (int i = effects.Count - 1; i >= 0; i--)
        {
            var fx = effects[i];
            fx.age += Mathf.Max(0, delta);
            float k = Mathf.Clamp01(fx.age / fx.life);
            if (k >= 1f) { ReturnFx(fx); effects.RemoveAt(i); continue; }
            fx.t.localScale = Vector3.one * Mathf.Lerp(fx.from, fx.to, 1 - (1 - k) * (1 - k));
            Tint(fx.r, fx.color, 1 - k);
        }
    }
}
