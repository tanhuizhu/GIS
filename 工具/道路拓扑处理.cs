using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// 道路拓扑处理：端点吸附、T 形搭接、交叉打断，输出 *_拓扑.shp。
/// 属性表（.dbf）和坐标系（.prj）原样复制，记录数、顺序不变。
/// </summary>
static class 道路拓扑处理
{
    const double 吸附容差 = 1.5;      // 端点吸附距离（米）
    const double 搭接容差 = 1.5;      // 端点搭到另一条线中间的距离（米）
    const double 去重容差 = 0.05;     // 重建线时顶点去重距离（米）

    sealed class 线要素
    {
        public List<List<double[]>> 部分 = new List<List<double[]>>();
    }

    sealed class 段
    {
        public int 要素号, 部分号, 段号;
        public double[] 甲, 乙;
        public List<KeyValuePair<double, double[]>> 附加 = new List<KeyValuePair<double, double[]>>();
    }

    static void Main(string[] args)
    {
        string 源 = args.Length > 0 ? args[0] : @"D:\GIS\数据\DL.shp";
        string 目录 = Path.GetDirectoryName(源);
        string 名 = Path.GetFileNameWithoutExtension(源);
        string 目标 = Path.Combine(目录, 名 + "_拓扑.shp");

        int 类型;
        List<线要素> 要素 = 读取(源, out 类型);
        Console.WriteLine("读入 " + 要素.Count + " 个线要素（形状类型 " + 类型 + "）");

        List<段> 段表 = 展开(要素);
        Console.WriteLine("原始线段 " + 段表.Count + " 条");

        int 块前; int[] 前几块;
        连通块(段表, 1.0, out 块前, out 前几块);
        Console.WriteLine("处理前：1 米容差下连通块 " + 块前 + " 个，最大的几块 [" + string.Join(",", 前几块) + "]");

        端点吸附(段表);
        搭接处理(段表);
        悬空端点连接(段表, 8.0);
        交叉打断(段表);
        int 桥数 = 组件桥接(要素, 段表);
        Console.WriteLine("连通块之间补桥 " + 桥数 + " 条");

        try
        {
            int 快捷数 = 消除绕行(要素, 段表, 120.0);
            Console.WriteLine("消除绕行补连接 " + 快捷数 + " 条");
        }
        catch (Exception ex)
        {
            Console.WriteLine("消除绕行异常：" + ex.GetType().Name + " " + ex.Message);
        }

        // 扫描不同容差，找出能把路网连起来的最小搭接距离
        foreach (double 容差 in new[] { 3.0, 5.0, 8.0, 12.0, 20.0 })
        {
            List<线要素> 试 = 读取(源, out 类型);
            List<段> 试段 = 展开(试);
            端点吸附(试段);
            搭接处理(试段);
            int 连接数 = 悬空端点连接(试段, 容差);
            交叉打断(试段);
            int 块试; int[] 大试;
            连通块(试段, 1.0, out 块试, out 大试);
            Console.WriteLine(string.Format("端点连接容差 {0} 米：连接 {1} 处，连通块 {2} 个，最大 [{3}]",
                容差, 连接数, 块试, string.Join(",", 大试.Take(6))));
        }

        int 插入点 = 段表.Sum(s => s.附加.Count);
        Console.WriteLine("共插入 " + 插入点 + " 个节点（交叉/搭接）");

        写出(目标, 要素, 段表, 类型);
        File.Copy(Path.ChangeExtension(源, ".dbf"), Path.ChangeExtension(目标, ".dbf"), true);
        if (File.Exists(Path.ChangeExtension(源, ".prj")))
            File.Copy(Path.ChangeExtension(源, ".prj"), Path.ChangeExtension(目标, ".prj"), true);

        Console.WriteLine("已写出：" + 目标);

        int 类型2;
        List<线要素> 新要素 = 读取(目标, out 类型2);
        List<段> 新段表 = 展开(新要素);
        int 块后; int[] 后几块;
        连通块(新段表, 1.0, out 块后, out 后几块);
        Console.WriteLine("处理后：" + 新段表.Count + " 条线段，1 米容差下连通块 " + 块后 + " 个，最大的几块 [" + string.Join(",", 后几块) + "]");
    }

    // ------------------------------------------------------------ 读

    static List<线要素> 读取(string 路径, out int 形状类型)
    {
        List<线要素> 结果 = new List<线要素>();
        形状类型 = 0;

        using (BinaryReader 读 = new BinaryReader(File.OpenRead(路径)))
        {
            byte[] 头 = 读.ReadBytes(100);
            形状类型 = BitConverter.ToInt32(头, 32);

            while (读.BaseStream.Position + 8 <= 读.BaseStream.Length)
            {
                byte[] 记录头 = 读.ReadBytes(8);
                Array.Reverse(记录头, 4, 4);
                int 内容长度 = BitConverter.ToInt32(记录头, 4) * 2;
                long 起点 = 读.BaseStream.Position;

                int 类型 = 读.ReadInt32();
                if (类型 == 3 || 类型 == 13 || 类型 == 23)
                {
                    读.ReadDouble(); 读.ReadDouble(); 读.ReadDouble(); 读.ReadDouble();
                    int 部分数 = 读.ReadInt32();
                    int 点数 = 读.ReadInt32();
                    int[] 部分索引 = new int[部分数];
                    for (int i = 0; i < 部分数; i++) 部分索引[i] = 读.ReadInt32();
                    double[] X = new double[点数], Y = new double[点数];
                    for (int i = 0; i < 点数; i++) { X[i] = 读.ReadDouble(); Y[i] = 读.ReadDouble(); }

                    线要素 要素 = new 线要素();
                    for (int p = 0; p < 部分数; p++)
                    {
                        int 起 = 部分索引[p];
                        int 止 = p + 1 < 部分数 ? 部分索引[p + 1] : 点数;
                        List<double[]> 点列 = new List<double[]>();
                        for (int i = 起; i < 止; i++) 点列.Add(new[] { X[i], Y[i] });
                        if (点列.Count >= 2) 要素.部分.Add(点列);
                    }
                    if (要素.部分.Count > 0) 结果.Add(要素);
                    else 结果.Add(new 线要素());   // 占位，保证记录数与属性表一致
                }
                else
                {
                    结果.Add(new 线要素());
                }

                读.BaseStream.Position = 起点 + 内容长度;
            }
        }
        return 结果;
    }

    static List<段> 展开(List<线要素> 要素)
    {
        List<段> 段表 = new List<段>();
        for (int f = 0; f < 要素.Count; f++)
            for (int p = 0; p < 要素[f].部分.Count; p++)
            {
                List<double[]> 点列 = 要素[f].部分[p];
                for (int i = 0; i + 1 < 点列.Count; i++)
                    段表.Add(new 段 { 要素号 = f, 部分号 = p, 段号 = i, 甲 = 点列[i], 乙 = 点列[i + 1] });
            }
        return 段表;
    }

    // ------------------------------------------------------------ 处理

    /// <summary>相邻线要素的端点如果在容差内，统一到同一个坐标。</summary>
    static void 端点吸附(List<段> 段表)
    {
        List<double[]> 端点 = new List<double[]>();
        foreach (段 一条 in 段表) { if (!端点.Contains(一条.甲)) 端点.Add(一条.甲); if (!端点.Contains(一条.乙)) 端点.Add(一条.乙); }

        bool[] 已合并 = new bool[端点.Count];
        for (int i = 0; i < 端点.Count; i++)
        {
            if (已合并[i]) continue;
            List<int> 组 = new List<int> { i };
            for (int j = i + 1; j < 端点.Count; j++)
            {
                if (已合并[j]) continue;
                if (距离(端点[i], 端点[j]) <= 吸附容差) { 组.Add(j); 已合并[j] = true; }
            }
            if (组.Count < 2) continue;

            double 平均X = 组.Average(k => 端点[k][0]);
            double 平均Y = 组.Average(k => 端点[k][1]);
            foreach (int k in 组) { 端点[k][0] = 平均X; 端点[k][1] = 平均Y; }
            已合并[i] = true;
        }
    }

    /// <summary>按坐标容差把线段端点合并，返回各个连通块包含的点。</summary>
    static List<List<double[]>> 分组(List<段> 段表, double 容差)
    {
        Dictionary<string, List<double[]>> 网格 = new Dictionary<string, List<double[]>>();
        Dictionary<double[], double[]> 父 = new Dictionary<double[], double[]>();

        Func<double[], double[]> 找根 = null;
        找根 = p =>
        {
            while (!ReferenceEquals(父[p], p)) { 父[p] = 父[父[p]]; p = 父[p]; }
            return p;
        };

        Func<double[], double[]> 取 = p =>
        {
            int kx = (int)Math.Floor(p[0] / 容差), ky = (int)Math.Floor(p[1] / 容差);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    List<double[]> 桶;
                    if (!网格.TryGetValue((kx + dx) + "_" + (ky + dy), out 桶)) continue;
                    foreach (double[] q in 桶)
                        if (距离(p, q) <= 容差) return q;
                }
            string 键 = kx + "_" + ky;
            List<double[]> 已有;
            if (!网格.TryGetValue(键, out 已有)) { 已有 = new List<double[]>(); 网格[键] = 已有; }
            已有.Add(p);
            父[p] = p;
            return p;
        };

        foreach (段 一条 in 段表)
        {
            if (一条.甲 == null || 一条.乙 == null) continue;
            double[] a = 取(一条.甲), b = 取(一条.乙);
            double[] ra = 找根(a), rb = 找根(b);
            if (!ReferenceEquals(ra, rb)) 父[ra] = rb;
        }

        Dictionary<double[], List<double[]>> 组 = new Dictionary<double[], List<double[]>>();
        List<double[]> 全部 = new List<double[]>(父.Keys);
        foreach (double[] p in 全部)
        {
            double[] 根 = 找根(p);
            List<double[]> 列表;
            if (!组.TryGetValue(根, out 列表)) { 列表 = new List<double[]>(); 组[根] = 列表; }
            列表.Add(p);
        }
        return 组.Values.ToList();
    }

    /// <summary>把互不相连的各个路网块用最短直线桥接起来，桥线作为独立片段挂到要素上。</summary>
    static int 组件桥接(List<线要素> 要素, List<段> 段表)
    {
        Dictionary<double[], double[]> 父 = new Dictionary<double[], double[]>();
        List<double[]> 节点 = new List<double[]>();

        Func<double[], double[]> 找根 = null;
        找根 = p =>
        {
            while (!ReferenceEquals(父[p], p)) { 父[p] = 父[父[p]]; p = 父[p]; }
            return p;
        };

        foreach (段 一条 in 段表)
        {
            if (一条.甲 == null || 一条.乙 == null) continue;
            foreach (double[] 点 in new[] { 一条.甲, 一条.乙 })
                if (!父.ContainsKey(点)) { 父[点] = 点; 节点.Add(点); }

            double[] ra = 找根(一条.甲), rb = 找根(一条.乙);
            if (!ReferenceEquals(ra, rb)) 父[ra] = rb;
        }

        Dictionary<double[], int> 归属 = new Dictionary<double[], int>();
        for (int f = 0; f < 要素.Count; f++)
            foreach (List<double[]> 部 in 要素[f].部分)
                foreach (double[] 点 in 部)
                    if (!归属.ContainsKey(点)) 归属[点] = f;

        int 桥数 = 0;
        double 最长 = 0, 合计 = 0;

        // 每轮都从几何重新算连通块，避免并查集维护出错
        while (桥数 < 120)
        {
            List<List<double[]>> 块 = 分组(展开(要素), 1.0);
            if (块.Count <= 1) break;

            List<double[]> 大块 = 块.OrderByDescending(l => l.Count).First();
            HashSet<double[]> 内部 = new HashSet<double[]>(大块);
            List<double[]> 全部点 = 块.SelectMany(l => l).ToList();

            double[] 甲点 = null, 乙点 = null;
            double 最近 = double.MaxValue;

            foreach (double[] p in 大块)
            {
                foreach (double[] q in 全部点)
                {
                    if (内部.Contains(q)) continue;
                    double d = 距离(p, q);
                    if (d < 最近) { 最近 = d; 甲点 = p; 乙点 = q; }
                }
            }
            if (甲点 == null) break;

            int 要素号;
            if (!归属.TryGetValue(甲点, out 要素号)) 要素号 = 0;
            要素[要素号].部分.Add(new List<double[]> { 甲点, 乙点 });

            桥数++;
            合计 += 最近;
            if (最近 > 最长) 最长 = 最近;
        }

        if (桥数 > 0)
            Console.WriteLine(string.Format("桥接合计 {0:F0} 米，最长一条 {1:F0} 米", 合计, 最长));
        return 桥数;
    }

    /// <summary>
    /// 找出“直线很近、路网却要绕远”的点对并直接补一条短连接，一次性消除全网绕行。
    /// 判据：直线距离 ≤ 最大缺口，且路网距离大于直线距离的 3 倍（或超出 40 米以上）。
    /// </summary>
    static int 消除绕行(List<线要素> 要素, List<段> 段表, double 最大缺口)
    {
        // 节点编号（1 米容差合并）
        Dictionary<string, List<double[]>> 网格 = new Dictionary<string, List<double[]>>();
        Dictionary<double[], double[]> 父 = new Dictionary<double[], double[]>();

        Func<double[], double[]> 找根 = null;
        找根 = p =>
        {
            while (!ReferenceEquals(父[p], p)) { 父[p] = 父[父[p]]; p = 父[p]; }
            return p;
        };

        Func<double[], double[]> 取节点 = p =>
        {
            int kx = (int)Math.Floor(p[0]), ky = (int)Math.Floor(p[1]);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    List<double[]> 桶;
                    if (!网格.TryGetValue((kx + dx) + "_" + (ky + dy), out 桶)) continue;
                    foreach (double[] q in 桶)
                        if (距离(p, q) <= 1.0) return q;
                }
            string 键 = kx + "_" + ky;
            List<double[]> 已有;
            if (!网格.TryGetValue(键, out 已有)) { 已有 = new List<double[]>(); 网格[键] = 已有; }
            已有.Add(p);
            父[p] = p;
            return p;
        };

        foreach (段 一条 in 段表)
        {
            if (一条.甲 == null || 一条.乙 == null) continue;
            double[] a = 取节点(一条.甲), b = 取节点(一条.乙);
            double[] ra = 找根(a), rb = 找根(b);
            if (!ReferenceEquals(ra, rb)) 父[ra] = rb;
        }

        Dictionary<double[], int> 编号 = new Dictionary<double[], int>();
        List<double[]> 坐标 = new List<double[]>();
        foreach (double[] 点 in new List<double[]>(父.Keys))
        {
            double[] 根 = 找根(点);
            if (!编号.ContainsKey(根)) { 编号[根] = 坐标.Count; 坐标.Add(根); }
        }

        List<Dictionary<int, double>> 邻接 = new List<Dictionary<int, double>>();
        for (int i = 0; i < 坐标.Count; i++) 邻接.Add(new Dictionary<int, double>());

        foreach (段 一条 in 段表)
        {
            if (一条.甲 == null || 一条.乙 == null) continue;
            double[] a = 取节点(一条.甲), b = 取节点(一条.乙);
            int ia = 编号[找根(a)], ib = 编号[找根(b)];
            if (ia == ib) continue;
            double w = 距离(一条.甲, 一条.乙);
            连边(邻接, ia, ib, w);
        }

        // 空间索引：按 最大缺口 分格
        Dictionary<string, List<int>> 空间 = new Dictionary<string, List<int>>();
        for (int i = 0; i < 坐标.Count; i++)
        {
            string 键 = (int)Math.Floor(坐标[i][0] / 最大缺口) + "_" + (int)Math.Floor(坐标[i][1] / 最大缺口);
            List<int> 桶;
            if (!空间.TryGetValue(键, out 桶)) { 桶 = new List<int>(); 空间[键] = 桶; }
            桶.Add(i);
        }

        List<int[]> 待补 = new List<int[]>();
        for (int i = 0; i < 坐标.Count; i++)
        {
            double[] 距离表 = 局部最短路(邻接, 坐标, i, 400.0);

            int 格X = (int)Math.Floor(坐标[i][0] / 最大缺口), 格Y = (int)Math.Floor(坐标[i][1] / 最大缺口);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    List<int> 桶;
                    if (!空间.TryGetValue((格X + dx) + "_" + (格Y + dy), out 桶)) continue;
                    foreach (int j in 桶)
                    {
                        if (j <= i) continue;
                        double 直线 = 距离(坐标[i], 坐标[j]);
                        if (直线 > 最大缺口 || 直线 < 1e-6) continue;
                        if (邻接[i].ContainsKey(j)) continue;

                        double 图距 = j < 距离表.Length ? 距离表[j] : double.MaxValue;
                        if (图距 == double.MaxValue) { 待补.Add(new[] { i, j }); continue; }

                        double 允许 = Math.Max(直线 * 3.0, 直线 + 40.0);
                        if (图距 > 允许) 待补.Add(new[] { i, j });
                    }
                }
        }

        // 每个节点只补一次，避免制造一堆短连线
        HashSet<int> 已用 = new HashSet<int>();
        int 补数 = 0, 要素号;
        Dictionary<double[], int> 归属 = new Dictionary<double[], int>();
        for (int f = 0; f < 要素.Count; f++)
            foreach (List<double[]> 部 in 要素[f].部分)
                foreach (double[] 点 in 部)
                    if (!归属.ContainsKey(点)) 归属[点] = f;

        foreach (int[] 一对 in 待补)
        {
            if (已用.Contains(一对[0]) || 已用.Contains(一对[1])) continue;

            double[] 甲点 = 坐标[一对[0]], 乙点 = 坐标[一对[1]];
            if (距离(甲点, 乙点) > 25.0) continue;   // 只自动补 25 米内的真缺口，远的不敢乱连

            if (!归属.TryGetValue(甲点, out 要素号)) 要素号 = 0;
            要素[要素号].部分.Add(new List<double[]> { 甲点, 乙点 });

            已用.Add(一对[0]); 已用.Add(一对[1]);
            补数++;
        }

        // 报告：随机抽样节点，找出“直线不远、走路却绕很多”的点对
        List<KeyValuePair<double, string>> 报告 = new List<KeyValuePair<double, string>>();
        for (int i = 0; i < 坐标.Count; i += 4)
        {
            double[] 距离表 = 堆最短路(邻接, 坐标.Count, i);

            for (int j = i + 1; j < 坐标.Count; j++)
            {
                double 直线 = 距离(坐标[i], 坐标[j]);
                if (直线 < 200 || 直线 > 1200) continue;
                if (距离表[j] == double.MaxValue) continue;

                double 倍 = 距离表[j] / 直线;
                if (倍 < 2.5) continue;

                报告.Add(new KeyValuePair<double, string>(倍, string.Format(
                    "({0:F0}, {1:F0}) ↔ ({2:F0}, {3:F0})  直线 {4:F0} 米，走路 {5:F0} 米，{6:F2} 倍",
                    坐标[i][0], 坐标[i][1], 坐标[j][0], 坐标[j][1], 直线, 距离表[j], 倍)));
            }
        }

        foreach (var 条 in 报告.OrderByDescending(kv => kv.Key).Take(12))
            Console.WriteLine("  " + 条.Value);
        if (报告.Count == 0)
            Console.WriteLine("  抽样未发现明显的绕行点对");

        return 补数;
    }

    static void 连边(List<Dictionary<int, double>> 邻接, int a, int b, double 权)
    {
        double 旧值;
        if (邻接[a].TryGetValue(b, out 旧值) && 旧值 <= 权) return;
        邻接[a][b] = 权;
        邻接[b][a] = 权;
    }

    /// <summary>限定半径的 Dijkstra，返回各节点在图上的最短距离（超出半径记 MaxValue）。</summary>
    static double[] 局部最短路(List<Dictionary<int, double>> 邻接, List<double[]> 坐标, int 起点, double 半径)
    {
        double[] 距离表 = new double[坐标.Count];
        for (int i = 0; i < 距离表.Length; i++) 距离表[i] = double.MaxValue;
        距离表[起点] = 0;

        List<int> 队列 = new List<int> { 起点 };
        while (队列.Count > 0)
        {
            int 当前 = 0;
            for (int i = 1; i < 队列.Count; i++) if (距离表[队列[i]] < 距离表[队列[当前]]) 当前 = i;
            int 节点 = 队列[当前];
            队列.RemoveAt(当前);

            foreach (KeyValuePair<int, double> 一条 in 邻接[节点])
            {
                double 新距 = 距离表[节点] + 一条.Value;
                if (新距 > 半径 || 新距 >= 距离表[一条.Key]) continue;
                距离表[一条.Key] = 新距;
                队列.Add(一条.Key);
            }
        }
        return 距离表;
    }

    /// <summary>带二叉堆的 Dijkstra。</summary>
    static double[] 堆最短路(List<Dictionary<int, double>> 邻接, int 节点数, int 起点)
    {
        double[] 距离表 = new double[节点数];
        for (int i = 0; i < 节点数; i++) 距离表[i] = double.MaxValue;
        距离表[起点] = 0;

        List<KeyValuePair<double, int>> 堆 = new List<KeyValuePair<double, int>>();
        堆.Add(new KeyValuePair<double, int>(0, 起点));

        while (堆.Count > 0)
        {
            KeyValuePair<double, int> 顶 = 堆[0];
            int 末尾 = 堆.Count - 1;
            堆[0] = 堆[末尾];
            堆.RemoveAt(末尾);
            下筛(堆, 0);

            if (顶.Key > 距离表[顶.Value]) continue;

            foreach (KeyValuePair<int, double> 一条 in 邻接[顶.Value])
            {
                double 新距 = 顶.Key + 一条.Value;
                if (新距 >= 距离表[一条.Key]) continue;
                距离表[一条.Key] = 新距;
                堆.Add(new KeyValuePair<double, int>(新距, 一条.Key));
                上筛(堆, 堆.Count - 1);
            }
        }
        return 距离表;
    }

    static void 上筛(List<KeyValuePair<double, int>> 堆, int 位置)
    {
        while (位置 > 0)
        {
            int 父 = (位置 - 1) / 2;
            if (堆[父].Key <= 堆[位置].Key) break;
            KeyValuePair<double, int> 暂 = 堆[父]; 堆[父] = 堆[位置]; 堆[位置] = 暂;
            位置 = 父;
        }
    }

    static void 下筛(List<KeyValuePair<double, int>> 堆, int 位置)
    {
        while (true)
        {
            int 左 = 位置 * 2 + 1, 右 = 左 + 1, 小 = 位置;
            if (左 < 堆.Count && 堆[左].Key < 堆[小].Key) 小 = 左;
            if (右 < 堆.Count && 堆[右].Key < 堆[小].Key) 小 = 右;
            if (小 == 位置) break;
            KeyValuePair<double, int> 暂 = 堆[小]; 堆[小] = 堆[位置]; 堆[位置] = 暂;
            位置 = 小;
        }
    }

    /// <summary>悬空端点（只有一条线用到它）在容差内接到最近的道路端点或线段上。</summary>
    static int 悬空端点连接(List<段> 段表, double 容差)
    {
        List<double[]> 端点 = new List<double[]>();
        Dictionary<double[], int> 度 = new Dictionary<double[], int>();
        foreach (段 一条 in 段表)
        {
            foreach (double[] 点 in new[] { 一条.甲, 一条.乙 })
            {
                if (点 == null) continue;
                if (!度.ContainsKey(点)) { 度[点] = 0; 端点.Add(点); }
                度[点]++;
            }
        }

        int 连接数 = 0;
        foreach (double[] 当前 in 端点)
        {
            if (度[当前] != 1) continue;   // 只处理悬空端点

            double 最近 = 容差;
            double[] 目标 = null;
            段 目标段 = null;
            double 目标比例 = 0;

            foreach (段 一条 in 段表)
            {
                if (一条.甲 == null || 一条.乙 == null) continue;
                if (ReferenceEquals(一条.甲, 当前) || ReferenceEquals(一条.乙, 当前)) continue;

                foreach (double[] 点 in new[] { 一条.甲, 一条.乙 })
                {
                    if (ReferenceEquals(点, 当前)) continue;
                    double d = 距离(当前, 点);
                    if (d < 最近) { 最近 = d; 目标 = 点; 目标段 = null; }
                }

                double 比例, 偏移 = 点到线段(当前, 一条, out 比例);
                if (偏移 < 最近 && 比例 > 0.001 && 比例 < 0.999)
                {
                    最近 = 偏移;
                    目标段 = 一条;
                    目标比例 = 比例;
                    目标 = new[] {
                        一条.甲[0] + 比例 * (一条.乙[0] - 一条.甲[0]),
                        一条.甲[1] + 比例 * (一条.乙[1] - 一条.甲[1]) };
                }
            }

            if (目标 == null) continue;

            当前[0] = 目标[0];
            当前[1] = 目标[1];
            if (目标段 != null) 目标段.附加.Add(new KeyValuePair<double, double[]>(目标比例, 目标));
            连接数++;
        }
        return 连接数;
    }

    /// <summary>端点落在另一条线段中间（T 形路口）时，把端点移到垂足，并在那条线上插点。</summary>
    static void 搭接处理(List<段> 段表)
    {
        foreach (段 一端 in 段表)
        {
            foreach (double[] 端点 in new[] { 一端.甲, 一端.乙 })
            {
                double 最近 = 搭接容差;
                段 命中 = null;
                double 命中比例 = 0;
                double[] 命中点 = null;

                foreach (段 另一端 in 段表)
                {
                    if (ReferenceEquals(另一端, 一端)) continue;
                    if (ReferenceEquals(另一端.甲, 端点) || ReferenceEquals(另一端.乙, 端点)) continue;

                    double 比例, 偏 = 点到线段(端点, 另一端, out 比例);
                    if (偏 < 最近 && 比例 > 0.001 && 比例 < 0.999)
                    {
                        最近 = 偏;
                        命中 = 另一端;
                        命中比例 = 比例;
                        命中点 = new[] {
                            另一端.甲[0] + 比例 * (另一端.乙[0] - 另一端.甲[0]),
                            另一端.甲[1] + 比例 * (另一端.乙[1] - 另一端.甲[1]) };
                    }
                }

                if (命中 != null)
                {
                    端点[0] = 命中点[0];
                    端点[1] = 命中点[1];
                    命中.附加.Add(new KeyValuePair<double, double[]>(命中比例, 命中点));
                }
            }
        }
    }

    /// <summary>两条线段内部相交时，在交点处给双方都插一个顶点。</summary>
    static void 交叉打断(List<段> 段表)
    {
        for (int i = 0; i < 段表.Count; i++)
        {
            段 甲段 = 段表[i];
            if (甲段.甲 == null || 甲段.乙 == null) continue;

            double 最小X1 = Math.Min(甲段.甲[0], 甲段.乙[0]), 最大X1 = Math.Max(甲段.甲[0], 甲段.乙[0]);
            double 最小Y1 = Math.Min(甲段.甲[1], 甲段.乙[1]), 最大Y1 = Math.Max(甲段.甲[1], 甲段.乙[1]);

            for (int j = i + 1; j < 段表.Count; j++)
            {
                段 乙段 = 段表[j];
                if (乙段.甲 == null || 乙段.乙 == null) continue;
                if (乙段.甲 == 甲段.甲 || 乙段.甲 == 甲段.乙 || 乙段.乙 == 甲段.甲 || 乙段.乙 == 甲段.乙) continue;

                if (Math.Min(乙段.甲[0], 乙段.乙[0]) > 最大X1 || Math.Max(乙段.甲[0], 乙段.乙[0]) < 最小X1) continue;
                if (Math.Min(乙段.甲[1], 乙段.乙[1]) > 最大Y1 || Math.Max(乙段.甲[1], 乙段.乙[1]) < 最小Y1) continue;

                double 比例1, 比例2;
                if (!求交(甲段, 乙段, out 比例1, out 比例2)) continue;

                double[] 交点 = new[] {
                    甲段.甲[0] + 比例1 * (甲段.乙[0] - 甲段.甲[0]),
                    甲段.甲[1] + 比例1 * (甲段.乙[1] - 甲段.甲[1]) };

                甲段.附加.Add(new KeyValuePair<double, double[]>(比例1, 交点));
                乙段.附加.Add(new KeyValuePair<double, double[]>(比例2, 交点));
            }
        }
    }

    // ------------------------------------------------------------ 写

    static void 写出(string 路径, List<线要素> 要素, List<段> 段表, int 原类型)
    {
        // 兼容旧文件：去掉之前生成的索引文件
        foreach (string 后缀 in new[] { ".shx", ".sbn", ".sbx", ".xml" })
        {
            string 旧 = Path.ChangeExtension(路径, 后缀);
            if (File.Exists(旧)) File.Delete(旧);
        }

        List<List<List<double[]>>> 新部分 = 重建(要素, 段表);
        int 记录数 = 新部分.Count;

        List<byte[]> 记录体 = new List<byte[]>();
        double 全局最小X = double.MaxValue, 全局最小Y = double.MaxValue;
        double 全局最大X = double.MinValue, 全局最大Y = double.MinValue;

        foreach (List<List<double[]>> 部分 in 新部分)
        {
            List<double[]> 全部点 = new List<double[]>();
            List<int> 部分索引 = new List<int>();
            foreach (List<double[]> 点列 in 部分)
            {
                if (点列.Count < 2) continue;
                部分索引.Add(全部点.Count);
                全部点.AddRange(点列);
            }
            if (全部点.Count < 2) { 记录体.Add(new byte[0]); continue; }

            double 最小X = 全部点.Min(p => p[0]), 最大X = 全部点.Max(p => p[0]);
            double 最小Y = 全部点.Min(p => p[1]), 最大Y = 全部点.Max(p => p[1]);
            全局最小X = Math.Min(全局最小X, 最小X); 全局最大X = Math.Max(全局最大X, 最大X);
            全局最小Y = Math.Min(全局最小Y, 最小Y); 全局最大Y = Math.Max(全局最大Y, 最大Y);

            using (MemoryStream 流 = new MemoryStream())
            using (BinaryWriter 写 = new BinaryWriter(流))
            {
                写.Write(3);                 // PolyLine
                写.Write(最小X); 写.Write(最小Y); 写.Write(最大X); 写.Write(最大Y);
                写.Write(部分索引.Count);
                写.Write(全部点.Count);
                foreach (int 索引 in 部分索引) 写.Write(索引);
                foreach (double[] 点 in 全部点) { 写.Write(点[0]); 写.Write(点[1]); }
                写.Flush();
                记录体.Add(流.ToArray());
            }
        }

        long 总长 = 100 + 记录体.Sum(b => 8 + b.Length) ;

        using (BinaryWriter 写 = new BinaryWriter(File.Create(路径)))
        {
            写.Write(大端(9994));
            写.Write(new byte[20]);
            写.Write(大端((int)(总长 / 2)));
            写.Write(1000);
            写.Write(3);
            if (全局最小X == double.MaxValue) { 全局最小X = 0; 全局最小Y = 0; 全局最大X = 0; 全局最大Y = 0; }
            写.Write(全局最小X); 写.Write(全局最小Y); 写.Write(全局最大X); 写.Write(全局最大Y);
            写.Write(0.0); 写.Write(0.0); 写.Write(0.0); 写.Write(0.0);

            for (int i = 0; i < 记录体.Count; i++)
            {
                写.Write(大端(i + 1));
                写.Write(大端(记录体[i].Length / 2));
                写.Write(记录体[i]);
            }
        }

        // 索引文件
        int 偏移字 = 50;
        using (BinaryWriter 写 = new BinaryWriter(File.Create(Path.ChangeExtension(路径, ".shx"))))
        {
            写.Write(大端(9994));
            写.Write(new byte[20]);
            写.Write(大端((50 + 4 * 记录体.Count)));
            写.Write(1000);
            写.Write(3);
            写.Write(全局最小X); 写.Write(全局最小Y); 写.Write(全局最大X); 写.Write(全局最大Y);
            写.Write(0.0); 写.Write(0.0); 写.Write(0.0); 写.Write(0.0);

            foreach (byte[] 体 in 记录体)
            {
                写.Write(大端(偏移字));
                写.Write(大端(体.Length / 2));
                偏移字 += 4 + 体.Length / 2;
            }
        }
    }

    /// <summary>按段表重建每个部分的点序列：原顶点 + 按比例排序的交叉/搭接点。</summary>
    static List<List<List<double[]>>> 重建(List<线要素> 要素, List<段> 段表)
    {
        Dictionary<string, List<段>> 索引 = new Dictionary<string, List<段>>();
        foreach (段 一条 in 段表)
        {
            string 键 = 一条.要素号 + "_" + 一条.部分号;
            List<段> 列表;
            if (!索引.TryGetValue(键, out 列表)) { 列表 = new List<段>(); 索引[键] = 列表; }
            列表.Add(一条);
        }

        List<List<List<double[]>>> 结果 = new List<List<List<double[]>>>();
        for (int f = 0; f < 要素.Count; f++)
        {
            List<List<double[]>> 部分结果 = new List<List<double[]>>();
            for (int p = 0; p < 要素[f].部分.Count; p++)
            {
                List<段> 列表;
                索引.TryGetValue(f + "_" + p, out 列表);
                if (列表 == null || 列表.Count == 0) { 部分结果.Add(new List<double[]>(要素[f].部分[p])); continue; }

                列表.Sort((a, b) => a.段号.CompareTo(b.段号));
                List<double[]> 点列 = new List<double[]>();
                追加(点列, 列表[0].甲);

                foreach (段 一条 in 列表)
                {
                    List<KeyValuePair<double, double[]>> 排序附加 = 一条.附加
                        .OrderBy(kv => kv.Key).ToList();
                    foreach (KeyValuePair<double, double[]> kv in 排序附加) 追加(点列, kv.Value);
                    追加(点列, 一条.乙);
                }
                部分结果.Add(点列);
            }
            结果.Add(部分结果);
        }
        return 结果;
    }

    static void 追加(List<double[]> 点列, double[] 点)
    {
        if (点列.Count > 0 && 距离(点列[点列.Count - 1], 点) < 去重容差) return;
        点列.Add(点);
    }

    // ------------------------------------------------------------ 几何

    static double 距离(double[] a, double[] b)
    {
        double dx = a[0] - b[0], dy = a[1] - b[1];
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static double 点到线段(double[] 点, 段 一条, out double 比例)
    {
        double dx = 一条.乙[0] - 一条.甲[0], dy = 一条.乙[1] - 一条.甲[1];
        double 长平方 = dx * dx + dy * dy;
        if (长平方 < 1e-12) { 比例 = 0; return 距离(点, 一条.甲); }

        比例 = ((点[0] - 一条.甲[0]) * dx + (点[1] - 一条.甲[1]) * dy) / 长平方;
        if (比例 < 0) 比例 = 0;
        if (比例 > 1) 比例 = 1;

        double 投影X = 一条.甲[0] + 比例 * dx, 投影Y = 一条.甲[1] + 比例 * dy;
        double ex = 点[0] - 投影X, ey = 点[1] - 投影Y;
        return Math.Sqrt(ex * ex + ey * ey);
    }

    static bool 求交(段 甲段, 段 乙段, out double 比例1, out double 比例2)
    {
        比例1 = 0; 比例2 = 0;

        double rx = 甲段.乙[0] - 甲段.甲[0], ry = 甲段.乙[1] - 甲段.甲[1];
        double sx = 乙段.乙[0] - 乙段.甲[0], sy = 乙段.乙[1] - 乙段.甲[1];
        double 行列式 = rx * sy - ry * sx;
        if (Math.Abs(行列式) < 1e-12) return false;

        double qx = 乙段.甲[0] - 甲段.甲[0], qy = 乙段.甲[1] - 甲段.甲[1];
        比例1 = (qx * sy - qy * sx) / 行列式;
        比例2 = (qx * ry - qy * rx) / 行列式;

        return 比例1 > 1e-4 && 比例1 < 1 - 1e-4 && 比例2 > 1e-4 && 比例2 < 1 - 1e-4;
    }

    static byte[] 大端(int 值)
    {
        byte[] 字节 = BitConverter.GetBytes(值);
        Array.Reverse(字节);
        return 字节;
    }

    // ------------------------------------------------------------ 验证

    static void 连通块(List<段> 段表, double 容差, out int 块数, out int[] 最大)
    {
        Dictionary<string, List<double[]>> 网格 = new Dictionary<string, List<double[]>>();
        Dictionary<double[], double[]> 父 = new Dictionary<double[], double[]>();

        Func<double[], double[]> 找根 = null;
        找根 = p =>
        {
            while (!ReferenceEquals(父[p], p)) { 父[p] = 父[父[p]]; p = 父[p]; }
            return p;
        };

        Func<double[], double[]> 取节点 = p =>
        {
            int kx = (int)Math.Floor(p[0] / 容差), ky = (int)Math.Floor(p[1] / 容差);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    List<double[]> 桶;
                    if (!网格.TryGetValue((kx + dx) + "_" + (ky + dy), out 桶)) continue;
                    foreach (double[] q in 桶)
                        if (距离(p, q) <= 容差) return q;
                }
            string 键 = kx + "_" + ky;
            List<double[]> 已有;
            if (!网格.TryGetValue(键, out 已有)) { 已有 = new List<double[]>(); 网格[键] = 已有; }
            已有.Add(p);
            父[p] = p;
            return p;
        };

        foreach (段 一条 in 段表)
        {
            if (一条.甲 == null || 一条.乙 == null) continue;
            double[] a = 取节点(一条.甲), b = 取节点(一条.乙);
            double[] ra = 找根(a), rb = 找根(b);
            if (!ReferenceEquals(ra, rb)) 父[ra] = rb;
        }

        List<double[]> 全部节点 = new List<double[]>(父.Keys);
        int[] 组 = 全部节点.GroupBy(找根).Select(g => g.Count()).OrderByDescending(v => v).ToArray();
        块数 = 组.Length;
        最大 = 组.Take(8).ToArray();
    }
}
