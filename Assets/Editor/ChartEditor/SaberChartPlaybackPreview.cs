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
        }

        private readonly PreviewRenderUtility renderer;
        private readonly GameObject root;
        private readonly FloorRenderer floor;
        private readonly NoteSpawner motion;
        private readonly Dictionary<SaberChartNote, Entry> entries = new Dictionary<SaberChartNote, Entry>();
        private readonly HashSet<SaberChartNote> visible = new HashSet<SaberChartNote>();
        private readonly List<SaberChartNote> expired = new List<SaberChartNote>();
        private readonly List<SimultaneousNoteLink> links = new List<SimultaneousNoteLink>();
        private readonly HashSet<Object> stageResources = new HashSet<Object>();
        private bool projectorMode;
        private bool disposed;

        public GameObject WorldRoot => root;
        public int VisibleNoteCount => entries.Count;
        public double DisplayedSongTime { get; private set; }

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
                floor.randomizeOnPlay = false;
                floor.Build(StageTheme.ObsidianRelay);
                CollectGeometry(root, stageResources);
                motion = root.AddComponent<NoteSpawner>();
                motion.enabled = false;
                projectorMode = DisplaySettings.ProjectorMode;
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
        public void Tick(SaberChartDocument document, double songTime)
        {
            if (disposed || document == null || double.IsNaN(songTime) || double.IsInfinity(songTime)) return;
            DisplayedSongTime = songTime;
            bool rebuildLinks = false;
            bool modeChanged = projectorMode != DisplaySettings.ProjectorMode;
            projectorMode = DisplaySettings.ProjectorMode;
            motion.approachTime = Mathf.Clamp(GameSession.NoteApproachTime, .5f, 5f);
            visible.Clear();
            foreach (var data in document.notes)
            {
                if (data == null) continue;
                double hit = data.time / 1000.0 + document.offsetMs / 1000.0;
                double elapsed = songTime - hit;
                double linger = data.count > 1 ? data.lengthMs > 0 ? data.lengthMs / 1000.0 : (data.count - 1) * motion.secondsPerLongCut : 0;
                if (elapsed < -motion.approachTime || elapsed >= linger + .12) continue;
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
                int remaining = elapsed < 0 ? Math.Max(1, data.count) : data.count <= 1 ? 0 :
                    Math.Max(0, data.count - 1 - (int)Math.Floor((elapsed + .000001) / Math.Max(.001, linger / (data.count - 1))));
                entry.note.RemainingCuts = remaining;
                float shrink = elapsed > linger ? Mathf.Clamp01(1 - (float)((elapsed - linger) / .12)) : 1;
                entry.note.transform.localScale = entry.scale * Mathf.Max(.001f, shrink);
                if (entry.label != null)
                {
                    entry.label.transform.position = entry.note.transform.position + LongNoteCountStyle.WorldOffset;
                    entry.label.text = remaining.ToString();
                    entry.label.gameObject.SetActive(remaining > 0);
                    // PreviewSceneは通常の描画ループ外なので、数字も同じフレームで更新する。
                    if (remaining > 0) entry.label.ForceMeshUpdate();
                }
            }
            expired.Clear();
            foreach (var pair in entries) if (!visible.Contains(pair.Key)) expired.Add(pair.Key);
            foreach (var key in expired) { DestroyEntry(entries[key]); entries.Remove(key); rebuildLinks = true; }
            if (rebuildLinks) RebuildLinks();
            foreach (var link in links) link.Refresh();
            floor.Tick(songTime, 0);
        }

        public Texture Render(Rect rect)
        {
            if (disposed || rect.width < 1 || rect.height < 1) return null;
            renderer.BeginPreview(rect, GUIStyle.none);
            try
            {
                renderer.camera.aspect = rect.width / rect.height;
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
            var entry = new Entry { snapshot = data.Clone(), note = note };
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
                LongNoteCountStyle.Apply(entry.label);
                HideHierarchy(label);
            }
            // 矢印などの本編描画部品が追加したColliderも、プレビューでは不要。
            foreach (var collider in body.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(collider);
            HideHierarchy(body);
            body.SetActive(true);
            return entry;
        }

        private static bool SameAppearance(SaberChartNote a, SaberChartNote b) =>
            a.count == b.count && a.lengthMs == b.lengthMs && a.type == b.type && a.color == b.color && a.direction == b.direction;

        private void RebuildLinks()
        {
            ClearLinks();
            var notes = new List<Entry>(entries.Values);
            for (int i = 0; i < notes.Count; i++)
                for (int j = i + 1; j < notes.Count; j++)
                    if (Math.Abs(notes[i].note.HitTime - notes[j].note.HitTime) <= NoteSpawner.SimultaneousEpsilonSeconds)
                    {
                        var link = SimultaneousNoteLink.Create(notes[i].note, notes[j].note, root.transform);
                        link.enabled = false;
                        HideHierarchy(link.gameObject);
                        links.Add(link);
                    }
        }

        private static void HideHierarchy(GameObject parent)
        {
            foreach (var item in parent.GetComponentsInChildren<Transform>(true)) item.gameObject.hideFlags = HideFlags.HideAndDontSave;
        }

        private static void DestroyEntry(Entry entry)
        {
            if (entry.note != null) Object.DestroyImmediate(entry.note.gameObject);
            if (entry.label != null) Object.DestroyImmediate(entry.label.gameObject);
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
            renderer.Cleanup();
            ReleaseGeometry(stageResources);
        }
    }
}
