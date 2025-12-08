using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System;
using System.IO;
using System.Linq;

namespace Flux
{
    public partial class LogViewerWindow : Window
    {
        private ListBox? _filesList;
        private TextBox? _contentBox;

        public LogViewerWindow()
        {
            InitializeComponent();
            _filesList = this.FindControl<ListBox>("LogsList");
            _contentBox = this.FindControl<TextBox>("LogContent");

            var refresh = this.FindControl<Button>("RefreshLogsButton");
            if (refresh != null) refresh.Click += (_, __) => RefreshLogs();

            if (_filesList != null) _filesList.DoubleTapped += (s, e) => OpenSelected();

            RefreshLogs();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void RefreshLogs()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? Directory.GetCurrentDirectory();
                var logDir = Path.Combine(baseDir, "flux_logs");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                var files = Directory.GetFiles(logDir, "*.log").OrderByDescending(f => File.GetLastWriteTimeUtc(f)).ToArray();
                if (_filesList != null) _filesList.ItemsSource = files;
            }
            catch (Exception ex)
            {
                if (_contentBox != null) _contentBox.Text = "Failed to list logs: " + ex.Message;
            }
        }

        private void OpenSelected()
        {
            try
            {
                var sel = _filesList?.SelectedItem as string;
                if (string.IsNullOrEmpty(sel)) return;
                var content = File.ReadAllText(sel);
                if (_contentBox != null) _contentBox.Text = content;
            }
            catch (Exception ex)
            {
                if (_contentBox != null) _contentBox.Text = "Failed to read file: " + ex.Message;
            }
        }
    }
}
