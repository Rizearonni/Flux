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
        public LuaRunner Owner { get; }

        public Rectangle Visual { get; set; }

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
        }

        public VisualFrame CreateFrame(LuaRunner owner)
        {
            var id = Guid.NewGuid().ToString("N");
            var vf = new VisualFrame(id, owner);
            _frames.Add(vf);

            Dispatcher.UIThread.Post(() =>
            {
                _canvas.Children.Add(vf.Visual);
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
