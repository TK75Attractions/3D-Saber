using System;

// 譜面の着地点を9マスへ。境界は右・上、外周ちょうどは内側。境目は float に丸めてから比べる(Mono は途中を double で計算するので、
// 丸めないと float で渡した境目ちょうどの点が左・下のマスに落ちる)。
public static class AchievementGrid
{
    public static int Cell(float x, float y)
    {
        if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y)
            || Math.Abs(x) > AchievementCatalog.GridHalfWidth || Math.Abs(y) > AchievementCatalog.GridHalfHeight) return -1;
        int column = 0, row = 0;
        for (int c = 1; c < AchievementCatalog.GridColumns; c++)
            if (x >= (float)((2 * c - AchievementCatalog.GridColumns) * AchievementCatalog.GridHalfWidth / AchievementCatalog.GridColumns)) column = c;
        for (int r = 1; r < AchievementCatalog.GridRows; r++)
            if (y >= (float)((2 * r - AchievementCatalog.GridRows) * AchievementCatalog.GridHalfHeight / AchievementCatalog.GridRows)) row = r;
        return row * AchievementCatalog.GridColumns + column;
    }
}
