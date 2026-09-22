using NUnit.Framework;
using UnityEditor;

public class PhoneSaberProjectSettingsTests
{
    [Test]
    public void RunInBackground_IsEnabled()
    {
        Assert.IsTrue(PlayerSettings.runInBackground,
            "PhoneSaber入力はUnityウィンドウが非アクティブでも更新を継続する");
    }
}
