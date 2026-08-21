using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

            await SubscribeAsync("/kayzer/kilitlenme_basarili", "std_msgs/msg/String");
            await SubscribeAsync("/kayzer/real_state", "std_msgs/msg/String");

            await SubscribeAsync("/kayzer/vision/target_error_x", "std_msgs/msg/Float64");
            await SubscribeAsync("/kayzer/vision/target_error_y", "std_msgs/msg/Float64");

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
            }
            catch (Exception ex)
            {
                LogRequested?.Invoke("Jetson mesajı çözümlenemedi: " + ex.Message);
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _webSocket?.Dispose();
        }
    }
}