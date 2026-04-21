using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class SongSelectController : MonoBehaviour
{
    public string gameSceneName = "Game";
    public Transform listRoot;
    public GameObject buttonPrefab;

    void Start()
    {
        Populate();
    }

    public List<string> EnumerateSongIds()
    {
        string root = Path.Combine(Application.streamingAssetsPath, "Songs");
        if (!Directory.Exists(root)) return new List<string>();
        List<string> result = new List<string>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            string chart = Path.Combine(dir, "chart.json");
            if (File.Exists(chart))
            {
                result.Add(Path.GetFileName(dir));
            }
        }
        result.Sort();
        return result;
    }

    private void Populate()
    {
        if (listRoot == null || buttonPrefab == null) return;
        foreach (string id in EnumerateSongIds())
        {
            GameObject go = Instantiate(buttonPrefab, listRoot);
            Button btn = go.GetComponent<Button>();
            Text label = go.GetComponentInChildren<Text>();
            if (label != null) label.text = id;
            string captured = id;
            if (btn != null) btn.onClick.AddListener(() => Select(captured));
        }
    }

    public void Select(string songId)
    {
        GameSession.SelectedSongId = songId;
        GameSession.SelectedSongTitle = songId;
        GameSession.ResetResult();
        SceneManager.LoadScene(gameSceneName);
    }
}
