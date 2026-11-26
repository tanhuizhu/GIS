using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Windows.Forms;
using ESRI.ArcGIS.Carto;
using ESRI.ArcGIS.Geodatabase;
using ESRI.ArcGIS.Geometry;

namespace 校园GIS
{
    /// <summary>下拉框中显示的图层条目。</summary>
    internal sealed class 图层项
    {
        public ILayer 图层;

        public 图层项(ILayer 图层)
        {
            this.图层 = 图层;
        }

        public override string ToString()
        {
            return 图层 == null ? "" : 图层.Name;
        }
    }

    /// <summary>属性查询窗口：按 SQL 条件过滤记录，并可在地图上高亮。</summary>
    public class 属性查询窗口 : Form
    {
        private readonly IMap _地图;
        private readonly Action _刷新地图;

        private readonly ComboBox _图层下拉框 = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill
        };
        private readonly TextBox _条件输入框 = new TextBox { Dock = DockStyle.Fill, Text = "" };
        private readonly ComboBox _字段下拉框 = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 150
        };
        private readonly DataGridView _结果表格 = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };
        private readonly Label _状态标签 = new Label
        {
            Dock = DockStyle.Fill,
            Text = "条件留空表示查询全部图层要素。例如：OBJECTID > 5"
        };

        private IFeatureLayer _当前图层;
        private IQueryFilter _当前过滤条件;

        public 属性查询窗口(IMap 地图, Action 刷新地图)
        {
            _地图 = 地图;
            _刷新地图 = 刷新地图;
            Text = "属性查询";
            Width = 920;
            Height = 640;
            MinimumSize = new Size(720, 480);
            StartPosition = FormStartPosition.CenterParent;

            TableLayoutPanel 根布局 = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                Padding = new Padding(8)
            };
            根布局.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            根布局.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            根布局.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            根布局.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            根布局.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            根布局.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            根布局.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

            根布局.Controls.Add(新建标签("图层："), 0, 0);
            根布局.Controls.Add(_图层下拉框, 1, 0);

            TableLayoutPanel 条件行 = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0)
            };
            条件行.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            条件行.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            条件行.Controls.Add(_条件输入框, 0, 0);
            条件行.Controls.Add(_字段下拉框, 1, 0);

            TableLayoutPanel 条件容器 = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0)
            };
            条件容器.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            条件容器.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            条件容器.Controls.Add(新建标签("条件："), 0, 0);
            条件容器.Controls.Add(条件行, 1, 0);

            FlowLayoutPanel 按钮行 = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0, 3, 0, 3)
            };
            Button 查询按钮 = new Button { Text = "查询", Width = 110, Height = 30 };
            查询按钮.Click += (s, e) => 执行查询();
            Button 高亮按钮 = new Button { Text = "地图高亮", Width = 110, Height = 30 };
            高亮按钮.Click += (s, e) => 地图高亮();
            Button 关闭按钮 = new Button { Text = "关闭", Width = 110, Height = 30 };
            关闭按钮.Click += (s, e) => Close();
            按钮行.Controls.Add(查询按钮);
            按钮行.Controls.Add(高亮按钮);
            按钮行.Controls.Add(关闭按钮);

            根布局.Controls.Add(条件容器, 0, 1);
            根布局.SetColumnSpan(条件容器, 2);
            根布局.Controls.Add(按钮行, 0, 2);
            根布局.SetColumnSpan(按钮行, 2);
            根布局.Controls.Add(_结果表格, 0, 3);
            根布局.SetColumnSpan(_结果表格, 2);
            根布局.Controls.Add(_状态标签, 0, 4);
            根布局.SetColumnSpan(_状态标签, 2);

            Controls.Add(根布局);

            _图层下拉框.SelectedIndexChanged += (s, e) => 载入字段();
            _字段下拉框.SelectedIndexChanged += (s, e) => 填入字段();

            载入图层();
            if (_图层下拉框.Items.Count > 0) _图层下拉框.SelectedIndex = 0;
        }

        private static Label 新建标签(string 文字)
        {
            return new Label
            {
                Text = 文字,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private void 载入图层()
        {
            if (_地图 == null) return;
            for (int i = _地图.LayerCount - 1; i >= 0; i--)
            {
                IFeatureLayer 图层 = _地图.get_Layer(i) as IFeatureLayer;
                if (图层 != null) _图层下拉框.Items.Add(new 图层项(图层));
            }
            if (_图层下拉框.Items.Count > 0) _图层下拉框.SelectedIndex = 0;
        }

        /// <summary>按当前图层刷新字段下拉框，避免用户手写不存在的字段名。</summary>
        private void 载入字段()
        {
            _字段下拉框.Items.Clear();
            _字段下拉框.Items.Add("（选择字段）");

            图层项 选中项 = _图层下拉框.SelectedItem as 图层项;
            IFeatureLayer 图层 = 选中项 == null ? null : 选中项.图层 as IFeatureLayer;
            if (图层 == null || 图层.FeatureClass == null)
            {
                _字段下拉框.SelectedIndex = 0;
                return;
            }

            try
            {
                IFields 字段集 = 图层.FeatureClass.Fields;
                for (int i = 0; i < 字段集.FieldCount; i++)
                {
                    IField 字段 = 字段集.get_Field(i);
                    if (字段.Type == esriFieldType.esriFieldTypeGeometry) continue;
                    _字段下拉框.Items.Add(字段.Name);
                }
            }
            catch { }

            _字段下拉框.SelectedIndex = 0;
        }

        private void 填入字段()
        {
            if (_字段下拉框.SelectedIndex <= 0) return;

            string 字段名 = _字段下拉框.SelectedItem as string;
            if (string.IsNullOrEmpty(字段名)) return;

            _条件输入框.Text = 字段名 + " ";
            _条件输入框.Focus();
            _条件输入框.SelectionStart = _条件输入框.Text.Length;
        }

        private void 执行查询()
        {
            try
            {
                图层项 选中项 = _图层下拉框.SelectedItem as 图层项;
                IFeatureLayer 图层 = 选中项 == null ? null : 选中项.图层 as IFeatureLayer;
                if (图层 == null)
                {
                    MessageBox.Show(this, "请选择要查询的图层。");
                    return;
                }

                IQueryFilter 过滤条件 = new QueryFilterClass();
                过滤条件.WhereClause = _条件输入框.Text.Trim();

                IFeatureClass 要素类 = 图层.FeatureClass;
                IFields 字段集 = 要素类.Fields;

                List<KeyValuePair<int, IField>> 列信息 = new List<KeyValuePair<int, IField>>();
                DataTable 表格 = new DataTable();
                for (int i = 0; i < 字段集.FieldCount; i++)
                {
                    IField 字段 = 字段集.get_Field(i);
                    if (字段.Type == esriFieldType.esriFieldTypeGeometry) continue;
                    列信息.Add(new KeyValuePair<int, IField>(i, 字段));
                    表格.Columns.Add(字段.Name, typeof(string));
                }

                IFeatureCursor 游标 = 要素类.Search(过滤条件, false);
                IFeature 要素;
                int 条数 = 0;
                while ((要素 = 游标.NextFeature()) != null && 条数 < 500)
                {
                    DataRow 行 = 表格.NewRow();
                    for (int c = 0; c < 列信息.Count; c++)
                    {
                        object 值 = null;
                        try { 值 = 要素.get_Value(列信息[c].Key); } catch { }
                        行[c] = 值 == null || 值 == DBNull.Value ? "" : 值.ToString();
                    }
                    表格.Rows.Add(行);
                    条数++;
                }
                System.Runtime.InteropServices.Marshal.ReleaseComObject(游标);

                _结果表格.DataSource = 表格;
                _当前图层 = 图层;
                _当前过滤条件 = 过滤条件;
                _状态标签.Text = "图层 " + 图层.Name + " 查询到 " + 条数 + " 条记录（最多显示 500 条）。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "查询失败：" + ex.Message, "属性查询");
            }
        }

        private void 地图高亮()
        {
            if (_当前图层 == null || _当前过滤条件 == null)
            {
                MessageBox.Show(this, "请先执行查询。");
                return;
            }
            try
            {
                IFeatureSelection 选择集 = (IFeatureSelection)_当前图层;
                选择集.SelectFeatures(_当前过滤条件,
                    esriSelectionResultEnum.esriSelectionResultNew, false);
                if (_刷新地图 != null) _刷新地图();
                _状态标签.Text = "已在地图中高亮选中要素。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "高亮失败：" + ex.Message);
            }
        }
    }

    /// <summary>单个要素的属性编辑窗口。</summary>
    public class 要素属性窗口 : Form
    {
        private readonly IFeature _要素;
        private readonly DataGridView _属性表格 = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AllowUserToDeleteRows = false
        };

        public 要素属性窗口(IFeature 要素, string 图层名)
        {
            _要素 = 要素;
            Text = "修改属性 - " + 图层名;
            Width = 480;
            Height = 520;
            StartPosition = FormStartPosition.CenterParent;

            _属性表格.Columns.Add("字段", "字段");
            _属性表格.Columns.Add("字段值", "字段值");
            _属性表格.Columns[0].ReadOnly = true;
            _属性表格.Columns[0].Width = 150;

            IFields 字段集 = 要素.Fields;
            for (int i = 0; i < 字段集.FieldCount; i++)
            {
                IField 字段 = 字段集.get_Field(i);
                if (字段.Type == esriFieldType.esriFieldTypeGeometry) continue;
                if (字段.Type == esriFieldType.esriFieldTypeOID) continue;

                object 值 = null;
                try { 值 = 要素.get_Value(i); } catch { }
                int 行号 = _属性表格.Rows.Add(字段.Name,
                    值 == null || 值 == DBNull.Value ? "" : 值.ToString());
                _属性表格.Rows[行号].Tag = i;
            }

            TableLayoutPanel 根布局 = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(8)
            };
            根布局.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            根布局.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

            FlowLayoutPanel 按钮区 = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft
            };
            Button 保存按钮 = new Button { Text = "保存", Width = 90, DialogResult = DialogResult.OK };
            Button 取消按钮 = new Button { Text = "取消", Width = 90, DialogResult = DialogResult.Cancel };
            保存按钮.Click += (s, e) => 应用修改();
            按钮区.Controls.Add(取消按钮);
            按钮区.Controls.Add(保存按钮);

            根布局.Controls.Add(_属性表格, 0, 0);
            根布局.Controls.Add(按钮区, 0, 1);
            Controls.Add(根布局);
        }

        private void 应用修改()
        {
            try
            {
                foreach (DataGridViewRow 行 in _属性表格.Rows)
                {
                    int 字段序号 = (int)行.Tag;
                    IField 字段 = _要素.Fields.get_Field(字段序号);
                    string 文本 = Convert.ToString(行.Cells[1].Value) ?? "";
                    _要素.set_Value(字段序号, 转换字段值(字段, 文本));
                }
                _要素.Store();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存属性失败：" + ex.Message, "修改属性");
            }
        }

        private static object 转换字段值(IField 字段, string 文本)
        {
            string 值文本 = 文本.Trim();
            switch (字段.Type)
            {
                case esriFieldType.esriFieldTypeInteger:
                case esriFieldType.esriFieldTypeSmallInteger:
                    if (值文本.Length == 0) return 0;
                    return int.Parse(值文本);
                case esriFieldType.esriFieldTypeDouble:
                case esriFieldType.esriFieldTypeSingle:
                    if (值文本.Length == 0) return 0.0;
                    return double.Parse(值文本);
                case esriFieldType.esriFieldTypeDate:
                    if (值文本.Length == 0) return DBNull.Value;
                    return DateTime.Parse(值文本);
                default:
                    return 文本;
            }
        }
    }

    /// <summary>空间分析的种类。</summary>

}
