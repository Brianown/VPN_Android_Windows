using System;
using System.Windows.Forms;

namespace LiteTunnel
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Logger.Log("LiteTunnel started, version " + typeof(Program).Assembly.GetName().Version);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                Logger.Log("UI 未处理异常：" + e.Exception);
                MessageBox.Show(e.Exception.Message, "LiteTunnel 发生错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                Logger.Log("未处理异常：" + e.ExceptionObject);
                MessageBox.Show(ex != null ? ex.Message : "未知错误", "LiteTunnel 发生错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.Run(new MainForm());
        }
    }
}
