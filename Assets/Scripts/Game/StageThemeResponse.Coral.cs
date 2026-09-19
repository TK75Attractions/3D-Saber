using UnityEngine;

public sealed partial class StageThemeResponse
{
    void DrawCoral(int lane, float age)
    {
        Vector3 root = Anchor(lane);
        int side = lane < 2 ? -1 : 1;
        float opening = CoralOpening(age) * (lastReduced ? .3f : 1);
        // 岩の三角面より下へ根を埋め、幹と枝は成功中も動かさない。
        Vector3 fork = root + new Vector3(side * .03f, .46f, .02f);
        CoralStem(root, fork, .09f, .065f);
        for (int polyp = 0; polyp < 3; polyp++)
        {
            Vector3 offset = polyp == 0 ? new Vector3(-side * .20f, .80f, -.20f)
                : polyp == 1 ? new Vector3(side * .22f, 1.03f, .04f)
                : new Vector3(side * .02f, .69f, .29f);
            Vector3 center = root + offset;
            CoralStem(fork, center, .055f, .04f);
            CoralBody(center);
            for (int tentacle = 0; tentacle < 6; tentacle++)
            {
                float angle = tentacle * Mathf.PI / 3 + polyp * .41f;
                Vector3 radial = new Vector3(side * Mathf.Cos(angle), 0, Mathf.Sin(angle));
                Vector3 across = new Vector3(-side * Mathf.Sin(angle), 0, Mathf.Cos(angle));
                // 根を固定し、中点と先端の開き方を変えて肉厚の触手をしならせる。
                // 全房を同時に開く一周期で、常時の海藻揺れや剛体の絞りとは分ける。
                Vector3 a = center + radial * .10f + Vector3.up * .025f;
                Vector3 b = center + radial * (.14f + .10f * opening) + Vector3.up * (.23f + .02f * opening);
                Vector3 c = center + radial * (.08f + .30f * opening) + Vector3.up * (.44f - .08f * opening);
                Vector3 rootAlong = (radial * .04f + Vector3.up * .205f).normalized;
                Vector3 axisA = Vector3.Cross(rootAlong, across).normalized;
                Vector3 axisB = Vector3.Cross((c - a).normalized, across).normalized;
                Vector3 axisC = Vector3.Cross((c - b).normalized, across).normalized;
                CoralTubeSides(a, b, .034f, .032f, across, axisA, axisB);
                CoralTubeSides(b, c, .032f, .018f, across, axisB, axisC);
                CoralTubeCap(a, .034f, across, axisA, -rootAlong);
                CoralTubeCap(c, .018f, across, axisC, (c - b).normalized);
            }
        }
    }

    static float CoralOpening(float age)
    {
        if (!Finite(age) || age < 0 || age >= CoralLifetime) return 0;
        if (age < .25f) return CoralSmooth(age / .25f);
        if (age <= .50f) return 1;
        return 1 - CoralSmooth((age - .50f) / (CoralLifetime - .50f));
    }

    static float CoralSmooth(float value)
    {
        float t = Mathf.Clamp01(value);
        return t * t * (3 - 2 * t);
    }

    void CoralStem(Vector3 a, Vector3 b, float radiusA, float radiusB)
    {
        Vector3 along = (b - a).normalized;
        Vector3 across = Vector3.Cross(along, Vector3.forward).normalized;
        Vector3 other = Vector3.Cross(along, across).normalized;
        CoralTubeSides(a, b, radiusA, radiusB, across, other, other);
        CoralTubeCap(a, radiusA, across, other, -along);
        CoralTubeCap(b, radiusB, across, other, along);
    }

    void CoralBody(Vector3 center)
    {
        Vector3 top = center + Vector3.up * .18f;
        Vector3 bottom = center - Vector3.up * .13f;
        for (int face = 0; face < 4; face++)
        {
            float a = face * Mathf.PI * .5f, b = (face + 1) * Mathf.PI * .5f;
            Vector3 p = center + new Vector3(Mathf.Cos(a) * .17f, 0, Mathf.Sin(a) * .17f);
            Vector3 q = center + new Vector3(Mathf.Cos(b) * .17f, 0, Mathf.Sin(b) * .17f);
            SolidTriangle(p, q, top, ((p + q + top) / 3 - center).normalized);
            SolidTriangle(q, p, bottom, ((p + q + bottom) / 3 - center).normalized);
        }
    }

    // 三角断面を二節で共有し、節の隙間や先端の平面だけの花弁を作らない。
    void CoralTubeSides(Vector3 a, Vector3 b, float radiusA, float radiusB,
        Vector3 across, Vector3 axisA, Vector3 axisB)
    {
        for (int face = 0; face < 3; face++)
        {
            float from = face * Mathf.PI * 2 / 3, to = (face + 1) * Mathf.PI * 2 / 3;
            Vector3 a0 = across * Mathf.Cos(from) + axisA * Mathf.Sin(from);
            Vector3 a1 = across * Mathf.Cos(to) + axisA * Mathf.Sin(to);
            Vector3 b0 = across * Mathf.Cos(from) + axisB * Mathf.Sin(from);
            Vector3 b1 = across * Mathf.Cos(to) + axisB * Mathf.Sin(to);
            Vector3 normal = (a0 + a1 + b0 + b1).normalized;
            SolidQuad(a + a0 * radiusA, b + b0 * radiusB,
                b + b1 * radiusB, a + a1 * radiusA, normal);
        }
    }

    void CoralTubeCap(Vector3 center, float radius, Vector3 across, Vector3 axis, Vector3 normal)
    {
        Vector3 a = center + across * radius;
        Vector3 b = center + (across * Mathf.Cos(Mathf.PI * 2 / 3) + axis * Mathf.Sin(Mathf.PI * 2 / 3)) * radius;
        Vector3 c = center + (across * Mathf.Cos(Mathf.PI * 4 / 3) + axis * Mathf.Sin(Mathf.PI * 4 / 3)) * radius;
        SolidTriangle(a, b, c, normal);
    }
}
