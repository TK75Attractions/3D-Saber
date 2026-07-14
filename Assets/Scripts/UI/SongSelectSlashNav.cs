using UnityEngine;

// 曲選択画面の「切って曲送り」ナビノーツ。
// ↑ノーツを切ると前の曲(リスト上方向)、↓ノーツを切ると次の曲へ移動する(端はループ)。
// タイトル画面の TitleStartNote と同じ本物の CuttableNote を使うので、
// 実機セーバー(UDP)でもマウスの素振りでも本編と同じ感触で切れる。
// RequiredDirection 付きのため逆方向のスイングは無反応(↓を切るつもりの下振りが↑に誤爆しない)。
// 切られたノーツは破片演出の後、respawnDelay 秒で同じ場所に再出現する。
public class SongSelectSlashNav : MonoBehaviour
{
    public float respawnDelay = 0.5f;
    public float noteScale = 0.66f;
    public float bobAmplitude = 0.05f;
    public float bobHz = 0.5f;
    [Range(0f, 1f)] public float sfxVolume = 0.22f;

    // 配置(ビューポート座標 0-1)。曲リスト(左)と難易度パネル(右)の間の空き縦帯に置く。
    public Vector2 upViewport = new Vector2(0.565f, 0.615f);
    public Vector2 downViewport = new Vector2(0.565f, 0.385f);

    private SongSelectController ctl;
    private Camera cam;
    private CuttableNote upNote;
    private CuttableNote downNote;
    // -1 = 待機なし / 0以上 = 再出現までの残り秒
    private float upRespawnTimer = -1f;
    private float downRespawnTimer = -1f;
    private Vector3 upBasePos;
    private Vector3 downBasePos;
    private float age;
    private AudioSource sfx;

    public CuttableNote UpNote => upNote;
    public CuttableNote DownNote => downNote;

    // 曲選択画面へ組み込む。曲が2曲未満なら曲送り自体に意味が無いので何も作らない。
    public static SongSelectSlashNav Build(SongSelectController controller)
    {
        if (controller == null || controller.SongCount <= 1) return null;
        var mainCam = Camera.main;
        if (mainCam == null) return null;

        EnsureSaber();
        var go = new GameObject("SongSelectSlashNav");
        var nav = go.AddComponent<SongSelectSlashNav>();
        nav.Init(controller, mainCam);
        return nav;
    }

    // 実機セーバー(UDP)/マウス素振りの受け皿。タイトル画面(BuildTitleSaber)と同じ構成。
    private static void EnsureSaber()
    {
        if (Object.FindFirstObjectByType<SaberCutJudge>() != null) return;
        InputPoint.EnsureInstance();
        var saber = new GameObject("SongSelectSaber");
        var tracker = saber.AddComponent<SaberTracker>();
        var bridge = saber.AddComponent<SaberInputBridge>();
        bridge.useInputPoint = true;
        bridge.fallbackToMouse = true;
        bridge.fixedZ = 0f;
        var judge = saber.AddComponent<SaberCutJudge>();
        judge.saber = tracker;
        // メニューはボタン類も多いので、タイトルよりわずかに速い振りだけを「カット」と見なす
        judge.bladeRadius = 0.32f;
        judge.noteHitRadiusXY = 0.55f;
        judge.minCutSpeed = 3.0f;
    }

    // テストから直接呼べる初期化(EditMode では Awake が呼ばれないため、Build 経由でも明示的に呼ぶ)。
    public void Init(SongSelectController controller, Camera camera)
    {
        ctl = controller;
        cam = camera;
        sfx = GetComponent<AudioSource>();
        if (sfx == null) sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;

        upBasePos = ResolveWorldPos(upViewport);
        downBasePos = ResolveWorldPos(downViewport);
        upNote = SpawnNavNote(CutDirection.Up, upBasePos);
        downNote = SpawnNavNote(CutDirection.Down, downBasePos);
    }

    // ビューポート座標を、セーバーが動く z=0 平面上のワールド座標へ変換する。
    // カメラ位置がシーンによって違っても同じ画面位置に出るようにレイキャストで解く。
    private Vector3 ResolveWorldPos(Vector2 viewport)
    {
        Ray ray = cam.ViewportPointToRay(new Vector3(viewport.x, viewport.y, 0f));
        var plane = new Plane(Vector3.back, Vector3.zero);
        if (plane.Raycast(ray, out float enter)) return ray.GetPoint(enter);
        // 異常系(カメラが平面と平行など): カメラ前方 10m に置く
        return cam.transform.position + cam.transform.forward * 10f;
    }

    private CuttableNote SpawnNavNote(CutDirection dir, Vector3 pos)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = dir == CutDirection.Up ? "NavNoteUp" : "NavNoteDown";
        go.transform.position = pos;
        go.transform.localScale = Vector3.one * noteScale;

        var note = go.AddComponent<CuttableNote>();
        note.IsJudgeable = true;
        note.RequiredDirection = dir; // NoteVisuals が Direction 種(フリック色)として描く
        go.AddComponent<NoteVisuals>();
        NoteSpawner.BuildArrow(go.transform, dir); // 本編と同じシェブロン矢印

        note.OnCut += HandleNavCut;
        return note;
    }

    private void HandleNavCut(CuttableNote note, Vector3 point, Vector3 velocity)
    {
        bool isUp = note == upNote;
        MoveSelection(isUp ? -1 : +1);
        if (isUp) upRespawnTimer = respawnDelay;
        else downRespawnTimer = respawnDelay;
        PlayTick();
    }

    // 曲送り(端でループ)。キーボード↑↓の Move と同じ回り込み規則。
    public void MoveSelection(int delta)
    {
        if (ctl == null || ctl.SongCount == 0) return;
        ctl.Select(NextIndex(ctl.SelectedIndex, delta, ctl.SongCount));
    }

    // 純粋関数: 現在位置 + 移動量を曲数でラップする。count<=0 は -1(選択なし)。
    public static int NextIndex(int current, int delta, int count)
    {
        if (count <= 0) return -1;
        return ((current + delta) % count + count) % count;
    }

    void Update()
    {
        Tick(Time.unscaledDeltaTime);
    }

    // 再出現タイマーと浮遊アニメ。テストから直接呼べる。
    public void Tick(float dt)
    {
        age += dt;

        if (upRespawnTimer >= 0f)
        {
            upRespawnTimer -= dt;
            if (upRespawnTimer < 0f) upNote = SpawnNavNote(CutDirection.Up, upBasePos);
        }
        if (downRespawnTimer >= 0f)
        {
            downRespawnTimer -= dt;
            if (downRespawnTimer < 0f) downNote = SpawnNavNote(CutDirection.Down, downBasePos);
        }

        // 上下逆位相のゆっくりした浮遊で「切れるオブジェクト」感を出す
        float bob = Mathf.Sin(age * bobHz * 2f * Mathf.PI) * bobAmplitude;
        if (upNote != null && !upNote.IsCut)
            upNote.transform.position = upBasePos + new Vector3(0f, bob, 0f);
        if (downNote != null && !downNote.IsCut)
            downNote.transform.position = downBasePos + new Vector3(0f, -bob, 0f);
    }

    private void PlayTick()
    {
        // EditMode テスト中は音を出さない(オーディオ系は再生モード前提のため)
        if (!Application.isPlaying || sfx == null) return;
        sfx.PlayOneShot(JudgmentSfx.Beep(660f, 0.10f), sfxVolume);
    }

    void OnDestroy()
    {
        if (upNote != null) upNote.OnCut -= HandleNavCut;
        if (downNote != null) downNote.OnCut -= HandleNavCut;
    }
}
