using NUnit.Framework;
using UnityEngine;

public class MeshSliceReuseTests
{
    [Test] public void ReusedOutputsContainBothSubmeshesAndStayOnTheirPlaneSides()
    {
        var cube=GameObject.CreatePrimitive(PrimitiveType.Cube);
        var source=Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh);
        var above=new Mesh(); var below=new Mesh();
        try {
            var triangles=source.triangles; source.subMeshCount=2;
            var first=new int[18]; var last=new int[18];
            System.Array.Copy(triangles,0,first,0,18); System.Array.Copy(triangles,18,last,0,18);
            source.SetTriangles(first,0); source.SetTriangles(last,1);
            for(int i=0;i<36;i++) {
                var normal=Quaternion.Euler(0,0,i*10)*Vector3.right;
                var plane=new Plane(normal,normal*.1f);
                Assert.True(MeshSlicer.SliceInto(source,plane,above,below));
                foreach(var vertex in above.vertices) Assert.GreaterOrEqual(plane.GetDistanceToPoint(vertex),-.0001f);
                foreach(var vertex in below.vertices) Assert.LessOrEqual(plane.GetDistanceToPoint(vertex),.0001f);
                var bounds=above.bounds; bounds.Encapsulate(below.bounds);
                Assert.Less((bounds.size-source.bounds.size).magnitude,.001f,"前回の切断データを残さず、全ての面を分割する");
            }
        } finally { Object.DestroyImmediate(source); Object.DestroyImmediate(above); Object.DestroyImmediate(below); Object.DestroyImmediate(cube); }
    }
}
