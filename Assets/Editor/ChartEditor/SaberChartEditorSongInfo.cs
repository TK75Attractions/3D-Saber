using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Saber.ChartEditor
{
    /// <summary>
    /// 曲情報パネル: 表示名・ふりがな・アーティスト・選曲に出すか・試聴の開始位置・表示レベル(3難易度)・
    /// サビの区間・ジャケットを、エディターの中で決めて stage.json などに保存する。新曲をコードの修正なしで出すため。
    /// </summary>
    public sealed partial class SaberChartEditorWindow
    {
        [SerializeField] private bool showSongInfo;
        private SaberStageInfo songInfo;
        private string songInfoSong;
        private readonly int[] songInfoLevels = new int[3];
        private readonly int[] songInfoLevelsLoaded = new int[3];
        private readonly bool[] songInfoLevelExists = new bool[3];
        private string coverInfo;
        private long coverStamp = -1;

        private Func<string, string, bool> confirmSongInfoDialog = (title, message) =>
            EditorUtility.DisplayDialog(title, message, "保存", "保存しない");

        private bool SongInfoDirty
        {
            get
            {
                if (songInfo == null) return false;
                if (songInfo.IsDirty) return true;
                for (int i = 0; i < DifficultyValues.Length; i++)
                    if (DifficultyValues[i] != EditingDifficulty && songInfoLevelExists[i] && songInfoLevels[i] != songInfoLevelsLoaded[i])
                        return true;
                return false;
            }
        }

        // 編集中の曲が変わったら読み直す。未保存の曲情報があれば、先に保存するかを聞く。
        private void EnsureSongInfo()
        {
            string song = EditingSongId;
            if (songInfo != null && songInfoSong == song) return;
            if (songInfo != null && SongInfoDirty && SaberChartFileStore.IsValidSongId(songInfoSong, out _))
            {
                if (confirmSongInfoDialog("曲情報が未保存です", $"{songInfoSong} の曲情報（stage.json）を保存しますか？"))
                    SaveSongInfo();
            }
            LoadSongInfo(song);
        }

        private void LoadSongInfo(string song)
        {
            songInfoSong = song;
            coverStamp = -1;
            coverInfo = null;
            if (!SaberChartFileStore.IsValidSongId(song, out _))
            {
                songInfo = null;
                return;
            }
            try
            {
                songInfo = SaberStageInfo.Load(SaberChartFileStore.StagePath(song), song);
            }
            catch (FormatException exception)
            {
                songInfo = null;
                SetStatus("stage.json を読めません: " + exception.Message);
            }
            for (int i = 0; i < DifficultyValues.Length; i++)
            {
                SaberChartDocument chart = SaberChartFileStore.TryLoadExact(song, DifficultyValues[i]);
                songInfoLevelExists[i] = chart != null;
                songInfoLevels[i] = songInfoLevelsLoaded[i] = chart?.displayLevel ?? 0;
            }
        }

        private void DrawSongInfoPanel()
        {
            EnsureSongInfo();
            showSongInfo = EditorGUILayout.Foldout(showSongInfo, SongInfoDirty ? "曲情報（stage.json）● 未保存" : "曲情報（stage.json）", true);
            if (!showSongInfo) return;
            if (songInfo == null)
            {
                GUILayout.Label("曲フォルダ名を確かめてください", smallMutedStyle);
                return;
            }
            if (!songInfo.Exists) GUILayout.Label("まだ stage.json がありません。保存すると作ります", smallMutedStyle);

            songInfo.DisplayName = EditorGUILayout.TextField(new GUIContent("表示名", "選曲・プレイ・リザルトに出る曲名"), songInfo.DisplayName);
            songInfo.Reading = EditorGUILayout.TextField(new GUIContent("ふりがな", "漢字の曲名のときだけ。選曲画面でルビになります"), songInfo.Reading);
            songInfo.Artist = EditorGUILayout.TextField("アーティスト", songInfo.Artist);
            songInfo.Listed = EditorGUILayout.ToggleLeft(new GUIContent("選曲に出す",
                "外すと本編の曲一覧に出ません（制作中や、曲の権利の確認が済んでいない曲に）"), songInfo.Listed);

            GUILayout.BeginHorizontal();
            double preview = songInfo.PreviewStartSeconds;
            float nextPreview = EditorGUILayout.DelayedFloatField(new GUIContent("試聴の開始（秒）", "選曲画面で流す位置。音源の先頭からの秒"),
                (float)Math.Max(0, preview));
            if (Math.Abs(nextPreview - Math.Max(0, preview)) > .0005 || preview < 0) songInfo.PreviewStartSeconds = nextPreview;
            if (GUILayout.Button("今の位置", EditorStyles.miniButton, GUILayout.Width(56f)))
                songInfo.PreviewStartSeconds = BeatToAudioSeconds(currentBeat);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("表示レベル", "選曲画面の難易度の数字（1〜10、0は自動）"), GUILayout.Width(64f));
            for (int i = 0; i < DifficultyValues.Length; i++)
            {
                bool editing = DifficultyValues[i] == EditingDifficulty;
                using (new EditorGUI.DisabledScope(!editing && !songInfoLevelExists[i]))
                {
                    GUILayout.Label(DifficultyLabels[i], editing ? EditorStyles.boldLabel : EditorStyles.label, GUILayout.Width(editing ? 48f : 44f));
                    int current = editing ? document.displayLevel : songInfoLevels[i];
                    int next = Mathf.Clamp(EditorGUILayout.DelayedIntField(current, GUILayout.Width(26f)), 0, 10);
                    if (next == current) continue;
                    if (editing) ApplyDisplayLevel(next);
                    else songInfoLevels[i] = next;
                }
            }
            GUILayout.EndHorizontal();

            DrawSongSections();
            DrawCoverRow();

            GUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!SongInfoDirty))
            {
                if (GUILayout.Button("曲情報を保存")) SaveSongInfo();
                if (GUILayout.Button("元に戻す", GUILayout.Width(70f))) LoadSongInfo(EditingSongId);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("編集中の難易度の表示レベルは、譜面と一緒に保存します", smallMutedStyle);
        }

        private void DrawSongSections()
        {
            var sections = songInfo.Sections;
            GUILayout.Label($"サビ・見せ場の区間（{sections.Count}）　背景の演出に使います（強さ0.65以上がサビ）", smallMutedStyle);
            for (int i = 0; i < sections.Count; i++)
            {
                SaberStageInfo.Section section = sections[i];
                GUILayout.BeginHorizontal();
                section.Label = GUILayout.TextField(section.Label, GUILayout.MinWidth(60f));
                float start = EditorGUILayout.DelayedFloatField((float)section.StartSeconds, GUILayout.Width(52f));
                float end = EditorGUILayout.DelayedFloatField((float)section.EndSeconds, GUILayout.Width(52f));
                if (Math.Abs(start - section.StartSeconds) > .0005) section.StartSeconds = Math.Max(0, start);
                if (Math.Abs(end - section.EndSeconds) > .0005) section.EndSeconds = Math.Max(section.StartSeconds, end);
                float strength = EditorGUILayout.DelayedFloatField((float)section.Intensity, GUILayout.Width(34f));
                if (Math.Abs(strength - section.Intensity) > .0005) section.Intensity = Mathf.Clamp(strength, .1f, 1f);
                bool remove = GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(20f));
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Space(8f);
                if (GUILayout.Button("始め=今", EditorStyles.miniButtonLeft)) section.StartSeconds = BeatToAudioSeconds(currentBeat);
                if (GUILayout.Button("終わり=今", EditorStyles.miniButtonRight)) section.EndSeconds = Math.Max(section.StartSeconds, BeatToAudioSeconds(currentBeat));
                if (GUILayout.Button("始めへ移動", EditorStyles.miniButton)) SeekToBeat(BeatAtTime((float)section.StartSeconds * 1000f - document.offsetMs));
                GUILayout.EndHorizontal();
                if (remove)
                {
                    songInfo.RemoveSection(i);
                    break;
                }
            }
            if (GUILayout.Button(new GUIContent("今の位置から区間を足す", "今の位置から16拍の区間を足します。名前・終わり・強さを直してください")))
            {
                double start = BeatToAudioSeconds(currentBeat);
                double end = BeatToAudioSeconds(currentBeat + 16f);
                songInfo.AddSection($"区間{sections.Count + 1}", start, end);
            }
        }

        private void DrawCoverRow()
        {
            string path = SaberChartFileStore.CoverPath(EditingSongId);
            long stamp = !string.IsNullOrEmpty(path) && File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
            if (stamp != coverStamp)
            {
                coverStamp = stamp;
                coverInfo = stamp == 0 ? "なし（選曲画面は色の板で表示）" : DescribeImage(path);
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("ジャケット", GUILayout.Width(64f));
            GUILayout.Label(coverInfo ?? string.Empty, smallMutedStyle);
            if (GUILayout.Button("画像を選ぶ…", EditorStyles.miniButton, GUILayout.Width(80f))) ChooseCoverImage();
            GUILayout.EndHorizontal();
        }

        private static string DescribeImage(string path)
        {
            var texture = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                if (!texture.LoadImage(File.ReadAllBytes(path))) return "読めない画像";
                string shape = texture.width == texture.height ? "正方形" : "正方形ではありません";
                return $"cover.png {texture.width}×{texture.height}（{shape}）";
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                return "読めない画像: " + exception.Message;
            }
            finally
            {
                DestroyImmediate(texture);
            }
        }

        private void ChooseCoverImage()
        {
            if (!SaberChartFileStore.IsValidSongId(EditingSongId, out string reason))
            {
                EditorUtility.DisplayDialog("ジャケットを取り込めません", reason, "OK");
                return;
            }
            string source = EditorUtility.OpenFilePanelWithFilters("ジャケット画像を選ぶ", string.Empty, new[] { "画像", "png,jpg,jpeg" });
            if (string.IsNullOrEmpty(source)) return;
            try
            {
                ImportCoverImage(source);
                SetStatus("ジャケットを cover.png にしました（前の画像はバックアップしました）");
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("ジャケットを取り込めません", exception.Message, "OK");
            }
        }

        // PNG・JPEG を読み、PNG で cover.png に書く。
        private void ImportCoverImage(string source)
        {
            var texture = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                if (!texture.LoadImage(File.ReadAllBytes(source)))
                    throw new InvalidOperationException("画像として読めませんでした（PNG か JPEG を選んでください）。");
                SaberChartFileStore.SaveCover(EditingSongId, texture.EncodeToPNG());
                coverStamp = -1;
            }
            finally
            {
                DestroyImmediate(texture);
            }
        }

        private void ApplyDisplayLevel(int level)
        {
            EndNoteDrag();
            string before = CurrentJson();
            document.displayLevel = Mathf.Clamp(level, 0, 10);
            if (CurrentJson() == before) return;
            history.Record(before);
            MarkChanged();
        }

        private bool SaveSongInfo()
        {
            if (songInfo == null) return false;
            if (!SaberChartFileStore.IsValidSongId(songInfoSong, out string reason))
            {
                EditorUtility.DisplayDialog("曲情報を保存できません", reason, "OK");
                return false;
            }
            try
            {
                songInfo.SortSections();
                if (!songInfo.Exists) songInfo.SetAudioHashIfMissing(SaberChartFileStore.AudioPath(songInfoSong));
                if (songInfo.IsDirty) SaberChartFileStore.SaveStage(songInfoSong, songInfo.Write());
                for (int i = 0; i < DifficultyValues.Length; i++)
                {
                    if (DifficultyValues[i] == EditingDifficulty && songInfoSong == EditingSongId) continue;
                    if (!songInfoLevelExists[i] || songInfoLevels[i] == songInfoLevelsLoaded[i]) continue;
                    SaberChartDocument chart = SaberChartFileStore.TryLoadExact(songInfoSong, DifficultyValues[i]);
                    if (chart == null) continue;
                    chart.displayLevel = songInfoLevels[i];
                    SaberChartFileStore.Save(chart, songInfoSong, DifficultyValues[i]);
                }
                // 本編の曲名の読み込みは短い間覚えているので、保存した名前がすぐ出るよう忘れさせる。
                SongDisplayInfo.ClearCache();
                overviewSectionsSong = null;
                string saved = songInfoSong;
                LoadSongInfo(saved);
                SetStatus($"{saved} の曲情報を保存しました");
                return true;
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("曲情報を保存できません", exception.Message, "OK");
                return false;
            }
        }
    }
}
