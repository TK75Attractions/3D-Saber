using UnityEngine;

public sealed partial class ScenicStageWorld
{
    private void BuildAbyss()
    {
        Sky(new Color(.018f,.075f,.105f),new Color(.027f,.115f,.14f),new Color(.09f,.22f,.26f));
        var path = Surface("SunkenStone",new Color(.125f,.205f,.217f));
        var stone = Surface("RuinedArches",new Color(.22f,.31f,.305f));
        var trim = Surface("AgedCarvings",new Color(.255f,.32f,.235f));
        var dark = Surface("Seabed",new Color(.075f,.12f,.13f));
        var kelp = Surface("SwayingKelp",new Color(.10f,.27f,.19f),0,0,null,1);
        var reef = Surface("Coral",new Color(.26f,.18f,.20f));
        var fish = Surface("FishSilhouettes",new Color(.17f,.31f,.32f));
        Path(path,trim,0);
        for (int bay = 0; bay < 4; bay++)
        {
            float z = 5+bay*11;
            foreach (int side in new[] {-1,1})
            {
                float x = side*8.2f;
                Column(G(stone),G(trim),new Vector3(x,floor,z),4.7f,.60f);
                G(stone).Box(new Vector3(x,floor+.26f,z),new Vector3(2.0f,.50f,1.9f));
                G(dark).Rock(new Vector3(side*10.3f,floor-.6f,z),new Vector3(3.7f,1.1f,5.5f));
                G(stone).Frustum(new Vector3(side*10.4f,floor+.65f,z+3),.55f,.50f,2.3f,10,Quaternion.Euler(0,20,side*71));
                for (int k = 0; k < 5; k++)
                {
                    float h = 1.4f + .38f*(k%3);
                    Vector3 root = new Vector3(side*(7.0f + k*.48f),floor,z-2.8f+k*.53f);
                    G(kelp).Frustum(root+Vector3.up*h*.5f,.045f,.018f,h,5);
                    for (int leaf = 0; leaf < 5; leaf++)
                    {
                        Vector3 start = root+Vector3.up*(.30f+leaf*h*.14f);
                        G(kelp).Leaf(start,start+new Vector3((leaf%2==0?-1:1)*.38f,.75f,.12f),.13f,Vector3.back);
                    }
                }
                for (int branch = 0; branch < 4; branch++)
                {
                    Vector3 a = new Vector3(side*9.9f,floor,z-3.2f);
                    Vector3 b = a+new Vector3(side*(branch-.6f)*.22f,.7f+branch*.22f,branch*.18f);
                    G(reef).Beam(a,b,.075f,.07f);
                    G(reef).Beam(b,b+new Vector3(side*.28f,.27f,.15f),.05f,.05f);
                }
            }
            // アーチの上端はノーツの高さより十分上に置く。
            G(stone).Torus(new Vector3(0,floor+4.7f,z),8.2f,.43f,Quaternion.identity,0,180,28);
            G(trim).Torus(new Vector3(0,floor+4.7f,z-.44f),8.2f,.055f,Quaternion.identity,0,180,36);
        }
        for (int i = 0; i < 4; i++)
        {
            var group = new StageGeometry();
            for (int f = 0; f < 5; f++)
            {
                Vector3 c = new Vector3(f*.7f,Mathf.Sin(f)*.3f,f*.25f);
                group.Rock(c,new Vector3(.36f,.105f,.11f),7,4);
                group.Leaf(c+Vector3.left*.3f,c+Vector3.left*.65f,.15f,Vector3.forward);
            }
            int side = i%2==0?-1:1;
            Moving("DriftingFish"+i,group,fish,new Vector3(side*11.7f,floor+3.4f+(i%2),12+i*7),new Vector3(.65f,.18f,0),Vector3.zero,.31f,i);
        }
    }

    private void BuildSky()
    {
        Sky(new Color(.095f,.205f,.31f),new Color(.32f,.44f,.52f),new Color(.55f,.59f,.60f));
        var path = Surface("BlueGreyBridge",new Color(.20f,.275f,.31f));
        var marble = Surface("TempleLimestone",new Color(.45f,.51f,.52f));
        var trim = Surface("WeatheredGold",new Color(.40f,.34f,.21f));
        var rock = Surface("FloatingIslandRock",new Color(.24f,.275f,.285f));
        var grass = Surface("IslandGrass",new Color(.22f,.35f,.265f));
        var clouds = Surface("CloudBanks",new Color(.49f,.59f,.65f));
        var cloth = Surface("WindBanners",new Color(.22f,.39f,.44f),0,0,null,-2.8f);
        Path(path,trim,1);
        foreach (int side in new[] {-1,1})
        {
            for (int bay = 0; bay < 5; bay++)
            {
                float z = 3+bay*9;
                Column(G(marble),G(trim),new Vector3(side*7.6f,floor,z),6.2f,.52f);
                G(marble).Box(new Vector3(side*7.6f,floor+6.35f,z+4.1f),new Vector3(1.30f,.44f,9.0f));
                if (bay < 3)
                {
                    G(trim).Beam(new Vector3(side*7.6f,floor+6,z),new Vector3(side*9.6f,floor+6,z),.055f,.055f);
                    G(cloth).Banner(new Vector3(side*8.72f,floor+6,z),1.22f,2.30f,side*.10f);
                    G(trim).Box(new Vector3(side*8.72f,floor+3.65f,z+.08f),new Vector3(1.24f,.07f,.08f));
                }
            }
            for (int island = 0; island < 3; island++)
            {
                Vector3 center = new Vector3(side*(12.7f+island*2),floor-1.0f,9+island*15);
                var floating = new StageGeometry();
                floating.Frustum(new Vector3(0,-1,0),.7f,3.4f,3.2f,9);
                floating.Rock(new Vector3(0,.45f,0),new Vector3(3.8f,.65f,3.5f),10,4);
                var islandRoot = Moving("FloatingIsland"+side+"-"+island,floating,rock,center,new Vector3(side*.25f,.65f,0),new Vector3(0,3.2f,0),.66f,island+side);
                var cap = new StageGeometry(); cap.Frustum(new Vector3(0,.87f,0),2.2f,2.5f,.14f,12);
                Emit("IslandGrass",cap,grass,islandRoot);
                var cloud = new StageGeometry();
                for (int puff = 0; puff < 6; puff++)
                    cloud.Ellipsoid(new Vector3((puff-2.5f)*1.1f,Mathf.Sin(puff*2)*.25f,puff%2),new Vector3(1.9f,.70f,1.65f),16,10);
                Moving("DriftingCloud"+side+"-"+island,cloud,clouds,center+new Vector3(side*2.8f,-.1f,-3),new Vector3(side*2.1f,.32f,1.2f),Vector3.zero,.42f/(1+island*.35f),island+side);
            }
            // 神殿の風輪と鳥の群れ。周辺に違う周期の動きを重ねて空の高さを見せる。
            var wind = new StageGeometry();
            wind.Torus(Vector3.zero,1.65f,.06f,Quaternion.identity,15,165,24);
            wind.Torus(Vector3.zero,1.65f,.06f,Quaternion.identity,195,345,24);
            for (int petal=0;petal<6;petal++)
            {
                var turn=Quaternion.Euler(0,0,petal*60);
                wind.Leaf(turn*new Vector3(0,.75f,0),turn*new Vector3(.35f,1.5f,0),.18f,Vector3.back);
            }
            Moving("TempleWindWheel"+side,wind,trim,new Vector3(side*10.3f,floor+4.4f,13),new Vector3(0,.18f,0),new Vector3(0,0,side*17),.6f,side);
            var birds=new StageGeometry();
            for (int bird=0;bird<4;bird++)
            {
                Vector3 body=new Vector3((bird-1.5f)*.65f,Mathf.Sin(bird)*.3f,bird*.3f);
                birds.Leaf(body,body+new Vector3(-.40f,.16f,.10f),.18f,Vector3.up);
                birds.Leaf(body,body+new Vector3(.40f,.16f,.10f),.18f,Vector3.up);
            }
            Moving("SkyBirds"+side,birds,marble,new Vector3(side*13.2f,floor+6.3f,23),new Vector3(side*2,1.2f,1.5f),Vector3.zero,.45f,side,new Vector3(0,9,12));
        }
        // 遠景の浮遊神殿。床下と空の層を分けて高さを見せる。
        for (int j = 0; j < 5; j++)
            Column(G(marble),G(trim),new Vector3(-12+j*6,floor+1,65),7.5f,.68f);
        G(marble).Box(new Vector3(0,floor+8.65f,65),new Vector3(27,.6f,3));
    }

    private static void Column(StageGeometry shaft, StageGeometry caps, Vector3 bottom, float height, float radius)
    {
        shaft.Frustum(bottom+Vector3.up*height*.5f,radius,radius*.84f,height,12);
        caps.Frustum(bottom+Vector3.up*.18f,radius*1.45f,radius*1.45f,.36f,12);
        caps.Frustum(bottom+Vector3.up*(height-.10f),radius*1.27f,radius*1.40f,.25f,12);
        caps.Box(bottom+Vector3.up*(height+.14f),new Vector3(radius*3,.24f,radius*3));
    }
}
