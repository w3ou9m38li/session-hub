using System.Diagnostics;
using System.Text;
using ClaudeLauncher.Infrastructure.Compare;
using Xunit.Abstractions;

namespace ClaudeLauncher.IntegrationTests;

/// <summary>
/// Lining up two conversations' prompts: the same request on one row, one asked on one side
/// alone on a row of its own, both sides in their own order — and fast at thousands a side.
/// </summary>
public sealed class PromptAlignerTests(ITestOutputHelper output)
{
    /// <summary>The alignment in short: <c>0=0</c> identical, <c>1~2</c> alike, <c>3-</c> A alone, <c>-4</c> B alone.</summary>
    private static string Shape(PromptAlignment alignment) => string.Join(" ", alignment.Pairs.Select(p =>
        p.A < 0 ? $"-{p.B}" : p.B < 0 ? $"{p.A}-" : p.Identical ? $"{p.A}={p.B}" : $"{p.A}~{p.B}"));

    private static PromptAlignment Align(string?[] a, string?[] b) => PromptAligner.Align(a, b);

    /// <summary>Every prompt of each side exactly once, each side in its own order.</summary>
    private static void AssertOrderKept(PromptAlignment alignment, int n, int m)
    {
        var a = alignment.Pairs.Where(p => p.A >= 0).Select(p => p.A).ToList();
        var b = alignment.Pairs.Where(p => p.B >= 0).Select(p => p.B).ToList();
        Assert.Equal(Enumerable.Range(0, n), a);
        Assert.Equal(Enumerable.Range(0, m), b);
    }

    [Fact]
    public void PromptsAreComparedWithoutCaseWidthOrSpacing()
    {
        Assert.Equal("fix the bug 改一下", PromptAligner.Normalize("  ＦＩＸ  the\tBug\n改一下 "));
        Assert.Equal("", PromptAligner.Normalize(null));
        Assert.Equal(1, PromptAligner.Similarity("Fix  the bug", "fix the BUG"));
        // ab bc cd against ab bc ce: two of three pairs shared each side.
        Assert.Equal(4 / 6.0, PromptAligner.Similarity("abcd", "abce"), 6);
        Assert.Equal(0, PromptAligner.Similarity("a", "b"));
    }

    [Fact]
    public void TheSamePromptsLineUpRowByRow()
    {
        var alignment = Align(
            ["修复登录页", "加上测试", "/compact", "ＦＩＸ the  bug"],
            ["修复登录页", "加上测试 ", "/compact", "fix the bug"]);

        Assert.Equal("0=0 1=1 2=2 3=3", Shape(alignment));
        Assert.All(alignment.Pairs, p => Assert.Equal(1, p.Similarity));
        Assert.False(alignment.Approximate);
    }

    [Fact]
    public void APromptAskedOnOneSideOnlyStandsAloneWhereItWasAsked()
    {
        string[] p = ["写一个解析器", "给它加单元测试", "提交并推送"];

        // Inserted in B: before, between and after the shared ones.
        Assert.Equal("-0 0=1 -2 1=3 2=4 -5", Shape(Align(p,
            ["先看看目录结构", "写一个解析器", "顺便解释一下正则", "给它加单元测试", "提交并推送", "写发布说明"])));

        // Asked only in A.
        Assert.Equal("0=0 1- 2=1", Shape(Align(p, ["写一个解析器", "提交并推送"])));

        // Unmatched on both sides between the same anchors: A's first, then B's.
        Assert.Equal("0=0 1- -1 2=2", Shape(Align(p, ["写一个解析器", "完全不同的请求", "提交并推送"])));
    }

    [Fact]
    public void ReorderedPromptsKeepTheLongestRunInCommonOrder()
    {
        var alignment = Align(["一", "二二", "三三三", "四四四四"], ["三三三", "四四四四", "一", "二二"]);

        AssertOrderKept(alignment, 4, 4);
        Assert.Equal(2, alignment.Pairs.Count(p => p.Matched));
        Assert.All(alignment.Pairs.Where(p => p.Matched), p => Assert.True(p.Identical));
    }

    [Fact]
    public void NearlyTheSamePromptMatchesAsAlikeAndAnUnrelatedOneDoesNot()
    {
        var alike = Align(["请把登录页面的按钮改成蓝色"], ["请把登录页的按钮改成蓝色吧"]);
        var pair = Assert.Single(alike.Pairs);
        Assert.Equal("0~0", Shape(alike));
        Assert.InRange(pair.Similarity, PromptAligner.SimilarAt, 0.999);
        Assert.Equal(PromptAligner.Similarity("请把登录页面的按钮改成蓝色", "请把登录页的按钮改成蓝色吧"), pair.Similarity, 9);

        Assert.Equal("0- -0", Shape(Align(["解释一下这个报错"], ["把 README 翻译成英文"])));
    }

    [Fact]
    public void AnIdenticalPromptWinsOverAMerelyAlikeOne()
    {
        // B's first prompt reads like A's, its second is A's exactly: the identical one is matched.
        Assert.Equal("-0 0=1", Shape(Align(["运行全部测试并修复失败的用例"], ["运行全部测试并修复失败用例", "运行全部测试并修复失败的用例"])));
    }

    [Fact]
    public void RepeatedPromptsPairUpFromTheStart()
    {
        Assert.Equal("0=0 1=1 2-", Shape(Align(["继续", "继续", "继续"], ["继续", "继续"])));
        Assert.Equal("0=0 -1 1=2", Shape(Align(["继续", "好的"], ["继续", "继续", "好的"])));
    }

    [Fact]
    public void EmptySidesAndPromptlessTurns()
    {
        Assert.Equal("", Shape(Align([], [])));
        Assert.Equal("-0 -1", Shape(Align([], ["一", "二"])));
        Assert.Equal("0-", Shape(Align(["一"], [])));

        // What the assistant said before any prompt has no prompt to match, even against another such.
        Assert.Equal("0- -0 1=1", Shape(Align([null, "问题"], [null, "问题"])));
    }

    [Fact]
    public void EveryAlikePairIsFoundWithoutComparingEveryPair()
    {
        // A few short texts over a small alphabet, each edited a little in every prompt: many
        // pairs are alike, near the threshold and on both sides of it.
        var random = new Random(7);
        const string letters = "abcde的了是";
        var texts = Enumerable.Range(0, 12)
            .Select(_ => new string(Enumerable.Range(0, random.Next(4, 14)).Select(_ => letters[random.Next(letters.Length)]).ToArray()))
            .ToArray();
        string Prompt()
        {
            var text = new StringBuilder(texts[random.Next(texts.Length)]);
            for (var edits = random.Next(4); edits > 0; edits--)
            {
                var at = random.Next(text.Length);
                var letter = letters[random.Next(letters.Length)];
                switch (random.Next(3))
                {
                    case 0: text[at] = letter; break;
                    case 1: text.Insert(at, letter); break;
                    default: if (text.Length > 2) text.Remove(at, 1); break;
                }
            }
            return text.ToString();
        }
        var a = Enumerable.Range(0, 150).Select(_ => Prompt()).ToArray();
        var b = Enumerable.Range(0, 150).Select(_ => Prompt()).ToArray();

        var expected = new List<(int, int)>();
        for (var i = 0; i < a.Length; i++)
            for (var j = 0; j < b.Length; j++)
                if (PromptAligner.Normalize(a[i]) != PromptAligner.Normalize(b[j])
                    && PromptAligner.Similarity(a[i], b[j]) >= PromptAligner.SimilarAt)
                    expected.Add((i, j));

        var found = PromptAligner.SimilarPairs(a, b);
        Assert.True(expected.Count > 200, $"the fixture should have many alike pairs, has {expected.Count}");
        Assert.Equal(expected, found.Select(f => (f.A, f.B)).Order().ToList());
        Assert.All(found, f => Assert.Equal(PromptAligner.Similarity(a[f.A], b[f.B]), f.Similarity, 9));
    }

    /// <summary>Prompts of a few varied words each, like people write them.</summary>
    private static List<string> Prompts(Random random, int count)
    {
        var words = Enumerable.Range(0, 3000).Select(_ => new string(
            Enumerable.Range(0, random.Next(2, 5)).Select(_ => random.Next(3) == 0
                ? (char)('a' + random.Next(26))
                : (char)(0x4E00 + random.Next(2500))).ToArray())).ToArray();
        return [.. Enumerable.Range(0, count).Select(_ =>
            string.Join(" ", Enumerable.Range(0, random.Next(4, 14)).Select(_ => words[random.Next(words.Length)])))];
    }

    [Fact]
    public void TwoThousandPromptsASideLineUpExactlyComparingFewPairs()
    {
        var random = new Random(11);
        var a = Prompts(random, 2000);
        var extra = Prompts(random, 100);
        // B: A with every 20th prompt reworded slightly, every 25th left out and a new one put in every 30th place.
        var b = new List<string>();
        var reworded = new Dictionary<int, int>();
        for (var i = 0; i < a.Count; i++)
        {
            if (i % 30 == 7 && b.Count < 2000) b.Add(extra[i / 30]);
            if (i % 25 == 3) continue;
            if (i % 20 == 11)
            {
                reworded[i] = b.Count;
                b.Add(a[i] + "吧");
            }
            else b.Add(a[i]);
        }
        Assert.InRange(b.Count, 1900, 2000);

        var clock = Stopwatch.StartNew();
        var alignment = PromptAligner.Align(a, b);
        output.WriteLine($"{a.Count} × {b.Count}: {clock.ElapsedMilliseconds} ms, {alignment.Compared:N0} pairs compared");

        Assert.False(alignment.Approximate);
        AssertOrderKept(alignment, a.Count, b.Count);
        foreach (var (i, j) in reworded)
            Assert.Contains(alignment.Pairs, p => p.A == i && p.B == j && !p.Identical && p.Similarity >= PromptAligner.SimilarAt);
        Assert.Equal(a.Count - a.Count / 25 - reworded.Count, alignment.Pairs.Count(p => p.Identical));
        // Every pair would be four million; the index leaves a small share of them (about 63,000 here).
        Assert.True(alignment.Compared < 200_000, $"{alignment.Compared:N0} pairs compared");
    }

    [Fact]
    public void BeyondTheExactLimitUniquePromptsAnchorAndOrderHolds()
    {
        var random = new Random(5);
        var a = Prompts(random, 2200);
        var b = new List<string?>(a);
        b.RemoveAt(100);
        b.Insert(500, "一条只在右边的新提问");
        b[900] = a[900] + "吧";
        for (var i = 0; i < 40; i++) b.Insert(1500, "继续");      // repeated: no anchor, lined up in their stretch

        var alignment = PromptAligner.Align(a, b);

        Assert.True(alignment.Approximate);
        AssertOrderKept(alignment, a.Count, b.Count);
        Assert.Equal(a.Count - 2, alignment.Pairs.Count(p => p.Identical));
        Assert.Contains(alignment.Pairs, p => p.A == 100 && p.B < 0);
        Assert.Contains(alignment.Pairs, p => p.A < 0 && b[p.B] == "一条只在右边的新提问");
        // The stretch around the reworded prompt is small enough to be lined up exactly.
        Assert.Contains(alignment.Pairs, p => p.A == 900 && p.B >= 0 && !p.Identical);
    }

    [Fact]
    public void AStretchTooLargeEvenWhenAnchoredMatchesIdenticalPromptsInOrder()
    {
        // Nothing is asked just once: no anchors, one stretch of 2,100 × 2,150.
        var a = Enumerable.Range(0, 2100).Select(i => i % 3 == 0 ? "继续" : "好").ToArray();
        var b = Enumerable.Range(0, 2150).Select(i => i % 2 == 0 ? "继续" : "好").ToArray();

        var alignment = PromptAligner.Align(a, b);

        Assert.True(alignment.Approximate);
        AssertOrderKept(alignment, a.Length, b.Length);
        Assert.All(alignment.Pairs.Where(p => p.Matched), p => Assert.Equal(a[p.A], b[p.B]));
        Assert.True(alignment.Pairs.Count(p => p.Matched) > 1000);
        Assert.Equal(0, alignment.Compared);
    }

    [Fact]
    public void PromptsThatAllReadAlikeStopBeingComparedAndIdenticalOnesStillMatch()
    {
        // One template throughout: every pair reads alike, nothing can be filtered out.
        var a = Enumerable.Range(0, 300).Select(i => $"请检查第 {i} 个文件里的错误并修复，然后运行测试").ToList();
        var b = Enumerable.Range(0, 300).Select(i => $"请检查第 {i + 3} 个文件里的错误并修复，然后跑一遍测试").ToList();
        b[250] = a[280];

        var whole = PromptAligner.Align(a, b);
        var cut = PromptAligner.Align(a, b, default, maxComparedSteps: 20_000);

        Assert.False(whole.Approximate);
        Assert.True(whole.Compared > 80_000);
        Assert.True(cut.Approximate);
        Assert.True(cut.Compared < whole.Compared / 10);
        AssertOrderKept(cut, a.Count, b.Count);
        Assert.Contains(cut.Pairs, p => p is { A: 280, B: 250, Identical: true });
    }

    [Fact]
    public void ACancelledAlignmentStops()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PromptAligner.Align(["一"], ["一"], cancel.Token));
    }

    [Fact]
    public void ALongPastedPromptIsJudgedByItsBeginning()
    {
        var head = new StringBuilder();
        for (var i = 0; i < 400; i++) head.Append("段落").Append(i).Append(' ');
        var a = head + new string('甲', 50_000);
        var b = head + new string('乙', 50_000);

        var pair = Assert.Single(PromptAligner.Align([a], [b]).Pairs);
        Assert.False(pair.Identical);
        Assert.Equal(1, pair.Similarity, 9);
        // Alike by the opening alone, and the pair says so: what follows was never looked at.
        Assert.True(pair.OpeningOnly);
    }

    [Fact]
    public void APairSaysWhenOnlyTheOpeningWasCompared()
    {
        var opening = string.Concat(Enumerable.Range(0, 30).Select(i => $"第{i:00}步做完了再看下"));
        Assert.Equal(PromptAligner.ComparedChars, opening.Length);

        // Both within the compared length: the whole prompts were compared.
        var whole = Assert.Single(Align([opening], [opening[..^1] + "改"]).Pairs);
        Assert.False(whole.Identical);
        Assert.False(whole.OpeningOnly);

        // One letter more on either side, and the likeness is the opening's.
        var longer = Assert.Single(Align([opening + "甲"], [opening[..^1] + "改"]).Pairs);
        Assert.True(longer.Matched);
        Assert.True(longer.OpeningOnly);
        Assert.True(Assert.Single(Align([opening], [opening + "乙"]).Pairs).OpeningOnly);

        // Identity is judged on the whole prompt: identical long ones were compared whole.
        var same = Assert.Single(Align([opening + "甲"], [opening + "甲"]).Pairs);
        Assert.True(same.Identical);
        Assert.False(same.OpeningOnly);
    }
}
