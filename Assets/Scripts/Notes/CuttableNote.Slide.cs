using System.Collections.Generic;
using UnityEngine;

// 元の着地点を別に保持し、毎コマの補間で誤差を積み重ねない。
public partial class CuttableNote
{
    public bool HasSlide { get; private set; }
    public bool HasSlidePath => HasSlide && slidePath != null && slidePath.Count > 0;
    Vector2 slideStart, slideEnd;
    List<SlidePathPoint> slidePath;
    float slideScale;

    internal void ResetSlide()
    {
        HasSlide = false;
        slidePath = null;
        slideStart = slideEnd = Vector2.zero;
        slideScale = 1f;
    }

    internal void ConfigureSlide(NoteData data, float scale)
    {
        ResetSlide();
        LongSlideRules.Normalize(data, scale);
        HasSlide = LongSlideRules.Enabled(data);
        slideStart = LandingPosition;
        if (!HasSlide) return;
        slideScale = scale > 0f && !float.IsInfinity(scale) ? scale : 1f;
        slideStart = new Vector2(data.x, data.y) * slideScale;
        LandingPosition = slideStart;
        slideEnd = new Vector2(data.slideToX, data.slideToY) * slideScale;
        slidePath = data.slidePath;
    }

    internal void UpdateSlide(double songTime, float duration)
    {
        if (!HasSlide) return;
        LandingPosition = LongSlideRules.Position(slideStart, slideEnd,
            slidePath, slideScale, songTime - HitTime, duration);
        // 接近中の風と奥行きは従来の担当へ任せる。到着後は判定の根元も動かす。
        if (songTime < HitTime) return;
        var p = transform.position;
        transform.position = new Vector3(LandingPosition.x, LandingPosition.y, p.z);
        // 判定前の更新・停止中のテストでも数字を現在位置へそろえる。
        if (countLabel != null)
            countLabel.transform.position = transform.position + LongNoteCountStyle.WorldOffset;
    }
}
