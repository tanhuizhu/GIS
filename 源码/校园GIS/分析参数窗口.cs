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
    /// <summary>
    /// 缓冲区/叠置分析的参数输入窗口（由搭档维护，只改本文件）。
    /// </summary>
    public enum 分析类型
    {
        缓冲区,
        相交叠置
    }

    /// <summary>缓冲区 / 叠置分析的参数窗口。</summary>
    public class 分析参数窗口 : Form
    {
        private readonly 分析类型 _分析类型;
        private readonly ComboBox _图层A下拉框 = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill
        };
        private readonly ComboBox _图层B下拉框 = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill
        };
        private readonly TextBox _半径输入框 = new TextBox { Dock = DockStyle.Fill, Text = "20" };

        public 分析参数窗口(分析类型 类型, IMap 地图)
        {
            _分析类型 = 类型;
            Text = 类型 == 分析类型.缓冲区 ? "缓冲区分析" : "叠置分析（相交）";
            Width = 420;
            Height = 类型 == 分析类型.缓冲区 ? 190 : 220;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            TableLayoutPanel 根布局 = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                Padding = new Padding(10)
            };
            根布局.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            根布局.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) 根布局.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

            根布局.Controls.Add(新建标签(类型 == 分析类型.缓冲区 ? "输入图层：" : "图层 A："), 0, 0);
            根布局.Controls.Add(_图层A下拉框, 1, 0);

            int 当前行 = 1;
            if (类型 == 分析类型.相交叠置)
            {
                根布局.Controls.Add(新建标签("图层 B："), 0, 当前行);
                根布局.Controls.Add(_图层B下拉框, 1, 当前行);
                当前行++;
            }
            else
            {
                根布局.Controls.Add(新建标签("缓冲半径(米)："), 0, 当前行);
                根布局.Controls.Add(_半径输入框, 1, 当前行);
                当前行++;
            }

            FlowLayoutPanel 按钮区 = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft
            };
            Button 确定按钮 = new Button { Text = "确定", Width = 90 };
            Button 取消按钮 = new Button { Text = "取消", Width = 90, DialogResult = DialogResult.Cancel };
            确定按钮.Click += (s, e) => 确认();
            按钮区.Controls.Add(取消按钮);
            按钮区.Controls.Add(确定按钮);

            根布局.Controls.Add(按钮区, 0, 当前行);
            根布局.SetColumnSpan(按钮区, 2);
            Controls.Add(根布局);

            if (地图 != null)
            {
                for (int i = 地图.LayerCount - 1; i >= 0; i--)
                {
                    IFeatureLayer 图层 = 地图.get_Layer(i) as IFeatureLayer;
                    if (图层 == null) continue;
                    _图层A下拉框.Items.Add(new 图层项(图层));
                    if (类型 == 分析类型.相交叠置) _图层B下拉框.Items.Add(new 图层项(图层));
                }
            }
            if (_图层A下拉框.Items.Count > 0) _图层A下拉框.SelectedIndex = 0;
            if (_图层B下拉框.Items.Count > 1) _图层B下拉框.SelectedIndex = 1;
            else if (_图层B下拉框.Items.Count > 0) _图层B下拉框.SelectedIndex = 0;
        }

        public IFeatureLayer 图层A
        {
            get
            {
                图层项 项 = _图层A下拉框.SelectedItem as 图层项;
                return 项 == null ? null : 项.图层 as IFeatureLayer;
            }
        }

        public IFeatureLayer 图层B
        {
            get
            {
                图层项 项 = _图层B下拉框.SelectedItem as 图层项;
                return 项 == null ? null : 项.图层 as IFeatureLayer;
            }
        }

        public double 缓冲半径
        {
            get
            {
                double 值;
                return double.TryParse(_半径输入框.Text.Trim(), out 值) ? 值 : 0;
            }
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

        private void 确认()
        {
            if (图层A == null)
            {
                MessageBox.Show(this, "请选择图层。");
                return;
            }
            if (_分析类型 == 分析类型.缓冲区 && 缓冲半径 <= 0)
            {
                MessageBox.Show(this, "请输入大于 0 的缓冲半径（米）。");
                return;
            }
            if (_分析类型 == 分析类型.相交叠置 && 图层B == null)
            {
                MessageBox.Show(this, "请选择图层 B。");
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
