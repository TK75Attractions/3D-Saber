using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class GManager : MonoBehaviour
{
    //GManager のシングルトンです。
    //シングルトンとは、クラスのインスタンスが一つしか存在しないことを保証するデザインパターンです。
    //static 変数は、クラス全体で共有される変数です。つまり、GManager クラスのどこからでもアクセスできます。
    //この場合、Control という static 変数は GManager クラスのインスタンスを保持します。これにより、他のクラスから GManager の機能に簡単にアクセスできます。
    static public GManager Control;

    public InputManager IManager;
    public GameObject PlayerPrefab;
    public PlayerController Player;


    //myName という public 変数は、Unity のインスペクターで表示され、値を設定できるようになります。これにより、GManager クラスのインスタンスに名前を付けることができます。
    public string myName = "Raymee675";

    //[SerializeField] は、Unity のインスペクター(右側のウィンドウ) で private 変数を表示するための属性です。
    //これにより、time 変数は private であるにもかかわらず、Unity エディタのインスペクターで値を設定できるようになります。
    //private 変数は、クラスの外部からアクセスできない変数です。これにより、time 変数は GManager クラスの内部でのみアクセスできます。
    [SerializeField] private float time = 0;
    [SerializeField] private bool logOn = false;

    public void Awake()
    {
        //シングルトンが存在するかどうかを確認し、存在しない場合はこのインスタンスを Control に割り当てます。すでに存在する場合は、このインスタンスを破棄します。
        if (Control == null)
        {
            Control = this;
            DontDestroyOnLoad(this.transform.parent.gameObject);
            Application.targetFrameRate = 30;
        }
        else
        {
            //Destroy 関数は、ゲームオブジェクトを破壊するための関数です。
            //Destroy(GameObject obj) と呼び出すと、指定されたゲームオブジェクトがシーンから削除されます。 
            Destroy(this.gameObject);
            return;
        }

        //GetComponent<クラス名>() は、ゲームオブジェクトにアタッチされている指定されたクラスのコンポーネントを取得するための関数です。
        //この場合、GManager クラスのインスタンスにアタッチされている InputManager クラスのコンポーネントを取得し、IManager 変数に割り当てます。
        IManager = GetComponent<InputManager>();

        //Instantiate 関数は、ゲームオブジェクトのインスタンスを生成するための関数です。
        //この場合、PlayerPrefab というゲームオブジェクトのインスタンスを生成し、Player 変数に割り当てます。
        Transform playerTransform = Instantiate(PlayerPrefab).transform;
        Player = playerTransform.GetComponent<PlayerController>();
        Player.Init();

        //Debug.Log 関数は、Unity のコンソールにメッセージを表示するための関数です。これを使用して、コードの実行状況や変数の値などを確認できます。
        Debug.Log($"Hello World {myName}!");
        Debug.Log("Awake はオブジェクトが生成されたときに一度だけ呼ばれる");
    }

    public void Update()
    {
        //Time は Unity の組み込みクラスで、ゲームの時間に関する情報を提供します。deltaTime は、前のフレームから現在のフレームまでの時間を秒単位返します。

        float dt = Time.deltaTime;
        time += dt;
        if (logOn)
        {
            Debug.Log($"Update は毎フレーム呼ばれる. 現在の時間は {time} 秒");
        }

        //InputManager の UpdateInput 関数を呼び出して、入力を検出します。
        if (IManager != null)
        {
            IManager.UpdateInput();
        }

        //PlayerController の UpdatePlayer 関数を呼び出して、プレイヤーの状態を更新します。
        if (Player != null)
        {
            Player.UpdatePlayer(dt);
        }
    }

    //LateUpdate は Update が実行された後に、毎フレーム呼ばれる
    public void LateUpdate()
    {
        if (logOn)
        {
            Debug.Log("LateUpdate は Update の後に毎フレーム呼ばれる");
        }
    }
}

