using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ESRI.ArcGIS;
using ESRI.ArcGIS.esriSystem;

namespace 校园GIS
{
    /// <summary>
    /// 程序入口：初始化 ArcGIS Engine 许可，然后打开主窗口。
    /// 注意：项目必须编译为 x86，ArcObjects 是 32 位组件。
    /// </summary>
    internal static class 程序入口
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main()
        {
            // 关闭系统 DPI 缩放，避免控件坐标和屏幕像素不一致
            try { SetProcessDPIAware(); } catch { }

            // ArcObjects 的运行时 DLL 在 Common Files 下，加入 PATH 让程序能找到
            string arcGis运行时目录 = @"C:\Program Files (x86)\Common Files\ArcGIS\bin";
            if (Directory.Exists(arcGis运行时目录))
            {
                string 原路径 = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (原路径.IndexOf(arcGis运行时目录, StringComparison.OrdinalIgnoreCase) < 0)
                    Environment.SetEnvironmentVariable("PATH", arcGis运行时目录 + ";" + 原路径);
            }

            IAoInitialize 许可 = null;
            try
            {
                RuntimeManager.Bind(ProductCode.Desktop);
                许可 = new AoInitializeClass();
                esriLicenseStatus 状态 = 许可.Initialize(esriLicenseProductCode.esriLicenseProductCodeAdvanced);
                if (状态 != esriLicenseStatus.esriLicenseCheckedOut &&
                    状态 != esriLicenseStatus.esriLicenseAlreadyInitialized)
                {
                    MessageBox.Show("ArcGIS 许可初始化失败，状态：" + 状态, "错误");
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("ArcGIS 初始化异常：" + ex.Message, "错误");
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += (s, e) => 记录崩溃日志("界面线程异常", e.Exception);
            AppDomain.CurrentDomain.UnhandledException +=
                (s, e) => 记录崩溃日志("未处理异常", e.ExceptionObject as Exception);

            try
            {
                Application.Run(new 主窗口());
            }
            catch (Exception ex)
            {
                记录崩溃日志("启动失败", ex);
                throw;
            }

            if (许可 != null)
            {
                try { 许可.Shutdown(); } catch { }
            }
        }

        /// <summary>把异常写入程序目录下的 错误日志.txt，方便排查。</summary>
        private static void 记录崩溃日志(string 类型, Exception ex)
        {
            try
            {
                string 文件 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "错误日志.txt");
                File.AppendAllText(文件,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + 类型 + Environment.NewLine +
                    (ex == null ? "(无详情)" : ex.ToString()) + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}
