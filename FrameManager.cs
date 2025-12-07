using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia;
using Avalonia.Threading;
using MoonSharp.Interpreter;

namespace Flux
{
    public class VisualFrame
    {
        public string Id { get; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 100;
        public double Height { get; set; } = 50;
        public bool Visible { get; set; } = true;
        public Closure? OnClick { get; set; }
        public Closure? OnUpdate { get; set; }
        public Closure? OnEnter { get; set; }
        public Closure? OnLeave { get; set; }
        public double Opacity { get; set; } = 1.0;
        public string? Text { get; set; }
        public double FontSize { get; set; } = 12.0;
        public LuaRunner Owner { get; }

        public Rectangle Visual { get; set; }
        public TextBlock? VisualText { get; set; }

        public VisualFrame(string id, LuaRunner owner)
        {
            Id = id;
            Owner = owner;
            Visual = new Rectangle
            {
                Fill = Brushes.LightGray,
                Stroke = Brushes.DarkGray,
                StrokeThickness = 1
            };
            VisualText = new TextBlock
            {
                Text = string.Empty,
                Foreground = Brushes.Black,
                FontSize = FontSize
            };
        }
    }

    public class FrameManager
    {
        private readonly Canvas _canvas;
        private readonly List<VisualFrame> _frames = new();

        public IReadOnlyList<VisualFrame> Frames => _frames.AsReadOnly();

        public FrameManager(Canvas canvas)
        {
            _canvas = canvas;
            // Start a simple update loop to call OnUpdate closures on frames
            DispatcherTimer.Run(() =>
            {
                try
                {
                    foreach (var f in _frames)
                    {
                        if (f.OnUpdate != null)
                        {
                            try { f.Owner.InvokeClosure(f.OnUpdate); } catch { }
                        }
                    }
                }
                catch { }
                return true; // keep running
            }, TimeSpan.FromMilliseconds(100));
        }

        public VisualFrame CreateFrame(LuaRunner owner)
        {
            var id = Guid.NewGuid().ToString("N");
            var vf = new VisualFrame(id, owner);
            _frames.Add(vf);

            Dispatcher.UIThread.Post(() =>
            {
                _canvas.Children.Add(vf.Visual);
                if (vf.VisualText != null)
                    _canvas.Children.Add(vf.VisualText);
                UpdateVisual(vf);
            });

            return vf;
        }

        public void UpdateVisual(VisualFrame vf)
        {
            if (vf.Visual == null) return;
            Dispatcher.UIThread.Post(() =>
            {
                vf.Visual.Width = vf.Width;
                vf.Visual.Height = vf.Height;
                Canvas.SetLeft(vf.Visual, vf.X);
                Canvas.SetTop(vf.Visual, vf.Y);
                vf.Visual.IsVisible = vf.Visible;
                vf.Visual.Opacity = vf.Opacity;

                // Update text overlay
                if (vf.VisualText != null)
                {
                    vf.VisualText.Text = vf.Text ?? string.Empty;
                    vf.VisualText.FontSize = vf.FontSize;
                    Canvas.SetLeft(vf.VisualText, vf.X + 4);
                    Canvas.SetTop(vf.VisualText, vf.Y + 4);
                    vf.VisualText.IsVisible = vf.Visible && !string.IsNullOrEmpty(vf.VisualText.Text);
                }
            });
        }

        public VisualFrame? HitTest(Point p)
        {
            // Find topmost frame containing the point
            for (int i = _frames.Count - 1; i >= 0; i--)
            {
                var f = _frames[i];
                if (!f.Visible) continue;
                if (p.X >= f.X && p.X <= f.X + f.Width && p.Y >= f.Y && p.Y <= f.Y + f.Height)
                    return f;
            }
            return null;
        }

        public VisualFrame? FindById(string id) => _frames.FirstOrDefault(f => f.Id == id);

        public Size GetCanvasSize()
        {
            return new Size(_canvas.Bounds.Width, _canvas.Bounds.Height);
        }
    }
}
