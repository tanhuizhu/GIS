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
                    输出("缓冲区分析完成，共生成 " + 要素数 + " 个缓冲区要素。");
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
                输出("起点已设置，请点击终点。");
                _画布.开始点选(路径点被选中);
            }
            else
            {
                计算并显示路径(建立点(x, y));
            }
        }

        private void 计算并显示路径(IPoint 终点)
        {
            IFeatureLayer 图层 = _路径图层;
            if (图层 == null || _路径起点 == null) return;

            try
            {
                道路网络 路网 = new 道路网络(图层);
                double 长度;
                IPolyline 路径 = 路网.最短路径(_路径起点, 终点, out 长度);

                if (路径 == null)
                {
                    输出("未找到连通路径（起点或终点可能不在道路网络上）。");
                    return;
                }

                ISpatialReference 空间参考 = 地图工具.获取空间参考(图层.FeatureClass);
                if (空间参考 != null) 路径.SpatialReference = 空间参考;

                string 结果名称 = "路径_" + DateTime.Now.ToString("HHmmss");
                IFeatureClass 结果要素类 = 地图工具.新建Shapefile(结果名称,
                    esriGeometryType.esriGeometryPolyline, 空间参考);
                地图工具.添加要素(结果要素类, 路径, "最短路径");

                地图工具.添加到地图(_画布.地图, 结果要素类, "最短路径结果");
                刷新图层列表();
                _画布.刷新地图();

                输出(string.Format("最短路径计算完成，长度 {0:F2} 米。", 长度));
                _状态信息.Text = string.Format("最短路径 {0:F2} 米", 长度);
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
