using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Saber.ChartEditor
{
    // 再生とレビューに絞った窓。元の譜面エディターと同じ描画・音声・保存処理を使う。
    public sealed class SaberPlaybackReviewWindow : EditorWindow
    {
        [SerializeField] string song = "RushE", difficulty = "e", chartJson, diskHash, legacyHash;
        [SerializeField] float seconds, rangeA, rangeB = 1;
        [SerializeField] string instruction = "", requestJson, candidateJson, lastFolder, status = "曲を選んで「開く」を押してください。";
        [SerializeField] List<SaberReviewComment> comments = new List<SaberReviewComment>();
        [SerializeField] bool hasPoint, loop, after;
        [SerializeField] Vector2 point;
        SaberChartDocument document, candidate;
        SaberChartPlaybackPreview preview;
        AudioClip clip;
        Texture lastFrame;
        bool playing, exportPending;
        double lastTick;
        Vector2 scroll, reviewScroll;
        [SerializeField] string changeSummary;
        readonly HashSet<int> selected = new HashSet<int>();
        string[] songs = Array.Empty<string>(), difficulties = Array.Empty<string>();
        readonly Stack<string> undo = new Stack<string>();
        float Duration => clip != null ? clip.length : document == null || document.notes.Count == 0 ? 30 : Mathf.Max(30, document.notes.Max(n => (n.time + document.offsetMs + SaberChartUtility.EffectiveLongLengthMs(n)) / 1000f) + 3);
        string ChartPath => SaberChartFileStore.ChartPath(song, difficulty);
        SaberChartDocument VisibleDocument => after && candidate != null ? candidate : document;

        [MenuItem("3D Saber/プレイレビュー・AI修正", priority = 11)]
        public static void Open()
        {
            var window = GetWindow<SaberPlaybackReviewWindow>();
            window.titleContent = new GUIContent("プレイレビュー"); window.minSize = new Vector2(1000, 660); window.Show();
        }
        void OnEnable()
        {
            minSize = new Vector2(1000, 660); titleContent = new GUIContent("プレイレビュー");
            RefreshSongs();
            if (!string.IsNullOrEmpty(chartJson)) { document = SaberChartUtility.FromJson(chartJson); clip = SaberChartFileStore.LoadAudioClip(song); }
            if (!string.IsNullOrEmpty(candidateJson)) candidate = SaberChartUtility.FromJson(candidateJson);
            EditorApplication.update += Tick;
        }
        void OnDisable()
        {
            Stop(); EditorApplication.update -= Tick; preview?.Dispose(); preview = null;
        }
        void RefreshSongs()
        {
            songs = Directory.GetDirectories(SaberChartFileStore.SongsRootPath).Where(folder => new[] { "easy", "normal", "hard", "e" }.Any(d => File.Exists(Path.Combine(folder, "chart_" + d + ".json")))).Select(Path.GetFileName).OrderBy(n => n).ToArray();
            RefreshDifficulties();
        }
        void RefreshDifficulties()
        {
            var folder = SaberChartFileStore.SongFolderPath(song);
            difficulties = Directory.Exists(folder) ? new[] { "easy", "normal", "hard", "e" }.Where(d => File.Exists(SaberChartFileStore.ChartPath(song, d))).ToArray() : Array.Empty<string>();
            if (!difficulties.Contains(difficulty)) difficulty = difficulties.FirstOrDefault() ?? "normal";
        }
        void LoadChart()
        {
            if (hasUnsavedChanges && !EditorUtility.DisplayDialog("未保存の譜面", "この窓の未保存の修正を破棄して開きますか？", "破棄して開く", "戻る")) return;
            Stop();
            string raw = File.ReadAllText(ChartPath);
            document = SaberChartUtility.FromJson(raw); chartJson = SaberChartUtility.ToJson(document, false); diskHash = SaberPlaybackReviewData.Hash(raw);
            legacyHash = ReadLegacyHash(); clip = SaberChartFileStore.LoadAudioClip(song);
            seconds = rangeA = 0; rangeB = Mathf.Min(1, Duration); comments.Clear(); selected.Clear(); undo.Clear();
            candidate = null; candidateJson = requestJson = ""; hasPoint = after = hasUnsavedChanges = false;
            status = clip == null ? "音源なし：映像のみ再生できます。" : "シークして停止 → 区間やノーツを指定 → 指示を追加。";
        }
        string ReadLegacyHash()
        {
            string path = Path.Combine(SaberChartFileStore.SongFolderPath(song), "chart.json");
            return File.Exists(path) ? SaberPlaybackReviewData.Hash(File.ReadAllText(path)) : "missing";
        }
        void Stop() { if (playing) SaberChartAudioPreview.Stop(); playing = false; }
        void Play()
        {
            if (document == null || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (loop && (seconds < rangeA || seconds >= rangeB)) seconds = rangeA;
            playing = clip == null || SaberChartAudioPreview.Play(clip, seconds); lastTick = EditorApplication.timeSinceStartup;
            if (!playing) status = SaberChartAudioPreview.LastError;
        }
        void Seek(float target)
        {
            if (float.IsNaN(target) || float.IsInfinity(target)) return;
            bool resume = playing; Stop(); seconds = Mathf.Clamp(target, 0, Duration); if (resume) Play(); Repaint();
        }
        void Tick()
        {
            if (!playing) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Stop(); return; }
            double now = EditorApplication.timeSinceStartup;
            if (clip == null) seconds += (float)(now - lastTick);
            else if (SaberChartAudioPreview.TryGetPosition(clip, out float position)) seconds = position;
            else { Stop(); Repaint(); return; }
            lastTick = now;
            if (loop && seconds >= rangeB && rangeB > rangeA) { Stop(); seconds = rangeA; Play(); }
            else if (seconds >= Duration) { seconds = Duration; Stop(); }
            Repaint();
        }
        void OnGUI()
        {
            try { Draw(); }
            catch (ExitGUIException) { throw; }
            catch (Exception e) { status = e.GetBaseException().Message; }
        }
        void Draw()
        {
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), new Color(.045f, .055f, .075f));
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("PLAY REVIEW", EditorStyles.boldLabel, GUILayout.Width(108));
                // 読み込み中の譜面名と、次に開く候補を混同しないよう、曲変更は即座に確認して読み込む。
                using (new EditorGUI.DisabledScope(hasUnsavedChanges))
                {
                    int nextSong = EditorGUILayout.Popup(Math.Max(0, Array.IndexOf(songs, song)), songs, GUILayout.Width(190));
                    if (songs.Length > 0 && songs[nextSong] != song) { Stop(); song = songs[nextSong]; RefreshDifficulties(); LoadChart(); }
                    int nextDifficulty = EditorGUILayout.Popup(Math.Max(0, Array.IndexOf(difficulties, difficulty)), difficulties, GUILayout.Width(80));
                    if (difficulties.Length > 0 && difficulties[nextDifficulty] != difficulty) { difficulty = difficulties[nextDifficulty]; LoadChart(); }
                }
                if (GUILayout.Button("開く / 再読込", EditorStyles.toolbarButton, GUILayout.Width(95))) LoadChart();
                if (GUILayout.Button("依頼を再開", EditorStyles.toolbarButton, GUILayout.Width(80))) ResumeRequest();
                GUILayout.FlexibleSpace();
                GUILayout.Label(hasUnsavedChanges ? "● 未保存" : "保存済み", GUILayout.Width(76));
                using (new EditorGUI.DisabledScope(!hasUnsavedChanges)) if (GUILayout.Button("本編へ保存", EditorStyles.toolbarButton, GUILayout.Width(90))) SaveChanges();
            }
            if (document == null) { EditorGUILayout.HelpBox(status, MessageType.Info); return; }
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(position.width - 366)))
                {
                    Rect area = GUILayoutUtility.GetRect(200, (position.width - 386) * 9 / 16f, GUILayout.ExpandWidth(true));
                    DrawFrame(area);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(playing ? "一時停止" : "▶ 再生", GUILayout.Width(88))) { if (playing) Stop(); else Play(); }
                        if (GUILayout.Button("−1秒", GUILayout.Width(55))) Seek(seconds - 1);
                        if (GUILayout.Button("−1コマ", GUILayout.Width(65))) { Stop(); Seek(seconds - 1f / 60); }
                        if (GUILayout.Button("+1コマ", GUILayout.Width(65))) { Stop(); Seek(seconds + 1f / 60); }
                        if (GUILayout.Button("+1秒", GUILayout.Width(55))) Seek(seconds + 1);
                        GUILayout.FlexibleSpace();
                        EditorGUI.BeginChangeCheck(); float next = EditorGUILayout.DelayedFloatField(seconds, GUILayout.Width(82));
                        if (EditorGUI.EndChangeCheck()) Seek(next);
                        GUILayout.Label($"/ {Duration:0.000} 秒", GUILayout.Width(94));
                    }
                    EditorGUI.BeginChangeCheck(); float sought = GUILayout.HorizontalSlider(seconds, 0, Duration);
                    if (EditorGUI.EndChangeCheck()) Seek(sought);
                    DrawRange();
                    GUILayout.Label("区間内のノーツ（クリックで選択・その時刻へ移動）", EditorStyles.boldLabel);
                    using (new EditorGUI.DisabledScope(candidate != null))
                    using (var list = new EditorGUILayout.ScrollViewScope(scroll, GUILayout.MinHeight(90)))
                    {
                        scroll = list.scrollPosition;
                        foreach (int i in SaberPlaybackReviewData.NotesInRange(document, rangeA, rangeB))
                        {
                            var n = document.notes[i]; bool picked = selected.Contains(i);
                            bool next = GUILayout.Toggle(picked, $"#{i:D4}   {(n.time + document.offsetMs) / 1000f:0.000}s   {n.color,-5}  {n.type,-9}  ({n.x:0.00}, {n.y:0.00})  ×{n.count}", "Button");
                            if (next != picked) { if (next) selected.Add(i); else selected.Remove(i); Stop(); Seek(Mathf.Max(0, (n.time + document.offsetMs) / 1000f - .2f)); }
                        }
                    }
                }
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.Width(346)))
                using (var panel = new EditorGUILayout.ScrollViewScope(reviewScroll))
                { reviewScroll = panel.scrollPosition; DrawReviewPanel(); }
            }
            EditorGUILayout.HelpBox(status ?? "", MessageType.None);
        }
        void DrawFrame(Rect area)
        {
            GUI.Box(area, GUIContent.none);
            if (Event.current.type == EventType.Repaint)
            {
                preview ??= new SaberChartPlaybackPreview(); preview.Tick(VisibleDocument, seconds);
                lastFrame = preview.Render(area); if (lastFrame != null) GUI.DrawTexture(area, lastFrame, ScaleMode.StretchToFill);
            }
            GUI.Label(new Rect(area.x + 10, area.y + 8, 270, 24), $"{song} / {difficulty.ToUpperInvariant()}  {(after && candidate != null ? "変更案" : "変更前")}  {seconds:0.000}s", EditorStyles.whiteBoldLabel);
            if (hasPoint)
            {
                Vector2 p = area.position + Vector2.Scale(point, area.size);
                EditorGUI.DrawRect(new Rect(p.x - 10, p.y - 2, 20, 4), Color.yellow); EditorGUI.DrawRect(new Rect(p.x - 2, p.y - 10, 4, 20), Color.yellow);
            }
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && area.Contains(Event.current.mousePosition))
            { Stop(); point = new Vector2((Event.current.mousePosition.x - area.x) / area.width, (Event.current.mousePosition.y - area.y) / area.height); hasPoint = true; Event.current.Use(); Repaint(); }
            if (exportPending && Event.current.type == EventType.Repaint) { exportPending = false; ExportRequest(); }
        }
        void DrawRange()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                float a = EditorGUILayout.FloatField(rangeA, GUILayout.Width(74));
                float b = EditorGUILayout.FloatField(rangeB, GUILayout.Width(74));
                if (!float.IsNaN(a) && !float.IsInfinity(a) && !float.IsNaN(b) && !float.IsInfinity(b)) { rangeA = Mathf.Clamp(a, 0, Duration); rangeB = Mathf.Clamp(b, rangeA, Duration); }
                if (GUILayout.Button("今をAに", GUILayout.Width(70))) { rangeA = seconds; rangeB = Mathf.Min(Duration, Mathf.Max(rangeB, rangeA + .1f)); selected.Clear(); }
                if (GUILayout.Button("今をBに", GUILayout.Width(70))) { rangeB = seconds; rangeA = Mathf.Min(rangeA, rangeB); selected.Clear(); }
                if (GUILayout.Button("現在±1秒", GUILayout.Width(78))) { rangeA = Mathf.Max(0, seconds - 1); rangeB = Mathf.Min(Duration, seconds + 1); selected.Clear(); }
                loop = GUILayout.Toggle(loop, "A–Bループ", GUILayout.Width(105));
                if (GUILayout.Button("Aへ", GUILayout.Width(45))) Seek(rangeA);
            }
        }
        void DrawReviewPanel()
        {
            GUILayout.Label("この箇所をAIに指摘", EditorStyles.boldLabel);
            GUILayout.Label($"{rangeA:0.000}〜{rangeB:0.000}秒 / 選択 {selected.Count} ノーツ");
            GUILayout.Label(hasPoint ? "画面の指摘位置を記録します" : "映像をクリックして指摘位置を指定できます", EditorStyles.wordWrappedMiniLabel);
            if (hasPoint && GUILayout.Button("指摘位置を外す")) hasPoint = false;
            instruction = EditorGUILayout.TextArea(instruction, GUILayout.Height(86));
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(instruction) || candidate != null))
                if (GUILayout.Button("指示を追加（現在のフレームを記録）", GUILayout.Height(29))) AddComment();
            GUILayout.Space(8);
            using (var list = new EditorGUILayout.ScrollViewScope(commentScroll, GUILayout.Height(Mathf.Max(95, position.height - 520))))
            {
                commentScroll = list.scrollPosition;
                foreach (var c in comments.ToArray())
                {
                    GUILayout.Label($"{c.fromSeconds:0.000}–{c.toSeconds:0.000}s", EditorStyles.boldLabel);
                    GUILayout.Label(c.instruction, EditorStyles.wordWrappedLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("この箇所へ")) { Stop(); Seek(c.frameSeconds); rangeA = c.fromSeconds; rangeB = c.toSeconds; point = c.screenPoint; hasPoint = c.hasPoint; }
                        if (GUILayout.Button("削除", GUILayout.Width(52))) comments.Remove(c);
                    }
                }
            }
            using (new EditorGUI.DisabledScope(comments.Count == 0 || candidate != null))
                if (GUILayout.Button("AIへの依頼を書き出す", GUILayout.Height(30))) { Stop(); exportPending = true; Repaint(); }
            if (!string.IsNullOrEmpty(lastFolder))
            {
                if (GUILayout.Button("依頼文をコピー")) { EditorGUIUtility.systemCopyBuffer = File.ReadAllText(Path.Combine(lastFolder, "prompt.txt")); status = "このチャットに貼り付けてください。"; }
                if (GUILayout.Button("依頼フォルダーを開く")) EditorUtility.RevealInFinder(lastFolder);
            }
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(requestJson)))
                if (GUILayout.Button("AIの返信JSONを開く")) ImportReply();
            if (candidate != null)
            {
                after = GUILayout.Toolbar(after ? 1 : 0, new[] { "変更前", "変更案" }) == 1;
                GUILayout.Label($"{document.notes.Count} → {candidate.notes.Count} ノーツ", EditorStyles.boldLabel);
                GUILayout.Label(changeSummary ?? "", EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("この修正を適用")) ApplyCandidate();
                    if (GUILayout.Button("案を閉じる")) { candidate = null; candidateJson = ""; after = false; }
                }
            }
            using (new EditorGUI.DisabledScope(undo.Count == 0)) if (GUILayout.Button("適用を1回戻す")) { Stop(); document = SaberChartUtility.FromJson(undo.Pop()); chartJson = SaberChartUtility.ToJson(document, false); candidate = null; candidateJson = ""; hasUnsavedChanges = true; }
            GUILayout.Label("AIのチャットへ依頼を渡し、返信JSONをここで確認します。", EditorStyles.wordWrappedMiniLabel);
        }
        Vector2 commentScroll;
        void AddComment()
        {
            Stop();
            var c = new SaberReviewComment { fromSeconds = rangeA, toSeconds = rangeB, frameSeconds = seconds, hasPoint = hasPoint, screenPoint = point,
                instruction = instruction.Trim(), noteIndices = selected.Where(i => i < document.notes.Count).OrderBy(i => i).ToArray() };
            comments.Add(c); instruction = "";
            // 次の再描画で常にこの時刻から画像を作り直すため、古い描画フレームは使わない。
            status = "指示を追加しました。書き出し時に各指示の時刻を撮影します。";
        }
        public void ExportRequest()
        {
            if (comments.Count == 0) return;
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "PlaybackReviews", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6)));
            var request = SaberPlaybackReviewData.Request(document, song, difficulty, "Assets/StreamingAssets/Songs/" + song + "/chart_" + difficulty + ".json", comments);
            foreach (var comment in request.comments)
                if (!Guid.TryParseExact(comment.id, "N", out _) || float.IsNaN(comment.frameSeconds) || float.IsInfinity(comment.frameSeconds))
                    throw new InvalidOperationException("指示のIDまたは撮影時刻が不正です。");
            request.sourceDiskHash = diskHash; request.sourceLegacyHash = legacyHash;
            SaberPlaybackReviewData.Export(folder, request);
            foreach (var comment in request.comments)
            {
                preview ??= new SaberChartPlaybackPreview(); preview.Tick(document, comment.frameSeconds);
                var frame = preview.Render(new Rect(0, 0, 1280, 720));
                File.WriteAllBytes(Path.Combine(folder, "frame-" + comment.id + ".png"), Capture(frame, comment));
            }
            requestJson = JsonUtility.ToJson(request); lastFolder = folder;
            EditorGUIUtility.systemCopyBuffer = SaberPlaybackReviewData.Prompt(request, folder);
            status = "依頼を書き出し、依頼文をコピーしました。このチャットへ貼り付けられます。";
        }
        static byte[] Capture(Texture frame, SaberReviewComment comment)
        {
            var render = RenderTexture.GetTemporary(1280, 720, 0); var old = RenderTexture.active;
            var pixels = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            try
            {
                Graphics.Blit(frame, render); RenderTexture.active = render; pixels.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); pixels.Apply();
                if (comment.hasPoint)
                {
                    int x = Mathf.RoundToInt(comment.screenPoint.x * 1279), y = Mathf.RoundToInt((1 - comment.screenPoint.y) * 719);
                    for (int i = -13; i <= 13; i++) for (int t = -2; t <= 2; t++)
                    { pixels.SetPixel(Mathf.Clamp(x + i, 0, 1279), Mathf.Clamp(y + t, 0, 719), Color.yellow); pixels.SetPixel(Mathf.Clamp(x + t, 0, 1279), Mathf.Clamp(y + i, 0, 719), Color.yellow); }
                    pixels.Apply();
                }
                return pixels.EncodeToPNG();
            }
            finally { RenderTexture.active = old; RenderTexture.ReleaseTemporary(render); DestroyImmediate(pixels); }
        }
        void ImportReply()
        {
            string path = EditorUtility.OpenFilePanel("AIの返信JSON", lastFolder, "json"); if (string.IsNullOrEmpty(path)) return;
            LoadReply(File.ReadAllText(path));
        }
        public void LoadReply(string json)
        {
            Stop(); var request = JsonUtility.FromJson<SaberReviewRequest>(requestJson); var patch = JsonUtility.FromJson<SaberReviewPatch>(json);
            candidate = SaberPlaybackReviewData.Apply(request, document, patch, Duration); candidateJson = SaberChartUtility.ToJson(candidate, false); after = true;
            changeSummary = string.Join("\n", patch.operations.Take(8).Select(op => op.action == "delete" ? $"#{op.index}: 削除" : op.action == "add" ? $"追加 {Describe(op.note)}" : $"#{op.index}: {Describe(document.notes[op.index])}\n → {Describe(op.note)}"));
            if (patch.operations.Count > 8) changeSummary += $"\nほか {patch.operations.Count - 8} 件";
            status = $"変更案 {patch.operations.Count}件: {patch.explanation}";
        }
        static string Describe(SaberChartNote note) => $"{note.time:0.###}ms ({note.x:0.##},{note.y:0.##}) {note.color}/{note.type} ×{note.count}";
        void ApplyCandidate()
        {
            if (candidate == null) return;
            Stop(); undo.Push(chartJson); document = candidate; chartJson = SaberChartUtility.ToJson(document, false);
            candidate = null; candidateJson = ""; after = false; hasUnsavedChanges = true; selected.Clear();
            comments.Clear(); requestJson = "";
            status = "修正を適用しました。「本編へ保存」でファイルへ反映します。";
        }
        public override void SaveChanges()
        {
            try
            {
                Stop();
                if (!File.Exists(ChartPath) || SaberPlaybackReviewData.Hash(File.ReadAllText(ChartPath)) != diskHash || (difficulty == "normal" && ReadLegacyHash() != legacyHash))
                    throw new InvalidOperationException("外部で譜面が更新されています。上書きせず、再読込して新しい譜面で依頼してください。");
                SaberChartFileStore.Save(document, song, difficulty); diskHash = SaberPlaybackReviewData.Hash(File.ReadAllText(ChartPath)); legacyHash = ReadLegacyHash();
                hasUnsavedChanges = false; status = "本編へ保存しました。元ファイルはLibrary/3DSaberChartBackupsに保存されています。"; base.SaveChanges();
            }
            catch (Exception e) { status = e.Message; Debug.LogWarning("[PlaybackReview] " + status); }
        }
        void ResumeRequest()
        {
            string path = EditorUtility.OpenFilePanel("保存したrequest.jsonを選ぶ", lastFolder, "json"); if (string.IsNullOrEmpty(path)) return;
            if (hasUnsavedChanges && !EditorUtility.DisplayDialog("未保存の譜面", "未保存の修正を破棄して依頼を開きますか？", "破棄して開く", "戻る")) return;
            ResumeRequestFile(path);
        }
        public void ResumeRequestFile(string path)
        {
            var request = JsonUtility.FromJson<SaberReviewRequest>(File.ReadAllText(path));
            if (request == null || request.version != 1 || request.chart == null || request.comments == null
                || !SaberChartFileStore.IsValidSongId(request.songId, out _) || !new[] { "easy", "normal", "hard", "e" }.Contains(request.difficulty)
                || SaberPlaybackReviewData.ChartHash(request.chart) != request.baseHash) throw new InvalidOperationException("有効なレビュー依頼ではありません。");
            string source = SaberChartFileStore.ChartPath(request.songId, request.difficulty);
            if (!File.Exists(source) || SaberPlaybackReviewData.Hash(File.ReadAllText(source)) != request.sourceDiskHash)
                throw new InvalidOperationException("依頼後に本編の譜面が変わっています。新しい譜面を開いて指示を追加してください。");
            Stop(); song = request.songId; difficulty = request.difficulty; RefreshDifficulties();
            document = SaberChartUtility.Clone(request.chart); chartJson = SaberChartUtility.ToJson(document, false);
            diskHash = request.sourceDiskHash; legacyHash = request.sourceLegacyHash; clip = SaberChartFileStore.LoadAudioClip(song);
            requestJson = JsonUtility.ToJson(request); comments = request.comments; lastFolder = Path.GetDirectoryName(path);
            candidate = null; candidateJson = ""; after = false; selected.Clear(); undo.Clear();
            hasUnsavedChanges = SaberPlaybackReviewData.ChartHash(SaberChartUtility.FromJson(File.ReadAllText(source))) != request.baseHash;
            var first = comments.FirstOrDefault(); seconds = first?.frameSeconds ?? 0; rangeA = first?.fromSeconds ?? 0; rangeB = first?.toSeconds ?? 1;
            hasPoint = first?.hasPoint ?? false; point = first?.screenPoint ?? Vector2.zero;
            status = "依頼を再開しました。AIの返信JSONを開けます。";
        }
    }
}
