using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

// InputPoint のシングルトン重複ガードの検証。
// 「タイトル/曲選択で常駐化した受信機」と「Game シーン直置きの受信機」が共存すると、
// 後者が Instance を上書き + ポート二重バインド(SocketException)で入力が全死する事故の回帰防止。
// EditMode では Awake が呼ばれないためリフレクションで直接起動する(確立済みパターン)。
public class InputPointSingletonTests
{
    private readonly List<GameObject> created = new List<GameObject>();
    private InputPoint savedInstance;

    [SetUp]
    public void SaveInstance()
    {
        savedInstance = InputPoint.Instance;
        SetInstance(null);
    }

    [TearDown]
    public void Cleanup()
    {
        foreach (var go in created)
        {
            if (go != null) Object.DestroyImmediate(go);
        }
        created.Clear();
        SetInstance(savedInstance);
    }

    static void SetInstance(InputPoint value)
    {
        typeof(InputPoint).GetProperty("Instance").SetMethod
            .Invoke(null, new object[] { value });
    }

    static void InvokeAwake(InputPoint ip)
    {
        typeof(InputPoint).GetMethod("Awake", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(ip, null);
    }

    [Test]
    public void FirstInstance_ClaimsSingleton()
    {
        var go = new GameObject("ipA");
        created.Add(go);
        var a = go.AddComponent<InputPoint>();
        InvokeAwake(a);
        Assert.AreSame(a, InputPoint.Instance);
    }

    [Test]
    public void DuplicateInstance_YieldsToExisting_AndRemovesItself()
    {
        var goA = new GameObject("ipA");
        created.Add(goA);
        var a = goA.AddComponent<InputPoint>();
        InvokeAwake(a);

        // Game シーン直置き相当の2つ目: 先住が勝ち、自分はコンポーネントだけ退場する
        var goB = new GameObject("ipB");
        created.Add(goB);
        var b = goB.AddComponent<InputPoint>();
        InvokeAwake(b);

        Assert.AreSame(a, InputPoint.Instance, "先住の受信機が Instance のまま");
        Assert.IsTrue(b == null, "重複コンポーネントは破棄される");
        Assert.IsFalse(goB == null, "GameObject 自体は残る(他コンポーネントを巻き込まない)");
    }

    [Test]
    public void EnsureInstance_ReusesExisting_WithDirectMapping()
    {
        var go = new GameObject("ipA");
        created.Add(go);
        var a = go.AddComponent<InputPoint>();
        InvokeAwake(a);
        a.useDirectWorldMapping = false;

        var result = InputPoint.EnsureInstance();
        Assert.AreSame(a, result, "既存インスタンスを再利用する(新規生成しない)");
        Assert.IsTrue(a.useDirectWorldMapping, "本番構成(直結マッピング)を強制する");
    }
}
