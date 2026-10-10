using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Saber.ChartEditor
{
    /// <summary>
    /// stage.json(曲ごとの表示名・ふりがな・アーティスト・試聴の開始位置・サビの区間・公開)の読み書き。
    /// 触っていない項目と書き方(字下げ・区間を1行で書くか)は読み込んだときのまま保つ。
    /// </summary>
    public sealed class SaberStageInfo
    {
        public sealed class Section
        {
            internal readonly SaberJsonObject Node;
            internal Section(SaberJsonObject node) { Node = node; }

            public string Label
            {
                get => SaberJson.TryGetString(Node.Get("label"), out string value) ? value : string.Empty;
                set
                {
                    // 同じ名前なら元の綴り(エスケープなど)を保つ。
                    if (SaberJson.TryGetString(Node.Get("label"), out string old) && old == (value ?? string.Empty)) return;
                    Node.Set("label", SaberJson.String(value ?? string.Empty));
                }
            }

            public double StartSeconds
            {
                get => SaberJson.TryGetNumber(Node.Get("startSeconds"), out double value) ? value : 0;
                set => SetNumber("startSeconds", value);
            }

            public double EndSeconds
            {
                get => SaberJson.TryGetNumber(Node.Get("endSeconds"), out double value) ? value : 0;
                set => SetNumber("endSeconds", value);
            }

            public double Intensity
            {
                get => SaberJson.TryGetNumber(Node.Get("intensity"), out double value) ? value : 1;
                set => SetNumber("intensity", value);
            }

            private void SetNumber(string key, double value)
            {
                // 値が同じなら元の綴り(0.80 など)を保つ。
                if (SaberJson.TryGetNumber(Node.Get(key), out double old) && Math.Abs(old - value) < 1e-9) return;
                Node.Set(key, SaberJson.Number(Math.Round(value, 3)));
            }
        }

        private readonly SaberJsonObject root;
        private readonly string indent;
        private readonly int inlineDepth;
        private readonly string newline;
        private readonly bool trailingNewline;

        public bool Exists { get; }
        public string OriginalText { get; }

        private SaberStageInfo(SaberJsonObject root, bool exists, string originalText, string indent, int inlineDepth,
            string newline, bool trailingNewline)
        {
            this.root = root;
            Exists = exists;
            OriginalText = originalText;
            this.indent = indent;
            this.inlineDepth = inlineDepth;
            this.newline = newline;
            this.trailingNewline = trailingNewline;
        }

        public static SaberStageInfo Load(string path, string songId)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return Parse(File.ReadAllText(path, Encoding.UTF8));
            return CreateNew(songId);
        }

        public static SaberStageInfo Parse(string text)
        {
            string source = (text ?? string.Empty).TrimStart('\uFEFF');
            if (!(SaberJson.Parse(source) is SaberJsonObject parsed))
                throw new FormatException("stage.json のいちばん外側がオブジェクトではありません。");
            Match first = Regex.Match(source, "\\{\\s*?\\r?\\n([ \\t]+)\"");
            string detectedIndent = first.Success ? first.Groups[1].Value : "  ";
            bool inlineSections = Regex.IsMatch(source, "\"sections\"\\s*:\\s*\\[\\s*\\{[^\\n]*\"startSeconds\"");
            string detectedNewline = source.Contains("\r\n") ? "\r\n" : "\n";
            return new SaberStageInfo(parsed, true, text, detectedIndent, inlineSections ? 2 : int.MaxValue, detectedNewline,
                source.EndsWith("\n"));
        }

        /// <summary>まだ stage.json が無い曲の新しい中身。保存するまで書かない。</summary>
        public static SaberStageInfo CreateNew(string songId)
        {
            var created = new SaberJsonObject();
            created.Set("schemaVersion", new SaberJsonValue("1"));
            created.Set("displayName", SaberJson.String(songId ?? string.Empty));
            created.Set("timeOrigin", SaberJson.String("audio-file-start"));
            created.Set("previewStartSeconds", new SaberJsonValue("0"));
            created.Set("sections", new SaberJsonArray());
            return new SaberStageInfo(created, false, null, "  ", 2, "\n", true);
        }

        public string DisplayName
        {
            get => SaberJson.TryGetString(root.Get("displayName"), out string value) ? value : string.Empty;
            set => SetString("displayName", value, keepWhenEmpty: true);
        }

        public string Reading
        {
            get => SaberJson.TryGetString(root.Get("displayReading"), out string value) ? value : string.Empty;
            set => SetString("displayReading", value, keepWhenEmpty: false, before: "timeOrigin");
        }

        public string Artist
        {
            get => SaberJson.TryGetString(root.Get("artist"), out string value) ? value : string.Empty;
            set => SetString("artist", value, keepWhenEmpty: root.Has("artist"), before: "timeOrigin");
        }

        /// <summary>選曲に出すか。出す(既定)ときは項目を書かない。</summary>
        public bool Listed
        {
            get => !SaberJson.TryGetBool(root.Get("listed"), out bool value) || value;
            set
            {
                if (value) root.Remove("listed");
                else root.SetBefore("listed", SaberJson.Bool(false), "audioSha256");
            }
        }

        public double PreviewStartSeconds
        {
            get => SaberJson.TryGetNumber(root.Get("previewStartSeconds"), out double value) ? value : -1;
            set
            {
                if (SaberJson.TryGetNumber(root.Get("previewStartSeconds"), out double old) && Math.Abs(old - value) < 1e-9) return;
                root.SetBefore("previewStartSeconds", SaberJson.Number(Math.Round(Math.Max(0, value), 3)), "sections");
            }
        }

        public bool HasAudioHash => root.Has("audioSha256");

        public IReadOnlyList<Section> Sections =>
            SectionArray().Items.OfType<SaberJsonObject>().Select(node => new Section(node)).ToList();

        public Section AddSection(string label, double start, double end)
        {
            var node = new SaberJsonObject();
            node.Set("label", SaberJson.String(label ?? string.Empty));
            node.Set("startSeconds", SaberJson.Number(Math.Round(Math.Max(0, start), 3)));
            node.Set("endSeconds", SaberJson.Number(Math.Round(Math.Max(start, end), 3)));
            node.Set("fadeInSeconds", new SaberJsonValue("1"));
            node.Set("fadeOutSeconds", new SaberJsonValue("2"));
            node.Set("intensity", new SaberJsonValue("0.8"));
            SectionArray().Items.Add(node);
            return new Section(node);
        }

        public void RemoveSection(int index)
        {
            List<SaberJsonNode> items = SectionArray().Items;
            var objects = items.OfType<SaberJsonObject>().ToList();
            if (index < 0 || index >= objects.Count) return;
            items.Remove(objects[index]);
        }

        // 本編は区間を開始の順に読む前提なので、保存の前に並べる。
        public void SortSections()
        {
            List<SaberJsonNode> items = SectionArray().Items;
            var sorted = items.OrderBy(item => item is SaberJsonObject node && SaberJson.TryGetNumber(node.Get("startSeconds"), out double start)
                ? start : double.MaxValue).ToList();
            items.Clear();
            items.AddRange(sorted);
        }

        /// <summary>音源の照合用の SHA-256。まだ持っていない新しい stage.json にだけ付ける。</summary>
        public void SetAudioHashIfMissing(string audioPath)
        {
            if (root.Has("audioSha256") || string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath)) return;
            using (var sha = SHA256.Create())
            {
                string hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(audioPath))).Replace("-", string.Empty)
                    .ToLowerInvariant();
                root.SetBefore("audioSha256", SaberJson.String(hash), "sections");
            }
        }

        public string Write()
        {
            string text = SaberJson.WritePretty(root, indent, inlineDepth);
            if (newline != "\n") text = text.Replace("\n", newline);
            return trailingNewline ? text + newline : text;
        }

        public bool IsDirty => !Exists || Write() != OriginalText;

        private SaberJsonArray SectionArray()
        {
            if (root.Get("sections") is SaberJsonArray array) return array;
            var created = new SaberJsonArray();
            root.Set("sections", created);
            return created;
        }

        private void SetString(string key, string value, bool keepWhenEmpty, string before = null)
        {
            string text = (value ?? string.Empty).Trim();
            if (SaberJson.TryGetString(root.Get(key), out string old) && old == text) return;
            if (text.Length == 0 && !keepWhenEmpty)
            {
                root.Remove(key);
                return;
            }
            if (before != null) root.SetBefore(key, SaberJson.String(text), before);
            else root.Set(key, SaberJson.String(text));
        }

        public static string FormatSeconds(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
