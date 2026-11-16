using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;

internal static class Setup
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        string temporary = null;
        try
        {
            if (arguments.Length == 2 && arguments[0] == "--extract")
            {
                Extract(Path.GetFullPath(arguments[1]));
                return 0;
            }
            bool secondary = arguments.Length == 1 && arguments[0] == "--prepare-second-display";
            if (arguments.Length != 0 && !secondary) return 2;
            temporary = Path.Combine(Path.GetTempPath(), "TabletDisplay-" + Guid.NewGuid().ToString("N"));
            Extract(temporary);
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(temporary, "Install.ps1") + "\"" +
                    (secondary ? " -PrepareSecondDisplay" : ""),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(start))
            {
                string errors = process.StandardError.ReadToEnd();
                process.WaitForExit();
                bool success = process.ExitCode == 0 || process.ExitCode == 10 || process.ExitCode == 11;
                string completed = process.ExitCode == 11 ? "更新完成。请从桌面重新打开「屏幕扩展控制台」。" :
                    secondary || process.ExitCode == 10 ? "第二块扩展屏的组件已安装。请返回屏幕扩展控制台连接设备。" :
                    "安装完成。请在手机或平板打开「屏幕扩展」，再从电脑的「屏幕扩展控制台」扫描设备。";
                MessageBox.Show(success ? completed : errors,
                    "屏幕扩展安装程序", MessageBoxButtons.OK, success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                return success ? 0 : process.ExitCode;
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "屏幕扩展安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            if (temporary != null && Directory.Exists(temporary))
            {
                string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                try
                {
                    if (Path.GetFullPath(temporary).StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileName(temporary).StartsWith("TabletDisplay-", StringComparison.Ordinal))
                        Directory.Delete(temporary, true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void Extract(string destination)
    {
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("输出目录已存在。请选择一个新目录。");
        using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
            archive.ExtractToDirectory(destination);
    }
}
