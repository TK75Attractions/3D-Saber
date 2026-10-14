using UnityEngine;

public sealed partial class ScenicStageWorld
{
    // 2:23 AM: 静かな水面、桟橋、沖の帆船と倉庫街。灯りは側面だけに置く。
    private void BuildHarbor()
    {
        Sky(new Color(.014f,.025f,.061f),new Color(.035f,.072f,.12f),new Color(.12f,.18f,.29f));
        var deck = Surface("HarborBoardwalk",new Color(.16f,.19f,.22f));
        var iron = Surface("HarborIron",new Color(.10f,.17f,.21f));
        var water = Surface("HarborWater",new Color(.025f,.075f,.13f),1,0,new Color(.16f,.31f,.39f));
        var brick = Surface("WarehouseBrick",new Color(.16f,.18f,.245f));
        var windows = Surface("HarborWindows",new Color(.35f,.32f,.19f),0,.12f);
        var sail = Surface("NightSails",new Color(.27f,.33f,.39f));
        var moon = Surface("HarborMoon",new Color(.36f,.43f,.52f),0,.08f);
        Path(deck,iron,1);
        G(moon).Ellipsoid(new Vector3(-28,23,112),new Vector3(6,6,6),24,16);
        foreach (int side in new[] {-1,1})
        {
            G(water).Box(new Vector3(side*29,floor-.48f,40),new Vector3(46,.12f,110));
            for (int bay = 0; bay < 9; bay++)
            {
                float z = bay*5.5f;
                Vector3 post = new Vector3(side*6.5f,floor+.5f,z);
                G(iron).Frustum(post,.095f,.075f,1,8);
                G(iron).Beam(post+Vector3.up*.35f,post+new Vector3(0,.35f,5.5f),.06f,.06f);
                if (bay%2 == 0)
                {
                    G(iron).Frustum(post+Vector3.up*1.5f,.065f,.05f,3,8);
                    LanternHousing(G(iron),G(windows),post+Vector3.up*3.2f,.65f);
                }
                float x = side*(20+(bay%3)*3), h = 3.4f+(bay%4)*1.7f;
                G(brick).Box(new Vector3(x,floor+h*.5f,10+z),new Vector3(4.5f,h,4.6f));
                G(iron).Box(new Vector3(x,floor+h+.13f,10+z),new Vector3(4.8f,.26f,4.9f));
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 3; col++)
                        if ((row+col+bay)%3 != 0)
                            G(windows).Box(new Vector3(x+(col-1)*1.05f,floor+1+row*.85f,7.68f+z),new Vector3(.38f,.48f,.035f));
            }
            // 船体と帆を同じ親でゆっくり揺らし、曲時計を止めると静止する。
            for (int i = 0; i < 2; i++)
            {
                var hull = new StageGeometry();
                hull.Rock(Vector3.zero,new Vector3(.85f,.38f,2.3f),10,5);
                hull.Beam(new Vector3(0,0,0),new Vector3(0,4.8f,0),.07f,.07f);
                var boat = Moving("MooredSailboat"+side+"-"+i,hull,iron,
                    new Vector3(side*(11.5f+i*3),floor-.05f,8+i*22),new Vector3(.10f,.12f,.07f),Vector3.zero,.48f,i+side,
                    sway:new Vector3(2,1.5f,3));
                var canvas = new StageGeometry();
                canvas.Leaf(new Vector3(0,4.55f,0),new Vector3(0,.75f,1.65f),1.4f,Vector3.left);
                Emit("FoldedSail",canvas,sail,boat);
            }
        }
        // 遠景の吊橋。通路の終端より先に置き、近いノーツと交差させない。
        G(iron).Box(new Vector3(0,5.1f,83),new Vector3(100,.45f,2));
        foreach (int side in new[] {-1,1})
        {
            G(brick).Box(new Vector3(side*23,7,83),new Vector3(1.4f,22,2.4f));
            for (int i = 0; i < 20; i++)
            {
                float x = i*2.5f, top = 6.8f+Mathf.Pow((x-23)/23,2)*8;
                G(iron).Beam(new Vector3(side*x,5.4f,83),new Vector3(side*x,top,83),.07f,.07f);
                if (i>0) G(iron).Beam(new Vector3(side*(x-2.5f),6.8f+Mathf.Pow((x-25.5f)/23,2)*8,83),new Vector3(side*x,top,83),.15f,.15f);
            }
        }
    }

    // 青葉騒: 温室の鉄骨、葉の段床、頭上のリブ。緑の量感と風で港・庭園と区別する。
    private void BuildAtrium()
    {
        Sky(new Color(.025f,.065f,.078f),new Color(.09f,.16f,.16f),new Color(.21f,.29f,.24f));
        var path = Surface("AtriumTiles",new Color(.23f,.27f,.25f));
        var frame = Surface("GreenhouseFrame",new Color(.18f,.25f,.23f));
        var pots = Surface("TerracottaPlanters",new Color(.31f,.20f,.13f));
        var stem = Surface("AtriumStems",new Color(.14f,.21f,.12f),0,0,null,1);
        var leaf = Surface("AtriumCanopy",new Color(.17f,.34f,.20f),0,0,null,1);
        var pale = Surface("YoungLeaves",new Color(.30f,.41f,.21f),0,0,null,1);
        var water = Surface("IrrigationChannels",new Color(.045f,.12f,.13f),1,0,new Color(.19f,.33f,.29f));
        var lamp = Surface("GreenhouseLamps",new Color(.41f,.43f,.29f),0,.08f);
        Path(path,frame,0);
        foreach (int side in new[] {-1,1})
        {
            G(water).Box(new Vector3(side*6.4f,floor-.12f,23),new Vector3(.7f,.08f,52));
            G(path).Box(new Vector3(side*11,floor-.30f,23),new Vector3(8.3f,.60f,52));
            G(frame).Box(new Vector3(side*11.2f,floor+.30f,23),new Vector3(2.6f,.60f,52));
            for (int bay = 0; bay < 8; bay++)
            {
                float z = bay*6.2f;
                G(frame).Beam(new Vector3(side*9.8f,floor,z),new Vector3(side*9.8f,6.2f,z),.19f,.19f);
                G(frame).Beam(new Vector3(side*9.8f,6.2f,z),new Vector3(0,9.3f,z),.17f,.17f);
                G(frame).Beam(new Vector3(side*9.8f,6.2f,z),new Vector3(side*9.8f,6.2f,z+6.2f),.12f,.12f);
                G(frame).Box(new Vector3(side*9.8f,floor+1.5f,z+3),new Vector3(.16f,.14f,6.2f));
                for (int tier = 0; tier < 2; tier++)
                {
                    Vector3 p = new Vector3(side*(8.1f+tier*3.1f),floor+tier*.65f,z+2.4f);
                    G(pots).Frustum(p+Vector3.up*.38f,.58f,.79f,.76f,10);
                    float height = 2.4f+(bay%3)*.55f+tier*.6f;
                    G(stem).Beam(p+Vector3.up*.65f,p+Vector3.up*height,.14f,.14f);
                    for (int frond = 0; frond < 14; frond++)
                    {
                        float angle = frond*2.39996f;
                        float crown = height-(frond<7?.7f:0);
                        Vector3 tip = p+new Vector3(Mathf.Cos(angle)*1.32f,crown-.15f+(frond%3)*.26f,Mathf.Sin(angle)*1.45f);
                        G(frond%3==0?pale:leaf).Leaf(p+Vector3.up*(crown-.45f),tip,.48f,Vector3.up);
                    }
                }
            }
            for (int i = 0; i < 3; i++)
            {
                var basket = new StageGeometry(); basket.Frustum(Vector3.zero,.35f,.48f,.5f,10);
                basket.Beam(new Vector3(0,.25f,0),new Vector3(0,1.5f,0),.025f,.025f);
                var hanging = Moving("HangingPlanter"+side+"-"+i,basket,pots,
                    new Vector3(side*7.8f,3.7f,5+i*13),new Vector3(.06f,.04f,0),Vector3.zero,.65f,i,
                    sway:new Vector3(0,0,4));
                var foliage = new StageGeometry();
                for (int j = 0; j < 7; j++)
                {
                    float a=j*Mathf.PI*2/7;
                    foliage.Leaf(new Vector3(0,.25f,0),new Vector3(Mathf.Cos(a)*.7f,-.55f,Mathf.Sin(a)*.7f),.18f,Vector3.up);
                }
                Emit("TrailingLeaves",foliage,leaf,hanging);
                G(lamp).Ellipsoid(new Vector3(side*9.8f,5.8f,5+i*13),new Vector3(.19f,.25f,.19f),10,6);
            }
        }
        // 中央の奥には閉じた格子扉。手前の判定領域は常に開放する。
        G(frame).Torus(new Vector3(0,5,61),6,.19f,Quaternion.identity,0,180,40);
        for (int i=-5;i<=5;i++) G(frame).Box(new Vector3(i,1.7f,61),new Vector3(.10f,7.5f,.15f));
    }

    // 勉強(Remaster): 高い書棚と真鍮の時計。色替えでなく、本・歯車・振り子の形で作る。
    private void BuildLibrary()
    {
        Sky(new Color(.029f,.027f,.037f),new Color(.10f,.081f,.069f),new Color(.21f,.16f,.10f));
        var wood = Surface("LibraryWalnut",new Color(.23f,.14f,.09f));
        var path = Surface("LibraryParquet",new Color(.24f,.20f,.17f));
        var brass = Surface("ClockBrass",new Color(.40f,.31f,.16f));
        var teal = Surface("TealBindings",new Color(.12f,.25f,.25f));
        var burgundy = Surface("WineBindings",new Color(.26f,.13f,.17f));
        var pages = Surface("BookPages",new Color(.43f,.39f,.28f));
        var lamp = Surface("ReadingLamps",new Color(.43f,.36f,.22f),0,.14f);
        Path(path,wood,1);
        G(path).Box(new Vector3(0,floor-.25f,23),new Vector3(27,.30f,52));
        foreach (int side in new[] {-1,1})
        {
            for (int bay = 0; bay < 7; bay++)
            {
                float z = 2+bay*6.7f;
                // 棚は内側へ向ける。本の色と背の高さは決定的な規則だけで変える。
                G(wood).Box(new Vector3(side*9.5f,floor+3.6f,z),new Vector3(.28f,7.2f,5.8f));
                foreach (int end in new[] {-1,1})
                    G(wood).Box(new Vector3(side*8.7f,floor+3.6f,z+end*2.9f),new Vector3(1.9f,7.2f,.22f));
                for (int row=0;row<6;row++)
                {
                    float y=floor+.3f+row*1.15f;
                    G(wood).Box(new Vector3(side*8.7f,y,z),new Vector3(1.9f,.16f,5.8f));
                    for (int book=0;book<11;book++)
                    {
                        float h=.55f+((book+row+bay)%4)*.12f;
                        Vector3 p=new Vector3(side*8.45f,y+.09f+h*.5f,z-2.5f+book*.48f);
                        G((book+row)%3==0?burgundy:teal).Box(p,new Vector3(1.15f,h,.34f));
                        G(brass).Box(p+new Vector3(-side*.58f,h*.21f,0),new Vector3(.018f,.035f,.27f));
                    }
                }
                G(brass).Beam(new Vector3(side*7.2f,floor+.4f,z),new Vector3(side*7.2f,4.7f,z),.10f,.10f);
                G(lamp).Frustum(new Vector3(side*7.2f,4.7f,z),.46f,.17f,.34f,12);
                G(wood).Beam(new Vector3(side*9.4f,5.2f,z),new Vector3(side*3.5f,8.4f,z),.30f,.30f);
            }
            for (int i=0;i<2;i++)
            {
                var pendulum=new StageGeometry();
                pendulum.Beam(Vector3.zero,new Vector3(0,-2.5f,0),.075f,.075f);
                pendulum.Ellipsoid(new Vector3(0,-2.5f,0),new Vector3(.40f,.40f,.12f),16,10);
                Moving("LibraryPendulum"+side+"-"+i,pendulum,brass,new Vector3(side*7.5f,4.3f,10+i*20),
                    Vector3.zero,Vector3.zero,1.1f,i*Mathf.PI,sway:new Vector3(0,0,9));
            }
        }
        G(wood).Box(new Vector3(0,5,65),new Vector3(28,22,.5f));
        G(pages).Frustum(new Vector3(0,6.2f,64.6f),4.6f,4.6f,.20f,64,Quaternion.Euler(90,0,0));
        G(brass).Torus(new Vector3(0,6.2f,64.4f),4.7f,.16f,Quaternion.identity);
        for (int i=0;i<12;i++)
        {
            float a=i*Mathf.PI/6;
            G(brass).Box(new Vector3(Mathf.Sin(a)*3.95f,6.2f+Mathf.Cos(a)*3.95f,64.2f),new Vector3(.12f,.42f,.06f),Quaternion.Euler(0,0,-i*30));
        }
        var hands=new StageGeometry();
        hands.Beam(Vector3.zero,new Vector3(0,3.3f,0),.13f,.08f);
        hands.Beam(Vector3.zero,new Vector3(2.15f,.8f,0),.18f,.08f);
        Moving("LibraryClockHands",hands,wood,new Vector3(0,6.2f,64.0f),Vector3.zero,new Vector3(0,0,-.5f));
        foreach (int side in new[] {-1,1})
        {
            var gear=new StageGeometry(); gear.Torus(Vector3.zero,2.0f,.18f,Quaternion.identity);
            for (int tooth=0;tooth<16;tooth++)
            {
                float a=tooth*Mathf.PI/8;
                gear.Box(new Vector3(Mathf.Cos(a)*2.05f,Mathf.Sin(a)*2.05f,0),new Vector3(.55f,.28f,.22f),Quaternion.Euler(0,0,tooth*22.5f));
                if (tooth%4==0) gear.Beam(Vector3.zero,new Vector3(Mathf.Cos(a)*1.9f,Mathf.Sin(a)*1.9f,0),.12f,.12f);
            }
            Moving("LibraryGear"+side,gear,brass,new Vector3(side*9,6,57),Vector3.zero,new Vector3(0,0,side*3));
        }
    }
}
