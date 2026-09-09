using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 曲選択専用の金属パネルUI。シーン・操作・曲データは維持し、実行中の外観だけ組み直す。
public class SongSelectSkin : MonoBehaviour
{
    SongSelectController ctl;
    SongWheelView wheel;
    TextMeshProUGUI panelTitle, startDifficultyHint, trackNumber;
    SongSelectActionStyle startStyle;
    GameObject masterWarning, jacketLockedOverlay, fallbackCover;
    readonly List<DifficultyTileItem> difficultyItems = new List<DifficultyTileItem>();
    readonly Dictionary<int, Sprite> coverCache = new Dictionary<int, Sprite>();
    const float DetailX = 530f;

    IEnumerator Start()
    {
        yield return null;
        ctl = Object.FindFirstObjectByType<SongSelectController>();
        if (ctl == null) yield break;
        var canvas = ctl.GetComponent<Canvas>() ?? ctl.GetComponentInParent<Canvas>();
        if (canvas == null) yield break;
        if (Camera.main != null)
        {
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = Camera.main;
            canvas.planeDistance = 20f;
        }
        var scaler = canvas.GetComponent<CanvasScaler>() ?? canvas.gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920,1080);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = .5f;
        ctl.selectedPrefix = ctl.normalPrefix = "";
        if (ctl.SelectedIndex >= 0) ctl.Select(ctl.SelectedIndex);
        HideSceneRelics(canvas);
        BuildBackdrop(canvas);
        BuildHeader(canvas);
        BuildPanels(canvas);
        wheel = SongWheelView.Build(ctl, canvas.transform, CoverSprite, DifficultyColor);
        BuildRightPanel(canvas);
        BuildFooter(canvas);
        ctl.OnSelectionChanged += HandleSelectionChanged;
        ctl.OnDifficultyChanged += HandleDifficultyChanged;
        HandleSelectionChanged(ctl.SelectedIndex);
        SaberUIPointer.Build();
        SongSelectSlashNav.Build(ctl);
    }

    void OnDestroy()
    {
        if (ctl != null)
        {
            ctl.OnSelectionChanged -= HandleSelectionChanged;
            ctl.OnDifficultyChanged -= HandleDifficultyChanged;
        }
        foreach (var sprite in coverCache.Values)
        {
            if (sprite == null) continue;
            var texture = sprite.texture;
            UISkinKit.SafeDestroy(sprite);
            if (texture != null) UISkinKit.SafeDestroy(texture);
        }
        coverCache.Clear();
    }

    // 既存UIテスト・他画面からの呼び出し契約は維持する。
    public static Color DifficultyColor(int index) => SongSelectVisuals.Difficulty[Mathf.Clamp(index,0,2)];
    public static string DifficultyDisplayName(int index, string sourceName)
    {
        if (index == 2) return "MASTER";
        return string.IsNullOrEmpty(sourceName) ? $"CHART {index+1}" : sourceName.ToUpperInvariant();
    }
    public static int MeterSegmentsForLevel(int level) => Mathf.Clamp(level,0,10);
    public static string FormatDifficultyLevel(int level) => level > 0 ? $"LEVEL {Mathf.Clamp(level,0,99):00}" : "LEVEL --";
    public static string FormatDifficultyCardLevel(int level) => level > 0 ? $"LV {Mathf.Clamp(level,0,99):00}" : "LV --";
    public static void EnterCalibration()
    {
        GameSession.IsCalibrationMode = true;
        UnityEngine.SceneManagement.SceneManager.LoadScene("Game");
    }
    public static void ApplyNeon(Button btn, Color accent, float fillAlpha) => UISkinKit.RestyleButton(btn, accent);

    void HideSceneRelics(Canvas canvas)
    {
        foreach (var img in canvas.GetComponentsInChildren<Image>(true))
            if (img.name == "ScrollView") img.gameObject.SetActive(false);
        var header = TitleSceneSkin.FindTextByContent(canvas,"Select Song");
        if (header != null) header.gameObject.SetActive(false);
        var oldLabel = TitleSceneSkin.FindTextByContent(canvas,"Difficulty");
        if (oldLabel != null) oldLabel.gameObject.SetActive(false);
        if (ctl.difficultyDisplay != null) ctl.difficultyDisplay.gameObject.SetActive(false);
        foreach (var text in canvas.GetComponentsInChildren<Text>(true))
            if (text.text.Contains("↑↓")) text.gameObject.SetActive(false);
    }

    void BuildBackdrop(Canvas canvas)
    {
        var rt = SongSelectVisuals.Rect(canvas.transform,"SelectBackdrop",Vector2.zero,Vector2.zero);
        SongSelectVisuals.Stretch(rt); rt.SetAsFirstSibling();
        var graphic = rt.gameObject.AddComponent<SongSelectBackdropGraphic>(); graphic.raycastTarget = false;
    }

    void BuildHeader(Canvas canvas)
    {
        SongSelectVisuals.Label(canvas.transform,"HeaderCrumb","3D SABER  /  MUSIC LIBRARY",16,new Vector2(-510,484),new Vector2(748,24),SongSelectVisuals.Muted);
        SongSelectVisuals.Label(canvas.transform,"HeaderTitle","SONG SELECT",52,new Vector2(-555,430),new Vector2(658,70),SongSelectVisuals.Text,TextAlignmentOptions.MidlineLeft,true);
        SongSelectVisuals.Label(canvas.transform,"HeaderJp","楽曲選択",22,new Vector2(-383,422),new Vector2(166,35),SongSelectVisuals.Muted);
        var back = Action(canvas.transform,"BackToTitle","タイトルへ",new Vector2(783,450),new Vector2(178,52),false);
        back.onClick.AddListener(() => UnityEngine.SceneManagement.SceneManager.LoadScene("Title"));
        SongSelectVisuals.Panel(canvas.transform,"HeaderRule",new Vector2(0,385),new Vector2(1768,1),SongSelectVisuals.Edge,Color.clear,0);
    }

    void BuildPanels(Canvas canvas)
    {
        SongSelectVisuals.Panel(canvas.transform,"LibraryPanel",new Vector2(-490,-26),new Vector2(788,772),
            new Color(.045f,.071f,.092f,.88f),SongSelectVisuals.Edge,16);
        SongSelectVisuals.Panel(canvas.transform,"DetailPanel",new Vector2(DetailX,-26),new Vector2(684,772),
            new Color(.055f,.087f,.109f,.94f),SongSelectVisuals.Edge,16);
        SongSelectVisuals.Label(canvas.transform,"LibraryLabel","TRACK LIST",17,new Vector2(-687,329),new Vector2(330,25),SongSelectVisuals.Muted);
        SongSelectVisuals.Label(canvas.transform,"LibraryCount",$"{ctl.SongCount:00} TRACKS",16,new Vector2(-196,329),new Vector2(156,25),SongSelectVisuals.Muted,TextAlignmentOptions.MidlineRight);
        SongSelectVisuals.Label(canvas.transform,"SelectedLabel","SELECTED TRACK",17,new Vector2(DetailX-155,329),new Vector2(300,25),SongSelectVisuals.Muted);
        trackNumber = SongSelectVisuals.Label(canvas.transform,"TrackNumber","",16,new Vector2(DetailX+221,329),new Vector2(156,25),SongSelectVisuals.Muted,TextAlignmentOptions.MidlineRight);
        // 3Dナビノーツはそのまま。背景パネルとラベルで操作場所を明確にする。
        foreach (int sign in new[] {1,-1})
        {
            float y=sign*194.4f;
            SongSelectVisuals.Panel(canvas.transform,sign>0?"NavUpDock":"NavDownDock",new Vector2(124.8f,y),new Vector2(116,146),SongSelectVisuals.Surface,SongSelectVisuals.Edge,12);
            SongSelectVisuals.Label(canvas.transform,sign>0?"NavPreviousLabel":"NavNextLabel",sign>0?"PREVIOUS":"NEXT",14,new Vector2(124.8f,y-57),new Vector2(116,20),SongSelectVisuals.Muted,TextAlignmentOptions.Center);
        }
        SongSelectVisuals.Label(canvas.transform,"NavInstruction","斬って\n曲送り",19,new Vector2(124.8f,0),new Vector2(114,74),SongSelectVisuals.Muted,TextAlignmentOptions.Center);
    }

    void BuildRightPanel(Canvas canvas)
    {
        BuildJacket(canvas);
        panelTitle = SongSelectVisuals.Label(canvas.transform,"PanelSongTitle","",33,new Vector2(DetailX,-39),new Vector2(596,52),SongSelectVisuals.Text,TextAlignmentOptions.Center,true);
        panelTitle.enableAutoSizing=true; panelTitle.fontSizeMin=23; panelTitle.fontSizeMax=33;
        BuildDifficultyRibbons(canvas);
        masterWarning = SongSelectVisuals.Rect(canvas.transform,"MasterWarning",new Vector2(DetailX,-219),new Vector2(592,40)).gameObject;
        SongSelectVisuals.Panel(masterWarning.transform,"WarningRule",new Vector2(-286,0),new Vector2(3,18),DifficultyColor(2),Color.clear,0);
        SongSelectVisuals.Label(masterWarning.transform,"Text","高難易度注意！！",18,Vector2.zero,new Vector2(550,40),DifficultyColor(2),TextAlignmentOptions.MidlineLeft);
        masterWarning.SetActive(false);
        if (ctl.startButton != null)
        {
            var rt=ctl.startButton.GetComponent<RectTransform>(); rt.SetParent(canvas.transform,false); rt.SetAsLastSibling();
            rt.anchorMin=rt.anchorMax=rt.pivot=new Vector2(.5f,.5f);
            rt.anchoredPosition=new Vector2(DetailX,-282); rt.sizeDelta=new Vector2(592,76);
            startStyle=SongSelectVisuals.StyleAction(ctl.startButton,"START  /  プレイ開始",true);
        }
        startDifficultyHint = SongSelectVisuals.Label(canvas.transform,"DifficultyHint","",16,new Vector2(DetailX,-346),new Vector2(592,24),SongSelectVisuals.Muted,TextAlignmentOptions.Center);
        SongSelectVisuals.Label(canvas.transform,"DwellHint","クリック / ENTER  または  セーバーを1秒かざす",17,new Vector2(DetailX,-379),new Vector2(618,25),SongSelectVisuals.Muted,TextAlignmentOptions.Center);
    }

    void BuildJacket(Canvas canvas)
    {
        var frame=SongSelectVisuals.Panel(canvas.transform,"JacketFrame",new Vector2(DetailX,149),new Vector2(316,316),SongSelectVisuals.Raised,SongSelectVisuals.Edge,12);
        var viewport=SongSelectVisuals.Rect(frame.transform,"Artwork",Vector2.zero,new Vector2(296,296));
        if (ctl.jacketImage != null)
        {
            ctl.jacketImage.rectTransform.SetParent(viewport,false);
            SongSelectVisuals.Stretch(ctl.jacketImage.rectTransform); ctl.jacketImage.preserveAspect=true; ctl.jacketImage.raycastTarget=false;
        }
        fallbackCover=SongSelectVisuals.Rect(viewport,"FallbackArtwork",Vector2.zero,new Vector2(296,296)).gameObject;
        var art=fallbackCover.AddComponent<SongSelectCoverGraphic>();art.raycastTarget=false;
        SongSelectVisuals.Label(fallbackCover.transform,"CoverBrand","3D / SABER",14,new Vector2(0,118),new Vector2(252,22),SongSelectVisuals.Muted);
        SongSelectVisuals.Label(fallbackCover.transform,"CoverCaption","RHYTHM ARCHIVE",14,new Vector2(0,-120),new Vector2(252,22),SongSelectVisuals.Muted,TextAlignmentOptions.MidlineRight);
        jacketLockedOverlay=SongSelectVisuals.Panel(viewport,"LockedOverlay",Vector2.zero,new Vector2(296,296),new Color(.025f,.04f,.06f,.83f),Color.clear,0).gameObject;
        SongSelectVisuals.Label(jacketLockedOverlay.transform,"Label","譜面準備中",26,Vector2.zero,new Vector2(252,42),SongSelectVisuals.Text,TextAlignmentOptions.Center,true);
        jacketLockedOverlay.SetActive(false);
    }

    void BuildDifficultyRibbons(Canvas canvas)
    {
        var row=SongSelectVisuals.Rect(canvas.transform,"DifficultyRibbonRow",new Vector2(DetailX,-136),new Vector2(592,DifficultyTileItem.TileHeight));
        var layout=row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing=12; layout.childAlignment=TextAnchor.MiddleCenter;
        layout.childControlWidth=layout.childControlHeight=true; layout.childForceExpandWidth=true; layout.childForceExpandHeight=false;
        ctl.suppressDefaultDifficultyTint=true;
        if (ctl.difficultyButtons==null) return;
        for (int i=0;i<ctl.difficultyButtons.Length;i++)
        {
            var button=ctl.difficultyButtons[i];
            if (button==null) { difficultyItems.Add(null); continue; }
            var rt=button.GetComponent<RectTransform>();rt.SetParent(row,false);
            var tile=button.GetComponent<DifficultyTileItem>() ?? button.gameObject.AddComponent<DifficultyTileItem>();
            string source=ctl.difficultyNames!=null && i<ctl.difficultyNames.Length?ctl.difficultyNames[i]:null;
            tile.Build(button,DifficultyColor(i),DifficultyDisplayName(i,source),ctl.DifficultyDisplayLevelAt(i));
            tile.SetSelected(i==ctl.SelectedDifficultyIndex,true);difficultyItems.Add(tile);
        }
    }

    void BuildFooter(Canvas canvas)
    {
        SongSelectVisuals.Panel(canvas.transform,"FooterRule",new Vector2(0,-449),new Vector2(1768,1),SongSelectVisuals.Edge,Color.clear,0);
        var calibration=Action(canvas.transform,"CalibrationButton","判定調整",new Vector2(-774,-488),new Vector2(220,52),false);
        calibration.onClick.AddListener(EnterCalibration);
        SongSelectVisuals.Label(canvas.transform,"FooterHint","↑ ↓  曲を選択     ← →  難易度     ENTER  プレイ開始",18,new Vector2(128,-488),new Vector2(1400,28),SongSelectVisuals.Muted,TextAlignmentOptions.MidlineRight);
    }

    static Button Action(Transform parent,string name,string label,Vector2 position,Vector2 size,bool primary)
    {
        var rt=SongSelectVisuals.Rect(parent,name,position,size); var b=rt.gameObject.AddComponent<Button>();
        SongSelectVisuals.StyleAction(b,label,primary);return b;
    }

    void HandleSelectionChanged(int index)
    {
        if (wheel!=null) wheel.SetSelected(index);
        if (panelTitle!=null) panelTitle.text=ResultSkin.SongIdToDisplayTitle(ctl.SongIdAt(index));
        if (trackNumber!=null) trackNumber.text=$"{index+1:00} / {ctl.SongCount:00}";
        bool hasCover=CoverSprite(index)!=null;
        if (fallbackCover!=null) fallbackCover.SetActive(!hasCover);
        if (ctl.jacketImage!=null) ctl.jacketImage.enabled=hasCover;
        if (jacketLockedOverlay!=null) jacketLockedOverlay.SetActive(ctl.IsLocked(index));
        HandleDifficultyChanged(ctl.SelectedDifficultyIndex);
    }

    void HandleDifficultyChanged(int index)
    {
        if (ctl==null || ctl.difficultyNames==null || ctl.difficultyNames.Length==0) return;
        index=Mathf.Clamp(index,0,ctl.difficultyNames.Length-1);
        int level=ctl.CurrentDifficultyDisplayLevel();
        for(int i=0;i<difficultyItems.Count;i++)
        {
            if(difficultyItems[i]==null) continue;
            difficultyItems[i].SetLevel(ctl.DifficultyDisplayLevelAt(i));difficultyItems[i].SetSelected(i==index);
        }
        if(wheel!=null) wheel.RefreshLevels(index);
        if(masterWarning!=null) masterWarning.SetActive(index==2 && level>0);
        if(startStyle!=null) startStyle.Refresh();
        if(startDifficultyHint!=null)
            startDifficultyHint.text=ctl.SelectedSongLocked?"譜面準備中":level<=0?"この難易度の譜面はありません":$"{DifficultyDisplayName(index,ctl.difficultyNames[index])}  /  {FormatDifficultyLevel(level)}";
    }

    Sprite CoverSprite(int index)
    {
        if(coverCache.TryGetValue(index,out var cached)) return cached;
        Sprite sprite=null;
        string id=ctl!=null?ctl.SongIdAt(index):"";
        string path=Path.Combine(Application.streamingAssetsPath,"Songs",id,"cover.png");
        if(!string.IsNullOrEmpty(id) && File.Exists(path))
        {
            Texture2D texture=null;
            try
            {
                texture=new Texture2D(2,2);
                if(texture.LoadImage(File.ReadAllBytes(path)))
                    sprite=Sprite.Create(texture,new Rect(0,0,texture.width,texture.height),new Vector2(.5f,.5f),100);
                else UISkinKit.SafeDestroy(texture);
            }
            catch(System.Exception) { if(texture!=null) UISkinKit.SafeDestroy(texture); }
        }
        coverCache[index]=sprite; return sprite;
    }
}
