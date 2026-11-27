using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using ESRI.ArcGIS.Carto;
using ESRI.ArcGIS.DataSourcesFile;
using ESRI.ArcGIS.Display;
using ESRI.ArcGIS.esriSystem;
using ESRI.ArcGIS.Geodatabase;
using ESRI.ArcGIS.Geometry;
using Path = System.IO.Path;

namespace 校园GIS
{
    /// <summary>
    /// 分析功能：缓冲区、叠置、最短路径、游览路线（由搭档维护，只改本文件）。
    /// </summary>
    public partial class 主窗口
    {        // ================================================================ 空间分析

        private void 缓冲区分析(object sender, EventArgs e)
        {
            using (分析参数窗口 窗口 = new 分析参数窗口(分析类型.缓冲区, _画布.地图))
            {
                if (窗口.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    int 要素数;
                    IFeatureLayer 结果 = 空间分析.缓冲区(_画布.地图, 窗口.图层A,
                        窗口.缓冲半径, out 要素数);

                    if (结果 == null)
                    {
                        输出("缓冲区分析未生成结果（请检查图层与半径）。");
                        return;
                    }

                    刷新图层列表();
                    _画布.全图();
                    输出("缓冲区分析完成，图层名：" + 结果.Name + "，共生成 " + 要素数 + " 个缓冲区要素。");

                    // 列出前 10 个缓冲区要素的名称
                    IFeatureClass 结果要素类 = 结果.FeatureClass;
                    int 名称字段 = 结果要素类.FindField("NAME");
                    if (名称字段 >= 0)
                    {
                        IFeatureCursor 游标 = 结果要素类.Search(null, false);
                        IFeature 要素;
                        int 计数 = 0;
                        while ((要素 = 游标.NextFeature()) != null && 计数 < 10)
                        {
                            object 名称值 = 要素.get_Value(名称字段);
                            if (名称值 != null)
                                输出("  - " + 名称值.ToString());
                            计数++;
                        }
                        if (要素数 > 10) 输出("  ... 等共 " + 要素数 + " 个");
                        System.Runtime.InteropServices.Marshal.ReleaseComObject(游标);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "缓冲区分析失败：" + ex.Message);
                }
            }
        }

        private void 叠置分析(object sender, EventArgs e)
        {
            using (分析参数窗口 窗口 = new 分析参数窗口(分析类型.相交叠置, _画布.地图))
            {
                if (窗口.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    IFeatureLayer 图层A = 窗口.图层A;
                    IFeatureLayer 图层B = 窗口.图层B;
                    if (图层A == null || 图层B == null ||
                        图层A.FeatureClass.ShapeType != esriGeometryType.esriGeometryPolygon ||
                        图层B.FeatureClass.ShapeType != esriGeometryType.esriGeometryPolygon)
                    {
                        输出("叠置（相交）要求两个图层都是面图层，请重新选择。");
                        return;
                    }

                    IFeatureLayer 结果 = 空间分析.相交叠置(_画布.地图, 图层A, 图层B);
                    if (结果 == null) { 输出("叠置分析未生成结果。"); return; }

                    刷新图层列表();
                    _画布.刷新地图();
                    输出("叠置分析完成，结果图层：" + 结果.Name);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "叠置分析失败：" + ex.Message);
                }
            }
        }

        private void 最短路径分析(object sender, EventArgs e)
        {
            IFeatureLayer 图层 = 选中的要素图层();
            if (图层 == null || 图层.FeatureClass == null ||
                图层.FeatureClass.ShapeType != esriGeometryType.esriGeometryPolyline)
            {
                图层 = 找第一个线图层();
            }
            if (图层 == null)
            {
                输出("没有找到道路线图层，请先加载线数据。");
                return;
            }

            _路径图层 = 图层;
            _路径起点 = null;
            输出("最短路径将基于图层：" + 图层.Name + "，请点击起点。");
            _画布.开始点选(路径点被选中);
        }

        /// <summary>在已加载图层中找一个线图层，供最短路径使用。</summary>
        private IFeatureLayer 找第一个线图层()
        {
            IMap 地图 = _画布.地图;
            for (int i = 地图.LayerCount - 1; i >= 0; i--)
            {
                IFeatureLayer 图层 = 地图.get_Layer(i) as IFeatureLayer;
                if (图层 == null || 图层.FeatureClass == null) continue;
                if (图层.FeatureClass.ShapeType == esriGeometryType.esriGeometryPolyline) return 图层;
            }
            return null;
        }

        private void 路径点被选中(double x, double y)
        {
            if (_路径起点 == null)
            {
                _路径起点 = 建立点(x, y);
                输出(string.Format("起点已设置：X={0:F1} Y={1:F1}，请点击终点。", x, y));
                _画布.开始点选(路径点被选中);
            }
            else
            {
                输出(string.Format("终点：X={0:F1} Y={1:F1}", x, y));
                计算并显示路径(建立点(x, y));
            }
        }

        private void 计算并显示路径(IPoint 终点)
        {
            IFeatureLayer 图层 = _路径图层;
            if (图层 == null || _路径起点 == null) return;

            try
            {
                道路网络 路网 = new 道路网络(图层, _画布.地图);
                输出(string.Format("道路网络已构建：{0} 个节点，其中并入了 {1} 个辅助线图层。", 路网.节点数, 路网.辅助图层数));
                道路网络.路径结果 结果 = 路网.求路径(_路径起点, 终点);
                if (结果.路径 == null)
                {
                    输出("最短路径计算失败：" + 结果.说明);
                    return;
                }

                ISpatialReference 空间参考 = 地图工具.获取空间参考(图层.FeatureClass);
                if (空间参考 != null)
                {
                    结果.路径.SpatialReference = 空间参考;
                    结果.吸附起点.SpatialReference = 空间参考;
                    结果.吸附终点.SpatialReference = 空间参考;
                }

                // 只保留最新一次的路径结果，避免图层越堆越多
                移除旧的路径图层();

                string 时间 = DateTime.Now.ToString("HHmmss");

                IFeatureClass 线要素类 = 地图工具.新建Shapefile("路径_" + 时间,
                    esriGeometryType.esriGeometryPolyline, 空间参考);
                地图工具.添加要素(线要素类, 结果.路径,
                    string.Format("最短路径 {0:F1} 米", 结果.长度));
                IFeatureLayer 线图层 = 地图工具.添加到地图(_画布.地图, 线要素类, "最短路径结果");
                设置路径符号(线图层);

                IFeatureClass 起点要素类 = 地图工具.新建Shapefile("起点_" + 时间,
                    esriGeometryType.esriGeometryPoint, 空间参考);
                地图工具.添加要素(起点要素类, 结果.吸附起点, "起点");
                IFeatureLayer 起点图层 = 地图工具.添加到地图(_画布.地图, 起点要素类, "路径起点");
                设置点符号(起点图层, 30, 160, 70);

                IFeatureClass 终点要素类 = 地图工具.新建Shapefile("终点_" + 时间,
                    esriGeometryType.esriGeometryPoint, 空间参考);
                地图工具.添加要素(终点要素类, 结果.吸附终点, "终点");
                IFeatureLayer 终点图层 = 地图工具.添加到地图(_画布.地图, 终点要素类, "路径终点");
                设置点符号(终点图层, 220, 40, 40);

                刷新图层列表();
                _画布.刷新地图();

                输出(string.Format("最短路径计算完成，长度 {0:F2} 米。", 结果.长度));
                输出(结果.说明);
                _状态信息.Text = string.Format("最短路径 {0:F2} 米", 结果.长度);
            }
            catch (Exception ex)
            {
                输出("路径分析失败：" + ex.Message);
            }
            finally
            {
                _路径起点 = null;
                _路径图层 = null;
            }
        }

        /// <summary>每次分析前清掉上一次的路径图层，保持图层列表干净。</summary>
        private void 移除旧的路径图层()
        {
            IMap 地图 = _画布.地图;
            for (int i = 地图.LayerCount - 1; i >= 0; i--)
            {
                ILayer 一层 = 地图.get_Layer(i);
                if (一层 == null) continue;
                if (一层.Name == "最短路径结果" || 一层.Name == "路径起点" || 一层.Name == "路径终点")
                    地图.DeleteLayer(一层);
            }
        }

        /// <summary>路径用加粗红色线显示，确保在任何底色上都醒目。</summary>
        private static void 设置路径符号(IFeatureLayer 图层)
        {
            IGeoFeatureLayer 可渲染 = 图层 as IGeoFeatureLayer;
            if (可渲染 == null) return;

            ISimpleLineSymbol 符号 = new SimpleLineSymbolClass();
            符号.Style = esriSimpleLineStyle.esriSLSSolid;
            符号.Width = 3.5;
            符号.Color = 取颜色(230, 30, 30);

            ISimpleRenderer 渲染器 = new SimpleRendererClass();
            渲染器.Symbol = (ISymbol)符号;
            可渲染.Renderer = (IFeatureRenderer)渲染器;
        }

        /// <summary>起终点用带白边的圆点标记。</summary>
        private static void 设置点符号(IFeatureLayer 图层, int 红, int 绿, int 蓝)
        {
            IGeoFeatureLayer 可渲染 = 图层 as IGeoFeatureLayer;
            if (可渲染 == null) return;

            ISimpleMarkerSymbol 符号 = new SimpleMarkerSymbolClass();
            符号.Style = esriSimpleMarkerStyle.esriSMSCircle;
            符号.Size = 10;
            符号.Color = 取颜色(红, 绿, 蓝);
            符号.Outline = true;
            符号.OutlineColor = 取颜色(255, 255, 255);

            ISimpleRenderer 渲染器 = new SimpleRendererClass();
            渲染器.Symbol = (ISymbol)符号;
            可渲染.Renderer = (IFeatureRenderer)渲染器;
        }

        private static IColor 取颜色(int 红, int 绿, int 蓝)
        {
            IRgbColor 色 = new RgbColorClass();
            色.Red = 红;
            色.Green = 绿;
            色.Blue = 蓝;
            return 色;
        }

        // ================================================================ 创新功能

        private void 游览路线(object sender, EventArgs e)
        {
            输出("自定义游览路线：请在地图上依次点击 2~5 个地点（左键加点，右键结束）。");
            _状态信息.Text = "依次点击想去的地点，右键结束";

            _画布.开始多点选(5, 点集 =>
            {
                if (点集 == null || 点集.Count < 2)
                {
                    输出("至少需要选择两个地点才能规划路线。");
                    _状态信息.Text = "就绪";
                    return;
                }

                try
                {
                    IFeatureLayer 道路 = 找第一个线图层();
                    int 景点数;
                    double 总长度;
                    IFeatureLayer 结果 = 游览路线生成器.生成自定义(_画布.地图, 道路,
                        点集, out 景点数, out 总长度);

                    if (结果 == null)
                    {
                        输出("游览路线生成失败：缺少道路数据。");
                        _状态信息.Text = "就绪";
                        return;
                    }

                    刷新图层列表();
                    _画布.全图();
                    输出(string.Format("已按您选择的 {0} 个地点规划游览路线，总长约 {1:F0} 米。",
                        景点数, 总长度));
                    _状态信息.Text = string.Format("自定义游览路线：{0} 个地点，{1:F0} 米",
                        景点数, 总长度);
                }
                catch (Exception ex)
                {
                    输出("游览路线生成失败：" + ex.Message);
                }
            });
        }
    }
}
