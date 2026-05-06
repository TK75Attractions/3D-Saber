using NUnit.Framework;
using UnityEngine;

public class DirectionAndLongNoteTests
{
    private CuttableNote MakeNote()
    {
        var go = new GameObject("note");
        return go.AddComponent<CuttableNote>();
    }

    [TearDown]
    public void Cleanup()
    {
        foreach (var n in Object.FindObjectsByType<CuttableNote>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (n != null) Object.DestroyImmediate(n.gameObject);
        }
    }

    [Test]
    public void DirectionNote_CutInRequiredDirection_IsCorrect()
    {
        var n = MakeNote();
        n.RequiredDirection = CutDirection.Right;
        n.Cut(Vector3.zero, new Vector3(5f, 0f, 0f));
        Assert.IsTrue(n.LastCutCorrectDirection);
    }

    [Test]
    public void DirectionNote_CutInWrongDirection_IsIncorrect()
    {
        var n = MakeNote();
        n.RequiredDirection = CutDirection.Right;
        n.Cut(Vector3.zero, new Vector3(0f, 5f, 0f));
        Assert.IsFalse(n.LastCutCorrectDirection);
    }

    [Test]
    public void NormalNote_AnyDirectionIsCorrect()
    {
        var n = MakeNote();
        n.RequiredDirection = CutDirection.None;
        n.Cut(Vector3.zero, new Vector3(0f, -3f, 0f));
        Assert.IsTrue(n.LastCutCorrectDirection);
    }

    [Test]
    public void LongNote_RequiresMultipleCuts()
    {
        var n = MakeNote();
        n.RequiredCutCount = 3;
        n.RemainingCuts = 3;

        n.Cut(Vector3.zero, new Vector3(5f, 0f, 0f));
        Assert.IsFalse(n.IsCut);
        Assert.IsFalse(n.IsFinalized);
        Assert.AreEqual(2, n.RemainingCuts);
        Assert.AreEqual(1, n.CutsAchieved);

        n.Cut(Vector3.zero, new Vector3(5f, 0f, 0f));
        Assert.IsFalse(n.IsCut);
        Assert.AreEqual(2, n.CutsAchieved);

        n.Cut(Vector3.zero, new Vector3(5f, 0f, 0f));
        Assert.IsTrue(n.IsCut);
        Assert.IsTrue(n.IsFinalized);
        Assert.AreEqual(3, n.CutsAchieved);
    }

    [Test]
    public void LongNote_OnCutFiresOnlyAtFinalCut()
    {
        var n = MakeNote();
        n.RequiredCutCount = 3;
        n.RemainingCuts = 3;
        int fired = 0;
        n.OnCut += (_, __, ___) => fired++;
        n.Cut(Vector3.zero, Vector3.right);
        n.Cut(Vector3.zero, Vector3.right);
        Assert.AreEqual(0, fired, "途中ではOnCutは発火しない");
        n.Cut(Vector3.zero, Vector3.right);
        Assert.AreEqual(1, fired, "最終カットでだけ発火");
    }

    [Test]
    public void LongNote_PartialMiss_FiresOnCutNotOnMiss()
    {
        var n = MakeNote();
        n.RequiredCutCount = 4;
        n.RemainingCuts = 4;
        int cutFired = 0;
        int missFired = 0;
        n.OnCut += (_, __, ___) => cutFired++;
        n.OnMiss += _ => missFired++;
        n.Cut(Vector3.zero, Vector3.right); // 1回切った
        n.Cut(Vector3.zero, Vector3.right); // 2回切った
        n.MarkMiss();                       // タイムアウト
        Assert.AreEqual(1, cutFired, "部分達成は OnCut で通知");
        Assert.AreEqual(0, missFired);
        Assert.AreEqual(2, n.CutsAchieved);
    }

    [Test]
    public void LongNote_ZeroCutThenMiss_FiresOnMiss()
    {
        var n = MakeNote();
        n.RequiredCutCount = 3;
        n.RemainingCuts = 3;
        int cutFired = 0;
        int missFired = 0;
        n.OnCut += (_, __, ___) => cutFired++;
        n.OnMiss += _ => missFired++;
        n.MarkMiss();
        Assert.AreEqual(0, cutFired);
        Assert.AreEqual(1, missFired);
    }

    [Test]
    public void LongNote_CracksAddedEachCut()
    {
        var n = MakeNote();
        n.RequiredCutCount = 3;
        n.RemainingCuts = 3;
        int cracksBefore = CountCracks(n);
        n.Cut(Vector3.zero, Vector3.right);
        int cracksAfter1 = CountCracks(n);
        n.Cut(Vector3.zero, Vector3.right);
        int cracksAfter2 = CountCracks(n);
        Assert.Greater(cracksAfter1, cracksBefore);
        Assert.Greater(cracksAfter2, cracksAfter1);
    }

    private static int CountCracks(CuttableNote n)
    {
        int count = 0;
        for (int i = 0; i < n.transform.childCount; i++)
        {
            if (n.transform.GetChild(i).name == "Crack") count++;
        }
        return count;
    }

    [Test]
    public void LongNote_CountLabel_DecrementsAndDisappears()
    {
        var n = MakeNote();
        n.RequiredCutCount = 3;
        n.RemainingCuts = 3;
        // ラベルを擬似的に持たせる（TMP は EditMode で生成しづらいので簡易モック相当）
        var labelGo = new GameObject("CountLabel");
        labelGo.transform.SetParent(n.transform, false);
        var tmp = labelGo.AddComponent<TMPro.TextMeshPro>();
        tmp.text = "3";
        n.countLabel = tmp;

        n.Cut(Vector3.zero, Vector3.right);
        Assert.AreEqual("2", n.countLabel.text);
        n.Cut(Vector3.zero, Vector3.right);
        Assert.AreEqual("1", n.countLabel.text);
        n.Cut(Vector3.zero, Vector3.right);
        // 0 になったらラベルを非表示
        Assert.IsFalse(n.countLabel.gameObject.activeSelf);
    }
}
