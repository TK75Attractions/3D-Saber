using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

public class SongLocalAudioTests
{
    [Test]
    public void SongAppearsOnlyWhenItsRequiredLocalAudioExists()
    {
        string id = "__LocalAudioTest_" + Guid.NewGuid().ToString("N");
        string folder = Path.Combine(Application.streamingAssetsPath, "Songs", id);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "chart.json"), "{\"bpm\":120,\"notes\":[{\"time\":1000,\"x\":1,\"y\":0}]}");
            File.WriteAllText(Path.Combine(folder, "stage.json"), "{\"requiredAudioFile\":\"audio.wav\"}");
            SongDisplayInfo.ClearCache();
            CollectionAssert.DoesNotContain(SongSelectController.EnumerateSongIds(), id);
            File.WriteAllBytes(Path.Combine(folder, "audio.wav"), new byte[] { 1 });
            CollectionAssert.Contains(SongSelectController.EnumerateSongIds(), id);
            File.WriteAllText(Path.Combine(folder, "stage.json"), "{\"requiredAudioFile\":\"../audio.wav\"}");
            SongDisplayInfo.ClearCache();
            CollectionAssert.DoesNotContain(SongSelectController.EnumerateSongIds(), id);
        }
        finally
        {
            string root = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "Songs")) + Path.DirectorySeparatorChar;
            Assert.IsTrue(Path.GetFullPath(folder).StartsWith(root, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(Path.GetFileName(folder).StartsWith("__LocalAudioTest_"));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            if (File.Exists(folder + ".meta")) File.Delete(folder + ".meta");
            SongDisplayInfo.ClearCache();
        }
    }
}
