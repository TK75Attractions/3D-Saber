using UnityEngine;

// 自然物・石造建築のための低ポリゴン形状。すべて実メッシュで、Collider は作らない。
internal sealed partial class StageGeometry
{
    public void Frustum(Vector3 center, float bottomRadius, float topRadius, float height, int sides = 12, Quaternion rotation = default)
    {
        if (rotation.Equals(default(Quaternion))) rotation = Quaternion.identity;
        Vector3 bottom = center + rotation * new Vector3(0, -height * .5f, 0);
        Vector3 top = center + rotation * new Vector3(0, height * .5f, 0);
        for (int i = 0; i < sides; i++)
        {
            float a = i * Mathf.PI * 2 / sides, b = (i + 1) * Mathf.PI * 2 / sides;
            Vector3 p = bottom + rotation * new Vector3(Mathf.Cos(a) * bottomRadius, 0, Mathf.Sin(a) * bottomRadius);
            Vector3 q = bottom + rotation * new Vector3(Mathf.Cos(b) * bottomRadius, 0, Mathf.Sin(b) * bottomRadius);
            Vector3 r = top + rotation * new Vector3(Mathf.Cos(a) * topRadius, 0, Mathf.Sin(a) * topRadius);
            Vector3 s = top + rotation * new Vector3(Mathf.Cos(b) * topRadius, 0, Mathf.Sin(b) * topRadius);
            Quad(p, r, s, q); Triangle(bottom, p, q); Triangle(top, s, r);
        }
    }

    public void Rock(Vector3 center, Vector3 scale, int segments = 9, int rings = 5, Quaternion rotation = default)
    {
        if (rotation.Equals(default(Quaternion))) rotation = Quaternion.identity;
        Vector3 Point(int row, int col)
        {
            float latitude = Mathf.PI * row / rings;
            float longitude = Mathf.PI * 2 * col / segments;
            float ripple = 1 + .075f * Mathf.Sin(col * 4.17f + row * 2.61f);
            var p = new Vector3(Mathf.Sin(latitude) * Mathf.Cos(longitude), Mathf.Cos(latitude), Mathf.Sin(latitude) * Mathf.Sin(longitude));
            return center + rotation * Vector3.Scale(p * ripple, scale);
        }
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < segments; s++)
            {
                Vector3 a = Point(r, s), b = Point(r, (s + 1) % segments), c = Point(r + 1, s), d = Point(r + 1, (s + 1) % segments);
                if (r != 0) Triangle(a, b, c);
                if (r != rings - 1) Triangle(b, d, c);
            }
    }

    public void Crystal(Vector3 basePoint, float radius, float height, Quaternion rotation)
    {
        Frustum(basePoint + rotation * new Vector3(0, height * .36f, 0), radius, radius * .83f, height * .72f, 6, rotation);
        Frustum(basePoint + rotation * new Vector3(0, height * .86f, 0), radius * .83f, 0, height * .28f, 6, rotation);
    }

    // 雲・月・惑星は岩と区別し、滑らかな輪郭と法線を持つ楕円体にする。
    public void Ellipsoid(Vector3 center,Vector3 radius,int segments=24,int rings=16)
    {
        int start=vertices.Count;
        for(int row=0;row<=rings;row++)
            for(int col=0;col<=segments;col++)
            {
                float a=Mathf.PI*row/rings,b=Mathf.PI*2*col/segments;
                Vector3 unit=new Vector3(Mathf.Sin(a)*Mathf.Cos(b),Mathf.Cos(a),Mathf.Sin(a)*Mathf.Sin(b));
                vertices.Add(center+Vector3.Scale(unit,radius));
                normals.Add(new Vector3(unit.x/radius.x,unit.y/radius.y,unit.z/radius.z).normalized);
            }
        for(int row=0;row<rings;row++)
            for(int col=0;col<segments;col++)
            {
                int a=start+row*(segments+1)+col,b=a+1,c=a+segments+1,d=c+1;
                if(row!=0) { triangles.Add(a);triangles.Add(b);triangles.Add(c); }
                if(row!=rings-1) { triangles.Add(b);triangles.Add(d);triangles.Add(c); }
            }
    }

    public void Torus(Vector3 center, float radius, float tube, Quaternion rotation, float startDegrees = 0, float endDegrees = 360, int segments = 64)
    {
        const int cross = 5;
        Vector3 Point(int s, int c)
        {
            float a = Mathf.Lerp(startDegrees, endDegrees, s / (float)segments) * Mathf.Deg2Rad;
            float b = c * Mathf.PI * 2 / cross;
            return center + rotation * new Vector3(Mathf.Cos(a) * (radius + Mathf.Cos(b) * tube), Mathf.Sin(a) * (radius + Mathf.Cos(b) * tube), Mathf.Sin(b) * tube);
        }
        for (int i = 0; i < segments; i++)
            for (int c = 0; c < cross; c++)
                Quad(Point(i, c), Point(i + 1, c), Point(i + 1, c + 1), Point(i, c + 1));
    }

    public void Leaf(Vector3 root, Vector3 tip, float width, Vector3 normal)
    {
        Vector3 across = Vector3.Cross((tip - root).normalized, normal.normalized) * width;
        Vector3 middle = Vector3.Lerp(root, tip, .44f) + normal * width * .35f;
        Triangle(root, middle + across, tip); Triangle(root, tip, middle - across);
        Triangle(root, tip, middle + across); Triangle(root, middle - across, tip);
    }

    public void Banner(Vector3 top, float width, float height, float tilt = 0)
    {
        const int rows = 12, cols = 5;
        Vector3 Point(int row, int col)
        {
            float t = row / (float)rows, u = col / (float)cols;
            return top + new Vector3((u - .5f) * width + tilt * t, -t * height, Mathf.Sin(t * 3 + u * 2) * .08f * t);
        }
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                Quad(Point(r, c), Point(r + 1, c), Point(r + 1, c + 1), Point(r, c + 1));
                Quad(Point(r, c + 1), Point(r + 1, c + 1), Point(r + 1, c), Point(r, c));
            }
    }

    public void Dune(Vector3 center, Vector3 size, float seed)
    {
        const int segments = 14;
        Vector3 Point(int x, int z)
        {
            float u = x / (float)segments, v = z / (float)segments;
            float h = Mathf.Sin(u * Mathf.PI) * Mathf.Sin(v * Mathf.PI) * (.66f + .34f * Mathf.Sin(u * 7 + v * 3 + seed));
            return center + new Vector3((u - .5f) * size.x, h * size.y, (v - .5f) * size.z);
        }
        for (int x = 0; x < segments; x++)
            for (int z = 0; z < segments; z++)
                Quad(Point(x, z), Point(x, z + 1), Point(x + 1, z + 1), Point(x + 1, z));
    }
}
