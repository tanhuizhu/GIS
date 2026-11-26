using System;
using System.IO;
using System.Text;
using ESRI.ArcGIS.Carto;
using ESRI.ArcGIS.DataSourcesFile;
using ESRI.ArcGIS.Display;
using ESRI.ArcGIS.esriSystem;
using ESRI.ArcGIS.Geodatabase;
using ESRI.ArcGIS.Geometry;
using ESRI.ArcGIS.Output;
using Path = System.IO.Path;

namespace 校园GIS
{
    /// <summary>
    /// 通用工具集合：目录定位、空间参考、Shapefile 读写、地图输出、符号生成等。
    /// </summary>
    internal static class 地图工具
    {
        private static string _根目录;

        /// <summary>项目根目录（即包含“数据”文件夹的那一级目录）。</summary>
        public static string 根目录
        {
            get
            {
                if (_根目录 != null) return _根目录;

                string 目录 = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                for (int i = 0; i < 10 && !string.IsNullOrEmpty(目录); i++)
                {
                    if (Directory.Exists(Path.Combine(目录, "数据_南邮")) ||
                        Directory.Exists(Path.Combine(目录, "数据")))
                    {
                        _根目录 = 目录;
                        return _根目录;
                    }
                    DirectoryInfo 上级 = Directory.GetParent(目录);
                    if (上级 == null) break;
                    目录 = 上级.FullName;
                }

                // 找不到就退回程序所在目录
                _根目录 = AppDomain.CurrentDomain.BaseDirectory;
                return _根目录;
            }
        }

        /// <summary>存放原始 shapefile 数据的目录，优先使用南邮校园数据。</summary>
        public static string 数据目录
        {
            get
            {
                string 南邮目录 = Path.Combine(根目录, "数据_南邮");
                if (Directory.Exists(南邮目录)) return 南邮目录;
                return Path.Combine(根目录, "数据");
            }
        }

        /// <summary>存放分析结果、导出图片的目录，不存在会自动创建。</summary>
        public static string 输出目录
        {
            get
            {
                string 目录 = Path.Combine(根目录, "输出");
                if (!Directory.Exists(目录)) Directory.CreateDirectory(目录);
                return 目录;
            }
        }

        public static ISpatialReference 获取空间参考(IFeatureClass 要素类)
        {
            IGeoDataset 数据集 = 要素类 as IGeoDataset;
            return 数据集 == null ? null : 数据集.SpatialReference;
        }

        /// <summary>在输出目录新建一个只含 NAME 字段的 Shapefile。</summary>
        public static IFeatureClass 新建Shapefile(string 名称, esriGeometryType 类型, ISpatialReference 空间参考)
        {
            string 目录 = 输出目录;
            string[] 后缀 = { ".shp", ".shx", ".dbf", ".prj", ".sbn", ".sbx", ".cpg" };
            foreach (string 扩展名 in 后缀)
            {
                string 路径 = Path.Combine(目录, 名称 + 扩展名);
                if (File.Exists(路径)) File.Delete(路径);
            }

            IWorkspaceFactory 工厂 = new ShapefileWorkspaceFactoryClass();
            IFeatureWorkspace 工作空间 = (IFeatureWorkspace)工厂.OpenFromFile(目录, 0);

            IFields 字段集 = new FieldsClass();
            IFieldsEdit 字段编辑 = (IFieldsEdit)字段集;

            IField 标识字段 = new FieldClass();
            IFieldEdit 标识编辑 = (IFieldEdit)标识字段;
            标识编辑.Name_2 = "FID";
            标识编辑.Type_2 = esriFieldType.esriFieldTypeOID;
            字段编辑.AddField(标识字段);

            IField 图形字段 = new FieldClass();
            IFieldEdit 图形编辑 = (IFieldEdit)图形字段;
            图形编辑.Name_2 = "Shape";
            图形编辑.Type_2 = esriFieldType.esriFieldTypeGeometry;

            IGeometryDef 几何定义 = new GeometryDefClass();
            IGeometryDefEdit 几何定义编辑 = (IGeometryDefEdit)几何定义;
            几何定义编辑.GeometryType_2 = 类型;
            if (空间参考 != null) 几何定义编辑.SpatialReference_2 = 空间参考;
            几何定义编辑.GridCount_2 = 1;
            几何定义编辑.set_GridSize(0, 0);
            图形编辑.GeometryDef_2 = 几何定义;
            字段编辑.AddField(图形字段);

            IField 名称字段 = new FieldClass();
            IFieldEdit 名称编辑 = (IFieldEdit)名称字段;
            名称编辑.Name_2 = "NAME";
            名称编辑.Type_2 = esriFieldType.esriFieldTypeString;
            名称编辑.Length_2 = 60;
            字段编辑.AddField(名称字段);

            return 工作空间.CreateFeatureClass(名称, 字段集, null, null,
                esriFeatureType.esriFTSimple, "Shape", "");
        }

        public static void 添加要素(IFeatureClass 要素类, IGeometry 图形, string 名称)
        {
            IFeature 要素 = 要素类.CreateFeature();
            要素.Shape = 图形;
            int 字段序号 = 要素类.FindField("NAME");
            if (字段序号 >= 0) 要素.set_Value(字段序号, 名称);
            要素.Store();
        }

        public static IFeatureLayer 添加到地图(IMap 地图, IFeatureClass 要素类, string 图层名)
        {
            IFeatureLayer 图层 = new FeatureLayerClass();
            图层.FeatureClass = 要素类;
            图层.Name = 图层名;

            // 给结果图层配上默认符号和选中符号，否则分析结果可能是看不见的颜色
            IGeoFeatureLayer 可渲染图层 = 图层 as IGeoFeatureLayer;
            if (可渲染图层 != null)
            {
                ISimpleRenderer 渲染器 = new SimpleRendererClass();
                渲染器.Symbol = 创建符号(要素类.ShapeType, 地图.LayerCount, 0);
                可渲染图层.Renderer = (IFeatureRenderer)渲染器;
            }

            IFeatureSelection 选择 = 图层 as IFeatureSelection;
            if (选择 != null)
                选择.SelectionSymbol = 创建符号(要素类.ShapeType, 3, 0);

            地图.AddLayer(图层);
            ((IActiveView)地图).Refresh();
            return 图层;
        }

        /// <summary>把某个要素图层按原字段结构导出为 Shapefile。</summary>
        public static void 导出图层(IFeatureLayer 图层, string 目录, string 名称)
        {
            IFeatureClass 源要素类 = 图层.FeatureClass;
            IDataset 源数据集 = (IDataset)源要素类;
            IFeatureClassName 源名称 = (IFeatureClassName)源数据集.FullName;

            IFeatureClassName 输出名称 = new FeatureClassNameClass();
            IDatasetName 输出数据集名称 = (IDatasetName)输出名称;
            输出数据集名称.Name = 名称;

            IWorkspaceName 工作空间名称 = new WorkspaceNameClass();
            工作空间名称.WorkspaceFactoryProgID = "esriDataSourcesFile.ShapefileWorkspaceFactory";
            工作空间名称.PathName = 目录;
            输出数据集名称.WorkspaceName = 工作空间名称;

            int 图形字段序号 = 源要素类.FindField(源要素类.ShapeFieldName);
            IGeometryDef 几何定义 = 源要素类.Fields.get_Field(图形字段序号).GeometryDef;

            IFeatureDataConverter 转换器 = new FeatureDataConverterClass();
            转换器.ConvertFeatureClass(源名称, null, null, 输出名称, 几何定义,
                源要素类.Fields, "", 1000, 0);
        }

        /// <summary>把当前视图按当前范围输出为 PNG 图片（保持地图宽高比，不拉伸）。</summary>
        public static void 导出地图图片(IActiveView 视图, IEnvelope 范围, string 路径)
        {
            if (视图 == null || 范围 == null || 范围.IsEmpty) return;

            const int 图片宽度 = 1600;
            double 比例 = 范围.Width / Math.Max(1e-9, 范围.Height);
            int 图片高度 = (int)Math.Round(图片宽度 / 比例);
            if (图片高度 < 200) 图片高度 = 200;
            if (图片高度 > 4000) 图片高度 = 4000;

            IExport 导出器 = new ExportPNGClass();
            导出器.ExportFileName = 路径;
            导出器.Resolution = 96;

            IEnvelope 像素范围 = new EnvelopeClass();
            像素范围.PutCoords(0, 0, 图片宽度, 图片高度);
            导出器.PixelBounds = 像素范围;

            tagRECT 矩形 = new tagRECT();
            矩形.left = 0;
            矩形.top = 0;
            矩形.right = 图片宽度;
            矩形.bottom = 图片高度;

            int 设备句柄 = 导出器.StartExporting();
            视图.Output(设备句柄, 96, ref 矩形, 范围, null);
            导出器.FinishExporting();
            导出器.Cleanup();
        }

        /// <summary>按几何类型生成简单符号，颜色序号用于区分不同图层。</summary>
        public static ISymbol 创建符号(esriGeometryType 类型, int 颜色序号, double 尺寸)
        {
            int[][] 调色板 =
            {
                new[] { 230, 120, 60 },
                new[] { 60, 130, 210 },
                new[] { 90, 175, 90 },
                new[] { 205, 165, 40 },
                new[] { 155, 95, 205 },
                new[] { 220, 85, 130 }
            };
            int[] 颜色值 = 调色板[Math.Abs(颜色序号) % 调色板.Length];

            IRgbColor 颜色 = new RgbColorClass();
            颜色.Red = 颜色值[0];
            颜色.Green = 颜色值[1];
            颜色.Blue = 颜色值[2];

            IRgbColor 轮廓色 = new RgbColorClass();
            轮廓色.Red = 70;
            轮廓色.Green = 70;
            轮廓色.Blue = 70;

            switch (类型)
            {
                case esriGeometryType.esriGeometryPoint:
                case esriGeometryType.esriGeometryMultipoint:
                {
                    ISimpleMarkerSymbol 点符号 = new SimpleMarkerSymbolClass();
                    点符号.Style = esriSimpleMarkerStyle.esriSMSCircle;
                    点符号.Size = 尺寸 <= 0 ? 7 : 尺寸;
                    点符号.Color = 颜色;
                    点符号.Outline = true;
                    点符号.OutlineColor = 轮廓色;
                    return (ISymbol)点符号;
                }
                case esriGeometryType.esriGeometryPolyline:
                {
                    ISimpleLineSymbol 线符号 = new SimpleLineSymbolClass();
                    线符号.Style = esriSimpleLineStyle.esriSLSSolid;
                    线符号.Width = 尺寸 <= 0 ? 1.5 : 尺寸;
                    线符号.Color = 颜色;
                    return (ISymbol)线符号;
                }
                default:
                {
                    ISimpleFillSymbol 面符号 = new SimpleFillSymbolClass();
                    面符号.Style = esriSimpleFillStyle.esriSFSSolid;
                    面符号.Color = 颜色;
                    ISimpleLineSymbol 边线 = new SimpleLineSymbolClass();
                    边线.Width = 0.4;
                    边线.Color = 轮廓色;
                    面符号.Outline = 边线;
                    return (ISymbol)面符号;
                }
            }
        }

        /// <summary>把一个要素的所有非图形字段拼成便于阅读的文本。</summary>
        public static string 行转文本(IRow 行)
        {
            StringBuilder 文本 = new StringBuilder();
            IFields 字段集 = 行.Fields;
            for (int i = 0; i < 字段集.FieldCount; i++)
            {
                IField 字段 = 字段集.get_Field(i);
                if (字段.Type == esriFieldType.esriFieldTypeGeometry) continue;
                object 值 = null;
                try { 值 = 行.get_Value(i); } catch { }
                文本.AppendLine("  " + 字段.Name + " = " +
                                (值 == null || 值 == DBNull.Value ? "" : 值.ToString()));
            }
            return 文本.ToString();
        }
    }
}
