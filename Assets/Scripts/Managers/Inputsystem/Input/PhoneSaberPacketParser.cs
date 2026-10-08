using System;
using System.Globalization;
using System.Text;

// receiver ごとに1個だけ作る。数値変換は従来と同じ TryParse に委ね、丸めを変えない。
public sealed class PhoneSaberPacketParser
{
    public const int MaximumDatagramBytes = 65536;
    readonly char[] characters = new char[MaximumDatagramBytes];

    public bool TryParse(ReadOnlySpan<byte> bytes, out bool isStick,
        out float a, out float b, out float c, out float d, out double? sentEpoch)
    {
        isStick = false;
        a = b = c = d = 0f;
        sentEpoch = null;
        int length = bytes.Length;
        if (length > characters.Length) return false;
        for (int i = 0; i < length; i++)
        {
            if (bytes[i] >= 128)
            {
                // 旧 UTF-8 decoder の置換文字・Unicode 空白も維持する。文字列は作らない。
                length = Encoding.UTF8.GetChars(bytes, characters.AsSpan());
                break;
            }
            characters[i] = (char)bytes[i];
        }
        ReadOnlySpan<char> message = characters.AsSpan(0, length);
        message = message.Trim();
        sentEpoch = PhoneSaberPacketStatistics.ReadSendEpoch(message);
        int prefixLength = message.StartsWith("ts=".AsSpan(), StringComparison.Ordinal) ? 3 :
            message.StartsWith("timestamp=".AsSpan(), StringComparison.Ordinal) ? 10 : 0;
        if (prefixLength != 0)
        {
            int separator = message.Slice(prefixLength).IndexOf(';');
            if (separator > 0) message = message.Slice(prefixLength + separator + 1);
        }

        // Split と同じく空要素も数え、2要素か4要素だけを受理する。
        Span<int> commas = stackalloc int[3];
        int count = 0;
        for (int i = 0; i < message.Length; i++)
        {
            if (message[i] != ',') continue;
            if (count == commas.Length) return false;
            commas[count++] = i;
        }
        if (count != 1 && count != 3) return false;
        if (!ParseFloat(message.Slice(0, commas[0]), out a) ||
            !ParseFloat(message.Slice(commas[0] + 1,
                (count == 1 ? message.Length : commas[1]) - commas[0] - 1), out b)) return false;
        isStick = count == 3;
        return !isStick ||
            (ParseFloat(message.Slice(commas[1] + 1, commas[2] - commas[1] - 1), out c) &&
             ParseFloat(message.Slice(commas[2] + 1), out d));
    }

    static bool ParseFloat(ReadOnlySpan<char> value, out float result)
        => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
}
