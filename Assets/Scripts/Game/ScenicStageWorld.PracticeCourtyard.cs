using UnityEngine;

public sealed partial class ScenicStageWorld
{
    // チュートリアル専用の中庭。石畳の中央を空け、目印と小さな動きは左右の休憩所に集める。
    private void BuildPracticeCourtyard()
    {
        Sky(new Color(.07f,.12f,.14f),new Color(.15f,.21f,.20f),new Color(.34f,.39f,.33f));
        var paving = Surface("PracticePaving",new Color(.22f,.26f,.25f));
        var stone = Surface("CourtyardLimestone",new Color(.42f,.41f,.34f));
        var wood = Surface("CourtyardCedar",new Color(.28f,.21f,.14f));
        var leaves = Surface("CourtyardHedges",new Color(.16f,.27f,.19f));
        var canopy = Surface("CourtyardCanopies",new Color(.27f,.36f,.23f),0,0,null,.35f);
        var marker = Surface("CourtyardGuideLights",new Color(.47f,.45f,.32f),0,.08f);
        Path(paving,stone,1);
        G(paving).Box(new Vector3(0,floor-.32f,51),new Vector3(30,.5f,25));

        foreach (int side in new[] {-1,1})
        {
            G(stone).Box(new Vector3(side*10.6f,floor-.32f,23),new Vector3(9,.5f,55));
            G(leaves).Box(new Vector3(side*15.2f,floor+.65f,25),new Vector3(1.6f,1.3f,58));
            for (int bay = 0; bay < 6; bay++)
            {
                float z = 1+bay*9;
                // 木組みは通路の外側だけ。上部の説明文字にも大きな装飾を重ねない。
                foreach (float x in new[] {7.5f,12.7f})
                {
                    Vector3 foot = new Vector3(side*x,floor+.18f,z);
                    G(stone).Box(foot,new Vector3(.72f,.36f,.72f));
                    G(wood).Beam(foot,new Vector3(side*x,6.7f,z),.24f,.24f);
                }
                G(wood).Box(new Vector3(side*10.1f,6.7f,z),new Vector3(5.9f,.28f,.34f));
                G(wood).Box(new Vector3(side*7.5f,6.5f,z+4.5f),new Vector3(.20f,.26f,9));
                G(wood).Box(new Vector3(side*12.7f,6.5f,z+4.5f),new Vector3(.20f,.26f,9));
                for (int rib = 0; rib < 4; rib++)
                    G(wood).Box(new Vector3(side*10.1f,6.9f,z+rib*2.25f),new Vector3(5.8f,.12f,.15f));

                // ベンチと丸い植木鉢。赤・青のノーツと紛れる色や形を避ける。
                Vector3 bench = new Vector3(side*10.7f,floor+.65f,z+4.5f);
                G(wood).Box(bench,new Vector3(1.1f,.16f,3.2f));
                G(wood).Box(bench+new Vector3(side*.52f,.42f,0),new Vector3(.12f,.75f,3.2f));
                foreach (int end in new[] {-1,1})
                    G(stone).Box(bench+new Vector3(0,-.33f,end*1.15f),new Vector3(.75f,.6f,.35f));
                Vector3 planter = new Vector3(side*13.3f,floor+.4f,z+1.6f);
                G(stone).Frustum(planter,.8f,1.0f,.8f,12);
                G(wood).Frustum(planter+Vector3.up*1.5f,.12f,.08f,2.5f,8);
                G(canopy).Ellipsoid(planter+Vector3.up*3.1f,new Vector3(1.5f,1.8f,1.5f),16,10);
                G(leaves).Ellipsoid(planter+new Vector3(side*.4f,2.5f,.3f),new Vector3(1.3f,1.0f,1.25f),12,8);

                Vector3 light = new Vector3(side*6.7f,floor+.45f,z+3);
                G(stone).Box(light,new Vector3(.42f,.9f,.42f));
                G(marker).Box(light+Vector3.up*.35f,new Vector3(.44f,.12f,.44f));
            }

            // 四つの小さな風鈴だけを穏やかに揺らす。動きは既存の曲時計に従う。
            for (int i = 0; i < 2; i++)
            {
                var chime = new StageGeometry();
                chime.Beam(Vector3.zero,new Vector3(0,-.65f,0),.025f,.025f);
                chime.Box(new Vector3(0,-.65f,0),new Vector3(.65f,.10f,.35f));
                for (int rod = 0; rod < 3; rod++)
                {
                    float length = .65f+rod*.12f;
                    chime.Frustum(new Vector3((rod-1)*.22f,-.78f-length*.5f,0),.035f,.035f,length,8);
                }
                Moving("CourtyardWindChime"+side+"-"+i,chime,wood,new Vector3(side*8.7f,6.4f,6+i*18),
                    new Vector3(.035f,0,0),Vector3.zero,.55f,i+side,sway:new Vector3(1,0,3));
            }
        }

        // 奥の入口は判定領域より十分遠くに置く。手前は最後まで見通せる。
        foreach (int side in new[] {-1,1})
        {
            G(stone).Box(new Vector3(side*7,floor+3.2f,64),new Vector3(1.2f,6.4f,1.2f));
            G(leaves).Box(new Vector3(side*15,floor+1.3f,64),new Vector3(14,2.6f,1.6f));
        }
        G(wood).Box(new Vector3(0,4.3f,64),new Vector3(15.8f,.45f,1.4f));
        G(stone).Box(new Vector3(0,4.65f,64),new Vector3(16.3f,.25f,1.7f));
    }
}
