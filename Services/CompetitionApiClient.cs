using Newtonsoft.Json;
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace UAV_GCS
{
    public class CompetitionApiClient : IDisposable
    {
        private readonly HttpClient _httpClient;

        public string BaseUrl { get; }
        public bool IsLoggedIn { get; private set; }
        public int TeamNumber { get; private set; }

        public CompetitionApiClient(string baseUrl)
        {
            BaseUrl = baseUrl.TrimEnd('/');

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(BaseUrl),
                Timeout = TimeSpan.FromSeconds(20)
            };
        }

        public async Task<int> LoginAsync(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new Exception("Kullanıcı adı boş olamaz.");

            if (string.IsNullOrWhiteSpace(password))
                throw new Exception("Şifre boş olamaz.");

            var request = new LoginRequestDto
            {
                kadi = username,
                sifre = password
            };

            using (HttpResponseMessage response = await PostJsonAsync("/api/giris", request))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    IsLoggedIn = false;
                    TeamNumber = 0;
                    await ThrowHttpErrorAsync(response, "Sunucu girişi");
                }

                string content = await response.Content.ReadAsStringAsync();

                if (!int.TryParse(content, out int teamNumber))
                {
                    IsLoggedIn = false;
                    TeamNumber = 0;
                    throw new Exception("Sunucudan takım numarası okunamadı. Cevap: " + content);
                }

                TeamNumber = teamNumber;
                IsLoggedIn = true;

                return teamNumber;
            }
        }

        public async Task<ServerTimeDto> GetServerTimeAsync()
        {
            EnsureLoggedIn();

            using (HttpResponseMessage response = await _httpClient.GetAsync("/api/sunucusaati"))
            {
                await EnsureOkStatusAsync(response, "Sunucu saati alma");

                string content = await response.Content.ReadAsStringAsync();

                return JsonConvert.DeserializeObject<ServerTimeDto>(content);
            }
        }

        public async Task<TelemetryResponseDto> SendTelemetryAsync(TelemetryRequestDto telemetry)
        {
            EnsureLoggedIn();

            using (HttpResponseMessage response = await PostJsonAsync("/api/telemetri_gonder", telemetry))
            {
                await EnsureOkStatusAsync(response, "Telemetri gönderme");

                string content = await response.Content.ReadAsStringAsync();

                return JsonConvert.DeserializeObject<TelemetryResponseDto>(content);
            }
        }

        public async Task SendLockInfoAsync(LockInfoRequestDto lockInfo)
        {
            EnsureLoggedIn();

            using (HttpResponseMessage response = await PostJsonAsync("/api/kilitlenme_bilgisi", lockInfo))
            {
                await EnsureOkStatusAsync(response, "Kilitlenme bilgisi gönderme");
            }
        }

        public async Task SendKamikazeInfoAsync(KamikazeInfoRequestDto kamikazeInfo)
        {
            EnsureLoggedIn();

            using (HttpResponseMessage response = await PostJsonAsync("/api/kamikaze_bilgisi", kamikazeInfo))
            {
                await EnsureOkStatusAsync(response, "Kamikaze bilgisi gönderme");
            }
        }

        public async Task<QrCoordinateResponseDto> GetQrCoordinateAsync()
        {
            EnsureLoggedIn();

            using (HttpResponseMessage response = await _httpClient.GetAsync("/api/qr_koordinati"))
            {
                await EnsureOkStatusAsync(response, "QR koordinatı alma");

                string content = await response.Content.ReadAsStringAsync();

                return JsonConvert.DeserializeObject<QrCoordinateResponseDto>(content);
            }
        }

        public async Task<HssResponseDto> GetHssCoordinatesAsync()
        {
            EnsureLoggedIn();

            using (HttpResponseMessage response = await _httpClient.GetAsync("/api/hss_koordinatlari"))
            {
                await EnsureOkStatusAsync(response, "HSS koordinatları alma");

                string content = await response.Content.ReadAsStringAsync();

                return JsonConvert.DeserializeObject<HssResponseDto>(content);
            }
        }

        public async Task<InitialServerDataDto> GetInitialServerDataAsync()
        {
            EnsureLoggedIn();

            ServerTimeDto serverTime = await GetServerTimeAsync();
            HssResponseDto hss = await GetHssCoordinatesAsync();
            QrCoordinateResponseDto qr = await GetQrCoordinateAsync();

            return new InitialServerDataDto
            {
                ServerTime = serverTime,
                Hss = hss,
                Qr = qr
            };
        }

        private async Task<HttpResponseMessage> PostJsonAsync<T>(string endpoint, T data)
        {
            string json = JsonConvert.SerializeObject(data);

            Debug.WriteLine("POST " + endpoint);

            if (endpoint != "/api/giris")
                Debug.WriteLine(json);

            var content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json"
            );

            return await _httpClient.PostAsync(endpoint, content);
        }

        private async Task EnsureOkStatusAsync(HttpResponseMessage response, string operationName)
        {
            if (response.StatusCode != HttpStatusCode.OK)
                await ThrowHttpErrorAsync(response, operationName);
        }
        private void EnsureLoggedIn()
        {
            if (!IsLoggedIn)
                throw new Exception("Sunucu oturumu açık değil. Önce giriş yapılmalı.");
        }

        private async Task ThrowHttpErrorAsync(HttpResponseMessage response, string operationName)
        {
            string content = "";

            try
            {
                content = await response.Content.ReadAsStringAsync();
            }
            catch
            {
                content = "";
            }

            string statusMessage;

            switch ((int)response.StatusCode)
            {
                case 204:
                    statusMessage = "204 - Gönderilen paketin formatı yanlış.";
                    break;

                case 400:
                    statusMessage = "400 - İstek hatalı veya geçersiz.";
                    break;

                case 401:
                    statusMessage = "401 - Kimliksiz erişim. Önce sunucuya giriş yapılmalı.";
                    break;

                case 403:
                    statusMessage = "403 - Yetkisiz erişim.";
                    break;

                case 404:
                    statusMessage = "404 - Geçersiz URL.";
                    break;

                case 500:
                    statusMessage = "500 - Sunucu içi hata.";
                    break;

                default:
                    statusMessage = $"{(int)response.StatusCode} - Bilinmeyen sunucu hatası.";
                    break;
            }

            if (!string.IsNullOrWhiteSpace(content))
                throw new Exception($"{operationName} başarısız. {statusMessage} Cevap: {content}");

            throw new Exception($"{operationName} başarısız. {statusMessage}");
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}