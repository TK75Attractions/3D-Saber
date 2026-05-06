using NUnit.Framework;
using UnityEngine;

public class FollowTransformWorldOffsetTests
{
    [TearDown]
    public void Cleanup()
    {
        foreach (var f in Object.FindObjectsByType<FollowTransformWorldOffset>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (f != null) Object.DestroyImmediate(f.gameObject);
        }
    }

    [Test]
    public void Follower_PositionEqualsTargetPlusOffset_AfterLateUpdate()
    {
        var target = new GameObject("target");
        target.transform.position = new Vector3(2f, 3f, 4f);

        var follower = new GameObject("follower");
        var f = follower.AddComponent<FollowTransformWorldOffset>();
        f.target = target.transform;
        f.worldOffset = new Vector3(0f, 1f, 0f);

        // EditMode は LateUpdate を自動呼び出ししないので手動で呼ぶ
        typeof(FollowTransformWorldOffset).GetMethod("LateUpdate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Invoke(f, null);

        Assert.AreEqual(new Vector3(2f, 4f, 4f), follower.transform.position);
        Object.DestroyImmediate(target);
    }
}
