# GameplayTag 层级关系存储：五种方案实测对比

> 从 CycloneGames.GameplayTags 的 CSR 平铺结构说起，用同一棵标签树跑穿所有实现。
> 2026-11-02 · .NET 10.0.14 / Release · 20 万对查询 × 20 轮取最小值 · 代码 `tag-bench/`

## 结论先行

1. **纯判定场景，位段码（path code）完胜。** 判父子 0.86 ns，是 CSR 平铺的 **8.5 倍快**、字符串子串方案的 **22 倍快**，树变深几乎不掉速。
2. **CSR 平铺的价值在工程属性，不在性能。** 只在「深树 + 批量取链」一个组合里拿到第一，领先 5%。换来的是无容量上限、不可变快照无锁读、加标签只重建。
3. **嵌套集（DFS 时间戳）被严重低估。** 8 B/tag 做到 O(1) 判父子，深树不掉速，没有容量天花板。代价是插入要重编号、做不了前缀深度比较。

## 1. 被比较的东西长什么样

> **分析对象**：[MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter) 里的第三方库 `CycloneGames.GameplayTags`
> - 库根目录：[CycloneGames.GameplayTags](https://github.com/MaiKuraki/UnityStarter/tree/main/UnityStarter/Assets/ThirdParty/CycloneGames/CycloneGames.GameplayTags)
> - 本文起点：[Core/TagDataSnapshot.cs](https://github.com/MaiKuraki/UnityStarter/blob/main/UnityStarter/Assets/ThirdParty/CycloneGames/CycloneGames.GameplayTags/Core/TagDataSnapshot.cs)
> - 关键文件：[GameplayTagManager.cs](https://github.com/MaiKuraki/UnityStarter/blob/main/UnityStarter/Assets/ThirdParty/CycloneGames/CycloneGames.GameplayTags/Core/GameplayTagManager.cs)（`BuildHierarchy` / `IsAncestorOf`）、[GameplayTagRegistrationContext.cs](https://github.com/MaiKuraki/UnityStarter/blob/main/UnityStarter/Assets/ThirdParty/CycloneGames/CycloneGames.GameplayTags/Core/GameplayTagRegistrationContext.cs)（注册与名字排序）

CycloneGames 这套实现把每个 tag 的**完整祖先链抄一遍**，首尾相接铺成一条大数组，再用一张前缀和表当目录。这就是 CSR（压缩稀疏行）。

两个数组语义完全错位，是唯一门槛：**`HierarchyOffsets` 是页码，`HierarchyPool` 是正文**。页码里不含任何 tag，正文里才是 tag 编号。

示例：五个标签 `A` / `A.B` / `A.B.C` / `A.D` / `B`

```
pool    = [ 1 | 1  2 | 1  2  3 | 1  4 | 5 ]
             ^   ^         ^
offsets = [0, 0, 1, 3, 6, 8, 9]
              #1 #2 #3 #4 #5
```

`#3` 的祖先链落在 `pool[3..6)` = `A`（根）、`A.B`、`A.B.C`（自己）。`A` 在池子里出现了 3 次——这就是冗余，也是全部开销来源。

三个位置读法：

```csharp
int start = HierarchyOffsets[i];
int end   = HierarchyOffsets[i + 1];   // 同时是 i+1 那段的起点

int root  = HierarchyPool[start];       // 段首 = 根
int self  = HierarchyPool[end - 1];     // 段尾 = 自己
int dad   = HierarchyPool[end - 2];     // 段尾前一格 = 父（段长≥2 才成立）
int level = end - start;                // 层级是减法副产品，不单独存
```

> **注释里的 `-2` 是个陷阱**
> 段长 1（根节点）时 `end - 2` 会跨到上一段末尾，返回一个完全无关的 tag，不抛异常、不越界，只是给错答案。真实代码从不用它，而是用 `ParentIndices` 或区间长度保护。

## 2. 五种方案摆一起

| 方案 | 每 tag 存储 | 判父子 | 求父/层 | 取祖先链 | 动态加标签 | 跨端自描述 |
|---|---|---|---|---|---|---|
| 字符串拼接 | 0 | O(len) + 分配 | 切串 | 反复切串 | 自由 | 是（传名字） |
| 位段码 7/7/9/9 | 4 B | **O(1) 无分支** | O(1) | O(深度) | O(1) 占槽 | **是** |
| 父指针 | 4~8 B | O(深度) 爬 | O(1) | O(深度) | O(1) | 否 |
| 嵌套集（DFS 时间戳） | 12 B | **O(1) 两比较** | O(1) | O(深度) | 要重编号 | 否 |
| CSR 平铺（本库） | 4 B + 4×Σ深度 | O(log 深度) | O(1) | **O(深度) 直读** | 全量重建 | 否 |

理论复杂度只是入场券，真实差距看实测。

## 3. 实测数据

两棵固定种子的树：浅树 4 层 6,391 个标签（平均链长 3.84），深树 10 层 8,766 个标签（平均链长 8.88）。深树的意义是把 CSR 的二分分支逼出来——浅树里切片长度永远不超过 `LinearScanLimit = 8`，二分路径执行率 0%，深树是 46%。

### IsAncestor（ns/次）

| 实现 | 场景 A · 4 层 | 场景 B · 10 层 |
|---|---|---|
| **位段码** 32 位 / 64 位 | **0.86** | **1.28** |
| 嵌套集 | 3.14 | 2.85 |
| 父指针爬升 | 5.54 | 11.43 |
| **CSR 平铺（本库）** | 7.28 | 17.74 |
| 字符串 Span（免分配） | 8.55 | 9.81 |
| 字符串 Substring | 18.65 | 28.12 |

### 场景 A · 4 层浅树，6,391 个标签

| 操作 | 位段码 | 嵌套集 | 父指针 | CSR | 字符串 Span |
|---|---|---|---|---|---|
| IsAncestor | **0.86** | 3.14 | 5.54 | 7.28 | 8.55 |
| Chain ×1 | 7.81 | 不适用 | **5.99** | 6.94 | — |
| Chain 批量 ×16 | 7.49 | 不适用 | **5.57** | 6.13 | — |
| MatchesTagDepth | **1.82** | 不适用 | — | 6.49 | — |
| 额外内存/tag | 4 B | 8 B | 5 B | 19.4 B | 0 |

### 场景 B · 10 层深树，8,766 个标签

| 操作 | 位段码 | 嵌套集 | 父指针 | CSR | 字符串 Span |
|---|---|---|---|---|---|
| IsAncestor | **1.28** | 2.85 | 11.43 | 17.74 | 9.81 |
| Chain ×1 | 12.65 | 不适用 | **9.98** | 12.21 | — |
| Chain 批量 ×16 | 13.27 | 不适用 | 11.77 | **11.15** | — |
| MatchesTagDepth | **1.98** | 不适用 | — | 11.56 | — |
| 额外内存/tag | 8 B | 8 B | 5 B | 39.5 B | 0 |

8 个执行格子里：位段码拿下 4 格、父指针 3 格、CSR 1 格、嵌套集 0 格。多次运行抖动约 ±10%，只看比例不看绝对值。

## 4. 四个反直觉的发现

### ① 深树上，CSR 比最土的父指针爬升还慢一倍

17.74 对 11.43，内存却是父指针的 8 倍。原因：CSR 成本 =「扫一条长度约等于深度的连续切片」，父指针 =「跳约等于深度的几次指针」。深度 3~4 时连续内存的局部性压得住跳转；深度到 9 以后，线性扫描的循环次数上去了，二分的 4~5 次随机访问又打不过顺序数组的几次 L1 命中。**两边都不占便宜，只剩内存劣势。**

### ② 批量化并不能普遍加速

CSR 只在「深树 + 批量」这一格拿到第一（11.15），领先 5%。浅树上位段码批量 7.49 甚至比单条 7.81 还慢。批量能摊薄的只有函数调用和边界校验那 1 ns，真正的成本是往输出缓冲区写 N 个元素，与批量无关。更有害的是批量版多了个持续累加的写游标 `w++`，形成依赖链——**每条链只有 1~2 个元素时，批量化是负优化。**

### ③ MatchesTagDepth 才是真正的分水岭

浅树 1.82 vs 6.49，深树 1.98 vs 11.56，**拉大到 5.8 倍**。

```csharp
// CSR：每次迭代依赖上一次的结果，形成串行依赖链
while (d < shared && _pool[sa + d] == _pool[sb + d]) d++;

// 位段码：一次异或 + lzcnt + 查表，整条指令流完全独立
ulong xor = _ids[a] ^ _ids[b];
return _layout.FirstDiffLevel(xor) - 1;
```

这不是「把循环写得更快」，是**结构上消灭了循环**。有意思的是 `MatchesTagDepth` 正是这个库注释里主推的功能之一，结果它在这个功能上是所有实现里最慢的。

### ④ 取祖先链的差距小到没有意义

三个实现全落在 6~13 ns。反推成本模型：浅树 6.94 ns / 1.88 个元素、深树 12.21 ns / 5.66 个元素，得到每元素约 1.5 ns、固定开销约 4.4 ns。**取出元素只有个位数时，函数调用、参数校验、循环建立、缓冲区写入这些固定开销占六成以上**，存储布局差异被完全淹没。这也解释了 `IsAncestor` 为什么能拉开差距——它不写缓冲区，输出只有一个 bool，剩下全是纯计算。

## 5. 踩坑记录：位段码判层级必须用 tzcnt

第一版用 `LeadingZeroCount` 判层级，10 万对真祖先全部判错。因为位段从**低位向高位**是「浅 → 深」，最高置位永远落在最浅那一层（L1 的 `1` 就是 bit54，任何后代都带着它）。要找最深一级必须用 `TrailingZeroCount`。

```csharp
// 错：最高非零位标的是最浅层
int level = (63 - BitOperations.LeadingZeroCount(id)) / width + 1;

// 对：最低非零位落在最深层的字段里
int level = depth - (BitOperations.TrailingZeroCount(id) / width);
```

> 位段法的通病：看起来只是位运算，实际藏着一串「必须为真但没人帮你检查」的隐式约定——每层必须占槽、0 必须留空、位段方向不能搞反、容量不能超。**CSR 那种结构至少会在构建期 throw。** 用位段码一定要在构建期补断言。

## 6. 选型结论

- **标签集合可控（四层固定、每层几百个）→ 位段码。** 判父子快 8 倍、前缀深度快 5 倍、内存省 5 倍。构建期加一句 `Debug.Assert(children <= 127)` 就完事。
- **只关心父子判定、不在意取链 → 嵌套集。** O(1) 两比较、8 B、无容量天花板，树深了不掉速。插入要重编号，同样是「改一次重建一次」。
- **标签来自编辑器 / mod / DLC，运行时长度不可预测 → 才轮到 CSR 平铺。** 真实卖点是无容量上限 + 不可变快照单次 `Volatile.Write` 发布、读侧无锁无撕裂。这些好处跑不出来，但线上偶发的多线程 bug 跑得出来。
- **别用字符串拼接做判定。** 免分配的 Span 版还能接受（8~10 ns），一旦 `StartsWith(a + ".")` 就翻倍。一帧几千次就是 0.1 ms 起步。

一句话：**CSR 是用 5 倍内存 + 5~8 倍的单次判定耗时，买了「这套东西永远不会因为标签变多而崩」。** 如果已经知道标签规模上限，这笔钱花得挺冤。

## 7. 复现

```bash
cd tag-bench
dotnet run -c Release
```

树生成种子固定（`20261102` / `20261103`），数字可复现。六种实现全部接受 tag 索引而非字符串，字符串方案只负责比较名字。开跑前先逐对校验六种实现结果一致——**不校验就跑基准等于在测错误的代码**，这次就是靠校验抓出了 tzcnt 那个 bug。

原库源码：[github.com/MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter/tree/main/UnityStarter/Assets/ThirdParty/CycloneGames/CycloneGames.GameplayTags)
本地基准工程：`tag-bench/Program.cs` + `tag-bench/TagBench.csproj`

> **数据局限**：微基准跑的是纯函数，数据全在缓存里。真实游戏里查询夹杂在属性计算中间，缓存状态完全不同——**绝对值别当真，比例可以参考**。真要下判断，把 `IsAncestor` 换进实际热路径采样一次。
