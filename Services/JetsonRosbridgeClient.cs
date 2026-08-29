using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace UAV_GCS
{
    public class JetsonRosbridgeClient : IDisposable
    {
        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cts;

        public bool IsConnected =>
            _webSocket != null &&
            _webSocket.State == WebSocketState.Open;

        public event Action<string> LogRequested;
        public event Action<bool> ConnectionChanged;
        public event Action<string> StateReceived;
        public event Action<string> LockReceived;
        public event Action<double> TargetErrorXReceived;
        public event Action<double> TargetErrorYReceived;
        public event Action<string> KamikazeReceived;
        public event Action<TelemetryData> TelemetryReceived;
        public event Action<TargetBoxData> TargetBoxReceived;
        public DateTime LastMessageReceivedTime { get; private set; } = DateTime.MinValue;

        public async Task SendHssAreasAsync(List<HssInfoDto> hssAreas)
        {
            if (!IsConnected)
                return;

            if (hssAreas == null || hssAreas.Count == 0)
                return;

            var simplifiedHssAreas = hssAreas.Select(hss => new
            {
                id = hss.id,
                lat = hss.hssEnlem,
                lon = hss.hssBoylam,
                radius = hss.hssYaricap
            }).ToList();

            string hssJson = JsonConvert.SerializeObject(simplifiedHssAreas);

            var packet = new
            {
                op = "publish",
                topic = "/kayzer/hss_koordinatlari",
                msg = new
                {
                    data = hssJson
                }
            };

            await SendJsonAsync(packet);

            LogRequested?.Invoke("HSS koordinatları Jetson'a gönderildi. Alan sayısı: " + hssAreas.Count);
        }
        public async Task ConnectAsync(string jetsonIp)
        {
            if (string.IsNullOrWhiteSpace(jetsonIp))
                throw new Exception("Jetson IP adresi boş olamaz.");

            if (IsConnected)
                return;

            string url = $"ws://{jetsonIp.Trim()}:9090";

            _cts = new CancellationTokenSource();
            _webSocket = new ClientWebSocket();

            LogRequested?.Invoke("Jetson rosbridge bağlantısı deneniyor: " + url);

            await _webSocket.ConnectAsync(new Uri(url), _cts.Token);

            LogRequested?.Invoke("Jetson rosbridge bağlantısı kuruldu.");
            ConnectionChanged?.Invoke(true);

            LastMessageReceivedTime = DateTime.Now;

            await AdvertiseAsync("/kayzer/server_time", "std_msgs/msg/Float64");
            await AdvertiseAsync("/sunucu_saati", "std_msgs/msg/String");
            await AdvertiseAsync("/kayzer/ui_command", "std_msgs/msg/String");
            await AdvertiseAsync("/kayzer/hss_koordinatlari", "std_msgs/msg/String");

            await SubscribeAsync("/kayzer/kilitlenme_basarili", "std_msgs/msg/String");
            await SubscribeAsync("/kayzer/real_state", "std_msgs/msg/String");
            await SubscribeAsync("/kayzer/kamikaze/hit_packet", "std_msgs/msg/String");

            await SubscribeAsync("/kayzer/vision/target_error_x", "std_msgs/msg/Float64");
            await SubscribeAsync("/kayzer/vision/target_error_y", "std_msgs/msg/Float64");
            await SubscribeAsync("/kayzer/telemetry", "std_msgs/msg/String");
            await SubscribeAsync("/kayzer/vision/target_bbox", "std_msgs/msg/String");

            _ = Task.Run(ReceiveLoopAsync);
        }

        public async Task DisconnectAsync()
        {
            try
            {
                if (_webSocket != null && _webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "YKİ kapatıyor",
                        CancellationToken.None
                    );
                }
            }
            catch
            {
                // Kapanış hatası önemsenmez.
            }
            finally
            {
                _cts?.Cancel();
                _webSocket?.Dispose();
                _webSocket = null;

                ConnectionChanged?.Invoke(false);
                LogRequested?.Invoke("Jetson rosbridge bağlantısı kapatıldı.");
            }
        }

        public async Task SendUiCommandAsync(string command)
        {
            if (!IsConnected)
                throw new Exception("Jetson bağlantısı yok.");

            var packet = new
            {
                op = "publish",
                topic = "/kayzer/ui_command",
                msg = new
                {
                    data = command
                }
            };

            await SendJsonAsync(packet);

            LogRequested?.Invoke("Jetson komutu gönderildi: " + command);
        }

        public async Task SendServerTimeMsAsync(double serverTimeMs)
        {
            if (!IsConnected)
                return;

            var packet = new
            {
                op = "publish",
                topic = "/kayzer/server_time",
                msg = new
                {
                    data = serverTimeMs
                }
            };

            await SendJsonAsync(packet);
        }
        public async Task SendServerTimeAsync(string timeText)
        {
            if (!IsConnected)
                return;

            var packet = new
            {
                op = "publish",
                topic = "/sunucu_saati",
                msg = new
                {
                    data = timeText
                }
            };

            await SendJsonAsync(packet);
        }

        public async Task SendTargetAsync(double latitude, double longitude, double altitude)
        {
            if (!IsConnected)
                return;

            var packet = new
            {
                op = "publish",
                topic = "/hedef/mavros/global_position/global",
                msg = new
                {
                    latitude = latitude,
                    longitude = longitude,
                    altitude = altitude
                }
            };

            await SendJsonAsync(packet);
        }

        private async Task SubscribeAsync(string topic, string type)
        {
            var packet = new
            {
                op = "subscribe",
                topic = topic,
                type = type
            };

            await SendJsonAsync(packet);

            LogRequested?.Invoke("Jetson topic aboneliği yapıldı: " + topic);
        }

        private async Task AdvertiseAsync(string topic, string type)
        {
            var packet = new
            {
                op = "advertise",
                topic = topic,
                type = type
            };

            await SendJsonAsync(packet);

            LogRequested?.Invoke("Jetson topic advertise yapıldı: " + topic + " / " + type);
        }

        private async Task SendJsonAsync(object packet)
        {
            string json = JsonConvert.SerializeObject(packet);

            byte[] bytes = Encoding.UTF8.GetBytes(json);

            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                _cts.Token
            );
        }

        private async Task ReceiveLoopAsync()
        {
            byte[] buffer = new byte[8192];

            try
            {
                while (_webSocket != null && _webSocket.State == WebSocketState.Open)
                {
                    StringBuilder messageBuilder = new StringBuilder();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            _cts.Token
                        );

                        if (result.MessageType == WebSocketMessageType.Close)
                            return;

                        string part = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        messageBuilder.Append(part);

                    } while (!result.EndOfMessage);

                    string json = messageBuilder.ToString();
                    LastMessageReceivedTime = DateTime.Now;
                    HandleIncomingMessage(json);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal kapanış.
            }
            catch (Exception ex)
            {
                LogRequested?.Invoke("Jetson bağlantı dinleme hatası: " + ex.Message);
            }
            finally
            {
                ConnectionChanged?.Invoke(false);
            }
        }

        private void HandleIncomingMessage(string json)
        {
            try
            {
                JObject obj = JObject.Parse(json);

                string topic = obj["topic"]?.ToString();
                string data = obj["msg"]?["data"]?.ToString();

                if (string.IsNullOrWhiteSpace(topic))
                    return;

                if (topic == "/kayzer/kilitlenme_basarili")
                {
                    LockReceived?.Invoke(data);
                    LogRequested?.Invoke("Jetson kilitlenme mesajı: " + data);
                }
                else if (topic == "/kayzer/real_state")
                {
                    StateReceived?.Invoke(data);
                    LogRequested?.Invoke("Jetson durum mesajı: " + data);
                }
                else if (topic == "/kayzer/kamikaze/hit_packet")
                {
                    KamikazeReceived?.Invoke(data);
                    LogRequested?.Invoke("Jetson kamikaze hit packet mesajı: " + data);
                }
                else if (topic == "/kayzer/vision/target_error_x")
                {
                    double value = obj["msg"]?["data"]?.Value<double>() ?? 0;
                    TargetErrorXReceived?.Invoke(value);
                }
                else if (topic == "/kayzer/vision/target_error_y")
                {
                    double value = obj["msg"]?["data"]?.Value<double>() ?? 0;
                    TargetErrorYReceived?.Invoke(value);
                }
                else if (topic == "/kayzer/telemetry")
                {
                    TelemetryData telemetry = ParseJetsonTelemetry(data);

                    if (telemetry != null)
                    {
                        TelemetryReceived?.Invoke(telemetry);
                    }
                }
                else if (topic == "/kayzer/vision/target_bbox")
                {
                    TargetBoxData targetBox = ParseTargetBox(data);

                    if (targetBox != null)
                        TargetBoxReceived?.Invoke(targetBox);
                }
            }
            catch (Exception ex)
            {
                LogRequested?.Invoke("Jetson mesajı çözümlenemedi: " + ex.Message);
            }
        }

        private TargetBoxData ParseTargetBox(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return null;

            JObject obj = JObject.Parse(data);

            return new TargetBoxData
            {
                Tracking =
                    obj["tracking"]?.Value<bool>() ??
                    obj["is_tracking"]?.Value<bool>() ??
                    false,

                CenterX =
                    obj["center_x"]?.Value<int>() ??
                    obj["hedef_merkez_X"]?.Value<int>() ??
                    0,

                CenterY =
                    obj["center_y"]?.Value<int>() ??
                    obj["hedef_merkez_Y"]?.Value<int>() ??
                    0,

                Width =
                    obj["width"]?.Value<int>() ??
                    obj["hedef_genislik"]?.Value<int>() ??
                    0,

                Height =
                    obj["height"]?.Value<int>() ??
                    obj["hedef_yukseklik"]?.Value<int>() ??
                    0
            };
        }

        private EventTimeDto ParseJetsonGpsTime(JToken token)
        {
            if (token == null)
                return null;

            try
            {
                return new EventTimeDto
                {
                    saat = token["saat"]?.Value<int>() ?? 0,
                    dakika = token["dakika"]?.Value<int>() ?? 0,
                    saniye = token["saniye"]?.Value<int>() ?? 0,
                    milisaniye = token["milisaniye"]?.Value<int>() ?? 0
                };
            }
            catch
            {
                return null;
            }
        }

        private TelemetryData ParseJetsonTelemetry(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return null;

            JObject obj = JObject.Parse(data);

            double latitude = obj["latitude"]?.Value<double>() ?? 0;
            double longitude = obj["longitude"]?.Value<double>() ?? 0;

            bool gpsFix =
                obj["gps_fix"]?.Value<bool>() ??
                !(latitude == 0 && longitude == 0);

            int batteryPercent = -1;

            JToken batteryToken = obj["battery"] ?? obj["battery_percent"];

            if (batteryToken != null)
            {
                batteryPercent = (int)Math.Round(batteryToken.Value<double>());
            }

            int satellites = 0;

            JToken satellitesToken = obj["satellites"] ?? obj["satellites_visible"];

            if (satellitesToken != null)
            {
                satellites = (int)Math.Round(satellitesToken.Value<double>());
            }

            return new TelemetryData
            {
                Latitude = latitude,
                Longitude = longitude,

                RelativeAltitudeMeters =
                    obj["relative_altitude"]?.Value<double>() ??
                    obj["altitude"]?.Value<double>() ??
                    0,

                AltitudeMeters =
                    obj["altitude"]?.Value<double>() ??
                    obj["relative_altitude"]?.Value<double>() ??
                    0,

                GroundSpeed =
                    obj["speed"]?.Value<double>() ??
                    obj["ground_speed"]?.Value<double>() ??
                    0,

                BatteryPercent = batteryPercent,

                PitchDegree =
                    obj["pitch"]?.Value<double>() ??
                    obj["pitch_degree"]?.Value<double>() ??
                    0,

                                RollDegree =
                    obj["roll"]?.Value<double>() ??
                    obj["roll_degree"]?.Value<double>() ??
                    0,

                GpsTime = ParseJetsonGpsTime(obj["gps_time"] ?? obj["gps_saati"]),

                HeadingDegree =
                    obj["heading"]?.Value<double>() ??
                    obj["yaw"]?.Value<double>() ??
                    0,

                FlightMode =
                    obj["flight_mode"]?.ToString() ??
                    "JETSON",

                HasGpsFix = gpsFix,

                SatellitesVisible = satellites
            };
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _webSocket?.Dispose();
        }
    }
}