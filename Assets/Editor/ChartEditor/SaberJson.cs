using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Saber.ChartEditor
{
    /// <summary>
    /// 知らない項目を落とさずに JSON を読み書きする小さな木構造。
    /// JsonUtility は宣言したフィールドしか扱えないため、制作メモ(_comment)や
    /// stage.json の未知の項目を保つときだけ使う。文字列と数値は元の綴りのまま保持する。
    /// </summary>
    internal abstract class SaberJsonNode
    {
    }

    internal sealed class SaberJsonObject : SaberJsonNode
    {
        public readonly List<KeyValuePair<string, SaberJsonNode>> Members = new List<KeyValuePair<string, SaberJsonNode>>();

        public SaberJsonNode Get(string key)
        {
            foreach (var member in Members)
                if (member.Key == key) return member.Value;
            return null;
        }

        public bool Has(string key) => Get(key) != null;

        // 既存の項目は同じ位置で置き換え、無ければ末尾に足す。項目の並びを保つ。
        public void Set(string key, SaberJsonNode value)
        {
            for (int i = 0; i < Members.Count; i++)
            {
                if (Members[i].Key != key) continue;
                Members[i] = new KeyValuePair<string, SaberJsonNode>(key, value);
                return;
            }
            Members.Add(new KeyValuePair<string, SaberJsonNode>(key, value));
        }

        // 指定した項目の直前に入れる。基準が無ければ末尾に足す。
        public void SetBefore(string key, SaberJsonNode value, string beforeKey)
        {
            if (Has(key)) { Set(key, value); return; }
            for (int i = 0; i < Members.Count; i++)
            {
                if (Members[i].Key != beforeKey) continue;
                Members.Insert(i, new KeyValuePair<string, SaberJsonNode>(key, value));
                return;
            }
            Members.Add(new KeyValuePair<string, SaberJsonNode>(key, value));
        }

        public void Remove(string key) => Members.RemoveAll(member => member.Key == key);
    }

    internal sealed class SaberJsonArray : SaberJsonNode
    {
        public readonly List<SaberJsonNode> Items = new List<SaberJsonNode>();
    }

    /// <summary>文字列・数値・true/false/null。読み込んだ綴り(引用符やエスケープも)をそのまま持つ。</summary>
    internal sealed class SaberJsonValue : SaberJsonNode
    {
        public readonly string Raw;
        public SaberJsonValue(string raw) { Raw = raw; }
        public bool IsString => Raw.Length >= 2 && Raw[0] == '"';
    }

    internal static class SaberJson
    {
        public static SaberJsonNode Parse(string json)
        {
            if (json == null) throw new FormatException("JSON がありません。");
            int index = 0;
            SkipSpace(json, ref index);
            if (index < json.Length && json[index] == '﻿') { index++; SkipSpace(json, ref index); }
            SaberJsonNode node = ParseValue(json, ref index);
            SkipSpace(json, ref index);
            if (index != json.Length) throw new FormatException("JSON の後ろに余分な文字があります。");
            return node;
        }

        public static bool TryParseObject(string json, out SaberJsonObject result)
        {
            try
            {
                result = Parse(json) as SaberJsonObject;
                return result != null;
            }
            catch (FormatException)
            {
                result = null;
                return false;
            }
        }

        public static SaberJsonValue String(string value) => new SaberJsonValue(Quote(value ?? string.Empty));
        public static SaberJsonValue Number(double value) =>
            new SaberJsonValue(double.IsNaN(value) || double.IsInfinity(value) ? "0" : value.ToString("R", CultureInfo.InvariantCulture));
        public static SaberJsonValue Bool(bool value) => new SaberJsonValue(value ? "true" : "false");

        public static bool TryGetString(SaberJsonNode node, out string value)
        {
            value = null;
            if (!(node is SaberJsonValue raw) || !raw.IsString) return false;
            value = Unquote(raw.Raw);
            return true;
        }

        public static bool TryGetNumber(SaberJsonNode node, out double value)
        {
            value = 0;
            return node is SaberJsonValue raw && !raw.IsString &&
                   double.TryParse(raw.Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public static bool TryGetBool(SaberJsonNode node, out bool value)
        {
            value = false;
            if (!(node is SaberJsonValue raw)) return false;
            if (raw.Raw == "true") { value = true; return true; }
            return raw.Raw == "false";
        }

        /// <summary>1行に詰めて書く。読み込んだ綴りは変えない。</summary>
        public static string WriteCompact(SaberJsonNode node)
        {
            var builder = new StringBuilder();
            WriteCompact(node, builder);
            return builder.ToString();
        }

        /// <summary>
        /// 字下げして書く。inlineDepth 以上の深さにあるオブジェクトは1行にまとめる
        /// (stage.json の sections のように、1区間1行の書き方を保つため)。
        /// </summary>
        public static string WritePretty(SaberJsonNode node, string indent = "  ", int inlineDepth = int.MaxValue)
        {
            var builder = new StringBuilder();
            WritePretty(node, builder, indent, 0, inlineDepth);
            return builder.ToString();
        }

        public static string Quote(string value)
        {
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    default:
                        if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4"));
                        else builder.Append(c);
                        break;
                }
            }
            builder.Append('"');
            return builder.ToString();
        }

        public static string Unquote(string raw)
        {
            var builder = new StringBuilder(raw.Length);
            for (int i = 1; i < raw.Length - 1; i++)
            {
                char c = raw[i];
                if (c != '\\' || i + 1 >= raw.Length - 1) { builder.Append(c); continue; }
                char e = raw[++i];
                switch (e)
                {
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'u':
                        if (i + 4 < raw.Length &&
                            int.TryParse(raw.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                        {
                            builder.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: builder.Append(e); break;
                }
            }
            return builder.ToString();
        }

        private static SaberJsonNode ParseValue(string json, ref int index)
        {
            SkipSpace(json, ref index);
            if (index >= json.Length) throw new FormatException("JSON が途中で終わっています。");
            char c = json[index];
            if (c == '{') return ParseObject(json, ref index);
            if (c == '[') return ParseArray(json, ref index);
            if (c == '"')
            {
                int end = StringEnd(json, index);
                var value = new SaberJsonValue(json.Substring(index, end - index + 1));
                index = end + 1;
                return value;
            }
            int start = index;
            while (index < json.Length && ",}] \t\r\n".IndexOf(json[index]) < 0) index++;
            string token = json.Substring(start, index - start);
            if (token.Length == 0) throw new FormatException("JSON の値を読めません。");
            if (token != "true" && token != "false" && token != "null" &&
                !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                throw new FormatException("JSON の値を読めません: " + token);
            return new SaberJsonValue(token);
        }

        private static SaberJsonObject ParseObject(string json, ref int index)
        {
            var result = new SaberJsonObject();
            index++; // {
            SkipSpace(json, ref index);
            if (index < json.Length && json[index] == '}') { index++; return result; }
            while (true)
            {
                SkipSpace(json, ref index);
                if (index >= json.Length || json[index] != '"') throw new FormatException("JSON の項目名を読めません。");
                int end = StringEnd(json, index);
                string key = Unquote(json.Substring(index, end - index + 1));
                index = end + 1;
                SkipSpace(json, ref index);
                if (index >= json.Length || json[index] != ':') throw new FormatException("JSON の ':' がありません。");
                index++;
                result.Members.Add(new KeyValuePair<string, SaberJsonNode>(key, ParseValue(json, ref index)));
                SkipSpace(json, ref index);
                if (index >= json.Length) throw new FormatException("JSON が途中で終わっています。");
                if (json[index] == ',') { index++; continue; }
                if (json[index] == '}') { index++; return result; }
                throw new FormatException("JSON の区切りを読めません。");
            }
        }

        private static SaberJsonArray ParseArray(string json, ref int index)
        {
            var result = new SaberJsonArray();
            index++; // [
            SkipSpace(json, ref index);
            if (index < json.Length && json[index] == ']') { index++; return result; }
            while (true)
            {
                result.Items.Add(ParseValue(json, ref index));
                SkipSpace(json, ref index);
                if (index >= json.Length) throw new FormatException("JSON が途中で終わっています。");
                if (json[index] == ',') { index++; continue; }
                if (json[index] == ']') { index++; return result; }
                throw new FormatException("JSON の区切りを読めません。");
            }
        }

        private static int StringEnd(string json, int start)
        {
            for (int i = start + 1; i < json.Length; i++)
            {
                if (json[i] == '\\') { i++; continue; }
                if (json[i] == '"') return i;
            }
            throw new FormatException("JSON の文字列が閉じていません。");
        }

        private static void SkipSpace(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
        }

        private static void WriteCompact(SaberJsonNode node, StringBuilder builder)
        {
            switch (node)
            {
                case SaberJsonObject obj:
                    builder.Append('{');
                    for (int i = 0; i < obj.Members.Count; i++)
                    {
                        if (i > 0) builder.Append(", ");
                        builder.Append(Quote(obj.Members[i].Key)).Append(": ");
                        WriteCompact(obj.Members[i].Value, builder);
                    }
                    builder.Append('}');
                    break;
                case SaberJsonArray array:
                    builder.Append('[');
                    for (int i = 0; i < array.Items.Count; i++)
                    {
                        if (i > 0) builder.Append(", ");
                        WriteCompact(array.Items[i], builder);
                    }
                    builder.Append(']');
                    break;
                case SaberJsonValue value:
                    builder.Append(value.Raw);
                    break;
                default:
                    builder.Append("null");
                    break;
            }
        }

        private static void WritePretty(SaberJsonNode node, StringBuilder builder, string indent, int depth, int inlineDepth)
        {
            if (node is SaberJsonObject obj && depth < inlineDepth && obj.Members.Count > 0)
            {
                builder.Append("{\n");
                for (int i = 0; i < obj.Members.Count; i++)
                {
                    Indent(builder, indent, depth + 1);
                    builder.Append(Quote(obj.Members[i].Key)).Append(": ");
                    WritePretty(obj.Members[i].Value, builder, indent, depth + 1, inlineDepth);
                    builder.Append(i + 1 < obj.Members.Count ? ",\n" : "\n");
                }
                Indent(builder, indent, depth);
                builder.Append('}');
                return;
            }
            if (node is SaberJsonArray array && array.Items.Count > 0 && depth < inlineDepth)
            {
                builder.Append("[\n");
                for (int i = 0; i < array.Items.Count; i++)
                {
                    Indent(builder, indent, depth + 1);
                    WritePretty(array.Items[i], builder, indent, depth + 1, inlineDepth);
                    builder.Append(i + 1 < array.Items.Count ? ",\n" : "\n");
                }
                Indent(builder, indent, depth);
                builder.Append(']');
                return;
            }
            WriteCompact(node, builder);
        }

        private static void Indent(StringBuilder builder, string indent, int depth)
        {
            for (int i = 0; i < depth; i++) builder.Append(indent);
        }
    }
}
