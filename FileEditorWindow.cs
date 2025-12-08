using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using System;
using System.IO;

namespace Flux
{
    public class FileEditorWindow : Window
    {
        private readonly string _filePath;
        private readonly Action<string>? _onSave;
        private TextBox _editor = null!;

        public FileEditorWindow(string filePath, string initialContent, Action<string>? onSave = null)
        {
            _filePath = filePath;
            _onSave = onSave;
            this.Title = "Editor - " + Path.GetFileName(filePath);
            this.Width = 800;
            this.Height = 600;

            var root = new StackPanel { Orientation = Orientation.Vertical };
            _editor = new TextBox { AcceptsReturn = true, Text = initialContent };
            _editor.Height = 520;

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var saveBtn = new Button { Content = "Save", Width = 100, Margin = new Thickness(6) };
            var closeBtn = new Button { Content = "Close", Width = 100, Margin = new Thickness(6) };
            saveBtn.Click += SaveBtn_Click;
            closeBtn.Click += CloseBtn_Click;

            btnPanel.Children.Add(saveBtn);
            btnPanel.Children.Add(closeBtn);

            root.Children.Add(_editor);
            root.Children.Add(btnPanel);

            this.Content = root;
        }

        private void CloseBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            this.Close();
        }

        private void SaveBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            try
            {
                File.WriteAllText(_filePath, _editor.Text ?? string.Empty);
                _onSave?.Invoke(_filePath);
                this.Close();
            }
            catch (Exception ex)
            {
                try { var mw = this.Owner as MainWindow; mw?.AppendToConsole("[Editor] Save failed: " + ex.Message); } catch { }
            }
        }
    }
}
