using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.WindowsForms;
using Newtonsoft.Json.Linq;
using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UAV_GCS
{
    public partial class Form1 : Form
    {
        private BoundaryManager _boundaryManager; //sınır ve hss alanlarını haritada çizmek için
        private CameraManager _cameraManager; //kamera streamini almak ve göstermek için
        private MavlinkManager _mavlinkManager; //MAVLink protokolü üzerinden telemetri almak ve komut göndermek için
        private MapVehicleManager _mapVehicleManager; //harita üzerinde kendi İHA ve rakip İHA ve QR marker göstermek için
        private CompetitionApiClient _competitionApiClient; //yarışma sunucusuna bağlanmak ve telemetri göndermek için
        private TelemetryRequestBuilder _telemetryRequestBuilder;
        private TimeSyncManager _timeSyncManager;
        private JetsonRosbridgeClient _jetsonClient;

        private const string SERVER_BASE_URL = "https://savasaniha.teknofest.org";

        private const int MAVLINK_UDP_PORT = 14552;

        private const string JETSON_IP = "192.168.1.50";
        private const string CAMERA_STREAM_URL = "rtsp://192.168.1.50:8554/stream";


        private DateTime _lastMapUpdateTime = DateTime.MinValue; //harita güncelleme zamanını takip etmek için

        private Timer _serverTelemetryTimer; //yarışma sunucusuna telemetri göndermek için 1 Hz timer
        private Timer _jetsonServerTimeTimer;
        private bool _isSendingServerTimeToJetson = false;

        private TelemetryData _lastTelemetryForServer; //yarışma sunucusuna gönderilecek son telemetri verisi
        private bool _isSendingTelemetryToServer = false; //yarışma sunucusuna telemetri gönderimi sırasında tekrar girişleri engellemek için
        private DateTime _lastTelemetrySuccessLogTime = DateTime.MinValue; //yarışma sunucusuna telemetri gönderimi başarılı olduğunda log paneline yazdırmak için en son log zamanı

        private int _telemetrySuccessCount = 0;
        private int _telemetryFailCount = 0;
        private DateTime _lastTelemetrySentTime = DateTime.MinValue;
        private DateTime _lastTargetSentToJetsonTime = DateTime.MinValue;

        private DateTime _lastTargetErrorLogTime = DateTime.MinValue;
        private double _lastTargetErrorX = 0;
        private double _lastTargetErrorY = 0;

        private DateTime _lastLockSentTime = DateTime.MinValue;
        private string _lastLockMessage = "";

        private Timer _serverMapDataTimer;
        private bool _isLoadingServerMapData = false;

        private string _lastHssSignature = "";

        private EventTimeDto _kamikazeStartTime;
        private bool _isKamikazeActive = false;
        private DateTime _lastKamikazeSentTime = DateTime.MinValue;

        private bool _isDogfightActive = false;

        private double _qrLatitude = 0;
        private double _qrLongitude = 0;
        private bool _hasQrCoordinate = false;

        private TelemetryData _lastCubeTelemetry;
        private TelemetryData _lastJetsonTelemetry;

        private DateTime _lastCubeTelemetryTime = DateTime.MinValue;
        private DateTime _lastJetsonTelemetryTime = DateTime.MinValue;

        public Form1()
        {
            InitializeComponent(); //desindaki komponentleri başlatır
        }

        private void Form1_Load(object sender, EventArgs e) //form ekrana geldiğinde ilk çalışan metot
        {
            InitializeMap(); //harita ayarlarını başlatır
            ClearLogPanel(); //log panelini temizler
            InitializeRealDefaultStatuses(); //başlangıçta tüm durumları default olarak ayarlar

            _jetsonClient = new JetsonRosbridgeClient();
            _jetsonClient.LogRequested += AddLog;
            _jetsonClient.ConnectionChanged += OnJetsonConnectionChanged;
            _jetsonClient.StateReceived += OnJetsonStateReceived;
            _jetsonClient.LockReceived += OnJetsonLockReceived;
            _jetsonClient.TargetErrorXReceived += OnJetsonTargetErrorXReceived;
            _jetsonClient.TargetErrorYReceived += OnJetsonTargetErrorYReceived;
            _jetsonClient.KamikazeReceived += OnJetsonKamikazeReceived;
            _jetsonClient.TelemetryReceived += OnJetsonTelemetryReceived;
            _ = TryConnectJetsonAsync();

            _cameraManager = new CameraManager(tableLayoutPanel9, 0, 0); //kamera streamini göstermek için CameraManager başlatılır
            _cameraManager.LogRequested += AddLog; //kamera loglarını log paneline ekler
            _cameraManager.Initialize(); //kamera streamini başlatır

            _boundaryManager = new BoundaryManager( //sınır ve hss alanlarını haritada çizmek için
                gMapControl1,
                listBoxBoundaryPoints
            );

            _mapVehicleManager = new MapVehicleManager(gMapControl1); //harita üzerinde kendi İHA ve rakip İHA ve QR marker göstermek için

            _mavlinkManager = new MavlinkManager(); //MAVLink protokolü üzerinden telemetri almak ve komut göndermek için
            _mavlinkManager.LogRequested += AddLog; //MAVLink loglarını log paneline ekler
            _cameraManager.ConnectionChanged += OnCameraConnectionChanged; //kamera bağlantısı değiştiğinde UI günceller
            _mavlinkManager.ConnectionChanged += OnMavlinkConnectionChanged; //MAVLink bağlantısı değiştiğinde UI günceller
            _mavlinkManager.TelemetryReceived += OnTelemetryReceived; //MAVLink telemetri verisi geldiğinde UI günceller ve yarışma sunucusuna gönderir
            _competitionApiClient = new CompetitionApiClient(SERVER_BASE_URL); //yarışma sunucusuna bağlanmak ve telemetri göndermek için

            _timeSyncManager = new TimeSyncManager();
            _telemetryRequestBuilder = new TelemetryRequestBuilder(_timeSyncManager);

            InitializeServerTelemetryTimer(); //yarışma sunucusuna telemetri göndermek için 1 Hz timer başlatır
            InitializeJetsonServerTimeTimer();
            InitializeServerMapDataTimer();


            TryConnectMavlinkUdp(); // MAVLink UDP bağlantısı başlatır. MAVProxy üzerinden gelen MAVLink verisi dinlenir.

            // Jetson bağlı değilse kamera stream kapalı kalabilir.
            TryStartCameraStream();

            //açılışta log paneline başlangıç mesajlarını ekler
            AddLog("Yer kontrol istasyonu başlatıldı.");
            AddLog("Gerçek bağlantılar bekleniyor: MAVLink, kamera stream ve yarışma sunucusu.");
            AddLog("HSS alanları yarışma sunucusu bağlantısından sonra yüklenecek.");
        }

        private void InitializeJetsonServerTimeTimer()
        {
            _jetsonServerTimeTimer = new Timer();
            _jetsonServerTimeTimer.Interval = 100; // 10 Hz
            _jetsonServerTimeTimer.Tick += JetsonServerTimeTimer_Tick;
        }

        private void InitializeServerMapDataTimer()
        {
            _serverMapDataTimer = new Timer();
            _serverMapDataTimer.Interval = 1000; // 1 Hz
            _serverMapDataTimer.Tick += ServerMapDataTimer_Tick;
        }

        private string ExtractQrTextFromHitPacket(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "";

            try
            {
                JObject obj = JObject.Parse(message);

                return
                    obj["qr_text"]?.ToString() ??
                    obj["qrMetni"]?.ToString() ??
                    obj["qr"]?.ToString() ??
                    "";
            }
            catch
            {
                return message.Trim();
            }
        }

        private EventTimeDto ExtractDiveEndTimeFromHitPacket(string message)
        {
            try
            {
                JObject obj = JObject.Parse(message);

                string timeText = obj["dive_end_time_str"]?.ToString();

                if (!string.IsNullOrWhiteSpace(timeText))
                {
                    string[] mainParts = timeText.Split('.');
                    string[] hms = mainParts[0].Split(':');

                    int hour = int.Parse(hms[0]);
                    int minute = int.Parse(hms[1]);
                    int second = int.Parse(hms[2]);

                    int millisecond = 0;
                    if (mainParts.Length > 1)
                        millisecond = int.Parse(mainParts[1].PadRight(3, '0').Substring(0, 3));

                    return new EventTimeDto
                    {
                        saat = hour,
                        dakika = minute,
                        saniye = second,
                        milisaniye = millisecond
                    };
                }
            }
            catch
            {
                // Parse edilemezse mevcut sunucu zamanını kullanacağız.
            }

            return _timeSyncManager.GetCurrentEventTimeDto();
        }
        private async void OnJetsonKamikazeReceived(string message)
        {
            AddLog("Jetson kamikaze hit packet aldı: " + message);

            if (!IsServerConnected())
            {
                AddLog("Kamikaze bilgisi gönderilemedi: Sunucu bağlantısı yok.");
                return;
            }

            if (!_isKamikazeActive)
            {
                AddLog("Kamikaze hit packet geldi ama aktif kamikaze görevi yok. Paket gönderilmedi.");
                return;
            }

            if ((DateTime.Now - _lastKamikazeSentTime).TotalSeconds < 5)
            {
                AddLog("Kamikaze hit packet tekrar geldi, sunucuya yeniden gönderilmedi.");
                return;
            }

            try
            {
                string qrText = ExtractQrTextFromHitPacket(message);

                if (string.IsNullOrWhiteSpace(qrText))
                {
                    AddLog("Kamikaze bilgisi gönderilemedi: QR metni boş.");
                    return;
                }

                var kamikazeInfo = new KamikazeInfoRequestDto
                {
                    kamikazeBaslangicZamani = _kamikazeStartTime,
                    kamikazeBitisZamani = ExtractDiveEndTimeFromHitPacket(message),
                    qrMetni = qrText
                };

                await _competitionApiClient.SendKamikazeInfoAsync(kamikazeInfo);

                _lastKamikazeSentTime = DateTime.Now;
                _isKamikazeActive = false;

                lblKamikazeStatus.Text = "Kamikaze tamamlandı";
                lblKamikazeStatus.ForeColor = Color.Lime;

                AddLog("Kamikaze bilgisi yarışma sunucusuna gönderildi.");
            }
            catch (Exception ex)
            {
                AddLog("Kamikaze bilgisi gönderilemedi: " + ex.Message);
            }
        }

        

        private void OnJetsonTargetErrorXReceived(double value)
        {
            _lastTargetErrorX = value;
            LogTargetErrorIfNeeded();
        }

        private void OnJetsonTargetErrorYReceived(double value)
        {
            _lastTargetErrorY = value;
            LogTargetErrorIfNeeded();
        }

        private void LogTargetErrorIfNeeded()
        {
            if ((DateTime.Now - _lastTargetErrorLogTime).TotalSeconds < 1)
                return;

            _lastTargetErrorLogTime = DateTime.Now;

            AddLog($"Jetson hedef sapması | X: {_lastTargetErrorX:0.00}, Y: {_lastTargetErrorY:0.00}");
        }

        private async void ServerMapDataTimer_Tick(object sender, EventArgs e)
        {
            if (_isLoadingServerMapData)
                return;

            if (!IsServerConnected())
                return;

            _isLoadingServerMapData = true;

            try
            {
                await LoadServerMapDataAsync();
            }
            finally
            {
                _isLoadingServerMapData = false;
            }
        }

        private async void JetsonServerTimeTimer_Tick(object sender, EventArgs e)
        {
            if (_isSendingServerTimeToJetson)
                return;

            if (_jetsonClient == null || !_jetsonClient.IsConnected)
                return;

            if (_timeSyncManager == null || !_timeSyncManager.IsSynced)
                return;

            _isSendingServerTimeToJetson = true;

            try
            {
                DateTime serverNow = _timeSyncManager.GetCurrentServerDateTime();

                string timeText =
                    $"{serverNow.Hour:00}:{serverNow.Minute:00}:{serverNow.Second:00}.{serverNow.Millisecond:000}";

                await _jetsonClient.SendServerTimeAsync(timeText);

                double serverTimeMs = new DateTimeOffset(serverNow).ToUnixTimeMilliseconds();

                await _jetsonClient.SendServerTimeMsAsync(serverTimeMs);
            }
            catch (Exception ex)
            {
                AddLog("Jetson'a sunucu saati gönderilemedi: " + ex.Message);
            }
            finally
            {
                _isSendingServerTimeToJetson = false;
            }
        }

        private double CalculateDistanceMeters(
    double lat1,
    double lon1,
    double lat2,
    double lon2)
        {
            const double earthRadiusMeters = 6371000.0;

            double lat1Rad = lat1 * Math.PI / 180.0;
            double lat2Rad = lat2 * Math.PI / 180.0;

            double deltaLatRad = (lat2 - lat1) * Math.PI / 180.0;
            double deltaLonRad = (lon2 - lon1) * Math.PI / 180.0;

            double a =
                Math.Sin(deltaLatRad / 2) * Math.Sin(deltaLatRad / 2) +
                Math.Cos(lat1Rad) * Math.Cos(lat2Rad) *
                Math.Sin(deltaLonRad / 2) * Math.Sin(deltaLonRad / 2);

            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

            return earthRadiusMeters * c;
        }

        private EnemyUavDto FindNearestEnemy(
    TelemetryData ownTelemetry,
    System.Collections.Generic.List<EnemyUavDto> enemies)
        {
            if (ownTelemetry == null || enemies == null || enemies.Count == 0)
                return null;

            EnemyUavDto nearestEnemy = null;
            double nearestDistance = double.MaxValue;

            foreach (EnemyUavDto enemy in enemies)
            {
                if (enemy == null)
                    continue;

                if (enemy.takim_numarasi == _competitionApiClient.TeamNumber)
                    continue;

                if (enemy.iha_enlem == 0 && enemy.iha_boylam == 0)
                    continue;

                if (enemy.iha_enlem < -90 || enemy.iha_enlem > 90)
                    continue;

                if (enemy.iha_boylam < -180 || enemy.iha_boylam > 180)
                    continue;

                double distance = CalculateDistanceMeters(
                    ownTelemetry.Latitude,
                    ownTelemetry.Longitude,
                    enemy.iha_enlem,
                    enemy.iha_boylam
                );

                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestEnemy = enemy;
                }
            }

            return nearestEnemy;
        }

        private string BuildHssSignature(System.Collections.Generic.List<HssInfoDto> hssAreas)
        {
            if (hssAreas == null || hssAreas.Count == 0)
                return "";

            var ordered = hssAreas
                .OrderBy(hss => hss.id)
                .Select(hss =>
                    $"{hss.id}|" +
                    $"{hss.hssEnlem:0.0000000}|" +
                    $"{hss.hssBoylam:0.0000000}|" +
                    $"{hss.hssYaricap:0.00}"
                );

            return string.Join(";", ordered);
        }

        
        private async Task SendNearestEnemyToJetsonAsync(
    TelemetryData ownTelemetry,
    System.Collections.Generic.List<EnemyUavDto> enemies)
        {
            if (!_isDogfightActive)
                return;

            if (_jetsonClient == null || !_jetsonClient.IsConnected)
                return;

            if (ownTelemetry == null || enemies == null || enemies.Count == 0)
                return;

            // 1-2 Hz isteniyor. Biz 1 Hz yapıyoruz.
            if ((DateTime.Now - _lastTargetSentToJetsonTime).TotalMilliseconds < 1000)
                return;

            EnemyUavDto nearestEnemy = FindNearestEnemy(ownTelemetry, enemies);

            if (nearestEnemy == null)
                return;

            await _jetsonClient.SendTargetAsync(
                nearestEnemy.iha_enlem,
                nearestEnemy.iha_boylam,
                nearestEnemy.iha_irtifa
            );

            _lastTargetSentToJetsonTime = DateTime.Now;

            AddLog(
                $"Jetson hedefi güncellendi. Takım: {nearestEnemy.takim_numarasi}, " +
                $"Lat: {nearestEnemy.iha_enlem:0.000000}, " +
                $"Lon: {nearestEnemy.iha_boylam:0.000000}, " +
                $"İrtifa: {nearestEnemy.iha_irtifa:0} m"
            );
        }

        private void OnJetsonConnectionChanged(bool isConnected)
        {
            SafeUi(() =>
            {
                AddLog(isConnected ? "Jetson bağlantısı aktif." : "Jetson bağlantısı kesildi.");
            });
        }

        private void OnJetsonStateReceived(string state)
        {
            SafeUi(() =>
            {
                AddLog("Jetson görev durumu: " + state);
            });
        }

        private async void OnJetsonLockReceived(string message)
        {
            if (message == _lastLockMessage &&
    (DateTime.Now - _lastLockSentTime).TotalSeconds < 5)
            {
                AddLog("Aynı kilitlenme mesajı tekrar geldi, sunucuya yeniden gönderilmedi.");
                return;
            }
            AddLog("Jetson kilitlenme sinyali aldı: " + message);

            if (!IsServerConnected())
            {
                AddLog("Kilitlenme bilgisi gönderilemedi: Sunucu bağlantısı yok.");
                return;
            }

            try
            {
                var lockInfo = new LockInfoRequestDto
                {
                    kilitlenmeBitisZamani = _timeSyncManager.GetCurrentEventTimeDto(),
                    otonom_kilitlenme = 1
                };

                await _competitionApiClient.SendLockInfoAsync(lockInfo);

                _lastLockMessage = message;
                _lastLockSentTime = DateTime.Now;

                AddLog("Kilitlenme bilgisi yarışma sunucusuna gönderildi.");
            }
            catch (Exception ex)
            {
                AddLog("Kilitlenme bilgisi gönderilemedi: " + ex.Message);
            }
        }

        private async Task TryConnectJetsonAsync()
        {
            try
            {
                await _jetsonClient.ConnectAsync(JETSON_IP);
            }
            catch (Exception ex)
            {
                AddLog("Jetson bağlantısı kurulamadı: " + ex.Message);
            }
        }
        // Yarışma sunucusuna telemetri göndermek için 1 Hz timer başlatır
        private void InitializeServerTelemetryTimer()
        {
            _serverTelemetryTimer = new Timer(); // Timer nesnesi oluşturulur
            _serverTelemetryTimer.Interval = 1000; // 1 Hz
            _serverTelemetryTimer.Tick += ServerTelemetryTimer_Tick; // Timer her tick olduğunda ServerTelemetryTimer_Tick metodu çağrılır
        }

        // Yarışma sunucusuna telemetri göndermek için 1 Hz timer tick metodu
        private async void ServerTelemetryTimer_Tick(object sender, EventArgs e)
        {
            if (_isSendingTelemetryToServer)
                return;

            if (_competitionApiClient == null || !_competitionApiClient.IsLoggedIn)
                return;

            TelemetryData telemetryForServer = GetBestTelemetryForServer();

            if (telemetryForServer == null)
                return;

            _isSendingTelemetryToServer = true;

            try
            {
                TelemetryRequestDto request =
                    _telemetryRequestBuilder.Build(telemetryForServer, _competitionApiClient.TeamNumber);

                TelemetryResponseDto response =
                    await _competitionApiClient.SendTelemetryAsync(request);

                if (response == null)
                    throw new Exception("Sunucudan telemetri cevabı boş geldi.");

                if (response.sunucusaati != null)
                    _timeSyncManager.UpdateServerTime(response.sunucusaati);

                _lastTelemetrySentTime = DateTime.Now;
                _telemetrySuccessCount++;

                UpdateServerTelemetryStatusLabel(true);

                if (response.konumBilgileri != null)
                {
                    _mapVehicleManager.UpdateEnemyUavs(response.konumBilgileri);

                    await SendNearestEnemyToJetsonAsync(
                        telemetryForServer,
                        response.konumBilgileri
                    );
                }

                if ((DateTime.Now - _lastTelemetrySuccessLogTime).TotalSeconds >= 10)
                {
                    _lastTelemetrySuccessLogTime = DateTime.Now;
                    int enemyCount = response.konumBilgileri != null ? response.konumBilgileri.Count : 0;

                    AddLog($"Telemetri aktif. OK: {_telemetrySuccessCount}, Hata: {_telemetryFailCount}, Rakip İHA: {enemyCount}");
                }
            }
            catch (Exception ex)
            {
                _telemetryFailCount++;
                UpdateServerTelemetryStatusLabel(false);
                AddLog("Telemetri gönderme hatası: " + ex.Message);
            }
            finally
            {
                _isSendingTelemetryToServer = false;
            }
        }




        private void OnCameraConnectionChanged(bool isConnected)
        {
            SafeUi(() =>
            {
                SetStatus(lblCameraStatus, isConnected ? "ACTIVE" : "DISCONNECTED", isConnected);
            });
        }
        private void OnMavlinkConnectionChanged(bool isConnected)
        {
            SafeUi(() =>
            {
                SetStatus(lblMavlinkStatus, isConnected ? "CONNECTED" : "DISCONNECTED", isConnected);

                if (!isConnected)
                {
                    _lastTelemetryForServer = null;

                    SetStatus(lblGpsStatus, "NO FIX", false);

                    lblFlightMode.Text = "--";
                    lblSpeed.Text = "--";
                    lblAltitude.Text = "--";
                    lblBattery.Text = "%--";

                    _mapVehicleManager?.ClearOwnUav();
                }
            });
        }

        private void OnTelemetryReceived(TelemetryData telemetry)
        {
            if (telemetry == null)
                return;

            _lastCubeTelemetry = telemetry;
            _lastCubeTelemetryTime = DateTime.Now;
            _lastTelemetryForServer = telemetry;

            SafeUi(() =>
            {
                lblFlightMode.Text = string.IsNullOrWhiteSpace(telemetry.FlightMode)
                    ? "--"
                    : telemetry.FlightMode;

                double speed = telemetry.GroundSpeed;

                if (speed > -0.5 && speed < 0.5)
                    speed = 0;

                lblSpeed.Text = speed >= 0
                    ? speed.ToString("0.00")
                    : "--";

                double altitude = telemetry.RelativeAltitudeMeters;

                // Yerdeyken sensör/GPS küçük negatif değer verebilir.
                // Bunları 0.00 kabul ediyoruz.
                if (altitude > -1 && altitude < 1)
                    altitude = 0;

                lblAltitude.Text = altitude >= 0
                    ? altitude.ToString("0.00")
                    : "--";

                lblBattery.Text = telemetry.BatteryPercent >= 0
                ? "%" + telemetry.BatteryPercent.ToString("0")
                : "%--";

                SetStatus(
                    lblGpsStatus,
                    telemetry.HasGpsFix ? $"FIX ({telemetry.SatellitesVisible})" : "NO FIX",
                    telemetry.HasGpsFix
                );

                if ((DateTime.Now - _lastMapUpdateTime).TotalMilliseconds > 1000)
                {
                    _lastMapUpdateTime = DateTime.Now;
                    _mapVehicleManager.UpdateOwnUav(telemetry);
                }
            });
        }

        private void OnJetsonTelemetryReceived(TelemetryData telemetry)
        {
            if (telemetry == null)
                return;

            _lastJetsonTelemetry = telemetry;
            _lastJetsonTelemetryTime = DateTime.Now;

            // Cube telemetrisi tazeyse UI'yi Cube yönetsin.
            if (IsCubeTelemetryFresh())
                return;

            _lastTelemetryForServer = telemetry;

            SafeUi(() =>
            {
                lblFlightMode.Text = string.IsNullOrWhiteSpace(telemetry.FlightMode)
                    ? "JETSON"
                    : telemetry.FlightMode + " / JETSON";

                lblSpeed.Text = telemetry.GroundSpeed >= 0
                    ? telemetry.GroundSpeed.ToString("0.00")
                    : "--";

                lblAltitude.Text = telemetry.RelativeAltitudeMeters >= 0
                    ? telemetry.RelativeAltitudeMeters.ToString("0.00")
                    : "--";

                lblBattery.Text = telemetry.BatteryPercent >= 0
                    ? "%" + telemetry.BatteryPercent.ToString("0")
                    : "%--";

                SetStatus(
                    lblGpsStatus,
                    telemetry.HasGpsFix ? $"FIX / JETSON ({telemetry.SatellitesVisible})" : "NO FIX / JETSON",
                    telemetry.HasGpsFix
                );

                if ((DateTime.Now - _lastMapUpdateTime).TotalMilliseconds > 1000)
                {
                    _lastMapUpdateTime = DateTime.Now;
                    _mapVehicleManager.UpdateOwnUav(telemetry);
                }
            });
        }

        private bool IsCubeTelemetryFresh()
        {
            return _lastCubeTelemetry != null &&
                   (DateTime.Now - _lastCubeTelemetryTime).TotalSeconds <= 2;
        }

        private bool IsJetsonTelemetryFresh()
        {
            return _lastJetsonTelemetry != null &&
                   (DateTime.Now - _lastJetsonTelemetryTime).TotalSeconds <= 2;
        }

        private TelemetryData GetBestTelemetryForServer()
        {
            if (IsValidTelemetryValues(_lastCubeTelemetry) && IsCubeTelemetryFresh())
                return _lastCubeTelemetry;

            if (IsValidTelemetryValues(_lastJetsonTelemetry) && IsJetsonTelemetryFresh())
                return _lastJetsonTelemetry;

            return null;
        }

        private bool IsValidTelemetryValues(TelemetryData telemetry)
        {
            if (telemetry == null)
                return false;

            if (!telemetry.HasGpsFix)
                return false;

            if (telemetry.Latitude == 0 && telemetry.Longitude == 0)
                return false;

            if (telemetry.Latitude < -90 || telemetry.Latitude > 90)
                return false;

            if (telemetry.Longitude < -180 || telemetry.Longitude > 180)
                return false;

            if (double.IsNaN(telemetry.Latitude) || double.IsNaN(telemetry.Longitude))
                return false;

            return true;
        }



        private bool IsValidTelemetryForServer(TelemetryData telemetry)
        {
            return IsValidTelemetryValues(telemetry);
        }
        private void TryConnectMavlinkUdp()
        {
            try
            {
                _mavlinkManager.ConnectUdp(MAVLINK_UDP_PORT);
                AddLog($"MAVLink UDP bağlantısı başlatıldı. Port: {MAVLINK_UDP_PORT}");
            }
            catch (Exception ex)
            {
                SetStatus(lblMavlinkStatus, "DISCONNECTED", false);
                AddLog("MAVLink UDP bağlantısı başlatılamadı: " + ex.Message);

                MessageBox.Show(
                    "MAVLink UDP bağlantısı başlatılamadı:\n" + ex.Message,
                    "MAVLink Hatası",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
        }

        private void TryStartCameraStream()
        {
            try
            {
                AddLog("Kamera stream bağlantısı deneniyor: " + CAMERA_STREAM_URL);
                _cameraManager.StartStream(CAMERA_STREAM_URL);
            }
            catch (Exception ex)
            {
                SetStatus(lblCameraStatus, "DISCONNECTED", false);
                AddLog("Kamera stream başlatılamadı: " + ex.Message);
            }
        }

        private void InitializeRealDefaultStatuses()
        {
            SetStatus(lblServerStatus, "DISCONNECTED", false);
            SetStatus(lblMavlinkStatus, "DISCONNECTED", false);
            SetStatus(lblGpsStatus, "NO FIX", false);
            SetStatus(lblCameraStatus, "DISCONNECTED", false);

            lblTeamId.Text = "--";
            lblFlightMode.Text = "--";
            lblBattery.Text = "%--";
            lblSpeed.Text = "--";
            lblAltitude.Text = "--";

            lblServerTelemetryStatus.Text = "Telemetri: Bekleniyor";
            lblServerTelemetryStatus.ForeColor = Color.Gold;

            lblKamikazeStatus.Text = "Bekleniyor...";
            lblKamikazeStatus.ForeColor = Color.Gold;

            lblRecordingStatus.Text = "Pasif";
            lblRecordingStatus.ForeColor = Color.OrangeRed;        
        }

        private void InitializeMap()
        {
            GMaps.Instance.Mode = AccessMode.ServerAndCache;

            gMapControl1.MapProvider = GMapProviders.GoogleSatelliteMap;
            gMapControl1.Position = new PointLatLng(39.90116412553519, 41.24359622374539);

            gMapControl1.MinZoom = 1;
            gMapControl1.MaxZoom = 20;
            gMapControl1.Zoom = 15;

            gMapControl1.ShowCenter = false;
            gMapControl1.DragButton = MouseButtons.Left;
        }

        private void ClearLogPanel()
        {
            richTextBox1.Clear();
        }

        private void AddLog(string message)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");

            SafeUi(() =>
            {
                richTextBox1.AppendText($"[{time}] {message}\n");
                richTextBox1.ScrollToCaret();
            });
        }

        private void SetStatus(Label label, string text, bool isOk)
        {
            SafeUi(() =>
            {
                label.Text = "● " + text;
                label.ForeColor = isOk ? Color.Lime : Color.Red;
            });
        }

        private void SafeUi(Action action)
        {
            if (InvokeRequired)
                BeginInvoke(action);
            else
                action();
        }

        private bool IsMavlinkConnected()
        {
            return _mavlinkManager != null && _mavlinkManager.IsConnected;
        }

        private bool IsServerConnected()
        {
            return _competitionApiClient != null && _competitionApiClient.IsLoggedIn;
        }

        private void ShowMavlinkNotConnectedMessage(string commandName)
        {
            AddLog($"{commandName} komutu gönderilmedi. MAVLink bağlantısı yok.");
            MessageBox.Show(
                "MAVLink bağlantısı kurulmadan bu komut gönderilemez.",
                "Bağlantı Yok",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }

        private void ShowServerNotConnectedMessage(string operationName)
        {
            AddLog($"{operationName} işlemi yapılamadı. Yarışma sunucusu bağlantısı yok.");
            MessageBox.Show(
                "Yarışma sunucusu bağlantısı kurulmadan bu işlem yapılamaz.",
                "Sunucu Bağlantısı Yok",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }

        private async Task LoadServerMapDataAsync()
        {
            try
            {
                InitialServerDataDto data = await _competitionApiClient.GetInitialServerDataAsync();

                if (data == null)
                {
                    AddLog("Sunucudan başlangıç verisi boş geldi.");
                    return;
                }

                if (data.Hss?.hss_koordinat_bilgileri != null)
                {
                    string currentHssSignature = BuildHssSignature(data.Hss.hss_koordinat_bilgileri);

                    if (currentHssSignature != _lastHssSignature)
                    {
                        _lastHssSignature = currentHssSignature;

                        _boundaryManager.DrawHssAreasFromServer(data.Hss.hss_koordinat_bilgileri);

                        AddLog($"HSS koordinatları güncellendi. Alan sayısı: {data.Hss.hss_koordinat_bilgileri.Count}");

                        if (_jetsonClient != null && _jetsonClient.IsConnected)
                        {
                            await _jetsonClient.SendHssAreasAsync(data.Hss.hss_koordinat_bilgileri);
                        }
                        else
                        {
                            AddLog("HSS koordinatları Jetson'a gönderilemedi: Jetson bağlantısı yok.");
                        }
                    }
                }

                if (data.Qr != null && !_hasQrCoordinate)
                {
                    _qrLatitude = data.Qr.qrEnlem;
                    _qrLongitude = data.Qr.qrBoylam;
                    _hasQrCoordinate = true;

                    _mapVehicleManager.UpdateQrTarget(data.Qr.qrEnlem, data.Qr.qrBoylam);

                    AddLog($"QR hedef koordinatı alındı: {data.Qr.qrEnlem:0.000000}, {data.Qr.qrBoylam:0.000000}");
                }

                if (data.ServerTime != null)
                {
                    _timeSyncManager.UpdateServerTime(data.ServerTime);

                    AddLog(
                        $"Sunucu saati senkronlandı: " +
                        $"{data.ServerTime.saat:00}:{data.ServerTime.dakika:00}:{data.ServerTime.saniye:00}.{data.ServerTime.milisaniye:000}"
                    );
                }
            }
            catch (Exception ex)
            {
                AddLog("Sunucu harita verileri alınamadı: " + ex.Message);
            }
        }

        private void UpdateServerTelemetryStatusLabel(bool isOk)
        {
            SafeUi(() =>
            {
                if (_lastTelemetrySentTime == DateTime.MinValue)
                {
                    lblServerTelemetryStatus.Text = "Telemetri: Bekleniyor";
                    lblServerTelemetryStatus.ForeColor = Color.Gold;
                    return;
                }

                lblServerTelemetryStatus.Text =
                    $"Telemetri: {(isOk ? "AKTİF" : "HATA")} | OK: {_telemetrySuccessCount} | Hata: {_telemetryFailCount} | Son: {_lastTelemetrySentTime:HH:mm:ss}";

                lblServerTelemetryStatus.ForeColor = isOk ? Color.Lime : Color.Red;
            });
        }

        private void btnAddBoundaryPoint_Click(object sender, EventArgs e)
        {
            try
            {
                _boundaryManager.AddPoint(
                    txtBoundaryLat.Text,
                    txtBoundaryLon.Text
                );

                AddLog($"Sınır noktası eklendi: {txtBoundaryLat.Text}, {txtBoundaryLon.Text}");

                txtBoundaryLat.Clear();
                txtBoundaryLon.Clear();
            }
            catch
            {
                MessageBox.Show("Geçerli enlem/boylam gir. Örn: 40.807000 / 29.354000");
            }
        }

        private void btnRemoveSelectedPoint_Click(object sender, EventArgs e)
        {
            _boundaryManager.RemoveSelectedPoint();
            AddLog("Seçili sınır noktası silindi.");
        }

        private void btnDrawBoundary_Click(object sender, EventArgs e)
        {
            _boundaryManager.DrawOutsideBoundary();
            AddLog("Uçuş sınırı harita üzerinde çizildi.");
        }

        private void btnClearBoundary_Click(object sender, EventArgs e)
        {
            _boundaryManager.ClearBoundary();
            AddLog("Uçuş sınırı temizlendi.");
        }


        private async void btnServerLogin_Click(object sender, EventArgs e)
        {
            if (IsServerConnected())
            {
                AddLog("Sunucu zaten bağlı.");
                return;
            }

            try
            {
                AddLog("Sunucu girişi deneniyor: " + SERVER_BASE_URL);

                int teamNumber = await _competitionApiClient.LoginAsync(
                    txtUsername.Text,
                    txtPassword.Text
                );

                lblTeamId.Text = teamNumber.ToString();

                SetStatus(lblServerStatus, "CONNECTED", true);
                AddLog($"Sunucu girişi başarılı. Takım numarası: {teamNumber}");

                await LoadServerMapDataAsync();

                _serverMapDataTimer.Start();
                AddLog("Sunucu HSS/QR güncelleme timer'ı başlatıldı. Frekans: 1 Hz");

                _telemetrySuccessCount = 0;
                _telemetryFailCount = 0;
                _lastTelemetrySentTime = DateTime.MinValue;
                _lastTelemetrySuccessLogTime = DateTime.MinValue;
                UpdateServerTelemetryStatusLabel(true);
                _serverTelemetryTimer.Start();
                AddLog("Sunucu telemetri gönderimi başlatıldı. Frekans: 1 Hz");

                _jetsonServerTimeTimer.Start();
                AddLog("Jetson'a sunucu saati gönderimi başlatıldı. Frekans: 10 Hz");
            }
            catch (Exception ex)
            {
                SetStatus(lblServerStatus, "DISCONNECTED", false);
                AddLog("Sunucu girişi başarısız: " + ex.Message);

                MessageBox.Show(
                    "Sunucu girişi başarısız:\n" + ex.Message,
                    "Sunucu Hatası",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
        }

        private async void btnAutonomousLanding_Click(object sender, EventArgs e)
        {
            await SendCommandCubeFirstThenJetsonAsync(
                "Otonom iniş",
                () => _mavlinkManager.SetModeLand(),
                "OTONOM_INIS",
                "Otonom iniş için QLAND komutu MAVLink üzerinden Cube'a gönderildi.",
                "Cube'a ulaşılamadığı için OTONOM_INIS komutu Jetson'a gönderildi."
            );
        }

        private async void btnAutonomousFlight_Click(object sender, EventArgs e)
        {
            await SendCommandCubeFirstThenJetsonAsync(
                "Otonom kalkış",
                () => _mavlinkManager.SendTakeoffCommand(100.0f),
                "OTONOM_KALKIS",
                "Otonom kalkış için TAKEOFF komutu MAVLink üzerinden Cube'a gönderildi. İrtifa: 100 m",
                "Cube'a ulaşılamadığı için OTONOM_KALKIS komutu Jetson'a gönderildi."
            );
        }

        private async void btnStartKamikaze_Click(object sender, EventArgs e)
        {
            try
            {
                if (_jetsonClient == null || !_jetsonClient.IsConnected)
                {
                    AddLog("Kamikaze başlatılamadı: Jetson bağlantısı yok.");
                    return;
                }

                if (_timeSyncManager == null || !_timeSyncManager.IsSynced)
                {
                    AddLog("Kamikaze başlatılamadı: Sunucu saati senkron değil.");
                    return;
                }

                if (!_hasQrCoordinate)
                {
                    AddLog("Kamikaze başlatılamadı: QR hedef koordinatı yok.");
                    return;
                }

                await _jetsonClient.SendTargetAsync(_qrLatitude, _qrLongitude, 100.0);
                AddLog($"Kamikaze hedef koordinatı Jetson'a gönderildi. Lat: {_qrLatitude:0.000000}, Lon: {_qrLongitude:0.000000}");

                await _jetsonClient.SendUiCommandAsync("ENGAGE_KAMIKAZE");

                _kamikazeStartTime = _timeSyncManager.GetCurrentEventTimeDto();
                _isKamikazeActive = true;

                lblKamikazeStatus.Text = "Kamikaze aktif";
                lblKamikazeStatus.ForeColor = Color.Lime;

                AddLog("Kamikaze başlatma komutu Jetson'a gönderildi.");
                AddLog("Kamikaze başlangıç zamanı kaydedildi.");
            }
            catch (Exception ex)
            {
                AddLog("Kamikaze komutu gönderilemedi: " + ex.Message);
            }
        }


        private async void btnFinishKamikaze_Click(object sender, EventArgs e)
        {
            try
            {
                if (_jetsonClient == null || !_jetsonClient.IsConnected)
                {
                    AddLog("Kamikaze iptal komutu gönderilemedi: Jetson bağlantısı yok.");
                    return;
                }

                await _jetsonClient.SendUiCommandAsync("ABORT");
                _isKamikazeActive = false;

                lblKamikazeStatus.Text = "Kamikaze iptal edildi";
                lblKamikazeStatus.ForeColor = Color.OrangeRed;
                AddLog("Kamikaze iptal / ABORT komutu Jetson'a gönderildi.");
            }
            catch (Exception ex)
            {
                AddLog("Kamikaze ABORT komutu gönderilemedi: " + ex.Message);
            }
        }

        private async void btnStartLock_Click(object sender, EventArgs e)
        {
            try
            {
                if (_jetsonClient == null || !_jetsonClient.IsConnected)
                {
                    AddLog("Dogfight başlatılamadı: Jetson bağlantısı yok.");
                    return;
                }

                await _jetsonClient.SendUiCommandAsync("ENGAGE_DOGFIGHT");
                _isDogfightActive = true;
                AddLog("Dogfight başlatma komutu Jetson'a gönderildi.");
                AddLog("Dogfight hedef aktarımı başlatıldı.");
            }
            catch (Exception ex)
            {
                AddLog("Dogfight komutu gönderilemedi: " + ex.Message);
            }
        }

        private async void btnFinishLock_Click(object sender, EventArgs e)
        {
            try
            {
                if (_jetsonClient == null || !_jetsonClient.IsConnected)
                {
                    AddLog("Görev iptal komutu gönderilemedi: Jetson bağlantısı yok.");
                    return;
                }

                await _jetsonClient.SendUiCommandAsync("ABORT");
                _isDogfightActive = false;

                AddLog("Görev iptal / ABORT komutu Jetson'a gönderildi.");
                AddLog("Dogfight hedef aktarımı durduruldu.");
            }
            catch (Exception ex)
            {
                AddLog("ABORT komutu gönderilemedi: " + ex.Message);
            }
        }

        private async void btnFailSafe_Click(object sender, EventArgs e)
        {
            DialogResult result = MessageBox.Show(
                "Fail Safe komutu gönderilecek. Emin misin?",
                "Fail Safe Onayı",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning
            );

            if (result != DialogResult.Yes)
            {
                AddLog("Fail Safe komutu iptal edildi.");
                return;
            }

            await SendCommandCubeFirstThenJetsonAsync(
                "Fail Safe",
                () => _mavlinkManager.SetModeRtl(),
                "FAIL_SAFE",
                "Fail Safe / RTL komutu MAVLink üzerinden Cube'a gönderildi.",
                "Cube'a ulaşılamadığı için FAIL_SAFE komutu Jetson'a gönderildi."
            );
        }

        private async Task SendCommandCubeFirstThenJetsonAsync(
    string commandName,
    Action cubeCommand,
    string jetsonCommand,
    string cubeSuccessLog,
    string jetsonFallbackLog)
        {
            bool cubeTried = false;

            if (IsMavlinkConnected())
            {
                try
                {
                    cubeTried = true;
                    cubeCommand.Invoke();

                    AddLog(cubeSuccessLog);
                    return;
                }
                catch (Exception ex)
                {
                    AddLog($"{commandName} komutu Cube'a gönderilemedi: {ex.Message}");
                }
            }
            else
            {
                AddLog($"{commandName}: MAVLink bağlantısı yok. Jetson fallback deneniyor.");
            }

            if (_jetsonClient != null && _jetsonClient.IsConnected)
            {
                try
                {
                    await _jetsonClient.SendUiCommandAsync(jetsonCommand);
                    AddLog(jetsonFallbackLog);
                    return;
                }
                catch (Exception ex)
                {
                    AddLog($"{commandName} komutu Jetson'a gönderilemedi: {ex.Message}");
                }
            }

            string reason = cubeTried
                ? "Cube komutu başarısız oldu ve Jetson'a da komut gönderilemedi."
                : "MAVLink bağlantısı yok ve Jetson'a da komut gönderilemedi.";

            AddLog($"{commandName} komutu gönderilemedi. {reason}");

            MessageBox.Show(
                $"{commandName} komutu gönderilemedi.\n{reason}",
                "Komut Gönderilemedi",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _serverTelemetryTimer?.Stop();
            _serverTelemetryTimer?.Dispose();

            _jetsonServerTimeTimer?.Stop();
            _jetsonServerTimeTimer?.Dispose();

            _serverMapDataTimer?.Stop();
            _serverMapDataTimer?.Dispose();

            _cameraManager?.Dispose();
            _mavlinkManager?.Dispose();
            _competitionApiClient?.Dispose();
            _jetsonClient?.Dispose();


            base.OnFormClosing(e);
        }

        private async void btnLoiter_Click(object sender, EventArgs e)
        {
            await SendCommandCubeFirstThenJetsonAsync(
                "Loiter",
                () => _mavlinkManager.SetModeLoiter(),
                "LOITER",
                "LOITER mod komutu MAVLink üzerinden Cube'a gönderildi.",
                "Cube'a ulaşılamadığı için LOITER komutu Jetson'a gönderildi."
            );
        }

        private void tableLayoutPanel11_Paint(object sender, PaintEventArgs e)
        {

        }

        private async void btnRtl_Click(object sender, EventArgs e)
        {
            await SendCommandCubeFirstThenJetsonAsync(
                "Otonom uçuş",
                () => _mavlinkManager.SetModeAuto(),
                "OTONOM_UCUS",
                "Otonom uçuş için AUTO mod komutu MAVLink üzerinden Cube'a gönderildi.",
                "Cube'a ulaşılamadığı için OTONOM_UCUS komutu Jetson'a gönderildi."
            );
        }
    }
}