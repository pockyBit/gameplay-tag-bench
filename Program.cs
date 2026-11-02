using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace TagBench;

internal interface IQuery
{
    long ExtraBytes { get; }
    bool Ask(int ancestor, int descendant);
}

internal sealed class Tree
{
    public int Count;
    public int MaxLevel;
    public string[] Names;
    public int[] Parents;
    public int[] Levels;
    public byte[] LevelBytes;
    public ulong[] BitIds;
    public int[] Tin;
    public int[] Tout;
    public int[] Offsets;
    public int[] Pool;
    public int[] Widths;

    public double AvgChain => (double)Pool.Length / Count;

    public static Tree Build(int seed, int[] widths, int rootCount, int branchLo, int branchHi, double stopChance)
    {
        int depth = widths.Length;
        int[] shift = new int[depth + 2];
        for (int l = depth - 1; l >= 1; l--) shift[l] = shift[l + 1] + widths[l];

        var names = new List<string> { "<None>" };
        var parents = new List<int> { 0 };
        var levels = new List<int> { 0 };
        var ids = new List<ulong> { 0UL };

        int Add(string name, int parent, int level, ulong id)
        {
            names.Add(name);
            parents.Add(parent);
            levels.Add(level);
            ids.Add(id);
            return names.Count - 1;
        }

        var rnd = new Random(seed);

        void Grow(int parentIdx, ulong parentId, int level, string prefix)
        {
            if (level > depth) return;
            int n = rnd.Next(branchLo, branchHi + 1);
            for (int k = 0; k < n; k++)
            {
                ulong id = parentId | ((ulong)(k + 1) << shift[level]);
                string name = $"{prefix}.N{k}";
                int idx = Add(name, parentIdx, level, id);
                if (level >= 2 && rnd.NextDouble() < stopChance) continue;
                Grow(idx, id, level + 1, name);
            }
        }

        for (int r = 0; r < rootCount; r++)
        {
            ulong id = (ulong)(r + 1) << shift[1];
            string name = $"R{r}";
            int idx = Add(name, 0, 1, id);
            Grow(idx, id, 2, name);
        }

        int count = names.Count - 1;
        var tree = new Tree
        {
            Count = count,
            MaxLevel = depth,
            Names = names.ToArray(),
            Parents = parents.ToArray(),
            Levels = levels.ToArray(),
            BitIds = ids.ToArray(),
            Widths = (int[])widths.Clone(),
        };

        tree.LevelBytes = new byte[count + 1];
        for (int i = 0; i <= count; i++) tree.LevelBytes[i] = (byte)tree.Levels[i];

        var children = new List<int>[count + 1];
        for (int i = 0; i <= count; i++) children[i] = new List<int>(4);
        for (int i = 1; i <= count; i++) children[tree.Parents[i]].Add(i);

        tree.Tin = new int[count + 1];
        tree.Tout = new int[count + 1];
        int timer = 0;
        void Dfs(int node)
        {
            tree.Tin[node] = timer++;
            foreach (int c in children[node]) Dfs(c);
            tree.Tout[node] = timer - 1;
        }
        for (int i = 1; i <= count; i++)
            if (tree.Parents[i] == 0) Dfs(i);

        BuildHierarchy(tree, count + 1);
        return tree;
    }

    private static void BuildHierarchy(Tree tree, int total)
    {
        int[] parentIndices = tree.Parents;
        int[] offsets = new int[total + 1];
        for (int i = 1; i < total; i++)
        {
            int parent = parentIndices[i];
            int inherited = parent > 0 ? offsets[parent + 1] - offsets[parent] : 0;
            offsets[i + 1] = offsets[i] + inherited + 1;
        }

        int[] pool = new int[offsets[total]];
        for (int i = 1; i < total; i++)
        {
            int start = offsets[i];
            int parent = parentIndices[i];
            if (parent > 0)
            {
                int parentStart = offsets[parent];
                int inherited = offsets[parent + 1] - parentStart;
                if (inherited > 0) Array.Copy(pool, parentStart, pool, start, inherited);
            }
            pool[offsets[i + 1] - 1] = i;
        }

        tree.Offsets = offsets;
        tree.Pool = pool;
    }
}

internal readonly struct StringSubstringQuery : IQuery
{
    private readonly string[] _names;
    public StringSubstringQuery(Tree t) { _names = t.Names; }
    public long ExtraBytes => 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Ask(int ancestor, int descendant)
    {
        if (ancestor == descendant) return false;
        return _names[descendant].StartsWith(_names[ancestor] + ".", StringComparison.Ordinal);
    }
}

internal readonly struct StringSpanQuery : IQuery
{
    private readonly string[] _names;
    public StringSpanQuery(Tree t) { _names = t.Names; }
    public long ExtraBytes => 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Ask(int ancestor, int descendant)
    {
        if (ancestor == descendant) return false;
        string a = _names[ancestor];
        string d = _names[descendant];
        if (d.Length <= a.Length) return false;
        if (d[a.Length] != '.') return false;
        return d.AsSpan(0, a.Length).SequenceEqual(a.AsSpan());
    }
}

internal readonly struct BitCode7321Query : IQuery
{
    private const int M1 = unchecked((int)0xFE000000);
    private const int M2 = 0x01FC0000;
    private const int M3 = 0x0003FE00;
    private const int M4 = 0x000001FF;
    private static readonly int[] Keep = { 0, M1, M1 | M2, M1 | M2 | M3, M1 | M2 | M3 | M4 };

    private readonly int[] _ids;
    private readonly long _bytes;

    public BitCode7321Query(Tree t)
    {
        var src = t.BitIds;
        _ids = new int[src.Length];
        for (int i = 0; i < src.Length; i++) _ids[i] = unchecked((int)src[i]);
        _bytes = 4L * src.Length;
    }

    public long ExtraBytes => _bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Level(int id) => (id & M4) != 0 ? 4 : (id & M3) != 0 ? 3 : (id & M2) != 0 ? 2 : 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Ask(int ancestor, int descendant)
    {
        if (ancestor == descendant) return false;
        int a = _ids[ancestor];
        return (_ids[descendant] & Keep[Level(a)]) == a;
    }
}

internal readonly struct BitCode64Query : IQuery
{
    private readonly ulong[] _ids;
    private readonly ulong[] _keep;
    private readonly int _width;
    private readonly int _depth;
    private readonly long _bytes;

    public BitCode64Query(Tree t, int width)
    {
        _ids = t.BitIds;
        _width = width;
        _depth = t.MaxLevel;
        _bytes = 8L * t.BitIds.Length;

        int depth = t.MaxLevel;
        _keep = new ulong[depth + 2];
        for (int l = 1; l <= depth; l++)
        {
            int shift = (depth - l) * width;
            _keep[l] = shift >= 64 ? ulong.MaxValue : ~((1UL << shift) - 1);
        }
    }

    public long ExtraBytes => _bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Level(ulong id)
        => _depth - (BitOperations.TrailingZeroCount(id) / _width);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Ask(int ancestor, int descendant)
    {
        if (ancestor == descendant) return false;
        ulong a = _ids[ancestor];
        return (_ids[descendant] & _keep[Level(a)]) == a;
    }
}

internal readonly struct ParentClimbQuery : IQuery
{
    private readonly int[] _parents;
    private readonly byte[] _levels;
    private readonly long _bytes;

    public ParentClimbQuery(Tree t)
    {
        _parents = t.Parents;
        _levels = t.LevelBytes;
        _bytes = 5L * (t.Count + 1);
    }

    public long ExtraBytes => _bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Ask(int ancestor, int descendant)
    {
        if (ancestor == descendant) return false;
        int target = _levels[ancestor];
        int cur = descendant;
        while (_levels[cur] > target) cur = _parents[cur];
        return cur == ancestor;
    }
}

internal readonly struct NestedSetQuery : IQuery
{
    private readonly int[] _tin;
    private readonly int[] _tout;
    private readonly long _bytes;

    public NestedSetQuery(Tree t) { _tin = t.Tin; _tout = t.Tout; _bytes = 8L * (t.Count + 1); }
    public long ExtraBytes => _bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Ask(int ancestor, int descendant)
    {
        if (ancestor == descendant) return false;
        return _tin[ancestor] <= _tin[descendant] && _tout[descendant] <= _tout[ancestor];
    }
}

internal readonly struct CsrFlatQuery : IQuery
{
    private const int LinearScanLimit = 8;

    private readonly int[] _offsets;
    private readonly int[] _pool;
    private readonly long _bytes;

    public CsrFlatQuery(Tree t)
    {
        _offsets = t.Offsets;
        _pool = t.Pool;
        _bytes = 4L * t.Offsets.Length + 4L * t.Pool.Length;
    }

    public long ExtraBytes => _bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Ask(int ancestor, int descendant)
    {
        if (ancestor <= 0 || descendant <= 0 || ancestor == descendant) return false;
        if ((uint)descendant >= (uint)(_offsets.Length - 1)) return false;

        int start = _offsets[descendant];
        int length = _offsets[descendant + 1] - start - 1;
        if (length <= 0) return false;

        if (length <= LinearScanLimit)
        {
            int end = start + length;
            for (int i = start; i < end; i++)
                if (_pool[i] == ancestor) return true;
            return false;
        }

        int lo = start;
        int hi = start + length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int v = _pool[mid];
            if (ancestor == v) return true;
            if (ancestor > v) lo = mid + 1;
            else hi = mid - 1;
        }
        return false;
    }
}

internal sealed class BitLayout
{
    public readonly int Depth;
    public readonly ulong[] Mask;
    public readonly ulong[] Keep;
    public readonly byte[] FieldOfBit;

    public BitLayout(int[] widths)
    {
        Depth = widths.Length;
        Mask = new ulong[Depth + 2];
        Keep = new ulong[Depth + 2];
        FieldOfBit = new byte[64];

        for (int l = 1; l <= Depth; l++)
        {
            int shift = 0;
            for (int k = l + 1; k <= Depth; k++) shift += widths[k - 1];
            int bits = widths[l - 1];

            Mask[l] = ((1UL << bits) - 1) << shift;
            Keep[l] = shift >= 64 ? ulong.MaxValue : ~((1UL << shift) - 1);
            for (int b = shift; b < shift + bits && b < 64; b++) FieldOfBit[b] = (byte)l;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Level(ulong id) => FieldOfBit[BitOperations.TrailingZeroCount(id)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int FirstDiffLevel(ulong xor) => FieldOfBit[63 - BitOperations.LeadingZeroCount(xor)];
}

internal interface IChain
{
    long ExtraBytes { get; }
    int Chain(int tag, ulong[] buffer);
}

internal interface IDepth
{
    long ExtraBytes { get; }
    int Depth(int a, int b);
}

internal readonly struct CsrChain : IChain
{
    private readonly int[] _offsets;
    private readonly int[] _pool;
    private readonly long _bytes;

    public CsrChain(Tree t)
    {
        _offsets = t.Offsets;
        _pool = t.Pool;
        _bytes = 4L * t.Offsets.Length + 4L * t.Pool.Length;
    }

    public long ExtraBytes => _bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Chain(int tag, ulong[] buffer)
    {
        if (tag <= 0) return 0;
        int start = _offsets[tag];
        int count = _offsets[tag + 1] - start - 1;
        for (int i = 0; i < count; i++) buffer[i] = (ulong)_pool[start + i];
        return count;
    }
}

internal readonly struct ParentChain : IChain
{
    private readonly int[] _parents;
    private readonly byte[] _levels;
    private readonly long _bytes;

    public ParentChain(Tree t)
    {
        _parents = t.Parents;
        _levels = t.LevelBytes;
        _bytes = 5L * (t.Count + 1);
    }

    public long ExtraBytes => _bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Chain(int tag, ulong[] buffer)
    {
        if (tag <= 0) return 0;
        int count = _levels[tag] - 1;
        int cur = _parents[tag];
        for (int i = count - 1; i >= 0; i--)
        {
            buffer[i] = (ulong)cur;
            cur = _parents[cur];
        }
        return count;
    }
}

internal readonly struct BitCodeChain : IChain
{
    private readonly ulong[] _ids;
    private readonly BitLayout _layout;
    private readonly long _bytes;

    public BitCodeChain(Tree t, BitLayout layout)
    {
        _ids = t.BitIds;
        _layout = layout;
        _bytes = 8L * t.BitIds.Length + 64 + 8L * layout.Keep.Length;
    }

    public long ExtraBytes => _bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Chain(int tag, ulong[] buffer)
    {
        if (tag <= 0) return 0;
        ulong id = _ids[tag];
        int count = _layout.Level(id) - 1;
        for (int i = 0; i < count; i++) buffer[i] = id & _layout.Keep[i + 1];
        return count;
    }
}

internal readonly struct CsrDepth : IDepth
{
    private readonly int[] _offsets;
    private readonly int[] _pool;
    private readonly long _bytes;

    public CsrDepth(Tree t)
    {
        _offsets = t.Offsets;
        _pool = t.Pool;
        _bytes = 4L * t.Offsets.Length + 4L * t.Pool.Length;
    }

    public long ExtraBytes => _bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Depth(int a, int b)
    {
        if (a <= 0 || b <= 0) return 0;
        int sa = _offsets[a];
        int sb = _offsets[b];
        int shared = Math.Min(_offsets[a + 1] - sa, _offsets[b + 1] - sb);
        int d = 0;
        while (d < shared && _pool[sa + d] == _pool[sb + d]) d++;
        return d;
    }
}

internal readonly struct BitCodeDepth : IDepth
{
    private readonly ulong[] _ids;
    private readonly BitLayout _layout;
    private readonly long _bytes;

    public BitCodeDepth(Tree t, BitLayout layout)
    {
        _ids = t.BitIds;
        _layout = layout;
        _bytes = 8L * t.BitIds.Length + 64 + 8L * layout.Keep.Length;
    }

    public long ExtraBytes => _bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Depth(int a, int b)
    {
        if (a == b) return _layout.Level(_ids[a]);
        ulong xor = _ids[a] ^ _ids[b];
        return _layout.FirstDiffLevel(xor) - 1;
    }
}

internal interface IBatchChain
{
    long ExtraBytes { get; }
    int Batch(int[] tags, int offset, int count, ulong[] buffer);
}

internal readonly struct CsrBatch : IBatchChain
{
    private readonly int[] _offsets;
    private readonly int[] _pool;
    private readonly long _bytes;

    public CsrBatch(Tree t)
    {
        _offsets = t.Offsets;
        _pool = t.Pool;
        _bytes = 4L * t.Offsets.Length + 4L * t.Pool.Length;
    }

    public long ExtraBytes => _bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Batch(int[] tags, int offset, int count, ulong[] buffer)
    {
        int w = 0;
        int end = offset + count;
        for (int i = offset; i < end; i++)
        {
            int tag = tags[i];
            int start = _offsets[tag];
            int stop = start + (_offsets[tag + 1] - start - 1);
            for (int k = start; k < stop; k++) buffer[w++] = (ulong)_pool[k];
        }
        return w;
    }
}

internal readonly struct ParentBatch : IBatchChain
{
    private readonly int[] _parents;
    private readonly byte[] _levels;
    private readonly long _bytes;

    public ParentBatch(Tree t)
    {
        _parents = t.Parents;
        _levels = t.LevelBytes;
        _bytes = 5L * (t.Count + 1);
    }

    public long ExtraBytes => _bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Batch(int[] tags, int offset, int count, ulong[] buffer)
    {
        int w = 0;
        int end = offset + count;
        for (int i = offset; i < end; i++)
        {
            int tag = tags[i];
            int n = _levels[tag] - 1;
            int cur = _parents[tag];
            int baseIdx = w;
            w += n;
            for (int k = n - 1; k >= 0; k--)
            {
                buffer[baseIdx + k] = (ulong)cur;
                cur = _parents[cur];
            }
        }
        return w;
    }
}

internal readonly struct BitBatch : IBatchChain
{
    private readonly ulong[] _ids;
    private readonly BitLayout _layout;
    private readonly long _bytes;

    public BitBatch(Tree t, BitLayout layout)
    {
        _ids = t.BitIds;
        _layout = layout;
        _bytes = 8L * t.BitIds.Length + 64 + 8L * layout.Keep.Length;
    }

    public long ExtraBytes => _bytes;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Batch(int[] tags, int offset, int count, ulong[] buffer)
    {
        int w = 0;
        int end = offset + count;
        for (int i = offset; i < end; i++)
        {
            ulong id = _ids[tags[i]];
            int n = _layout.Level(id) - 1;
            for (int k = 0; k < n; k++) buffer[w++] = id & _layout.Keep[k + 1];
        }
        return w;
    }
}

internal static class Program
{
    private const int QueryCount = 200_000;
    private const int Reps = 20;

    private static void Main()
    {
        Console.WriteLine($".NET {Environment.Version} / Release / 200,000 次查询 × 20 轮取最小值");
        Console.WriteLine();

        ScenarioA();
        Console.WriteLine();
        ScenarioB();
    }

    private static void ScenarioA()
    {
        Console.WriteLine("=== 场景 A：4 层浅树，位段码 32 位 7/7/9/9（你原来的方案） ===");
        Tree tree = Tree.Build(20261102, new[] { 7, 7, 9, 9 }, 12, 6, 12, 0.20);
        Report(tree, scenario: "A");
    }

    private static void ScenarioB()
    {
        Console.WriteLine("=== 场景 B：10 层深树，位段码 64 位 6×10（逼出 CSR 的二分分支） ===");
        Tree tree = Tree.Build(20261103, new[] { 6, 6, 6, 6, 6, 6, 6, 6, 6, 6 }, 8, 2, 3, 0.25);
        Report(tree, scenario: "B");
    }

    private static void Report(Tree tree, string scenario)
    {
        Console.WriteLine($"标签树  {tree.Count:N0} 个标签 / 最大深度 {tree.MaxLevel} / 平均链长 {tree.AvgChain:F2}");

        int[] qa = new int[QueryCount];
        int[] qd = new int[QueryCount];
        BuildQueries(tree, qa, qd, 12345 + scenario[0]);

        bool[] reference = new bool[QueryCount];
        int trueCount = 0;
        int binaryPath = 0;
        for (int i = 0; i < QueryCount; i++)
        {
            reference[i] = ReferenceClimb(tree, qa[i], qd[i]);
            if (reference[i]) trueCount++;
            if (tree.Levels[qd[i]] - 1 > 8) binaryPath++;
        }

        Console.WriteLine($"查询集  真祖先 {trueCount:N0} / 非祖先 {QueryCount - trueCount:N0}" +
                          $" / 落在 CSR 二分路径上 {binaryPath * 100.0 / QueryCount:F0}%");
        Console.WriteLine();

        bool ok = true;
        ok &= Check("字符串 Substring", new StringSubstringQuery(tree), qa, qd, reference);
        ok &= Check("字符串 Span", new StringSpanQuery(tree), qa, qd, reference);
        if (tree.MaxLevel == 4)
            ok &= Check("位段码 32/7-7-9-9", new BitCode7321Query(tree), qa, qd, reference);
        else
            ok &= Check("位段码 64/6×10", new BitCode64Query(tree, 6), qa, qd, reference);
        ok &= Check("父指针爬升", new ParentClimbQuery(tree), qa, qd, reference);
        ok &= Check("嵌套集", new NestedSetQuery(tree), qa, qd, reference);
        ok &= Check("CSR 平铺（本库）", new CsrFlatQuery(tree), qa, qd, reference);
        Console.WriteLine($"校验  {(ok ? "六个实现判定完全一致" : "存在分歧，结果不可信")}");
        Console.WriteLine();

        var results = new List<(string Name, double Ns, long Bytes)>();
        if (tree.MaxLevel == 4) Measure("位段码 32/7-7-9-9", new BitCode7321Query(tree), qa, qd, results);
        else Measure("位段码 64/6×10", new BitCode64Query(tree, 6), qa, qd, results);
        Measure("嵌套集", new NestedSetQuery(tree), qa, qd, results);
        Measure("父指针爬升", new ParentClimbQuery(tree), qa, qd, results);
        Measure("CSR 平铺（本库）", new CsrFlatQuery(tree), qa, qd, results);
        Measure("字符串 Span", new StringSpanQuery(tree), qa, qd, results);
        Measure("字符串 Substring", new StringSubstringQuery(tree), qa, qd, results);

        double worst = results.Max(r => r.Ns);
        Console.WriteLine($"{"方案",-24}{"ns/次",10}{"相对最慢",12}{"额外内存/tag",14}");
        Console.WriteLine(new string('-', 62));
        foreach (var r in results.OrderBy(x => x.Ns))
        {
            double perTag = (double)r.Bytes / (tree.Count + 1);
            Console.WriteLine($"{r.Name,-24}{r.Ns,10:F2}{worst / r.Ns,11:F1}x{perTag,13:F1} B");
        }
        Console.Write($"CSR 池子 {tree.Pool.Length:N0} int / {4L * tree.Pool.Length / 1024} KB；");
        Console.WriteLine($"位段码是 {results.First(r => r.Name.StartsWith("位段码")).Bytes / 1024} KB 定长");

        ChainAndDepth(tree, qa, qd);
    }

    private static void ChainAndDepth(Tree tree, int[] tags, int[] partners)
    {
        const int BufLen = 16;
        var layout = new BitLayout(tree.Widths);
        var csrC = new CsrChain(tree);
        var parC = new ParentChain(tree);
        var bitC = new BitCodeChain(tree, layout);

        ulong[] b1 = new ulong[BufLen];
        ulong[] b2 = new ulong[BufLen];
        ulong[] b3 = new ulong[BufLen];

        var idToIndex = new Dictionary<ulong, int>(tree.Count);
        for (int i = 1; i <= tree.Count; i++) idToIndex[tree.BitIds[i]] = i;
        m_IdToIndex = idToIndex;

        int bad = 0;
        long totalCount = 0;
        for (int i = 0; i < tags.Length; i++)
        {
            int t = tags[i];
            int n1 = csrC.Chain(t, b1);
            int n2 = parC.Chain(t, b2);
            int n3 = bitC.Chain(t, b3);
            totalCount += n1;
            if (n1 != n2 || n1 != n3) { bad++; continue; }
            for (int k = 0; k < n1; k++)
            {
                if (b1[k] != b2[k] || idToIndex[b3[k]] != (int)b1[k]) { bad++; break; }
            }
        }

        var csrD = new CsrDepth(tree);
        var bitD = new BitCodeDepth(tree, layout);
        int badDepth = 0;
        for (int i = 0; i < tags.Length; i++)
            if (csrD.Depth(tags[i], partners[i]) != bitD.Depth(tags[i], partners[i])) badDepth++;

        Console.WriteLine();
        Console.WriteLine($"--- 取整条祖先链（根在前、不含自己；平均取出 {totalCount / (double)tags.Length:F2} 个）---");
        Console.WriteLine($"校验  {(bad == 0 ? "三种实现逐元素一致" : $"链内容 {bad} 处不一致")}，" +
                          $"{(badDepth == 0 ? "前缀深度一致" : $"前缀深度 {badDepth} 处不一致")}");
        Console.WriteLine();

        var chainResults = new List<(string, double, long)>();
        MeasureChain("CSR 平铺（本库）", csrC, tags, b1, chainResults);
        MeasureChain("位段码（掩码逐层剥）", bitC, tags, b3, chainResults);
        MeasureChain("父指针倒着写", parC, tags, b2, chainResults);
        PrintTable(chainResults, tree.Count);

        Console.WriteLine();
        Console.WriteLine("--- MatchesTagDepth（两个 tag 共享的前缀层数）---");
        var depthResults = new List<(string, double, long)>();
        MeasureDepth("CSR 平铺（本库）", csrD, tags, partners, depthResults);
        MeasureDepth("位段码（异或找首个分叉层）", bitD, tags, partners, depthResults);
        PrintTable(depthResults, tree.Count);

        BatchSection(tree, layout, csrC, bitC, parC, tags, totalCount);
    }

    private static void BatchSection(Tree tree, BitLayout layout, CsrChain csrC, BitCodeChain bitC, ParentChain parC, int[] tags, long totalElements)
    {
        const int BatchSize = 16;
        int calls = tags.Length / BatchSize;

        var csrB = new CsrBatch(tree);
        var parB = new ParentBatch(tree);
        var bitB = new BitBatch(tree, layout);

        var bufA = new ulong[BatchSize * 16];
        var bufB = new ulong[BatchSize * 16];
        var bufC = new ulong[BatchSize * 16];
        var bufD = new ulong[BatchSize * 16];

        int bad = 0;
        for (int i = 0; i < 64; i++)
        {
            int off = i * BatchSize;
            int na = csrB.Batch(tags, off, BatchSize, bufA);
            int nb = parB.Batch(tags, off, BatchSize, bufB);
            int nc = bitB.Batch(tags, off, BatchSize, bufC);
            if (na != nb || na != nc) { bad++; continue; }
            for (int k = 0; k < na; k++)
            {
                if (bufA[k] != bufB[k] || idConvert(bufC[k]) != (int)bufA[k]) { bad++; break; }
            }
            _ = bufD;
        }

        Console.WriteLine();
        Console.WriteLine($"--- 批量取链（每次调用取 {BatchSize} 条，摊薄固定开销）---");
        Console.WriteLine($"校验  {(bad == 0 ? "三种实现逐元素一致" : $"{bad} 处分歧")}");

        var batchResults = new List<(string Name, double Ns, long Bytes)>();
        MeasureBatch("CSR 平铺（本库）", csrB, tags, calls, BatchSize, bufA, batchResults);
        MeasureBatch("位段码（掩码逐层剥）", bitB, tags, calls, BatchSize, bufC, batchResults);
        MeasureBatch("父指针倒着写", parB, tags, calls, BatchSize, bufB, batchResults);

        double worst = batchResults.Max(r => r.Ns);
        double perElementCsr = batchResults.First(r => r.Name.StartsWith("CSR")).Ns
                               / (totalElements / (double)tags.Length);
        Console.WriteLine($"{"方案",-26}{"ns/链",9}{"ns/元素",10}{"相对最慢",11}");
        Console.WriteLine(new string('-', 58));
        foreach (var r in batchResults.OrderBy(x => x.Ns))
        {
            double perElement = r.Ns / (totalElements / (double)tags.Length);
            Console.WriteLine($"{r.Name,-26}{r.Ns,9:F2}{perElement,10:F2}{worst / r.Ns,10:F1}x");
        }
        _ = perElementCsr;
        _ = csrC;
        _ = bitC;
        _ = parC;
    }

    private static int idConvert(ulong id) => m_IdToIndex.TryGetValue(id, out int v) ? v : -1;
    private static Dictionary<ulong, int> m_IdToIndex;

    private static void MeasureBatch<T>(string label, T q, int[] tags, int calls, int batchSize, ulong[] buffer, List<(string Name, double Ns, long Bytes)> results)
        where T : struct, IBatchChain
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();

        long sink = 0;
        for (int w = 0; w < 3; w++)
            for (int c = 0; c < calls; c++)
            {
                int n = q.Batch(tags, c * batchSize, batchSize, buffer);
                sink += n + (long)buffer[n > 0 ? n - 1 : 0];
            }

        double best = double.MaxValue;
        for (int r = 0; r < Reps; r++)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int c = 0; c < calls; c++)
            {
                int n = q.Batch(tags, c * batchSize, batchSize, buffer);
                sink += n + (long)buffer[n > 0 ? n - 1 : 0];
            }
            long t1 = Stopwatch.GetTimestamp();
            double ms = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms < best) best = ms;
        }
        GC.KeepAlive(sink);
        results.Add((label, best * 1_000_000.0 / (calls * (long)batchSize), q.ExtraBytes));
    }

    private static void PrintTable(List<(string Name, double Ns, long Bytes)> results, int count)
    {
        double worst = results.Max(r => r.Ns);
        Console.WriteLine($"{"方案",-28}{"ns/次",10}{"相对最慢",12}{"额外内存/tag",14}");
        Console.WriteLine(new string('-', 66));
        foreach (var r in results.OrderBy(x => x.Ns))
            Console.WriteLine($"{r.Name,-28}{r.Ns,10:F2}{worst / r.Ns,11:F1}x{(double)r.Bytes / (count + 1),13:F1} B");
    }

    private static void MeasureChain<T>(string label, T q, int[] tags, ulong[] buffer, List<(string, double, long)> results)
        where T : struct, IChain
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();

        long sink = 0;
        for (int w = 0; w < 3; w++)
            for (int i = 0; i < tags.Length; i++)
            {
                int n = q.Chain(tags[i], buffer);
                sink += n + (long)(n > 0 ? buffer[n - 1] : 0UL);
            }

        double best = double.MaxValue;
        for (int r = 0; r < Reps; r++)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < tags.Length; i++)
            {
                int n = q.Chain(tags[i], buffer);
                sink += n + (long)(n > 0 ? buffer[n - 1] : 0UL);
            }
            long t1 = Stopwatch.GetTimestamp();
            double ms = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms < best) best = ms;
        }
        GC.KeepAlive(sink);
        results.Add((label, best * 1_000_000.0 / tags.Length, q.ExtraBytes));
    }

    private static void MeasureDepth<T>(string label, T q, int[] a, int[] b, List<(string, double, long)> results)
        where T : struct, IDepth
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();

        long sink = 0;
        for (int w = 0; w < 3; w++)
            for (int i = 0; i < a.Length; i++) sink += q.Depth(a[i], b[i]);

        double best = double.MaxValue;
        for (int r = 0; r < Reps; r++)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < a.Length; i++) sink += q.Depth(a[i], b[i]);
            long t1 = Stopwatch.GetTimestamp();
            double ms = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms < best) best = ms;
        }
        GC.KeepAlive(sink);
        results.Add((label, best * 1_000_000.0 / a.Length, q.ExtraBytes));
    }

    private static bool Check<T>(string label, T q, int[] qa, int[] qd, bool[] reference)
        where T : struct, IQuery
    {
        int mismatch = 0;
        for (int i = 0; i < qa.Length; i++)
            if (q.Ask(qa[i], qd[i]) != reference[i]) mismatch++;
        if (mismatch != 0) Console.WriteLine($"  {label} 不一致 {mismatch} 处");
        return mismatch == 0;
    }

    private static void Measure<T>(string label, T q, int[] qa, int[] qd, List<(string, double, long)> results)
        where T : struct, IQuery
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        double ns = Bench(q, qa, qd) * 1_000_000.0 / qa.Length;
        results.Add((label, ns, q.ExtraBytes));
    }

    private static double Bench<T>(T q, int[] qa, int[] qd) where T : struct, IQuery
    {
        long sink = 0;
        for (int w = 0; w < 4; w++)
            for (int i = 0; i < qa.Length; i++)
                if (q.Ask(qa[i], qd[i])) sink++;

        double best = double.MaxValue;
        for (int r = 0; r < Reps; r++)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < qa.Length; i++)
                if (q.Ask(qa[i], qd[i])) sink++;
            long t1 = Stopwatch.GetTimestamp();
            double ms = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms < best) best = ms;
        }
        GC.KeepAlive(sink);
        return best;
    }

    private static void BuildQueries(Tree tree, int[] qa, int[] qd, int seed)
    {
        var rnd = new Random(seed);
        for (int i = 0; i < qa.Length; i++)
        {
            if ((i & 1) == 0)
            {
                int d;
                do { d = rnd.Next(1, tree.Count + 1); } while (tree.Levels[d] < 2);
                int steps = rnd.Next(1, tree.Levels[d]);
                int a = d;
                for (int s = 0; s < steps; s++) a = tree.Parents[a];
                qa[i] = a;
                qd[i] = d;
            }
            else
            {
                qa[i] = rnd.Next(1, tree.Count + 1);
                qd[i] = rnd.Next(1, tree.Count + 1);
            }
        }
    }

    private static bool ReferenceClimb(Tree tree, int ancestor, int descendant)
    {
        if (ancestor <= 0 || descendant <= 0 || ancestor == descendant) return false;
        int cur = tree.Parents[descendant];
        while (cur > 0)
        {
            if (cur == ancestor) return true;
            cur = tree.Parents[cur];
        }
        return false;
    }
}
