using System;
using System.Net;
using System.Windows.Forms;
using IceTube.Logging;

namespace IceTube
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // Do not inherit an old machine's legacy .NET TLS defaults when
            // connecting to YouTube's HTTPS media endpoints.
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 8;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                LogService.Error("Unhandled application error", ex);
                MessageBox.Show(
                    "IceTube 遇到无法恢复的错误。详情已写入 logs 文件夹。\r\n\r\n" + ex.Message,
                    "IceTube",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }
}
