using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IceTube.Configuration;
using IceTube.Logging;
using IceTube.Models;
using IceTube.Services;

namespace IceTube
{
    public sealed class MainForm : Form
    {
        private readonly TextBox _urlTextBox;
        private readonly Button _playButton;
        private readonly Button _stopButton;
        private readonly Label _titleValue;
        private readonly Label _formatValue;
        private readonly Label _statusValue;
        private readonly IStreamResolver _resolver;
        private readonly PlayerService _player;
        private CancellationTokenSource _resolveCancellation;

        public MainForm()
        {
            Text = "IceTube v0.1";
            ClientSize = new Size(620, 286);
            MinimumSize = new Size(520, 325);
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;

            Label heading = new Label
            {
                Text = "IceTube v0.1 — L460 Mode",
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Location = new Point(18, 18)
            };

            Label urlLabel = new Label { Text = "YouTube URL:", AutoSize = true, Location = new Point(18, 57) };
            _urlTextBox = new TextBox
            {
                Location = new Point(18, 77),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Width = 584
            };

            _playButton = new Button { Text = "播放", Location = new Point(18, 112), Size = new Size(92, 30) };
            _stopButton = new Button { Text = "停止", Location = new Point(118, 112), Size = new Size(92, 30), Enabled = false };

            Label titleLabel = new Label { Text = "视频标题：", AutoSize = true, Location = new Point(18, 166) };
            _titleValue = new Label
            {
                Text = "—",
                AutoEllipsis = true,
                Location = new Point(92, 164),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Width = 510,
                Height = 22
            };

            Label formatLabel = new Label { Text = "当前格式：", AutoSize = true, Location = new Point(18, 199) };
            _formatValue = new Label { Text = "H.264 / ≤480p / ≤30fps", AutoSize = true, Location = new Point(92, 199) };

            Label statusLabel = new Label { Text = "状态：", AutoSize = true, Location = new Point(18, 232) };
            _statusValue = new Label
            {
                Text = "正在检查组件…",
                AutoEllipsis = true,
                Location = new Point(92, 230),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Width = 510,
                Height = 40
            };

            Controls.AddRange(new Control[]
            {
                heading, urlLabel, _urlTextBox, _playButton, _stopButton,
                titleLabel, _titleValue, formatLabel, _formatValue, statusLabel, _statusValue
            });

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            AppSettings settings = AppSettings.LoadOrCreate(baseDirectory);
            ToolLocator tools = new ToolLocator(baseDirectory, settings);
            _resolver = new YtDlpStreamResolver(settings, tools);
            _player = new PlayerService(tools);
            _player.PlayerExited += PlayerOnExited;

            _playButton.Click += async (sender, args) => await PlayAsync();
            _stopButton.Click += (sender, args) => StopPlayback();
            _urlTextBox.KeyDown += UrlTextBoxOnKeyDown;
            FormClosing += MainFormOnClosing;
            Shown += (sender, args) => CheckTools(tools);
        }

        private void CheckTools(ToolLocator tools)
        {
            IList<string> missing = tools.GetMissingTools();
            if (missing.Count == 0)
            {
                SetStatus("Ready — 输入 YouTube URL 后点击播放。", false);
                return;
            }

            _playButton.Enabled = false;
            SetStatus("缺少组件：" + string.Join("、", missing) + "。请重新复制完整发布目录。", true);
        }

        private async Task PlayAsync()
        {
            string url = _urlTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                SetStatus("请输入 YouTube URL。", true);
                _urlTextBox.Focus();
                return;
            }

            CancelResolution();
            _player.Stop();
            _resolveCancellation = new CancellationTokenSource();
            _playButton.Enabled = false;
            _stopButton.Enabled = true;
            _titleValue.Text = "正在获取视频信息…";
            _formatValue.Text = "—";
            SetStatus("Resolving — 正在选择 H.264 480p/30fps 以下格式…", false);

            try
            {
                VideoInfo video = await _resolver.ResolveAsync(url, _resolveCancellation.Token);
                _titleValue.Text = video.Title;
                _formatValue.Text = video.DisplayFormat;
                _player.Play(video);
                SetStatus("Playing — mpv 已启动，正在缓冲或播放。", false);
                _stopButton.Enabled = true;
            }
            catch (OperationCanceledException)
            {
                SetStatus("Ready — 已取消。", false);
                _stopButton.Enabled = false;
            }
            catch (StreamResolutionException ex)
            {
                LogService.Error("Stream resolution failed.", ex);
                SetStatus("Error — " + ex.Message, true);
                _stopButton.Enabled = false;
            }
            catch (Exception ex)
            {
                LogService.Error("Playback start failed.", ex);
                SetStatus("Error — 无法启动播放：" + ex.Message, true);
                _stopButton.Enabled = false;
            }
            finally
            {
                _playButton.Enabled = true;
            }
        }

        private void StopPlayback()
        {
            CancelResolution();
            _player.Stop();
            _stopButton.Enabled = false;
            _playButton.Enabled = true;
            SetStatus("Ready — 播放已停止。", false);
        }

        private void PlayerOnExited(object sender, PlayerExitedEventArgs eventArgs)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)(() =>
            {
                _stopButton.Enabled = false;
                _playButton.Enabled = true;
                if (eventArgs.WasStopped)
                {
                    SetStatus("Ready — 播放已停止。", false);
                }
                else if (eventArgs.ExitCode == 0)
                {
                    SetStatus("Ready — 播放结束。", false);
                }
                else if (!string.IsNullOrWhiteSpace(eventArgs.ErrorMessage))
                {
                    SetStatus("Error — " + eventArgs.ErrorMessage, true);
                }
                else
                {
                    SetStatus("Error — mpv 已退出（代码 " + eventArgs.ExitCode + "）。", true);
                }
            }));
        }

        private void UrlTextBoxOnKeyDown(object sender, KeyEventArgs eventArgs)
        {
            if (eventArgs.KeyCode != Keys.Enter || !_playButton.Enabled) return;
            eventArgs.SuppressKeyPress = true;
            _playButton.PerformClick();
        }

        private void SetStatus(string text, bool error)
        {
            _statusValue.Text = text;
            _statusValue.ForeColor = error ? Color.DarkRed : SystemColors.ControlText;
        }

        private void CancelResolution()
        {
            CancellationTokenSource cancellation = Interlocked.Exchange(ref _resolveCancellation, null);
            if (cancellation == null) return;
            cancellation.Cancel();
            cancellation.Dispose();
        }

        private void MainFormOnClosing(object sender, FormClosingEventArgs eventArgs)
        {
            CancelResolution();
            _player.Dispose();
        }
    }
}
