using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace UAV_GCS
{
    public class CameraManager : IDisposable
    {
        private readonly TableLayoutPanel _parentLayout;
        private readonly int _column;
        private readonly int _row;

        private LibVLC _libVLC;
        private MediaPlayer _mediaPlayer;
        private VideoView _videoView;
        private GroupBox _cameraGroupBox;
        private Label _statusLabel;

        private static bool _libVlcInitialized = false;

        public event Action<string> LogRequested;
        public event Action<bool> ConnectionChanged;

        public DateTime LastFrameReceivedTime { get; private set; } = DateTime.MinValue;
        public bool IsStreamActive { get; private set; } = false;

        public CameraManager(TableLayoutPanel parentLayout, int column, int row)
        {
            _parentLayout = parentLayout;
            _column = column;
            _row = row;
        }

        public void Initialize()
        {
            if (!_libVlcInitialized)
            {
                Core.Initialize();
                _libVlcInitialized = true;
            }

            _libVLC = new LibVLC(
                "--rtsp-tcp",
                "--network-caching=500",
                "--live-caching=500",
                "--avcodec-hw=none"
            );
            _mediaPlayer = new MediaPlayer(_libVLC);

            _mediaPlayer.Playing += (s, e) =>
            {
                IsStreamActive = true;
                LastFrameReceivedTime = DateTime.Now;

                SetStatusSafe("Kamera bağlantısı aktif.", Color.Lime);

                LogRequested?.Invoke("Kamera stream aktif.");
                ConnectionChanged?.Invoke(true);
            };

            _mediaPlayer.TimeChanged += (s, e) =>
            {
                LastFrameReceivedTime = DateTime.Now;
            };

            _mediaPlayer.PositionChanged += (s, e) =>
            {
                LastFrameReceivedTime = DateTime.Now;
            };

            _mediaPlayer.EncounteredError += (s, e) =>
            {
                IsStreamActive = false;
                LastFrameReceivedTime = DateTime.MinValue;

                SetStatusSafe("Kamera bağlantı hatası.", Color.Red);

                LogRequested?.Invoke("Kamera stream hatası oluştu.");
                ConnectionChanged?.Invoke(false);
            };

            _mediaPlayer.Stopped += (s, e) =>
            {
                IsStreamActive = false;
                LastFrameReceivedTime = DateTime.MinValue;

                SetStatusSafe("Kamera durduruldu.", Color.OrangeRed);

                LogRequested?.Invoke("Kamera stream durdu.");
                ConnectionChanged?.Invoke(false);
            };

            _mediaPlayer.EndReached += (s, e) =>
            {
                IsStreamActive = false;
                LastFrameReceivedTime = DateTime.MinValue;

                SetStatusSafe("Kamera stream sona erdi.", Color.OrangeRed);

                LogRequested?.Invoke("Kamera stream sona erdi.");
                ConnectionChanged?.Invoke(false);
            };


            _cameraGroupBox = new GroupBox
            {
                Text = "Kamera Paneli",
                Dock = DockStyle.Fill,
                ForeColor = Color.Teal,
                Font = new Font("Microsoft Tai Le", 9.75F, FontStyle.Bold)
            };

            Panel cameraContainer = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };

            _videoView = new VideoView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                MediaPlayer = _mediaPlayer
            };

            _statusLabel = new Label
            {
                Text = "Kamera bağlantısı bekleniyor...",
                Dock = DockStyle.Bottom,
                Height = 28,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.Gold,
                BackColor = Color.FromArgb(0, 30, 30),
                Font = new Font("Microsoft Sans Serif", 9F, FontStyle.Bold)
            };

            cameraContainer.Controls.Add(_videoView);
            cameraContainer.Controls.Add(_statusLabel);

            _cameraGroupBox.Controls.Add(cameraContainer);
            _parentLayout.Controls.Add(_cameraGroupBox, _column, _row);

            LogRequested?.Invoke("Kamera paneli oluşturuldu.");
        }

        public void StartStream(string streamUrl)
        {
            if (string.IsNullOrWhiteSpace(streamUrl))
            {
                MessageBox.Show("Kamera stream adresi boş olamaz.");
                return;
            }

            try
            {
                
                StopStream();

                if (_libVLC == null || _mediaPlayer == null)
                {
                    SetStatusSafe("Kamera başlatılamadı.", Color.Red);
                    LogRequested?.Invoke("Kamera başlatılamadı: LibVLC hazır değil.");
                    ConnectionChanged?.Invoke(false);
                    return;
                }

                using (Media media = new Media(_libVLC, streamUrl, FromType.FromLocation))
                {
                    // RTSP'yi UDP yerine TCP ile alır.
                    // Paket kaybı varsa görüntü bozulmasını azaltabilir.
                    media.AddOption(":rtsp-tcp");

                    // Kopma / bloklanma varsa biraz tampon koyar.
                    // Gecikme biraz artabilir ama görüntü daha stabil olur.
                    media.AddOption(":network-caching=500");
                    media.AddOption(":live-caching=500");

                    // Bazı bilgisayarlarda donanımsal çözme görüntüyü bozabiliyor.
                    media.AddOption(":avcodec-hw=none");

                    // Geç kalan frame'leri direkt atlama davranışını azaltır.
                    media.AddOption(":no-drop-late-frames");
                    media.AddOption(":no-skip-frames");

                    _mediaPlayer.Play(media);
                }

                SetStatusSafe("Kamera stream başlatılıyor...", Color.Gold);
                LogRequested?.Invoke("Kamera stream başlatılıyor: " + streamUrl);
            }
            catch (Exception ex)
            {
                SetStatusSafe("Kamera başlatılamadı.", Color.Red);
                ConnectionChanged?.Invoke(false);
                LogRequested?.Invoke("Kamera hatası: " + ex.Message);
                MessageBox.Show("Kamera başlatılamadı: " + ex.Message);
            }
        }

        public void StopStream()
        {
            IsStreamActive = false;
            LastFrameReceivedTime = DateTime.MinValue;

            bool wasPlaying = _mediaPlayer != null && _mediaPlayer.IsPlaying;

            if (wasPlaying)
            {
                _mediaPlayer.Stop();
            }
        }


        private void SetStatus(string text, Color color)
        {
            if (_statusLabel == null)
                return;

            _statusLabel.Text = text;
            _statusLabel.ForeColor = color;
        }

        private void SetStatusSafe(string text, Color color)
        {
            if (_statusLabel == null)
                return;

            if (_statusLabel.InvokeRequired)
            {
                _statusLabel.BeginInvoke(new Action(() =>
                {
                    SetStatus(text, color);
                }));
            }
            else
            {
                SetStatus(text, color);
            }
        }

        public void Dispose()
        {
            if (_mediaPlayer != null)
            {
                

                if (_mediaPlayer.IsPlaying)
                    _mediaPlayer.Stop();

                _mediaPlayer.Dispose();
                _mediaPlayer = null;
            }

            _videoView?.Dispose();
            _videoView = null;

            _cameraGroupBox?.Dispose();
            _cameraGroupBox = null;

            _libVLC?.Dispose();
            _libVLC = null;
        }
    }
}