using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// 操作名と許可値を HTTP と UI で共用。任意のスマホコマンド文字列は受け付けない。
public static class PhoneSaberControlPanelActions
{
    static readonly Dictionary<string, string[]> choices = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        { "calibration-start", new[] { "" } }, { "calibration-advance", new[] { "" } },
        { "calibration-cancel", new[] { "" } }, { "calibration-reset", new[] { "" } },
        { "calibration-blue", new[] { "0", "1" } }, { "calibration-enabled", new[] { "0", "1" } },
        { "prediction", new[] { "auto", "0", "20", "40", "60" } },
        { "display-sync", new[] { "0", "1" } }, { "frame-cap", new[] { "0", "1", "2" } },
        { "judge-mode", new[] { "0", "1" } }, { "judge-adjust", new[] { "-10", "10" } }, { "judge-centre", new[] { "" } },
        { "filter", new[] { "0", "1", "2" } }, { "stabilizer", new[] { "0", "1" } },
        { "phone-ignore", new[] { "red", "blue", "both" } }, { "phone-clear-ignore", new[] { "" } },
        { "phone-learn-area", new[] { "" } }, { "phone-clear-area", new[] { "" } },
        { "phone-exposure", new[] { "0", "-1", "-2" } },
        { "phone-track-lock", new[] { "" } }, { "phone-clip", new[] { "" } },
        { "pc-ignore", new[] { "red", "blue" } }, { "pc-clear-ignore", new[] { "" } },
        { "clocks-synchronized", new[] { "0", "1" } }
    };

    public static IEnumerable<string> Names => choices.Keys;
    public static bool IsValid(string name, string value) => name != null && choices.TryGetValue(name, out var values) && Array.IndexOf(values, value) >= 0;
    public static bool Dispatch(string name, string value, IReadOnlyDictionary<string, Action<string>> handlers)
    {
        if (!IsValid(name, value) || !handlers.TryGetValue(name, out var handler)) return false;
        handler(value);
        return true;
    }

    // キーの判断を UI・ブラウザ・Unity 入力から分離する。
    public static bool ShouldOpenBrowser(bool shift, bool serverRunning) => !shift && serverRunning;
}

// main thread で一度組み立てた同じ表示を、IMGUI と JSON の両方で読む。
public sealed class PhoneSaberControlPanelState
{
    public sealed class Item
    {
        public string Text, Name, Value;
        public bool Enabled, Selected, Warning;
    }
    public sealed class Section
    {
        public string Title;
        public readonly List<Item> Items = new List<Item>();
    }
    public readonly List<Section> Sections = new List<Section>();
    Section current;
    public void BeginSection(string title) { current = new Section { Title = title }; Sections.Add(current); }
    public void Label(string text, bool warning = false) => current.Items.Add(new Item { Text = text ?? "", Warning = warning });
    public void Button(string text, string name, string value = "", bool enabled = true, bool selected = false)
    {
        if (!PhoneSaberControlPanelActions.IsValid(name, value)) throw new ArgumentException("Unknown control panel action: " + name);
        current.Items.Add(new Item { Text = text, Name = name, Value = value, Enabled = enabled, Selected = selected });
    }
    public void Toggle(string text, bool selected, string name, bool enabled = true) => Button(text, name, selected ? "0" : "1", enabled, selected);
    public string ToJson()
    {
        var json = new StringBuilder("{\"sections\":[");
        foreach (var section in Sections)
        {
            if (json[json.Length - 1] != '[') json.Append(',');
            json.Append("{\"title\":").Append(Quote(section.Title)).Append(",\"items\":[");
            foreach (var item in section.Items)
            {
                if (json[json.Length - 1] != '[') json.Append(',');
                json.Append("{\"text\":").Append(Quote(item.Text)).Append(",\"name\":").Append(Quote(item.Name))
                    .Append(",\"value\":").Append(Quote(item.Value)).Append(",\"enabled\":").Append(item.Enabled ? "true" : "false")
                    .Append(",\"selected\":").Append(item.Selected ? "true" : "false").Append(",\"warning\":").Append(item.Warning ? "true" : "false").Append('}');
            }
            json.Append("]}");
        }
        return json.Append("]}").ToString();
    }
    public static string Quote(string value)
    {
        if (value == null) return "null";
        var result = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (c < 32 || char.IsSurrogate(c) || c == '\u2028' || c == '\u2029') result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else result.Append(c);
                    break;
            }
        }
        return result.Append('"').ToString();
    }
}
