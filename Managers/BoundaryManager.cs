using GMap.NET;
using GMap.NET.WindowsForms;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using GMap.NET.WindowsForms.Markers;

namespace UAV_GCS
{
    public class BoundaryManager
    {
        private readonly GMapControl _map;
        private readonly ListBox _listBox;
        private readonly GMapOverlay _boundaryOverlay;
        private readonly GMapOverlay _hssOverlay;

        private readonly List<PointLatLng> _boundaryPoints = new List<PointLatLng>();

        private bool _drawOutsideMask = false;

        public BoundaryManager(GMapControl map, ListBox listBox)
        {
            _map = map;
            _listBox = listBox;

            _boundaryOverlay = new GMapOverlay("boundary");
            _map.Overlays.Add(_boundaryOverlay);
            _hssOverlay = new GMapOverlay("hss");
            _map.Overlays.Add(_hssOverlay);

            _listBox.SelectionMode = SelectionMode.One;

            _map.Paint += Map_Paint;
        }

        public void AddPoint(string latText, string lonText)
        {
            double lat = ParseCoordinate(latText);
            double lon = ParseCoordinate(lonText);

            PointLatLng point = new PointLatLng(lat, lon);

            _boundaryPoints.Add(point);
            RefreshList();
            _map.Invalidate();
        }

        public void RemoveSelectedPoint()
        {
            int selectedIndex = _listBox.SelectedIndex;

            if (selectedIndex < 0)
            {
                MessageBox.Show("Silmek için listeden bir nokta seç.");
                return;
            }

            _boundaryPoints.RemoveAt(selectedIndex);
            RefreshList();

            if (_boundaryPoints.Count >= 3)
                DrawOutsideBoundary();
            else
                ClearOnlyPolygon();
        }

        public void DrawBoundary()
        {
            _drawOutsideMask = false;
            _boundaryOverlay.Polygons.Clear();

            if (_boundaryPoints.Count < 3)
            {
                MessageBox.Show("Sınır çizmek için en az 3 nokta gerekli.");
                return;
            }

            List<PointLatLng> orderedPoints = SortPointsClockwise(_boundaryPoints);

            List<PointLatLng> closedPoints = new List<PointLatLng>(orderedPoints);
            closedPoints.Add(closedPoints[0]);

            GMapPolygon polygon = new GMapPolygon(closedPoints, "Uçuş Sınırı")
            {
                Stroke = new Pen(Color.Red, 3),
                Fill = new SolidBrush(Color.FromArgb(25, Color.Red))
            };

            _boundaryOverlay.Polygons.Add(polygon);

            CenterMapToBoundary();
            _map.Invalidate();
        }

        public void DrawOutsideBoundary()
        {
            _boundaryOverlay.Polygons.Clear();

            if (_boundaryPoints.Count < 3)
            {
                MessageBox.Show("Sınır çizmek için en az 3 nokta gerekli.");
                return;
            }

            _drawOutsideMask = true;

            List<PointLatLng> orderedPoints = SortPointsClockwise(_boundaryPoints);

            List<PointLatLng> closedBoundary = new List<PointLatLng>(orderedPoints);
            closedBoundary.Add(closedBoundary[0]);

            GMapPolygon boundaryLine = new GMapPolygon(closedBoundary, "Uçuş Sınırı")
            {
                Stroke = new Pen(Color.Red, 3),
                Fill = new SolidBrush(Color.Transparent)
            };

            _boundaryOverlay.Polygons.Add(boundaryLine);

            CenterMapToBoundary();
            _map.Invalidate();
        }

        private void Map_Paint(object sender, PaintEventArgs e)
        {
            if (!_drawOutsideMask)
                return;

            if (_boundaryPoints.Count < 3)
                return;

            List<PointLatLng> orderedPoints = SortPointsClockwise(_boundaryPoints);

            Point[] screenPoints = orderedPoints
                .Select(p =>
                {
                    GPoint gPoint = _map.FromLatLngToLocal(p);
                    return new Point((int)gPoint.X, (int)gPoint.Y);
                })
                .ToArray();

            if (screenPoints.Length < 3)
                return;

            using (GraphicsPath boundaryPath = new GraphicsPath())
            using (Region outsideRegion = new Region(new Rectangle(0, 0, _map.Width, _map.Height)))
            using (SolidBrush maskBrush = new SolidBrush(Color.FromArgb(120, Color.Black)))
            {
                boundaryPath.AddPolygon(screenPoints);

                outsideRegion.Exclude(boundaryPath);

                e.Graphics.FillRegion(maskBrush, outsideRegion);
            }
        }

        public void ClearBoundary()
        {
            _drawOutsideMask = false;
            _boundaryPoints.Clear();
            _boundaryOverlay.Polygons.Clear();
            RefreshList();
            _map.Invalidate();
        }

        private void ClearOnlyPolygon()
        {
            _drawOutsideMask = false;
            _boundaryOverlay.Polygons.Clear();
            _map.Invalidate();
        }

        private void RefreshList()
        {
            _listBox.Items.Clear();

            for (int i = 0; i < _boundaryPoints.Count; i++)
            {
                PointLatLng point = _boundaryPoints[i];

                _listBox.Items.Add(
                    $"{i + 1}) {point.Lat:0.000000}, {point.Lng:0.000000}"
                );
            }
        }

        private void CenterMapToBoundary()
        {
            if (_boundaryPoints.Count == 0)
                return;

            double avgLat = _boundaryPoints.Average(p => p.Lat);
            double avgLng = _boundaryPoints.Average(p => p.Lng);

            _map.Position = new PointLatLng(avgLat, avgLng);
            _map.Zoom = 15;
        }

        private List<PointLatLng> SortPointsClockwise(List<PointLatLng> points)
        {
            double centerLat = points.Average(p => p.Lat);
            double centerLng = points.Average(p => p.Lng);

            return points
                .OrderBy(p => Math.Atan2(p.Lat - centerLat, p.Lng - centerLng))
                .ToList();
        }

        private double ParseCoordinate(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Koordinat boş olamaz.");

            text = text.Replace(",", ".");

            return double.Parse(text, CultureInfo.InvariantCulture);
        }
        public void DrawSampleHssAreas()
        {
            _hssOverlay.Polygons.Clear();

            AddHssCircle(0, 39.902435, 41.235321, 250);
            AddHssCircle(1, 39.903831, 41.253074, 100);
            AddHssCircle(2, 39.896428, 41.236293, 320);

            _map.Invalidate();
        }

        private void AddHssCircle(int id, double lat, double lng, double radiusMeters)
        {
            List<PointLatLng> circlePoints = CreateCirclePoints(lat, lng, radiusMeters, 72);

            GMapPolygon circle = new GMapPolygon(circlePoints, $"HSS {id}")
            {
                Stroke = new Pen(Color.Red, 3),
                Fill = new SolidBrush(Color.FromArgb(70, Color.Red))
            };

            _hssOverlay.Polygons.Add(circle);

            PointLatLng center = new PointLatLng(lat, lng);

            GMapMarker marker = new GMarkerGoogle(center, GMarkerGoogleType.red_dot)
            {
                ToolTipText = $"HSS {id}\nYarıçap: {radiusMeters:0} m",
                ToolTipMode = MarkerTooltipMode.Always
            };

            _hssOverlay.Markers.Add(marker);
        }

        private List<PointLatLng> CreateCirclePoints(double centerLat, double centerLng, double radiusMeters, int segments)
        {
            List<PointLatLng> points = new List<PointLatLng>();

            double earthRadius = 6378137.0;
            double latRad = centerLat * Math.PI / 180.0;
            double lngRad = centerLng * Math.PI / 180.0;
            double angularDistance = radiusMeters / earthRadius;

            for (int i = 0; i <= segments; i++)
            {
                double angle = 2.0 * Math.PI * i / segments;

                double pointLatRad = Math.Asin(
                    Math.Sin(latRad) * Math.Cos(angularDistance) +
                    Math.Cos(latRad) * Math.Sin(angularDistance) * Math.Cos(angle)
                );

                double pointLngRad = lngRad + Math.Atan2(
                    Math.Sin(angle) * Math.Sin(angularDistance) * Math.Cos(latRad),
                    Math.Cos(angularDistance) - Math.Sin(latRad) * Math.Sin(pointLatRad)
                );

                points.Add(new PointLatLng(
                    pointLatRad * 180.0 / Math.PI,
                    pointLngRad * 180.0 / Math.PI
                ));
            }

            return points;
        }

        public void DrawHssAreasFromServer(List<HssInfoDto> hssAreas)
        {
            _hssOverlay.Polygons.Clear();
            _hssOverlay.Markers.Clear();

            if (hssAreas == null || hssAreas.Count == 0)
            {
                _map.Invalidate();
                return;
            }

            foreach (var hss in hssAreas)
            {
                AddHssCircle(
                    hss.id,
                    hss.hssEnlem,
                    hss.hssBoylam,
                    hss.hssYaricap
                );
            }

            CenterMapToHssAreas(hssAreas);

            _map.Invalidate();
        }

        private void CenterMapToHssAreas(List<HssInfoDto> hssAreas)
        {
            if (hssAreas == null || hssAreas.Count == 0)
                return;

            double avgLat = hssAreas.Average(h => h.hssEnlem);
            double avgLng = hssAreas.Average(h => h.hssBoylam);

            _map.Position = new PointLatLng(avgLat, avgLng);
            _map.Zoom = 15;
        }
    }
}