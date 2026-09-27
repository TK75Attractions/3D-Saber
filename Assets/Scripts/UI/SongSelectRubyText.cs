using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;

// 親文字の実際の文字位置に小さなTMPを重ねる。日本語の固定語と曲名に使用する。
public sealed class SongSelectRubyText : MonoBehaviour
{
    sealed class Reading { public int start, length; public TextMeshProUGUI text; }
    readonly List<Reading> readings = new List<Reading>();
    TextMeshProUGUI label;
    Vector2 lastSize;
    float lastFontSize;
    bool dirty;
    string markup;
    public void Set(string value)
    {
        if (markup == value) return;
        markup = value;
        label = GetComponent<TextMeshProUGUI>();
        foreach (var r in readings) UISkinKit.SafeDestroy(r.text.gameObject);
        readings.Clear();
        var plain = new StringBuilder(); int previous = 0;
        foreach (Match match in Regex.Matches(value, "<ruby=([^>]+)>(.*?)</ruby>"))
        {
            plain.Append(value.Substring(previous, match.Index - previous));
            var reading = new Reading { start = Regex.Replace(plain.ToString(), "<[^>]+>", "").Length, length = match.Groups[2].Value.Length };
            plain.Append(match.Groups[2].Value);
            reading.text = UISkinKit.MakeTMP(transform, "Ruby", match.Groups[1].Value, 16, label.color,
                TextAlignmentOptions.Center, Vector2.zero, new Vector2(100, 30), FontStyles.Bold, 0, UISkinKit.JapaneseFallbackFontAsset());
            reading.text.raycastTarget = false; reading.text.overflowMode = TextOverflowModes.Overflow;
            readings.Add(reading); previous = match.Index + match.Length;
        }
        plain.Append(value.Substring(previous)); label.text = plain.ToString(); dirty = true;
    }
    void LateUpdate() { Refresh(); }
    public void Refresh()
    {
        if (label == null || !label.isActiveAndEnabled) return;
        if (!dirty && lastSize == label.rectTransform.rect.size && lastFontSize == label.fontSize && !label.havePropertiesChanged) return;
        label.ForceMeshUpdate(); lastSize = label.rectTransform.rect.size; lastFontSize = label.fontSize; dirty = false;
        foreach (var r in readings)
        {
            if (r.start + r.length > label.textInfo.characterCount) continue;
            var first = label.textInfo.characterInfo[r.start]; var last = label.textInfo.characterInfo[r.start + r.length - 1];
            float size = Mathf.Max(10, first.pointSize * .32f), width = Mathf.Max(last.topRight.x - first.bottomLeft.x, r.text.text.Length * size);
            r.text.fontSize = size; r.text.color = label.color;
            r.text.rectTransform.sizeDelta = new Vector2(width + 8, size * 1.8f);
            r.text.rectTransform.anchoredPosition = new Vector2((first.bottomLeft.x + last.topRight.x) * .5f, first.ascender + size * .65f + 2);
        }
    }
}
