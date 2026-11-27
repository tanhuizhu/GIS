using System;
using System.Collections.Generic;
using System.Globalization;
using ESRI.ArcGIS.Carto;
using ESRI.ArcGIS.Geodatabase;
using ESRI.ArcGIS.Geometry;

namespace 校园GIS
{
    /// <summary>空间分析：缓冲区、叠置（相交）。</summary>
    internal static class 空间分析
    {
        /// <summary>对图层内每个要素生成缓冲区，结果保存为新的 Shapefile 并加载到地图。</summary>
        public static IFeatureLayer 缓冲区(IMap 地图, IFeatureLayer 输入图层,
            double 距离, out int 要素数)
        {
            要素数 = 0;
            if (输入图层 == null || 距离 <= 0) return null;

            IFeatureClass 源要素类 = 输入图层.FeatureClass;
            ISpatialReference 空间参考 = 地图工具.获取空间参考(源要素类);

            string 结果名称 = "缓冲区_" + DateTime.Now.ToString("HHmmss");
            IFeatureClass 结果要素类 = 地图工具.新建Shapefile(结果名称,
                esriGeometryType.esriGeometryPolygon, 空间参考);

            // 读取源图层的名称字段索引（如果有的话）
            int 名称字段序号 = 源要素类.FindField("NAME");
            if (名称字段序号 < 0) 名称字段序号 = 源要素类.FindField("名称");
            if (名称字段序号 < 0) 名称字段序号 = 源要素类.FindField("Name");

            IFeatureCursor 游标 = 源要素类.Search(null, false);
            IFeature 要素;
            while ((要素 = 游标.NextFeature()) != null)
            {
                IGeometry 图形 = 要素.ShapeCopy;
                if (图形 == null || 图形.IsEmpty) continue;

                IGeometry 缓冲图形 = null;
                try
                {
                    if (图形.SpatialReference == null && 空间参考 != null)
                        图形.SpatialReference = 空间参考;
                    缓冲图形 = ((ITopologicalOperator)图形).Buffer(距离);
                }
                catch
                {
                    continue;
                }
                if (缓冲图形 == null || 缓冲图形.IsEmpty) continue;

                try { ((ITopologicalOperator)缓冲图形).Simplify(); } catch { }

                // 读取源要素名称，用于结果命名
                string 要素名称 = "要素" + 要素.OID;
                if (名称字段序号 >= 0)
                {
                    object 名称值 = 要素.get_Value(名称字段序号);
                    if (名称值 != null && !string.IsNullOrWhiteSpace(名称值.ToString()))
                        要素名称 = 名称值.ToString();
                }

                地图工具.添加要素(结果要素类, 缓冲图形,
                    要素名称 + " 缓冲区 " + 距离.ToString("0.##") + " 米");
                要素数++;
            }
            System.Runtime.InteropServices.Marshal.ReleaseComObject(游标);

            if (要素数 == 0) return null;
            return 地图工具.添加到地图(地图, 结果要素类, "缓冲区结果");
        }

        /// <summary>两个图层的相交叠置分析，结果保存为新的 Shapefile 并加载到地图。</summary>
        public static IFeatureLayer 相交叠置(IMap 地图, IFeatureLayer 图层A, IFeatureLayer 图层B)
        {
            if (图层A == null || 图层B == null) return null;

            IBasicGeoprocessor 地理处理器 = new BasicGeoprocessorClass();
            地理处理器.SpatialReference = 地图工具.获取空间参考(图层A.FeatureClass);

            string 结果名称 = "叠置_" + DateTime.Now.ToString("HHmmss");

            IFeatureClassName 输出名称 = new FeatureClassNameClass();
            IDatasetName 输出数据集名称 = (IDatasetName)输出名称;
            输出数据集名称.Name = 结果名称;

            IWorkspaceName 工作空间名称 = new WorkspaceNameClass();
            工作空间名称.WorkspaceFactoryProgID = "esriDataSourcesFile.ShapefileWorkspaceFactory";
            工作空间名称.PathName = 地图工具.输出目录;
            输出数据集名称.WorkspaceName = 工作空间名称;

            IFeatureClass 结果要素类 = 地理处理器.Intersect(
                (ITable)图层A.FeatureClass, false,
                (ITable)图层B.FeatureClass, false,
                0.0, 输出名称);

            if (结果要素类 == null) return null;
            return 地图工具.添加到地图(地图, 结果要素类, "叠置结果");
        }
    }

    /// <summary>基于道路线图层构建的简单路网，用 Dijkstra 算法求最短路径。</summary>
    internal sealed class 道路网络
    {
        private struct 边
        {
            public int 目标节点;
            public double 长度;
        }

        /// <summary>道路图中的一条边（相邻两个顶点之间的一小段）。</summary>
        private struct 道路边
        {
            public int 甲;
            public int 乙;
            public double 长度;
        }

        /// <summary>端点合并容差（米）：小于该距离的两个顶点视为同一个路口。</summary>
        private const double 合并容差 = 1.0;

        private readonly List<double[]> _节点坐标 = new List<double[]>();
        private readonly List<List<边>> _邻接表 = new List<List<边>>();
        private readonly List<道路边> _道路边 = new List<道路边>();
        /// <summary>按容差格网建立的空间索引：格网编号 → 落在该格内的节点编号。</summary>
        private readonly Dictionary<long, List<int>> _节点网格 = new Dictionary<long, List<int>>();

        public int 节点数 { get { return _节点坐标.Count; } }

        public 道路网络(IFeatureLayer 线图层) : this(线图层, null) { }

        /// <summary>地图不为空时，把其它可见的线图层也并进路网（支路、步道等），避免只有主干道导致绕远。</summary>
        public 道路网络(IFeatureLayer 线图层, IMap 地图)
        {
            IFeatureClass 要素类 = 线图层.FeatureClass;
            读入要素类(要素类);

            if (地图 != null) 读入辅助线图层(地图, 线图层);

            // 同一交叉口的不同道路常常没有共用端点，按 6 米把邻近节点补连起来；
            // 容差偏大是为了让平行的双向车道、路边支线也能互通，避免绕远路
            补近距离连接(6.0);

            // 支路端点常常直接搭在另一条道路的中间（T 形路口），没有共用顶点，这里补连
            连接T形路口(4.0);

            // 两条路在中间交叉（X 形路口）、各自都没有节点，这里在交点处补节点
            建立交叉节点();
        }

        private void 读入要素类(IFeatureClass 要素类)
        {
            IFeatureCursor 游标 = 要素类.Search(null, false);
            IFeature 要素;
            while ((要素 = 游标.NextFeature()) != null)
            {
                加入几何(要素.ShapeCopy);
            }
            System.Runtime.InteropServices.Marshal.ReleaseComObject(游标);
        }

        private void 读入辅助线图层(IMap 地图, IFeatureLayer 主图层)
        {
            for (int i = 0; i < 地图.LayerCount; i++)
            {
                IFeatureLayer 图层 = 地图.get_Layer(i) as IFeatureLayer;
                if (图层 == null || !图层.Visible) continue;
                if (ReferenceEquals(图层, 主图层) || 图层.FeatureClass == null) continue;
                if (图层.FeatureClass.ShapeType != esriGeometryType.esriGeometryPolyline) continue;
                if (图层.FeatureClass.FeatureCount(null) == 0) continue;

                读入要素类(图层.FeatureClass);
                辅助图层数++;
            }
        }

        public int 辅助图层数 { get; private set; }

        /// <summary>遍历线段对，在真正相交的位置生成节点并接到两条路的端点上。</summary>
        private void 建立交叉节点()
        {
            int 边数 = _道路边.Count;
            for (int i = 0; i < 边数; i++)
            {
                道路边 边1 = _道路边[i];
                double[] A1 = _节点坐标[边1.甲];
                double[] B1 = _节点坐标[边1.乙];
                double 最小X1 = Math.Min(A1[0], B1[0]), 最大X1 = Math.Max(A1[0], B1[0]);
                double 最小Y1 = Math.Min(A1[1], B1[1]), 最大Y1 = Math.Max(A1[1], B1[1]);

                for (int j = i + 1; j < 边数; j++)
                {
                    道路边 边2 = _道路边[j];
                    if (边2.甲 == 边1.甲 || 边2.甲 == 边1.乙 ||
                        边2.乙 == 边1.甲 || 边2.乙 == 边1.乙) continue;

                    double[] A2 = _节点坐标[边2.甲];
                    double[] B2 = _节点坐标[边2.乙];

                    // 外包框不相交的直接跳过，避免大量无用的线段求交
                    if (Math.Min(A2[0], B2[0]) > 最大X1 || Math.Max(A2[0], B2[0]) < 最小X1) continue;
                    if (Math.Min(A2[1], B2[1]) > 最大Y1 || Math.Max(A2[1], B2[1]) < 最小Y1) continue;

                    double 比例1, 比例2;
                    if (!线段交点(A1[0], A1[1], B1[0], B1[1], A2[0], A2[1], B2[0], B2[1],
                        out 比例1, out 比例2)) continue;

                    加入交叉节点(边1, 比例1, 边2, 比例2);
                }
            }
        }

        /// <summary>判断两条线段是否在内部相交，返回各自的比例位置。</summary>
        private static bool 线段交点(double ax, double ay, double bx, double by,
            double cx, double cy, double dx, double dy, out double 比例1, out double 比例2)
        {
            比例1 = 0;
            比例2 = 0;

            double 向量1X = bx - ax, 向量1Y = by - ay;
            double 向量2X = dx - cx, 向量2Y = dy - cy;
            double 行列式 = 向量1X * 向量2Y - 向量1Y * 向量2X;
            if (Math.Abs(行列式) < 1e-9) return false;

            double 差X = cx - ax, 差Y = cy - ay;
            比例1 = (差X * 向量2Y - 差Y * 向量2X) / 行列式;
            比例2 = (差X * 向量1Y - 差Y * 向量1X) / 行列式;

            return 比例1 > 1e-4 && 比例1 < 1 - 1e-4 && 比例2 > 1e-4 && 比例2 < 1 - 1e-4;
        }

        private void 加入交叉节点(道路边 边1, double 比例1, 道路边 边2, double 比例2)
        {
            double[] A1 = _节点坐标[边1.甲];
            double[] B1 = _节点坐标[边1.乙];
            double 交点X = A1[0] + 比例1 * (B1[0] - A1[0]);
            double 交点Y = A1[1] + 比例1 * (B1[1] - A1[1]);

            int 节点 = _节点坐标.Count;
            _节点坐标.Add(new[] { 交点X, 交点Y });
            _邻接表.Add(new List<边>());

            连接节点(节点, 边1.甲, 比例1 * 边1.长度);
            连接节点(节点, 边1.乙, (1 - 比例1) * 边1.长度);
            连接节点(节点, 边2.甲, 比例2 * 边2.长度);
            连接节点(节点, 边2.乙, (1 - 比例2) * 边2.长度);
        }

        private void 连接节点(int 甲, int 乙, double 长度)
        {
            if (长度 <= 1e-6 || 甲 == 乙 || 已有边(甲, 乙)) return;
            _邻接表[甲].Add(new 边 { 目标节点 = 乙, 长度 = 长度 });
            _邻接表[乙].Add(new 边 { 目标节点 = 甲, 长度 = 长度 });
        }

        /// <summary>
        /// 把"悬空端点"接到它所在的道路上：端点落在另一条道路线段中间时，
        /// 按沿线距离补两条边连到该线段的两端，相当于在路网上打了个节点。
        /// </summary>
        private void 连接T形路口(double 最大距离)
        {
            int 节点总数 = _节点坐标.Count;
            List<int> 悬空节点 = new List<int>();
            for (int i = 0; i < 节点总数; i++)
            {
                if (_邻接表[i].Count == 1) 悬空节点.Add(i);
            }

            for (int k = 0; k < 悬空节点.Count; k++)
            {
                int 端点 = 悬空节点[k];
                double x = _节点坐标[端点][0];
                double y = _节点坐标[端点][1];

                double 最佳距离 = 最大距离;
                int 甲 = -1, 乙 = -1;
                double 距甲 = 0, 距乙 = 0;

                for (int e = 0; e < _道路边.Count; e++)
                {
                    道路边 一条道路 = _道路边[e];
                    if (一条道路.甲 == 端点 || 一条道路.乙 == 端点) continue;

                    double[] A = _节点坐标[一条道路.甲];
                    double[] B = _节点坐标[一条道路.乙];
                    double 段X = B[0] - A[0];
                    double 段Y = B[1] - A[1];
                    double 段长平方 = 段X * 段X + 段Y * 段Y;
                    if (段长平方 <= 1e-9) continue;

                    double 比例 = ((x - A[0]) * 段X + (y - A[1]) * 段Y) / 段长平方;
                    if (比例 <= 0.001 || 比例 >= 0.999) continue;

                    double 投影X = A[0] + 比例 * 段X;
                    double 投影Y = A[1] + 比例 * 段Y;
                    double 差X = x - 投影X;
                    double 差Y = y - 投影Y;
                    double 距离 = Math.Sqrt(差X * 差X + 差Y * 差Y);

                    if (距离 >= 最佳距离) continue;

                    最佳距离 = 距离;
                    甲 = 一条道路.甲;
                    乙 = 一条道路.乙;
                    距甲 = 比例 * 一条道路.长度;
                    距乙 = 一条道路.长度 - 距甲;
                }

                if (甲 < 0) continue;

                if (!已有边(端点, 甲))
                {
                    _邻接表[端点].Add(new 边 { 目标节点 = 甲, 长度 = 距甲 });
                    _邻接表[甲].Add(new 边 { 目标节点 = 端点, 长度 = 距甲 });
                }
                if (!已有边(端点, 乙))
                {
                    _邻接表[端点].Add(new 边 { 目标节点 = 乙, 长度 = 距乙 });
                    _邻接表[乙].Add(new 边 { 目标节点 = 端点, 长度 = 距乙 });
                }
            }
        }

        /// <summary>把距离很近但没有连边的两个节点补一条边，提高路网连通性。</summary>
        private void 补近距离连接(double 最大距离)
        {
            int 总数 = _节点坐标.Count;
            if (总数 < 2) return;

            double 最大平方 = 最大距离 * 最大距离;

            for (int i = 0; i < 总数; i++)
            {
                for (int j = i + 1; j < 总数; j++)
                {
                    double 差X = _节点坐标[i][0] - _节点坐标[j][0];
                    double 差Y = _节点坐标[i][1] - _节点坐标[j][1];
                    double 距离平方 = 差X * 差X + 差Y * 差Y;

                    if (距离平方 > 最大平方 || 距离平方 <= 1e-6) continue;
                    if (已有边(i, j)) continue;

                    double 距离 = Math.Sqrt(距离平方);
                    _邻接表[i].Add(new 边 { 目标节点 = j, 长度 = 距离 });
                    _邻接表[j].Add(new 边 { 目标节点 = i, 长度 = 距离 });
                }
            }
        }

        private bool 已有边(int 甲, int 乙)
        {
            List<边> 表 = _邻接表[甲];
            for (int i = 0; i < 表.Count; i++)
            {
                if (表[i].目标节点 == 乙) return true;
            }
            return false;
        }

        private void 加入几何(IGeometry 图形)
        {
            if (图形 == null || 图形.IsEmpty) return;
            if (图形.GeometryType != esriGeometryType.esriGeometryPolyline) return;

            IGeometryCollection 几何集合 = (IGeometryCollection)图形;
            for (int g = 0; g < 几何集合.GeometryCount; g++)
            {
                IPointCollection 点集 = 几何集合.get_Geometry(g) as IPointCollection;
                if (点集 == null || 点集.PointCount < 2) continue;

                int 上一个节点 = -1;
                for (int i = 0; i < 点集.PointCount; i++)
                {
                    IPoint 点 = 点集.get_Point(i);
                    int 节点 = 取得节点编号(点.X, 点.Y);

                    if (上一个节点 >= 0 && 上一个节点 != 节点)
                    {
                        double 差X = _节点坐标[上一个节点][0] - _节点坐标[节点][0];
                        double 差Y = _节点坐标[上一个节点][1] - _节点坐标[节点][1];
                        double 长度 = Math.Sqrt(差X * 差X + 差Y * 差Y);

                        if (长度 > 1e-6)
                        {
                            _邻接表[上一个节点].Add(new 边 { 目标节点 = 节点, 长度 = 长度 });
                            _邻接表[节点].Add(new 边 { 目标节点 = 上一个节点, 长度 = 长度 });
                            _道路边.Add(new 道路边 { 甲 = 上一个节点, 乙 = 节点, 长度 = 长度 });
                        }
                    }
                    上一个节点 = 节点;
                }
            }
        }

        /// <summary>以容差把相邻道路端点合并成同一个节点，保证路口连通。</summary>
        private int 取得节点编号(double x, double y)
        {
            long 列 = (long)Math.Floor(x / 合并容差);
            long 行 = (long)Math.Floor(y / 合并容差);

            for (long 列偏移 = -1; 列偏移 <= 1; 列偏移++)
            {
                for (long 行偏移 = -1; 行偏移 <= 1; 行偏移++)
                {
                    List<int> 候选;
                    if (!_节点网格.TryGetValue(格键(列 + 列偏移, 行 + 行偏移), out 候选)) continue;

                    for (int i = 0; i < 候选.Count; i++)
                    {
                        int 编号 = 候选[i];
                        double 差X = _节点坐标[编号][0] - x;
                        double 差Y = _节点坐标[编号][1] - y;
                        if (差X * 差X + 差Y * 差Y <= 合并容差 * 合并容差) return 编号;
                    }
                }
            }

            int 新编号 = _节点坐标.Count;
            _节点坐标.Add(new[] { x, y });
            _邻接表.Add(new List<边>());

            long 键 = 格键(列, 行);
            List<int> 本格;
            if (!_节点网格.TryGetValue(键, out 本格))
            {
                本格 = new List<int>();
                _节点网格[键] = 本格;
            }
            本格.Add(新编号);
            return 新编号;
        }

        private static long 格键(long 列, long 行)
        {
            return (列 << 32) ^ (行 & 0xFFFFFFFFL);
        }

        /// <summary>找离某点最近的**道路线段**，返回投影点及沿线距离（不是最近顶点）。</summary>
        private bool 吸附到道路(double x, double y, out int 边序号, out double[] 投影点,
            out double 距甲距离, out double 偏移)
        {
            边序号 = -1;
            投影点 = null;
            距甲距离 = 0;
            偏移 = double.MaxValue;

            for (int i = 0; i < _道路边.Count; i++)
            {
                道路边 一条道路 = _道路边[i];
                double[] 甲 = _节点坐标[一条道路.甲];
                double[] 乙 = _节点坐标[一条道路.乙];

                double 段X = 乙[0] - 甲[0];
                double 段Y = 乙[1] - 甲[1];
                double 段长平方 = 段X * 段X + 段Y * 段Y;
                if (段长平方 <= 1e-9) continue;

                double 比例 = ((x - 甲[0]) * 段X + (y - 甲[1]) * 段Y) / 段长平方;
                if (比例 < 0) 比例 = 0;
                if (比例 > 1) 比例 = 1;

                double 投影X = 甲[0] + 比例 * 段X;
                double 投影Y = 甲[1] + 比例 * 段Y;
                double 差X = x - 投影X;
                double 差Y = y - 投影Y;
                double 距离 = Math.Sqrt(差X * 差X + 差Y * 差Y);

                if (距离 < 偏移)
                {
                    偏移 = 距离;
                    边序号 = i;
                    投影点 = new[] { 投影X, 投影Y };
                    距甲距离 = 比例 * 一条道路.长度;
                }
            }

            return 边序号 >= 0;
        }

        /// <summary>把吸附点接进路网：建临时节点，按沿线距离连到该边的两个端点。</summary>
        private int 加入临时节点(int 边序号, double 距甲距离, double[] 坐标)
        {
            道路边 一条道路 = _道路边[边序号];
            int 编号 = _节点坐标.Count;
            _节点坐标.Add(坐标);
            _邻接表.Add(new List<边>());

            double 到乙距离 = 一条道路.长度 - 距甲距离;
            if (距甲距离 > 1e-6)
            {
                _邻接表[编号].Add(new 边 { 目标节点 = 一条道路.甲, 长度 = 距甲距离 });
                _邻接表[一条道路.甲].Add(new 边 { 目标节点 = 编号, 长度 = 距甲距离 });
            }
            if (到乙距离 > 1e-6)
            {
                _邻接表[编号].Add(new 边 { 目标节点 = 一条道路.乙, 长度 = 到乙距离 });
                _邻接表[一条道路.乙].Add(new 边 { 目标节点 = 编号, 长度 = 到乙距离 });
            }
            return 编号;
        }

        /// <summary>临时节点总是最后加入，先删编号大的，避免索引错位。</summary>
        private void 清理临时节点(int 甲, int 乙)
        {
            移除节点(Math.Max(甲, 乙));
            移除节点(Math.Min(甲, 乙));
        }

        private void 移除节点(int 编号)
        {
            for (int i = 0; i < _邻接表.Count; i++)
            {
                if (i == 编号) continue;
                List<边> 表 = _邻接表[i];
                for (int k = 表.Count - 1; k >= 0; k--)
                {
                    if (表[k].目标节点 == 编号) 表.RemoveAt(k);
                }
            }
            _节点坐标.RemoveAt(编号);
            _邻接表.RemoveAt(编号);
        }

        /// <summary>路径计算结果：路径、长度、吸附后的起终点和提示文字。</summary>
        internal sealed class 路径结果
        {
            public IPolyline 路径;
            public double 长度;
            public IPoint 吸附起点;
            public IPoint 吸附终点;
            public double 起点偏移;
            public double 终点偏移;
            public string 说明;
        }

        /// <summary>两点间沿道路的最短路径；点在路外时自动吸附到最近道路。</summary>
        public 路径结果 求路径(IPoint 起点, IPoint 终点)
        {
            路径结果 结果 = new 路径结果 { 说明 = "" };
            if (_道路边.Count == 0)
            {
                结果.说明 = "道路图层中没有可用的线要素。";
                return 结果;
            }

            int 起点边, 终点边;
            double[] 起点投影, 终点投影;
            double 起点距甲, 终点距甲, 起点偏移, 终点偏移;

            bool 起点可用 = 吸附到道路(起点.X, 起点.Y, out 起点边, out 起点投影, out 起点距甲, out 起点偏移);
            bool 终点可用 = 吸附到道路(终点.X, 终点.Y, out 终点边, out 终点投影, out 终点距甲, out 终点偏移);
            if (!起点可用 || !终点可用)
            {
                结果.说明 = "道路图层中没有可用的线要素。";
                return 结果;
            }

            IPoint 吸附起点 = new PointClass();
            吸附起点.PutCoords(起点投影[0], 起点投影[1]);
            IPoint 吸附终点 = new PointClass();
            吸附终点.PutCoords(终点投影[0], 终点投影[1]);

            结果.吸附起点 = 吸附起点;
            结果.吸附终点 = 吸附终点;
            结果.起点偏移 = 起点偏移;
            结果.终点偏移 = 终点偏移;

            int 临时起点 = 加入临时节点(起点边, 起点距甲, 起点投影);
            int 临时终点 = 加入临时节点(终点边, 终点距甲, 终点投影);

            // 起终点在同一条道路上时，允许直接沿这条道路走
            if (起点边 == 终点边)
            {
                double 直达 = Math.Abs(起点距甲 - 终点距甲);
                if (直达 > 1e-6)
                {
                    _邻接表[临时起点].Add(new 边 { 目标节点 = 临时终点, 长度 = 直达 });
                    _邻接表[临时终点].Add(new 边 { 目标节点 = 临时起点, 长度 = 直达 });
                }
            }

            double[] 距离;
            int[] 前驱;
            bool 已连通 = 最短路(临时起点, 临时终点, out 距离, out 前驱);
            string 补连说明 = "";

            // 道路数据常见两类断口：路口端点没重合、两条路交叉但都没有节点。
            // 一次桥接往往不够，这里反复把可达部分与其余部分之间最近的一对节点直连，
            // 直到连通或没有可接受的缺口为止，保证任意取点都能算出一条路线。
            if (!已连通)
            {
                int 补连次数 = 0;
                double 补连总长 = 0, 最大缺口 = 0;
                while (补连次数 < 8)
                {
                    double 缺口;
                    if (!桥接缺口(距离, 300.0, out 缺口)) break;

                    补连次数++;
                    补连总长 += 缺口;
                    if (缺口 > 最大缺口) 最大缺口 = 缺口;

                    已连通 = 最短路(临时起点, 临时终点, out 距离, out 前驱);
                    if (已连通) break;
                }

                if (已连通)
                {
                    补连说明 = string.Format(CultureInfo.InvariantCulture,
                        "道路数据有 {0} 处断口，已用直线补连（合计 {1:F0} 米，最长 {2:F0} 米）。",
                        补连次数, 补连总长, 最大缺口);
                }
            }

            if (!已连通)
            {
                清理临时节点(临时起点, 临时终点);
                结果.说明 = "路网不连通：起终点所在的道路之间断口过多（已尝试补连 8 处），请检查道路数据。";
                return 结果;
            }

            List<int> 路径 = new List<int>();
            for (int 节点 = 临时终点; 节点 != -1; 节点 = 前驱[节点])
            {
                if (路径.Contains(节点)) break;
                路径.Add(节点);
            }
            路径.Reverse();

            if (路径.Count < 2)
            {
                清理临时节点(临时起点, 临时终点);
                结果.说明 = "起点与终点几乎重合，无法生成路径。";
                return 结果;
            }

            IPointCollection 点集 = new PolylineClass();
            foreach (int 节点 in 路径)
            {
                IPoint 点 = new PointClass();
                点.PutCoords(_节点坐标[节点][0], _节点坐标[节点][1]);
                点集.AddPoint(点);
            }

            结果.长度 = 距离[临时终点];
            结果.路径 = (IPolyline)点集;
            double 直线距离 = Math.Sqrt((终点.X - 起点.X) * (终点.X - 起点.X) +
                                       (终点.Y - 起点.Y) * (终点.Y - 起点.Y));
            结果.说明 = string.Format(CultureInfo.InvariantCulture,
                "起点已吸附到最近道路（偏移 {0:F1} 米），终点偏移 {1:F1} 米。路线 {2:F0} 米，两点直线 {3:F0} 米。{4}",
                起点偏移, 终点偏移, 结果.长度, 直线距离, 补连说明);

            清理临时节点(临时起点, 临时终点);
            return 结果;
        }

        /// <summary>Dijkstra：返回两点是否连通，并带出各节点距离与前驱，用于回推路径。</summary>
        private bool 最短路(int 起点, int 终点, out double[] 距离, out int[] 前驱)
        {
            int 节点总数 = _节点坐标.Count;
            距离 = new double[节点总数];
            前驱 = new int[节点总数];
            bool[] 已确定 = new bool[节点总数];

            for (int i = 0; i < 节点总数; i++)
            {
                距离[i] = double.MaxValue;
                前驱[i] = -1;
            }
            距离[起点] = 0;

            for (int 轮次 = 0; 轮次 < 节点总数; 轮次++)
            {
                int 当前 = -1;
                double 最小 = double.MaxValue;
                for (int i = 0; i < 节点总数; i++)
                {
                    if (!已确定[i] && 距离[i] < 最小)
                    {
                        最小 = 距离[i];
                        当前 = i;
                    }
                }
                if (当前 < 0) break;
                if (当前 == 终点) break;
                已确定[当前] = true;

                List<边> 相邻边 = _邻接表[当前];
                for (int k = 0; k < 相邻边.Count; k++)
                {
                    边 一条边 = 相邻边[k];
                    if (距离[当前] + 一条边.长度 < 距离[一条边.目标节点])
                    {
                        距离[一条边.目标节点] = 距离[当前] + 一条边.长度;
                        前驱[一条边.目标节点] = 当前;
                    }
                }
            }

            return 距离[终点] != double.MaxValue;
        }

        /// <summary>在"可达"与"其余"节点之间找最近的一对并直连，返回缺口长度。</summary>
        private bool 桥接缺口(double[] 距离, double 最大缺口, out double 缺口长度)
        {
            int 可达 = -1, 目标 = -1;
            double 最近平方 = double.MaxValue;
            int 节点总数 = _节点坐标.Count;

            for (int i = 0; i < 节点总数; i++)
            {
                if (距离[i] == double.MaxValue) continue;

                for (int j = 0; j < 节点总数; j++)
                {
                    if (距离[j] != double.MaxValue) continue;

                    double 差X = _节点坐标[i][0] - _节点坐标[j][0];
                    double 差Y = _节点坐标[i][1] - _节点坐标[j][1];
                    double 平方 = 差X * 差X + 差Y * 差Y;
                    if (平方 < 最近平方)
                    {
                        最近平方 = 平方;
                        可达 = i;
                        目标 = j;
                    }
                }
            }

            缺口长度 = 最近平方 == double.MaxValue ? double.MaxValue : Math.Sqrt(最近平方);
            if (可达 < 0 || 目标 < 0 || 缺口长度 > 最大缺口) return false;

            _邻接表[可达].Add(new 边 { 目标节点 = 目标, 长度 = 缺口长度 });
            _邻接表[目标].Add(new 边 { 目标节点 = 可达, 长度 = 缺口长度 });
            return true;
        }

        /// <summary>求起点到终点的最短路径，返回折线；长度单位为米。</summary>
        public IPolyline 最短路径(IPoint 起点, IPoint 终点, out double 长度)
        {
            路径结果 结果 = 求路径(起点, 终点);
            长度 = 结果.长度;
            return 结果.路径;
        }
    }

    /// <summary>
    /// 创新功能一：家长亲友游览路线。
    /// 把图书馆、教学区、操场、宿舍区、行政区、医务室等景点，
    /// 用道路最短路径依次串联成一条推荐游览路线，并输出景点位置。
    /// </summary>
    internal static class 游览路线生成器
    {
        /// <summary>按游览顺序排列的景点图层名称（与界面上的中文图层名一致）。</summary>
        private static readonly string[] 景点名称 =
        {
            "图书馆", "教学区", "操场", "宿舍区", "行政区", "医务室"
        };

        public static IFeatureLayer 生成(IMap 地图, IFeatureLayer 道路图层,
            out int 景点数, out double 总长度)
        {
            景点数 = 0;
            总长度 = 0;
            if (地图 == null) return null;

            // 按预设顺序收集景点中心点
            List<IPoint> 景点 = new List<IPoint>();
            ISpatialReference 空间参考 = 道路图层 == null
                ? null
                : 地图工具.获取空间参考(道路图层.FeatureClass);

            for (int k = 0; k < 景点名称.Length; k++)
            {
                IFeatureLayer 图层 = 找图层(地图, 景点名称[k]);
                if (图层 == null) continue;

                IPoint 中心 = 图层中心(图层);
                if (中心 == null) continue;

                景点.Add(中心);
                if (空间参考 == null) 空间参考 = 地图工具.获取空间参考(图层.FeatureClass);
            }

            if (景点.Count < 2) return null;

            // 用道路网络逐段求最短路径，走不通时用直线连接，保证路线完整
            道路网络 路网 = 道路图层 == null ? null : new 道路网络(道路图层);
            IPointCollection 路线点集 = new PolylineClass();

            for (int i = 1; i < 景点.Count; i++)
            {
                double 段长 = 0;
                IPolyline 段 = null;
                if (路网 != null) 段 = 路网.最短路径(景点[i - 1], 景点[i], out 段长);
                if (段 == null)
                {
                    段 = 直线(景点[i - 1], 景点[i]);
                    段长 = 段.Length;
                }

                追加顶点(路线点集, 段);
                总长度 += 段长;
            }

            string 路线名称 = "游览路线_" + DateTime.Now.ToString("HHmmss");
            IFeatureClass 路线要素类 = 地图工具.新建Shapefile(路线名称,
                esriGeometryType.esriGeometryPolyline, 空间参考);
            地图工具.添加要素(路线要素类, (IGeometry)路线点集,
                string.Format("校园游览路线（{0} 个景点）", 景点.Count));

            string 景点名称表 = "景点_" + DateTime.Now.ToString("HHmmss");
            IFeatureClass 景点要素类 = 地图工具.新建Shapefile(景点名称表,
                esriGeometryType.esriGeometryPoint, 空间参考);
            for (int i = 0; i < 景点.Count; i++)
                地图工具.添加要素(景点要素类, 景点[i], 第几个景点名(i));

            地图工具.添加到地图(地图, 景点要素类, "游览景点");
            IFeatureLayer 结果 = 地图工具.添加到地图(地图, 路线要素类, "校园游览路线");

            景点数 = 景点.Count;
            return 结果;
        }

        /// <summary>按用户在地图上点选的若干地点，依次规划游览路线。</summary>
        public static IFeatureLayer 生成自定义(IMap 地图, IFeatureLayer 道路图层,
            List<IPoint> 地点, out int 景点数, out double 总长度)
        {
            景点数 = 地点 == null ? 0 : 地点.Count;
            总长度 = 0;
            if (地图 == null || 地点 == null || 地点.Count < 2) return null;

            ISpatialReference 空间参考 = 道路图层 == null
                ? null
                : 地图工具.获取空间参考(道路图层.FeatureClass);
            if (空间参考 == null) 空间参考 = 地图.SpatialReference;

            道路网络 路网 = 道路图层 == null ? null : new 道路网络(道路图层);
            IPointCollection 路线点集 = new PolylineClass();

            for (int i = 1; i < 地点.Count; i++)
            {
                double 段长 = 0;
                IPolyline 段 = null;
                if (路网 != null) 段 = 路网.最短路径(地点[i - 1], 地点[i], out 段长);
                if (段 == null)
                {
                    段 = 直线(地点[i - 1], 地点[i]);
                    段长 = 段.Length;
                }

                追加顶点(路线点集, 段);
                总长度 += 段长;
            }

            string 路线名称 = "自定义游览路线_" + DateTime.Now.ToString("HHmmss");
            IFeatureClass 路线要素类 = 地图工具.新建Shapefile(路线名称,
                esriGeometryType.esriGeometryPolyline, 空间参考);
            地图工具.添加要素(路线要素类, (IGeometry)路线点集, "自定义游览路线");

            string 地点名称 = "游览地点_" + DateTime.Now.ToString("HHmmss");
            IFeatureClass 地点要素类 = 地图工具.新建Shapefile(地点名称,
                esriGeometryType.esriGeometryPoint, 空间参考);
            for (int i = 0; i < 地点.Count; i++)
                地图工具.添加要素(地点要素类, 地点[i], "第 " + (i + 1) + " 站");

            地图工具.添加到地图(地图, 地点要素类, "游览地点");
            return 地图工具.添加到地图(地图, 路线要素类, "自定义游览路线");
        }

        private static IFeatureLayer 找图层(IMap 地图, string 名称)
        {
            for (int i = 0; i < 地图.LayerCount; i++)
            {
                ILayer 图层 = 地图.get_Layer(i);
                if (图层 != null && 图层.Name == 名称) return 图层 as IFeatureLayer;
            }
            return null;
        }

        private static string 第几个景点名(int 序号)
        {
            return 序号 < 景点名称.Length ? 景点名称[序号] : ("景点 " + (序号 + 1));
        }

        /// <summary>取一个图层所有要素范围的中心作为景点位置。</summary>
        private static IPoint 图层中心(IFeatureLayer 图层)
        {
            try
            {
                IGeoDataset 数据集 = 图层.FeatureClass as IGeoDataset;
                if (数据集 == null) return null;

                IEnvelope 范围 = 数据集.Extent;
                if (范围 == null || 范围.IsEmpty) return null;

                IPoint 点 = new PointClass();
                点.PutCoords((范围.XMin + 范围.XMax) / 2.0,
                             (范围.YMin + 范围.YMax) / 2.0);
                return 点;
            }
            catch
            {
                return null;
            }
        }

        private static IPolyline 直线(IPoint 起, IPoint 止)
        {
            IPointCollection 点集 = new PolylineClass();
            IPoint 起点 = new PointClass();
            起点.PutCoords(起.X, 起.Y);
            IPoint 终点 = new PointClass();
            终点.PutCoords(止.X, 止.Y);
            点集.AddPoint(起点);
            点集.AddPoint(终点);
            return (IPolyline)点集;
        }

        private static void 追加顶点(IPointCollection 接收, IPolyline 段)
        {
            if (段 == null) return;

            IPointCollection 来源 = 段 as IPointCollection;
            if (来源 == null) return;

            for (int i = 0; i < 来源.PointCount; i++)
            {
                IPoint 原点 = 来源.get_Point(i);
                IPoint 点 = new PointClass();
                点.PutCoords(原点.X, 原点.Y);
                接收.AddPoint(点);
            }
        }
    }

    /// <summary>
    /// 创新功能二：非机动车停车位推荐。
    /// 沿道路每隔一定距离布设候选点，剔除离建筑、绿地、水体太近的点，
    /// 剩下的点作为推荐停车位输出为点图层。
    /// </summary>
    internal static class 停车位推荐器
    {
        private static readonly string[] 避让图层名称 =
        {
            "其他建筑", "图书馆", "行政区", "宿舍区", "教学区", "医务室", "绿地", "水体"
        };

        public static IFeatureLayer 生成(IMap 地图, IFeatureLayer 道路图层,
            double 间距, double 避让距离, out int 点位数)
        {
            点位数 = 0;
            if (地图 == null || 道路图层 == null || 道路图层.FeatureClass == null) return null;
            if (间距 <= 0) 间距 = 40;

            ISpatialReference 空间参考 = 地图工具.获取空间参考(道路图层.FeatureClass);

            List<IFeatureLayer> 避让图层 = new List<IFeatureLayer>();
            for (int i = 0; i < 地图.LayerCount; i++)
            {
                IFeatureLayer 图层 = 地图.get_Layer(i) as IFeatureLayer;
                if (图层 == null) continue;
                if (Array.IndexOf(避让图层名称, 图层.Name) >= 0) 避让图层.Add(图层);
            }

            string 结果名称 = "非机动车停车位_" + DateTime.Now.ToString("HHmmss");
            IFeatureClass 结果要素类 = 地图工具.新建Shapefile(结果名称,
                esriGeometryType.esriGeometryPoint, 空间参考);

            IFeatureCursor 游标 = 道路图层.FeatureClass.Search(null, false);
            IFeature 道路;
            while ((道路 = 游标.NextFeature()) != null)
            {
                IGeometry 图形 = 道路.ShapeCopy;
                if (图形 == null || 图形.IsEmpty) continue;

                IGeometryCollection 集合 = 图形 as IGeometryCollection;
                if (集合 == null) continue;

                for (int g = 0; g < 集合.GeometryCount; g++)
                {
                    IPointCollection 点集 = 集合.get_Geometry(g) as IPointCollection;
                    if (点集 == null || 点集.PointCount < 2) continue;

                    double 累计 = 0;
                    double 下一个 = 间距 / 2.0;

                    for (int i = 1; i < 点集.PointCount; i++)
                    {
                        IPoint 前 = 点集.get_Point(i - 1);
                        IPoint 后 = 点集.get_Point(i);
                        double 段长 = 两点距离(前, 后);
                        if (段长 <= 0) continue;

                        while (下一个 <= 累计 + 段长)
                        {
                            double 比例 = (下一个 - 累计) / 段长;
                            IPoint 候选 = new PointClass();
                            候选.PutCoords(前.X + (后.X - 前.X) * 比例,
                                           前.Y + (后.Y - 前.Y) * 比例);

                            if (可以布设(候选, 避让图层, 避让距离))
                            {
                                地图工具.添加要素(结果要素类, 候选, "非机动车停车位");
                                点位数++;
                            }
                            下一个 += 间距;
                        }
                        累计 += 段长;
                    }
                }
            }
            System.Runtime.InteropServices.Marshal.ReleaseComObject(游标);

            if (点位数 == 0) return null;
            return 地图工具.添加到地图(地图, 结果要素类, "非机动车停车位");
        }

        private static double 两点距离(IPoint A, IPoint B)
        {
            double 差X = A.X - B.X;
            double 差Y = A.Y - B.Y;
            return Math.Sqrt(差X * 差X + 差Y * 差Y);
        }

        /// <summary>候选点周围不能压到建筑、绿地、水体。</summary>
        private static bool 可以布设(IPoint 点, List<IFeatureLayer> 避让图层, double 避让距离)
        {
            for (int i = 0; i < 避让图层.Count; i++)
            {
                IFeatureLayer 图层 = 避让图层[i];
                try
                {
                    IEnvelope 范围 = new EnvelopeClass();
                    范围.PutCoords(点.X - 避让距离, 点.Y - 避让距离,
                                   点.X + 避让距离, 点.Y + 避让距离);

                    ISpatialFilter 过滤 = new SpatialFilterClass();
                    过滤.Geometry = 范围;
                    过滤.GeometryField = 图层.FeatureClass.ShapeFieldName;
                    过滤.SpatialRel = esriSpatialRelEnum.esriSpatialRelIntersects;

                    if (图层.FeatureClass.FeatureCount(过滤) > 0) return false;
                }
                catch { }
            }
            return true;
        }
    }
}
