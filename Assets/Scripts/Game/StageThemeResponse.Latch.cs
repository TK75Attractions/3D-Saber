using UnityEngine;

public sealed partial class StageThemeResponse
{
    // 固定デッキ上の一装置内で、片の着座と二つの爪の反動を完結させる。
    // すべて既存の表面／細部メッシュへ積み、部品別の描画物を生成しない。
    void DrawLatch(int lane, float age)
    {
        Vector3 c = Anchor(lane);
        float side = lane < 2 ? -1 : 1;
        float slide = LatchSlide(age);
        float recoil = LatchRecoil(age) * (lastReduced ? .3f : 1f);

        // 台座の底は固定側デッキの上面floor+.185。中央の浮沈床と接続しない。
        LatchBox(c + Vector3.down * .535f, new Vector3(.64f, .26f, .64f), Quaternion.identity);
        LatchBox(c + Vector3.down * .24f, new Vector3(.36f, .33f, .40f), Quaternion.identity);
        LatchBox(c, new Vector3(1.10f, .15f, .64f), Quaternion.identity);
        for (int rail = -1; rail <= 1; rail += 2)
            LatchBox(c + new Vector3(0, .1225f, rail * .2825f),
                new Vector3(1.10f, .095f, .075f), Quaternion.identity);

        // 固定ストッパの内面|x|=6.90へ、片の外面がちょうど接触する。
        // 反動する爪とは別の面なので、LOWでも着座後の接触が外れない。
        LatchBox(c + new Vector3(side * .36f, .215f, 0),
            new Vector3(.22f, .28f, .46f), Quaternion.identity);
        Vector3 slug = c + new Vector3(side * (-.29f + .36f * slide), .215f, 0);
        LatchBox(slug, new Vector3(.36f, .28f, .40f), Quaternion.identity);

        // 爪は受け口の前後に根を持つ小さいバネ。別列へ反応を渡さない。
        // 片が止まった後だけ一度外へ倒れ、持続する振動や点滅を付けない。
        for (int paw = -1; paw <= 1; paw += 2)
        {
            Vector3 pivot = c + new Vector3(side * .25f, .12f, paw * .25f);
            Quaternion turn = Quaternion.AngleAxis(paw * 8f * recoil, Vector3.right);
            LatchBox(pivot + turn * (Vector3.up * .11f), new Vector3(.28f, .22f, .05f), turn);
        }

        // 片の上面に常時同じ刻線を残し、増光でなく移動量を読み取れるようにする。
        // LOWの細部alphaは親のDraw後段が一度だけ下げる。
        Color detail = new Color(.35f, .46f, .52f, .26f * (lastProjector ? .78f : 1f));
        Vector3 mark = slug + Vector3.up * .144f;
        Stroke(mark + Vector3.left * .12f, mark + Vector3.right * .12f, .018f, detail, Vector3.forward);
    }

    static float LatchSlide(float age)
    {
        if (float.IsNaN(age) || float.IsInfinity(age) || age < 0 || age >= LatchLifetime) return 0;
        if (age < .08f)
        {
            float remaining = 1 - age / .08f;
            return .9f * (1 - remaining * remaining);
        }
        if (age < .12f) return .9f + .1f * LatchEase((age - .08f) / .04f);
        if (age <= .32f) return 1;
        return 1 - LatchEase((age - .32f) / (LatchLifetime - .32f));
    }

    static float LatchRecoil(float age)
    {
        if (float.IsNaN(age) || float.IsInfinity(age) || age <= .12f || age >= .34f) return 0;
        return age < .20f ? LatchEase((age - .12f) / .08f)
            : 1 - LatchEase((age - .20f) / .14f);
    }

    static float LatchEase(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3 - 2 * t);
    }

    // 配列やStageGeometryを毎回作らず、値型の8頂点から六面を積む。
    void LatchBox(Vector3 center, Vector3 size, Quaternion turn)
    {
        Vector3 right = turn * Vector3.right;
        Vector3 up = turn * Vector3.up;
        Vector3 forward = turn * Vector3.forward;
        Vector3 x = right * (size.x * .5f), y = up * (size.y * .5f), z = forward * (size.z * .5f);
        Vector3 p000 = center - x - y - z, p001 = center - x - y + z;
        Vector3 p010 = center - x + y - z, p011 = center - x + y + z;
        Vector3 p100 = center + x - y - z, p101 = center + x - y + z;
        Vector3 p110 = center + x + y - z, p111 = center + x + y + z;
        SolidQuad(p000, p010, p110, p100, -forward);
        SolidQuad(p001, p101, p111, p011, forward);
        SolidQuad(p000, p001, p011, p010, -right);
        SolidQuad(p100, p110, p111, p101, right);
        SolidQuad(p000, p100, p101, p001, -up);
        SolidQuad(p010, p011, p111, p110, up);
    }
}
