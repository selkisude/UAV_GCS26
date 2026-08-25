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

            bool hasTargetBox = HasValidTargetBox(telemetry);

            return new TelemetryRequestDto
            {
                takim_numarasi = teamNumber,

                iha_enlem = telemetry.Latitude,
                iha_boylam = telemetry.Longitude,
                iha_irtifa = telemetry.RelativeAltitudeMeters,

                // MAVLink ATTITUDE pitch değeri.
                iha_dikilme = Clamp(telemetry.PitchDegree, -90, 90),

                iha_yonelme = NormalizeHeading(telemetry.HeadingDegree),

                // MAVLink ATTITUDE roll değeri.
                iha_yatis = Clamp(telemetry.RollDegree, -90, 90),

                iha_hiz = telemetry.GroundSpeed < 0 ? 0 : telemetry.GroundSpeed,
                iha_batarya = ClampInt(telemetry.BatteryPercent, 0, 100),

                iha_otonom = IsAutonomousMode(telemetry.FlightMode) ? 1 : 0,

                // Hedef kutusu gerçekten varsa 1 gönderiyoruz.
                // Çünkü dokümana göre iha_kilitlenme 1 ise hedef bilgileri sıfırdan farklı olmalı.
                iha_kilitlenme = hasTargetBox ? 1 : 0,

                hedef_merkez_X = hasTargetBox ? telemetry.TargetCenterX : 0,
                hedef_merkez_Y = hasTargetBox ? telemetry.TargetCenterY : 0,
                hedef_genislik = hasTargetBox ? telemetry.TargetWidth : 0,
                hedef_yukseklik = hasTargetBox ? telemetry.TargetHeight : 0,

                // Öncelik hava aracından gelen GPS saati.
                // GpsTime null ise geçici fallback olarak sunucu senkron saatini kullanıyoruz.
                // Sahaya çıkmadan önce GpsTime'ın MAVLink GPS_RAW_INT time_usec'ten dolması gerekiyor.
                gps_saati = telemetry.GpsTime ?? _timeSyncManager.GetCurrentEventTimeDto()
            };
        }

        private bool HasValidTargetBox(TelemetryData telemetry)
        {
            if (telemetry == null)
                return false;

            if (!telemetry.IsTrackingTarget)
                return false;

            if (telemetry.TargetCenterX <= 0)
                return false;

            if (telemetry.TargetCenterY <= 0)
                return false;

            if (telemetry.TargetWidth <= 0)
                return false;

            if (telemetry.TargetHeight <= 0)
                return false;

            return true;
        }

        private double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value))
                return 0;

            if (value < min)
                return min;

            if (value > max)
                return max;

            return value;
        }

        private int ClampInt(int value, int min, int max)
        {
            if (value < min)
                return min;

            if (value > max)
                return max;

            return value;
        }

        private double NormalizeHeading(double heading)
        {
            if (double.IsNaN(heading))
                return 0;

            while (heading < 0)
                heading += 360;

            while (heading > 360)
                heading -= 360;

            return heading;
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
                   flightMode == "QRTL" ||
                   flightMode == "TAKEOFF" ||
                   flightMode == "LOITER_TO_QLAND";
        }
    }
}