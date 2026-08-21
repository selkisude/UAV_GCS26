using System;

namespace UAV_GCS
{
    public class TelemetryRequestBuilder
    {
        private readonly TimeSyncManager _timeSyncManager;

        public TelemetryRequestBuilder(TimeSyncManager timeSyncManager)
        {
            _timeSyncManager = timeSyncManager;
        }

        public TelemetryRequestDto Build(
            TelemetryData telemetry,
            int teamNumber)
        {
            if (telemetry == null)
                throw new ArgumentNullException(nameof(telemetry));

            return new TelemetryRequestDto
            {
                takim_numarasi = teamNumber,

                iha_enlem = telemetry.Latitude,
                iha_boylam = telemetry.Longitude,
                iha_irtifa = telemetry.RelativeAltitudeMeters,

                // MAVLink ATTITUDE eklenince buraya gerçek pitch gelecek.
                iha_dikilme = 0,

                iha_yonelme = telemetry.HeadingDegree,

                // MAVLink ATTITUDE eklenince buraya gerçek roll gelecek.
                iha_yatis = 0,

                iha_hiz = telemetry.GroundSpeed,
                iha_batarya = telemetry.BatteryPercent >= 0 ? telemetry.BatteryPercent : 0,

                iha_otonom = IsAutonomousMode(telemetry.FlightMode) ? 1 : 0,
                iha_kilitlenme = 0,

                // Jetson görüntü işleme bağlanınca buraya hedef kutusu gelecek.
                hedef_merkez_X = 0,
                hedef_merkez_Y = 0,
                hedef_genislik = 0,
                hedef_yukseklik = 0,

                // Artık DateTime.Now değil, sunucu saatine göre üretiliyor.
                gps_saati = _timeSyncManager.GetCurrentEventTimeDto()
            };
        }

        private bool IsAutonomousMode(string flightMode)
        {
            if (string.IsNullOrWhiteSpace(flightMode))
                return false;

            flightMode = flightMode.ToUpperInvariant();

            return flightMode == "AUTO" ||
                   flightMode == "GUIDED" ||
                   flightMode == "RTL" ||
                   flightMode == "LOITER" ||
                   flightMode == "QLAND" ||
                   flightMode == "QRTL";
        }
    }
}