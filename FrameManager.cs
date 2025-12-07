using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Input;
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
        public Avalonia.Media.IBrush? BackdropBrush { get; set; }
        public Avalonia.Media.Imaging.Bitmap? BackdropBitmap { get; set; }
        public bool UseNinePatch { get; set; } = false;
        public (int left, int right, int top, int bottom)? NinePatchInsets { get; set; }
        public bool TileBackdrop { get; set; } = false;

        // When using nine-patch, these are the 9 child images (TL, T, TR, L, C, R, BL, B, BR)
        public Avalonia.Controls.Image[]? NineRects { get; set; }
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
            NineRects = null;
        }
    }

    public class FrameManager
    {
        private readonly Canvas _canvas;
        private readonly List<VisualFrame> _frames = new();
        private DateTime _lastUpdate = DateTime.UtcNow;
        private VisualFrame? _hoveredFrame;

        public IReadOnlyList<VisualFrame> Frames => _frames.AsReadOnly();

        public FrameManager(Canvas canvas)
        {
            _canvas = canvas;
            // Start a simple update loop to call OnUpdate closures on frames
            DispatcherTimer.Run(() =>
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var delta = (now - _lastUpdate).TotalSeconds;
                    _lastUpdate = now;

                    foreach (var f in _frames)
                    {
                        if (f.OnUpdate != null)
                        {
                            try { f.Owner.InvokeClosure(f.OnUpdate, delta); } catch { }
                        }
                    }
                }
                catch { }
                return true; // keep running
            }, TimeSpan.FromMilliseconds(100));

            // Pointer handlers on the canvas to detect enter/leave and clicks
            _canvas.PointerMoved += (s, e) =>
            {
                try
                {
                    var p = e.GetPosition(_canvas);
                    var hit = HitTest(p);
                    if (hit != _hoveredFrame)
                    {
                        if (_hoveredFrame != null)
                        {
                            try { if (_hoveredFrame.OnLeave != null) _hoveredFrame.Owner.InvokeClosure(_hoveredFrame.OnLeave); } catch { }
                        }
                        _hoveredFrame = hit;
                        if (hit != null)
                        {
                            try { if (hit.OnEnter != null) hit.Owner.InvokeClosure(hit.OnEnter); } catch { }
                        }
                    }
                }
                catch { }
            };

            _canvas.PointerPressed += (s, e) =>
            {
                try
                {
                    var p = e.GetPosition(_canvas);
                    var hit = HitTest(p);
                    if (hit != null)
                    {
                        try { if (hit.OnClick != null) hit.Owner.InvokeClosure(hit.OnClick); } catch { }
                    }
                }
                catch { }
            };
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

                // Note: pointer events are handled at the canvas level for hit-testing
            });

            return vf;
        }

        public void UpdateVisual(VisualFrame vf)
        {
            if (vf.Visual == null) return;
            Console.WriteLine($"[FrameManager] UpdateVisual called (sync) for frame {vf.Id}");
            Dispatcher.UIThread.Post(() =>
            {
                vf.Visual.Width = vf.Width;
                vf.Visual.Height = vf.Height;
                Canvas.SetLeft(vf.Visual, vf.X);
                Canvas.SetTop(vf.Visual, vf.Y);
                vf.Visual.IsVisible = vf.Visible;
                vf.Visual.Opacity = vf.Opacity;
                // If using nine-patch bitmap rendering, create/update 9 child rects
                if (vf.UseNinePatch && vf.BackdropBitmap != null && vf.NinePatchInsets.HasValue)
                {
                    Console.WriteLine($"[FrameManager] UpdateVisual for frame {vf.Id}: UseNinePatch={vf.UseNinePatch} Bitmap={(vf.BackdropBitmap != null ? vf.BackdropBitmap.PixelSize.ToString() : "null")} Insets={vf.NinePatchInsets}");
                    // remove main visual fill so the nine rects show
                    vf.Visual.Fill = Brushes.Transparent;

                    // ensure NineRects exist
                    if (vf.NineRects == null)
                    {
                        Console.WriteLine($"[FrameManager] Creating 9 slice images for frame {vf.Id}");
                        vf.NineRects = new Avalonia.Controls.Image[9];
                        for (int i = 0; i < 9; i++)
                        {
                            var im = new Avalonia.Controls.Image { Stretch = Avalonia.Media.Stretch.Fill };
                            vf.NineRects[i] = im;
                            _canvas.Children.Add(im);
                        }
                    }

                    // compute slices in pixels based on bitmap size
                    var bmp = vf.BackdropBitmap;
                    int bmpW = bmp.PixelSize.Width;
                    int bmpH = bmp.PixelSize.Height;
                    var insets = vf.NinePatchInsets.Value;

                    int left = Math.Max(0, insets.left);
                    int right = Math.Max(0, insets.right);
                    int top = Math.Max(0, insets.top);
                    int bottom = Math.Max(0, insets.bottom);

                    // source rectangles
                    var src = new Avalonia.PixelRect[9];
                    // TL
                    src[0] = new Avalonia.PixelRect(0, 0, left, top);
                    // T
                    src[1] = new Avalonia.PixelRect(left, 0, Math.Max(0, bmpW - left - right), top);
                    // TR
                    src[2] = new Avalonia.PixelRect(Math.Max(0, bmpW - right), 0, right, top);
                    // L
                    src[3] = new Avalonia.PixelRect(0, top, left, Math.Max(0, bmpH - top - bottom));
                    // C
                    src[4] = new Avalonia.PixelRect(left, top, Math.Max(0, bmpW - left - right), Math.Max(0, bmpH - top - bottom));
                    // R
                    src[5] = new Avalonia.PixelRect(Math.Max(0, bmpW - right), top, right, Math.Max(0, bmpH - top - bottom));
                    // BL
                    src[6] = new Avalonia.PixelRect(0, Math.Max(0, bmpH - bottom), left, bottom);
                    // B
                    src[7] = new Avalonia.PixelRect(left, Math.Max(0, bmpH - bottom), Math.Max(0, bmpW - left - right), bottom);
                    // BR
                    src[8] = new Avalonia.PixelRect(Math.Max(0, bmpW - right), Math.Max(0, bmpH - bottom), right, bottom);

                    // target rectangles layout
                    double x = vf.X;
                    double y = vf.Y;
                    double w = vf.Width;
                    double h = vf.Height;

                    double leftW = left;
                    double rightW = right;
                    double topH = top;
                    double bottomH = bottom;

                    // clamp to available size
                    leftW = Math.Min(leftW, w / 2.0);
                    rightW = Math.Min(rightW, w / 2.0);
                    topH = Math.Min(topH, h / 2.0);
                    bottomH = Math.Min(bottomH, h / 2.0);

                    var dst = new Rect[9];
                    // TL
                    dst[0] = new Rect(x, y, leftW, topH);
                    // T
                    dst[1] = new Rect(x + leftW, y, Math.Max(0, w - leftW - rightW), topH);
                    // TR
                    dst[2] = new Rect(x + w - rightW, y, rightW, topH);
                    // L
                    dst[3] = new Rect(x, y + topH, leftW, Math.Max(0, h - topH - bottomH));
                    // C
                    dst[4] = new Rect(x + leftW, y + topH, Math.Max(0, w - leftW - rightW), Math.Max(0, h - topH - bottomH));
                    // R
                    dst[5] = new Rect(x + w - rightW, y + topH, rightW, Math.Max(0, h - topH - bottomH));
                    // BL
                    dst[6] = new Rect(x, y + h - bottomH, leftW, bottomH);
                    // B
                    dst[7] = new Rect(x + leftW, y + h - bottomH, Math.Max(0, w - leftW - rightW), bottomH);
                    // BR
                    dst[8] = new Rect(x + w - rightW, y + h - bottomH, rightW, bottomH);

                    // apply each slice
                    for (int i = 0; i < 9; i++)
                    {
                        var img = vf.NineRects[i];
                        var srect = src[i];
                        var drect = dst[i];

                        img.Width = drect.Width;
                        img.Height = drect.Height;
                        Canvas.SetLeft(img, drect.X);
                        Canvas.SetTop(img, drect.Y);
                        img.IsVisible = vf.Visible && drect.Width > 0 && drect.Height > 0;

                        try
                        {
                            if (srect.Width > 0 && srect.Height > 0)
                            {
                                var cropped = new Avalonia.Media.Imaging.CroppedBitmap(bmp, srect);
                                img.Source = cropped;
                                Console.WriteLine($"[FrameManager] NinePatch slice {i}: src={srect} dst={drect} loaded");
                            }
                            else
                            {
                                img.Source = null;
                                Console.WriteLine($"[FrameManager] NinePatch slice {i}: empty source");
                            }
                        }
                        catch (Exception ex)
                        {
                            img.Source = null;
                            Console.WriteLine($"[FrameManager] NinePatch slice {i} failed: {ex.Message}");
                        }
                    }
                }
                else
                {
                    // Apply backdrop brush if set
                    if (vf.BackdropBrush != null)
                    {
                        vf.Visual.Fill = vf.BackdropBrush;
                    }
                    else
                    {
                        vf.Visual.Fill = Brushes.LightGray;
                    }
                }

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
