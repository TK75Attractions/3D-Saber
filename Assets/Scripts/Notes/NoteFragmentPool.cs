using System.Collections.Generic;
using UnityEngine;

// 破片だけの上限付き再利用。所有するメッシュと材質はシーン退出時にまとめて解放する。
public sealed class NoteFragmentPool : MonoBehaviour
{
    public const int Capacity = 128;
    readonly Stack<SlicePieceDecay> idle = new Stack<SlicePieceDecay>();
    public int CreatedCount { get; private set; }
    public int IdleCount => idle.Count;
    static Mesh cube;
    public static Mesh CubeMesh
    {
        get {
            if (cube != null) return cube;
            var temporary = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube = temporary.GetComponent<MeshFilter>().sharedMesh;
            temporary.SetActive(false); UISkinKit.SafeDestroy(temporary); return cube;
        }
    }
    public static SlicePieceDecay CreatePiece()
    {
        var go = new GameObject("NotePiece",typeof(MeshFilter),typeof(MeshRenderer),typeof(SlicePieceDecay));
        go.SetActive(false);
        return go.GetComponent<SlicePieceDecay>();
    }
    public void Prewarm(int count, Material material)
    {
        _ = CubeMesh;
        while (CreatedCount < Mathf.Min(Capacity,count))
        {
            var piece = Create(); piece.CopyMaterial(material); _ = piece.ReusableMesh;
            idle.Push(piece);
        }
        if(idle.Count >= 2) {
            var a=Rent(); var b=Rent();
            MeshSlicer.SliceInto(CubeMesh,new Plane(Vector3.right,Vector3.zero),a.ReusableMesh,b.ReusableMesh);
            a.Release(); b.Release();
        }
    }
    SlicePieceDecay Create()
    {
        var piece = CreatePiece(); piece.transform.SetParent(transform,false);
        piece.Pool = this; CreatedCount++; return piece;
    }
    public SlicePieceDecay Rent()
    {
        while(idle.Count > 0) { var piece=idle.Pop(); if(piece != null) { piece.PrepareForRent(); return piece; } }
        return Create();
    }
    internal void Return(SlicePieceDecay piece)
    {
        piece.gameObject.SetActive(false);
        if(idle.Count < Capacity) idle.Push(piece);
        else { CreatedCount--; UISkinKit.SafeDestroy(piece.gameObject); }
    }
}
