using System.Text;
using NUnit.Framework;

// QR コード(QrCode、2026-10-28。スマホで印のアドレスを結果画面に出す)。型番の選び方・大きさ・ファインダーなどの形・
// 形式情報と型番情報が規格の値・空と長すぎ・リードソロモン・既知の正解との一致。
// 既知の正解は segno 1.6.6 で作った(バイトモード・M・自動のマスク)。segno は終端のあとがちょうど 8 の倍数でも 0x00 を1語足す
// (規格 7.4.10 は語の境目で終わらないときだけ 0 を足す)ので、そこだけ規格どおりに直して作った。zxing-cpp でも元の文字列に戻る。
public class QrCodeTests
{
    const string Url = "http://192.168.137.1:5020/m?t=0123456789abcdef0123456789abcdef";
    // 型番ごとの上限(バイト。規格の表 7 の M・バイト)。
    static readonly int[] Capacity = { 0, 14, 26, 42, 62, 84, 106, 122, 152, 180, 213 };
    // 誤り訂正 M の形式情報(規格の表 C.1、マスク 0〜7)。
    static readonly int[] FormatM = { 0x5412, 0x5125, 0x5E7C, 0x5B4B, 0x45F9, 0x40CE, 0x4F97, 0x4AA0 };
    // 型番情報(規格の表 D.1、型番 7〜10)。
    static readonly int[] VersionInfo = { 0x07C94, 0x085BC, 0x09A99, 0x0A4D3 };

    sealed class Symbol
    {
        public readonly string Text, Rows;
        public readonly int Version, Mask;
        public Symbol(string text, int version, int mask, string rows) { Text = text; Version = version; Mask = mask; Rows = rows; }
    }

    // 行を上から、1行は左から 4 モジュールずつ 16 進(右の端は 0 で埋める)。
    static readonly Symbol[] Known =
    {
        new Symbol("Hello, QR!", 1, 7,
            "FE33F8 820A08 BA4AE8 BA02E8 BA3AE8 82C208 FEABF8 002000 " +
            "969D00 BD1418 E6B368 A4AB58 AF1680 00A120 FE5AF0 82A518 " +
            "BA2D90 BAE7F8 BA36A8 827800 FEE150"),
        new Symbol("スマホで印", 2, 3,
            "FEFF3F8 82CCA08 BA68AE8 BAD32E8 BA7AAE8 8265A08 FEAABF8 " +
            "00A1800 B72DA58 CD571F0 1ADC558 2DB0FC8 A65B968 211B410 " +
            "5FD50C8 B493E70 132BF90 00C28A8 FE87AB0 82B68A8 BA67FF8 " +
            "BA80028 BAC3630 827E4C0 FE90C78"),
        new Symbol(Url, 4, 2,
            "FE2217BF8 826346A08 BADBB72E8 BA8F7AAE8 BA8C912E8 829B0CA08 " +
            "FEAAAABF8 008B8C000 BE25E83E0 A8EA95F78 5667CA2A0 1D80848E8 " +
            "66A8688C0 BC3493738 52D3A20D0 F5F22DC20 4E85E81C8 E8E257F78 " +
            "D7B5A4AA0 B417BD4F0 D35679CC0 BC1A97368 BF236CBB0 9D9637C28 " +
            "BB8760FD8 00A0D68B8 FE61ADAA0 828A9E8A0 BA9C48FC0 BAFEB61F8 " +
            "BA83A0F60 8269361A0 FEE969DD0"),
        new Symbol(Url + "&song=ManicSonic&difficulty=Hard&mark=12&from=phone", 7, 5,
            "FE48CC2FCBF8 829458A8D208 BAD1E46ED2E8 BAC6F48F9AE8 BA343FCFBAE8 " +
            "826B28A1C208 FEAAAAAAABF8 008DF89CA800 82F93FD10670 21CFF6A73A30 " +
            "8E0CE169BBF0 FCCDBC4CADA0 E779746AA290 7DDE591A4968 4A06F9ED6F10 " +
            "C88EAB654660 E612F0A07640 282C8942C8E8 5BE6D7C8C878 CDBAB15E89A8 " +
            "2F8FEFD17F90 E8CD98A238F0 7ADECA946AA0 D88E88A888E0 AFC08FDACF98 " +
            "BDFC339F8228 E2B6CF607D10 AD44A6E431B8 CB60F8C706C0 34F86287C638 " +
            "5A271091C788 6D1460CBBA70 576289A60580 396BD4E3F1E0 0B2F02246010 " +
            "78D80BFBDFF0 9BE2BFDEDFC8 0098A8F2D8F8 FE557A957AA0 820298F3D8F8 " +
            "BA7E6FD57FD8 BA4E579B96B8 BA3CDF1D9A88 82230F4A95A0 FED1D2A360D0"),
    };

    static int VersionOf(bool[,] modules) => (modules.GetLength(0) - 17) / 4;

    static string Hex(bool[,] modules)
    {
        int size = modules.GetLength(0);
        var text = new StringBuilder();
        for (int y = 0; y < size; y++)
        {
            if (y > 0) text.Append(' ');
            for (int x = 0; x < size; x += 4)
            {
                int nibble = 0;
                for (int k = 0; k < 4; k++) nibble = (nibble << 1) | (x + k < size && modules[x + k, y] ? 1 : 0);
                text.Append("0123456789ABCDEF"[nibble]);
            }
        }
        return text.ToString();
    }

    // 形式情報(ビット 0 が一番下の位)。左上か、右上と左下か。
    static int ReadFormat(bool[,] m, bool second)
    {
        int size = m.GetLength(0), bits = 0;
        for (int i = 0; i < 15; i++)
        {
            bool on = second
                ? (i < 8 ? m[size - 1 - i, 8] : m[8, size - 15 + i])
                : (i < 8 ? m[8, i < 6 ? i : i + 1] : m[i == 8 ? 7 : 14 - i, 8]);
            if (on) bits |= 1 << i;
        }
        return bits;
    }

    // 型番情報。右上(3 列 × 6 行)か左下(6 列 × 3 行)か。
    static int ReadVersion(bool[,] m, bool bottomLeft)
    {
        int size = m.GetLength(0), bits = 0;
        for (int i = 0; i < 18; i++)
        {
            int a = size - 11 + i % 3, b = i / 3;
            if (bottomLeft ? m[b, a] : m[a, b]) bits |= 1 << i;
        }
        return bits;
    }

    [Test]
    public void PicksTheSmallestVersionThatFits()
    {
        for (int v = 1; v <= QrCode.MaxVersion; v++)
        {
            Assert.AreEqual(Capacity[v], QrCode.Capacity(v), "型番 " + v);
            Assert.AreEqual(v, VersionOf(QrCode.Encode(new string('a', Capacity[v]))), Capacity[v] + " バイト");
            if (v < QrCode.MaxVersion)
                Assert.AreEqual(v + 1, VersionOf(QrCode.Encode(new string('a', Capacity[v] + 1))), Capacity[v] + 1 + " バイト");
        }
        Assert.AreEqual(1, VersionOf(QrCode.Encode("A")));
        // 日本語は UTF-8 で1字3バイト。
        Assert.AreEqual(1, VersionOf(QrCode.Encode("スマホ印")), "12 バイト");
        Assert.AreEqual(2, VersionOf(QrCode.Encode("スマホで印")), "15 バイト");
        // 実際のアドレス(62〜70字)は型番 4 か 5。
        Assert.AreEqual(4, VersionOf(QrCode.Encode(Url)));
        Assert.AreEqual(5, VersionOf(QrCode.Encode("http://192.168.100.200:5029/m?t=0123456789abcdef0123456789abcdef")));
        Assert.AreEqual(4, QrCode.VersionFor(62));
        Assert.AreEqual(5, QrCode.VersionFor(63));
    }

    [Test]
    public void SizeIs17PlusFourTimesTheVersion()
    {
        for (int v = 1; v <= QrCode.MaxVersion; v++)
        {
            var m = QrCode.Encode(new string('x', Capacity[v]));
            Assert.AreEqual(17 + 4 * v, QrCode.Size(v));
            Assert.AreEqual(17 + 4 * v, m.GetLength(0), "型番 " + v);
            Assert.AreEqual(17 + 4 * v, m.GetLength(1), "型番 " + v);
        }
    }

    [Test]
    public void FindersTimingAlignmentAndDarkModuleAreInPlace()
    {
        foreach (string text in new[] { "A", Url, new string('z', 200) })
        {
            var m = QrCode.Encode(text);
            int size = m.GetLength(0);
            foreach (var (left, top) in new[] { (0, 0), (size - 7, 0), (0, size - 7) })
                for (int dy = -1; dy <= 7; dy++)
                    for (int dx = -1; dx <= 7; dx++)
                    {
                        int x = left + dx, y = top + dy;
                        if (x < 0 || y < 0 || x >= size || y >= size) continue;
                        // 7×7 の外枠と 3×3 の芯が黒。その間と、外の1マス(分離帯)は白。
                        bool inside = dx >= 0 && dx <= 6 && dy >= 0 && dy <= 6;
                        bool ring = dx == 0 || dx == 6 || dy == 0 || dy == 6;
                        bool core = dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4;
                        Assert.AreEqual(inside && (ring || core), m[x, y], text.Length + "字 (" + x + ", " + y + ")");
                    }
            for (int i = 8; i < size - 8; i++)
            {
                Assert.AreEqual(i % 2 == 0, m[i, 6], "横のタイミング " + i);
                Assert.AreEqual(i % 2 == 0, m[6, i], "縦のタイミング " + i);
            }
            // [x, y] の向き: ダークモジュールは左下のファインダーの右上(8 列目、下から 8 行目)。
            Assert.IsTrue(m[8, size - 8], "ダークモジュール");
            if (VersionOf(m) >= 2)
            {
                int c = size - 7;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        Assert.AreEqual(System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy)) != 1, m[c + dx, c + dy], "右下の位置合わせ");
            }
        }
    }

    [Test]
    public void FormatAndVersionInformationAreTheStandardValues()
    {
        for (int mask = 0; mask < 8; mask++) Assert.AreEqual(FormatM[mask], QrCode.FormatBits(mask), "マスク " + mask);
        for (int v = 7; v <= 10; v++) Assert.AreEqual(VersionInfo[v - 7], QrCode.VersionBits(v), "型番 " + v);
        foreach (string text in new[] { "A", "スマホで印", Url, new string('q', 120), new string('q', 213) })
            for (int mask = -1; mask < 8; mask++)
            {
                var m = QrCode.Encode(text, mask, out int version, out int chosen);
                if (mask >= 0) Assert.AreEqual(mask, chosen);
                Assert.AreEqual(FormatM[chosen], ReadFormat(m, false), "左上の形式情報");
                Assert.AreEqual(FormatM[chosen], ReadFormat(m, true), "右上と左下の形式情報");
                if (version < 7) continue;
                Assert.AreEqual(VersionInfo[version - 7], ReadVersion(m, false), "右上の型番情報");
                Assert.AreEqual(VersionInfo[version - 7], ReadVersion(m, true), "左下の型番情報");
            }
    }

    [Test]
    public void EmptyAndTooLongGiveNull()
    {
        Assert.IsNull(QrCode.Encode(null));
        Assert.IsNull(QrCode.Encode(""));
        Assert.IsNotNull(QrCode.Encode(new string('a', 213)));
        Assert.IsNull(QrCode.Encode(new string('a', 214)));
        // 71 字 × 3 バイト = 213 は入る。
        Assert.IsNotNull(QrCode.Encode(new string('印', 71)));
        Assert.IsNull(QrCode.Encode(new string('印', 72)));
        Assert.AreEqual(0, QrCode.VersionFor(214));
        Assert.IsNull(QrCode.Encode("A", 8, out _, out _), "マスクは 0〜7");
    }

    [Test]
    public void ReedSolomonMatchesTheHelloWorldExample()
    {
        // 「HELLO WORLD」型番 1-M のデータの語と訂正の語(よく引かれる例。英数字モードだが訂正の計算は同じ)。
        byte[] data = { 32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17 };
        byte[] ecc = { 196, 35, 39, 119, 235, 215, 231, 226, 93, 23 };
        CollectionAssert.AreEqual(ecc, QrCode.ReedSolomon(data, 10));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void MatchesKnownSymbols(int index)
    {
        var known = Known[index];
        var m = QrCode.Encode(known.Text, -1, out int version, out int mask);
        Assert.AreEqual(known.Version, version, "型番");
        Assert.AreEqual(known.Mask, mask, "マスクの選び方(失点の一番少ないもの)");
        Assert.AreEqual(known.Rows, Hex(m));
        Assert.AreEqual(known.Rows, Hex(QrCode.Encode(known.Text)), "公開の Encode も同じ");
        // マスクを固定しても、そのマスクの所は同じ。
        Assert.AreEqual(known.Rows, Hex(QrCode.Encode(known.Text, known.Mask, out _, out _)));
    }
}
