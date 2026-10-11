using NUnit.Framework;
using UnityEngine;

// 校歌の専用演出を再びビルドに混入させない。通常の曲データには触れない。
public class HardIntroTimelineTests
{
    [Test] public void RetiredSchoolIntroHasNoRuntimeTypesOrResources()
    {
        var assembly=typeof(ScreenTransition).Assembly;
        Assert.IsNull(assembly.GetType("HardSongIntro"));
        Assert.IsNull(assembly.GetType("HardIntroTimeline"));
        Assert.IsEmpty(Resources.LoadAll("UI/HardIntro"));
    }
}
