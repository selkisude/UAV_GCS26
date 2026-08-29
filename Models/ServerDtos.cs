using System.Collections.Generic;

namespace UAV_GCS
{
    public class LoginRequestDto
    {
        public string kadi { get; set; }
        public string sifre { get; set; }
    }

    public class ServerTimeDto
    {
        public int gun { get; set; }
        public int saat { get; set; }
        public int dakika { get; set; }
        public int saniye { get; set; }
        public int milisaniye { get; set; }
    }

    public class EventTimeDto
    {
        public int saat { get; set; }
        public int dakika { get; set; }
        public int saniye { get; set; } 
        public int milisaniye { get; set; }
    }

    public class TelemetryRequestDto
    {
        public int takim_numarasi { get; set; }

        public double iha_enlem { get; set; }
        public double iha_boylam { get; set; }
        public double iha_irtifa { get; set; }

        public double iha_dikilme { get; set; }
        public double iha_yonelme { get; set; }
        public double iha_yatis { get; set; }

        public double iha_hiz { get; set; }
        public int iha_batarya { get; set; }

        public int iha_otonom { get; set; }
        public int iha_kilitlenme { get; set; }

        public int hedef_merkez_X { get; set; }
        public int hedef_merkez_Y { get; set; }
        public int hedef_genislik { get; set; }
        public int hedef_yukseklik { get; set; }

        public EventTimeDto gps_saati { get; set; }
    }

    public class TelemetryResponseDto
    {
        public ServerTimeDto sunucusaati { get; set; } = new ServerTimeDto();
        public List<EnemyUavDto> konumBilgileri { get; set; } = new List<EnemyUavDto>();
    }

    public class EnemyUavDto
    {
        public int takim_numarasi { get; set; }

        public double iha_enlem { get; set; }
        public double iha_boylam { get; set; }
        public double iha_irtifa { get; set; }

        public double iha_dikilme { get; set; }
        public double iha_yonelme { get; set; }
        public double iha_yatis { get; set; }

        // Dokümandaki telemetri cevabında "iha_hizi" olarak geliyor.
        public double iha_hizi { get; set; }

        public int zaman_farki { get; set; }
    }

    public class LockInfoRequestDto
    {
        public EventTimeDto kilitlenmeBitisZamani { get; set; }
        public int otonom_kilitlenme { get; set; }
    }

    public class KamikazeInfoRequestDto
    {
        public EventTimeDto kamikazeBaslangicZamani { get; set; }
        public EventTimeDto kamikazeBitisZamani { get; set; }
        public string qrMetni { get; set; }
    }

    public class QrCoordinateResponseDto
    {
        public double qrEnlem { get; set; }
        public double qrBoylam { get; set; }
    }

    public class HssResponseDto
    {
        public ServerTimeDto sunucusaati { get; set; } = new ServerTimeDto();
        public List<HssInfoDto> hss_koordinat_bilgileri { get; set; } = new List<HssInfoDto>();
    }

    public class HssInfoDto
    {
        public int id { get; set; }
        public double hssEnlem { get; set; }
        public double hssBoylam { get; set; }
        public double hssYaricap { get; set; }
    }

    public class InitialServerDataDto
    {
        public ServerTimeDto ServerTime { get; set; }
        public HssResponseDto Hss { get; set; }
        public QrCoordinateResponseDto Qr { get; set; }
    }
}