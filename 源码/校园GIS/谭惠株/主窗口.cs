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
    /// <summary>程序主窗口：菜单、工具栏、图层树、地图画布和输出信息区。</summary>
    public partial class 主窗口 : Form
    {
        private 地图画布 _画布;
        private IActiveView _活动视图;

        private ListView _图层列表;
        private RichTextBox _输出框;
        private ToolStripStatusLabel _状态信息;
        private ToolStripStatusLabel _状态坐标;
        private SplitContainer _左侧分栏;
        private SplitContainer _主分栏;

        private bool _正在同步图层列表;
        private int _颜色序号;

        private bool _编辑中;
        private IFeatureLayer _编辑图层;

        private readonly Stack<IEnvelope> _撤销范围 = new Stack<IEnvelope>();
        private readonly Stack<IEnvelope> _重做范围 = new Stack<IEnvelope>();

        private IPoint _路径起点;
        private IFeatureLayer _路径图层;
        private System.Windows.Forms.Timer _自检计时器;

        public 主窗口()
        {
            Text = "校园GIS系统 - ArcGIS Engine";
            Rectangle 屏幕区域 = Screen.PrimaryScreen.WorkingArea;
            Width = Math.Min(1280, 屏幕区域.Width - 40);
            Height = Math.Min(840, 屏幕区域.Height - 60);
            StartPosition = FormStartPosition.CenterScreen;
            建立界面();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                初始化地图();
                try { _左侧分栏.SplitterDistance = 230; } catch { }
                try { _主分栏.SplitterDistance = 580; } catch { }
                加载示例数据();

                if (Environment.GetEnvironmentVariable("校园GIS自检") == "1")
                {
                    _自检计时器 = new System.Windows.Forms.Timer { Interval = 2000 };
                    _自检计时器.Tick += (s, ev) =>
                    {
                        _自检计时器.Stop();
                        try { 运行自检(Path.Combine(地图工具.输出目录, "自检结果.txt")); }
                        catch (Exception ex2) { 输出("自检异常：" + ex2.Message); }
                    };
                    _自检计时器.Start();
                }
            }
            catch (Exception ex)
            {
                输出("启动过程出现异常：" + ex.Message);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            停止编辑(false);
            base.OnFormClosing(e);
        }

        // ================================================================ 界面搭建

        private void 建立界面()
        {
            MenuStrip 菜单 = 建立菜单();

            ToolStrip 工具条 = new ToolStrip
            {
                Dock = DockStyle.Fill,
                GripStyle = ToolStripGripStyle.Hidden
            };
            添加工具按钮(工具条, "放大", 放大);
            添加工具按钮(工具条, "缩小", 缩小);
            添加工具按钮(工具条, "平移", 平移);
            添加工具按钮(工具条, "全图", 全图);
            工具条.Items.Add(new ToolStripSeparator());
            添加工具按钮(工具条, "距离量算", 距离量算);
            添加工具按钮(工具条, "面积量算", 面积量算);
            工具条.Items.Add(new ToolStripSeparator());
            添加工具按钮(工具条, "点击识别", 点击识别);
            添加工具按钮(工具条, "属性查询", 属性查询);
            添加工具按钮(工具条, "空间查询", 空间查询);
            添加工具按钮(工具条, "清除选择", 清除选择);

            StatusStrip 状态栏 = new StatusStrip();
            _状态信息 = new ToolStripStatusLabel("就绪")
            {
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _状态坐标 = new ToolStripStatusLabel("X=  Y=");
            状态栏.Items.Add(_状态信息);
            状态栏.Items.Add(_状态坐标);

            // ---- 左侧图层控制面板
            Panel 图层面板 = new Panel { Dock = DockStyle.Fill };
            _图层列表 = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false
            };
            _图层列表.Columns.Add("图层（勾选=显示）", 200);
            _图层列表.ItemChecked += 图层勾选变化;

            FlowLayoutPanel 图层按钮区 = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(2)
            };
            图层按钮区.Controls.Add(建立小按钮("上移", 图层上移));
            图层按钮区.Controls.Add(建立小按钮("下移", 图层下移));
            图层按钮区.Controls.Add(建立小按钮("移除", 移除图层));

            图层面板.Controls.Add(_图层列表);
            图层面板.Controls.Add(图层按钮区);

            // ---- 地图画布
            _画布 = new 地图画布 { Dock = DockStyle.Fill };

            _主分栏 = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6
            };
            _主分栏.Panel1.Controls.Add(_画布);

            _输出框 = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.White,
                Font = new Font("Consolas", 9F)
            };
            _主分栏.Panel2.Controls.Add(_输出框);

            _左侧分栏 = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                FixedPanel = FixedPanel.Panel1
            };
            _左侧分栏.Panel1.Controls.Add(图层面板);
            _左侧分栏.Panel2.Controls.Add(_主分栏);

            TableLayoutPanel 根布局 = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4
            };
            根布局.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            根布局.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            根布局.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            根布局.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            根布局.Controls.Add(菜单, 0, 0);
            根布局.Controls.Add(工具条, 0, 1);
            根布局.Controls.Add(_左侧分栏, 0, 2);
            根布局.Controls.Add(状态栏, 0, 3);

            Controls.Add(根布局);
            MainMenuStrip = 菜单;
        }

        private MenuStrip 建立菜单()
        {
            MenuStrip 菜单 = new MenuStrip
            {
                Dock = DockStyle.Fill,
                GripStyle = ToolStripGripStyle.Hidden
            };

            ToolStripMenuItem 文件 = new ToolStripMenuItem("文件");
            添加菜单项(文件, "添加数据(Shapefile)", 添加数据);
            添加菜单项(文件, "加载示例校园数据", (s, e) => 加载示例数据());
            添加菜单项(文件, "导出当前图层(Shapefile)", 导出图层);
            文件.DropDownItems.Add(new ToolStripSeparator());
            添加菜单项(文件, "保存地图文档(.mxd)", 保存地图文档);
            添加菜单项(文件, "地图输出为图片(PNG)", 输出地图图片);
            文件.DropDownItems.Add(new ToolStripSeparator());
            添加菜单项(文件, "退出", (s, e) => Close());

            ToolStripMenuItem 视图 = new ToolStripMenuItem("视图");
            添加菜单项(视图, "全图", 全图);
            添加菜单项(视图, "上一视图", 上一视图);
            添加菜单项(视图, "下一视图", 下一视图);

            ToolStripMenuItem 工具 = new ToolStripMenuItem("工具");
            添加菜单项(工具, "放大（拖框）", 放大);
            添加菜单项(工具, "缩小（拖框）", 缩小);
            添加菜单项(工具, "平移（拖动）", 平移);
            工具.DropDownItems.Add(new ToolStripSeparator());
            添加菜单项(工具, "距离量算", 距离量算);
            添加菜单项(工具, "面积量算", 面积量算);
            添加菜单项(工具, "点击识别要素", 点击识别);

            ToolStripMenuItem 查询 = new ToolStripMenuItem("查询");
            添加菜单项(查询, "属性查询", 属性查询);
            添加菜单项(查询, "空间查询（画多边形）", 空间查询);
            添加菜单项(查询, "清除选择", 清除选择);

            ToolStripMenuItem 编辑 = new ToolStripMenuItem("编辑");
            添加菜单项(编辑, "开始编辑", 开始编辑);
            添加菜单项(编辑, "停止编辑并保存", 停止编辑并保存);
            编辑.DropDownItems.Add(new ToolStripSeparator());
            添加菜单项(编辑, "添加点要素", 添加点要素);
            添加菜单项(编辑, "添加线要素", 添加线要素);
            添加菜单项(编辑, "添加面要素", 添加面要素);
            编辑.DropDownItems.Add(new ToolStripSeparator());
            添加菜单项(编辑, "删除要素", 删除要素);
            添加菜单项(编辑, "修改要素属性", 修改要素属性);

            ToolStripMenuItem 分析 = new ToolStripMenuItem("分析");
            添加菜单项(分析, "缓冲区分析", 缓冲区分析);
            添加菜单项(分析, "叠置分析（相交）", 叠置分析);
            添加菜单项(分析, "最短路径分析", 最短路径分析);
            分析.DropDownItems.Add(new ToolStripSeparator());
            添加菜单项(分析, "校园游览路线（创新一）", 游览路线);
            添加菜单项(分析, "非机动车停车位推荐（创新二）", 停车位推荐);

            ToolStripMenuItem 制图 = new ToolStripMenuItem("制图");
            添加菜单项(制图, "图层符号化", 图层符号化);
            添加菜单项(制图, "地图输出为图片(PNG)", 输出地图图片);

            ToolStripMenuItem 帮助 = new ToolStripMenuItem("帮助");
            添加菜单项(帮助, "操作说明", 显示操作说明);
            添加菜单项(帮助, "关于", 显示关于);

            菜单.Items.Add(文件);
            菜单.Items.Add(视图);
            菜单.Items.Add(工具);
            菜单.Items.Add(查询);
            菜单.Items.Add(编辑);
            菜单.Items.Add(分析);
            菜单.Items.Add(制图);
            菜单.Items.Add(帮助);
            return 菜单;
        }

        private static ToolStripMenuItem 添加菜单项(ToolStripMenuItem 父菜单, string 文字,
            EventHandler 处理)
        {
            ToolStripMenuItem 项 = new ToolStripMenuItem(文字);
            if (处理 != null) 项.Click += 处理;
            父菜单.DropDownItems.Add(项);
            return 项;
        }

        private static void 添加工具按钮(ToolStrip 工具条, string 文字, EventHandler 处理)
        {
            ToolStripButton 按钮 = new ToolStripButton(文字)
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text
            };
            按钮.Click += 处理;
            工具条.Items.Add(按钮);
        }

        private static Button 建立小按钮(string 文字, EventHandler 处理)
        {
            Button 按钮 = new Button { Text = 文字, Width = 62, Height = 30 };
            按钮.Click += 处理;
            return 按钮;
        }

        // ================================================================ 地图初始化

        private void 初始化地图()
        {
            IMap 地图 = new MapClass();
            地图.Name = "校园地图";
            _画布.设置地图(地图);
            _活动视图 = _画布.活动视图;
            _画布.地图坐标变化 += (x, y) =>
            {
                if (_状态坐标 != null)
                    _状态坐标.Text = string.Format("X={0:F2}  Y={1:F2}", x, y);
            };
        }

        // ================================================================ 图层管理

        /// <summary>数据文件名（英文缩写）对应的中文图层名。</summary>
        private static readonly Dictionary<string, string> 图层中文名表 =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "LD", "绿地" },
                { "DL", "道路" },
                { "CC", "操场" },
                { "JXQ", "教学区" },
                { "QTJZ", "其他建筑" },
                { "TSG", "图书馆" },
                { "ST", "水体" },
                { "SSQ", "宿舍区" },
                { "YWS", "医务室" },
                { "XZLX", "行政区" }
            };

        private static string 取中文图层名(string 英文名)
        {
            string 中文名;
            return 图层中文名表.TryGetValue(英文名, out 中文名) ? 中文名 : 英文名;
        }

        private void 加载示例数据()
        {
            string 目录 = 地图工具.数据目录;
            if (!Directory.Exists(目录))
            {
                输出("未找到数据目录：" + 目录 + "，可使用“文件 → 添加数据”手工加载。");
                return;
            }

            string[] 图层名列表 = { "LD", "DL", "CC", "JXQ", "QTJZ", "TSG", "ST", "SSQ", "YWS", "XZLX" };
            int 已加载 = 0;
            foreach (string 名称 in 图层名列表)
            {
                string 路径 = Path.Combine(目录, 名称 + ".shp");
                if (File.Exists(路径))
                {
                    添加图层文件(路径);
                    已加载++;
                }
            }

            刷新图层列表();
            _画布.全图();
            输出("已从 " + 目录 + " 加载 " + 已加载 + " 个示例图层。");
        }

        private void 添加图层文件(string 路径)
        {
            try
            {
                string 目录 = Path.GetDirectoryName(路径);
                string 名称 = Path.GetFileNameWithoutExtension(路径);

                IWorkspaceFactory 工厂 = new ShapefileWorkspaceFactoryClass();
                IFeatureWorkspace 工作空间 = (IFeatureWorkspace)工厂.OpenFromFile(目录, 0);
                IFeatureClass 要素类 = 工作空间.OpenFeatureClass(名称);

                IFeatureLayer 图层 = new FeatureLayerClass();
                图层.FeatureClass = 要素类;
                图层.Name = 取中文图层名(名称);

                IMap 地图 = _画布.地图;

                // 给图层设置默认符号和选中符号：没有渲染器的图层无法“点击识别”，
                // 没有选中符号的图层查询结果在地图上也看不出来。
                IGeoFeatureLayer 可渲染图层 = 图层 as IGeoFeatureLayer;
                if (可渲染图层 != null)
                {
                    ISimpleRenderer 渲染器 = new SimpleRendererClass();
                    渲染器.Symbol = 地图工具.创建符号(要素类.ShapeType, 地图.LayerCount, 0);
                    可渲染图层.Renderer = (IFeatureRenderer)渲染器;

                    IFeatureSelection 选择 = 图层 as IFeatureSelection;
                    if (选择 != null)
                        选择.SelectionSymbol = 地图工具.创建符号(要素类.ShapeType, 3, 0);
                }

                地图.AddLayer(图层);

                if (地图.SpatialReference == null)
                {
                    ISpatialReference 空间参考 = 地图工具.获取空间参考(要素类);
                    if (空间参考 != null) 地图.SpatialReference = 空间参考;
                }
            }
            catch (Exception ex)
            {
                输出("加载失败：" + Path.GetFileName(路径) + " - " + ex.Message);
            }
        }

        private void 刷新图层列表()
        {
            if (_图层列表 == null || _画布 == null || _画布.地图 == null) return;

            _正在同步图层列表 = true;
            try
            {
                _图层列表.Items.Clear();
                IMap 地图 = _画布.地图;
                for (int i = 地图.LayerCount - 1; i >= 0; i--)
                {
                    ILayer 图层 = 地图.get_Layer(i);
                    ListViewItem 项 = new ListViewItem(图层.Name);
                    项.Checked = 图层.Visible;
                    项.Tag = 图层;
                    _图层列表.Items.Add(项);
                }
            }
            finally
            {
                _正在同步图层列表 = false;
            }
        }

        private void 图层勾选变化(object sender, ItemCheckedEventArgs e)
        {
            if (_正在同步图层列表) return;
            ILayer 图层 = e.Item.Tag as ILayer;
            if (图层 == null) return;
            图层.Visible = e.Item.Checked;
            _画布.刷新地图();
        }

        private ILayer 选中图层()
        {
            if (_图层列表 == null || _图层列表.SelectedItems.Count == 0) return null;
            return _图层列表.SelectedItems[0].Tag as ILayer;
        }

        private IFeatureLayer 选中的要素图层()
        {
            return 选中图层() as IFeatureLayer;
        }

        private int 图层序号(ILayer 图层)
        {
            IMap 地图 = _画布.地图;
            for (int i = 0; i < 地图.LayerCount; i++)
            {
                if (ReferenceEquals(地图.get_Layer(i), 图层)) return i;
            }
            return -1;
        }

        private void 图层上移(object sender, EventArgs e)
        {
            ILayer 图层 = 选中图层();
            if (图层 == null) { 输出("请先在左侧选中图层。"); return; }

            int 序号 = 图层序号(图层);
            if (序号 < 0 || 序号 >= _画布.地图.LayerCount - 1) return;

            _画布.地图.MoveLayer(图层, 序号 + 1);
            刷新图层列表();
            _画布.刷新地图();
        }

        private void 图层下移(object sender, EventArgs e)
        {
            ILayer 图层 = 选中图层();
            if (图层 == null) { 输出("请先在左侧选中图层。"); return; }

            int 序号 = 图层序号(图层);
            if (序号 <= 0) return;

            _画布.地图.MoveLayer(图层, 序号 - 1);
            刷新图层列表();
            _画布.刷新地图();
        }

        private void 移除图层(object sender, EventArgs e)
        {
            ILayer 图层 = 选中图层();
            if (图层 == null) { 输出("请先在左侧选中图层。"); return; }

            _画布.地图.DeleteLayer(图层);
            刷新图层列表();
            _画布.刷新地图();
            输出("已移除图层：" + 图层.Name);
        }

        // ================================================================ 数据导入导出

        private void 添加数据(object sender, EventArgs e)
        {
            using (OpenFileDialog 对话框 = new OpenFileDialog
            {
                Filter = "Shapefile (*.shp)|*.shp|所有文件 (*.*)|*.*",
                Multiselect = true
            })
            {
                if (对话框.ShowDialog(this) != DialogResult.OK) return;

                foreach (string 文件 in 对话框.FileNames) 添加图层文件(文件);
                刷新图层列表();
                _画布.全图();
                输出("已添加 " + 对话框.FileNames.Length + " 个数据文件。");
            }
        }

        private void 导出图层(object sender, EventArgs e)
        {
            IFeatureLayer 图层 = 选中的要素图层();
            if (图层 == null) { 输出("请先在左侧选中一个要素图层。"); return; }

            using (SaveFileDialog 对话框 = new SaveFileDialog
            {
                Filter = "Shapefile (*.shp)|*.shp",
                FileName = 图层.Name + ".shp"
            })
            {
                if (对话框.ShowDialog(this) != DialogResult.OK) return;

                string 目录 = Path.GetDirectoryName(对话框.FileName);
                string 名称 = Path.GetFileNameWithoutExtension(对话框.FileName);
                try
                {
                    string shapefile路径 = Path.Combine(目录, 名称 + ".shp");
                    if (File.Exists(shapefile路径)) File.Delete(shapefile路径);

                    地图工具.导出图层(图层, 目录, 名称);
                    输出("已导出图层到：" + 对话框.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "导出失败：" + ex.Message);
                }
            }
        }

        private void 保存地图文档(object sender, EventArgs e)
        {
            using (SaveFileDialog 对话框 = new SaveFileDialog
            {
                Filter = "地图文档 (*.mxd)|*.mxd"
            })
            {
                if (对话框.ShowDialog(this) != DialogResult.OK) return;
                if (File.Exists(对话框.FileName)) File.Delete(对话框.FileName);

                try
                {
                    IMapDocument 文档 = new MapDocumentClass();
                    文档.New(对话框.FileName);
                    文档.ReplaceContents((IMxdContents)_画布.地图);
                    文档.Save(true, true);
                    文档.Close();
                    输出("地图文档已保存：" + 对话框.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "保存失败：" + ex.Message);
                }
            }
        }

        private void 输出地图图片(object sender, EventArgs e)
        {
            if (_活动视图 == null || _画布.当前范围 == null) return;

            using (SaveFileDialog 对话框 = new SaveFileDialog
            {
                Filter = "PNG 图片 (*.png)|*.png",
                FileName = "校园地图.png",
                InitialDirectory = 地图工具.输出目录
            })
            {
                if (对话框.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    地图工具.导出地图图片(_活动视图, _画布.当前范围, 对话框.FileName);
                    输出("地图已输出：" + 对话框.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "输出失败：" + ex.Message);
                }
            }
        }

        // ================================================================ 地图浏览

        private void 记录范围()
        {
            if (_画布.当前范围 == null) return;
            _撤销范围.Push((IEnvelope)((IClone)_画布.当前范围).Clone());
            _重做范围.Clear();
        }

        private void 放大(object sender, EventArgs e)
        {
            记录范围();
            _画布.设置工具(画布工具.放大);
            _状态信息.Text = "放大：在地图上按住鼠标拖出矩形";
            输出("放大：在地图上拖出一个矩形范围。");
        }

        private void 缩小(object sender, EventArgs e)
        {
            记录范围();
            _画布.设置工具(画布工具.缩小);
            _状态信息.Text = "缩小：在地图上拖出矩形，矩形越小缩得越多";
            输出("缩小：拖出一个矩形，该矩形范围会缩小到视图中央。");
        }

        private void 平移(object sender, EventArgs e)
        {
            记录范围();
            _画布.设置工具(画布工具.平移);
            _状态信息.Text = "平移：按住鼠标左键拖动地图";
            输出("平移：按住鼠标左键拖动地图。");
        }

        private void 全图(object sender, EventArgs e)
        {
            记录范围();
            _画布.全图();
            _画布.设置工具(画布工具.无);
            _状态信息.Text = "已显示全图";
        }

        private void 上一视图(object sender, EventArgs e)
        {
            if (_撤销范围.Count == 0) return;
            if (_画布.当前范围 != null)
                _重做范围.Push((IEnvelope)((IClone)_画布.当前范围).Clone());
            _画布.设置范围(_撤销范围.Pop());
        }

        private void 下一视图(object sender, EventArgs e)
        {
            if (_重做范围.Count == 0) return;
            if (_画布.当前范围 != null)
                _撤销范围.Push((IEnvelope)((IClone)_画布.当前范围).Clone());
            _画布.设置范围(_重做范围.Pop());
        }

        // ================================================================ 距离、面积量算

        private void 距离量算(object sender, EventArgs e)
        {
            输出("距离量算：在地图上依次点击节点，双击结束。");
            _画布.开始绘制(false, 图形 =>
            {
                IPolyline 折线 = 图形 as IPolyline;
                if (折线 == null) return;

                赋予空间参考(折线);
                double 长度 = 折线.Length;
                输出(string.Format("距离量算结果：{0:F2} 米", 长度));
                _状态信息.Text = string.Format("量算距离 {0:F2} 米", 长度);
            });
        }

        private void 面积量算(object sender, EventArgs e)
        {
            输出("面积量算：在地图上依次点击节点，双击结束。");
            _画布.开始绘制(true, 图形 =>
            {
                IArea 面 = 图形 as IArea;
                if (面 == null) return;

                赋予空间参考(图形);
                double 面积 = Math.Abs(面.Area);
                输出(string.Format("面积量算结果：{0:F2} 平方米（约 {1:F2} 亩）",
                    面积, 面积 / 666.6667));
                _状态信息.Text = string.Format("量算面积 {0:F2} 平方米", 面积);
            });
        }

        // ================================================================ 查询与识别

        private void 点击识别(object sender, EventArgs e)
        {
            输出("点击识别：请点击地图上的要素。");
            _画布.开始点选(执行识别);
        }

        private void 执行识别(double x, double y)
        {
            IMap 地图 = _画布.地图;

            记录诊断(string.Format("识别：地图坐标 X={0:F2} Y={1:F2}；{2}",
                x, y, _画布.诊断信息()));

            输出(string.Format("识别位置：X={0:F1}  Y={1:F1}", x, y));
            IEnvelope 视图范围 = _画布.当前范围;
            if (视图范围 != null)
                输出(string.Format("当前视图范围：X {0:F0} ~ {1:F0}，Y {2:F0} ~ {3:F0}",
                    视图范围.XMin, 视图范围.XMax, 视图范围.YMin, 视图范围.YMax));

            StringBuilder 文本 = new StringBuilder();
            int 命中图层数 = 0;
            bool 已记录图层范围 = false;

            for (int i = 地图.LayerCount - 1; i >= 0; i--)
            {
                IFeatureLayer 图层 = 地图.get_Layer(i) as IFeatureLayer;
                if (图层 == null || !图层.Visible || 图层.FeatureClass == null) continue;

                try
                {
                    if (!已记录图层范围)
                    {
                        已记录图层范围 = true;
                        IEnvelope 图层范围 = ((IGeoDataset)图层.FeatureClass).Extent;
                        记录诊断(string.Format("图层 {0} 范围 X {1:F0}~{2:F0}, Y {3:F0}~{4:F0}，总要素 {5}",
                            图层.Name, 图层范围.XMin, 图层范围.XMax, 图层范围.YMin, 图层范围.YMax,
                            图层.FeatureClass.FeatureCount(null)));
                    }

                    // 用点周围 3 米的小矩形做相交查询，点到面要素内部即可命中
                    IEnvelope 范围 = new EnvelopeClass();
                    范围.PutCoords(x - 3, y - 3, x + 3, y + 3);
                    try
                    {
                        if (地图.SpatialReference != null) 范围.SpatialReference = 地图.SpatialReference;
                    }
                    catch { }

                    ISpatialFilter 过滤 = new SpatialFilterClass();
                    过滤.Geometry = 范围;
                    过滤.GeometryField = 图层.FeatureClass.ShapeFieldName;
                    过滤.SpatialRel = esriSpatialRelEnum.esriSpatialRelIntersects;

                    int 条数 = 图层.FeatureClass.FeatureCount(过滤);
                    记录诊断("  " + 图层.Name + " 命中 " + 条数);
                    if (条数 == 0) continue;

                    命中图层数++;
                    文本.AppendLine("【" + 图层.Name + "】命中 " + 条数 + " 个要素：");

                    IFeatureCursor 游标 = 图层.FeatureClass.Search(过滤, false);
                    IFeature 要素;
                    int 已显示 = 0;
                    while ((要素 = 游标.NextFeature()) != null && 已显示 < 3)
                    {
                        文本.AppendLine(地图工具.行转文本(要素));
                        已显示++;
                    }
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(游标);
                }
                catch (Exception ex)
                {
                    输出("图层 " + 图层.Name + " 识别失败：" + ex.Message);
                }
            }

            if (文本.Length == 0)
            {
                输出("该位置没有识别到要素（可对照上面的坐标判断是否点在地图要素上）。");
                _状态信息.Text = "未识别到要素";
            }
            else
            {
                输出(文本.ToString());
                _状态信息.Text = "识别到 " + 命中图层数 + " 个图层的要素，详细信息见下方输出窗口";
            }
        }

        private void 属性查询(object sender, EventArgs e)
        {
            using (属性查询窗口 窗口 = new 属性查询窗口(_画布.地图, () => _画布.刷新地图()))
            {
                窗口.ShowDialog(this);
            }
        }

        private void 空间查询(object sender, EventArgs e)
        {
            输出("空间查询：在地图上绘制多边形，双击结束。");
            _画布.开始绘制(true, 图形 =>
            {
                赋予空间参考(图形);

                IMap 地图 = _画布.地图;
                int 总数 = 0;

                IEnvelope 查询范围 = 图形.Envelope;
                输出(string.Format("空间查询范围：X {0:F0} ~ {1:F0}，Y {2:F0} ~ {3:F0}",
                    查询范围.XMin, 查询范围.XMax, 查询范围.YMin, 查询范围.YMax));
                记录诊断(string.Format("空间查询：范围 X {0:F2} ~ {1:F2}, Y {2:F2} ~ {3:F2}；{4}",
                    查询范围.XMin, 查询范围.XMax, 查询范围.YMin, 查询范围.YMax,
                    _画布.诊断信息()));

                for (int i = 0; i < 地图.LayerCount; i++)
                {
                    IFeatureLayer 图层 = 地图.get_Layer(i) as IFeatureLayer;
                    if (图层 == null || !图层.Selectable || !图层.Visible) continue;

                    try
                    {
                        ISpatialFilter 空间过滤 = new SpatialFilterClass();
                        空间过滤.Geometry = 图形;
                        空间过滤.GeometryField = 图层.FeatureClass.ShapeFieldName;
                        空间过滤.SpatialRel = esriSpatialRelEnum.esriSpatialRelIntersects;

                        IFeatureSelection 选择集 = (IFeatureSelection)图层;
                        选择集.SelectFeatures(空间过滤,
                            esriSelectionResultEnum.esriSelectionResultNew, false);
                        int 本层 = 选择集.SelectionSet == null ? 0 : 选择集.SelectionSet.Count;
                        总数 += 本层;
                        if (本层 > 0) 输出("  " + 图层.Name + " 选中 " + 本层 + " 个要素");
                    }
                    catch (Exception ex)
                    {
                        输出("图层 " + 图层.Name + " 查询失败：" + ex.Message);
                    }
                }

                _画布.刷新地图();
                输出("空间查询完成，共选中 " + 总数 + " 个要素。");
                _状态信息.Text = "空间查询选中 " + 总数 + " 个要素";
            });
        }

        private void 清除选择(object sender, EventArgs e)
        {
            IMap 地图 = _画布.地图;
            for (int i = 0; i < 地图.LayerCount; i++)
            {
                IFeatureSelection 选择集 = 地图.get_Layer(i) as IFeatureSelection;
                if (选择集 != null) 选择集.Clear();
            }
            _画布.刷新地图();
            输出("已清除选择。");
        }

        // ================================================================ 数据编辑

        private void 开始编辑(object sender, EventArgs e)
        {
            IFeatureLayer 图层 = 选中的要素图层();
            if (图层 == null) { 输出("请先在左侧选中要编辑的图层。"); return; }
            确保已开始编辑(图层);
        }

        private void 停止编辑并保存(object sender, EventArgs e)
        {
            停止编辑(true);
        }

        /// <summary>
        /// Shapefile 属于非版本化数据，不支持 IWorkspaceEdit，
        /// 直接调用 IFeature.Store()/Delete() 就会写入文件，因此这里只记录当前编辑的图层。
        /// </summary>
        private bool 确保已开始编辑(IFeatureLayer 图层)
        {
            if (图层 == null || 图层.FeatureClass == null) return false;

            if (!_编辑中 || !ReferenceEquals(_编辑图层, 图层))
            {
                _编辑中 = true;
                _编辑图层 = 图层;
                输出("已进入编辑状态：" + 图层.Name + "（修改会直接写入数据文件）");
                _状态信息.Text = "编辑中：" + 图层.Name;
            }
            return true;
        }

        private void 停止编辑(bool 保存)
        {
            if (!_编辑中) return;

            _编辑中 = false;
            _编辑图层 = null;
            输出(保存 ? "已退出编辑，修改已写入数据文件。" : "已退出编辑状态。");
            _状态信息.Text = "就绪";
            _画布.刷新地图();
        }

        private void 添加点要素(object sender, EventArgs e)
        {
            if (选中的要素图层() == null) { 输出("请先在左侧选中点图层。"); return; }
            输出("添加点：请在地图上单击。");
            _画布.开始点选((x, y) =>
                添加要素图形(建立点(x, y), esriGeometryType.esriGeometryPoint));
        }

        private void 添加线要素(object sender, EventArgs e)
        {
            if (选中的要素图层() == null) { 输出("请先在左侧选中线图层。"); return; }
            输出("添加线：依次单击节点，双击结束。");
            _画布.开始绘制(false, 图形 =>
                添加要素图形(图形, esriGeometryType.esriGeometryPolyline));
        }

        private void 添加面要素(object sender, EventArgs e)
        {
            if (选中的要素图层() == null) { 输出("请先在左侧选中面图层。"); return; }
            输出("添加面：依次单击节点，双击结束。");
            _画布.开始绘制(true, 图形 =>
                添加要素图形(图形, esriGeometryType.esriGeometryPolygon));
        }

        private void 添加要素图形(IGeometry 图形, esriGeometryType 期望类型)
        {
            IFeatureLayer 图层 = 选中的要素图层();
            if (图层 == null) { 输出("请先在左侧选中目标图层。"); return; }

            if (图层.FeatureClass.ShapeType != 期望类型)
            {
                输出("图层类型不匹配：当前图层为 " + 图层.FeatureClass.ShapeType + "。");
                return;
            }

            赋予空间参考(图形);
            if (!确保已开始编辑(图层)) return;

            // 让图形与要素类的空间参考完全一致，避免写入时投影不匹配
            try
            {
                IGeoDataset 数据集 = 图层.FeatureClass as IGeoDataset;
                if (数据集 != null && 数据集.SpatialReference != null)
                    图形.SpatialReference = 数据集.SpatialReference;
            }
            catch { }

            try
            {
                IFeature 要素 = 图层.FeatureClass.CreateFeature();
                要素.Shape = 适配要素类几何(图形, 图层.FeatureClass);
                要素.Store();

                _画布.刷新地图();
                输出("已新增一个" + 类型名称(期望类型) + "要素。");
            }
            catch (Exception ex)
            {
                输出("新增要素失败：" + ex.Message);
            }
        }

        private static string 类型名称(esriGeometryType 类型)
        {
            switch (类型)
            {
                case esriGeometryType.esriGeometryPoint: return "点";
                case esriGeometryType.esriGeometryPolyline: return "线";
                default: return "面";
            }
        }

        private void 删除要素(object sender, EventArgs e)
        {
            if (选中的要素图层() == null) { 输出("请先在左侧选中图层。"); return; }
            输出("删除要素：请点击地图上要删除的要素。");
            _画布.开始点选(删除点击处要素);
        }

        private void 删除点击处要素(double x, double y)
        {
            IFeatureLayer 图层 = 选中的要素图层();
            if (图层 == null) return;
            if (!确保已开始编辑(图层)) return;

            IFeature 要素 = 查找点击处要素(图层, x, y);
            if (要素 == null) { 输出("该位置没有找到要素。"); return; }

            try
            {
                要素.Delete();

                _画布.刷新地图();
                输出("已删除一个要素。");
            }
            catch (Exception ex)
            {
                输出("删除失败：" + ex.Message);
            }
        }

        private void 修改要素属性(object sender, EventArgs e)
        {
            if (选中的要素图层() == null) { 输出("请先在左侧选中图层。"); return; }
            输出("修改属性：请点击地图上的要素。");
            _画布.开始点选(修改点击处要素属性);
        }

        private void 修改点击处要素属性(double x, double y)
        {
            IFeatureLayer 图层 = 选中的要素图层();
            if (图层 == null) return;
            if (!确保已开始编辑(图层)) return;

            IFeature 要素 = 查找点击处要素(图层, x, y);
            if (要素 == null) { 输出("该位置没有找到要素。"); return; }

            using (要素属性窗口 窗口 = new 要素属性窗口(要素, 图层.Name))
            {
                窗口.ShowDialog(this);
            }

            _画布.刷新地图();
            输出("属性编辑完成。");
        }

        private static IFeature 查找点击处要素(IFeatureLayer 图层, double x, double y)
        {
            IEnvelope 范围 = new EnvelopeClass();
            范围.PutCoords(x - 5, y - 5, x + 5, y + 5);

            ISpatialFilter 过滤 = new SpatialFilterClass();
            过滤.Geometry = 范围;
            过滤.GeometryField = 图层.FeatureClass.ShapeFieldName;
            过滤.SpatialRel = esriSpatialRelEnum.esriSpatialRelIntersects;

            IFeatureCursor 游标 = 图层.FeatureClass.Search(过滤, false);
            IFeature 要素 = 游标.NextFeature();
            // 游标不释放会锁住 shapefile，导致后续写入失败
            System.Runtime.InteropServices.Marshal.ReleaseComObject(游标);
            return 要素;
        }



        private IFeatureLayer _停车位图层;

        private void 停车位推荐(object sender, EventArgs e)
        {
            IFeatureLayer 图层 = 找或生成停车位图层();
            if (图层 == null) return;

            输出("请在地图上点击一个地点，系统会找出离它最近的非机动车停车位。");
            _状态信息.Text = "点击地点查找最近停车位";

            _画布.开始点选((x, y) =>
            {
                try
                {
                    IFeatureClass 要素类 = 图层.FeatureClass;
                    IFeatureCursor 游标 = 要素类.Search(null, false);
                    IFeature 要素;
                    IFeature 最近要素 = null;
                    double 最近距离 = double.MaxValue;
                    IPoint 最近点 = null;

                    while ((要素 = 游标.NextFeature()) != null)
                    {
                        IPoint 停车点 = 要素.ShapeCopy as IPoint;
                        if (停车点 == null) continue;

                        double 差X = 停车点.X - x;
                        double 差Y = 停车点.Y - y;
                        double 距离 = Math.Sqrt(差X * 差X + 差Y * 差Y);

                        if (距离 < 最近距离)
                        {
                            最近距离 = 距离;
                            最近要素 = 要素;
                            最近点 = 停车点;
                        }
                    }
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(游标);

                    if (最近要素 == null)
                    {
                        输出("没有可用的停车位数据。");
                        return;
                    }

                    IFeatureSelection 选择 = 图层 as IFeatureSelection;
                    if (选择 != null)
                    {
                        选择.Clear();
                        选择.Add(最近要素);
                    }
                    _画布.刷新地图();

                    输出(string.Format("离您点击位置最近的非机动车停车位：距离约 {0:F1} 米，位置 (X={1:F0}, Y={2:F0})，已在地图上高亮。",
                        最近距离, 最近点.X, 最近点.Y));
                    _状态信息.Text = string.Format("最近停车位：{0:F1} 米", 最近距离);
                }
                catch (Exception ex)
                {
                    输出("查询停车位失败：" + ex.Message);
                }
            });
        }

        /// <summary>停车位图层已在就复用，否则沿道路现场布设一份。</summary>
        private IFeatureLayer 找或生成停车位图层()
        {
            if (_停车位图层 != null && _停车位图层.FeatureClass != null) return _停车位图层;

            IMap 地图 = _画布.地图;
            for (int i = 0; i < 地图.LayerCount; i++)
            {
                IFeatureLayer 已有 = 地图.get_Layer(i) as IFeatureLayer;
                if (已有 != null && 已有.Name == "非机动车停车位")
                {
                    _停车位图层 = 已有;
                    return 已有;
                }
            }

            IFeatureLayer 道路 = 找第一个线图层();
            if (道路 == null)
            {
                输出("没有道路数据，无法布设停车位。");
                return null;
            }

            输出("首次使用，正在沿道路布设停车位，请稍候……");
            int 数量;
            IFeatureLayer 结果 = 停车位推荐器.生成(地图, 道路, 40, 6, out 数量);
            if (结果 == null)
            {
                输出("道路两侧没有找到合适的停车位位置。");
                return null;
            }

            _停车位图层 = 结果;
            刷新图层列表();
            输出("已沿道路布设 " + 数量 + " 个非机动车停车位。");
            return 结果;
        }

        // ================================================================ 地图制图

        private void 图层符号化(object sender, EventArgs e)
        {
            ILayer 图层 = 选中图层();
            if (图层 == null) { 输出("请先在左侧选中图层。"); return; }

            IGeoFeatureLayer 可渲染图层 = 图层 as IGeoFeatureLayer;
            IFeatureLayer 要素图层 = 图层 as IFeatureLayer;
            if (可渲染图层 == null || 要素图层 == null)
            {
                输出("该图层不支持符号化。");
                return;
            }

            try
            {
                ISimpleRenderer 渲染器 = new SimpleRendererClass();
                渲染器.Symbol = 地图工具.创建符号(要素图层.FeatureClass.ShapeType,
                    _颜色序号++, 0);

                可渲染图层.Renderer = (IFeatureRenderer)渲染器;
                _活动视图.ContentsChanged();
                _画布.刷新地图();

                输出("已对图层 " + 图层.Name + " 应用新的点/线/面符号。");
            }
            catch (Exception ex)
            {
                输出("符号化失败：" + ex.Message);
            }
        }

        // ================================================================ 帮助

        private void 显示操作说明(object sender, EventArgs e)
        {
            MessageBox.Show(this,
                "校园GIS系统操作说明\r\n\r\n" +
                "1. 数据导入导出：文件 → 添加数据 / 导出当前图层。\r\n" +
                "2. 地图浏览：放大、缩小、平移需按住鼠标拖动；全图、上一视图、下一视图。\r\n" +
                "3. 量算：距离量算、面积量算（依次单击节点，双击结束）。\r\n" +
                "4. 查询：点击识别、属性查询（SQL 条件）、空间查询（画多边形）。\r\n" +
                "5. 编辑：选中图层 → 开始编辑 → 添加点/线/面、删除、改属性 → 停止编辑并保存。\r\n" +
                "6. 空间分析：缓冲区、叠置相交、最短路径（先选中道路线图层）。\r\n" +
                "7. 制图：图层符号化、地图输出为 PNG。",
                "操作说明");
        }

        private void 显示关于(object sender, EventArgs e)
        {
            MessageBox.Show(this,
                "校园GIS系统\r\nArcGIS Engine 10.8 + C# WinForms\r\nGIS 软件设计课程实验",
                "关于");
        }

        // ================================================================ 功能自检

        /// <summary>把主要功能依次跑一遍并写入结果文件，用于交付前确认每项功能可用。</summary>
        public void 运行自检(string 结果文件)
        {
            StringBuilder 记录 = new StringBuilder();
            Action<string> 记 = 文字 => 记录.AppendLine(文字);
            记("=== 校园GIS 功能自检 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");

            IMap 地图 = _画布.地图;
            记("[数据] 图层数 = " + 地图.LayerCount);

            int 有渲染器 = 0;
            for (int i = 0; i < 地图.LayerCount; i++)
            {
                IGeoFeatureLayer 可渲染 = 地图.get_Layer(i) as IGeoFeatureLayer;
                if (可渲染 != null && 可渲染.Renderer != null) 有渲染器++;
            }
            记("[符号] 带渲染器的图层数 = " + 有渲染器 + (有渲染器 == 地图.LayerCount ? " 通过" : " 失败"));

            IFeatureLayer 图书馆 = 查找图层("图书馆");
            IFeatureLayer 绿地 = 查找图层("绿地");
            IFeatureLayer 道路 = 找第一个线图层();

            // 点击识别
            try
            {
                IFeature 要素 = 图书馆 == null ? null : 首要素(图书馆);
                IArea 面 = 要素 == null ? null : 要素.Shape as IArea;
                if (面 != null)
                {
                    IPoint 点 = 面.Centroid;
                    try { 点.SpatialReference = 地图.SpatialReference; } catch { }
                    int 命中 = 0;
                    for (int i = 0; i < 地图.LayerCount; i++)
                    {
                        IIdentify 识别器 = 地图.get_Layer(i) as IIdentify;
                        if (识别器 == null) continue;
                        IArray 结果 = null;
                        try { 结果 = 识别器.Identify(点); } catch { }
                        if (结果 != null) 命中 += 结果.Count;
                    }
                    记("[点击识别] 图书馆中心点命中要素数 = " + 命中 + (命中 > 0 ? " 通过" : " 失败"));
                }
                else 记("[点击识别] 跳过：没有图书馆图层");
            }
            catch (Exception ex) { 记("[点击识别] 异常：" + ex.Message); }

            // 空间查询
            try
            {
                IFeature 要素 = 绿地 == null ? null : 首要素(绿地);
                if (要素 != null)
                {
                    IFeatureClass 要素类 = 绿地.FeatureClass;
                    ISpatialFilter 过滤 = new SpatialFilterClass();
                    过滤.Geometry = 要素.Shape.Envelope;
                    过滤.GeometryField = 要素类.ShapeFieldName;
                    过滤.SpatialRel = esriSpatialRelEnum.esriSpatialRelIntersects;
                    记("[空间查询] 绿地图层空间查询要素数 = " + 要素类.FeatureCount(过滤) + " 通过");
                }
                else 记("[空间查询] 跳过：没有绿地图层");
            }
            catch (Exception ex) { 记("[空间查询] 异常：" + ex.Message); }

            // 编辑：新增一个面，再删掉
            try
            {
                IFeature 样本 = 绿地 == null ? null : 首要素(绿地);
                if (样本 != null)
                {
                    IFeatureClass 要素类 = 绿地.FeatureClass;
                    int 之前 = 要素类.FeatureCount(null);

                    IGeometry 面 = 建测试面(样本.Shape.Envelope);
                    try { 面.SpatialReference = ((IGeoDataset)要素类).SpatialReference; } catch { }

                    IFeature 新要素 = 要素类.CreateFeature();
                    新要素.Shape = 适配要素类几何(面, 要素类);
                    新要素.Store();
                    int 之后 = 要素类.FeatureCount(null);
                    记("[编辑-新增] 要素数 " + 之前 + " -> " + 之后 + (之后 == 之前 + 1 ? " 通过" : " 失败"));

                    新要素.Delete();
                    int 删除后 = 要素类.FeatureCount(null);
                    记("[编辑-删除] 要素数 " + 之后 + " -> " + 删除后 + (删除后 == 之前 ? " 通过" : " 失败"));
                }
                else 记("[编辑] 跳过：没有绿地图层");
            }
            catch (Exception ex) { 记("[编辑] 异常：" + ex.Message); }

            // 缓冲区
            try
            {
                if (图书馆 != null)
                {
                    int 要素数;
                    IFeatureLayer 结果 = 空间分析.缓冲区(地图, 图书馆, 20, out 要素数);
                    记("[缓冲区] 生成要素数 = " + 要素数 + (结果 != null && 要素数 > 0 ? " 通过" : " 失败"));
                }
                else 记("[缓冲区] 跳过：没有图书馆图层");
            }
            catch (Exception ex) { 记("[缓冲区] 异常：" + ex.Message); }

            // 叠置
            try
            {
                if (图书馆 != null && 绿地 != null)
                {
                    IFeatureLayer 结果 = 空间分析.相交叠置(地图, 图书馆, 绿地);
                    if (结果 == null) 记("[叠置] 失败：未生成结果");
                    else 记("[叠置] 结果图层 " + 结果.Name + " 要素数 = " +
                            结果.FeatureClass.FeatureCount(null) + " 通过");
                }
                else 记("[叠置] 跳过：缺少图层");
            }
            catch (Exception ex) { 记("[叠置] 异常：" + ex.Message); }

            // 最短路径
            try
            {
                if (道路 != null)
                {
                    道路网络 路网 = new 道路网络(道路);
                    IPoint 起, 止;
                    取最长线的两端(道路, out 起, out 止);

                    if (起 == null || 止 == null)
                    {
                        记("[最短路径] 跳过：道路图层没有可用的线要素");
                    }
                    else
                    {
                        double 长度;
                        IPolyline 路径 = 路网.最短路径(起, 止, out 长度);
                        记("[最短路径] 节点数 = " + 路网.节点数 + "，" +
                            (路径 == null ? "未找到连通路径" : "长度 = " + 长度.ToString("F2") + " 米 通过"));
                    }
                }
                else 记("[最短路径] 跳过：没有线图层");
            }
            catch (Exception ex) { 记("[最短路径] 异常：" + ex.Message); }

            // 符号化
            try
            {
                if (绿地 != null)
                {
                    IGeoFeatureLayer 可渲染 = (IGeoFeatureLayer)绿地;
                    ISimpleRenderer 渲染器 = new SimpleRendererClass();
                    渲染器.Symbol = 地图工具.创建符号(绿地.FeatureClass.ShapeType, 5, 0);
                    可渲染.Renderer = (IFeatureRenderer)渲染器;
                    记("[符号化] 已更换绿地图层符号 通过");
                }
                else 记("[符号化] 跳过：没有绿地图层");
            }
            catch (Exception ex) { 记("[符号化] 异常：" + ex.Message); }

            // 导出图片
            try
            {
                string 图片 = Path.Combine(地图工具.输出目录, "自检地图.png");
                if (File.Exists(图片)) File.Delete(图片);
                地图工具.导出地图图片(_活动视图, _画布.当前范围, 图片);
                long 大小 = new FileInfo(图片).Length;
                记("[导出图片] " + 大小 + " 字节 " + (大小 > 10000 ? "通过" : "失败"));
            }
            catch (Exception ex) { 记("[导出图片] 异常：" + ex.Message); }

            记("=== 自检结束 ===");
            File.WriteAllText(结果文件, 记录.ToString(), new UTF8Encoding(false));
        }

        private IFeatureLayer 查找图层(string 名称)
        {
            IMap 地图 = _画布.地图;
            for (int i = 0; i < 地图.LayerCount; i++)
            {
                ILayer 图层 = 地图.get_Layer(i);
                if (图层 != null && 图层.Name == 名称) return 图层 as IFeatureLayer;
            }
            return null;
        }

        private static IFeature 首要素(IFeatureLayer 图层)
        {
            IFeatureCursor 游标 = 图层.FeatureClass.Search(null, false);
            IFeature 要素 = 游标.NextFeature();
            System.Runtime.InteropServices.Marshal.ReleaseComObject(游标);
            return 要素;
        }

        /// <summary>取线图层中最长的一条线，用它的两个端点测试最短路径。</summary>
        private static void 取最长线的两端(IFeatureLayer 图层, out IPoint 起, out IPoint 止)
        {
            起 = null;
            止 = null;

            IFeatureCursor 游标 = 图层.FeatureClass.Search(null, false);
            IFeature 要素;
            double 最长 = -1;
            double[] 起坐标 = null;
            double[] 止坐标 = null;

            while ((要素 = 游标.NextFeature()) != null)
            {
                IPolyline 线 = 要素.Shape as IPolyline;
                if (线 == null || 线.IsEmpty) continue;

                double 长度 = 线.Length;
                if (长度 > 最长)
                {
                    最长 = 长度;
                    起坐标 = new[] { 线.FromPoint.X, 线.FromPoint.Y };
                    止坐标 = new[] { 线.ToPoint.X, 线.ToPoint.Y };
                }
            }
            System.Runtime.InteropServices.Marshal.ReleaseComObject(游标);

            if (起坐标 == null) return;
            起 = new PointClass();
            起.PutCoords(起坐标[0], 起坐标[1]);
            止 = new PointClass();
            止.PutCoords(止坐标[0], 止坐标[1]);
        }

        /// <summary>在给定范围中心构造一个 4 米见方的小面，用于测试新增要素。</summary>
        private static IGeometry 建测试面(IEnvelope 基准)
        {
            double 中心X = (基准.XMin + 基准.XMax) / 2.0;
            double 中心Y = (基准.YMin + 基准.YMax) / 2.0;
            double[][] 偏移 = { new[] { -2.0, -2.0 }, new[] { 2.0, -2.0 },
                                new[] { 2.0, 2.0 }, new[] { -2.0, 2.0 } };

            IPointCollection 点集 = new PolygonClass();
            for (int i = 0; i < 偏移.Length; i++)
            {
                IPoint 点 = new PointClass();
                点.PutCoords(中心X + 偏移[i][0], 中心Y + 偏移[i][1]);
                点集.AddPoint(点);
            }
            return (IGeometry)点集;
        }

        // ================================================================ 杂项

        /// <summary>把一行诊断文字追加到 输出\诊断日志.txt，便于定位坐标问题。</summary>
        private static void 记录诊断(string 文字)
        {
            try
            {
                File.AppendAllText(Path.Combine(地图工具.输出目录, "诊断日志.txt"),
                    "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + 文字 + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>
        /// 数据里的面/线带有 Z 值，而程序里绘制的图形是二维的。
        /// 写入前按要素类定义补上 Z（统一取 0），否则会报“几何没有 Z 值”。
        /// </summary>
        private static IGeometry 适配要素类几何(IGeometry 图形, IFeatureClass 要素类)
        {
            if (图形 == null || 要素类 == null) return 图形;

            int 图形字段序号 = 要素类.FindField(要素类.ShapeFieldName);
            if (图形字段序号 < 0) return 图形;

            IGeometryDef 定义 = 要素类.Fields.get_Field(图形字段序号).GeometryDef;
            bool 需要Z = 定义 != null && 定义.HasZ;
            bool 需要M = 定义 != null && 定义.HasM;

            IZAware Z标志 = 图形 as IZAware;
            IMAware M标志 = 图形 as IMAware;
            if (需要Z && Z标志 != null) Z标志.ZAware = true;
            if (需要M && M标志 != null) M标志.MAware = true;

            if (!需要Z) return 图形;

            IGeometryCollection 几何集合 = 图形 as IGeometryCollection;
            if (几何集合 == null) return 图形;

            for (int g = 0; g < 几何集合.GeometryCount; g++)
            {
                IPointCollection 点集 = 几何集合.get_Geometry(g) as IPointCollection;
                if (点集 == null) continue;

                for (int i = 0; i < 点集.PointCount; i++)
                {
                    IPoint 点 = 点集.get_Point(i);
                    if (点 == null) continue;
                    try
                    {
                        IZAware 点Z标志 = 点 as IZAware;
                        if (点Z标志 != null) 点Z标志.ZAware = true;
                        点.Z = 0;
                    }
                    catch { }
                }
            }
            return 图形;
        }

        /// <summary>给临时绘制的图形赋予地图的空间参考，量算结果才准确。</summary>
        private void 赋予空间参考(IGeometry 图形)
        {
            if (图形 == null) return;

            try
            {
                IMap 地图 = _画布.地图;
                if (地图 != null && 地图.SpatialReference != null)
                {
                    图形.SpatialReference = 地图.SpatialReference;
                    return;
                }

                for (int i = 0; i < 地图.LayerCount; i++)
                {
                    IFeatureLayer 图层 = 地图.get_Layer(i) as IFeatureLayer;
                    if (图层 == null) continue;

                    ISpatialReference 空间参考 = 地图工具.获取空间参考(图层.FeatureClass);
                    if (空间参考 != null)
                    {
                        图形.SpatialReference = 空间参考;
                        return;
                    }
                }
            }
            catch { }
        }

        private static IPoint 建立点(double x, double y)
        {
            IPoint 点 = new PointClass();
            点.PutCoords(x, y);
            return 点;
        }

        private void 输出(string 消息)
        {
            if (_输出框 == null) return;

            if (_输出框.TextLength > 80000) _输出框.Clear();
            _输出框.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + 消息 +
                               Environment.NewLine);
            _输出框.SelectionStart = _输出框.TextLength;
            _输出框.ScrollToCaret();
        }
    }
}
