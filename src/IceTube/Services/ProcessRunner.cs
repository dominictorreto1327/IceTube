using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IceTube.Services
{
    internal sealed class ProcessResult
    {
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; }
        public string StandardError { get; set; }
    }

    internal sealed class ProcessRunner
    {
        public async Task<ProcessResult> RunAsync(
            string executablePath,
            IEnumerable<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = WindowsCommandLine.Join(arguments),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };

            using (Process process = new Process { StartInfo = startInfo })
            using (ProcessJob job = new ProcessJob())
            {
                try
                {
                    if (!process.Start()) throw new InvalidOperationException("无法启动外部工具。");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("无法启动 " + executablePath + "。", ex);
                }

                job.TryAssign(process);
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                Task waitTask = Task.Run(() => process.WaitForExit());
                Task timeoutTask = Task.Delay(timeout, cancellationToken);

                Task completed = await Task.WhenAny(waitTask, timeoutTask).ConfigureAwait(false);
                if (completed != waitTask)
                {
                    TryKill(process);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("外部工具运行超时。");
                }

                await waitTask.ConfigureAwait(false);
                string stdout = await stdoutTask.ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                return new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = stdout,
                    StandardError = stderr
                };
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch
            {
                // The job object will also terminate descendants when disposed.
            }
        }
    }
}
