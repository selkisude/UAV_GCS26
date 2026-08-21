using System;

namespace UAV_GCS
{
    public class TimeSyncManager
    {
        private TimeSpan _serverOffset = TimeSpan.Zero;
        private bool _isSynced = false;
        private DateTime _lastSyncLocalTime = DateTime.MinValue;
        private ServerTimeDto _lastServerTime;

        public bool IsSynced => _isSynced;

        public DateTime LastSyncLocalTime => _lastSyncLocalTime;

        public ServerTimeDto LastServerTime => _lastServerTime;

        public void UpdateServerTime(ServerTimeDto serverTime)
        {
            if (serverTime == null)
                return;

            DateTime localNow = DateTime.Now;
            DateTime serverDateTime = ConvertServerTimeToDateTime(serverTime, localNow);

            _serverOffset = serverDateTime - localNow;
            _lastSyncLocalTime = localNow;
            _lastServerTime = serverTime;
            _isSynced = true;
        }

        public DateTime GetCurrentServerDateTime()
        {
            if (!_isSynced)
                return DateTime.Now;

            return DateTime.Now + _serverOffset;
        }

        public ServerTimeDto GetCurrentServerTimeDto()
        {
            DateTime serverNow = GetCurrentServerDateTime();

            return new ServerTimeDto
            {
                gun = serverNow.Day,
                saat = serverNow.Hour,
                dakika = serverNow.Minute,
                saniye = serverNow.Second,
                milisaniye = serverNow.Millisecond
            };
        }

        public EventTimeDto GetCurrentEventTimeDto()
        {
            DateTime serverNow = GetCurrentServerDateTime();

            return new EventTimeDto
            {
                saat = serverNow.Hour,
                dakika = serverNow.Minute,
                saniye = serverNow.Second,
                milisaniye = serverNow.Millisecond
            };
        }

        private DateTime ConvertServerTimeToDateTime(ServerTimeDto serverTime, DateTime localNow)
        {
            int year = localNow.Year;
            int month = localNow.Month;

            int day = serverTime.gun;

            int maxDay = DateTime.DaysInMonth(year, month);

            if (day < 1)
                day = localNow.Day;

            if (day > maxDay)
                day = maxDay;

            DateTime result = new DateTime(
                year,
                month,
                day,
                serverTime.saat,
                serverTime.dakika,
                serverTime.saniye,
                serverTime.milisaniye
            );

            // Ay sonu / ay başı geçişinde gün farkı çok büyük görünmesin diye düzeltme.
            if ((result - localNow).TotalDays > 15)
                result = result.AddMonths(-1);

            if ((localNow - result).TotalDays > 15)
                result = result.AddMonths(1);

            return result;
        }
    }
}