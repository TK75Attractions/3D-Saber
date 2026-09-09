using UnityEngine;

public sealed partial class ScenicStageWorld
{
    private void BuildGarden()
    {
        Sky(new Color(.023f,.041f,.085f),new Color(.055f,.10f,.14f),new Color(.13f,.18f,.23f));
        var path = Surface("GardenFlagstones",new Color(.19f,.23f,.245f));
        var stone = Surface("GardenGranite",new Color(.27f,.31f,.31f));
        var water = Surface("MoonPools",new Color(.045f,.11f,.14f),1,0,new Color(.22f,.34f,.39f));
        var bamboo = Surface("BambooStems",new Color(.15f,.275f,.20f),0,0,null,1);
        var leaves = Surface("BambooLeaves",new Color(.13f,.27f,.21f),0,0,null,1);
        var wood = Surface("DarkWood",new Color(.18f,.12f,.085f));
        var lamp = Surface("LanternPaper",new Color(.44f,.35f,.18f),0,.34f,new Color(.56f,.41f,.20f));
        var moon = Surface("Moon",new Color(.38f,.43f,.46f),0,.1f);
        Path(path,stone,2);
        G(moon).Ellipsoid(new Vector3(20,22,92),new Vector3(7,7,7),32,20);
        foreach (int side in new[] {-1,1})
        {
            G(water).Box(new Vector3(side*10.5f,floor-.16f,22),new Vector3(8.8f,.08f,54));
            for (int k = 0; k < 11; k++)
            {
                float z = .5f+k*4;
                G(stone).Rock(new Vector3(side*(6.65f+(k%2)*.32f),floor-.1f,z),new Vector3(.65f,.36f,.9f),8,4);
                float x = side*(8.8f+(k%3)*1.1f), h = 6+(k%4)*.48f;
                for (int stalk = 0; stalk < 3; stalk++)
                {
                    Vector3 root = new Vector3(x+side*stalk*.52f,floor,z+stalk*.28f);
                    G(bamboo).Frustum(root+Vector3.up*h*.5f,.095f,.066f,h,7);
                    for (int node = 1; node < 7; node++)
                    {
                        Vector3 c = root+Vector3.up*(node*h/7);
                        G(bamboo).Frustum(c,.116f,.112f,.075f,7);
                        if (node < 3) continue;
                        for (int leaf = 0; leaf < 3; leaf++)
                        {
                            Vector3 branch = new Vector3((leaf-1)*.40f,.13f,((node+leaf)%2==0?-1:1)*.28f);
                            G(leaves).Leaf(c,c+branch*2,.11f,Vector3.back);
                        }
                    }
                }
            }
            for (int i = 0; i < 4; i++)
            {
                Vector3 p = new Vector3(side*7.7f,floor,3.8f+i*10.6f);
                G(stone).Box(p+Vector3.up*.15f,new Vector3(.95f,.3f,.95f));
                G(stone).Frustum(p+Vector3.up*.7f,.21f,.19f,1.1f,6);
                G(stone).Box(p+Vector3.up*1.3f,new Vector3(.75f,.13f,.75f));
                LanternHousing(G(wood),G(lamp),p+Vector3.up*1.7f,.8f);
            }
            for (int i = 0; i < 3; i++)
            {
                var lantern = new StageGeometry();
                lantern.Frustum(Vector3.zero,.37f,.29f,.9f,8);
                var pos = new Vector3(side*(9.4f+i*.6f),floor+2.8f+(i%2)*.7f,8+i*12);
                var t = Moving("FloatingLantern"+side+"-"+i,lantern,lamp,pos,new Vector3(.12f,.30f,.05f),new Vector3(0,4,0),.55f,i+side);
                var cap = new StageGeometry();
                cap.Frustum(new Vector3(0,-.49f,0),.40f,.4f,.08f,8); cap.Frustum(new Vector3(0,.49f,0),.32f,.32f,.08f,8);
                Emit("LanternRims",cap,wood,t);
            }
        }
        // 遠景の橋と屋根をシルエットで添える。
        for (int i = 0; i < 16; i++)
        {
            float x = -12+i*1.6f, y = floor+.6f+Mathf.Sin(i/15f*Mathf.PI)*1.8f;
            G(wood).Box(new Vector3(x,y,57),new Vector3(1.7f,.28f,3.2f));
            foreach (int s in new[] {-1,1}) G(wood).Box(new Vector3(x,y+.70f,57+s*1.5f),new Vector3(.12f,1.4f,.12f));
        }
    }

    private static void LanternHousing(StageGeometry frame,StageGeometry paper,Vector3 center,float size)
    {
        paper.Box(center,new Vector3(size*.68f,size*.72f,size*.68f));
        foreach (int x in new[] {-1,1}) foreach (int z in new[] {-1,1})
            frame.Box(center+new Vector3(x*size*.40f,0,z*size*.40f),new Vector3(.055f,size,.055f));
        frame.Frustum(center+Vector3.up*size*.60f,size*.80f,size*.28f,size*.32f,4,Quaternion.Euler(0,45,0));
        frame.Box(center+Vector3.down*size*.46f,new Vector3(size,.09f,size));
    }

    private void BuildCrystal()
    {
        Sky(new Color(.025f,.025f,.06f),new Color(.047f,.071f,.12f),new Color(.1f,.13f,.24f));
        var path = Surface("BasaltPath",new Color(.10f,.12f,.17f));
        var rock = Surface("CaveBasalt",new Color(.16f,.175f,.22f));
        var edge = Surface("MineralVeins",new Color(.23f,.29f,.34f));
        var purple = Surface("Amethyst",new Color(.25f,.18f,.37f),2,.04f,new Color(.32f,.26f,.47f));
        var teal = Surface("GlacialCrystal",new Color(.15f,.30f,.34f),2,.04f,new Color(.24f,.40f,.43f));
        var water = Surface("CaveChannels",new Color(.025f,.09f,.14f),1,0,new Color(.15f,.3f,.39f));
        Path(path,edge,2);
        foreach (int side in new[] {-1,1})
        {
            G(water).Box(new Vector3(side*6.55f,floor-.05f,20),new Vector3(.9f,.05f,48));
            for (int i = 0; i < 8; i++)
            {
                float z = 2+i*6.2f;
                G(rock).Rock(new Vector3(side*11.8f,floor+1.8f,z),new Vector3(3.7f,4.4f,4.6f),8,5);
                for (int shard = 0; shard < 4; shard++)
                {
                    Vector3 p = new Vector3(side*(7.25f+shard*.62f),floor,z+shard*.37f);
                    float h = 2.1f+((i+shard)%4)*.85f;
                    G(shard%2==0?purple:teal).Crystal(p,.34f+shard*.09f,h,Quaternion.Euler(8+shard*5,shard*53,-side*(5+shard*4)));
                }
                G(rock).Rock(new Vector3(side*9.5f,floor+.13f,z-1),new Vector3(2.0f,.45f,2.1f),9,4);
                G(rock).Crystal(new Vector3(side*9.0f,7.8f,z+2),.8f,2.4f,Quaternion.Euler(0,0,180));
            }
            for (int i = 0; i < 3; i++)
            {
                var shard = new StageGeometry(); shard.Crystal(new Vector3(0,-.8f,0),.26f,1.6f,Quaternion.identity);
                Moving("FloatingCrystal"+side+"-"+i,shard,i%2==0?teal:purple,new Vector3(side*7.4f,floor+2.8f,5+i*12),
                    new Vector3(.06f,.22f,0),new Vector3(0,13,0),.68f,i+side,sway:new Vector3(5,0,7));
            }
        }
    }
}
