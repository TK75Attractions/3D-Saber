using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class TitleStartNoteTests
{
    private readonly List<GameObject> created = new List<GameObject>();

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in created)
        {
            if (go != null) Object.DestroyImmediate(go);
        }
        created.Clear();
        // カット時に生成されるスライス片・破片を掃除
        foreach (var d in Object.FindObjectsByType<SlicePieceDecay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (d != null) Object.DestroyImmediate(d.gameObject);
        }
        foreach (var n in Object.FindObjectsByType<CuttableNote>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (n != null) Object.DestroyImmediate(n.gameObject);
        }
    }

    [Test]
    public void Build_CreatesJudgeableNoteWithVisuals()
    {
        var tsn = TitleStartNote.Build(new Vector3(0f, -2.3f, 0f), Color.red);
        created.Add(tsn.gameObject);

        Assert.IsNotNull(tsn.Note, "CuttableNote が割り付く");
        Assert.IsTrue(tsn.Note.IsJudgeable, "タイトルでは常時切れる状態");
        Assert.IsNotNull(tsn.GetComponent<NoteVisuals>(), "結晶外観コンポーネントが付く");
        Assert.IsNotNull(tsn.GetComponent<MeshRenderer>().sharedMaterial, "マテリアルが残っている");
        Assert.AreEqual(new Vector3(0f, -2.3f, 0f), tsn.transform.position);
    }

    [Test]
    public void Cut_FiresOnSlashedOnce()
    {
        var tsn = TitleStartNote.Build(Vector3.zero, Color.red);
        created.Add(tsn.gameObject);
        int fired = 0;
        tsn.OnSlashed += () => fired++;

        tsn.Note.Cut(Vector3.zero, new Vector3(8f, 0f, 0f));

        Assert.AreEqual(1, fired, "最初のカットで1回だけ発火");
    }

    [Test]
    public void SlashProgrammatically_CutsTheNote()
    {
        var tsn = TitleStartNote.Build(Vector3.zero, Color.red);
        created.Add(tsn.gameObject);
        bool fired = false;
        tsn.OnSlashed += () => fired = true;
        var note = tsn.Note;

        tsn.SlashProgrammatically();

        Assert.IsTrue(fired, "キーボード経由でも同じスラッシュ演出を通る");
        Assert.IsTrue(note.IsCut);
    }
}
