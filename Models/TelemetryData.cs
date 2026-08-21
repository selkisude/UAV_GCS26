namespace UAV_GCS
{
    public class TelemetryData
    {
        public int TeamId { get; set; } = 0;

        public double Latitude { get; set; } = 0;
        public double Longitude { get; set; } = 0;

        public double AltitudeMeters { get; set; } = 0;
        public double RelativeAltitudeMeters { get; set; } = 0;

        public double GroundSpeed { get; set; } = 0;
        public double AirSpeed { get; set; } = 0;

        public int BatteryPercent { get; set; } = -1;

        public string FlightMode { get; set; } = "--";

        public bool HasGpsFix { get; set; } = false;
        public int SatellitesVisible { get; set; } = 0;

        public double HeadingDegree { get; set; } = 0;
    }
}