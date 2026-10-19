using System.Text;

namespace ClaudeLauncher.Infrastructure.Compare;

/// <summary>A row of an alignment: a prompt of each side that match, or one side's prompt alone (the other index −1).</summary>
/// <param name="Similarity">How alike the two read, 0 to 1; 0 for a prompt alone.</param>
/// <param name="Identical">The same prompt once case, character width and spacing are set aside.</param>
/// <param name="OpeningOnly">
/// Alike by their first <see cref="PromptAligner.ComparedChars"/> characters, all that was
/// compared: one of them is longer, and what follows was not looked at.
/// </param>
public readonly record struct PromptPair(int A, int B, double Similarity, bool Identical, bool OpeningOnly = false)
{
    public bool Matched => A >= 0 && B >= 0;
}

/// <summary>Two lists of prompts lined up: every prompt of each side exactly once, each side in its own order.</summary>
/// <param name="Approximate">Too much to line up exactly: identical prompts were matched in order, alike ones may stand apart.</param>
public sealed record PromptAlignment(IReadOnlyList<PromptPair> Pairs, bool Approximate)
{
    /// <summary>How many pairs of prompts were compared letter pair by letter pair: what the alignment cost.</summary>
    internal long Compared { get; init; }
}

/// <summary>
/// Lines up the prompts of two conversations, so the same request asked in both sits on one row
/// and one asked in only one of them stands alone, in order on both sides.
///
/// <para>Prompts match when they are the same once case, character width and spacing are set
/// aside, or when they read alike: the Dice coefficient of their letter pairs is at least
/// <see cref="SimilarAt"/>. The alignment is the order-preserving one with the greatest total
/// likeness — an LCS in which a match weighs what it is alike. Only pairs that can reach the
/// threshold are compared at all: each prompt's rarest letter pairs are indexed, and two prompts
/// that share none of them cannot be alike enough (prefix filtering, with PPJoin's positional
/// filter), so 2,000 everyday prompts a side compare about one pair in a hundred, in about a
/// tenth of a second. Prompts that all read alike (one template, two thousand times) leave nothing
/// to filter: the comparing stops after <see cref="MaxComparedSteps"/> steps, identical prompts
/// still match, and the alignment says it is approximate. Past <see cref="MaxExactCells"/> pairs,
/// prompts asked exactly once on each side anchor the alignment and only the stretches between
/// them are lined up exactly; a stretch still too large matches identical prompts greedily, in order.</para>
///
/// <para>Everything lives in one call: nothing is cached between alignments.</para>
/// </summary>
public static class PromptAligner
{
    /// <summary>The share of letter pairs two prompts must have in common to count as alike.</summary>
    public const double SimilarAt = 0.6;

    /// <summary>Largest table of prompt pairs lined up exactly (2,000 × 2,000); beyond it the alignment is anchored.</summary>
    public const long MaxExactCells = 4_000_000;

    /// <summary>
    /// How much of a prompt its likeness is judged on: most prompts are shorter, and a pasted
    /// document is alike by its beginning. (Identity is judged on the whole prompt.)
    /// </summary>
    public const int ComparedChars = 300;

    /// <summary>Index entries and letter pairs stepped over comparing prompts before likeness is given up for identity alone (about a second).</summary>
    internal const long MaxComparedSteps = 100_000_000;

    /// <summary>
    /// A prompt as it is compared: compatibility forms folded (full-width letters, ligatures),
    /// lower case, every run of white space one space, nothing at either end. A slash command
    /// arrives as what was typed (<c>/compact</c>) and is compared as that text.
    /// </summary>
    public static string Normalize(string? prompt)
    {
        if (string.IsNullOrEmpty(prompt)) return string.Empty;
        var text = prompt;
        try
        {
            text = prompt.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // A lone surrogate: compared as it is.
        }
        var folded = new StringBuilder(text.Length);
        var gap = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                gap = folded.Length > 0;
                continue;
            }
            if (gap) folded.Append(' ');
            gap = false;
            folded.Append(char.ToLowerInvariant(c));
        }
        return folded.ToString();
    }

    /// <summary>
    /// How alike two prompts read, 0 to 1: 1 when they are the same once normalised, otherwise
    /// the Dice coefficient of the letter pairs of their first <see cref="ComparedChars"/> characters.
    /// </summary>
    public static double Similarity(string? a, string? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x == y) return 1;
        var pairs = new Dictionary<uint, int>();
        var lx = Math.Min(x.Length, ComparedChars) - 1;
        var ly = Math.Min(y.Length, ComparedChars) - 1;
        if (lx <= 0 || ly <= 0) return 0;
        for (var i = 0; i < lx; i++)
        {
            var pair = Pair(x, i);
            pairs[pair] = pairs.GetValueOrDefault(pair) + 1;
        }
        var shared = 0;
        for (var i = 0; i < ly; i++)
        {
            var pair = Pair(y, i);
            if (pairs.GetValueOrDefault(pair) is > 0 and var left)
            {
                pairs[pair] = left - 1;
                shared++;
            }
        }
        return 2.0 * shared / (lx + ly);
    }

    /// <summary>Lines up <paramref name="a"/>'s prompts with <paramref name="b"/>'s. A null prompt never matches.</summary>
    public static PromptAlignment Align(IReadOnlyList<string?> a, IReadOnlyList<string?> b,
        CancellationToken cancellation = default) =>
        new Aligner(a, b, cancellation, MaxComparedSteps).Run();

    /// <summary>The same, with the comparing stopped after <paramref name="maxComparedSteps"/> steps.</summary>
    internal static PromptAlignment Align(IReadOnlyList<string?> a, IReadOnlyList<string?> b,
        CancellationToken cancellation, long maxComparedSteps) =>
        new Aligner(a, b, cancellation, maxComparedSteps).Run();

    /// <summary>Every pair of prompts that read alike without being identical, found the way the alignment finds them.</summary>
    internal static List<(int A, int B, double Similarity)> SimilarPairs(IReadOnlyList<string?> a, IReadOnlyList<string?> b) =>
        new Aligner(a, b, default, long.MaxValue).SimilarPairs();

    private static uint Pair(string text, int at) => ((uint)text[at] << 16) | text[at + 1];

    private sealed class Aligner
    {
        private const byte Diagonal = 0, Up = 1, Left = 2;

        /// <summary>What an identical match weighs; a merely alike one weighs its likeness in thousandths, below this.</summary>
        private const int Whole = 1000;

        private readonly int _n, _m;
        private readonly CancellationToken _cancel;

        /// <summary>Per prompt — A's first, then B's — which identical group it is in; −1 for no prompt.</summary>
        private readonly int[] _group;

        /// <summary>Per prompt, whether it runs past <see cref="ComparedChars"/>: its likeness is that of its opening.</summary>
        private readonly bool[] _longer;

        /// <summary>
        /// Per prompt, its letter pairs as tokens, in one order shared by all: a pair's k-th
        /// occurrence is a token of its own (<c>pair rank &lt;&lt; 16 | k</c>), so shared tokens
        /// count the pairs two prompts have in common, repeats included. Pairs few prompts have come first.
        /// </summary>
        private readonly long[][] _tokens;

        private readonly long _maxSteps;
        private long _steps, _compared;
        private bool _overBudget, _anchored;

        public Aligner(IReadOnlyList<string?> a, IReadOnlyList<string?> b, CancellationToken cancellation, long maxSteps)
        {
            _n = a.Count;
            _m = b.Count;
            _cancel = cancellation;
            _maxSteps = maxSteps;
            _group = new int[_n + _m];
            _longer = new bool[_n + _m];
            _tokens = new long[_n + _m][];

            var groups = new Dictionary<string, int>(StringComparer.Ordinal);
            var ids = new Dictionary<uint, int>();
            var spread = new List<int>();               // per letter pair: how many prompts have it
            var seen = new Dictionary<uint, int>();
            for (var k = 0; k < _n + _m; k++)
            {
                if ((k & 255) == 0) _cancel.ThrowIfCancellationRequested();
                var prompt = k < _n ? a[k] : b[k - _n];
                if (prompt is null)
                {
                    _group[k] = -1;
                    _tokens[k] = [];
                    continue;
                }
                var text = Normalize(prompt);
                if (!groups.TryGetValue(text, out var group)) groups[text] = group = groups.Count;
                _group[k] = group;
                _longer[k] = text.Length > ComparedChars;

                var count = Math.Min(text.Length, ComparedChars) - 1;
                if (count <= 0)
                {
                    _tokens[k] = [];
                    continue;
                }
                var tokens = new long[count];
                seen.Clear();
                for (var i = 0; i < count; i++)
                {
                    var pair = Pair(text, i);
                    if (!ids.TryGetValue(pair, out var id))
                    {
                        ids[pair] = id = spread.Count;
                        spread.Add(0);
                    }
                    var before = seen.GetValueOrDefault(pair);
                    seen[pair] = before + 1;
                    if (before == 0) spread[id]++;
                    tokens[i] = ((long)id << 16) | (uint)before;        // before < ComparedChars < 2^16
                }
                _tokens[k] = tokens;
            }

            // Rarest first: the tokens a prompt is indexed by are then the ones few others share.
            var order = new int[spread.Count];
            for (var i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(spread.ToArray(), order);
            var rank = new long[order.Length];
            for (var r = 0; r < order.Length; r++) rank[order[r]] = r;
            foreach (var tokens in _tokens)
            {
                for (var i = 0; i < tokens.Length; i++) tokens[i] = (rank[tokens[i] >> 16] << 16) | (tokens[i] & 0xFFFF);
                Array.Sort(tokens);
            }
        }

        public PromptAlignment Run()
        {
            var pairs = new List<PromptPair>(_n + _m);
            if ((long)_n * _m <= MaxExactCells) Exact(0, _n, 0, _m, pairs);
            else Anchored(pairs);
            return new PromptAlignment(pairs, _anchored || _overBudget) { Compared = _compared };
        }

        // ---------------------------------------------------------------- exact

        /// <summary>B's prompts in a stretch, indexed: by identical group, and by the rarest tokens of each with where they stand.</summary>
        private sealed class Columns(int from, int count)
        {
            public readonly int From = from;
            public readonly int Count = count;
            public readonly Dictionary<int, List<int>> ByGroup = [];
            /// <summary>Per token, the prompts whose leading tokens hold it: where, and how many tokens the prompt has.</summary>
            public readonly Dictionary<long, List<(int Column, int Position, int Length)>> ByToken = [];

            /// <summary>
            /// The A prompt a column was last met for; the tokens it was seen to share with it then,
            /// −1 once ruled out; and the overlap it needs to be alike.
            /// </summary>
            public readonly int[] Stamp = new int[count];
            public readonly int[] Shared = new int[count];
            public readonly int[] Need = new int[count];
            public readonly List<int> Met = [];
        }

        private Columns Index(int bFrom, int bTo)
        {
            var columns = new Columns(bFrom, bTo - bFrom);
            Array.Fill(columns.Stamp, -1);
            for (var j = 0; j < columns.Count; j++)
            {
                var k = _n + bFrom + j;
                if (_group[k] < 0) continue;
                Add(columns.ByGroup, _group[k], j);
                var tokens = _tokens[k];
                var prefix = PrefixLength(tokens.Length);
                for (var p = 0; p < prefix; p++)
                {
                    if (!columns.ByToken.TryGetValue(tokens[p], out var list)) columns.ByToken[tokens[p]] = list = [];
                    list.Add((j, p, tokens.Length));
                }
            }
            return columns;
        }

        /// <summary>What matching A's prompt <paramref name="i"/> with each of the stretch's B prompts would weigh; 0 for no match.</summary>
        private void Weigh(int i, Columns columns, int[] weight)
        {
            Array.Clear(weight);
            if (_group[i] < 0) return;
            if (columns.ByGroup.TryGetValue(_group[i], out var same))
                foreach (var j in same) weight[j] = Whole;
            if (_overBudget) return;

            // Two prompts alike enough share a token among the rarest of each: only those are met.
            // Tokens are in one order everywhere, so once a shared one is met at position p in x
            // and q in y, the rest can add at most min(|x| − p, |y| − q) − 1: a prompt that can no
            // longer reach the overlap needed is ruled out unread (PPJoin's positional filter).
            var x = _tokens[i];
            var prefix = PrefixLength(x.Length);
            columns.Met.Clear();
            for (var p = 0; p < prefix; p++)
            {
                if (!columns.ByToken.TryGetValue(x[p], out var postings)) continue;
                _steps += postings.Count;
                foreach (var (j, q, ly) in postings)
                {
                    if (weight[j] != 0) continue;
                    if (columns.Stamp[j] != i)
                    {
                        columns.Stamp[j] = i;
                        if (MayReach(x.Length, ly))
                        {
                            columns.Shared[j] = 0;
                            columns.Need[j] = Needed(x.Length, ly);
                            columns.Met.Add(j);
                        }
                        else columns.Shared[j] = -1;
                    }
                    var shared = columns.Shared[j];
                    if (shared < 0) continue;
                    columns.Shared[j] = shared + 1 + Math.Min(x.Length - p - 1, ly - q - 1) < columns.Need[j] ? -1 : shared + 1;
                }
            }
            foreach (var j in columns.Met)
            {
                if (columns.Shared[j] < 0) continue;
                var dice = Dice(x, _tokens[_n + columns.From + j]);
                if (dice >= SimilarAt) weight[j] = Math.Clamp((int)Math.Round(dice * Whole), 1, Whole - 1);
            }
        }

        /// <summary>
        /// The order-preserving alignment of a stretch with the greatest total weight. Where
        /// leaving either prompt out scores the same, B's is left out first (the walk back runs
        /// from the end), so in reading order a stretch of unmatched prompts lists A's before B's.
        /// </summary>
        private void Exact(int aFrom, int aTo, int bFrom, int bTo, List<PromptPair> pairs)
        {
            int rows = aTo - aFrom, cols = bTo - bFrom;
            if (rows == 0 || cols == 0)
            {
                Unmatched(aFrom, aTo, bFrom, bTo, pairs);
                return;
            }
            var columns = Index(bFrom, bTo);
            var weight = new int[cols];
            var previous = new int[cols + 1];
            var current = new int[cols + 1];
            var move = new byte[rows * cols];
            for (var i = 0; i < rows; i++)
            {
                _cancel.ThrowIfCancellationRequested();
                Weigh(aFrom + i, columns, weight);
                current[0] = 0;
                var row = i * cols;
                for (var j = 1; j <= cols; j++)
                {
                    int up = previous[j], left = current[j - 1];
                    int best;
                    byte step;
                    if (up > left) (best, step) = (up, Up);
                    else (best, step) = (left, Left);
                    // Strictly better only: of equal alignments, a prompt asked again later stays alone
                    // and the earlier one is matched.
                    var w = weight[j - 1];
                    if (w > 0 && previous[j - 1] + w > best) (best, step) = (previous[j - 1] + w, Diagonal);
                    current[j] = best;
                    move[row + j - 1] = step;
                }
                (previous, current) = (current, previous);
            }

            var back = new List<PromptPair>(rows + cols);
            int ii = rows, jj = cols;
            while (ii > 0 || jj > 0)
            {
                var step = ii == 0 ? Left : jj == 0 ? Up : move[(ii - 1) * cols + jj - 1];
                switch (step)
                {
                    case Up:
                        back.Add(new PromptPair(aFrom + ii - 1, -1, 0, false));
                        ii--;
                        break;
                    case Left:
                        back.Add(new PromptPair(-1, bFrom + jj - 1, 0, false));
                        jj--;
                        break;
                    default:
                        back.Add(Match(aFrom + ii - 1, bFrom + jj - 1));
                        ii--;
                        jj--;
                        break;
                }
            }
            back.Reverse();
            pairs.AddRange(back);
        }

        private PromptPair Match(int i, int j) =>
            _group[i] == _group[_n + j]
                ? new PromptPair(i, j, 1, true)
                : new PromptPair(i, j, Math.Min(DiceOf(_tokens[i], _tokens[_n + j]), 1), false, _longer[i] || _longer[_n + j]);

        private static void Unmatched(int aFrom, int aTo, int bFrom, int bTo, List<PromptPair> pairs)
        {
            for (var i = aFrom; i < aTo; i++) pairs.Add(new PromptPair(i, -1, 0, false));
            for (var j = bFrom; j < bTo; j++) pairs.Add(new PromptPair(-1, j, 0, false));
        }

        // ------------------------------------------------------------- anchored

        /// <summary>
        /// Too many pairs to line up whole: prompts asked exactly once on each side, in the order
        /// both sides agree on, divide the rest into stretches, each lined up exactly when small
        /// enough and by identical prompts alone otherwise.
        /// </summary>
        private void Anchored(List<PromptPair> pairs)
        {
            _anchored = true;
            var inA = new Dictionary<int, int>();
            var inB = new Dictionary<int, (int Count, int At)>();
            for (var i = 0; i < _n; i++)
                if (_group[i] >= 0) inA[_group[i]] = inA.GetValueOrDefault(_group[i]) + 1;
            for (var j = 0; j < _m; j++)
                if (_group[_n + j] is >= 0 and var group) inB[group] = (inB.GetValueOrDefault(group).Count + 1, j);

            var unique = new List<(int A, int B)>();
            for (var i = 0; i < _n; i++)
                if (_group[i] >= 0 && inA[_group[i]] == 1 && inB.TryGetValue(_group[i], out var b) && b.Count == 1)
                    unique.Add((i, b.At));

            int aFrom = 0, bFrom = 0;
            foreach (var (i, j) in LongestIncreasing(unique))
            {
                Stretch(aFrom, i, bFrom, j, pairs);
                pairs.Add(new PromptPair(i, j, 1, true));
                (aFrom, bFrom) = (i + 1, j + 1);
            }
            Stretch(aFrom, _n, bFrom, _m, pairs);
        }

        private void Stretch(int aFrom, int aTo, int bFrom, int bTo, List<PromptPair> pairs)
        {
            if ((long)(aTo - aFrom) * (bTo - bFrom) <= MaxExactCells) Exact(aFrom, aTo, bFrom, bTo, pairs);
            else Greedy(aFrom, aTo, bFrom, bTo, pairs);
        }

        /// <summary>Each A prompt takes the next identical B prompt after the last one taken; what lies between stays in order.</summary>
        private void Greedy(int aFrom, int aTo, int bFrom, int bTo, List<PromptPair> pairs)
        {
            var places = new Dictionary<int, List<int>>();
            for (var j = bFrom; j < bTo; j++)
                if (_group[_n + j] >= 0) Add(places, _group[_n + j], j);

            int a = aFrom, b = bFrom;
            for (var i = aFrom; i < aTo; i++)
            {
                if ((i & 1023) == 0) _cancel.ThrowIfCancellationRequested();
                if (_group[i] < 0 || !places.TryGetValue(_group[i], out var list)) continue;
                var k = list.BinarySearch(b);
                if (k < 0) k = ~k;
                if (k == list.Count) continue;
                var j = list[k];
                Unmatched(a, i, b, j, pairs);
                pairs.Add(new PromptPair(i, j, 1, true));
                (a, b) = (i + 1, j + 1);
            }
            Unmatched(a, aTo, b, bTo, pairs);
        }

        /// <summary>The longest run of pairs, in A's order, whose B indices rise too (patience sorting).</summary>
        private static List<(int A, int B)> LongestIncreasing(List<(int A, int B)> items)
        {
            var tails = new List<int>();
            var previous = new int[items.Count];
            for (var k = 0; k < items.Count; k++)
            {
                int low = 0, high = tails.Count;
                while (low < high)
                {
                    var middle = (low + high) / 2;
                    if (items[tails[middle]].B < items[k].B) low = middle + 1;
                    else high = middle;
                }
                previous[k] = low > 0 ? tails[low - 1] : -1;
                if (low == tails.Count) tails.Add(k);
                else tails[low] = k;
            }
            var chain = new List<(int A, int B)>(tails.Count);
            for (var k = tails.Count > 0 ? tails[^1] : -1; k >= 0; k = previous[k]) chain.Add(items[k]);
            chain.Reverse();
            return chain;
        }

        // ------------------------------------------------------------ likeness

        public List<(int A, int B, double Similarity)> SimilarPairs()
        {
            var found = new List<(int, int, double)>();
            var columns = Index(0, _m);
            var weight = new int[_m];
            for (var i = 0; i < _n; i++)
            {
                Weigh(i, columns, weight);
                for (var j = 0; j < _m; j++)
                    if (weight[j] is > 0 and < Whole) found.Add((i, j, DiceOf(_tokens[i], _tokens[_n + j])));
            }
            return found;
        }

        /// <summary>
        /// How many leading tokens of a prompt must hold one of any prompt's alike enough to it.
        /// Dice ≥ t needs an overlap of at least t·|x|/(2−t) once the lengths can match at all,
        /// and two sets that share that many tokens share one among each one's first
        /// |x| − overlap + 1 in any common order.
        /// </summary>
        private static int PrefixLength(int length)
        {
            if (length == 0) return 0;
            var overlap = (int)Math.Ceiling(SimilarAt * length / (2 - SimilarAt) - 1e-9);
            return Math.Clamp(length - overlap + 1, 1, length);
        }

        /// <summary>Whether prompts of these token counts can be alike at all: Dice is at most 2·min / (|x| + |y|).</summary>
        private static bool MayReach(int lx, int ly) => lx > 0 && ly > 0 && 2.0 * Math.Min(lx, ly) >= SimilarAt * (lx + ly);

        /// <summary>The tokens two prompts must share to be alike.</summary>
        private static int Needed(int lx, int ly) => (int)Math.Ceiling(SimilarAt * (lx + ly) / 2 - 1e-9);

        /// <summary>The Dice coefficient of two prompts' tokens when it can reach the threshold; −1 when it cannot.</summary>
        private double Dice(long[] x, long[] y)
        {
            int lx = x.Length, ly = y.Length;
            if (!MayReach(lx, ly)) return -1;
            if (_steps > _maxSteps)
            {
                _overBudget = true;
                return -1;
            }
            _compared++;
            var need = Needed(lx, ly);
            int p = 0, q = 0, shared = 0;
            // Without branches on the comparison: which side steps on is as good as random.
            while (p < lx && q < ly && shared + lx - p >= need && shared + ly - q >= need)
            {
                long u = x[p], v = y[q];
                shared += u == v ? 1 : 0;
                p += u <= v ? 1 : 0;
                q += u >= v ? 1 : 0;
            }
            _steps += p + q;
            return shared >= need ? 2.0 * shared / (lx + ly) : -1;
        }

        private static double DiceOf(long[] x, long[] y)
        {
            if (x.Length == 0 || y.Length == 0) return 0;
            int p = 0, q = 0, shared = 0;
            while (p < x.Length && q < y.Length)
            {
                if (x[p] == y[q])
                {
                    shared++;
                    p++;
                    q++;
                }
                else if (x[p] < y[q]) p++;
                else q++;
            }
            return 2.0 * shared / (x.Length + y.Length);
        }

        private static void Add(Dictionary<int, List<int>> lists, int key, int value)
        {
            if (!lists.TryGetValue(key, out var list)) lists[key] = list = [];
            list.Add(value);
        }
    }
}
