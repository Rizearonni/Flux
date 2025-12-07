using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace Flux
{
    public partial class MainWindow : Window
    {
        private AddonManager? _addonManager;
        private FrameManager? _frameManager;
        private VisualFrame? _draggingFrame;
        private Avalonia.Point _dragOffset;
        private bool _dragMoved = false;
        private VisualFrame? _selectedFrame;

        public MainWindow()
        {
            InitializeComponent();
            LoadLogo();

            var preview = this.FindControl<Canvas>("PreviewCanvas");
            if (preview != null)
            {
                _frameManager = new FrameManager(preview);
                preview.PointerPressed += Preview_PointerPressed;
                preview.PointerMoved += Preview_PointerMoved;
                preview.PointerReleased += Preview_PointerReleased;
            }

            _addonManager = new AddonManager(_frameManager);

            // Wire buttons null-safely (explicit checks to avoid preview language features)
            var inspectorApplyBtn = this.FindControl<Button>("InspectorApply");
            if (inspectorApplyBtn != null) inspectorApplyBtn.Click += InspectorApply_Click;
            var loadAddonBtn = this.FindControl<Button>("LoadAddonButton");
            if (loadAddonBtn != null) loadAddonBtn.Click += LoadAddonButton_Click;
            var openSampleBtn = this.FindControl<Button>("OpenSampleButton");
            if (openSampleBtn != null) openSampleBtn.Click += OpenSampleButton_Click;
            var fireEventBtn = this.FindControl<Button>("FireEventButton");
            if (fireEventBtn != null) fireEventBtn.Click += FireEventButton_Click;
            var fireCustomBtn = this.FindControl<Button>("FireCustomButton");
            if (fireCustomBtn != null) fireCustomBtn.Click += FireCustomButton_Click;

            var runBtn = this.FindControl<Button>("RunButton");
            if (runBtn != null) runBtn.Click += RunButton_Click;
            var stopBtn = this.FindControl<Button>("StopButton");
            if (stopBtn != null) stopBtn.Click += StopButton_Click;
            var reloadBtn = this.FindControl<Button>("ReloadButton");
            if (reloadBtn != null) reloadBtn.Click += ReloadButton_Click;
            var openBtn = this.FindControl<Button>("OpenButton");
            if (openBtn != null) openBtn.Click += OpenButton_Click;
            var settingsBtn = this.FindControl<Button>("SettingsButton");
            if (settingsBtn != null) settingsBtn.Click += SettingsButton_Click;
        }

        private void LoadLogo()
        {
            try
            {
                var logoImg = this.FindControl<Image>("LogoImage");
                var logoText = this.FindControl<TextBlock>("LogoText");
                var logoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets", "logo.png");
                logoPath = Path.GetFullPath(logoPath);
                if (File.Exists(logoPath) && logoImg != null)
                {
                    logoImg.Source = new Avalonia.Media.Imaging.Bitmap(logoPath);
                    if (logoText != null) logoText.IsVisible = false;
                }
            }
            catch { }
        }

        private void AppendToConsole(string text)
        {
            var tb = this.FindControl<TextBox>("ConsoleTextBox");
            if (tb == null) return;
            tb.Text += text + "\r\n";
            tb.CaretIndex = tb.Text.Length;
        }

        private void LuaRunner_OnOutput(object? sender, string e)
        {
            Dispatcher.UIThread.Post(() => AppendToConsole(e));
        }

        private void LoadAddonButton_Click(object? sender, RoutedEventArgs e)
        {
            AppendToConsole("[Info] Load addon - feature not implemented yet.");
        }

        private void OpenSampleButton_Click(object? sender, RoutedEventArgs e)
        {
            var folder = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "sample_addons", "SimpleHello"));
            if (!Directory.Exists(folder))
            {
                AppendToConsole($"[Error] Sample addon folder not found: {folder}");
                return;
            }

            AppendToConsole("[Info] Loading sample addon folder...");
            var runner = _addonManager?.LoadAddonFromFolder(folder, LuaRunner_OnOutput);
            if (runner != null)
            {
                _addonManager?.TriggerEvent("PLAYER_LOGIN");
                _addonManager?.SaveSavedVariables(runner.AddonName);
            }
        }

        private void FireEventButton_Click(object? sender, RoutedEventArgs e)
        {
            var combo = this.FindControl<ComboBox>("EventCombo");
            if (combo == null) return;
            var item = combo.SelectedItem as ComboBoxItem;
            var evName = item?.Content?.ToString();
            if (string.IsNullOrEmpty(evName))
            {
                AppendToConsole("[Info] No event selected.");
                return;
            }

            var args = GetEventArgsFromInputs();
            AppendToConsole($"[Info] Firing event: {evName} with args: {FormatArgs(args)}");
            _addonManager?.TriggerEvent(evName, args);
        }

        private void FireCustomButton_Click(object? sender, RoutedEventArgs e)
        {
            var tb = this.FindControl<TextBox>("CustomEventText");
            if (tb == null) return;
            var evName = tb.Text?.Trim();
            if (string.IsNullOrEmpty(evName))
            {
                AppendToConsole("[Info] Custom event name is empty.");
                return;
            }

            var args = GetEventArgsFromInputs();
            AppendToConsole($"[Info] Firing custom event: {evName} with args: {FormatArgs(args)}");
            _addonManager?.TriggerEvent(evName, args);
        }

        private void Preview_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var preview = this.FindControl<Canvas>("PreviewCanvas");
            if (preview == null) return;

            var pos = e.GetPosition(preview);
            var vf = _frameManager?.HitTest(pos);
            if (vf == null)
            {
                AppendToConsole("[Info] Clicked empty area.");
                return;
            }

            _draggingFrame = vf;
            _dragOffset = new Avalonia.Point(pos.X - vf.X, pos.Y - vf.Y);
            _dragMoved = false;

            try { e.Pointer.Capture(preview); } catch { }

            if (vf.Visual != null)
            {
                vf.Visual.Stroke = Avalonia.Media.Brushes.DodgerBlue;
                vf.Visual.Fill = Avalonia.Media.Brushes.LightSteelBlue;
            }
            _selectedFrame = vf;
            UpdateInspector();

            AppendToConsole($"[Info] Pressed frame: {vf.Id}");
        }

        private void Preview_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (_draggingFrame == null) return;
            var preview = this.FindControl<Canvas>("PreviewCanvas");
            if (preview == null) return;
            var pos = e.GetPosition(preview);
            _dragMoved = true;
            _draggingFrame.X = pos.X - _dragOffset.X;
            _draggingFrame.Y = pos.Y - _dragOffset.Y;
            _frameManager?.UpdateVisual(_draggingFrame);
        }

        private void Preview_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_draggingFrame == null) return;
            try { e.Pointer.Capture(null); } catch { }

            if (!_dragMoved)
            {
                AppendToConsole($"[Info] Clicked frame: {_draggingFrame.Id}");
                if (_draggingFrame.OnClick != null)
                {
                    _draggingFrame.Owner.InvokeClosure(_draggingFrame.OnClick);
                }
            }

            if (_draggingFrame.Visual != null)
            {
                _draggingFrame.Visual.Stroke = Avalonia.Media.Brushes.DarkGray;
                _draggingFrame.Visual.Fill = Avalonia.Media.Brushes.LightGray;
            }

            _draggingFrame = null;
            _dragMoved = false;
            UpdateInspector();
        }

        private void UpdateInspector()
        {
            var idBox = this.FindControl<TextBox>("InspectorId");
            var xBox = this.FindControl<TextBox>("InspectorX");
            var yBox = this.FindControl<TextBox>("InspectorY");
            var wBox = this.FindControl<TextBox>("InspectorW");
            var hBox = this.FindControl<TextBox>("InspectorH");
            var vis = this.FindControl<CheckBox>("InspectorVisible");

            if (_selectedFrame == null)
            {
                if (idBox != null) idBox.Text = string.Empty;
                if (xBox != null) xBox.Text = string.Empty;
                if (yBox != null) yBox.Text = string.Empty;
                if (wBox != null) wBox.Text = string.Empty;
                if (hBox != null) hBox.Text = string.Empty;
                if (vis != null) vis.IsChecked = false;
                return;
            }

            if (idBox != null) idBox.Text = _selectedFrame.Id;
            if (xBox != null) xBox.Text = Math.Round(_selectedFrame.X, 1).ToString();
            if (yBox != null) yBox.Text = Math.Round(_selectedFrame.Y, 1).ToString();
            if (wBox != null) wBox.Text = Math.Round(_selectedFrame.Width, 1).ToString();
            if (hBox != null) hBox.Text = Math.Round(_selectedFrame.Height, 1).ToString();
            if (vis != null) vis.IsChecked = _selectedFrame.Visible;
        }

        private void InspectorApply_Click(object? sender, RoutedEventArgs e)
        {
            if (_selectedFrame == null) return;
            var xBox = this.FindControl<TextBox>("InspectorX");
            var yBox = this.FindControl<TextBox>("InspectorY");
            var wBox = this.FindControl<TextBox>("InspectorW");
            var hBox = this.FindControl<TextBox>("InspectorH");
            var vis = this.FindControl<CheckBox>("InspectorVisible");

            if (xBox != null && double.TryParse(xBox.Text, out var nx)) _selectedFrame.X = nx;
            if (yBox != null && double.TryParse(yBox.Text, out var ny)) _selectedFrame.Y = ny;
            if (wBox != null && double.TryParse(wBox.Text, out var nw)) _selectedFrame.Width = nw;
            if (hBox != null && double.TryParse(hBox.Text, out var nh)) _selectedFrame.Height = nh;
            if (vis != null && vis.IsChecked.HasValue) _selectedFrame.Visible = vis.IsChecked.Value;

            _frameManager?.UpdateVisual(_selectedFrame);
        }

        private object?[] GetEventArgsFromInputs()
        {
            var list = new System.Collections.Generic.List<object?>();
            var a1 = this.FindControl<TextBox>("Arg1Text")?.Text?.Trim();
            var a2 = this.FindControl<TextBox>("Arg2Text")?.Text?.Trim();
            var a3 = this.FindControl<TextBox>("Arg3Text")?.Text?.Trim();

            void tryAdd(string? s)
            {
                if (string.IsNullOrEmpty(s)) return;
                if (bool.TryParse(s, out var b)) { list.Add(b); return; }
                if (int.TryParse(s, out var i)) { list.Add(i); return; }
                if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)) { list.Add(d); return; }
                list.Add(s);
            }

            tryAdd(a1);
            tryAdd(a2);
            tryAdd(a3);

            return list.ToArray();
        }

        private string FormatArgs(object?[] args)
        {
            if (args == null || args.Length == 0) return "none";
            try
            {
                return string.Join(", ", System.Array.ConvertAll(args, a => a?.ToString() ?? "nil"));
            }
            catch { return "(error formatting args)"; }
        }

        // Toolbar handlers
        private void RunButton_Click(object? sender, RoutedEventArgs e)
        {
            AppendToConsole("[Toolbar] Run clicked");
        }

        private void StopButton_Click(object? sender, RoutedEventArgs e)
        {
            AppendToConsole("[Toolbar] Stop clicked");
        }

        private void ReloadButton_Click(object? sender, RoutedEventArgs e)
        {
            AppendToConsole("[Toolbar] Reload clicked — reloading addons");
            _addonManager?.TriggerEvent("PLAYER_LOGIN");
        }

        private async void OpenButton_Click(object? sender, RoutedEventArgs e)
        {
            AppendToConsole("[Toolbar] Open clicked — choose addon folder");
            try
            {
                // Try StorageProvider (Avalonia TopLevel.StorageProvider) via reflection for broad compat.
                var top = TopLevel.GetTopLevel(this);
                var storageProvider = top?.StorageProvider;
                bool handled = false;
                if (storageProvider != null)
                {
                    var mi = storageProvider.GetType().GetMethod("OpenFolderPickerAsync", BindingFlags.Public | BindingFlags.Instance) ??
                             storageProvider.GetType().GetMethod("OpenFolderPickerAsync");
                    if (mi != null)
                    {
                        var parameters = mi.GetParameters();
                        object? taskObj = null;
                        if (parameters.Length == 0)
                            taskObj = mi.Invoke(storageProvider, Array.Empty<object>());
                        else
                            taskObj = mi.Invoke(storageProvider, new object[] { this });

                        if (taskObj is Task task)
                        {
                            await task.ConfigureAwait(true);
                            var resultProp = task.GetType().GetProperty("Result");
                            if (resultProp != null)
                            {
                                var result = resultProp.GetValue(task);
                                var enumRes = result as System.Collections.IEnumerable;
                                if (enumRes != null)
                                {
                                    foreach (var item in enumRes)
                                    {
                                        string? path = null;
                                        var pathProp = item.GetType().GetProperty("Path") ?? item.GetType().GetProperty("FullPath") ?? item.GetType().GetProperty("LocalPath");
                                        if (pathProp != null)
                                        {
                                            path = pathProp.GetValue(item) as string;
                                        }
                                        if (string.IsNullOrEmpty(path)) path = item?.ToString();
                                        if (!string.IsNullOrEmpty(path))
                                        {
                                            AppendToConsole($"[Toolbar] Selected: {path}");
                                            _addonManager?.LoadAddonFromFolder(path, LuaRunner_OnOutput);
                                            handled = true;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (!handled)
                {
                    // Fallback to OpenFolderDialog (suppress obsolete warning locally)
#pragma warning disable CS0618
                    var dlg = new OpenFolderDialog();
                    var path = await dlg.ShowAsync(this);
#pragma warning restore CS0618
                    if (!string.IsNullOrEmpty(path))
                    {
                        AppendToConsole($"[Toolbar] Selected: {path}");
                        _addonManager?.LoadAddonFromFolder(path, LuaRunner_OnOutput);
                    }
                    else
                    {
                        AppendToConsole("[Toolbar] Open cancelled");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendToConsole($"[Toolbar] Open error: {ex.Message}");
            }
        }

        private void SettingsButton_Click(object? sender, RoutedEventArgs e)
        {
            AppendToConsole("[Toolbar] Settings clicked — (not implemented)");
        }
    }
}
