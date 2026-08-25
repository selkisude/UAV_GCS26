using GMap.NET;
using GMap.NET.WindowsForms;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace UAV_GCS
{
    public class UavMapMarker : GMapMarker
    {
        private readonly Color _color;

        public double BearingDegree { get; set; }

        public UavMapMarker(PointLatLng position, Color color) : base(position)
        {
            _color = color;

            Size = new Size(34, 34);
            Offset = new Point(-17, -17);
        }

        public override void OnRender(Graphics g)
        {
            GraphicsState state = g.Save();

            g.SmoothingMode = SmoothingMode.AntiAlias;

            g.TranslateTransform(LocalPosition.X, LocalPosition.Y);
            g.RotateTransform((float)BearingDegree);

            using (Brush brush = new SolidBrush(_color))
            using (Pen outlinePen = new Pen(Color.White, 2))
            {
                Point[] plane =
                {
                    new Point(0, -16),
                    new Point(-7, 7),
                    new Point(-16, 13),
                    new Point(-4, 15),
                    new Point(0, 8),
                    new Point(4, 15),
                    new Point(16, 13),
                    new Point(7, 7)
                };

                g.FillPolygon(brush, plane);
                g.DrawPolygon(outlinePen, plane);
            }

            g.Restore(state);
        }
    }
}