using GMap.NET;
using GMap.NET.WindowsForms;
using GMap.NET.WindowsForms.Markers;
using System.Collections.Generic;
using System.Drawing;

namespace UAV_GCS
{
    public class MapVehicleManager
    {
        private readonly GMapControl _map;
        private readonly GMapOverlay _vehicleOverlay;

        private UavMapMarker _ownUavMarker;
        private GMapMarker _qrMarker;

        private readonly Dictionary<int, UavMapMarker> _enemyMarkers = new Dictionary<int, UavMapMarker>();

        public bool FollowOwnUav { get; set; } = true;

        public MapVehicleManager(GMapControl map)
        {
            _map = map;

            _vehicleOverlay = new GMapOverlay("vehicles");
            _map.Overlays.Add(_vehicleOverlay);
        }

        public void UpdateOwnUav(TelemetryData telemetry)
        {
            if (telemetry == null)
                return;

            if (!IsValidCoordinate(telemetry.Latitude, telemetry.Longitude))
                return;

            PointLatLng position = new PointLatLng(
                telemetry.Latitude,
                telemetry.Longitude
            );

            if (_ownUavMarker == null)
            {
                _ownUavMarker = new UavMapMarker(position, Color.DodgerBlue)
                {
                    BearingDegree = telemetry.HeadingDegree,
                    ToolTipText = BuildOwnUavTooltip(telemetry),
                    ToolTipMode = MarkerTooltipMode.OnMouseOver
                };

                _vehicleOverlay.Markers.Add(_ownUavMarker);
            }
            else
            {
                _ownUavMarker.Position = position;
                _ownUavMarker.BearingDegree = telemetry.HeadingDegree;
                _ownUavMarker.ToolTipText = BuildOwnUavTooltip(telemetry);
            }

            if (FollowOwnUav)
                _map.Position = position;

            _map.Invalidate();
        }

        public void UpdateQrTarget(double lat, double lon)
        {
            if (!IsValidCoordinate(lat, lon))
                return;

            PointLatLng position = new PointLatLng(lat, lon);

            if (_qrMarker == null)
            {
                _qrMarker = new GMarkerGoogle(position, GMarkerGoogleType.yellow_dot)
                {
                    ToolTipText = "QR Hedef",
                    ToolTipMode = MarkerTooltipMode.OnMouseOver
                };

                _vehicleOverlay.Markers.Add(_qrMarker);
            }
            else
            {
                _qrMarker.Position = position;
            }

            _map.Invalidate();
        }

        public void ClearOwnUav()
        {
            if (_ownUavMarker != null)
            {
                _vehicleOverlay.Markers.Remove(_ownUavMarker);
                _ownUavMarker = null;
                _map.Invalidate();
            }
        }

        public void UpdateEnemyUavs(List<EnemyUavDto> enemies)
        {
            if (enemies == null)
                return;

            foreach (var enemy in enemies)
            {
                if (enemy == null)
                    continue;

                if (!IsValidCoordinate(enemy.iha_enlem, enemy.iha_boylam))
                    continue;

                PointLatLng position = new PointLatLng(
                    enemy.iha_enlem,
                    enemy.iha_boylam
                );

                string tooltip = BuildEnemyTooltip(enemy);

                if (!_enemyMarkers.ContainsKey(enemy.takim_numarasi))
                {
                    UavMapMarker marker = new UavMapMarker(position, Color.Red)
                    {
                        BearingDegree = enemy.iha_yonelme,
                        ToolTipText = tooltip,
                        ToolTipMode = MarkerTooltipMode.OnMouseOver
                    };

                    _enemyMarkers.Add(enemy.takim_numarasi, marker);
                    _vehicleOverlay.Markers.Add(marker);
                }
                else
                {
                    UavMapMarker marker = _enemyMarkers[enemy.takim_numarasi];

                    marker.Position = position;
                    marker.BearingDegree = enemy.iha_yonelme;
                    marker.ToolTipText = tooltip;
                }
            }

            _map.Invalidate();
        }

        public void ClearEnemyUavs()
        {
            foreach (var marker in _enemyMarkers.Values)
            {
                _vehicleOverlay.Markers.Remove(marker);
            }

            _enemyMarkers.Clear();
            _map.Invalidate();
        }

        private bool IsValidCoordinate(double lat, double lon)
        {
            if (lat == 0 && lon == 0)
                return false;

            if (lat < -90 || lat > 90)
                return false;

            if (lon < -180 || lon > 180)
                return false;

            return true;
        }

        private string BuildOwnUavTooltip(TelemetryData telemetry)
        {
            return
                "Kendi İHA\n" +
                $"Lat: {telemetry.Latitude:0.000000}\n" +
                $"Lon: {telemetry.Longitude:0.000000}\n" +
                $"İrtifa: {telemetry.RelativeAltitudeMeters:0} m\n" +
                $"Hız: {telemetry.GroundSpeed:0.0} m/s\n" +
                $"Yönelme: {telemetry.HeadingDegree:0}°\n" +
                $"Mod: {telemetry.FlightMode}";
        }

        private string BuildEnemyTooltip(EnemyUavDto enemy)
        {
            return
                $"Rakip İHA - Takım {enemy.takim_numarasi}\n" +
                $"Lat: {enemy.iha_enlem:0.000000}\n" +
                $"Lon: {enemy.iha_boylam:0.000000}\n" +
                $"İrtifa: {enemy.iha_irtifa:0} m\n" +
                $"Hız: {enemy.iha_hizi:0.0} m/s\n" +
                $"Yönelme: {enemy.iha_yonelme:0}°\n" +
                $"Zaman farkı: {enemy.zaman_farki} ms";
        }
    }
}