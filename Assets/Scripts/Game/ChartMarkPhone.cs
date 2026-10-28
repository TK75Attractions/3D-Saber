using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

// スマホのブラウザから譜面の印を付け、一言を書く(2026-10-28、ユーザー「AI指摘さ、スマホでもできるようにはできる？」)。
// LAN に小さな操作盤(ChartMarkPhoneServer、ポート 5020〜5029)を出し、受けた操作をここ(メインスレッドの Update)で
// 土台の ChartMarkRecorder.TryMark("phone")・ChartMarks.SetComment・SetCanceled に渡す。印は F5 と同じ入口なので、同じファイルに入る。
// アドレスは http://<この PC の LAN の IPv4>:<ポート>/m?t=<合言葉>(合言葉は PlayerPrefs に残して変えない)。起動時にログへ出し、
// ChartMarkPhoneLink.Url に入れる(結果画面の一言の欄の下に出る)。来場者の画面には何も出さない。
// テスト中(PhoneSaberP2PBridgeProcess.AutoStartEnabled が false)とバッチ実行では作らない。
public sealed class ChartMarkPhone : MonoBehaviour
{
    public const string TokenKey = "ChartMarks.PhoneToken.v1";
    public const int MaxJobsPerFrame = 8;
    // この回の印(最大 30)と、前の回の印(最近の 20 件)。
    public const int MaxMarks = ChartMarks.MaxMarksPerPlay + ChartMarkPhonePage.PastMarks;
    // 曲の外では、印の一覧と数をフォルダーから 1 秒ごとに読み直す(スマホからの操作のあとと、場面が変わったときはすぐ)。
    // 演奏中はフォルダーを読まない(処理落ちを避ける)。曲の前に読んだ一覧と数に、この回の印(メモリ)を足して使い、曲が終わったら読み直す。
    public const float MarksRefreshSeconds = 1f;
    public const float AddressRefreshSeconds = 5f;

    public static ChartMarkPhone Instance { get; private set; }
    // スマホで開くアドレス。LAN の IPv4 が見つからないときは null。
    public static string Url { get; private set; }

    ChartMarkPhoneServer server;
    string station = "PC";
    // 曲の外でフォルダーから読んだ一覧(Recent)と数(OpenCountOnThisPc)。数の -1 はまだ読んでいない。
    List<ChartMarkSummary> folderMarks = new List<ChartMarkSummary>();
    int folderOpen = -1;
    float folderNextAt;
    bool folderDirty = true;
    string lastPlayState;
    // 印の送り直し(同じ要求 id)は、もう一度付けずに同じ返事を返す。
    readonly ChartMarkPhoneReplies markReplies = new ChartMarkPhoneReplies();
    float addressNextAt;
    bool lookingUp;
    // 別のスレッドで調べた番地(よい順に "\n" 区切り。見つからなければ "")。null はまだ。
    volatile string foundAddresses;
    string lastLogged;

    public ChartMarkPhoneServer Server => server;
    public int Port => server != null ? server.Port : 0;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Instance = null;
        Url = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void CreateAtStartup()
    {
        if (!PhoneSaberP2PBridgeProcess.AutoStartEnabled || Application.isBatchMode) return; // テスト中・バッチ実行では作らない
        Create(LoadOrCreateToken());
    }

    // 合言葉とポートを決めて作る。始められなければ null。
    // テストは決まった合言葉・ポート 0・ループバック(IPAddress.Loopback)を渡し、PlayerPrefs に触れず、LAN にも出さない。
    public static ChartMarkPhone Create(string token, int firstPort = ChartMarkPhoneServer.DefaultFirstPort,
        int lastPort = ChartMarkPhoneServer.DefaultLastPort, IPAddress address = null)
    {
        if (Instance != null) return Instance;
        var go = new GameObject("ChartMarkPhone");
        DontDestroyOnLoad(go);
        var phone = go.AddComponent<ChartMarkPhone>();
        if (phone.Begin(token, firstPort, lastPort, address)) return phone;
        Destroy(go);
        return null;
    }

    // 合言葉は一度作ったら変えない(スマホでブックマークしたアドレスが次も使える)。無い・壊れているときだけ作る。
    static string LoadOrCreateToken()
    {
        string token = PlayerPrefs.GetString(TokenKey, "");
        if (ChartMarkPhoneServer.IsValidToken(token)) return token;
        token = ChartMarkPhoneServer.NewToken();
        PlayerPrefs.SetString(TokenKey, token);
        PlayerPrefs.Save();
        return token;
    }

    bool Begin(string token, int firstPort, int lastPort, IPAddress address)
    {
        try { server = new ChartMarkPhoneServer(token); }
        catch (ArgumentException e)
        {
            Debug.LogWarning("[ChartMarks] スマホの印を始められません: " + e.Message);
            return false;
        }
        if (!server.Start(firstPort, lastPort, address))
        {
            Debug.LogWarning("[ChartMarks] スマホの印を始められません(ポート " + firstPort + "〜" + lastPort + "): " + server.Error);
            server.Dispose();
            server = null;
            return false;
        }
        Instance = this;
        string name = PhoneSaberStation.Read();
        station = string.IsNullOrEmpty(name) ? "PC" : name;
        LookUpAddress();
        return true;
    }

    void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
    void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    // 場面が変わったら一覧を読み直し(演奏中を除く)、アドレスも調べ直す(あとからホットスポットを入れた・Wi-Fi が変わった)。
    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        folderDirty = true;
        if (Time.unscaledTime >= addressNextAt) LookUpAddress();
    }

    void LookUpAddress()
    {
        if (lookingUp || server == null) return;
        lookingUp = true;
        addressNextAt = Time.unscaledTime + AddressRefreshSeconds;
        // ループバックで待ち受けるとき(テスト)は、LAN のアドレスを出さない。
        if (IPAddress.IsLoopback(server.ListenAddress))
        {
            foundAddresses = "";
            return;
        }
        // 網の一覧は時間がかかることがあるので、別のスレッドで調べる(遊びを止めない)。
        Task.Run(() =>
        {
            string found = "";
            try { found = string.Join("\n", ChartMarkPhoneServer.RankAddresses(ChartMarkPhoneServer.LocalAddresses())); }
            catch (Exception) { }
            foundAddresses = found;
        });
    }

    void Update()
    {
        if (server == null) return;
        string found = foundAddresses;
        if (found != null)
        {
            foundAddresses = null;
            lookingUp = false;
            ApplyAddresses(found);
        }
        // キューは 1 コマに 8 件まで。土台の関数(PlayerPrefs・Unity の API・ファイルの書き込み)はここからだけ呼ぶ。
        for (int i = 0; i < MaxJobsPerFrame && server.TryDequeue(out var job); i++)
        {
            if (!job.TryBegin()) continue; // スマホ側が待ちきれずに取りやめた操作は実行しない
            try { Execute(job); }
            catch (Exception e)
            {
                job.Complete(500, ChartMarkPhoneServer.Result(false, "ゲームの中で失敗しました(" + e.GetType().Name + ")"));
                Debug.LogWarning("[ChartMarks] スマホからの操作で失敗しました: " + e);
            }
        }
        if (!server.Running)
        {
            Debug.LogWarning("[ChartMarks] スマホの印が止まりました: " + server.Error);
            Stop();
        }
    }

    void ApplyAddresses(string found)
    {
        string[] addresses = found.Length > 0 ? found.Split('\n') : new string[0];
        string url = addresses.Length > 0 ? ChartMarkPhoneServer.PhoneUrl(addresses[0], server.Port, server.Token) : null;
        Url = url;
        ChartMarkPhoneLink.Url = url;
        if ((url ?? "") == lastLogged) return;
        lastLogged = url ?? "";
        if (url == null)
            Debug.Log("[ChartMarks] スマホで印: LAN の IPv4 が見つかりません(待ち受けはポート " + server.Port +
                      "。同じ Wi-Fi かホットスポットにつなぐと出ます)");
        else
            Debug.Log("[ChartMarks] スマホで印: " + url +
                      (addresses.Length > 1 ? "(ほかの番地: " + string.Join(", ", addresses, 1, addresses.Length - 1) + ")" : "") +
                      "。つながらないときは、この PC のファイアウォールで TCP " + server.Port + " の受信を許可");
    }

    void Execute(ChartMarkPhoneJob job)
    {
        var request = job.Request;
        bool ok;
        string message;
        switch (request.Action)
        {
            case ChartMarkPhoneAction.State:
                job.Complete(200, BuildState());
                return;
            case ChartMarkPhoneAction.Mark:
                // 返事が届かずにスマホが同じ要求 id で送り直したときは、もう一度付けずに前の返事を返す(二重の印を作らない)。
                if (markReplies.TryGet(request.RequestId, out string again))
                {
                    job.Complete(200, again);
                    return;
                }
                var recorder = ChartMarkRecorder.Instance;
                if (recorder == null)
                {
                    ok = false;
                    message = "印の部品が動いていません";
                }
                else ok = recorder.TryMark(ChartMarks.SourcePhone, out message);
                string reply = ChartMarkPhoneServer.Result(ok, message);
                markReplies.Remember(request.RequestId, reply);
                folderDirty = true;
                job.Complete(200, reply);
                return;
            case ChartMarkPhoneAction.Comment:
                ok = ChartMarks.SetComment(request.Id, request.Text, out message);
                if (ok)
                {
                    message = "保存しました";
                    string comment = ChartMarks.Clean(request.Text);
                    Patch(request.Id, row => row.comment = comment);
                }
                break;
            case ChartMarkPhoneAction.Cancel:
                ok = ChartMarks.SetCanceled(request.Id, request.Canceled, out message);
                if (ok)
                {
                    message = request.Canceled ? "取り消しました" : "戻しました";
                    string status = request.Canceled ? ChartMarks.StatusCanceled : ChartMarks.StatusOpen;
                    Patch(request.Id, row =>
                    {
                        bool wasOpen = IsOpen(row.status), nowOpen = !request.Canceled;
                        if (folderOpen >= 0 && wasOpen != nowOpen) folderOpen += nowOpen ? 1 : -1;
                        row.status = status;
                    });
                }
                break;
            default:
                job.Complete(404, ChartMarkPhoneServer.Result(false, ChartMarkPhoneServer.StatusMessage(404)));
                return;
        }
        folderDirty = true;
        job.Complete(200, ChartMarkPhoneServer.Result(ok, message));
    }

    // 演奏中はフォルダーを読み直さないので、前の回の印への一言・取り消しは、覚えている一覧にも写しておく。
    void Patch(string id, Action<ChartMarkSummary> change)
    {
        foreach (var row in folderMarks)
            if (row != null && row.id == id) change(row);
    }

    static bool IsOpen(string status) => string.IsNullOrEmpty(status) || status == ChartMarks.StatusOpen;

    string BuildState()
    {
        string state = ChartMarkRecorder.PlayState;
        bool playing = state == ChartMarkRecorder.StatePlaying;
        if (lastPlayState == ChartMarkRecorder.StatePlaying && !playing) folderDirty = true; // 曲が終わったら読み直す
        lastPlayState = state;
        List<ChartMarkSummary> list;
        int open;
        if (playing)
        {
            list = MergePlaying(ChartMarks.Current, folderMarks, out int currentOpen);
            open = folderOpen < 0 ? -1 : folderOpen + currentOpen;
        }
        else
        {
            float now = Time.unscaledTime;
            if (folderDirty || now >= folderNextAt)
            {
                folderMarks = ChartMarks.Recent(MaxMarks) ?? new List<ChartMarkSummary>();
                folderOpen = ChartMarks.OpenCountOnThisPc();
                folderDirty = false;
                folderNextAt = now + MarksRefreshSeconds;
            }
            list = folderMarks;
            open = folderOpen;
        }
        return StateJson(GameSession.SelectedSongId, GameSession.SelectedSongTitle, GameSession.SelectedDifficulty, state,
            ChartMarkRecorder.Instance != null, station, open, list);
    }

    // 演奏中の一覧: この回の印(メモリの ChartMarks.Current、新しい順)のあとに、曲の前に読んだ印を「前の回」として並べる。
    // 曲の前に読んだ一覧には、そのときの「この回」(ひとつ前の曲)が current=true で入っているので、前の回に直す。
    // currentOpen は、この回の直していない印(取り消していないもの)の数。
    public static List<ChartMarkSummary> MergePlaying(ChartMarkFile current, IList<ChartMarkSummary> before, out int currentOpen)
    {
        var list = new List<ChartMarkSummary>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        currentOpen = 0;
        if (current?.marks != null)
            for (int i = current.marks.Count - 1; i >= 0; i--)
            {
                var mark = current.marks[i];
                if (mark == null || mark.status == ChartMarks.StatusFixed) continue;
                list.Add(new ChartMarkSummary
                {
                    id = mark.id ?? "", song = current.songId ?? "", title = current.songTitle ?? "", difficulty = current.difficulty ?? "",
                    station = string.IsNullOrEmpty(current.station) ? "PC" : current.station,
                    number = mark.number, songSeconds = mark.songTime, bar = mark.bar, comment = mark.comment ?? "",
                    status = string.IsNullOrEmpty(mark.status) ? ChartMarks.StatusOpen : mark.status,
                    source = string.IsNullOrEmpty(mark.source) ? ChartMarks.SourceKey : mark.source,
                    current = true, savedAt = mark.markedAt ?? ""
                });
                ids.Add(mark.id ?? "");
                if (IsOpen(mark.status)) currentOpen++;
            }
        if (before != null)
            foreach (var row in before)
            {
                if (row == null || ids.Contains(row.id ?? "")) continue;
                list.Add(new ChartMarkSummary
                {
                    id = row.id, song = row.song, title = row.title, difficulty = row.difficulty, station = row.station,
                    number = row.number, songSeconds = row.songSeconds, bar = row.bar, comment = row.comment, status = row.status,
                    source = row.source, current = false, savedAt = row.savedAt
                });
            }
        return list;
    }

    // スマホの画面が 1 秒ごとに読む状態:
    // {"song","title","difficulty","state","ready","station","open","marks":[{id,song,title,difficulty,station,number,songSeconds,
    // bar,comment,status,source,current,savedAt}]}。open は数を読んでいないとき null。前の回の印は最近の ChartMarkPhonePage.PastMarks 件まで。
    public static string StateJson(string song, string title, string difficulty, string state, bool ready, string station,
        int open, IList<ChartMarkSummary> marks)
    {
        var json = new StringBuilder(256 + 320 * (marks?.Count ?? 0));
        json.Append("{\"song\":").Append(Q(song)).Append(",\"title\":").Append(Q(title))
            .Append(",\"difficulty\":").Append(Q(difficulty)).Append(",\"state\":").Append(Q(state))
            .Append(",\"ready\":").Append(ready ? "true" : "false").Append(",\"station\":").Append(Q(station))
            .Append(",\"open\":").Append(open < 0 ? "null" : open.ToString(CultureInfo.InvariantCulture)).Append(",\"marks\":[");
        int past = 0;
        bool first = true;
        if (marks != null)
            foreach (var mark in marks)
            {
                if (mark == null || !mark.current && ++past > ChartMarkPhonePage.PastMarks) continue;
                if (!first) json.Append(',');
                first = false;
                json.Append("{\"id\":").Append(Q(mark.id)).Append(",\"song\":").Append(Q(mark.song))
                    .Append(",\"title\":").Append(Q(mark.title)).Append(",\"difficulty\":").Append(Q(mark.difficulty))
                    .Append(",\"station\":").Append(Q(mark.station))
                    .Append(",\"number\":").Append(mark.number.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"songSeconds\":").Append(Number(mark.songSeconds))
                    .Append(",\"bar\":").Append(mark.bar.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"comment\":").Append(Q(mark.comment)).Append(",\"status\":").Append(Q(mark.status))
                    .Append(",\"source\":").Append(Q(mark.source)).Append(",\"current\":").Append(mark.current ? "true" : "false")
                    .Append(",\"savedAt\":").Append(Q(mark.savedAt)).Append('}');
            }
        return json.Append("]}").ToString();
    }

    static string Q(string value) => PhoneSaberControlPanelState.Quote(value ?? "");

    static string Number(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? "null" : value.ToString("0.###", CultureInfo.InvariantCulture);

    void Stop()
    {
        server?.Dispose();
        server = null;
        if (Url != null && ChartMarkPhoneLink.Url == Url) ChartMarkPhoneLink.Url = null;
        Url = null;
    }

    void OnDestroy()
    {
        Stop();
        if (Instance == this) Instance = null;
    }
}
