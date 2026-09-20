using UnityEngine;

public sealed partial class StageThemeResponse
{
    public const float VaneLifetime = .95f;

    // 柱の前面から短い金具で支える。左右の内外列は別々の柱へ対応する。
    public Vector3 VaneAnchor(int lane)
    {
        if (lane < 0 || lane >= LaneCount) return Vector3.zero;
        float side = lane < 2 ? -1 : 1;
        bool outer = lane == 0 || lane == 3;
        return new Vector3(side * 6.85f, floor + 2.65f, outer ? 11.10f : 20.10f);
    }

    public static float EvaluateVaneTurn(float age)
    {
        if (float.IsNaN(age) || float.IsInfinity(age) || age < 0 || age >= VaneLifetime) return 0;
        if (age < .20f) return VaneEase(age / .20f);
        if (age <= .30f) return 1;
        return 1 - VaneEase((age - .30f) / (VaneLifetime - .30f));
    }

    static float VaneEase(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3 - 2 * t);
    }

    // 非発光の一枚板が縦軸の周りで一度だけ向きを変える。尾の別ピークは作らない。
    // 部品を生成せず、既存の結合表面へ固定数の三角形を積む。
    void DrawVane(int lane, float age)
    {
        Vector3 c = VaneAnchor(lane);
        float side = lane < 2 ? -1 : 1;
        Vector3 mounting = new Vector3(side * 7.6f, c.y - .40f, c.z + .45f);

        // 既存柱の前面z=bayZ-.48へ取付座を埋め、二本の腕で軸の底を受ける。
        VaneBox(mounting, new Vector3(.40f, .64f, .20f), Quaternion.identity);
        Vector3 foot = c + Vector3.down * .50f;
        VaneBeam(mounting, foot, .12f);
        VaneBeam(mounting + Vector3.down * .23f, foot, .08f);
        VaneCylinder(c + Vector3.up * .02f, .055f, 1.08f);
        VaneCylinder(c + Vector3.down * .30f, .115f, .14f);
        VaneCylinder(c + Vector3.up * .50f, .115f, .14f);

        // 基礎角35度はLOWでも保ち、追加の30度だけを一度30%へ縮める。
        float turn = EvaluateVaneTurn(age) * (lastReduced ? .3f : 1f);
        Quaternion yaw = Quaternion.Euler(0, side * (35f + 30f * turn), 0);
        Vector3 outward = yaw * new Vector3(side, 0, 0);
        Vector3 front = yaw * Vector3.back;
        Vector3 hub = c + Vector3.up * .10f;
        VaneBox(hub + outward * .15f, new Vector3(.38f, .13f, .13f), yaw);

        // 長円は両端が丸く、矢尻や切る方向を示す形にしない。
        // 前後面と縁を閉じるので斜めから見ても薄い透明板にならない。
        const int segments = 16;
        const float radiusX = .67f, radiusY = .38f, halfDepth = .045f;
        Vector3 center = hub + outward * .46f;
        for (int i = 0; i < segments; i++)
        {
            float angleA = i * (Mathf.PI * 2 / segments);
            float angleB = (i + 1) * (Mathf.PI * 2 / segments);
            Vector3 a = outward * (Mathf.Cos(angleA) * radiusX) + Vector3.up * (Mathf.Sin(angleA) * radiusY);
            Vector3 b = outward * (Mathf.Cos(angleB) * radiusX) + Vector3.up * (Mathf.Sin(angleB) * radiusY);
            Vector3 aFront = center + a + front * halfDepth, bFront = center + b + front * halfDepth;
            Vector3 aBack = center + a - front * halfDepth, bBack = center + b - front * halfDepth;
            // 外周の寸法を保ち、中心だけ浅く膨らませて金属面の向きを読ませる。
            // 実三角面の法線を使い、頂点数・並び・時刻・回頭角は変えない。
            Vector3 frontCenter = center + front * .095f;
            Vector3 backCenter = center - front * .095f;
            Vector3 frontNormal = Vector3.Cross(aFront - frontCenter, bFront - frontCenter).normalized;
            Vector3 backNormal = Vector3.Cross(bBack - backCenter, aBack - backCenter).normalized;
            if (Vector3.Dot(frontNormal, front) < 0) frontNormal = -frontNormal;
            if (Vector3.Dot(backNormal, -front) < 0) backNormal = -backNormal;
            SolidTriangle(frontCenter, aFront, bFront, frontNormal);
            SolidTriangle(backCenter, bBack, aBack, backNormal);
            float middle = (angleA + angleB) * .5f;
            Vector3 edgeNormal = (outward * (Mathf.Cos(middle) / radiusX)
                + Vector3.up * (Mathf.Sin(middle) / radiusY)).normalized;
            SolidQuad(aFront, aBack, bBack, bFront, edgeNormal);
        }
    }

    void VaneCylinder(Vector3 center, float radius, float height)
    {
        const int segments = 8;
        Vector3 top = center + Vector3.up * (height * .5f);
        Vector3 bottom = center - Vector3.up * (height * .5f);
        for (int i = 0; i < segments; i++)
        {
            float a = i * (Mathf.PI * 2 / segments), b = (i + 1) * (Mathf.PI * 2 / segments);
            Vector3 va = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * radius;
            Vector3 vb = new Vector3(Mathf.Cos(b), 0, Mathf.Sin(b)) * radius;
            SolidTriangle(top, top + va, top + vb, Vector3.up);
            SolidTriangle(bottom, bottom + vb, bottom + va, Vector3.down);
            SolidQuad(bottom + va, top + va, top + vb, bottom + vb, (va + vb).normalized);
        }
    }

    void VaneBeam(Vector3 a, Vector3 b, float width)
    {
        Vector3 delta = b - a;
        VaneBox((a + b) * .5f, new Vector3(width, width, delta.magnitude),
            Quaternion.LookRotation(delta.normalized, Vector3.up));
    }

    void VaneBox(Vector3 center, Vector3 size, Quaternion turn)
    {
        Vector3 right = turn * Vector3.right, up = turn * Vector3.up, forward = turn * Vector3.forward;
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

