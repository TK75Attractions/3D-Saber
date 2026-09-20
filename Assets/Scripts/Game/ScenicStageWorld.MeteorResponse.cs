using System.Collections.Generic;
using UnityEngine;

public sealed partial class ScenicStageWorld
{
    public const float MeteorResponseLifetime = 1.10f;
    private const float MeteorTravel = .20f;
    private readonly MeteorResponse[] meteorResponses = new MeteorResponse[4];
    private double meteorClock;
    private bool meteorHasClock;

    private sealed class MeteorResponse
    {
        public Mesh mesh;
        public Vector3 anchor;
        public Vector3[] original, working, offsets;
        public double started;
        public float opening;
        public bool active;
    }

    public bool MeteorResponsesReady => Theme == StageTheme.AstralOrbit && MeteorReady(meteorResponses[0]) &&
        MeteorReady(meteorResponses[1]) && MeteorReady(meteorResponses[2]) && MeteorReady(meteorResponses[3]);
    public int ActiveMeteorResponseCount { get; private set; }
    public int MeteorLaneMask { get; private set; }
    public float MeteorOpening(int lane) => lane >= 0 && lane < 4 && meteorResponses[lane] != null ? meteorResponses[lane].opening : 0;
    public Vector3 MeteorAnchor(int lane) => lane >= 0 && lane < 4 && meteorResponses[lane] != null ? meteorResponses[lane].anchor : Vector3.zero;

    private static bool MeteorReady(MeteorResponse response) => response != null && response.mesh != null &&
        response.original != null && response.working != null && response.offsets != null &&
        response.original.Length == 198 && response.working.Length == 198 && response.offsets.Length == 198 && response.mesh.vertexCount == 198;

    // 元のMoving()を通して所有・常時運動を維持し、同じMeshの中だけを三つの塊へ分ける。
    private void BuildRespondingMeteor(int side, int index, Material material)
    {
        int lane = side < 0 ? index - 1 : 4 - index;
        var seed = new StageGeometry();
        seed.Rock(Vector3.zero, new Vector3(1, .72f, 1.2f), 7, 4);
        Vector3 anchor = new Vector3(side * (8.1f + index * .9f), floor + .9f + (index % 3) * .8f, 3 + index * 8);
        Transform root = Moving("OrbitingRock" + side + "-" + index, seed, material, anchor,
            new Vector3(.15f, .28f, .15f), new Vector3(3 + index, 6 - index, 2), .28f, index);
        Mesh mesh = root.GetComponent<MeshFilter>().sharedMesh;
        var vertices = new List<Vector3>(198);
        var normals = new List<Vector3>(198);
        var offsets = new List<Vector3>(198);
        var indices = new List<int>(198);
        for (int piece = 0; piece < 3; piece++)
        {
            int start = piece * 2, end = piece == 2 ? 7 : start + 2;
            float middle = Mathf.PI * 2 * (start + end) * .5f / 7;
            Vector3 offset = new Vector3(Mathf.Cos(middle), 0, Mathf.Sin(middle)) * MeteorTravel;
            // 元Rockと同じ外側の三角面。停止時に元の低多角形の輪郭を変えない。
            for (int row = 0; row < 4; row++)
                for (int col = start; col < end; col++)
                {
                    Vector3 a = MeteorPoint(row, col), b = MeteorPoint(row, col + 1);
                    Vector3 c = MeteorPoint(row + 1, col), d = MeteorPoint(row + 1, col + 1);
                    if (row != 0) MeteorTriangle(a, b, c, Vector3.zero, offset, vertices, normals, offsets, indices);
                    if (row != 3) MeteorTriangle(b, d, c, Vector3.zero, offset, vertices, normals, offsets, indices);
                }
            // 離れた時に内部が裏抜けしないよう、各扇区の両断面を閉じる。
            for (int edge = 0; edge < 2; edge++)
            {
                int col = edge == 0 ? start : end;
                float angle = Mathf.PI * 2 * col / 7;
                Vector3 outward = new Vector3(Mathf.Sin(angle), 0, -Mathf.Cos(angle)) * (edge == 0 ? 1 : -1);
                for (int row = 0; row < 4; row++)
                    MeteorTriangle(Vector3.zero, MeteorPoint(row, col), MeteorPoint(row + 1, col),
                        outward, offset, vertices, normals, offsets, indices);
            }
        }
        var response = new MeteorResponse { mesh = mesh, anchor = anchor, original = vertices.ToArray(),
            working = vertices.ToArray(), offsets = offsets.ToArray() };
        mesh.Clear(); mesh.MarkDynamic(); mesh.SetVertices(response.original); mesh.SetNormals(normals); mesh.SetTriangles(indices, 0);
        mesh.RecalculateBounds();
        Bounds bounds = mesh.bounds; bounds.Expand(MeteorTravel * 2); mesh.bounds = bounds;
        meteorResponses[lane] = response;
    }

    // StageGeometry.Rock(7,4)と同じ頂点式。7列目を0列目へ戻して継ぎ目も共有する。
    private static Vector3 MeteorPoint(int row, int col)
    {
        col %= 7;
        float latitude = Mathf.PI * row / 4, longitude = Mathf.PI * 2 * col / 7;
        float ripple = 1 + .075f * Mathf.Sin(col * 4.17f + row * 2.61f);
        Vector3 p = new Vector3(Mathf.Sin(latitude) * Mathf.Cos(longitude), Mathf.Cos(latitude), Mathf.Sin(latitude) * Mathf.Sin(longitude));
        return Vector3.Scale(p * ripple, new Vector3(1, .72f, 1.2f));
    }

    private static void MeteorTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 outward, Vector3 offset,
        List<Vector3> vertices, List<Vector3> normals, List<Vector3> offsets, List<int> indices)
    {
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0) { Vector3 swap = b; b = c; c = swap; }
        Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
        int start = vertices.Count;
        vertices.Add(a); vertices.Add(b); vertices.Add(c);
        for (int i = 0; i < 3; i++) { normals.Add(normal); offsets.Add(offset); indices.Add(start + i); }
    }

    public void OnMeteorPerfect(int lane)
    {
        if (!MeteorResponsesReady || !isActiveAndEnabled || lane < 0 || lane >= 4) return;
        MeteorResponse response = meteorResponses[lane];
        if (response.active) return;
        response.active = true; response.started = meteorClock;
        CountMeteorResponses();
    }

    public void TickMeteorResponses(double songSeconds)
    {
        if (!MeteorResponsesReady || !isActiveAndEnabled) return;
        if (double.IsNaN(songSeconds) || double.IsInfinity(songSeconds)) return;
        songSeconds = System.Math.Max(0, songSeconds);
        if (meteorHasClock && songSeconds < meteorClock) ClearMeteorResponses();
        // 判定が最初の背景Tickより先に来た場合は予約し、最初の有効時計を開始時刻にする。
        // 同じ0秒の再描画は停止なので、明示Clearや実際の巻戻しと区別して保持する。
        if (!meteorHasClock)
            for (int lane = 0; lane < 4; lane++)
                if (meteorResponses[lane].active) meteorResponses[lane].started = songSeconds;
        meteorClock = songSeconds; meteorHasClock = true;
        float gain = DisplaySettings.ReducedEffects ? .3f : 1;
        for (int lane = 0; lane < 4; lane++)
        {
            MeteorResponse response = meteorResponses[lane];
            float age = response.active ? (float)(songSeconds - response.started) : -1;
            if (age < 0 || age >= MeteorResponseLifetime) response.active = false;
            float opening = response.active ? EvaluateMeteorOpening(age) * gain : 0;
            SetMeteorOpening(response, opening);
        }
        CountMeteorResponses();
    }

    public static float EvaluateMeteorOpening(float age)
    {
        if (float.IsNaN(age) || float.IsInfinity(age) || age < 0 || age >= MeteorResponseLifetime) return 0;
        if (age < .24f) return Mathf.SmoothStep(0, 1, age / .24f);
        if (age <= .42f) return 1;
        return 1 - Mathf.SmoothStep(0, 1, (age - .42f) / .68f);
    }

    private static void SetMeteorOpening(MeteorResponse response, float opening)
    {
        if (response.opening == opening || response.mesh == null) return;
        response.opening = opening;
        for (int i = 0; i < response.working.Length; i++)
            response.working[i] = response.original[i] + response.offsets[i] * opening;
        // 平行移動だけなので元の面法線・UV・indicesは更新しない。
        response.mesh.SetVertices(response.working);
    }

    public void ClearMeteorResponses()
    {
        meteorClock = 0; meteorHasClock = false;
        for (int lane = 0; lane < 4; lane++)
        {
            MeteorResponse response = meteorResponses[lane];
            if (response == null) continue;
            response.active = false; response.started = 0;
            SetMeteorOpening(response, 0);
        }
        ActiveMeteorResponseCount = 0; MeteorLaneMask = 0;
    }

    private void CountMeteorResponses()
    {
        ActiveMeteorResponseCount = 0; MeteorLaneMask = 0;
        for (int lane = 0; lane < 4; lane++)
            if (meteorResponses[lane] != null && meteorResponses[lane].active)
            { ActiveMeteorResponseCount++; MeteorLaneMask |= 1 << lane; }
    }
}
