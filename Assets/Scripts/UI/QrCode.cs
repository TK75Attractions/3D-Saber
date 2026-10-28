using System;
using System.Text;

// 外部ライブラリなしの QR コード(ISO/IEC 18004、2026-10-28。スマホで印のアドレスを結果画面に出す)。
// UTF-8 のバイトモード・誤り訂正 M・型番 1〜10(213 バイトまで)。ECI は付けない(読む側は UTF-8 とみなす。アドレスは ASCII だけ)。
// 組み立ての考え方は Nayuki の QR-Code-generator(MIT)を参考にした。マスクの失点は segno と同じ数え方
// (形式情報・ダークモジュール・型番情報の場所はまだ白のまま数える。規格はここを決めていない。1:1:3:1:1 の前後は記号の外を白とみなす)。
// UnityEngine には依存しない。
public static class QrCode
{
    public const int MinVersion = 1, MaxVersion = 10;

    // 型番ごとの語の総数、誤り訂正 M のブロック数と1ブロックの訂正の語数(添字 0 は使わない)。
    static readonly int[] TotalCodewords = { 0, 26, 44, 70, 100, 134, 172, 196, 242, 292, 346 };
    static readonly int[] Blocks = { 0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };
    static readonly int[] EccPerBlock = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
    // 位置合わせの中心(行と列で同じ並び)。
    static readonly int[][] AlignCenters =
    {
        new int[0], new int[0], new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 }, new[] { 6, 34 },
        new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 },
    };

    // GF(256)(原始多項式 0x11D)の指数と対数。
    static readonly byte[] Exp = new byte[512], Log = new byte[256];

    static QrCode()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = Exp[i + 255] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if (x >= 256) x ^= 0x11D;
        }
    }

    public static int Size(int version) => 17 + 4 * version;

    // 文字列を QR にする。戻り値は [x, y](x は左から、y は上から。余白は含まない)で true が黒。
    // null・空・入らない長さ(UTF-8 で 213 バイトより長い)は null。
    public static bool[,] Encode(string text) => Encode(text, -1, out _, out _);

    // mask を 0〜7 にするとそのマスクで作る(検証用)。-1 なら失点の一番少ないもの。
    internal static bool[,] Encode(string text, int mask, out int version, out int chosenMask)
    {
        version = 0;
        chosenMask = -1;
        if (string.IsNullOrEmpty(text) || mask < -1 || mask > 7) return null;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        version = VersionFor(bytes.Length);
        if (version == 0) return null;
        return Build(bytes, version, mask, out chosenMask);
    }

    // バイトで入る一番小さい型番(入らなければ 0)。
    public static int VersionFor(int byteCount)
    {
        if (byteCount < 0) return 0;
        for (int v = MinVersion; v <= MaxVersion; v++)
            if (byteCount <= Capacity(v)) return v;
        return 0;
    }

    // バイトモードで入るバイト数(モード 4 ビットと文字数のビットを除く)。
    public static int Capacity(int version)
    {
        if (version < MinVersion || version > MaxVersion) return 0;
        return (DataCodewordCount(version) * 8 - 4 - CountBits(version)) / 8;
    }

    static int DataCodewordCount(int version) => TotalCodewords[version] - Blocks[version] * EccPerBlock[version];
    static int CountBits(int version) => version <= 9 ? 8 : 16;

    static bool[,] Build(byte[] bytes, int version, int mask, out int chosenMask)
    {
        int size = Size(version);
        var dark = new bool[size, size];
        var function = new bool[size, size];
        DrawFunctionPatterns(dark, function, version);
        PlaceCodewords(dark, function, Interleave(DataCodewords(bytes, version), version));
        chosenMask = mask;
        if (chosenMask < 0)
        {
            int best = int.MaxValue;
            for (int m = 0; m < 8; m++)
            {
                ApplyMask(dark, function, m);
                int score = Penalty(dark);
                if (score < best) { best = score; chosenMask = m; }
                ApplyMask(dark, function, m); // XOR なのでもう一度かけると戻る
            }
        }
        ApplyMask(dark, function, chosenMask);
        DrawFormat(dark, chosenMask);
        DrawVersion(dark, version);
        return dark;
    }

    // モード(0100)・文字数・データ・終端(0 を4つまで)・8ビットそろえ・埋め草(0xEC と 0x11 を交互)。
    static byte[] DataCodewords(byte[] bytes, int version)
    {
        var result = new byte[DataCodewordCount(version)];
        int capacityBits = result.Length * 8, length = 0;
        void Put(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--, length++)
                if (((value >> i) & 1) != 0) result[length >> 3] |= (byte)(0x80 >> (length & 7));
        }
        Put(0b0100, 4);
        Put(bytes.Length, CountBits(version));
        foreach (byte b in bytes) Put(b, 8);
        length += Math.Min(4, capacityBits - length);
        length = (length + 7) / 8 * 8; // すでに語の境目なら 0 は足さない(規格 7.4.10)
        for (bool first = true; length < capacityBits; first = !first) Put(first ? 0xEC : 0x11, 8);
        return result;
    }

    // ブロックに分けて訂正の語を足し、規格の順に並べる(データの短いブロックが先。i 番目の語をブロックの順に)。
    static byte[] Interleave(byte[] data, int version)
    {
        int blocks = Blocks[version], ecc = EccPerBlock[version], total = TotalCodewords[version];
        int shortBlocks = blocks - total % blocks;
        int shortData = total / blocks - ecc;
        byte[] generator = Generator(ecc);
        var parts = new byte[blocks][];
        var checks = new byte[blocks][];
        for (int b = 0, at = 0; b < blocks; b++)
        {
            parts[b] = new byte[shortData + (b < shortBlocks ? 0 : 1)];
            Array.Copy(data, at, parts[b], 0, parts[b].Length);
            at += parts[b].Length;
            checks[b] = Remainder(parts[b], generator);
        }
        var result = new byte[total];
        int k = 0;
        for (int i = 0; i <= shortData; i++)
            for (int b = 0; b < blocks; b++)
                if (i < parts[b].Length) result[k++] = parts[b][i];
        for (int i = 0; i < ecc; i++)
            for (int b = 0; b < blocks; b++)
                result[k++] = checks[b][i];
        return result;
    }

    // リードソロモンの訂正の語(検証用)。
    internal static byte[] ReedSolomon(byte[] data, int eccCount) => Remainder(data, Generator(eccCount));

    static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    // 生成多項式 (x - α^0)(x - α^1)…(x - α^(n-1))。次数の高い順で、先頭は 1。
    static byte[] Generator(int degree)
    {
        var g = new byte[degree + 1];
        g[0] = 1;
        for (int i = 0; i < degree; i++)
            for (int j = i + 1; j > 0; j--)
                g[j] ^= Mul(g[j - 1], Exp[i]);
        return g;
    }

    // data(x)·x^n を生成多項式で割った余り。
    static byte[] Remainder(byte[] data, byte[] generator)
    {
        int n = generator.Length - 1;
        var rem = new byte[n];
        foreach (byte d in data)
        {
            byte factor = (byte)(d ^ rem[0]);
            Array.Copy(rem, 1, rem, 0, n - 1);
            rem[n - 1] = 0;
            for (int j = 0; j < n; j++) rem[j] ^= Mul(generator[j + 1], factor);
        }
        return rem;
    }

    static void Set(bool[,] dark, bool[,] function, int x, int y, bool on)
    {
        dark[x, y] = on;
        function[x, y] = true;
    }

    // タイミング・ファインダーと分離帯・位置合わせを描き、形式情報・ダークモジュール・型番情報の場所を取っておく(まだ白)。
    static void DrawFunctionPatterns(bool[,] dark, bool[,] function, int version)
    {
        int size = dark.GetLength(0);
        for (int i = 0; i < size; i++)
        {
            Set(dark, function, 6, i, i % 2 == 0);
            Set(dark, function, i, 6, i % 2 == 0);
        }
        // 中心からの距離(チェビシェフ)が 0・1・3 なら黒、2 と分離帯の 4 は白。
        foreach (var (cx, cy) in new[] { (3, 3), (size - 4, 3), (3, size - 4) })
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= size || y >= size) continue;
                    int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    Set(dark, function, x, y, d != 2 && d != 4);
                }
        int[] centers = AlignCenters[version];
        int last = centers.Length - 1;
        for (int i = 0; i <= last; i++)
            for (int j = 0; j <= last; j++)
            {
                // ファインダーと重なる3隅には置かない。
                if ((i == 0 && j == 0) || (i == 0 && j == last) || (i == last && j == 0)) continue;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        Set(dark, function, centers[i] + dx, centers[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
        for (int i = 0; i <= 8; i++)
        {
            function[8, i] = true;
            function[i, 8] = true;
        }
        for (int i = 0; i < 8; i++)
        {
            function[size - 1 - i, 8] = true;
            function[8, size - 1 - i] = true;
        }
        if (version >= 7)
            for (int i = 0; i < 18; i++)
            {
                function[size - 11 + i % 3, i / 3] = true;
                function[i / 3, size - 11 + i % 3] = true;
            }
    }

    // 右下から2列ずつ、上り・下りを交互に(6 列目のタイミングは飛ばす)。余りのビット(型番 2〜6 は 7 個)は白のまま。
    static void PlaceCodewords(bool[,] dark, bool[,] function, byte[] codewords)
    {
        int size = dark.GetLength(0);
        int bit = 0, total = codewords.Length * 8;
        bool upward = true;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int k = 0; k < size; k++)
            {
                int y = upward ? size - 1 - k : k;
                for (int x = right; x >= right - 1; x--)
                {
                    if (function[x, y]) continue;
                    if (bit < total) dark[x, y] = ((codewords[bit >> 3] >> (7 - (bit & 7))) & 1) != 0;
                    bit++;
                }
            }
            upward = !upward;
        }
        if (bit < total || bit - total > 7)
            throw new InvalidOperationException("QR: データの場所が合いません(" + bit + " / " + total + ")");
    }

    // マスクの式(x は列、y は行)。
    static bool MaskHit(int mask, int x, int y)
    {
        switch (mask)
        {
            case 0: return (x + y) % 2 == 0;
            case 1: return y % 2 == 0;
            case 2: return x % 3 == 0;
            case 3: return (x + y) % 3 == 0;
            case 4: return (x / 3 + y / 2) % 2 == 0;
            case 5: return x * y % 2 + x * y % 3 == 0;
            case 6: return (x * y % 2 + x * y % 3) % 2 == 0;
            default: return ((x + y) % 2 + x * y % 3) % 2 == 0;
        }
    }

    static void ApplyMask(bool[,] dark, bool[,] function, int mask)
    {
        int size = dark.GetLength(0);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (!function[x, y] && MaskHit(mask, x, y)) dark[x, y] = !dark[x, y];
    }

    // 失点(規格 7.8.3): 同じ色が5つ以上続く・同じ色の 2×2・1:1:3:1:1 の前か後ろに白4つ・黒の割合。
    internal static int Penalty(bool[,] dark)
    {
        int size = dark.GetLength(0), score = 0, darkCount = 0;
        var line = new bool[size];
        for (int i = 0; i < size; i++)
        {
            for (int k = 0; k < size; k++) line[k] = dark[k, i];
            score += LineScore(line);
            for (int k = 0; k < size; k++) line[k] = dark[i, k];
            score += LineScore(line);
        }
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool on = dark[x, y];
                if (on) darkCount++;
                if (x + 1 < size && y + 1 < size && dark[x + 1, y] == on && dark[x, y + 1] == on && dark[x + 1, y + 1] == on)
                    score += 3;
            }
        int total = size * size;
        // 50% から 5% 離れるごとに 10。
        score += Math.Abs(darkCount * 20 - total * 10) / total * 10;
        return score;
    }

    static int LineScore(bool[] line)
    {
        int n = line.Length, score = 0, run = 1;
        for (int k = 1; k <= n; k++)
        {
            if (k < n && line[k] == line[k - 1]) { run++; continue; }
            if (run >= 5) score += run - 2; // 3 + (続いた数 - 5)
            run = 1;
        }
        // 数えた模様と重なる次の模様は数えない(segno と同じ)。
        for (int k = 0; k + 7 <= n;)
        {
            if (!FinderLike(line, k)) { k++; continue; }
            if (Light(line, k - 4, k) || Light(line, k + 7, k + 11)) { score += 40; k += 7; }
            else k += 4;
        }
        return score;
    }

    static bool FinderLike(bool[] line, int at)
        => line[at] && !line[at + 1] && line[at + 2] && line[at + 3] && line[at + 4] && !line[at + 5] && line[at + 6];

    // [from, to) が白か(記号の外は白)。
    static bool Light(bool[] line, int from, int to)
    {
        for (int k = Math.Max(from, 0); k < Math.Min(to, line.Length); k++)
            if (line[k]) return false;
        return true;
    }

    // value(valueBits ビット)·x^checkBits を generator で割った余りを後ろに付ける(BCH)。
    static int WithBch(int value, int valueBits, int checkBits, int generator)
    {
        int rem = value << checkBits;
        for (int bit = valueBits + checkBits - 1; bit >= checkBits; bit--)
            if (((rem >> bit) & 1) != 0) rem ^= generator << (bit - checkBits);
        return (value << checkBits) | rem;
    }

    // 形式情報 15 ビット: 誤り訂正 M(00)とマスク 3 ビットに BCH(15,5)、0x5412 を XOR。
    internal static int FormatBits(int mask) => WithBch(mask & 7, 5, 10, 0x537) ^ 0x5412;

    // 型番情報 18 ビット: 型番 6 ビットに BCH(18,6)。
    internal static int VersionBits(int version) => WithBch(version & 63, 6, 12, 0x1F25);

    // 左上: 0〜7 は 8 列目を上から(6 行目は飛ばす)、8〜14 は 8 行目を右から(6 列目は飛ばす)。
    // 右上と左下: 0〜7 は 8 行目を右端から、8〜14 は 8 列目の下から 7 マス。ダークモジュールは (8, size-8)。
    static void DrawFormat(bool[,] dark, int mask)
    {
        int size = dark.GetLength(0), bits = FormatBits(mask);
        for (int i = 0; i < 15; i++)
        {
            bool on = ((bits >> i) & 1) != 0;
            if (i < 8)
            {
                dark[8, i < 6 ? i : i + 1] = on;
                dark[size - 1 - i, 8] = on;
            }
            else
            {
                dark[i == 8 ? 7 : 14 - i, 8] = on;
                dark[8, size - 15 + i] = on;
            }
        }
        dark[8, size - 8] = true;
    }

    // 型番 7 以上: 右上(3 列 × 6 行)と左下(6 列 × 3 行)に同じ 18 ビット。
    static void DrawVersion(bool[,] dark, int version)
    {
        if (version < 7) return;
        int size = dark.GetLength(0), bits = VersionBits(version);
        for (int i = 0; i < 18; i++)
        {
            bool on = ((bits >> i) & 1) != 0;
            dark[size - 11 + i % 3, i / 3] = on;
            dark[i / 3, size - 11 + i % 3] = on;
        }
    }
}
