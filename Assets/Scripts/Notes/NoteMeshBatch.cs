using System.Collections.Generic;
using UnityEngine;

// 同じ材質の固定部品を、元の位置・法線・UVのまま結合する。生成時に一回だけ呼ぶ。
public sealed class NoteMeshBatch : MonoBehaviour
{
    readonly List<Mesh> owned = new List<Mesh>();
    public static void Combine(Transform parent, string name, Material material, params Transform[] parts)
    {
        if(parts.Length < 2) return;
        var combinations=new CombineInstance[parts.Length];
        for(int i=0;i<parts.Length;i++) combinations[i]=new CombineInstance {
            mesh=parts[i].GetComponent<MeshFilter>().sharedMesh,
            transform=parent.worldToLocalMatrix*parts[i].localToWorldMatrix
        };
        var mesh=new Mesh { name=name, hideFlags=HideFlags.DontSave };
        mesh.CombineMeshes(combinations,true,true);
        var go=new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer)); go.layer=parent.gameObject.layer;
        go.transform.SetParent(parent,false); go.GetComponent<MeshFilter>().sharedMesh=mesh;
        go.GetComponent<MeshRenderer>().sharedMaterial=material;
        var owner=parent.GetComponent<NoteMeshBatch>() ?? parent.gameObject.AddComponent<NoteMeshBatch>(); owner.owned.Add(mesh);
        foreach(var part in parts) { part.gameObject.SetActive(false); UISkinKit.SafeDestroy(part.gameObject); }
    }
    void OnDestroy() { foreach(var mesh in owned) UISkinKit.SafeDestroy(mesh); owned.Clear(); }
}
