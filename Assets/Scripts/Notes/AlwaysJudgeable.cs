using UnityEngine;

// 実験A専用：スポーン時に CuttableNote.IsJudgeable を常時 true にする。
// 本編では NoteSpawner が時刻ウィンドウで制御するので、このスクリプトは外す。
public class AlwaysJudgeable : MonoBehaviour
{
    void Awake()
    {
        var n = GetComponent<CuttableNote>();
        if (n != null) n.IsJudgeable = true;
    }
}
