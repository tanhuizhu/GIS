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

                地图工具.添加要素(结果要素类, 缓冲图形,
                    "缓冲区 " + 距离.ToString("0.##") + " 米");
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

        private readonly List<double[]> _节点坐标 = new List<double[]>();
        private readonly Dictionary<string, int> _节点索引 = new Dictionary<string, int>();
        private readonly List<List<边>> _邻接表 = new List<List<边>>();

        public int 节点数 { get { return _节点坐标.Count; } }

        public 道路网络(IFeatureLayer 线图层)
        {
            IFeatureClass 要素类 = 线图层.FeatureClass;
            IFeatureCursor 游标 = 要素类.Search(null, false);
            IFeature 要素;
            while ((要素 = 游标.NextFeature()) != null)
            {
                加入几何(要素.ShapeCopy);
            }
            System.Runtime.InteropServices.Marshal.ReleaseComObject(游标);

            // 同一交叉口的不同道路常常没有共用端点，按 2 米把邻近节点补连起来
            补近距离连接(2.0);
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
                        }
                    }
                    上一个节点 = 节点;
                }
            }
        }

        /// <summary>以 1 米为容差把相邻道路端点合并成同一个节点，保证路网连通。</summary>
        private int 取得节点编号(double x, double y)
        {
            double 圆整X = Math.Round(x);
            double 圆整Y = Math.Round(y);
            string 键 = string.Format(CultureInfo.InvariantCulture, "{0:F0},{1:F0}", 圆整X, 圆整Y);

            int 编号;
            if (_节点索引.TryGetValue(键, out 编号)) return 编号;

            编号 = _节点坐标.Count;
            _节点索引[键] = 编号;
            _节点坐标.Add(new[] { 圆整X, 圆整Y });
            _邻接表.Add(new List<边>());
            return 编号;
        }

        private int 最近节点(double x, double y)
        {
            int 最佳编号 = -1;
            double 最佳距离 = double.MaxValue;
            for (int i = 0; i < _节点坐标.Count; i++)
            {
                double 差X = _节点坐标[i][0] - x;
                double 差Y = _节点坐标[i][1] - y;
                double 距离平方 = 差X * 差X + 差Y * 差Y;
                if (距离平方 < 最佳距离)
                {
                    最佳距离 = 距离平方;
                    最佳编号 = i;
                }
            }
            return 最佳编号;
        }

        /// <summary>求起点到终点的最短路径，返回折线；长度单位为米。</summary>
        public IPolyline 最短路径(IPoint 起点, IPoint 终点, out double 长度)
        {
            长度 = -1;
            if (_节点坐标.Count == 0) return null;

            int 起点编号 = 最近节点(起点.X, 起点.Y);
            int 终点编号 = 最近节点(终点.X, 终点.Y);
            if (起点编号 < 0 || 终点编号 < 0) return null;

            int 节点总数 = _节点坐标.Count;
            double[] 距离 = new double[节点总数];
            int[] 前驱 = new int[节点总数];
            bool[] 已确定 = new bool[节点总数];

            for (int i = 0; i < 节点总数; i++)
            {
                距离[i] = double.MaxValue;
                前驱[i] = -1;
            }
            距离[起点编号] = 0;

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
                if (当前 == 终点编号) break;
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

            // 终点所在道路与起点不连通时，退而求其次：接到离终点最近的可达节点，
            // 再用一段直线连到真实终点，保证路线画到目的地。
            bool 用了替代终点 = false;
            if (距离[终点编号] == double.MaxValue)
            {
                int 替代节点 = -1;
                double 最佳距离 = double.MaxValue;
                for (int i = 0; i < 节点总数; i++)
                {
                    if (距离[i] == double.MaxValue) continue;

                    double 差X = _节点坐标[i][0] - 终点.X;
                    double 差Y = _节点坐标[i][1] - 终点.Y;
                    double 距离平方 = 差X * 差X + 差Y * 差Y;
                    if (距离平方 < 最佳距离)
                    {
                        最佳距离 = 距离平方;
                        替代节点 = i;
                    }
                }
                if (替代节点 < 0) return null;
                终点编号 = 替代节点;
                用了替代终点 = true;
            }

            List<int> 路径 = new List<int>();
            for (int v = 终点编号; v != -1; v = 前驱[v]) 路径.Add(v);
            路径.Reverse();

            if (路径.Count < 2) return null;

            IPointCollection 点集 = new PolylineClass();
            foreach (int 编号 in 路径)
            {
                IPoint 点 = new PointClass();
                点.PutCoords(_节点坐标[编号][0], _节点坐标[编号][1]);
                点集.AddPoint(点);
            }

            长度 = 距离[终点编号];

            if (用了替代终点)
            {
                IPoint 真实终点 = new PointClass();
                真实终点.PutCoords(终点.X, 终点.Y);
                点集.AddPoint(真实终点);

                double 差X = _节点坐标[终点编号][0] - 终点.X;
                double 差Y = _节点坐标[终点编号][1] - 终点.Y;
                长度 += Math.Sqrt(差X * 差X + 差Y * 差Y);
            }

            return (IPolyline)点集;
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
