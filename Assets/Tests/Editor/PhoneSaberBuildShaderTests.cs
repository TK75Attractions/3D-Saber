using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Shader.Find は Editor ではどの shader も見つけるが、ビルド済み Player では build に入った shader しか見つけない。
// 2026-10-12: "Universal Render Pipeline/Unlit" が build に無く、Player だけ剣の初期化が例外で止まった。
// script が名前で探す shader は、Always Included Shaders・render pipeline の既定 shader・Resources フォルダ・
// Assets の Material のどれかで build に入っていること(最終確認は unity_copy.sh build-mac の起動確認)。
public class PhoneSaberBuildShaderTests
{
    // 第一候補が必ず見つかる前提の予備。build に入れていない。
    static readonly HashSet<string> FallbackOnly = new HashSet<string> { "Standard", "Unlit/Color" };
    // scene や prefab の中の Material 経由で build に入ることを、build log と起動確認で確かめた shader(2026-10-12)。
    static readonly HashSet<string> ConfirmedInBuild = new HashSet<string> { "Universal Render Pipeline/Lit" };

    [Test]
    public void ShadersLookedUpByNameAreIncludedInPlayerBuilds()
    {
        var names = new SortedSet<string>();
        var pattern = new Regex("Shader\\.Find\\(\"([^\"]+)\"\\)");
        foreach (string file in Directory.GetFiles("Assets/Scripts", "*.cs", SearchOption.AllDirectories))
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
                names.Add(match.Groups[1].Value);
        Assert.IsNotEmpty(names, "Shader.Find の呼び出しが1つも見つからない(検索の書き方を確認)");

        var included = new HashSet<string>();
        var graphics = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
        SerializedProperty always = graphics.FindProperty("m_AlwaysIncludedShaders");
        for (int i = 0; i < always.arraySize; i++)
            if (always.GetArrayElementAtIndex(i).objectReferenceValue is Shader shader) included.Add(shader.name);
        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" }))
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            if (material != null && material.shader != null) included.Add(material.shader.name);
        }

        // render pipeline の既定 shader と、Resources フォルダにある shader も必ず build に入る。
        Shader pipelineDefault = GraphicsSettings.defaultRenderPipeline != null ? GraphicsSettings.defaultRenderPipeline.defaultShader : null;
        if (pipelineDefault != null) included.Add(pipelineDefault.name);
        foreach (string name in names)
        {
            Shader shader = Shader.Find(name);
            if (shader != null && AssetDatabase.GetAssetPath(shader).Contains("/Resources/")) included.Add(name);
        }

        var missing = names.Where(name => !FallbackOnly.Contains(name) && !ConfirmedInBuild.Contains(name)
            && !included.Contains(name)).ToList();
        Assert.IsEmpty(missing, "ビルド済み Player で Shader.Find が null を返す shader: " + string.Join(", ", missing) +
            "。Project Settings > Graphics の Always Included Shaders に足すか、Assets の Material から参照する。");
        Assert.Contains("Universal Render Pipeline/Unlit", included.ToList());
    }

    [Test]
    public void SmokeArgumentIsOffByDefaultAndClampsTheDuration()
    {
        Assert.AreEqual(0f, PhoneSaberPlayerSmoke.ResolveSeconds(new[] { "app", "-phonesaberStation", "A" }));
        Assert.AreEqual(5f, PhoneSaberPlayerSmoke.ResolveSeconds(new[] { "app", "-phonesaberSmoke", "5" }));
        Assert.AreEqual(PhoneSaberPlayerSmoke.DefaultSeconds, PhoneSaberPlayerSmoke.ResolveSeconds(new[] { "app", "-phonesaberSmoke" }));
        Assert.AreEqual(PhoneSaberPlayerSmoke.DefaultSeconds, PhoneSaberPlayerSmoke.ResolveSeconds(new[] { "app", "-phonesaberSmoke", "9999" }));
        Assert.AreEqual(PhoneSaberPlayerSmoke.DefaultSeconds, PhoneSaberPlayerSmoke.ResolveSeconds(new[] { "app", "-phonesaberSmoke", "-batchmode" }));
    }
}
