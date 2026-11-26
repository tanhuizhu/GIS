using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using ESRI.ArcGIS.Carto;
using ESRI.ArcGIS.esriSystem;
using ESRI.ArcGIS.Geodatabase;
using ESRI.ArcGIS.Geometry;
using ESRI.ArcGIS.Output;
using Point = System.Drawing.Point;

namespace 校园GIS
{
    /// <summary>画布上当前处于哪种操作状态。</summary>
    public enum 画布工具
    {
        无,
        放大,
        缩小,
        平移
    }

    /// <summary>
    /// 地图显示控件。用 ArcObjects 的 IActiveView.Output 把地图渲染成位图后贴到控件上，
    /// 并负责缩放、平移、量算/查询用的折线绘制和单点拾取。
    /// </summary>
    public class 地图画布 : Control
    {
        private IMap _地图;
        private IActiveView _视图;
        private IEnvelope _当前范围;

        private 画布工具 _工具 = 画布工具.无;

        private bool _正在拖拽;
        private Point _拖拽起点;
        private Point _拖拽终点;

        private bool _正在绘制;
        private bool _闭合草图;
        private readonly List<Point> _草稿点 = new List<Point>();
        private Point _鼠标位置;
        private Action<IGeometry> _绘制完成回调;

        private Action<double, double> _点选回调;

        private List<IPoint> _多点选缓存;
        private int _最多选点数;
        private Action<List<IPoint>> _多点选完成;

        private Bitmap _地图位图;
        private IEnvelope _位图范围;
        private int _位图宽度;
        private int _位图高度;
        private string _渲染错误;
        private bool _位图失效 = true;

        /// <summary>鼠标移动时通知外部当前地图坐标，用于状态栏显示。</summary>
        public event Action<double, double> 地图坐标变化;

        public 地图画布()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            TabStop = true;
        }

        public IMap 地图 { get { return _地图; } }

        public IActiveView 活动视图 { get { return _视图; } }

        public IEnvelope 当前范围 { get { return _当前范围; } }

        public bool 是否正在绘制 { get { return _正在绘制; } }

        public void 设置地图(IMap 地图)
        {
            _地图 = 地图;
            _视图 = (IActiveView)地图;
            _当前范围 = null;
            _地图位图 = null;
            _位图范围 = null;
            _位图失效 = true;
            Invalidate();
        }

        public void 设置工具(画布工具 工具)
        {
            取消绘制();
            _工具 = 工具;
            Cursor = 工具 == 画布工具.无 ? Cursors.Default : Cursors.Cross;
        }

        // ---------------------------------------------------------------- 地图范围

        /// <summary>缩放到所有图层范围的并集。</summary>
        public void 全图()
        {
            if (_地图 == null || _地图.LayerCount == 0) return;

            IEnvelope 合并范围 = null;
            for (int i = 0; i < _地图.LayerCount; i++)
            {
                IFeatureLayer 图层 = _地图.get_Layer(i) as IFeatureLayer;
                if (图层 == null || 图层.FeatureClass == null) continue;

                // 分析结果图层（缓冲区、路线、停车位等）不参与全图范围，
                // 否则它们会把范围越撑越大，导致底图缩得很小、点不中要素
                if (是分析结果图层(图层.Name)) continue;

                IGeoDataset 数据集 = 图层.FeatureClass as IGeoDataset;
                if (数据集 == null) continue;

                IEnvelope 图层范围 = 数据集.Extent;
                if (图层范围 == null || 图层范围.IsEmpty) continue;

                if (合并范围 == null) 合并范围 = (IEnvelope)((IClone)图层范围).Clone();
                else 合并范围.Union(图层范围);
            }

            if (合并范围 == null || 合并范围.IsEmpty) return;
            合并范围.Expand(1.02, 1.02, true);
            设置范围(合并范围);
        }

        private static bool 是分析结果图层(string 名称)
        {
            if (string.IsNullOrEmpty(名称)) return false;

            string[] 前缀 = { "缓冲区", "叠置", "最短路径", "游览", "自定义", "非机动车", "景点" };
            for (int i = 0; i < 前缀.Length; i++)
            {
                if (名称.StartsWith(前缀[i])) return true;
            }
            return false;
        }

        public void 设置范围(IEnvelope 范围)
        {
            if (范围 == null || 范围.IsEmpty) return;
            if (范围.Width <= 0 || 范围.Height <= 0) return;

            _当前范围 = (IEnvelope)((IClone)范围).Clone();
            同步视图范围();
            _位图失效 = true;
            Invalidate();
        }

        /// <summary>图层可见性、选择集、要素、符号等发生变化后，强制重新渲染地图。</summary>
        public void 刷新地图()
        {
            _位图失效 = true;
            Invalidate();
        }

        /// <summary>把控件范围写进 ArcObjects 视图。以控件范围为准，不回读，避免视图改写范围。</summary>
        private void 同步视图范围()
        {
            if (_视图 == null || _当前范围 == null) return;

            try { _视图.Extent = _当前范围; }
            catch { }
        }

        /// <summary>把控件像素坐标换算成地图坐标。</summary>
        public IPoint 转地图坐标(int 像素X, int 像素Y)
        {
            // 与渲染使用的是同一个范围对象，保证画面和坐标一一对应
            IEnvelope 范围 = _当前范围;
            if (范围 == null) return null;

            double 地图X = 范围.XMin + (double)像素X / Math.Max(1, Width) * 范围.Width;
            double 地图Y = 范围.YMax - (double)像素Y / Math.Max(1, Height) * 范围.Height;
            IPoint 点 = new PointClass();
            点.PutCoords(地图X, 地图Y);
            return 点;
        }

        /// <summary>把控件尺寸、位图尺寸和当前范围拼成一行文字，用于排查坐标换算问题。</summary>
        public string 诊断信息()
        {
            string 范围 = _当前范围 == null
                ? "无"
                : string.Format("X {0:F2} ~ {1:F2}, Y {2:F2} ~ {3:F2}",
                    _当前范围.XMin, _当前范围.XMax, _当前范围.YMin, _当前范围.YMax);

            string 视图范围 = "无";
            try
            {
                if (_视图 != null && _视图.Extent != null && !_视图.Extent.IsEmpty)
                    视图范围 = string.Format("X {0:F2} ~ {1:F2}, Y {2:F2} ~ {3:F2}",
                        _视图.Extent.XMin, _视图.Extent.XMax,
                        _视图.Extent.YMin, _视图.Extent.YMax);
            }
            catch { }

            string 位图 = _地图位图 == null ? "无" : _地图位图.Width + "x" + _地图位图.Height;
            return "控件 " + Width + "x" + Height + "，位图 " + 位图 +
                   "，绘制范围 " + 范围 + "，视图范围 " + 视图范围;
        }

        // ---------------------------------------------------------------- 绘制

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 地图整块重绘，忽略默认背景绘制以减少闪烁
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_视图 == null || _当前范围 == null || _当前范围.IsEmpty) return;
            if (Width <= 0 || Height <= 0) return;

            确保位图();

            if (_地图位图 != null)
            {
                e.Graphics.DrawImageUnscaled(_地图位图, 0, 0);
            }
            else if (!string.IsNullOrEmpty(_渲染错误))
            {
                using (Font 字体 = new Font("微软雅黑", 10F))
                {
                    e.Graphics.DrawString("地图渲染失败：" + _渲染错误, 字体,
                        Brushes.Firebrick, 12, 12);
                }
            }

            绘制草图(e.Graphics);

            if (_多点选缓存 != null && _多点选缓存.Count > 0)
            {
                string 提示 = "已选 " + _多点选缓存.Count + " 个地点，右键结束";
                using (Font 字体 = new Font("微软雅黑", 10F, FontStyle.Bold))
                {
                    e.Graphics.DrawString(提示, 字体, Brushes.Red, 12, 12);
                }
            }
        }

        /// <summary>范围或控件尺寸变化时重新渲染一张位图，后续绘制直接贴图。</summary>
        private void 确保位图()
        {
            bool 范围未变 = _位图范围 != null &&
                            Math.Abs(_位图范围.XMin - _当前范围.XMin) < 0.001 &&
                            Math.Abs(_位图范围.YMin - _当前范围.YMin) < 0.001 &&
                            Math.Abs(_位图范围.XMax - _当前范围.XMax) < 0.001 &&
                            Math.Abs(_位图范围.YMax - _当前范围.YMax) < 0.001;

            if (!_位图失效 && _地图位图 != null && 范围未变 && _位图宽度 == Width && _位图高度 == Height) return;

            string 临时文件 = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "校园GIS_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                IExport 导出器 = new ExportPNGClass();
                导出器.ExportFileName = 临时文件;
                导出器.Resolution = 96;

                IEnvelope 像素范围 = new EnvelopeClass();
                像素范围.PutCoords(0, 0, Width, Height);
                导出器.PixelBounds = 像素范围;

                tagRECT 矩形 = new tagRECT();
                矩形.left = 0;
                矩形.top = 0;
                矩形.right = Width;
                矩形.bottom = Height;

                int 设备句柄 = 导出器.StartExporting();
                _视图.Output(设备句柄, 96, ref 矩形, _当前范围, null);
                导出器.FinishExporting();
                导出器.Cleanup();

                using (Bitmap 读入 = new Bitmap(临时文件))
                {
                    Bitmap 副本 = new Bitmap(Width, Height,
                        System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    using (Graphics g = Graphics.FromImage(副本))
                    {
                        g.Clear(Color.White);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(读入, 0, 0, Width, Height);
                    }
                    if (_地图位图 != null) _地图位图.Dispose();
                    _地图位图 = 副本;
                }

                if (_位图范围 == null) _位图范围 = new EnvelopeClass();
                _位图范围.PutCoords(_当前范围.XMin, _当前范围.YMin, _当前范围.XMax, _当前范围.YMax);
                _位图宽度 = Width;
                _位图高度 = Height;
                _渲染错误 = null;
                _位图失效 = false;
            }
            catch (Exception ex)
            {
                _渲染错误 = ex.Message;
            }
            finally
            {
                try { if (System.IO.File.Exists(临时文件)) System.IO.File.Delete(临时文件); } catch { }
            }
        }

        private void 绘制草图(Graphics 画板)
        {
            if (!_正在绘制 || _草稿点.Count == 0) return;

            using (Pen 画笔 = new Pen(Color.Red, 1.6f) { DashStyle = DashStyle.Dash })
            {
                if (_草稿点.Count > 1)
                    画板.DrawLines(画笔, _草稿点.ToArray());

                画板.DrawLine(画笔, _草稿点[_草稿点.Count - 1], _鼠标位置);

                if (_闭合草图 && _草稿点.Count > 1)
                    画板.DrawLine(画笔, _鼠标位置, _草稿点[0]);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // 缩放窗口时保持地图范围不变，只重渲染，避免范围被改写导致点击位置偏移
            同步视图范围();
            _位图失效 = true;
            Invalidate();
        }

        // ---------------------------------------------------------------- 鼠标键盘

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_正在拖拽)
            {
                绘制拖拽预览();
                _拖拽终点 = e.Location;
                绘制拖拽预览();
            }
            else if (_正在绘制)
            {
                _鼠标位置 = e.Location;
                Invalidate();
            }

            if (地图坐标变化 != null && _当前范围 != null)
            {
                double 地图X = _当前范围.XMin + (double)e.X / Math.Max(1, Width) * _当前范围.Width;
                double 地图Y = _当前范围.YMax - (double)e.Y / Math.Max(1, Height) * _当前范围.Height;
                地图坐标变化(地图X, 地图Y);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();

            // 多点选（自定义游览路线）：左键加点，右键结束
            if (_多点选缓存 != null)
            {
                if (e.Button == MouseButtons.Right)
                {
                    完成多点选();
                    return;
                }
                if (e.Button != MouseButtons.Left) return;

                IPoint 点 = 转地图坐标(e.X, e.Y);
                if (点 != null)
                {
                    _多点选缓存.Add(点);
                    Invalidate();
                    if (_多点选缓存.Count >= _最多选点数) 完成多点选();
                }
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            // 优先处理“点选一个位置”的请求
            if (_点选回调 != null)
            {
                Action<double, double> 回调 = _点选回调;
                _点选回调 = null;
                Cursor = _工具 == 画布工具.无 ? Cursors.Default : Cursors.Cross;
                IPoint 点 = 转地图坐标(e.X, e.Y);
                if (点 != null) 回调(点.X, 点.Y);
                return;
            }

            // 正在画线/画面：记录一个节点
            if (_正在绘制)
            {
                _草稿点.Add(new Point(e.X, e.Y));
                _鼠标位置 = e.Location;
                Invalidate();
                return;
            }

            if (_工具 == 画布工具.放大 || _工具 == 画布工具.缩小 || _工具 == 画布工具.平移)
            {
                _正在拖拽 = true;
                _拖拽起点 = _拖拽终点 = e.Location;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_正在拖拽) return;

            绘制拖拽预览();
            _正在拖拽 = false;

            if (_当前范围 == null) return;

            int 位移X = e.X - _拖拽起点.X;
            int 位移Y = e.Y - _拖拽起点.Y;

            // 平移
            if (_工具 == 画布工具.平移)
            {
                if (Math.Abs(位移X) < 2 && Math.Abs(位移Y) < 2) return;
                IEnvelope 移动后 = (IEnvelope)((IClone)_当前范围).Clone();
                移动后.Offset(-(double)位移X / Width * _当前范围.Width,
                    (double)位移Y / Height * _当前范围.Height);
                设置范围(移动后);
                return;
            }

            // 拖框太小视为误操作
            if (Math.Abs(位移X) < 6 || Math.Abs(位移Y) < 6) return;

            int 左 = Math.Min(_拖拽起点.X, e.X);
            int 右 = Math.Max(_拖拽起点.X, e.X);
            int 上 = Math.Min(_拖拽起点.Y, e.Y);
            int 下 = Math.Max(_拖拽起点.Y, e.Y);

            IEnvelope 新范围 = new EnvelopeClass();
            IPoint 点1 = 转地图坐标(左, 下);
            IPoint 点2 = 转地图坐标(右, 上);

            if (_工具 == 画布工具.放大)
            {
                新范围.PutCoords(点1.X, 点1.Y, 点2.X, 点2.Y);
                设置范围(新范围);
            }
            else
            {
                // 缩小：拖出的矩形越小，缩小倍数越大
                double 倍数 = Math.Max((double)Width / (右 - 左), (double)Height / (下 - 上));
                double 中心X = 点1.X + (点2.X - 点1.X) / 2.0;
                double 中心Y = 点1.Y + (点2.Y - 点1.Y) / 2.0;
                double 宽 = _当前范围.Width * 倍数;
                double 高 = _当前范围.Height * 倍数;
                新范围.PutCoords(中心X - 宽 / 2, 中心Y - 高 / 2, 中心X + 宽 / 2, 中心Y + 高 / 2);
                设置范围(新范围);
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (!_正在绘制 || e.Button != MouseButtons.Left) return;

            // 双击会额外产生一个重复节点，去掉
            if (_草稿点.Count >= 2)
            {
                Point 最后 = _草稿点[_草稿点.Count - 1];
                Point 前一个 = _草稿点[_草稿点.Count - 2];
                if (Math.Abs(最后.X - 前一个.X) <= 2 && Math.Abs(最后.Y - 前一个.Y) <= 2)
                    _草稿点.RemoveAt(_草稿点.Count - 1);
            }

            结束绘制();
        }

        private void 绘制拖拽预览()
        {
            using (Graphics 画板 = CreateGraphics())
            {
                if (_工具 == 画布工具.平移)
                {
                    ControlPaint.DrawReversibleLine(PointToScreen(_拖拽起点),
                        PointToScreen(_拖拽终点), Color.Black);
                }
                else
                {
                    int 左 = Math.Min(_拖拽起点.X, _拖拽终点.X);
                    int 上 = Math.Min(_拖拽起点.Y, _拖拽终点.Y);
                    int 宽 = Math.Abs(_拖拽起点.X - _拖拽终点.X);
                    int 高 = Math.Abs(_拖拽起点.Y - _拖拽终点.Y);
                    if (宽 > 0 && 高 > 0)
                    {
                        ControlPaint.DrawReversibleFrame(
                            RectangleToScreen(new Rectangle(左, 上, 宽, 高)),
                            Color.Black, FrameStyle.Dashed);
                    }
                }
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Escape) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) 取消绘制();
            base.OnKeyDown(e);
        }

        // ---------------------------------------------------------------- 对外交互

        /// <summary>开始画折线（闭合=false）或面（闭合=true），依次单击节点，双击结束。</summary>
        public void 开始绘制(bool 闭合, Action<IGeometry> 完成回调)
        {
            取消绘制();
            _工具 = 画布工具.无;
            _正在绘制 = true;
            _闭合草图 = 闭合;
            _绘制完成回调 = 完成回调;
            _草稿点.Clear();
            Cursor = Cursors.Cross;
            Focus();
        }

        /// <summary>等待用户在地图上单击一个位置。</summary>
        public void 开始点选(Action<double, double> 回调)
        {
            取消绘制();
            _工具 = 画布工具.无;
            _点选回调 = 回调;
            Cursor = Cursors.Cross;
            Focus();
        }

        public void 取消绘制()
        {
            _正在绘制 = false;
            _绘制完成回调 = null;
            _点选回调 = null;
            _多点选缓存 = null;
            _多点选完成 = null;
            _草稿点.Clear();
            Cursor = _工具 == 画布工具.无 ? Cursors.Default : Cursors.Cross;
            Invalidate();
        }

        /// <summary>
        /// 连续点选多个地点（用于自定义游览路线），最多、个，右键结束，点满自动结束。
        /// </summary>
        public void 开始多点选(int 最多点数, Action<List<IPoint>> 完成)
        {
            取消绘制();
            _工具 = 画布工具.无;
            _正在绘制 = false;
            _多点选缓存 = new List<IPoint>();
            _最多选点数 = Math.Max(2, 最多点数);
            _多点选完成 = 完成;
            Cursor = Cursors.Cross;
            Focus();
            Invalidate();
        }

        public int 已选点数
        {
            get { return _多点选缓存 == null ? 0 : _多点选缓存.Count; }
        }

        private void 完成多点选()
        {
            List<IPoint> 点集 = _多点选缓存;
            Action<List<IPoint>> 回调 = _多点选完成;

            _多点选缓存 = null;
            _多点选完成 = null;
            Cursor = Cursors.Default;
            Invalidate();

            if (回调 != null) 回调(点集 ?? new List<IPoint>());
        }

        private void 结束绘制()
        {
            IPointCollection 点集 = _闭合草图
                ? (IPointCollection)new PolygonClass()
                : (IPointCollection)new PolylineClass();

            foreach (Point 屏幕点 in _草稿点)
            {
                IPoint 地图点 = 转地图坐标(屏幕点.X, 屏幕点.Y);
                if (地图点 != null) 点集.AddPoint(地图点);
            }

            IGeometry 图形 = (IGeometry)点集;
            Action<IGeometry> 回调 = _绘制完成回调;
            _正在绘制 = false;
            _绘制完成回调 = null;
            _草稿点.Clear();
            Invalidate();

            if (回调 != null) 回调(图形);
        }
    }
}
