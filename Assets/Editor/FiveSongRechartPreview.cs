using System;
using System.IO;
using System.Linq;
using Saber.ChartEditor;
using UnityEditor;
using UnityEngine;

// 保存譜面を本編視点のエディタープレビューで区間撮影する。シーンや設定は変更しない。
public static class FiveSongRechartPreview
{
    public static void Render()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("専用バッチから実行してください");
        var args=Environment.GetCommandLineArgs();int at=Array.IndexOf(args,"-rechartPreviewOutput");
        if(at<0 || at+1>=args.Length)throw new ArgumentException("出力先が必要です");
        string output=Path.GetFullPath(args[at+1]);Directory.CreateDirectory(output);
        string[] songs={"ManicSonic","escapade","logic","BenkyouRemaster","Aobasou"};
        double[][] starts={new[]{21.0,56.4,126.6,143.0},new[]{20.0,65.0,85.5},new[]{30.0,93.0,103.5,183.8},new[]{29.0,72.5,112.0,132.5},new[]{54.0,81.5,109.0}};
        for(int s=0;s<songs.Length;s++)
        foreach(string diff in new[]{"easy","normal","hard"})
        {
            string json=File.ReadAllText(Path.Combine(Application.streamingAssetsPath,"Songs",songs[s],"chart_"+diff+".json"));
            var document=SaberChartUtility.FromJson(json);
            using(var preview=new SaberChartPlaybackPreview())
            foreach(double start in starts[s])
            for(int frame=0;frame<8;frame++)
            {
                double time=start+frame*.25;preview.Tick(document,time);
                var texture=preview.Render(new Rect(0,0,960,540));
                var target=RenderTexture.GetTemporary(960,540,0,RenderTextureFormat.ARGB32);
                var previous=RenderTexture.active;var image=new Texture2D(960,540,TextureFormat.RGB24,false);
                try
                {
                    Graphics.Blit(texture,target);RenderTexture.active=target;
                    image.ReadPixels(new Rect(0,0,960,540),0,0);image.Apply();
                    File.WriteAllBytes(Path.Combine(output,songs[s]+"-"+diff+"-"+time.ToString("F2",System.Globalization.CultureInfo.InvariantCulture)+".png"),image.EncodeToPNG());
                }
                finally {RenderTexture.active=previous;RenderTexture.ReleaseTemporary(target);UnityEngine.Object.DestroyImmediate(image);}
            }
        }
        Debug.Log("[FiveSongRechartPreview] PASS");EditorApplication.Exit(0);
    }
}
