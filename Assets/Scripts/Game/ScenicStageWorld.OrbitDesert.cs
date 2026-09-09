using UnityEngine;

public sealed partial class ScenicStageWorld
{
    private void BuildOrbit()
    {
        Sky(new Color(.018f,.023f,.065f),new Color(.057f,.05f,.11f),new Color(.22f,.10f,.29f));
        var path = Surface("MeteoritePath",new Color(.085f,.095f,.145f));
        var trim = Surface("SilverInclusions",new Color(.29f,.32f,.39f));
        var rocks = Surface("OrbitingMeteorites",new Color(.17f,.16f,.225f));
        var planet = Surface("BandedPlanet",new Color(.19f,.215f,.32f),4,0,new Color(.43f,.30f,.30f));
        var rings = Surface("CelestialRings",new Color(.29f,.25f,.39f),0,.16f,new Color(.32f,.3f,.46f));
        var stars = Surface("DistantStars",new Color(.30f,.38f,.49f),0,.25f);
        Path(path,trim,1);
        G(planet).Ellipsoid(new Vector3(17,12,87),new Vector3(11,11,11),40,28);
        G(planet).Ellipsoid(new Vector3(-27,17,108),new Vector3(5.8f,5.8f,5.8f),32,20);
        for (int i = 0; i < 3; i++)
        {
            var ring = new StageGeometry(); ring.Torus(Vector3.zero,15.5f+i*1.5f,.08f,Quaternion.identity,0,360,100);
            Moving("PlanetaryRing"+i,ring,rings,new Vector3(17,12,87),Vector3.zero,new Vector3(1.5f+i*.4f,2.3f-i*.4f,0),.2f,i,
                rotation:Quaternion.Euler(63+i*12,-18+i*21,24+i*29));
        }
        // 手前の大きな環は通路外。遠景の惑星と異なる軸でゆっくり回す。
        foreach (int side in new[] {-1,1})
        {
            var orbit = new StageGeometry(); orbit.Torus(Vector3.zero,3.2f,.06f,Quaternion.identity,0,360,64);
            Moving("NearCelestialRing"+side,orbit,rings,new Vector3(side*10.5f,floor+3.3f,16),new Vector3(0,.20f,0),new Vector3(0,side*5,side*3),.3f,side,
                rotation:Quaternion.Euler(20,side*28,side*20));
            for (int i = 0; i < 5; i++)
            {
                var asteroid = new StageGeometry();
                asteroid.Rock(Vector3.zero,new Vector3(1.0f,.72f,1.2f),7,4);
                Moving("OrbitingRock"+side+"-"+i,asteroid,rocks,new Vector3(side*(8.1f+i*.9f),floor+.9f+(i%3)*.8f,3+i*8),
                    new Vector3(.15f,.28f,.15f),new Vector3(3+i,6-i,2),.28f,i);
            }
        }
        for (int i = 0; i < 170; i++)
        {
            float x = Mathf.Sin(i*2.39996f)*(35+i%25), y = 5+(i%31)*1.5f, z = 70+(i%5)*10;
            float r = .028f+(i%4)*.012f;
            G(stars).Rock(new Vector3(x,y,z),Vector3.one*r,5,3);
        }
    }

    private void BuildDesert()
    {
        Sky(new Color(.14f,.12f,.15f),new Color(.30f,.245f,.18f),new Color(.48f,.36f,.24f));
        var path = Surface("DesertPaving",new Color(.245f,.205f,.155f));
        var sandstone = Surface("CarvedSandstone",new Color(.41f,.315f,.205f));
        var trim = Surface("TempleInscriptions",new Color(.24f,.18f,.11f));
        var sand = Surface("Dunes",new Color(.37f,.295f,.21f));
        var cloth = Surface("DesertBanners",new Color(.24f,.22f,.29f),0,0,null,-1);
        var gold = Surface("AncientGold",new Color(.39f,.30f,.16f));
        Path(path,trim,0);
        foreach (int side in new[] {-1,1})
        {
            for (int i = 0; i < 4; i++)
            {
                float z = 4+i*11;
                Vector3 basePoint = new Vector3(side*8.0f,floor,z);
                G(sandstone).Box(basePoint+Vector3.up*.22f,new Vector3(2.3f,.44f,2.3f));
                G(sandstone).Frustum(basePoint+Vector3.up*2.7f,.70f,.48f,5.0f,4,Quaternion.Euler(0,45,0));
                G(sandstone).Frustum(basePoint+Vector3.up*5.65f,.48f,0,.9f,4,Quaternion.Euler(0,45,0));
                for (int mark = 0; mark < 6; mark++)
                {
                    float y = floor+1.1f+mark*.55f;
                    G(gold).Box(new Vector3(side*7.42f,y,z),new Vector3(.025f,.08f,.43f));
                    G(trim).Box(new Vector3(side*7.41f,y+.17f,z+(mark%2==0?.1f:-.1f)),new Vector3(.027f,.17f,.065f));
                }
                G(sand).Dune(new Vector3(side*18,floor-.3f,z+4),new Vector3(17,3.6f,22),i+side);
                G(sandstone).Box(new Vector3(side*8.4f,floor+6.15f,z),new Vector3(3.2f,.45f,2.6f));
                if (i < 3)
                {
                    G(gold).Beam(new Vector3(side*8.4f,floor+6,z),new Vector3(side*10.5f,floor+6,z),.065f,.065f);
                    G(cloth).Banner(new Vector3(side*9.3f,floor+6,z-.5f),1.6f,2.8f,side*.13f);
                }
            }
            // 守護像は面取りした石の塊として構成し、工業設備とは違う輪郭にする。
            for (int i = 0; i < 2; i++)
            {
                Vector3 p = new Vector3(side*11.6f,floor,12+i*24);
                G(sandstone).Box(p+Vector3.up*.3f,new Vector3(3.2f,.6f,3.7f));
                G(sandstone).Frustum(p+Vector3.up*1.9f,1.25f,.92f,2.7f,6);
                G(sandstone).Rock(p+Vector3.up*3.9f,new Vector3(1.1f,1.3f,1.0f),8,6);
                G(sandstone).Box(p+new Vector3(0,3.5f,-.8f),new Vector3(.38f,.66f,.52f));
                G(trim).Box(p+new Vector3(0,4.12f,-.92f),new Vector3(1.15f,.13f,.11f));
                G(gold).Frustum(p+Vector3.up*5.1f,1.16f,.53f,.70f,6);
            }
            for (int i = 0; i < 3; i++)
            {
                var stone = new StageGeometry(); stone.Rock(Vector3.zero,new Vector3(.70f,.47f,.65f),7,4);
                Moving("SuspendedSandstone"+side+"-"+i,stone,sandstone,new Vector3(side*(8.4f+i*.35f),floor+2.1f+i*.45f,9+i*13),
                    new Vector3(.13f,.19f,.05f),new Vector3(0,6+i*2,3),.43f,i+side);
            }
        }
        // 遠景の古代門と段状ピラミッド。接近ノーツの生成位置より奥に置く。
        for (int level = 0; level < 9; level++)
            G(sandstone).Box(new Vector3(0,floor+level*.85f,70),new Vector3(29-level*2.7f,.86f,19-level*1.4f));
        G(trim).Box(new Vector3(0,floor+1.6f,59.95f),new Vector3(3.0f,3.2f,.12f));
    }
}
