using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

// 試験用紙の上の絵。毎コマ曲の時計と、切った・逃したの記録から描き直す。
//   画像(問題・札・箱の語・数・桁・点・「…」・「?」・花丸)は曲フォルダの quiz/ の PNG(File.ReadAllBytes + Texture2D.LoadImage)。
//   同じ画像を同じコマに何枚も置ける(π の桁など)。読めない画像は出さず、一度だけ知らせる。
//   線・四角・箱・赤ペンの丸は QuizMesh(marks)。赤ペンの丸と花丸は画像より手前(MarkSortingOrder)。
public sealed partial class QuizMode
{
    sealed class BoardImage
    {
        public string file;
        public Texture2D texture;
        public float aspect = 1f;   // 幅 ÷ 高さ
        public int pixels;          // 高さの画素(同じ字の大きさで書き出した画像どうしの大きさの比に使う)
        public readonly List<BoardSprite> sprites = new List<BoardSprite>();
        public int used;            // このコマで置いた枚数
    }

    sealed class BoardSprite
    {
        public GameObject go;
        public Material material;
        public MeshRenderer renderer;
    }

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    readonly Dictionary<string, BoardImage> images = new Dictionary<string, BoardImage>(StringComparer.Ordinal);
    readonly HashSet<string> missingImages = new HashSet<string>(StringComparer.Ordinal);
    // 無くてもよい画像(qmark.png)が無かったとき。知らせず、毎コマ読み直さない。
    readonly HashSet<string> absentOptional = new HashSet<string>(StringComparer.Ordinal);
    Shader boardShader;
    bool boardShaderFallback, boardShaderChecked;
    Mesh quad;

    public int LoadedImageCount => images.Count;
    public IReadOnlyCollection<string> MissingImages => missingImages;
    public bool UsesFallbackImageShader => boardShaderFallback;

    // 画像が今出ている枚数と、index 枚目(このコマに置いた順)の色(濃さ)・中心・大きさ・描画(テスト用)。
    public int ImageCount(string file)
    {
        if (file == null || !images.TryGetValue(file, out var image)) return 0;
        int count = 0;
        foreach (var sprite in image.sprites) if (sprite.go != null && sprite.go.activeSelf) count++;
        return count;
    }
    public bool ImageShown(string file) => ImageCount(file) > 0;
    public Color ImageColor(string file, int index = 0)
    {
        var sprite = ShownSprite(file, index);
        return sprite != null && sprite.material != null && sprite.material.HasProperty(BaseColorId)
            ? sprite.material.GetColor(BaseColorId) : Color.clear;
    }
    public Vector3 ImagePosition(string file, int index = 0)
    {
        var sprite = ShownSprite(file, index);
        return sprite != null ? sprite.go.transform.position : Vector3.zero;
    }
    public Vector3 ImageSize(string file, int index = 0)
    {
        var sprite = ShownSprite(file, index);
        return sprite != null ? sprite.go.transform.lossyScale : Vector3.zero;
    }
    public Renderer ImageRenderer(string file, int index = 0)
    {
        var sprite = ShownSprite(file, index);
        return sprite != null ? sprite.renderer : null;
    }

    BoardSprite ShownSprite(string file, int index)
    {
        if (file == null || !images.TryGetValue(file, out var image)) return null;
        int seen = 0;
        foreach (var sprite in image.sprites)
        {
            if (sprite.go == null || !sprite.go.activeSelf) continue;
            if (seen++ == index) return sprite;
        }
        return null;
    }

    // ---- 画像 ----

    void PrepareImages()
    {
        if (quiz?.items == null) return;
        foreach (var item in quiz.items)
        {
            Load(item.question);
            foreach (var file in item.options) Load(file);
            foreach (var file in item.boxes) Load(file);
            foreach (var file in item.numbers) Load(file);
            if (item.kind == QuizRules.Count) MarkImage(item);
            Load(item.pointImage);
            Load(item.ellipsisImage);
            Load(item.flowerImage);
        }
    }

    // count の「?」: QuizItemData.markImage、無ければ quiz/qmark.png(無くてもよい)。
    BoardImage MarkImage(QuizItemData item)
        => !string.IsNullOrWhiteSpace(item.markImage) ? Load(item.markImage) : Load(QuizRules.DefaultMarkImage, optional: true);

    BoardImage Load(string file, bool optional = false)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        if (images.TryGetValue(file, out var cached)) return cached;
        if (missingImages.Contains(file) || absentOptional.Contains(file)) return null;
        var texture = ReadTexture(file, !optional);
        if (texture == null)
        {
            if (optional) absentOptional.Add(file);
            else missingImages.Add(file);
            return null;
        }
        var image = new BoardImage
        {
            file = file, texture = texture, pixels = texture.height,
            aspect = texture.height > 0 ? (float)texture.width / texture.height : 1f
        };
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
                Debug.LogWarning("[Quiz] 試験用紙の画像の shader が無いか未対応です。URP Unlit で代えます。");
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

    // 1枚ぶんの板(材質は1枚ごと。同じ画像を違う濃さで並べるため)。
    BoardSprite NewSprite(BoardImage image)
    {
        var material = ImageMaterial(image.texture);
        if (material == null) return null;
        var go = new GameObject("QuizImage " + image.file + " " + image.sprites.Count, typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(board, false);
        go.GetComponent<MeshFilter>().sharedMesh = Quad();
        var renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.sortingLayerID = 0;
        renderer.sortingOrder = ImageSortingOrder;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        go.SetActive(false);
        return new BoardSprite { go = go, material = material, renderer = renderer };
    }

    // このコマの次の1枚を、中心 center(ワールド)・大きさ size(ワールド)で出す。色の a は濃さ。
    void Place(BoardImage image, Vector3 center, Vector2 size, Color color, int order = ImageSortingOrder)
    {
        if (image == null || image.texture == null || board == null) return;
        if (image.used >= image.sprites.Count)
        {
            var created = NewSprite(image);
            if (created == null) return;
            image.sprites.Add(created);
        }
        var sprite = image.sprites[image.used++];
        if (!sprite.go.activeSelf) sprite.go.SetActive(true);
        PlaceWorld(sprite.go.transform, center, new Vector3(size.x, size.y, 1f));
        if (sprite.renderer.sortingOrder != order) sprite.renderer.sortingOrder = order;
        if (sprite.material != null && sprite.material.HasProperty(BaseColorId)) sprite.material.SetColor(BaseColorId, color);
    }

    // 親の拡大に左右されず、ワールドの位置と大きさで置く。
    static void PlaceWorld(Transform target, Vector3 position, Vector3 size)
    {
        target.SetPositionAndRotation(position, Quaternion.identity);
        Vector3 parent = target.parent != null ? target.parent.lossyScale : Vector3.one;
        target.localScale = new Vector3(Divide(size.x, parent.x), Divide(size.y, parent.y), Divide(size.z, parent.z));
    }

    static float Divide(float value, float by) => Mathf.Abs(by) > 1e-6f ? value / by : value;

    void HideUnusedSprites()
    {
        foreach (var image in images.Values)
            for (int i = image.used; i < image.sprites.Count; i++)
            {
                var sprite = image.sprites[i];
                if (sprite.go != null && sprite.go.activeSelf) sprite.go.SetActive(false);
            }
    }

    // ---- 毎コマの描き直し ----

    void DrawBoard(double time, float visibility)
    {
        if (marks != null) marks.Clear();
        foreach (var image in images.Values) image.used = 0;
        RedCircles = Squares = 0;
        GateOutlineDrawn = false;
        if (visibility > 0f && quiz?.items != null && marks != null)
        {
            DrawGateOutline(visibility);
            if (hasRhythm && InSection(rhythmSpan, time, QuizRules.PaperFadeSeconds)) DrawRhythm(time, visibility);
            // 斬り分けの板は、接近 2.0 秒では最初の問題より前から出る。
            if (hasSort && InSection(sortSpan, time, QuizRules.SortCoverLeadSeconds)) DrawSort(time, visibility);
            if (hasCount && InSection(countSpan, time, QuizRules.PaperFadeSeconds)) DrawCount(time, visibility);
            if (hasPi && InSection(piSpan, time, QuizRules.PaperFadeSeconds)) DrawPi(time, visibility);
        }
        if (marks != null) marks.Flush();
        HideUnusedSprites();
    }

    // 頂点色は線形の値としてそのまま使われるので、材質の色と同じ見え方になるよう直す(白と黒は変わらない)。
    static Color Vertex(Color color, float alpha)
    {
        Color linear = color.linear;
        linear.a = Mathf.Clamp01(alpha);
        return linear;
    }

    static Color White(float alpha) => new Color(1f, 1f, 1f, alpha);

    static bool InSection(QuizRules.Span span, double time, double lead)
        => time >= span.Start - lead && time < span.End + QuizRules.PaperFadeSeconds;

    // 判定面のゲートの枠(紙の間だけ、白い細線)。奥行きと判定面の場所を見せる。
    void DrawGateOutline(float visibility)
    {
        marks.Frame(QuizRules.GateCenter, judgeZ - MarkLift, QuizRules.GateHalfWidth * 2f, QuizRules.GateTopY - QuizRules.GateBottomY,
            QuizRules.GateLineWidth, Vertex(Color.white, QuizRules.GateLineAlpha * visibility));
        GateOutlineDrawn = true;
    }

    // 赤ペンの丸(札・数のまわり)。start から一筆で描く。
    void DrawPenCircle(Vector2 center, Vector2 size, double start, double time, float visibility)
    {
        float progress = QuizRules.PenProgress(time, start);
        if (progress <= 0f) return;
        RedCircles++;
        marks.PenEllipse(center, judgeZ - MarkLift, size.x * .5f + QuizRules.PenCirclePad, size.y * .5f + QuizRules.PenCirclePad,
            QuizRules.RedPenWidth, Vertex(QuizRules.RedPen, visibility), progress, QuizRules.PenOvershoot);
    }

    // 赤ペン・「…」・花丸を出す時刻。revealMs(譜面)、無ければその問題の最後のノーツが届く時刻。どちらも無ければ出さない。
    double RevealTime(int index, QuizItemData item)
    {
        if (item.revealMs > 0f) return QuizRules.Seconds(item.revealMs, offset);
        var times = itemTimes[index];
        return times.Count > 0 ? times[times.Count - 1] : double.NaN;
    }

    // ---- rhythm「答えの柱」 ----

    // 出ている問題の板(問題・札・赤ペン)と、柱の判定面の薄い四角。答えの柱の問題は A・C・D に分かれて、あいだに斬り分けが
    // 入るので、四角はどれかの答えの柱の問題が出ている間だけ描く(第3.1版、10/28)。
    void DrawRhythm(double time, float visibility)
    {
        bool any = false;
        for (int i = 0; i < quiz.items.Count; i++)
        {
            var item = quiz.items[i];
            if (item.kind != QuizRules.Rhythm || !QuizRules.Active(item, offset, time)) continue;
            any = true;
            DrawRhythmBoard(i, item, time, visibility);
        }
        if (!any) return;
        float z = judgeZ - MarkLift;
        for (int p = 0; p < QuizRules.PillarX.Length; p++)
        {
            marks.Square(new Vector2(QuizRules.PillarX[p], QuizRules.PillarY), z, QuizRules.PillarSquareSize,
                Vertex(Color.white, QuizRules.PillarSquareAlpha * visibility));
            Squares++;
        }
    }

    void DrawRhythmBoard(int index, QuizItemData item, double time, float visibility)
    {
        float z = judgeZ - ImageLift;
        var question = Load(item.question);
        Vector2 questionSize = Vector2.zero;
        float questionY = QuizRules.RhythmQuestionY;
        if (question != null)
        {
            // 背の高い問題(積分・Σ)は画素の比で大きく(上限あり)、下の端をそろえて上へ伸ばす。
            questionSize = QuizRules.LabelSize(question.pixels, question.aspect, QuizRules.RhythmQuestionHeight,
                QuizRules.RhythmQuestionReferencePixels, QuizRules.BoardMaxWidth);
            // 大きくするだけ(ふつうの高さより小さい問題は 0.85 のまま。幅の上限で縮んだときは縮んだまま)。
            if (questionSize.y < QuizRules.RhythmQuestionHeight && questionSize.x < QuizRules.BoardMaxWidth - 1e-4f)
                questionSize = QuizRules.FitSize(question.aspect, QuizRules.RhythmQuestionHeight, QuizRules.BoardMaxWidth);
            if (questionSize.y > QuizRules.RhythmQuestionMaxHeight)
                questionSize *= QuizRules.RhythmQuestionMaxHeight / questionSize.y;
            questionY = QuizRules.RhythmQuestionBottomY + questionSize.y * .5f;
            Place(question, new Vector3(0f, questionY, z), questionSize, White(visibility));
        }
        // 練習は最初から、ほかは最後の本物が届いたら、正しい札を赤い丸で囲む(end まで)。
        double penStart = item.hint ? QuizRules.Seconds(item.showMs, offset) : RevealTime(index, item);
        // 最後の問題は、答えが出たら問題の右に花丸を押す(膨らみながら)。
        var flower = item.hint ? null : Load(item.flowerImage);
        if (flower != null && QuizRules.Finite(penStart) && time >= penStart)
        {
            float amount = QuizRules.FlowerAmount(time, penStart);
            float h = QuizRules.FlowerHeight * Mathf.Lerp(.7f, 1f, amount);
            float x = questionSize.x * .5f + QuizRules.OptionFlowerGap + QuizRules.FlowerHeight * flower.aspect * .5f;
            Place(flower, new Vector3(x, questionY, z), new Vector2(h * flower.aspect, h), White(amount * visibility), MarkSortingOrder);
        }
        for (int p = 0; p < QuizRules.PillarX.Length && p < item.options.Count; p++)
        {
            var option = Load(item.options[p]);
            if (option == null) continue;
            // 背の高い札(分数)は画素の比で大きくして、ほかの札と字の大きさをそろえる。
            Vector2 size = QuizRules.LabelSize(option.pixels, option.aspect, QuizRules.OptionHeight,
                QuizRules.OptionReferencePixels, QuizRules.OptionMaxWidth);
            var center = new Vector2(QuizRules.PillarX[p], QuizRules.OptionY);
            bool flash = optionFlashPillar[index] == p && QuizRules.OptionFlashing(time, optionFlashStart[index]);
            Color color = flash ? UISkinPalette.NoteGold : Color.white;
            color.a = visibility;
            Place(option, new Vector3(center.x, center.y, z), size, color);
            if (p == item.answer) DrawPenCircle(center, size, penStart, time, visibility);
        }
    }

    // ---- sort「斬り分け」 ----

    // 左右の端の箱と真ん中の的の薄い四角、出ている問題の板(見出し・箱の語・札)。
    void DrawSort(double time, float visibility)
    {
        int active = ActiveSortItem(time);
        if (active < 0) return;
        float z = judgeZ - MarkLift;
        Color frame = Vertex(Color.white, QuizRules.BoxAlpha * visibility);
        for (int s = -1; s <= 1; s += 2)
            marks.Frame(QuizRules.BoxCenter(s), z, QuizRules.BoxWidth, QuizRules.BoxHeight, QuizRules.BoxLineWidth, frame);
        marks.Square(QuizRules.SortTarget, z, QuizRules.PillarSquareSize, Vertex(Color.white, QuizRules.PillarSquareAlpha * visibility));
        Squares++;
        DrawSortBoard(active, quiz.items[active], time, visibility);
    }

    // 札は、その金が出るときに少し明るくなり、届く時刻に答えの箱へ 0.25 秒で滑り込む。切れていれば金、逃した・違う向きなら灰色で積む。
    void DrawSortBoard(int index, QuizItemData item, double time, float visibility)
    {
        float z = judgeZ - ImageLift;
        var header = Load(item.question);
        if (header != null)
            Place(header, new Vector3(0f, QuizRules.SortHeaderY, z),
                QuizRules.FitSize(header.aspect, QuizRules.SortHeaderHeight, QuizRules.BoardMaxWidth), White(visibility));
        for (int s = 0; s < 2 && s < item.boxes.Count; s++)
        {
            var word = Load(item.boxes[s]);
            if (word == null) continue;
            Vector2 center = QuizRules.BoxWordCenter(s == 0 ? -1f : 1f);
            Place(word, new Vector3(center.x, center.y, z), QuizRules.LabelSize(word.pixels, word.aspect, QuizRules.BoxWordHeight,
                QuizRules.BoxWordReferencePixels, QuizRules.BoxWordMaxWidth), White(visibility));
        }
        int left = 0, right = 0;
        for (int k = 0; k < QuizRules.SortLabelX.Length && k < item.options.Count; k++)
        {
            float sign = QuizRules.SideSign(k < item.sides.Count ? item.sides[k] : "");
            int stack = sign < 0f ? left++ : sign > 0f ? right++ : 0;
            var label = Load(item.options[k]);
            if (label == null) continue;
            double hit = k < itemSlotCount[index] ? itemTimes[index][k]
                : QuizRules.Seconds(item.playMs, offset) + k * QuizRules.SortStepSeconds;
            double spawn = spawner != null ? spawner.SpawnTimeFor(hit) : hit - 1.0;
            var home = new Vector2(QuizRules.SortLabelX[k], QuizRules.SortLabelY);
            Vector2 fromSize = QuizRules.LabelSize(label.pixels, label.aspect, QuizRules.SortLabelHeight,
                QuizRules.SortLabelReferencePixels, QuizRules.SortLabelMaxWidth);
            float slide = sign != 0f ? QuizRules.SortSlide(time, hit) : 0f;
            Vector2 to = sign != 0f ? QuizRules.BoxSlot(sign, stack) : home;
            Vector2 toSize = QuizRules.LabelSize(label.pixels, label.aspect, QuizRules.BoxLabelHeight,
                QuizRules.SortLabelReferencePixels, QuizRules.BoxLabelMaxWidth);
            Vector2 center = Vector2.Lerp(home, to, slide);
            Vector2 size = Vector2.Lerp(fromSize, toSize, slide);
            int state = SlotState(index, k);
            Color color = state == SlotCut ? UISkinPalette.NoteGold : state == SlotMissed ? QuizRules.MissGray : Color.white;
            color.a = QuizRules.SortLabelAlpha(time, spawn) * visibility;
            Place(label, new Vector3(center.x, center.y, z), size, color);
        }
    }

    // ---- count「答えの形」 ----

    void DrawCount(double time, float visibility)
    {
        for (int i = 0; i < quiz.items.Count; i++)
        {
            var item = quiz.items[i];
            if (item.kind == QuizRules.Count && QuizRules.Active(item, offset, time)) DrawCountBoard(i, item, time, visibility);
        }
    }

    // 問題の「?」の所(slotX・slotY)に、最初は赤い「?」、金が届くたびに白い 1, 2, 3 …。最後の金のあと赤い丸。
    void DrawCountBoard(int index, QuizItemData item, double time, float visibility)
    {
        float z = judgeZ - ImageLift;
        var center = new Vector2(0f, QuizRules.CountQuestionY);
        var question = Load(item.question);
        Vector2 size = question != null ? QuizRules.FitSize(question.aspect, QuizRules.CountQuestionHeight, QuizRules.BoardMaxWidth)
            : new Vector2(0f, QuizRules.CountQuestionHeight);
        if (question != null) Place(question, new Vector3(center.x, center.y, z), size, White(visibility));
        // 「?」の所。slotX・slotY が無ければ問題の右の端の外。
        Vector2 slot = item.slotX > 0f || item.slotY > 0f
            ? QuizRules.SlotPoint(center, size, item.slotX, item.slotY)
            : new Vector2(center.x + size.x * .5f + QuizRules.CountQuestionHeight * .4f, center.y);
        // 途中の数は等号の右に出さない(「1 dozen = 8」のような間違った式になる。Codex の点検、10/28)。
        // 最後の金が届くまでは赤い「?」、そのあと答えの数と赤い丸。
        int count = time >= RevealTime(index, item) ? CountShown(index, time) : 0;
        BoardImage glyph;
        Color color;
        if (count <= 0)
        {
            glyph = MarkImage(item);
            color = QuizRules.RedPen;
        }
        else
        {
            glyph = item.numbers.Count > 0 ? Load(item.numbers[Mathf.Min(count, item.numbers.Count - 1)]) : null;
            color = Color.white;
        }
        if (glyph == null) return;
        float height = QuizRules.GlyphHeight(glyph.pixels, question != null ? question.pixels : 0, size.y,
            QuizRules.CountQuestionHeight * QuizRules.CountNumberScale,
            QuizRules.CountQuestionHeight * QuizRules.CountNumberMinScale, QuizRules.CountQuestionHeight * QuizRules.CountNumberMaxScale);
        var glyphSize = new Vector2(height * glyph.aspect, height);
        color.a = visibility;
        Place(glyph, new Vector3(slot.x, slot.y, z), glyphSize, color);
        if (count > 0) DrawPenCircle(slot, glyphSize, RevealTime(index, item), time, visibility);
    }

    // ---- pi「π を書く」 ----

    void DrawPi(double time, float visibility)
    {
        for (int i = 0; i < quiz.items.Count; i++)
        {
            var item = quiz.items[i];
            if (item.kind == QuizRules.Pi && QuizRules.Active(item, offset, time)) DrawPiBoard(i, item, time, visibility);
        }
    }

    // 1行目の左に「π =」。切ったマスに白い桁(逃したら灰色)、最初のマスは「3.」。行は埋まるたびに上へ送る。
    // マスの間隔は行の長さで決まる(QuizRules.PiPitch。11 マスの行は詰める)。
    // 最後のノーツのあと、最後の桁の右に「…」と、「π」の赤い花丸。字の下の端(並ぶ線)は行の高さ − 桁の高さの半分にそろえる。
    void DrawPiBoard(int index, QuizItemData item, double time, float visibility)
    {
        float z = judgeZ - ImageLift;
        var zero = item.numbers.Count > 0 ? Load(item.numbers[0]) : null;
        int reference = zero != null ? zero.pixels : 0;
        var label = Load(item.question);
        Vector2 labelCenter = default, labelSize = default;
        if (label != null)
        {
            float h = PiGlyphHeight(label, reference);
            labelSize = new Vector2(h * label.aspect, h);
            // 中心は x −2.45。広い画像でも最初のマスに重ねない。
            int firstRow = QuizRules.RowLength(item.rows, 0);
            float right = Mathf.Min(QuizRules.PiLabelX + labelSize.x * .5f,
                QuizRules.CellX(0, firstRow) - QuizRules.PiPitch(firstRow) * .5f - QuizRules.PiLabelGap);
            float baseline = RowY(0, time) - QuizRules.PiDigitHeight * .5f;
            labelCenter = new Vector2(right - labelSize.x * .5f, baseline + h * .5f);
            Place(label, new Vector3(labelCenter.x, labelCenter.y, z), labelSize, White(visibility));
        }
        int first = itemFirstSlot[index], cells = itemSlotCount[index];
        for (int k = 0; k < cells; k++)
        {
            int state = slotState[first + k];
            if (state == SlotWaiting) continue;
            QuizRules.CellOf(item.rows, k, out int row, out int column);
            int length = QuizRules.RowLength(item.rows, row);
            float baseline = RowY(row, time) - QuizRules.PiDigitHeight * .5f;
            float x = QuizRules.CellX(column, length);
            float alpha = (state == SlotCut ? 1f : QuizRules.PiMissAlpha) * visibility;
            int digit = QuizRules.Digit(item.digits, k);
            if (digit >= 0 && digit < item.numbers.Count) PlaceGlyph(Load(item.numbers[digit]), reference, x, baseline, alpha, z);
            // 最初のマスは「3.」。点は次のマスとのあいだ。
            if (k == 0) PlaceGlyph(Load(item.pointImage), reference, x + QuizRules.PiPitch(length) * .5f, baseline, alpha, z);
        }
        double reveal = RevealTime(index, item);
        if (cells <= 0 || !QuizRules.Finite(reveal) || time < reveal) return;
        QuizRules.CellOf(item.rows, cells - 1, out int lastRow, out int lastColumn);
        int lastLength = QuizRules.RowLength(item.rows, lastRow);
        var ellipsis = Load(item.ellipsisImage);
        if (ellipsis != null)
        {
            float h = PiGlyphHeight(ellipsis, reference);
            float leftEdge = QuizRules.CellX(lastColumn, lastLength) + QuizRules.PiPitch(lastLength) * .5f;
            float baseline = RowY(lastRow, time) - QuizRules.PiDigitHeight * .5f;
            Place(ellipsis, new Vector3(leftEdge + h * ellipsis.aspect * .5f, baseline + h * .5f, z),
                new Vector2(h * ellipsis.aspect, h), White(visibility));
        }
        var flower = Load(item.flowerImage);
        if (flower != null && label != null)
        {
            // 花丸の画像は赤で描いてある(色は掛けない)。「π」の中心(slotX・slotY、無ければ左寄り)に、膨らみながら出る。
            float amount = QuizRules.FlowerAmount(time, reveal);
            bool given = item.slotX > 0f || item.slotY > 0f;
            Vector2 at = QuizRules.SlotPoint(labelCenter, labelSize, given ? item.slotX : QuizRules.PiGlyphX,
                given ? item.slotY : QuizRules.PiGlyphY);
            float h = QuizRules.FlowerHeight * Mathf.Lerp(.7f, 1f, amount);
            Place(flower, new Vector3(at.x, at.y, z), new Vector2(h * flower.aspect, h), White(amount * visibility), MarkSortingOrder);
        }
    }

    float PiGlyphHeight(BoardImage glyph, int reference)
        => QuizRules.GlyphHeight(glyph.pixels, reference, QuizRules.PiDigitHeight, QuizRules.PiDigitHeight, .05f, QuizRules.PiDigitHeight * 2.5f);

    // 字を x を中心に、下の端を baseline にそろえて置く。
    void PlaceGlyph(BoardImage glyph, int reference, float x, float baseline, float alpha, float z)
    {
        if (glyph == null) return;
        float h = PiGlyphHeight(glyph, reference);
        Place(glyph, new Vector3(x, baseline + h * .5f, z), new Vector2(h * glyph.aspect, h), White(alpha));
    }

    // ---- 片付け ----

    void HideBoard()
    {
        foreach (var image in images.Values)
            foreach (var sprite in image.sprites)
                if (sprite.go != null) sprite.go.SetActive(false);
    }

    void DisposeBoard()
    {
        foreach (var image in images.Values)
        {
            foreach (var sprite in image.sprites)
            {
                UISkinKit.SafeDestroy(sprite.go);
                UISkinKit.SafeDestroy(sprite.material);
            }
            image.sprites.Clear();
            UISkinKit.SafeDestroy(image.texture);
        }
        images.Clear();
        UISkinKit.SafeDestroy(quad);
        if (board != null) UISkinKit.SafeDestroy(board.gameObject);
        quad = null;
        board = null;
    }
}
