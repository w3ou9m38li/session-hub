using ClaudeLauncher.Infrastructure.Semantic;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// What the model is given to read: BERT's tokenizer exactly as the models were trained with, and
/// each message cut into pieces short enough to read whole.
/// </summary>
public sealed class SemanticTextTests
{
    private static readonly string[] Vocabulary =
    [
        "[PAD]", "[UNK]", "[CLS]", "[SEP]", "中", "文", "搜", "索", "un", "##aff", "##able", "hello", "hell", "##o",
        ",", "!", "-", "cafe", "a", "b", "引", "物", "kasp", "##p",
    ];

    private static WordPieceTokenizer Tokenizer(bool lowercase = true) => new(Vocabulary, lowercase);

    [Fact]
    public void WordsArePiecedTogetherLongestFirst()
    {
        var tokenizer = Tokenizer();
        Assert.Equal(["un", "##aff", "##able"], tokenizer.Tokenize("unaffable"));
        Assert.Equal(["hello", "##o"], tokenizer.Tokenize("helloo"));               // the longest piece first, not "hell"
        Assert.Equal(["[UNK]"], tokenizer.Tokenize("unxyz"));                       // one piece missing: the whole word is unknown
        Assert.Equal(["[UNK]"], tokenizer.Tokenize(new string('a', 101)));          // too long to piece at all
        Assert.Equal(["a"], tokenizer.Tokenize(new string('a', 1)));
    }

    [Fact]
    public void EveryChineseCharacterIsAWordOfItsOwn()
    {
        var tokenizer = Tokenizer();
        Assert.Equal(["中", "文", "搜", "索"], tokenizer.Tokenize("中文搜索"));
        Assert.Equal(["kasp", "引", "物"], tokenizer.Tokenize("KASP引物"));          // no space needed between them
        Assert.Equal(["中", "[UNK]", "文"], tokenizer.Tokenize("中 的 文"));          // a character not in the vocabulary
    }

    [Fact]
    public void CaseAccentsPunctuationAndControlCharacters()
    {
        var tokenizer = Tokenizer();
        Assert.Equal(["hello", ",", "[UNK]", "!"], tokenizer.Tokenize("Hello, World!"));
        Assert.Equal(["cafe"], tokenizer.Tokenize("Café"));                         // lower case, accent stripped
        Assert.Equal(["a", "-", "b"], tokenizer.Tokenize("a-b"));
        Assert.Equal(["中", "文"], tokenizer.Tokenize("中\u0000文​\t"));        // control and format characters dropped

        var cased = Tokenizer(lowercase: false);
        Assert.Equal(["[UNK]"], cased.Tokenize("Café"));                            // a cased vocabulary keeps both
        Assert.Equal(["[UNK]", ",", "[UNK]", "!"], cased.Tokenize("Hello, World!"));
    }

    [Fact]
    public void EncodingWrapsInClsAndSepAndCutsToTheLimit()
    {
        var tokenizer = Tokenizer();
        Assert.Equal([2L, 4, 5, 6, 7, 3], tokenizer.Encode("中文搜索", 512));
        Assert.Equal([2L, 4, 5, 3], tokenizer.Encode("中文搜索", 4));
        Assert.Equal([2L, 8, 9, 3], tokenizer.Encode("unaffable", 4));              // cut inside a word's pieces too
        Assert.Equal([2L, 3], tokenizer.Encode("", 512));
        Assert.Throws<SemanticUnavailableException>(() => new WordPieceTokenizer(["a", "b"]));
    }

    [Fact]
    public void ShortMessagesAreNotPieces()
    {
        Assert.Empty(TextChunker.Chunk("好的"));
        Assert.Empty(TextChunker.Chunk("继续      "));
        var one = Assert.Single(TextChunker.Chunk("  KASP 引物怎么设计  "));
        Assert.Equal("KASP 引物怎么设计", one.Text);
        Assert.Equal(2, one.Start);
    }

    [Fact]
    public void LongMessagesAreCutAtSentencesWithOverlap()
    {
        var sentence = "候选基因的位置先要确定下来，然后才能设计引物。";              // 23 characters
        var text = string.Concat(Enumerable.Repeat(sentence, 60));
        var chunks = TextChunker.Chunk(text);

        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= TextChunker.Window));
        Assert.All(chunks.Take(chunks.Count - 1), c => Assert.EndsWith("。", c.Text));   // cut at a sentence end
        Assert.All(chunks.Skip(1), c => Assert.StartsWith("候选", c.Text));              // and begun at one
        Assert.Equal(0, chunks[0].Start);
        for (var i = 1; i < chunks.Count; i++)
        {
            var end = chunks[i - 1].Start + chunks[i - 1].Text.Length;
            Assert.True(chunks[i].Start < end, "pieces overlap");
            Assert.True(end - chunks[i].Start <= TextChunker.Overlap, "by a little");
            Assert.Equal(text.Substring(chunks[i].Start, chunks[i].Text.Length), chunks[i].Text);
        }
        Assert.Equal(text.Length, chunks[^1].Start + chunks[^1].Text.Length);           // to the end
    }

    [Fact]
    public void LinesComeBeforeSentencesAndTextWithoutEitherIsCutAtTheLimit()
    {
        var lines = string.Join('\n', Enumerable.Range(0, 30).Select(i => $"第 {i:00} 行：用 GEMMA 跑一遍 LMM。"));
        Assert.All(TextChunker.Chunk(lines).SkipLast(1), c => Assert.Matches(@"^第 \d\d 行[\s\S]*。$", c.Text));

        var blob = new string('x', 1000);
        Assert.Equal([0, 350, 700], TextChunker.Chunk(blob).Select(c => c.Start));
        Assert.All(TextChunker.Chunk(blob), c => Assert.True(c.Text.Length <= 400));
    }

    [Fact]
    public void SnippetsAreOneLineAndShort()
    {
        Assert.Equal("第一行 第二行", TextChunker.Snippet("第一行\n\n  第二行  "));
        var snippet = TextChunker.Snippet(new string('长', 300));
        Assert.Equal(161, snippet.Length);
        Assert.EndsWith("…", snippet);
    }

    [Fact]
    public void ModelSettingsDefaultToBgeAndSayWhatIsWrong()
    {
        var defaults = EmbeddingConfig.Parse("{}");
        Assert.Equal(new EmbeddingConfig(), defaults);
        Assert.Equal(EmbeddingPooling.Cls, defaults.Pooling);
        Assert.True(defaults.Normalize);
        Assert.Equal(512, defaults.MaxTokens);
        Assert.True(defaults.Lowercase);

        var e5 = EmbeddingConfig.Parse("""{ "pooling": "mean", "queryPrefix": "query: ", "passagePrefix": "passage: ", "maxTokens": 256, }""");
        Assert.Equal(EmbeddingPooling.Mean, e5.Pooling);
        Assert.Equal("query: ", e5.QueryPrefix);
        Assert.Equal("passage: ", e5.PassagePrefix);
        Assert.Equal(256, e5.MaxTokens);

        Assert.Contains("pooling", Assert.Throws<SemanticUnavailableException>(() => EmbeddingConfig.Parse("""{"pooling":"max"}""")).Message);
        Assert.Contains("maxTokens", Assert.Throws<SemanticUnavailableException>(() => EmbeddingConfig.Parse("""{"maxTokens":2}""")).Message);
        Assert.Contains("JSON", Assert.Throws<SemanticUnavailableException>(() => EmbeddingConfig.Parse("pooling: cls")).Message);

        // The model's id follows its files and its settings.
        var folder = Path.Combine(Path.GetTempPath(), "launcher-semantic-id", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "model.onnx"), "one");
            var first = EmbeddingConfig.ModelIdOf(folder, defaults);
            Assert.Equal(first, EmbeddingConfig.ModelIdOf(folder, defaults));
            Assert.NotEqual(first, EmbeddingConfig.ModelIdOf(folder, e5));
            File.WriteAllText(Path.Combine(folder, "model.onnx"), "another");
            Assert.NotEqual(first, EmbeddingConfig.ModelIdOf(folder, defaults));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
