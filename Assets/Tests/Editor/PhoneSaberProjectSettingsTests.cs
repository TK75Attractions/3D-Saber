using NUnit.Framework;
using UnityEditor;
using System.Linq;

public class PhoneSaberProjectSettingsTests
{
    [Test]
    public void RunInBackground_IsEnabled()
    {
        Assert.IsTrue(PlayerSettings.runInBackground,
            "PhoneSaber入力はUnityウィンドウが非アクティブでも更新を継続する");
    }

    [Test]
    public void ProductionPlayerStartsAtTitle()
    {
        var firstEnabledScene = EditorBuildSettings.scenes.FirstOrDefault(scene => scene.enabled);
        Assert.NotNull(firstEnabledScene, "Player buildに有効なSceneが登録されている");
        Assert.AreEqual("Assets/Scenes/Title.unity", firstEnabledScene.path,
            "PlayerはBuild Settingsの最初の有効Sceneから起動する");
    }
}
