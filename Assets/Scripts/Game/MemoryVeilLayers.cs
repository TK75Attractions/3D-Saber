// 覚える裏譜面(Morning の「M」、2026-10-25)の描画順と、手がかりを消す境目。
// 画面のノイズはステージとノーツの上に重ね、剣と切ったときの光だけをノイズの上に出す(見えないノーツを切った手応えは残す)。
// 本編のカメラは1台(Main Camera)で、2D レンダラーは sortingOrder → renderQueue の順に並べるので、両方をそろえて指定する。
public static class MemoryVeilLayers
{
    public const int NoiseSortingOrder = 30000;
    public const int NoiseRenderQueue = 3040;
    // まね以外のノーツ(次の見本など)はノイズの上、剣の下。まねの2小節はずっとノイズをかけるので、
    // その終わり近くで奥から出てくる次の見本をノイズの上に見せる。
    public const int AboveNoteSortingOrder = 30005;
    // 剣(刃・縁・光)と切った場所の光。ノイズより後に描く。
    public const int AboveSortingOrder = 30010;
    public const int AboveRenderQueue = 3060;
    // 見えにくさ(NoteData.veil)がこれ以上のノーツは、位置や時刻を漏らす手がかり
    // (着地の輪・床の印・同時押しの線・切断演出の保護の穴・矢印・残り回数)を出さない。
    public const float HideCuesAtVeil = .5f;
}
