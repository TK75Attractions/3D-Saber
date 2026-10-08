// 結果の「選曲へ」だけに有効な一回限りの復元。タイトル経由の新規選曲へは持ち越さない。
public static class ResultSelectionReturn
{
    static string songId, difficulty;
    public static void Remember(string song, string chart) { songId = song; difficulty = chart; }
    public static void Consume(out string song, out string chart)
    {
        song = songId; chart = difficulty; songId = difficulty = null;
    }
}
