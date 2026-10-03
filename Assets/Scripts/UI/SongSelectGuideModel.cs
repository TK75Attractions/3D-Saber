using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// 立体の関節人形をUIメッシュへ透視投影する。追加カメラ・ライト・物理判定は不要。
// 人形の背後から見せ、本人の右手と画面上の右側を一致させる。
[RequireComponent(typeof(CanvasRenderer))]
public sealed class SongSelectGuideModel : MaskableGraphic
{
    struct Face
    {
        public Vector3 a, b, c;
        public Color ca, cb, cc;
        public float depth;
    }
    sealed class BackToFront : IComparer<Face>
    {
        public int Compare(Face a, Face b) => b.depth.CompareTo(a.depth);
    }
    static readonly BackToFront Sorter = new BackToFront();
    static readonly Vector3 Eye = new Vector3(3, 2.65f, -6);
    static readonly Vector3 Center = new Vector3(0, 1.02f, 0);
    static readonly Vector3 Forward = (Center - Eye).normalized;
    static readonly Vector3 Right = Vector3.Cross(Vector3.up, Forward).normalized;
    static readonly Vector3 Up = Vector3.Cross(Forward, Right);
    static readonly Vector3 Light = new Vector3(-.5f, .9f, -1).normalized;
    static readonly Color Body = new Color(.78f, .88f, .94f);
    static readonly Color Joint = new Color(.16f, .28f, .36f);
    static readonly Color Red = new Color(1, .22f, .34f);
    static readonly Vector3[] Sphere = MakeSphere();
    readonly List<Face> faces = new List<Face>(6000);
    float seconds;
    Vector2 handPoint, tipPoint;
    public void SetPose(float time) { seconds = Mathf.Max(0, time); SetVerticesDirty(); }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear(); faces.Clear();
        float cycle = seconds % 5.2f;
        float lift = Mathf.SmoothStep(0, 1, (cycle - .25f) / 1.15f) * (1 - Mathf.SmoothStep(0, 1, (cycle - 4.25f) / .9f));
        float breathe = Mathf.Sin(seconds * 2) * .012f;
        var shoulder = new Vector3(.29f, 1.51f + breathe, 0);
        var elbow = Vector3.Lerp(new Vector3(.42f, 1.15f, -.015f), new Vector3(.5f, 1.25f, .26f), lift);
        var hand = Vector3.Lerp(new Vector3(.47f, .93f, .02f), new Vector3(.65f, 1.51f, .5f), lift);
        hand.x += Mathf.Sin(seconds * 1.8f) * .035f * lift;
        var direction = Vector3.Lerp(new Vector3(.35f, .8f, .07f), new Vector3(.12f, .9f, .4f), lift).normalized;
        var tip = hand + direction * .86f;
        handPoint = Project(hand); tipPoint = Project(tip);

        // 台座、足、胴体、頭、左右の腕。右だけを赤で示す。
        Ball(new Vector3(0, -.035f, 0), new Vector3(.61f, .06f, .44f), new Color(.07f, .17f, .22f));
        for (int side = -1; side <= 1; side += 2)
        {
            var hip = new Vector3(side * .14f, .88f, 0);
            var knee = new Vector3(side * .18f, .48f, .025f);
            var ankle = new Vector3(side * .2f, .14f, -.025f);
            Limb(hip, knee, .108f, Body); Ball(knee, Vector3.one * .089f, Joint);
            Limb(knee, ankle, .082f, Body);
            Ball(new Vector3(side * .2f, .075f, .055f), new Vector3(.105f, .075f, .2f), Body);
        }
        Ball(new Vector3(0, .89f, 0), new Vector3(.24f, .17f, .15f), Joint);
        Ball(new Vector3(0, 1.27f + breathe, 0), new Vector3(.285f, .37f, .175f), Body);
        Limb(new Vector3(0, 1.53f, 0), new Vector3(0, 1.69f, 0), .073f, Joint);
        Ball(new Vector3(0, 1.835f + breathe, .01f), new Vector3(.163f, .211f, .16f), Body);
        // 背面の細い発光ラインが体の向きと立体感を伝える。
        Limb(new Vector3(0, 1.16f, -.157f), new Vector3(0, 1.46f, -.161f), .012f, new Color(.25f, .9f, .92f));
        var leftShoulder = new Vector3(-.29f, 1.5f + breathe, 0);
        var leftElbow = new Vector3(-.37f, 1.15f, .015f);
        var leftHand = new Vector3(-.39f, .88f, .075f);
        Ball(leftShoulder, Vector3.one * .115f, Joint);
        Limb(leftShoulder, leftElbow, .088f, Body);
        Ball(leftElbow, Vector3.one * .071f, Joint);
        Limb(leftElbow, leftHand, .069f, Body);
        Ball(leftHand, new Vector3(.07f, .105f, .066f), Body);
        Ball(shoulder, Vector3.one * .12f, Red);
        Limb(shoulder, elbow, .091f, Body); Ball(elbow, Vector3.one * .076f, Joint);
        Limb(elbow, hand, .071f, Body);
        Ball(hand - direction * .07f, Vector3.one * .078f, Red);
        Tube(hand - direction * .14f, hand + direction * .12f, .047f, Joint);
        Ball(hand, new Vector3(.083f, .085f, .072f), Body);
        Tube(hand + direction * .12f, hand + direction * .18f, .063f, Red);
        Tube(hand + direction * .18f, tip, .035f, Red, true);
        Vector3 coreOffset = Vector3.ProjectOnPlane(Eye - hand, direction).normalized * .034f;
        Tube(hand + direction * .2f + coreOffset, tip + coreOffset, .012f, new Color(1, .88f, .91f), true);
        Ball(tip, Vector3.one * .034f, new Color(1, .7f, .77f));

        faces.Sort(Sorter);
        foreach (var face in faces)
        {
            int i = vh.currentVertCount;
            vh.AddVert(Project(face.a), face.ca, Vector2.zero);
            vh.AddVert(Project(face.b), face.cb, Vector2.zero);
            vh.AddVert(Project(face.c), face.cc, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2);
        }
        // 操作例の照準。実際の選択には触れず、かざす先をゆっくり示す。
        var target = new Vector2(128, 124);
        Color cyan = new Color(.3f, 1, .96f, .55f + .4f * lift);
        Ring(vh, target, 25, 2.4f, cyan);
        Ring(vh, target, 33, 1, new Color(.3f, 1, .96f, .25f));
        Line(vh, target + new Vector2(-39, 0), target + new Vector2(-19, 0), 2, cyan);
        Line(vh, target + new Vector2(19, 0), target + new Vector2(39, 0), 2, cyan);
        Line(vh, target + new Vector2(0, -39), target + new Vector2(0, -19), 2, cyan);
        Line(vh, target + new Vector2(0, 19), target + new Vector2(0, 39), 2, cyan);
        if (lift > .5f)
        {
            Vector2 start = tipPoint + (target - tipPoint).normalized * 10;
            for (int j = 0; j < 7; j++)
                Line(vh, Vector2.Lerp(start, target, j / 7f), Vector2.Lerp(start, target, (j + .4f) / 7f), 1.5f, new Color(1, .45f, .53f, .6f * lift));
        }
        Ring(vh, handPoint, 16 + 2 * Mathf.Sin(seconds * 3), 1.5f, new Color(1, .38f, .48f, .7f));
    }

    static Vector3[] MakeSphere()
    {
        const int rows = 12, columns = 16;
        var result = new List<Vector3>(rows * columns * 6);
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < columns; col++)
            {
                Vector3 a = Unit(row, col), b = Unit(row + 1, col), c = Unit(row + 1, col + 1), d = Unit(row, col + 1);
                if (row > 0) { result.Add(a); result.Add(b); result.Add(d); }
                if (row < rows - 1) { result.Add(d); result.Add(b); result.Add(c); }
            }
        return result.ToArray();
        Vector3 Unit(int row, int column)
        {
            float latitude = Mathf.PI * row / rows, longitude = 2 * Mathf.PI * column / columns;
            return new Vector3(Mathf.Sin(latitude) * Mathf.Cos(longitude), Mathf.Cos(latitude), Mathf.Sin(latitude) * Mathf.Sin(longitude));
        }
    }
    void Ball(Vector3 position, Vector3 scale, Color tint) { Ellipsoid(position, scale, Quaternion.identity, tint); }
    void Limb(Vector3 a, Vector3 b, float radius, Color tint)
    {
        Ellipsoid((a + b) * .5f, new Vector3(radius, Vector3.Distance(a, b) * .5f + radius * .4f, radius), Quaternion.FromToRotation(Vector3.up, b - a), tint);
    }
    void Ellipsoid(Vector3 position, Vector3 scale, Quaternion rotation, Color tint)
    {
        for (int i = 0; i < Sphere.Length; i += 3)
        {
            Vector3 na = Sphere[i], nb = Sphere[i + 1], nc = Sphere[i + 2];
            Vector3 a = position + rotation * Vector3.Scale(na, scale), b = position + rotation * Vector3.Scale(nb, scale), c = position + rotation * Vector3.Scale(nc, scale);
            Vector3 Normal(Vector3 n) => rotation * new Vector3(n.x / scale.x, n.y / scale.y, n.z / scale.z).normalized;
            Vector3 an = Normal(na), bn = Normal(nb), cn = Normal(nc);
            if (Vector3.Dot(an + bn + cn, Eye - (a + b + c) / 3) <= 0) continue;
            Add(a, b, c, Shade(tint, an), Shade(tint, bn), Shade(tint, cn));
        }
    }
    void Tube(Vector3 a, Vector3 b, float radius, Color tint, bool glow = false)
    {
        Quaternion rotation = Quaternion.FromToRotation(Vector3.up, b - a);
        for (int i = 0; i < 12; i++)
        {
            float angle = i * Mathf.PI / 6, next = (i + 1) * Mathf.PI / 6;
            Vector3 u = rotation * new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
            Vector3 v = rotation * new Vector3(Mathf.Cos(next), 0, Mathf.Sin(next));
            if (Vector3.Dot(u + v, Eye - (a + b) * .5f) <= 0) continue;
            Color cu = glow ? tint : Shade(tint, u), cv = glow ? tint : Shade(tint, v);
            Add(a + u * radius, b + u * radius, b + v * radius, cu, cu, cv);
            Add(a + u * radius, b + v * radius, a + v * radius, cu, cv, cv);
            Add(b, b + v * radius, b + u * radius, tint, cv, cu);
        }
    }
    void Add(Vector3 a, Vector3 b, Vector3 c, Color ca, Color cb, Color cc)
    {
        faces.Add(new Face { a = a, b = b, c = c, ca = ca, cb = cb, cc = cc, depth = Vector3.Dot((a + b + c) / 3 - Eye, Forward) });
    }
    static Color Shade(Color tint, Vector3 normal)
    {
        float diffuse = .42f + .58f * Mathf.Max(0, Vector3.Dot(normal, Light));
        float specular = Mathf.Pow(Mathf.Max(0, Vector3.Dot(normal, (Light - Forward).normalized)), 24) * .3f;
        return new Color(tint.r * diffuse + specular, tint.g * diffuse + specular, tint.b * diffuse + specular, 1);
    }
    static Vector2 Project(Vector3 point)
    {
        Vector3 p = point - Center;
        float scale = 155 * Vector3.Distance(Eye, Center) / Vector3.Dot(point - Eye, Forward);
        return new Vector2(Vector3.Dot(p, Right) * scale - 25, Vector3.Dot(p, Up) * scale - 15);
    }
    static void Ring(VertexHelper vh, Vector2 center, float radius, float width, Color tint)
    {
        for (int i = 0; i < 48; i++)
        {
            float a = i * Mathf.PI / 24, b = (i + 1) * Mathf.PI / 24;
            Line(vh, center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius, center + new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * radius, width, tint);
        }
    }
    static void Line(VertexHelper vh, Vector2 a, Vector2 b, float width, Color tint)
    {
        Vector2 n = new Vector2(a.y - b.y, b.x - a.x).normalized * width * .5f;
        int i = vh.currentVertCount;
        vh.AddVert(a - n, tint, Vector2.zero); vh.AddVert(a + n, tint, Vector2.zero);
        vh.AddVert(b + n, tint, Vector2.zero); vh.AddVert(b - n, tint, Vector2.zero);
        vh.AddTriangle(i, i + 1, i + 2); vh.AddTriangle(i, i + 2, i + 3);
    }
}
