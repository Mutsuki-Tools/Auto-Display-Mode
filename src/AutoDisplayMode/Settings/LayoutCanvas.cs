using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using AutoDisplayMode.Core;

namespace AutoDisplayMode.Settings
{
    /// <summary>
    /// 螢幕配置畫布：依比例畫出接上的螢幕，開啟的螢幕依座標擺放，關閉的螢幕以灰色排在下方一列；
    /// 此模式沒有記錄的螢幕以橘色虛線框標示「此模式未設定」。
    /// 點一下選取；拖拉開啟的螢幕時靠近其他螢幕的邊會磁吸貼齊（按住 Alt 不貼齊），放開時觸發 <see cref="Moved"/>。
    /// 畫布只負責顯示與操作，螢幕設定由分頁寫回副本後再以 <see cref="SetItems"/> 交回。
    /// </summary>
    sealed class LayoutCanvas : Control
    {
        /// <summary>
        /// 畫布上的一台螢幕：識別、顯示的名稱、目前的螢幕設定，以及是否「此模式未設定」
        /// （此時 <paramref name="Shown"/> 是套用時依「未記錄的螢幕」會有的狀態）。
        /// </summary>
        public sealed record Item(MonitorId Id, string Label, MonitorSettings Shown, bool Unrecorded);

        /// <summary>桌面座標換算到畫布座標的比例與位移，以及每台螢幕在桌面座標中的矩形。</summary>
        sealed record View(double Scale, double OffsetX, double OffsetY, Dictionary<MonitorId, Rectangle> Rects)
        {
            public Rectangle ToScreen(Rectangle r) => Rectangle.FromLTRB(
                (int)Math.Round(r.Left * Scale + OffsetX), (int)Math.Round(r.Top * Scale + OffsetY),
                (int)Math.Round(r.Right * Scale + OffsetX), (int)Math.Round(r.Bottom * Scale + OffsetY));

            public Point ToWorld(Point p) => new((int)Math.Round((p.X - OffsetX) / Scale), (int)Math.Round((p.Y - OffsetY) / Scale));
        }

        IReadOnlyList<Item> items = Array.Empty<Item>();
        MonitorId? selected;

        // 拖拉中：拖拉的螢幕、按下時游標在螢幕內的位置（桌面座標）、目前的位置；拖拉期間比例固定，放開後才重新縮放
        MonitorId? dragging;
        Size grab;
        Point dragPos;
        Point downAt;
        bool moved;
        View? frozen;

        public LayoutCanvas()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            TabStop = true;
        }

        /// <summary>選取的螢幕改變（使用者點選）。</summary>
        public event Action? SelectionChanged;

        /// <summary>拖拉結束：螢幕與新的座標（桌面座標）。</summary>
        public event Action<MonitorId, int, int>? Moved;

        public bool IsDragging => dragging != null;

        public MonitorId? Selected
        {
            get => selected;
            set
            {
                selected = value;
                Invalidate();
            }
        }

        public void SetItems(IReadOnlyList<Item> list)
        {
            items = list;
            Invalidate();
        }

        // ------------------------------------------------------------
        //  版面
        // ------------------------------------------------------------

        /// <summary>邊距與磁吸距離：以字型高度為單位，跟著 DPI 放大。</summary>
        int Unit => Font.Height;

        View ComputeView()
        {
            var rects = new Dictionary<MonitorId, Rectangle>();
            List<Item> on = items.Where(i => i.Shown.Enabled).ToList();
            foreach (Item i in on) rects[i.Id] = new Rectangle(i.Shown.X, i.Shown.Y, Math.Max(1, i.Shown.DesktopWidth), Math.Max(1, i.Shown.DesktopHeight));

            Rectangle bounds = on.Count == 0 ? Rectangle.Empty : on.Select(i => rects[i.Id]).Aggregate(Rectangle.Union);

            // 關閉的螢幕排在下方一列（大小依解析度，座標沒有意義）
            int gap = Math.Max(200, bounds.Height / 6);
            int x = bounds.Left, y = on.Count == 0 ? 0 : bounds.Bottom + gap;
            foreach (Item i in items.Where(i => !i.Shown.Enabled))
            {
                var r = new Rectangle(x, y, Math.Max(800, i.Shown.DesktopWidth), Math.Max(450, i.Shown.DesktopHeight));
                rects[i.Id] = r;
                x = r.Right + gap;
            }

            Rectangle world = rects.Count == 0 ? new Rectangle(0, 0, 1920, 1080) : rects.Values.Aggregate(Rectangle.Union);
            int margin = Unit;
            double scale = Math.Min((Width - 2.0 * margin) / Math.Max(1, world.Width), (Height - 2.0 * margin) / Math.Max(1, world.Height));
            scale = Math.Max(scale, 0.001);
            double ox = (Width - world.Width * scale) / 2 - world.Left * scale;
            double oy = (Height - world.Height * scale) / 2 - world.Top * scale;
            return new View(scale, ox, oy, rects);
        }

        /// <summary>目前要畫的矩形（拖拉中的螢幕用拖拉位置）。</summary>
        Rectangle WorldRect(View v, Item i)
        {
            Rectangle r = v.Rects[i.Id];
            return i.Id == dragging ? new Rectangle(dragPos, r.Size) : r;
        }

        // ------------------------------------------------------------
        //  繪製
        // ------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.Clear(SystemColors.ControlLight);
            if (items.Count == 0)
            {
                TextRenderer.DrawText(g, "沒有偵測到螢幕", Font, ClientRectangle, SystemColors.GrayText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            View v = frozen ?? ComputeView();
            var on = items.Where(i => i.Shown.Enabled).Select(i => (Item: i, Rect: WorldRect(v, i))).ToList();
            var overlapping = new HashSet<MonitorId>();
            for (int a = 0; a < on.Count; a++)
                for (int b = a + 1; b < on.Count; b++)
                    if (on[a].Rect.IntersectsWith(on[b].Rect))
                    {
                        overlapping.Add(on[a].Item.Id);
                        overlapping.Add(on[b].Item.Id);
                    }

            // 選取的螢幕最後畫（在最上層）
            foreach (Item i in items.OrderBy(i => i.Id == selected))
                DrawMonitor(g, i, v.ToScreen(WorldRect(v, i)), overlapping.Contains(i.Id));
        }

        void DrawMonitor(Graphics g, Item item, Rectangle r, bool overlap)
        {
            MonitorSettings s = item.Shown;
            bool isSelected = item.Id == selected;
            r.Inflate(-1, -1);

            Color fill = !s.Enabled ? Color.Gainsboro
                : isSelected ? Color.FromArgb(0xC6, 0xDD, 0xF7)
                : item.Unrecorded ? Color.FromArgb(0xFD, 0xF1, 0xE0)
                : Color.FromArgb(0xEE, 0xF4, 0xFB);
            using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);

            int width = isSelected || overlap || item.Unrecorded ? Math.Max(2, Unit / 6) : 1;
            Color border = overlap ? Color.Firebrick
                : isSelected ? SystemColors.Highlight
                : item.Unrecorded ? Color.DarkOrange
                : SystemColors.ControlDark;
            using (var p = new Pen(border, width) { Alignment = PenAlignment.Inset })
            {
                if (!s.Enabled || item.Unrecorded) p.DashStyle = DashStyle.Dash;
                g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            }

            // 狀態只佔一列，小矩形中才不會被切掉（未設定的螢幕不會是主螢幕）
            string status = item.Unrecorded ? "此模式未設定" + (s.Enabled ? "" : "（關閉）")
                : !s.Enabled ? "（關閉）"
                : s.Primary ? "主螢幕"
                : "";
            string text = item.Label + "\n" + s.Width + " x " + s.Height + (status.Length > 0 ? "\n" + status : "");
            Rectangle textBounds = Rectangle.Inflate(r, -4, -2);
            TextRenderer.DrawText(g, text, Font, textBounds, s.Enabled ? SystemColors.ControlText : SystemColors.GrayText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        // ------------------------------------------------------------
        //  選取與拖拉
        // ------------------------------------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;

            View v = ComputeView();
            Item? hit = items.OrderBy(i => i.Id == selected).LastOrDefault(i => v.ToScreen(v.Rects[i.Id]).Contains(e.Location));
            if (hit?.Id != selected)
            {
                selected = hit?.Id;
                SelectionChanged?.Invoke();
                Invalidate();
            }
            if (hit == null || !hit.Shown.Enabled) return;

            Rectangle r = v.Rects[hit.Id];
            Point p = v.ToWorld(e.Location);
            dragging = hit.Id;
            grab = new Size(p.X - r.X, p.Y - r.Y);
            dragPos = r.Location;
            downAt = e.Location;
            moved = false;
            frozen = v;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging is not MonitorId id || frozen is not View v) return;
            Size drag = SystemInformation.DragSize;
            if (!moved && Math.Abs(e.X - downAt.X) < drag.Width && Math.Abs(e.Y - downAt.Y) < drag.Height) return;
            moved = true;

            Point p = v.ToWorld(e.Location);
            var candidate = new Rectangle(p.X - grab.Width, p.Y - grab.Height, v.Rects[id].Width, v.Rects[id].Height);
            dragPos = (ModifierKeys & Keys.Alt) != 0
                ? candidate.Location
                : Snap(candidate, items.Where(i => i.Shown.Enabled && i.Id != id).Select(i => v.Rects[i.Id]), (int)(Unit / v.Scale));
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging is not MonitorId id) return;
            bool wasMoved = moved;
            EndDrag();
            if (wasMoved) Moved?.Invoke(id, dragPos.X, dragPos.Y);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && dragging != null) EndDrag();   // 拖拉被中斷（例如跳出其他視窗）：放棄這次拖拉
        }

        void EndDrag()
        {
            dragging = null;
            frozen = null;
            moved = false;
            Capture = false;
            Invalidate();
        }

        /// <summary>
        /// 磁吸：兩個方向各自找最近的貼齊位置（左右邊緊鄰其他螢幕的邊，或與其他螢幕的邊對齊），
        /// 距離在 <paramref name="threshold"/>（桌面像素）內才貼齊。
        /// </summary>
        static Point Snap(Rectangle moving, IEnumerable<Rectangle> others, int threshold)
        {
            List<Rectangle> list = others.ToList();
            int x = SnapAxis(moving.X, moving.Width, list.Select(o => (o.Left, o.Right)), threshold);
            int y = SnapAxis(moving.Y, moving.Height, list.Select(o => (o.Top, o.Bottom)), threshold);
            return new Point(x, y);
        }

        static int SnapAxis(int pos, int size, IEnumerable<(int Start, int End)> others, int threshold)
        {
            int best = pos, bestDistance = threshold + 1;
            foreach ((int start, int end) in others)
            {
                foreach (int candidate in new[] { end, start - size, start, end - size })
                {
                    int d = Math.Abs(candidate - pos);
                    if (d < bestDistance)
                    {
                        best = candidate;
                        bestDistance = d;
                    }
                }
            }
            return best;
        }
    }
}
