using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class PrismSfxAssetTests
{
    [TestCase("Saber_NoteCut", .160f)]
    [TestCase("Saber_NoteCut_Rapid", .095f)]
    [TestCase("Saber_FlickCut", .190f)]
    [TestCase("Saber_LongTick", .085f)]
    [TestCase("Saber_LongFinish", .250f)]
    [TestCase("Saber_GoldCut", .460f)]
    [TestCase("Saber_Miss", .150f)]
    public void PrismClip_ImportsAsReadyMonoPcmWithImmediateCleanAttack(string name, float seconds)
    {
        var clip=Resources.Load<AudioClip>("Audio/SFX/"+name);
        Assert.NotNull(clip);
        Assert.AreEqual(48000,clip.frequency);
        Assert.AreEqual(1,clip.channels);
        Assert.AreEqual(seconds,clip.length,.0001f);
        Assert.AreEqual(AudioClipLoadType.DecompressOnLoad,clip.loadType);
        var importer=(AudioImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(clip));
        Assert.AreEqual(AudioCompressionFormat.PCM,importer.defaultSampleSettings.compressionFormat);
        Assert.IsFalse(importer.loadInBackground,"小さい判定音をバックグラウンド待ちにしない");
        var data=new float[clip.samples];
        Assert.True(clip.GetData(data,0),"インポート後も波形を取得できる");
        float peak=0;int onset=-1;
        for(int i=0;i<data.Length;i++)
        {
            Assert.False(float.IsNaN(data[i])||float.IsInfinity(data[i]));
            float value=Mathf.Abs(data[i]);peak=Mathf.Max(peak,value);
            if(onset<0&&value>.0031623f) onset=i;
        }
        Assert.Greater(peak,.025f,"無音に変化していない");
        // 2026-09-23: ユーザー依頼で切断系6音源を波形で +5 dB(ピーク約 -4 dBFS)。左右同時の同一音は加算で一瞬 0 dBFS を超え得るが、
        // それ以上の余裕は曲側の音量で確保する方針にし、上限を 0.43 → 0.70 へ。Miss だけ据え置き。
        Assert.Less(peak,.70f,"単発でクリップしない余裕を残す");
        Assert.That(onset,Is.InRange(0,96),"素材の先頭2ms以内に打点がある");
        Assert.Less(Mathf.Abs(data[0]),.00001f);
        Assert.Less(Mathf.Abs(data[data.Length-1]),.00001f);
    }
}
