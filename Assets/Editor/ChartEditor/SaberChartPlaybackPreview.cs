using System;
using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Saber.ChartEditor
{
    // 編集中の譜面を専用のPreviewSceneで描く。本編シーン・判定・保存データには接続しない。
    public sealed class SaberChartPlaybackPreview : IDisposable
    {
        private sealed class Entry
        {
            public SaberChartNote snapshot;
            public CuttableNote note;
            public TextMeshPro label;
            public Vector3 scale;
            public readonly HashSet<Object> resources = new HashSet<Object>();
            // 「Perfect で切る」の部品。切る時刻になって初めて作り、巻き戻したら隠して使い回す。
            public SlicePieceDecay[] slices;
            public SlicePieceDecay[] debris;
            public readonly List<Transform> cracks = new List<Transform>();
            public readonly List<Material> crackMaterials = new List<Material>();
            public Vector3 swing;
            public int seed;
        }

        private readonly PreviewRenderUtility renderer;
        private readonly GameObject root;
        private readonly FloorRenderer floor;
        private readonly NoteSpawner motion;
        private readonly GameplayCutFeedback cutFeedback;
        private readonly Dictionary<SaberChartNote, Entry> entries = new Dictionary<SaberChartNote, Entry>();
        private readonly HashSet<SaberChartNote> visible = new HashSet<SaberChartNote>();
        private readonly List<SaberChartNote> expired = new List<SaberChartNote>();
        private readonly List<SimultaneousNoteLink> links = new List<SimultaneousNoteLink>();
        private readonly List<GameplayCutFeedback.StaticBurst> bursts = new List<GameplayCutFeedback.StaticBurst>();
        private readonly HashSet<Object> stageResources = new HashSet<Object>();

        private bool projectorMode;
        private bool disposed;
        private bool labelWarned;

        // 本編の視点(Game.unity の useSlightTopDownView と GamePlayManager の既定値)。
        public static readonly Vector3 GameCameraPosition = new Vector3(0f, 2.35f, -7f);
        public static readonly Vector3 GameCameraRotation = new Vector3(12f, 0f, 0f);
        // これまでのプレビューの視点(少し低い位置から正面寄り)。
        public static readonly Vector3 FlatCameraPosition = new Vector3(0f, 1.6f, -7f);
        public static readonly Vector3 FlatCameraRotation = new Vector3(6f, 0f, 0f);
        // 刃の太さ 0.32 + ノーツの当たり 0.88(本編の既定値)。この距離まで刃が近づけば切れる。
        public const float GameHitRange = 1.2f;
        private const float HitRingSeconds = .25f;
        // 「Perfect で切る」: 判定時刻ちょうどに中心を切ったときの本編の見た目(2片・細片・ひび・光の輪)を、
        // 切ってからの経過秒だけから作る(巻き戻しても、飛ばしても同じ絵になる)。
        // 振りの速さ(m/s)は破片の飛び方にだけ使う。本編の判定(SaberCutJudge)は使わない。
        public const float SwingSpeed = 6f;
        // 本編は毎フレーム SlicePieceDecay.Step で進めるので、同じ細かさで刻んで同じ軌道にする。
        private const float PieceStepSeconds = 1f / 60f;

        public bool UseGameCamera { get; set; } = true;
        public bool ShowLandingFrame { get; set; } = true;
        public bool ShowBarLines { get; set; } = true;
        public bool ShowHitRadius { get; set; }
        // 判定時刻ちょうど(Perfect)で切って見せる。オフなら判定時刻のあと 0.12 秒で縮んで消える従来の表示。
        public bool ShowPerfectCuts { get; set; } = true;

        private readonly GameObject guideRoot;
        private readonly Transform[] frameBars = new Transform[4];
        private readonly List<GameObject> barLines = new List<GameObject>();
        private readonly List<LineRenderer> hitRings = new List<LineRenderer>();
        private Material frameMaterial;
        private Material barMaterial;
        private Material ringMaterial;

        public GameObject WorldRoot => root;
        public int VisibleNoteCount => entries.Count;
        // いま2片で描いている(切ったあとの)ノーツの数。
        public int VisibleCutCount { get; private set; }
        public double DisplayedSongTime { get; private set; }
        public int VisibleBarLineCount { get; private set; }
        public int VisibleHitRingCount { get; private set; }
        public Vector3 CameraPosition => renderer.camera.transform.position;

        public SaberChartPlaybackPreview()
        {
            renderer = new PreviewRenderUtility();
            try
            {
                renderer.camera.transform.SetPositionAndRotation(new Vector3(0, 1.6f, -7), Quaternion.Euler(6, 0, 0));
                renderer.camera.fieldOfView = 60;
                renderer.camera.nearClipPlane = .1f;
                renderer.camera.farClipPlane = 100;
                renderer.camera.clearFlags = CameraClearFlags.SolidColor;
                renderer.camera.backgroundColor = new Color(.018f, .032f, .048f);
                renderer.camera.allowHDR = false;
                renderer.ambientColor = new Color(.28f, .32f, .4f);
                renderer.lights[0].intensity = 1.2f;
                renderer.lights[0].transform.rotation = Quaternion.Euler(40, -25, 0);
                renderer.lights[1].intensity = .5f;
                root = new GameObject("ChartEditorPreviewWorld");
                renderer.AddSingleGO(root);
                floor = root.AddComponent<FloorRenderer>();
                floor.useSongThemeOnPlay = false;
                floor.Build(StageTheme.ObsidianRelay);
                CollectGeometry(root, stageResources);
                motion = root.AddComponent<NoteSpawner>();
                motion.enabled = false;
                // 本編の切断の演出(光の輪・火花・光片)。切ってからの経過秒を渡して描くので、本編の時計は使わない。
                cutFeedback = GameplayCutFeedback.Create(motion);
                cutFeedback.ProtectDisabledPreviewNotes = true;
                CollectGeometry(cutFeedback.gameObject, stageResources);
                projectorMode = DisplaySettings.ProjectorMode;
                guideRoot = new GameObject("PreviewGuides");
                guideRoot.transform.SetParent(root.transform, false);
                frameMaterial = GuideMaterial(new Color(.16f, .88f, .95f));
                barMaterial = GuideMaterial(new Color(.55f, .62f, .7f));
                ringMaterial = GuideMaterial(new Color(1f, .82f, .3f));
                for (int i = 0; i < frameBars.Length; i++) frameBars[i] = GuideBar("LandingFrame", frameMaterial).transform;
                ApplyCamera();
                HideHierarchy(root);
            }
            catch
            {
                renderer.Cleanup();
                throw;
            }
        }

        // カーソルは既に音声サンプル位置から判定調整を引いた値。ここで再加算しない。
        // 全状態を時刻から求めるので、停止・巻き戻し・途中へのジャンプも同じ結果になる。
        // ノーツが飛んでくる秒数を固定する(null ならこのPCの設定)。プレイレビューは、本番が1プレイごとに戻す既定値で見せる。
        public float? ApproachTimeOverride { get; set; }

        public void Tick(SaberChartDocument document, double songTime)
        {
            if (disposed || document == null || double.IsNaN(songTime) || double.IsInfinity(songTime)) return;
            DisplayedSongTime = songTime;
            bool rebuildLinks = false;
            bool modeChanged = projectorMode != DisplaySettings.ProjectorMode;
            projectorMode = DisplaySettings.ProjectorMode;
            motion.approachTime = Mathf.Clamp(ApproachTimeOverride ?? GameSession.NoteApproachTime, .5f, 5f);
            // 切ったあと2片が飛んでいる間(本編の破片の寿命)はノーツを残す。切る演出を使わないときは従来の 0.12 秒で消す。
            double tail = ShowPerfectCuts ? CuttableNote.FlowPieceLife : .12;
            visible.Clear();
            bursts.Clear();
            int cutting = 0;
            foreach (var data in document.notes)
            {
                if (data == null) continue;
                double hit = data.time / 1000.0 + document.offsetMs / 1000.0;
                double elapsed = songTime - hit;
                double linger = data.count > 1 ? data.lengthMs > 0 ? data.lengthMs / 1000.0 : (data.count - 1) * motion.secondsPerLongCut : 0;
                if (elapsed < -motion.approachTime || elapsed >= linger + tail) continue;
                visible.Add(data);
                if (entries.TryGetValue(data, out var entry) && (modeChanged || !SameAppearance(data, entry.snapshot)))
                {
                    DestroyEntry(entry);
                    entries.Remove(data);
                    entry = null;
                }
                if (entry == null)
                {
                    entry = CreateEntry(data, (float)linger);
                    entries.Add(data, entry);
                    rebuildLinks = true;
                }
                if (Math.Abs(entry.note.HitTime - hit) > .000001) rebuildLinks = true;
                entry.note.HitTime = hit;
                entry.note.transform.localPosition = new Vector3(data.x * document.coordScale, data.y * document.coordScale,
                    motion.ComputeNoteZ(entry.note, -elapsed, motion.Speed));
                int count = Math.Max(1, data.count);
                int remaining = elapsed < 0 ? count : count <= 1 ? 0 :
                    Math.Max(0, count - 1 - (int)Math.Floor((elapsed + .000001) / Math.Max(.001, linger / (count - 1))));
                entry.note.RemainingCuts = remaining;
                if (ShowPerfectCuts && elapsed >= linger)
                {
                    // 最後の1振り(タップは判定時刻、ロングは滞留の終わり)からは本体を隠し、本編と同じ2片を飛ばす。
                    cutting++;
                    ShowCut(entry, (float)(elapsed - linger), linger);
                }
                else
                {
                    HideCut(entry);
                    if (!entry.note.gameObject.activeSelf) entry.note.gameObject.SetActive(true);
                    float shrink = !ShowPerfectCuts && elapsed > linger ? Mathf.Clamp01(1 - (float)((elapsed - linger) / .12)) : 1;
                    entry.note.transform.localScale = entry.scale * Mathf.Max(.001f, shrink);
                    // ロングの途中の振り: 切った回数ぶん、本編と同じ場所にひびを残す。
                    UpdateCracks(entry, ShowPerfectCuts && elapsed >= 0 ? count - remaining : 0, count, elapsed, linger);
                }
                if (entry.label != null)
                {
                    entry.label.transform.position = entry.note.transform.position + LongNoteCountStyle.WorldOffset;
                    entry.label.text = remaining.ToString();
                    entry.label.gameObject.SetActive(remaining > 0);
                    // PreviewSceneは通常の描画ループ外なので、数字も同じフレームで更新する。
                    if (remaining > 0) RefreshLabel(entry);
                }
            }
            expired.Clear();
            foreach (var pair in entries) if (!visible.Contains(pair.Key)) expired.Add(pair.Key);
            foreach (var key in expired) { DestroyEntry(entries[key]); entries.Remove(key); rebuildLinks = true; }
            if (rebuildLinks) RebuildLinks();
            foreach (var link in links) link.Refresh();
            VisibleCutCount = cutting;
            cutFeedback.SetProtectionTime(songTime);
            cutFeedback.DrawBursts(bursts);
            floor.Tick(songTime, 0);
            ApplyCamera();
            UpdateLandingFrame(document.coordScale);
            UpdateBarLines(document, songTime);
            UpdateHitRings(document, songTime);
        }

        private void ApplyCamera()
        {
            renderer.camera.transform.SetPositionAndRotation(
                UseGameCamera ? GameCameraPosition : FlatCameraPosition,
                Quaternion.Euler(UseGameCamera ? GameCameraRotation : FlatCameraRotation));
        }

        // 判定面(z=0)に、推奨の XY 範囲(±2.5 × ±1.5)の枠を描く。ノーツが着く場所の目安。
        private void UpdateLandingFrame(float coordScale)
        {
            float scale = coordScale > 0f ? coordScale : 1f;
            float halfWidth = SaberChartUtility.DefaultXMax * scale, halfHeight = SaberChartUtility.DefaultYMax * scale;
            const float thickness = .025f;
            Vector3[] positions =
            {
                new Vector3(0f, halfHeight, 0f), new Vector3(0f, -halfHeight, 0f),
                new Vector3(-halfWidth, 0f, 0f), new Vector3(halfWidth, 0f, 0f),
            };
            Vector3[] scales =
            {
                new Vector3(halfWidth * 2f + thickness, thickness, thickness), new Vector3(halfWidth * 2f + thickness, thickness, thickness),
                new Vector3(thickness, halfHeight * 2f, thickness), new Vector3(thickness, halfHeight * 2f, thickness),
            };
            for (int i = 0; i < frameBars.Length; i++)
            {
                frameBars[i].gameObject.SetActive(ShowLandingFrame);
                frameBars[i].localPosition = positions[i];
                frameBars[i].localScale = scales[i];
            }
        }

        // 本編の小節線と同じ時刻(ファイルの BPM・原点・OFFSET・拍子)で、ノーツと同じ速さで流す。
        private void UpdateBarLines(SaberChartDocument document, double songTime)
        {
            int used = 0;
            if (ShowBarLines && document.bpm > 0f)
            {
                var clock = new SaberChartGrid(document.bpm, document.beatZeroMs, document.tempoMap);
                double offset = document.offsetMs / 1000.0;
                double from = clock.BeatAt((float)((songTime - .3 - offset) * 1000.0));
                double through = clock.BeatAt((float)((songTime + motion.approachTime - offset) * 1000.0));
                float scale = document.coordScale > 0f ? document.coordScale : 1f;
                float width = SaberChartUtility.DefaultXMax * scale * 2f;
                float floorY = -SaberChartUtility.DefaultYMax * scale;
                var meter = new ChartMeterMap(document.timeSignatures);
                foreach (double beat in meter.BarStarts(Math.Max(0, from), Math.Max(0, through)))
                {
                    double time = clock.TimeAt((float)beat) / 1000.0 + offset;
                    double dt = time - songTime;
                    if (dt < -.3 || dt > motion.approachTime) continue;
                    if (used == barLines.Count) barLines.Add(GuideBar("BarLine", barMaterial));
                    GameObject line = barLines[used++];
                    line.SetActive(true);
                    line.transform.localPosition = new Vector3(0f, floorY, motion.judgeZ + motion.Speed * (float)dt);
                    line.transform.localScale = new Vector3(width, .02f, .05f);
                }
            }
            for (int i = used; i < barLines.Count; i++) barLines[i].SetActive(false);
            VisibleBarLineCount = used;
        }

        // 判定の前後 0.25 秒のノーツに、刃が届けば切れる範囲(半径 1.2)の円を判定面に描く。
        private void UpdateHitRings(SaberChartDocument document, double songTime)
        {
            int used = 0;
            if (ShowHitRadius)
            {
                float scale = document.coordScale > 0f ? document.coordScale : 1f;
                foreach (var data in document.notes)
                {
                    if (data == null) continue;
                    double hit = data.time / 1000.0 + document.offsetMs / 1000.0;
                    if (Math.Abs(songTime - hit) > HitRingSeconds) continue;
                    if (used == hitRings.Count) hitRings.Add(HitRing());
                    LineRenderer ring = hitRings[used++];
                    ring.gameObject.SetActive(true);
                    ring.transform.localPosition = new Vector3(data.x * scale, data.y * scale, motion.judgeZ);
                }
            }
            for (int i = used; i < hitRings.Count; i++) hitRings[i].gameObject.SetActive(false);
            VisibleHitRingCount = used;
        }

        private static Material GuideMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            return material;
        }

        private GameObject GuideBar(string name, Material material)
        {
            var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bar.name = name;
            Object.DestroyImmediate(bar.GetComponent<Collider>());
            bar.transform.SetParent(guideRoot.transform, false);
            bar.GetComponent<MeshRenderer>().sharedMaterial = material;
            HideHierarchy(bar);
            return bar;
        }

        private LineRenderer HitRing()
        {
            var go = new GameObject("HitRange");
            go.transform.SetParent(guideRoot.transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.loop = true;
            line.widthMultiplier = .025f;
            line.sharedMaterial = ringMaterial;
            const int segments = 40;
            line.positionCount = segments;
            for (int i = 0; i < segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                line.SetPosition(i, new Vector3(Mathf.Cos(angle) * GameHitRange, Mathf.Sin(angle) * GameHitRange, 0f));
            }
            HideHierarchy(go);
            return line;
        }

        public Texture Render(Rect rect)
        {
            if (disposed || rect.width < 1 || rect.height < 1) return null;
            renderer.BeginPreview(rect, GUIStyle.none);
            try
            {
                renderer.camera.aspect = rect.width / rect.height;
                cutFeedback.PrepareProtection(renderer.camera, DisplayedSongTime);
                renderer.Render(true);
            }
            catch
            {
                renderer.EndPreview();
                throw;
            }
            return renderer.EndPreview();
        }

        private Entry CreateEntry(SaberChartNote data, float linger)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "PreviewNote";
            body.SetActive(false);
            body.transform.SetParent(root.transform, false);
            Object.DestroyImmediate(body.GetComponent<Collider>());
            var note = body.AddComponent<CuttableNote>();
            note.enabled = false;
            note.IsJudgeable = false;
            note.RequiredHand = SaberHandHelper.FromColor(data.color);
            note.IsGold = string.Equals(data.color, "gold", StringComparison.OrdinalIgnoreCase);
            note.RequiredDirection = CutDirectionHelper.Parse(data.direction);
            note.RequiredCutCount = note.RemainingCuts = Math.Max(1, data.count);
            note.OverrideLingerSeconds = linger;
            var visuals = body.AddComponent<NoteVisuals>();
            visuals.inheritColorFromMainRenderer = false;
            visuals.Initialize();
            visuals.ResetForReuse();
            visuals.enabled = false;
            var entry = new Entry { snapshot = data.Clone(), note = note, swing = Swing(note) };
            // 細片の散り方の種。巻き戻しても同じ散り方になるよう、ノーツの時刻と位置から決める。
            entry.seed = data.time.GetHashCode() ^ (data.x.GetHashCode() << 7) ^ (data.y.GetHashCode() << 13) ^ data.count;
            entry.scale = new Vector3(.8f, .8f, .8f * Mathf.Clamp(1 + linger / motion.secondsPerLongCut, 1, motion.longMaxVisualZScale));
            if (projectorMode) { entry.scale.x *= ProjectorMode.NoteScale; entry.scale.y *= ProjectorMode.NoteScale; }
            body.transform.localScale = entry.scale;
            if (note.RequiredDirection != CutDirection.None)
            {
                NoteSpawner.BuildArrow(body.transform, note.RequiredDirection);
            }
            CollectGeometry(body, entry.resources);
            if (data.count > 1)
            {
                var label = new GameObject("PreviewLongCount");
                label.transform.SetParent(root.transform, false);
                entry.label = label.AddComponent<TextMeshPro>();
                HideHierarchy(label);
                StyleLabel(entry);
            }
            // 矢印などの本編描画部品が追加したColliderも、プレビューでは不要。
            foreach (var collider in body.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(collider);
            HideHierarchy(body);
            body.SetActive(true);
            cutFeedback.Track(note);
            return entry;
        }

        private static bool SameAppearance(SaberChartNote a, SaberChartNote b) =>
            a.count == b.count && a.lengthMs == b.lengthMs && a.type == b.type && a.color == b.color && a.direction == b.direction;

        // 切る向きと速さ。向きノーツは矢印の向き。向きのないノーツはその手の自然な振り下ろし
        // (右手=赤は右上から左下へ、左手=青は左上から右下へ、どちらでも=金は真下)。
        private static Vector3 Swing(CuttableNote note)
        {
            Vector2 direction = note.RequiredDirection != CutDirection.None ? CutDirectionHelper.ToVector(note.RequiredDirection)
                : note.RequiredHand == SaberHand.Right ? new Vector2(-.7071068f, -.7071068f)
                : note.RequiredHand == SaberHand.Left ? new Vector2(.7071068f, -.7071068f) : Vector2.down;
            return new Vector3(direction.x, direction.y, 0f) * SwingSpeed;
        }

        // 演出の色(本編 GameplayCutFeedback.HandleCut と同じ: ノーツの色、金は金)。
        private static Color Accent(CuttableNote note)
        {
            var visuals = note.GetComponent<NoteVisuals>();
            Color accent = visuals != null ? visuals.baseColor : UISkinPalette.NoteFlick;
            if (note.IsGold) accent = UISkinPalette.NoteGold;
            accent.a = 1f;
            return accent;
        }

        // 本編は Random.insideUnitSphere を使う。プレビューはノーツごとの種で固定し、巻き戻しても同じ散り方にする。
        private static Vector3 Jitter(System.Random random) => new Vector3(
            (float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f);

        // 切ってから t 秒後の2片(ロングは細片も)を、本編 CuttableNote.TrySpawnSlices / SpawnDebris と同じ初速で置き、
        // Perfect の光の輪を予約する。本体は隠す。
        private void ShowCut(Entry entry, float t, double linger)
        {
            CuttableNote note = entry.note;
            if (note.gameObject.activeSelf) note.gameObject.SetActive(false);
            if (entry.slices == null) BuildPieces(entry);
            if (entry.slices == null) return;
            bool alive = t < CuttableNote.FlowPieceLife;
            Vector3 origin = note.transform.localPosition;
            origin.z = motion.ComputeNoteZ(note, -linger, motion.Speed);
            var body = note.GetComponent<MeshRenderer>();
            Material source = body != null ? body.sharedMaterial : null;
            Vector3 motionDir = new Vector3(entry.swing.x, entry.swing.y, 0f).normalized;
            Vector3 cutNormal = Vector3.Cross(motionDir, Vector3.forward).normalized;
            Vector3 carry = Vector3.ClampMagnitude(entry.swing, 14f) * note.saberVelocityScale;
            Vector3 separation = cutNormal * (note.sliceSeparationImpulse * (note.RequiredCutCount > 1 ? 1.22f : 1f));
            // 本編の「流れる破片」: タップはノーツの前進の勢いを残し、ロングは判定面に留まるので勢いなし。
            Vector3 flow = note.RequiredCutCount > 1 ? Vector3.zero : new Vector3(0f, 0f, -motion.Speed);
            Color accent = Accent(note);
            Color flash = Color.Lerp(accent, Color.white, .55f);
            var random = new System.Random(entry.seed);
            for (int i = 0; i < entry.slices.Length; i++)
            {
                SlicePieceDecay piece = entry.slices[i];
                Vector3 spin = cutNormal * CuttableNote.FlowSpin + Jitter(random) * (note.pieceAngularImpulse * .25f);
                if (!alive) { piece.gameObject.SetActive(false); continue; }
                piece.transform.localPosition = origin;
                piece.transform.localRotation = Quaternion.identity;
                piece.transform.localScale = entry.scale;
                Drive(piece, source, (i == 0 ? separation : -separation) + carry, spin, .1f, flow, flash, t);
            }
            if (entry.debris != null)
            {
                // ロングの最後の1振りは細片も散る(本編 SpawnDebris)。乱数の順番を固定し、巻き戻しても同じ散り方にする。
                Vector3 normal = Vector3.Cross(motionDir, Vector3.forward);
                Vector3 debrisCarry = Vector3.ClampMagnitude(entry.swing, 14f) * .15f;
                for (int i = 0; i < entry.debris.Length; i++)
                {
                    SlicePieceDecay piece = entry.debris[i];
                    Vector3 offset = Jitter(random) * .3f;
                    Quaternion rotation = Quaternion.Euler(Jitter(random) * 180f);
                    float size = Mathf.Lerp(.06f, .14f, (float)random.NextDouble());
                    float side = (i & 1) == 0 ? 1f : -1f;
                    Vector3 fan = (motionDir * .6f + normal * (side * (.55f + (i % 3) * .3f))
                        + Vector3.forward * Mathf.Lerp(-.18f, .18f, (float)random.NextDouble())).normalized;
                    Vector3 spin = Jitter(random) * (note.pieceAngularImpulse * 1.5f);
                    if (!alive) { piece.gameObject.SetActive(false); continue; }
                    piece.transform.localPosition = origin + offset;
                    piece.transform.localRotation = rotation;
                    piece.transform.localScale = Vector3.one * size;
                    Drive(piece, source, fan * note.shatterDebrisSpeed + debrisCarry, spin, 0f, Vector3.zero, null, t);
                }
            }
            if (alive && t < GameplayCutFeedback.BurstLifetime)
            {
                // 本編 GameplayCutFeedback.HandleCut と同じ場所(切った点を正面へ少し出す)に Perfect の演出を出す。
                bursts.Add(new GameplayCutFeedback.StaticBurst
                {
                    tier = JudgmentTier.Perfect,
                    position = new Vector3(origin.x, origin.y, origin.z - entry.scale.z * .5f - .035f),
                    direction = new Vector2(entry.swing.x, entry.swing.y),
                    color = accent,
                    scale = Mathf.Clamp(Mathf.Max(Mathf.Abs(entry.scale.x), Mathf.Abs(entry.scale.y)), .6f, 1.5f),
                    release = note.RequiredCutCount > 1 ? 1.18f : 1f,
                    age = t,
                });
            }
        }

        private static void HideCut(Entry entry)
        {
            foreach (var piece in AllPieces(entry)) if (piece.gameObject.activeSelf) piece.gameObject.SetActive(false);
        }

        private static IEnumerable<SlicePieceDecay> AllPieces(Entry entry)
        {
            if (entry.slices != null) foreach (var piece in entry.slices) if (piece != null) yield return piece;
            if (entry.debris != null) foreach (var piece in entry.debris) if (piece != null) yield return piece;
        }

        // 本体のメッシュを本編 TrySpawnSlices と同じ平面で2片に分ける。Perfect は中心に当たるので、切断面はノーツの中心を通る。
        // ロングは細片(本編 SpawnDebris と同じ数)も用意する。片の材質は本体の複製で、消すときに一緒に解放する。
        private void BuildPieces(Entry entry)
        {
            CuttableNote note = entry.note;
            var filter = note.GetComponent<MeshFilter>();
            var body = note.GetComponent<MeshRenderer>();
            if (filter == null || body == null || filter.sharedMesh == null || body.sharedMaterial == null) return;
            Vector3 motionDir = new Vector3(entry.swing.x, entry.swing.y, 0f);
            if (motionDir.sqrMagnitude < .0001f) return;
            Vector3 cutNormal = Vector3.Cross(motionDir.normalized, Vector3.forward).normalized;
            Vector3 normalLocal = note.transform.InverseTransformDirection(cutNormal).normalized;
            var plane = new Plane(normalLocal, filter.sharedMesh.bounds.center);
            SlicePieceDecay first = MakePiece("PreviewSlice", null), second = MakePiece("PreviewSlice", null);
            first.GetComponent<MeshFilter>().sharedMesh = first.ReusableMesh;
            second.GetComponent<MeshFilter>().sharedMesh = second.ReusableMesh;
            if (!MeshSlicer.SliceInto(filter.sharedMesh, plane, first.ReusableMesh, second.ReusableMesh))
            {
                DiscardPiece(first);
                DiscardPiece(second);
                return;
            }
            entry.slices = new[] { first, second };
            if (note.RequiredCutCount > 1)
            {
                entry.debris = new SlicePieceDecay[Mathf.Max(0, note.shatterDebrisCount)];
                for (int i = 0; i < entry.debris.Length; i++) entry.debris[i] = MakePiece("PreviewDebris", NoteFragmentPool.CubeMesh);
            }
            foreach (var piece in AllPieces(entry))
            {
                piece.SetOwnedMaterial(new Material(body.sharedMaterial));
                CollectGeometry(piece.gameObject, entry.resources);
            }
        }

        private SlicePieceDecay MakePiece(string name, Mesh mesh)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer), typeof(SlicePieceDecay));
            go.transform.SetParent(root.transform, false);
            if (mesh != null) go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.SetActive(false);
            HideHierarchy(go);
            return go.GetComponent<SlicePieceDecay>();
        }

        private static void DiscardPiece(SlicePieceDecay piece)
        {
            Mesh mesh = piece.GetComponent<MeshFilter>().sharedMesh;
            if (mesh != null && !EditorUtility.IsPersistent(mesh)) Object.DestroyImmediate(mesh);
            Object.DestroyImmediate(piece.gameObject);
        }

        // 本編の SlicePieceDecay をそのまま使い、切った瞬間の状態から t 秒ぶん同じ刻みで進める(位置・回転・薄れ・Perfect の縁の光)。
        // 編集モードでは Update が回らないので、毎フレーム打ち上げ直して刻む。
        private static void Drive(SlicePieceDecay piece, Material source, Vector3 velocity, Vector3 spin, float drag,
            Vector3 flow, Color? flash, float t)
        {
            Material material = piece.GetComponent<MeshRenderer>().sharedMaterial;
            if (material != null && source != null)
            {
                // 前のフレームで薄くした色・発光を本体の値へ戻してから進める。
                material.CopyPropertiesFromMaterial(source);
                piece.SetOwnedMaterial(material);
            }
            piece.Launch(null, velocity, spin, false, CuttableNote.FlowPieceLife, CuttableNote.FlowFadeStart, drag);
            piece.Flow(flow * CuttableNote.FlowMomentumKeep, CuttableNote.FlowGravityScale);
            if (flash.HasValue) piece.Flash(flash.Value, GameplayCutFeedback.SliceFlashSeconds);
            // 寿命に達した片は自分を消すので、その手前で止める。
            float remaining = Mathf.Min(t, CuttableNote.FlowPieceLife - .002f);
            while (remaining > 0f)
            {
                float step = Mathf.Min(PieceStepSeconds, remaining);
                piece.Step(step);
                remaining -= step;
            }
        }

        // ロングの途中の振りで入るひび。本編 CuttableNote.AddCrack と同じ位置・向き・太さ(Perfect は中心に当たる)。
        private void UpdateCracks(Entry entry, int achieved, int count, double elapsed, double linger)
        {
            while (entry.cracks.Count < achieved)
            {
                var crack = GameObject.CreatePrimitive(PrimitiveType.Cube);
                crack.name = "Crack";
                Object.DestroyImmediate(crack.GetComponent<Collider>());
                crack.transform.SetParent(entry.note.transform, false);
                var material = CuttableNote.MakeCrackMaterial();
                crack.GetComponent<MeshRenderer>().sharedMaterial = material;
                entry.crackMaterials.Add(material);
                entry.resources.Add(material);
                CuttableNote.PlaceCrack(crack.transform, entry.cracks.Count, Vector3.zero,
                    entry.note.transform.InverseTransformDirection(entry.swing));
                HideHierarchy(crack);
                entry.cracks.Add(crack.transform);
            }
            for (int i = 0; i < entry.cracks.Count; i++)
            {
                bool shown = i < achieved;
                if (entry.cracks[i].gameObject.activeSelf != shown) entry.cracks[i].gameObject.SetActive(shown);
                if (shown)
                {
                    float progress = (i + 1f) / Mathf.Max(1, count - 1);
                    float age = (float)(elapsed - i * linger / Mathf.Max(1, count - 1));
                    CuttableNote.ApplyCrack(entry.cracks[i], entry.crackMaterials[i],
                        CuttableNote.CrackScale(i, progress), age);
                }
            }
        }

        // 残数の数字の書式。Play モードを抜けた直後はフォントの素材が壊れていることがあり(UISkinKit が作り直す)、
        // それでも数字を作れないときは数字だけを省いて、プレイ画面全体は止めない。
        private void StyleLabel(Entry entry)
        {
            try { LongNoteCountStyle.Apply(entry.label); }
            catch (Exception exception) { DropLabel(entry, exception); }
        }

        private void RefreshLabel(Entry entry)
        {
            try
            {
                if (!UISkinKit.IsFontUsable(entry.label.font)) LongNoteCountStyle.Apply(entry.label);
                entry.label.ForceMeshUpdate();
            }
            catch (Exception exception) { DropLabel(entry, exception); }
        }

        private void DropLabel(Entry entry, Exception exception)
        {
            if (!labelWarned)
            {
                labelWarned = true;
                Debug.LogWarning("プレイ画面: ロングの残数の数字を描けないので、数字だけ省きます。" + exception.Message);
            }
            if (entry.label != null) Object.DestroyImmediate(entry.label.gameObject);
            entry.label = null;
        }

        private void RebuildLinks()
        {
            ClearLinks();
            var notes = new List<Entry>(entries.Values);
            for (int i = 0; i < notes.Count; i++)
                for (int j = i + 1; j < notes.Count; j++)
                    if (Math.Abs(notes[i].note.HitTime - notes[j].note.HitTime) <= NoteSpawner.SimultaneousEpsilonSeconds)
                    {
                        var link = SimultaneousNoteLink.Create(notes[i].note, notes[j].note, root.transform);
                        if (link == null) continue;
                        link.enabled = false;
                        HideHierarchy(link.gameObject);
                        links.Add(link);
                    }
        }

        private static void HideHierarchy(GameObject parent)
        {
            foreach (var item in parent.GetComponentsInChildren<Transform>(true)) item.gameObject.hideFlags = HideFlags.HideAndDontSave;
        }

        private void DestroyEntry(Entry entry)
        {
            if (entry.note != null) cutFeedback.Untrack(entry.note);
            if (entry.note != null) Object.DestroyImmediate(entry.note.gameObject);
            if (entry.label != null) Object.DestroyImmediate(entry.label.gameObject);
            foreach (var piece in AllPieces(entry)) Object.DestroyImmediate(piece.gameObject);
            ReleaseGeometry(entry.resources);
        }

        // 編集モードでは通常のMonoBehaviourのOnDestroyが呼ばれないことがある。
        // 明示生成した描画資源だけを所有し、共有アセット・TMPのフォント素材は触らない。
        private static void CollectGeometry(GameObject owner, HashSet<Object> resources)
        {
            foreach (var part in owner.GetComponentsInChildren<Renderer>(true))
                foreach (var material in part.sharedMaterials)
                    if (material != null && !EditorUtility.IsPersistent(material)) resources.Add(material);
            foreach (var filter in owner.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh != null && (filter.sharedMesh.hideFlags & HideFlags.DontSave) != 0 && !EditorUtility.IsPersistent(filter.sharedMesh))
                    resources.Add(filter.sharedMesh);
        }

        private static void ReleaseGeometry(HashSet<Object> resources)
        {
            foreach (var resource in resources) if (resource != null) Object.DestroyImmediate(resource);
            resources.Clear();
        }

        private void ClearLinks()
        {
            foreach (var link in links)
            {
                if (link == null) continue;
                var material = link.Line != null ? link.Line.sharedMaterial : null;
                Object.DestroyImmediate(link.gameObject);
                if (material != null) Object.DestroyImmediate(material);
            }
            links.Clear();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ClearLinks();
            foreach (var entry in entries.Values) DestroyEntry(entry);
            entries.Clear();
            cutFeedback.ReleaseProtection();
            renderer.Cleanup();
            ReleaseGeometry(stageResources);
            // 目印の線や円、ひびの素材は、このプレビューだけが作って持つ。
            foreach (Material material in new[] { frameMaterial, barMaterial, ringMaterial })
                if (material != null) Object.DestroyImmediate(material);
        }
    }
}
