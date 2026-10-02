using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class PhotographicStageTests
{
    private GameObject root;
    private static readonly StageTheme[] Themes = { StageTheme.AuroraLake, StageTheme.RainyCity, StageTheme.SunlitOcean };
    [TearDown] public void Cleanup()
    {
        DisplaySettings.ResetReducedEffectsCacheForTest();
        if (root != null) Object.DestroyImmediate(root);
    }

    private FloorRenderer Build(StageTheme theme)
    {
        root = new GameObject("PhotoStageTest");
        var floor = root.AddComponent<FloorRenderer>();
        floor.Build(theme);
        return floor;
    }

    [TestCaseSource(nameof(Themes))]
    public void BackdropLoadsRealAssetAndLeavesGameplaySpaceClear(StageTheme theme)
    {
        var floor = Build(theme);
        var photo = root.GetComponentInChildren<PhotographicStage>();
        Assert.NotNull(photo);
        Assert.AreEqual(theme, photo.Theme);
        Assert.IsEmpty(root.GetComponentsInChildren<Collider>());
        Assert.IsEmpty(root.GetComponentsInChildren<Light>());
        Assert.IsNull(root.GetComponentInChildren<ScenicStageWorld>());
        var renderers = root.GetComponentsInChildren<MeshRenderer>();
        Assert.AreEqual(1, renderers.Length);
        var material = renderers[0].sharedMaterial;
        Assert.False(ShaderUtil.ShaderHasError(material.shader));
        Assert.Less(material.renderQueue, 2000);
        var texture = material.GetTexture("_BackgroundTex");
        Assert.NotNull(texture);
        Assert.GreaterOrEqual(texture.width,1280);
        Assert.That(texture.width/(float)texture.height, Is.EqualTo(16f/9).Within(.02f));
        Assert.AreEqual(TextureWrapMode.Clamp,texture.wrapMode);
        var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture));
        Assert.False(importer.isReadable);
        Assert.AreEqual(TextureImporterNPOTScale.None,importer.npotScale);
        floor.Build(StageTheme.ObsidianRelay);
        Assert.AreSame(photo,PhotographicStage.Create(floor));
        Assert.AreEqual(theme,floor.ActiveTheme);
        Assert.AreEqual(1,root.GetComponentsInChildren<MeshRenderer>().Length);
    }

    [TestCaseSource(nameof(Themes))]
    public void FloorClockDrivesFreezeRewindAndRejectsInvalidTime(StageTheme theme)
    {
        var floor = Build(theme);
        var photo = root.GetComponentInChildren<PhotographicStage>();
        var material = photo.GetComponent<MeshRenderer>().sharedMaterial;
        floor.Tick(3.5,.7f);
        Assert.AreEqual(3.5,photo.LastTickSeconds);
        Assert.AreEqual(3.5f,material.GetFloat("_MotionTime"));
        Assert.AreEqual(.7f,material.GetFloat("_Chorus"));
        floor.Tick(9); floor.Tick(3.5,.7f);
        Assert.AreEqual(3.5f,material.GetFloat("_MotionTime"));
        floor.Tick(double.NaN); floor.Tick(double.PositiveInfinity);
        Assert.AreEqual(3.5,photo.LastTickSeconds);
        floor.enabled=false; floor.Tick(8);
        Assert.AreEqual(3.5,photo.LastTickSeconds);
        floor.enabled=true; floor.Tick(-1,float.NaN);
        Assert.AreEqual(0,photo.LastTickSeconds);
        Assert.AreEqual(0,material.GetFloat("_Chorus"));
        DisplaySettings.SetReducedEffectsForTest(true); floor.Tick(1);
        Assert.AreEqual(.3f,material.GetFloat("_Effects"));
        DisplaySettings.SetReducedEffectsForTest(false); floor.Tick(1);
        Assert.AreEqual(1f,material.GetFloat("_Effects"));
    }

    [TestCaseSource(nameof(Themes))]
    public void UnloadingStageReleasesOwnedAssetsButPreservesSharedTexture(StageTheme theme)
    {
        Build(theme);
        var mesh = root.GetComponentInChildren<MeshFilter>().sharedMesh;
        var material = root.GetComponentInChildren<MeshRenderer>().sharedMaterial;
        var texture = material.GetTexture("_BackgroundTex");
        Object.DestroyImmediate(root);
        Assert.IsTrue(mesh==null);
        Assert.IsTrue(material==null);
        Assert.IsTrue(texture!=null);
    }
}
