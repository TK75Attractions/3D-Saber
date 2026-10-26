using UnityEngine;

// 再利用される個体に、その飛行だけの設定を保持する。
public partial class CuttableNote
{
    public bool HasOvertake { get; private set; }
    public float OvertakeScale { get; private set; }
    public float OvertakeLaneX { get; private set; }
    public double OvertakeApproach { get; private set; }

    internal void ConfigureOvertake(NoteData data, bool enabled, double approach)
    {
        HasOvertake = enabled;
        OvertakeApproach = enabled ? OvertakeRules.BaseApproach(approach) : 0;
        OvertakeScale = enabled
            ? OvertakeRules.FlightScale(data.approachScale, OvertakeApproach) : 0f;
        OvertakeLaneX = enabled ? OvertakeRules.NormalizeLane(data.laneX) : 0f;
    }
}
