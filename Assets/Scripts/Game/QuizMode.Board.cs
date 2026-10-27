using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

// 試験用紙の上の絵(問題・楽譜・行列・凡例・数式の画像、線・的・玉・レーン・輪、的の上の文字)。毎コマ曲の時計から描き直す。
// 画像は曲フォルダの quiz/ の PNG(File.ReadAllBytes + Texture2D.LoadImage)。読めない画像は出さず、一度だけ知らせる。
public sealed partial class QuizMode
{
    sealed class BoardImage
    {
        public string file;
        public Texture2D texture;
        public float aspect = 1f;   // 幅 ÷ 高さ
        public GameObject go;
        public Material material;
        public bool used;
    }

    // 的の上の文字(行列の答えの列)とレーンの下の字の大きさ(TextMeshPro の 3D は 10 で 1 単位の字)。
    const float TargetLabelSize = 3.7f, LaneLetterSize = 2.4f;
    const string LabelFont = "Oxanium-Bold";

    readonly Dictionary<string, BoardImage> images = new Dictionary<string, BoardImage>(StringComparer.Ordinal);
    readonly HashSet<string> missingImages = new HashSet<string>(StringComparer.Ordinal);
    readonly TextMeshPro[] targetLabels = new TextMeshPro[QuizRules.LaneCount];
    readonly TextMeshPro[] laneLetters = new TextMeshPro[QuizRules.LaneCount];
    readonly bool[] targetLabelUsed = new bool[QuizRules.LaneCount];
    readonly bool[] laneLetterUsed = new bool[QuizRules.LaneCount];
    Shader boardShader;
    bool boardShaderFallback, boardShaderChecked;
    Mesh quad;

    public int LoadedImageCount => images.Count;
    public IReadOnlyCollection<string> MissingImages => missingImages;
    public bool UsesFallbackImageShader => boardShaderFallback;

    // 画像が今出ているか・その色(濃さ)・中心・大きさ(テスト用)。
    public bool ImageShown(string file) => file != null && images.TryGetValue(file, out var image)
        && image.go != null && image.go.activeSelf;
    public Color ImageColor(string file) => file != null && images.TryGetValue(file, out var image)
        && image.material != null && image.material.HasProperty("_BaseColor")
            ? image.material.GetColor("_BaseColor") : Color.clear;
    public Vector3 ImagePosition(string file) => file != null && images.TryGetValue(file, out var image) && image.go != null
        ? image.go.transform.position : Vector3.zero;
    public Vector3 ImageSize(string file) => file != null && images.TryGetValue(file, out var image) && image.go != null
        ? image.go.transform.lossyScale : Vector3.zero;
    public Renderer ImageRenderer(string file) => file != null && images.TryGetValue(file, out var image) && image.go != null
        ? image.go.GetComponent<MeshRenderer>() : null;
    // 行列の的の上の文字(出ていなければ null)。
    public TextMeshPro TargetLabel(int target) => target >= 0 && target < targetLabels.Length
        && targetLabels[target] != null && targetLabels[target].gameObject.activeSelf ? targetLabels[target] : null;

    // ---- 画像 ----

    void PrepareImages()
    {
        if (quiz?.items == null) return;
        foreach (var item in quiz.items)
        {
            Load(item.question);
            Load(item.answerImage);
            foreach (var row in item.rows) Load(row);
            if (item.kind == QuizRules.Count) LegendImage(item);
        }
    }

    // 数える問題の凡例: QuizItemData.legend、無ければ rows の最初、それも無ければ quiz/legend.png。
    BoardImage LegendImage(QuizItemData item)
    {
        BoardImage image = null;
        if (!string.IsNullOrWhiteSpace(item.legend)) image = Load(item.legend);
        if (image == null && item.rows.Count > 0) image = Load(item.rows[0]);
        if (image == null) image = Load(QuizRules.DefaultLegendImage, warn: false);
        return image;
    }

    BoardImage Load(string file, bool warn = true)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        if (images.TryGetValue(file, out var cached)) return cached;
        if (missingImages.Contains(file)) return null;
        var texture = ReadTexture(file, warn);
        if (texture == null)
        {
            missingImages.Add(file);
            return null;
        }
        var image = new BoardImage
        {
            file = file, texture = texture,
            aspect = texture.height > 0 ? (float)texture.width / texture.height : 1f
        };
        image.go = new GameObject("QuizImage " + file, typeof(MeshFilter), typeof(MeshRenderer));
        image.go.transform.SetParent(board, false);
        image.go.GetComponent<MeshFilter>().sharedMesh = Quad();
        image.material = ImageMaterial(texture);
        var renderer = image.go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = image.material;
        renderer.sortingLayerID = 0;
        renderer.sortingOrder = ImageSortingOrder;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        image.go.SetActive(false);
        images[file] = image;
        return image;
    }

    Texture2D ReadTexture(string file, bool warn)
    {
        string name = file.Trim();
        if (!ChartLoader.ValidPathSegment(name)) return null;
        string path = Path.Combine(folder, QuizRules.ImageFolder, name);
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                if (warn) Debug.LogWarning($"[Quiz] 画像がありません: {path}");
                return null;
            }
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            Debug.LogWarning($"[Quiz] 画像を開けません: {path} ({exception.GetType().Name})");
            return null;
        }
        // 小さく映すので、ミップマップでにじませずに縮める。
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "Quiz/" + name, hideFlags = HideFlags.DontSave };
        if (!texture.LoadImage(bytes, true))
        {
            UISkinKit.SafeDestroy(texture);
            Debug.LogWarning($"[Quiz] 画像を読めません: {path}");
            return null;
        }
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Trilinear;
        texture.anisoLevel = 4;
        return texture;
    }

    // 画像の材質。霧・光を使わず深さを見ない Effects/QuizBoard。使えないときだけ URP Unlit の半透明で代える。
    Material ImageMaterial(Texture2D texture)
    {
        if (!boardShaderChecked)
        {
            boardShaderChecked = true;
            boardShader = Resources.Load<Shader>("Effects/QuizBoard");
            if (boardShader == null || !boardShader.isSupported)
            {
                boardShader = Shader.Find("Universal Render Pipeline/Unlit");
                boardShaderFallback = true;
                Debug.LogWarning("[Quiz] 黒板の画像の shader が無いか未対応です。URP Unlit で代えます。");
            }
        }
        if (boardShader == null) return null;
        var material = new Material(boardShader) { name = "Quiz/" + texture.name, hideFlags = HideFlags.DontSave, renderQueue = 3045 };
        if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
        if (boardShaderFallback && material.HasProperty("_Surface"))
        {
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_SrcBlendAlpha")) material.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            if (material.HasProperty("_DstBlendAlpha")) material.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }
        return material;
    }

    Mesh Quad()
    {
        if (quad != null) return quad;
        quad = new Mesh { name = "Quiz/Quad", hideFlags = HideFlags.DontSave };
        quad.vertices = new[] { new Vector3(-.5f, -.5f, 0f), new Vector3(.5f, -.5f, 0f), new Vector3(.5f, .5f, 0f), new Vector3(-.5f, .5f, 0f) };
        quad.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
        quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        quad.RecalculateBounds();
        return quad;
    }

    // 中心 center(ワールド)・大きさ size(ワールド)で出す。色の a は濃さ。
    void Place(BoardImage image, Vector3 center, Vector2 size, Color color)
    {
        if (image == null || image.go == null) return;
        image.used = true;
        if (!image.go.activeSelf) image.go.SetActive(true);
        PlaceWorld(image.go.transform, center, new Vector3(size.x, size.y, 1f));
        if (image.material != null && image.material.HasProperty("_BaseColor")) image.material.SetColor("_BaseColor", color);
    }

    // 親の拡大に左右されず、ワールドの位置と大きさで置く。
    static void PlaceWorld(Transform target, Vector3 position, Vector3 size)
    {
        target.SetPositionAndRotation(position, Quaternion.identity);
        Vector3 parent = target.parent != null ? target.parent.lossyScale : Vector3.one;
        target.localScale = new Vector3(Divide(size.x, parent.x), Divide(size.y, parent.y), Divide(size.z, parent.z));
    }

    static float Divide(float value, float by) => Mathf.Abs(by) > 1e-6f ? value / by : value;

    // count・formula などは revealMs から答えの入った画像に差し替える。
    BoardImage QuestionImage(QuizItemData item, double time)
    {
        if (QuizRules.Revealed(item, offset, time))
        {
            var answer = Load(item.answerImage);
            if (answer != null) return answer;
        }
        return Load(item.question);
    }

    // ---- 字 ----

    void PrepareLabels()
    {
        if (!hasMatrix) return;
        for (int i = 0; i < QuizRules.LaneCount; i++)
        {
            targetLabels[i] = MakeLabel("QuizTargetLabel" + i, TargetLabelSize);
            laneLetters[i] = MakeLabel("QuizLaneLetter" + i, LaneLetterSize);
            if (laneLetters[i] != null) laneLetters[i].text = LaneNames[i];
        }
    }

    TextMeshPro MakeLabel(string name, float size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(board, false);
        var text = go.AddComponent<TextMeshPro>();
        var font = UISkinKit.FontAsset(LabelFont);
        if (font != null) text.font = font;
        text.fontSize = size;
        text.fontStyle = FontStyles.Normal;
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.isOrthographic = false;
        text.color = Color.white;
        text.rectTransform.sizeDelta = new Vector2(1.2f, .6f);
        // 前後は sortingOrder で決める(試験用紙の上、ノーツと剣の下)。字の材質は共有のまま変えない。
        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            renderer.sortingLayerID = 0;
            renderer.sortingOrder = MarkSortingOrder;
        }
        go.SetActive(false);
        return text;
    }

    static readonly string[] LaneNames = { "A", "B", "C", "D" };

    static void ShowLabel(TextMeshPro text, string value, Vector3 position, float alpha)
    {
        if (text == null) return;
        if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);
        if (text.text != value) text.text = value;
        PlaceWorld(text.transform, position, Vector3.one);
        var color = new Color(1f, 1f, 1f, alpha);
        if (text.color != color) text.color = color;
    }

    // ---- 毎コマの描き直し ----

    void DrawBoard(double time, float visibility)
    {
        if (marks != null) marks.Clear();
        foreach (var image in images.Values) image.used = false;
        Array.Clear(targetLabelUsed, 0, targetLabelUsed.Length);
        Array.Clear(laneLetterUsed, 0, laneLetterUsed.Length);
        ActivePops = ActiveRings = BallsDrawn = GuidesDrawn = 0;
        if (visibility > 0f && quiz?.items != null && marks != null)
        {
            if (hasRhythm) DrawRhythm(time, visibility);
            if (hasMatrix) DrawMatrix(time, visibility);
            if (hasCount) DrawCount(time, visibility);
            if (hasFormula) DrawFormula(time, visibility);
            DrawPops(time, visibility);
        }
        if (marks != null) marks.Flush();
        foreach (var image in images.Values)
            if (!image.used && image.go != null && image.go.activeSelf) image.go.SetActive(false);
        for (int i = 0; i < QuizRules.LaneCount; i++)
        {
            if (!targetLabelUsed[i] && targetLabels[i] != null && targetLabels[i].gameObject.activeSelf) targetLabels[i].gameObject.SetActive(false);
            if (!laneLetterUsed[i] && laneLetters[i] != null && laneLetters[i].gameObject.activeSelf) laneLetters[i].gameObject.SetActive(false);
        }
    }

    // 頂点色は線形の値としてそのまま使われるので、材質の色と同じ見え方になるよう直す(白と黒は変わらない)。
    static Color Vertex(Color color, float alpha)
    {
        Color linear = color.linear;
        linear.a = Mathf.Clamp01(alpha);
        return linear;
    }

    static bool InSection(QuizRules.Span span, double time)
        => time >= span.Start - QuizRules.PaperFadeSeconds && time < span.End + QuizRules.PassFadeSeconds;

    // rhythm: 横の線・的・右から流れる玉(本物と飾り)、出ている問題の画像と答えの行(正しい行の緑)。
    void DrawRhythm(double time, float visibility)
    {
        if (!InSection(rhythmSpan, time)) return;
        float z = judgeZ - MarkLift;
        marks.Line(new Vector3(QuizRules.RhythmTargetX - QuizRules.RhythmLineLeftMargin, QuizRules.RhythmLineY, z),
            new Vector3(rightEdgeX + QuizRules.BallRadius * 2f, QuizRules.RhythmLineY, z),
            QuizRules.LineWidth, Vertex(Color.white, QuizRules.LineAlpha * visibility));
        marks.Ring(new Vector2(QuizRules.RhythmTargetX, QuizRules.RhythmLineY), z, QuizRules.RhythmTargetRadius,
            QuizRules.TargetWidth, Vertex(Color.white, visibility));
        double lead = QuizRules.LeadSeconds(QuizRules.RhythmTargetX, rightEdgeX + QuizRules.BallRadius, QuizRules.RhythmSpeed);
        for (int i = 0; i < quiz.items.Count; i++)
        {
            var item = quiz.items[i];
            if (item.kind != QuizRules.Rhythm) continue;
            // その問題が出る前は玉を出さず、出てから薄く現れる。
            float appear = QuizRules.AppearFade(item, offset, time);
            for (int d = 0; appear > 0f && d < item.dots.Count; d++)
            {
                var dot = item.dots[d];
                double arrival = QuizRules.Seconds(dot.ms, offset);
                if (!QuizRules.BallShown(arrival, time, lead) || popped.Contains(Key(i, d))) continue;
                float alpha = QuizRules.BallAlpha(item.hint, dot.real, arrival, time) * appear * visibility;
                marks.Disc(new Vector2(QuizRules.BallX(arrival, time), QuizRules.RhythmLineY), z, QuizRules.BallRadius,
                    Vertex(Color.white, alpha));
                BallsDrawn++;
            }
            if (QuizRules.Active(item, offset, time)) DrawRhythmBoard(item, time, visibility);
        }
    }

    void DrawRhythmBoard(QuizItemData item, double time, float visibility)
    {
        float z = judgeZ - ImageLift;
        int rows = item.rows.Count;
        var question = QuestionImage(item, time);
        if (question != null)
            Place(question, new Vector3(0f, QuizRules.QuestionCenterY(rows), z),
                new Vector2(QuizRules.QuestionHeight * question.aspect, QuizRules.QuestionHeight), new Color(1f, 1f, 1f, visibility));
        // 答えの行は左をそろえ、まとまりを真ん中に置く。
        float widest = 0f;
        for (int r = 0; r < rows; r++)
        {
            var row = Load(item.rows[r]);
            if (row != null) widest = Mathf.Max(widest, QuizRules.RowHeight * row.aspect);
        }
        float left = -widest * .5f;
        bool green = QuizRules.Green(item, offset, time);
        for (int r = 0; r < rows; r++)
        {
            var row = Load(item.rows[r]);
            if (row == null) continue;
            float width = QuizRules.RowHeight * row.aspect;
            Color color = green && r == item.answer ? QuizRules.AnswerGreen : Color.white;
            color.a = visibility;
            Place(row, new Vector3(left + width * .5f, QuizRules.RowCenterY(r, rows), z), new Vector2(width, QuizRules.RowHeight), color);
        }
    }

    // matrix: 左の小さなレーン(縦線・小さな丸・A B C D)と落ちる印、4つの的、出ている行列の画像と的の上の文字。
    // レーンと印の道は画面でまっすぐ縦(MeasureLanes)。ノーツが来ても的には何も出さない(切ったら輪が広がるだけ)。
    void DrawMatrix(double time, float visibility)
    {
        if (!InSection(matrixSpan, time)) return;
        float z = judgeZ - MarkLift;
        Color lane = Vertex(Color.white, QuizRules.LineAlpha * .8f * visibility);
        for (int l = 0; l < QuizRules.LaneCount; l++)
        {
            Vector2 bottom = laneBottoms[l], top = laneTops[l];
            float stopY = QuizRules.LaneBottomY + QuizRules.LaneDotRadius;
            marks.Line(new Vector3(top.x, top.y, z), new Vector3(QuizRules.LaneXAt(bottom, top, stopY), stopY, z),
                QuizRules.LaneLineWidth, lane);
            marks.Ring(bottom, z, QuizRules.LaneDotRadius, QuizRules.LaneLineWidth * 1.4f, Vertex(Color.white, visibility), 24);
            laneLetterUsed[l] = true;
            ShowLabel(laneLetters[l], LaneNames[l],
                new Vector3(QuizRules.LaneXAt(bottom, top, QuizRules.LaneLetterY), QuizRules.LaneLetterY, judgeZ - ImageLift), visibility);
        }
        double lead = QuizRules.LeadSeconds(QuizRules.LaneBottomY, laneTops[0].y + QuizRules.GuideRadius, QuizRules.GuideSpeed);
        for (int i = 0; i < quiz.items.Count; i++)
        {
            var item = quiz.items[i];
            if (item.kind != QuizRules.Matrix) continue;
            // その問題が出る前は印を出さず、出てから薄く現れる(最初の印はレーンの途中から)。
            float appear = QuizRules.AppearFade(item, offset, time);
            if (appear <= 0f) continue;
            foreach (var guide in item.guides)
            {
                double arrival = QuizRules.Seconds(guide.ms, offset);
                if (!QuizRules.BallShown(arrival, time, lead)) continue;
                float y = QuizRules.FallY(QuizRules.LaneBottomY, QuizRules.GuideSpeed, arrival, time);
                var center = new Vector2(QuizRules.LaneXAt(laneBottoms[guide.lane], laneTops[guide.lane], y), y);
                marks.Disc(center, z, QuizRules.GuideRadius,
                    Vertex(Color.white, QuizRules.PassFade(arrival, time) * appear * visibility), 20);
                GuidesDrawn++;
            }
        }
        for (int t = 0; t < QuizRules.MatrixTargetX.Length; t++)
            marks.Ring(new Vector2(QuizRules.MatrixTargetX[t], QuizRules.MatrixTargetY), z, QuizRules.MatrixTargetRadius,
                QuizRules.TargetWidth, Vertex(Color.white, visibility));
        foreach (var item in quiz.items)
            if (item.kind == QuizRules.Matrix && QuizRules.Active(item, offset, time)) DrawMatrixBoard(item, time, visibility);
    }

    void DrawMatrixBoard(QuizItemData item, double time, float visibility)
    {
        float z = judgeZ - ImageLift;
        var image = QuestionImage(item, time);
        if (image != null)
            Place(image, new Vector3(0f, QuizRules.MatrixBoardBottomY + QuizRules.MatrixImageHeight * .5f, z),
                new Vector2(QuizRules.MatrixImageHeight * image.aspect, QuizRules.MatrixImageHeight), new Color(1f, 1f, 1f, visibility));
        // 空の文字は出さない(「= ?」の問題)。
        for (int t = 0; t < QuizRules.MatrixTargetX.Length && t < item.labels.Count; t++)
        {
            if (string.IsNullOrEmpty(item.labels[t])) continue;
            targetLabelUsed[t] = true;
            ShowLabel(targetLabels[t], item.labels[t], new Vector3(QuizRules.MatrixTargetX[t], QuizRules.MatrixLabelY, z), visibility);
        }
    }

    // count: 縦の線・的・上から落ちる玉(金は数える玉)、左上の凡例、左の問題(revealMs から答え)、切った金の玉の数だけ金の小さな丸。
    void DrawCount(double time, float visibility)
    {
        if (!InSection(countSpan, time)) return;
        float z = judgeZ - MarkLift;
        marks.Line(new Vector3(QuizRules.CountLineX, topEdgeY + .3f, z),
            new Vector3(QuizRules.CountLineX, QuizRules.CountTargetY, z), QuizRules.LineWidth,
            Vertex(Color.white, QuizRules.LineAlpha * visibility));
        marks.Ring(new Vector2(QuizRules.CountLineX, QuizRules.CountTargetY), z, QuizRules.CountTargetRadius,
            QuizRules.TargetWidth, Vertex(Color.white, visibility));
        double lead = QuizRules.LeadSeconds(QuizRules.CountTargetY, topEdgeY + QuizRules.BallRadius, QuizRules.CountSpeed);
        for (int i = 0; i < quiz.items.Count; i++)
        {
            var item = quiz.items[i];
            if (item.kind != QuizRules.Count) continue;
            // その問題が出る前は玉を出さず、出てから薄く現れる。
            float appear = QuizRules.AppearFade(item, offset, time);
            for (int d = 0; appear > 0f && d < item.dots.Count; d++)
            {
                var dot = item.dots[d];
                double arrival = QuizRules.Seconds(dot.ms, offset);
                if (!QuizRules.BallShown(arrival, time, lead) || popped.Contains(Key(i, d))) continue;
                var center = new Vector2(QuizRules.CountLineX, QuizRules.FallY(QuizRules.CountTargetY, QuizRules.CountSpeed, arrival, time));
                float alpha = QuizRules.BallAlpha(item.hint, dot.real, arrival, time) * appear * visibility;
                marks.Disc(center, z, QuizRules.BallRadius, Vertex(dot.unit ? UISkinPalette.NoteGold : Color.white, alpha));
                BallsDrawn++;
            }
            if (QuizRules.Active(item, offset, time)) DrawCountBoard(i, item, time, visibility);
        }
    }

    // 動画のように、左上の大きな凡例「● = 1」と、縦の線の左の大きな式(CountScale 倍)。
    void DrawCountBoard(int index, QuizItemData item, double time, float visibility)
    {
        float z = judgeZ - ImageLift;
        var legend = LegendImage(item);
        if (legend != null)
        {
            var size = new Vector2(QuizRules.LegendHeight * legend.aspect, QuizRules.LegendHeight);
            float left = leftEdgeX + QuizRules.LegendMargin, top = topEdgeY - QuizRules.LegendMargin;
            Place(legend, new Vector3(left + size.x * .5f, top - size.y * .5f, z), size, new Color(1f, 1f, 1f, visibility));
        }
        // 問題は右の端を縦の線の少し左にそろえる(横に長い式は画面の左の端に収まるよう縮める)。
        // 金の小さな丸は問題(差し替える前)の左下から並べる。
        var question = Load(item.question);
        Vector2 size0 = QuizRules.CountQuestionSize(question != null ? question.aspect : 1f, countLeftEdgeX);
        float questionLeft = QuizRules.CountQuestionRightX - size0.x;
        float questionBottom = QuizRules.CountQuestionCenterY - size0.y * .5f;
        var shown = QuestionImage(item, time);
        if (shown != null)
        {
            Vector2 shownSize = QuizRules.CountQuestionSize(shown.aspect, countLeftEdgeX);
            Place(shown, new Vector3(QuizRules.CountQuestionRightX - shownSize.x * .5f, QuizRules.CountQuestionCenterY, z),
                shownSize, new Color(1f, 1f, 1f, visibility));
        }
        int count = UnitMarks(index);
        for (int k = 0; k < count; k++)
            marks.Disc(QuizRules.UnitMark(k, questionLeft, questionBottom), judgeZ - MarkLift, QuizRules.UnitMarkRadius,
                Vertex(UISkinPalette.NoteGold, visibility), 36);
    }

    // formula: 数式の画像(revealMs から答え)をゲートの中央のわずかに奥へ、幅 QuizRules.FormulaWidth(9.0)・濃さ 0.55。
    // ノーツはその上(手前)に描かれる。輪は予算に通ったものだけ。
    void DrawFormula(double time, float visibility)
    {
        if (!InSection(formulaSpan, time)) return;
        foreach (var item in quiz.items)
        {
            if (item.kind != QuizRules.Formula || !QuizRules.Active(item, offset, time)) continue;
            var image = QuestionImage(item, time);
            if (image == null) continue;
            float height = QuizRules.FormulaWidth / Mathf.Max(.01f, image.aspect);
            Place(image, new Vector3(QuizRules.GateCenter.x, QuizRules.GateCenter.y, judgeZ + QuizRules.FormulaDepth),
                new Vector2(QuizRules.FormulaWidth, height), new Color(1f, 1f, 1f, QuizRules.FormulaAlpha * visibility));
        }
        if (DisplaySettings.ReducedEffects) return;
        float z = judgeZ - MarkLift;
        foreach (double start in ringStarts)
        {
            double age = time - start;
            if (!QuizRules.RingShown(age)) continue;
            ActiveRings++;
            marks.Ring(QuizRules.GateCenter, z, QuizRules.RingRadius(age), QuizRules.RingWidth,
                Vertex(Color.white, QuizRules.RingOpacity(age) * visibility), 96);
        }
    }

    // 見えないノーツを切った所の弾け。F3 の弱める設定では輪を広げず、的を一瞬明るくするだけ。
    void DrawPops(double time, float visibility)
    {
        bool reduced = DisplaySettings.ReducedEffects;
        float z = judgeZ - MarkLift;
        foreach (var pop in pops)
        {
            if (!QuizRules.Finite(pop.start)) continue;
            double age = time - pop.start;
            if (!QuizRules.PopShown(age)) continue;
            ActivePops++;
            if (reduced)
                marks.Disc(pop.center, z, pop.radius, Vertex(pop.color, .35f * (1f - (float)(age / QuizRules.PopSeconds)) * visibility));
            else
                marks.Ring(pop.center, z, QuizRules.PopRadius(pop.radius, age), QuizRules.PopWidth,
                    Vertex(pop.color, QuizRules.PopOpacity(age) * visibility));
        }
    }

    // ---- 片付け ----

    void HideBoard()
    {
        foreach (var image in images.Values) if (image.go != null) image.go.SetActive(false);
        foreach (var text in targetLabels) if (text != null) text.gameObject.SetActive(false);
        foreach (var text in laneLetters) if (text != null) text.gameObject.SetActive(false);
    }

    void DisposeBoard()
    {
        foreach (var image in images.Values)
        {
            UISkinKit.SafeDestroy(image.go);
            UISkinKit.SafeDestroy(image.material);
            UISkinKit.SafeDestroy(image.texture);
        }
        images.Clear();
        UISkinKit.SafeDestroy(quad);
        if (board != null) UISkinKit.SafeDestroy(board.gameObject);
        quad = null;
        board = null;
    }
}
