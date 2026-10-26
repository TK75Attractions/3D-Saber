using UnityEngine;

// 元の着地点を別に保持し、毎コマの補間で誤差を積み重ねない。
public partial class CuttableNote
{
    public bool HasSlide { get; private set; }
    Vector2 slideStart, slideEnd;

    internal void ConfigureSlide(NoteData data, float scale)
    {
        HasSlide = LongSlideRules.Enabled(data);
        slideStart = LandingPosition;
        slideEnd = HasSlide ? new Vector2(data.slideToX, data.slideToY) * scale : slideStart;
    }

    internal void UpdateSlide(double songTime, float duration)
    {
        if (!HasSlide) return;
        LandingPosition = LongSlideRules.Position(slideStart, slideEnd, songTime - HitTime, duration);
        // 接近中の風と奥行きは従来の担当へ任せる。到着後は判定の根元も動かす。
        if (songTime < HitTime) return;
        var p = transform.position;
        transform.position = new Vector3(LandingPosition.x, LandingPosition.y, p.z);
        // 通常は既存の LateUpdate も追従する。判定前の更新・停止中のテストでも同じ位置にする。
        if (countLabel != null)
            countLabel.transform.position = transform.position + LongNoteCountStyle.WorldOffset;
    }
}
