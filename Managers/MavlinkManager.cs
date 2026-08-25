using System;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace UAV_GCS
{
    public class MavlinkManager : IDisposable
    {
        private readonly object _lockObject = new object();
        private UdpClient _udpClient;
        private bool _isRunning = false;
        private bool _isConnected = false;
        private Thread _readThread;
        private IPEndPoint _lastUdpRemoteEndPoint;
        private DateTime _lastTelemetryEventTime = DateTime.MinValue;

        private SerialPort _serialPort;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _readTask;

        private DateTime _lastHeartbeatTime = DateTime.MinValue;
        private System.Threading.Timer _connectionWatchdogTimer;

        private readonly MAVLink.MavlinkParse _mavlinkParser = new MAVLink.MavlinkParse();

        private byte _packetSequence = 0;

        // Varsayılan hedef ID'ler. Pixhawk/Cube çoğu sistemde 1/1 kullanır. Güncellenebilir.
        private byte _targetSystemId = 1;
        private byte _targetComponentId = 1;

        private const uint ARDUPLANE_MODE_AUTO = 10;
        private const uint ARDUPLANE_MODE_RTL = 11;
        private const uint ARDUPLANE_MODE_LOITER = 12;
        private const uint ARDUPLANE_MODE_TAKEOFF = 13;
        private const uint ARDUPLANE_MODE_GUIDED = 15;
        private const uint ARDUPLANE_MODE_QLAND = 23;

        private const ushort MAV_CMD_DO_SET_MODE = 176;
        private const ushort MAV_CMD_NAV_TAKEOFF = 22;
        private const byte MAV_MODE_FLAG_CUSTOM_MODE_ENABLED = 1;

        private const byte MAVLINK_STX = 0xFE;
        private const byte GCS_SYSTEM_ID = 255;
        private const byte GCS_COMPONENT_ID = 190;

        private const byte MSG_ID_COMMAND_LONG = 76;
        private const byte CRC_EXTRA_COMMAND_LONG = 152;

        private const uint MSG_ID_ATTITUDE = 30;

        public void SetModeAuto()
        {
            SendSetModeCommand(ARDUPLANE_MODE_AUTO, "AUTO");
        }

        public void SetModeGuided()
        {
            SendSetModeCommand(ARDUPLANE_MODE_GUIDED, "GUIDED");
        }

        public void SetModeRtl()
        {
            SendSetModeCommand(ARDUPLANE_MODE_RTL, "RTL");
        }

        public void SetModeLand()
        {
            SendSetModeCommand(ARDUPLANE_MODE_QLAND, "QLAND / OTONOM İNİŞ");
        }

        
        public void SetModeLoiter()
        {
            SendSetModeCommand(ARDUPLANE_MODE_LOITER, "LOITER");
        }

        public void SendTakeoffCommand(float altitudeMeters)
        {
            if (!IsConnected)
                throw new InvalidOperationException("MAVLink bağlantısı yok.");

            byte[] packet = BuildCommandLongPacket(
                targetSystem: _targetSystemId,
                targetComponent: _targetComponentId,
                command: MAV_CMD_NAV_TAKEOFF,
                confirmation: 0,
                param1: 0,
                param2: 0,
                param3: 0,
                param4: float.NaN,
                param5: 0,
                param6: 0,
                param7: altitudeMeters
            );

            SendMavlinkPacket(packet);

            LogRequested?.Invoke($"TAKEOFF komutu gönderildi. Hedef irtifa: {altitudeMeters:0} m");
        }

        public void ConnectUdp(int listenPort)
        {
            if (IsConnected)
                return;

            try
            {
                _udpClient?.Close();
                _udpClient?.Dispose();
                _udpClient = null;
            }
            catch { }

            _udpClient = new UdpClient(listenPort);

            _isRunning = true;
            _isConnected = false;
            _lastHeartbeatTime = DateTime.MinValue;

            ConnectionChanged?.Invoke(false);
            LogRequested?.Invoke($"MAVLink UDP dinleniyor. Port: {listenPort}. HEARTBEAT bekleniyor.");

            StartConnectionWatchdog();

            _readThread = new Thread(ReadUdpLoop)
            {
                IsBackground = true
            };

            _readThread.Start();
        }

        private void ReadUdpLoop()
        {
            IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

            while (_isRunning)
            {
                try
                {
                    byte[] buffer = _udpClient.Receive(ref remoteEndPoint);
                    _lastUdpRemoteEndPoint = remoteEndPoint;

                    if (buffer == null || buffer.Length == 0)
                        continue;

                    using (MemoryStream ms = new MemoryStream(buffer))
                    {
                        while (ms.Position < ms.Length)
                        {
                            MAVLink.MAVLinkMessage packet = _mavlinkParser.ReadPacket(ms);

                            if (packet != null)
                            {
                                ProcessPacket(packet);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                    {
                        LogRequested?.Invoke("UDP MAVLink okuma hatası: " + ex.Message);
                    }
                }
            }

            _isConnected = false;
            ConnectionChanged?.Invoke(false);
        }

        private void SendMavlinkPacket(byte[] packet)
        {
            if (packet == null || packet.Length == 0)
                return;

            // Eğer direkt seri port bağlantısı kullanılıyorsa
            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.Write(packet, 0, packet.Length);
                return;
            }

            // Eğer MAVProxy üzerinden UDP kullanılıyorsa
            if (_udpClient != null && _lastUdpRemoteEndPoint != null)
            {
                _udpClient.Send(packet, packet.Length, _lastUdpRemoteEndPoint);
                return;
            }

            throw new InvalidOperationException("MAVLink paket gönderilecek bağlantı bulunamadı.");
        }

        private void SendSetModeCommand(uint customMode, string modeName)
        {
            if (!IsConnected)
                throw new InvalidOperationException("MAVLink bağlantısı yok.");

            byte[] packet = BuildCommandLongPacket(
                targetSystem: _targetSystemId,
                targetComponent: _targetComponentId,
                command: MAV_CMD_DO_SET_MODE,
                confirmation: 0,
                param1: MAV_MODE_FLAG_CUSTOM_MODE_ENABLED,
                param2: customMode,
                param3: 0,
                param4: 0,
                param5: 0,
                param6: 0,
                param7: 0
            );

            SendMavlinkPacket(packet);

            LogRequested?.Invoke($"{modeName} mod komutu gönderildi.");
        }

        private byte[] BuildCommandLongPacket(
            byte targetSystem,
            byte targetComponent,
            ushort command,
            byte confirmation,
            float param1,
            float param2,
            float param3,
            float param4,
            float param5,
            float param6,
            float param7)
        {
            byte payloadLength = 33;
            byte[] payload = new byte[payloadLength];

            int index = 0;

            WriteFloat(payload, ref index, param1);
            WriteFloat(payload, ref index, param2);
            WriteFloat(payload, ref index, param3);
            WriteFloat(payload, ref index, param4);
            WriteFloat(payload, ref index, param5);
            WriteFloat(payload, ref index, param6);
            WriteFloat(payload, ref index, param7);

            WriteUInt16(payload, ref index, command);

            payload[index++] = targetSystem;
            payload[index++] = targetComponent;
            payload[index++] = confirmation;

            byte sequence = _packetSequence++;

            byte[] packetWithoutChecksum = new byte[6 + payloadLength];

            packetWithoutChecksum[0] = MAVLINK_STX;
            packetWithoutChecksum[1] = payloadLength;
            packetWithoutChecksum[2] = sequence;
            packetWithoutChecksum[3] = GCS_SYSTEM_ID;
            packetWithoutChecksum[4] = GCS_COMPONENT_ID;
            packetWithoutChecksum[5] = MSG_ID_COMMAND_LONG;

            Array.Copy(payload, 0, packetWithoutChecksum, 6, payloadLength);

            ushort checksum = CalculateMavlinkChecksum(
                packetWithoutChecksum,
                payloadLength,
                CRC_EXTRA_COMMAND_LONG
            );

            byte[] finalPacket = new byte[packetWithoutChecksum.Length + 2];

            Array.Copy(packetWithoutChecksum, finalPacket, packetWithoutChecksum.Length);

            finalPacket[finalPacket.Length - 2] = (byte)(checksum & 0xFF);
            finalPacket[finalPacket.Length - 1] = (byte)(checksum >> 8);

            return finalPacket;
        }

        private void WriteFloat(byte[] buffer, ref int index, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Array.Copy(bytes, 0, buffer, index, 4);
            index += 4;
        }

        private void WriteUInt16(byte[] buffer, ref int index, ushort value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Array.Copy(bytes, 0, buffer, index, 2);
            index += 2;
        }

        private ushort CalculateMavlinkChecksum(byte[] packet, byte payloadLength, byte crcExtra)
        {
            ushort crc = 0xFFFF;

            // MAVLink v1 checksum STX byte hariç hesaplanır.
            // Yani index 1'den başlıyoruz.
            for (int i = 1; i < 6 + payloadLength; i++)
            {
                AccumulateChecksum(packet[i], ref crc);
            }

            AccumulateChecksum(crcExtra, ref crc);

            return crc;
        }

        private void AccumulateChecksum(byte data, ref ushort crc)
        {
            byte tmp = (byte)(data ^ (byte)(crc & 0xFF));
            tmp ^= (byte)(tmp << 4);

            crc = (ushort)(
                (crc >> 8) ^
                (tmp << 8) ^
                (tmp << 3) ^
                (tmp >> 4)
            );
        }

        private void ProcessAttitude(MAVLink.MAVLinkMessage packet)
        {
            MAVLink.mavlink_attitude_t attitude =
                (MAVLink.mavlink_attitude_t)packet.data;

            _latestTelemetry.RollDegree = attitude.roll * 180.0 / Math.PI;
            _latestTelemetry.PitchDegree = attitude.pitch * 180.0 / Math.PI;
        }
        private TelemetryData _latestTelemetry = new TelemetryData
        {
            TeamId = 0,
            FlightMode = "--",
            BatteryPercent = -1
        };

        public bool IsConnected
        {
            get
            {
                return _isConnected;
            }
        }

        public event Action<TelemetryData> TelemetryReceived;
        public event Action<bool> ConnectionChanged;
        public event Action<string> LogRequested;

        public void Connect(string portName, int baudRate)
        {
            if (IsConnected)
            {
                LogRequested?.Invoke("MAVLink zaten bağlı.");
                return;
            }

            try
            {
                _serialPort = new SerialPort(portName, baudRate)
                {
                    DataBits = 8,
                    Parity = Parity.None,
                    StopBits = StopBits.One,
                    ReadTimeout = 1000,
                    WriteTimeout = 1000,
                    DtrEnable = true,
                    RtsEnable = true
                };

                _serialPort.Open();
                _isConnected = true;
                _cancellationTokenSource = new CancellationTokenSource();
                _readTask = Task.Run(() => ReadLoop(_cancellationTokenSource.Token));

                ConnectionChanged?.Invoke(true);
                LogRequested?.Invoke($"MAVLink bağlantısı açıldı: {portName} / {baudRate}");
            }
            catch (Exception ex)
            {
                ConnectionChanged?.Invoke(false);
                LogRequested?.Invoke("MAVLink bağlantısı açılamadı: " + ex.Message);
                throw;
            }
        }

        public void Disconnect()
        {
            _isRunning = false;

            try
            {
                _cancellationTokenSource?.Cancel();
            }
            catch { }

            try
            {
                if (_serialPort != null && _serialPort.IsOpen)
                    _serialPort.Close();

                _serialPort?.Dispose();
                _serialPort = null;
            }
            catch { }

            try
            {
                _udpClient?.Close();
                _udpClient?.Dispose();
                _udpClient = null;
            }
            catch { }

            try
            {
                _connectionWatchdogTimer?.Dispose();
                _connectionWatchdogTimer = null;
            }
            catch { }

            _isConnected = false;
            _lastHeartbeatTime = DateTime.MinValue;

            _latestTelemetry = new TelemetryData
            {
                TeamId = 0,
                FlightMode = "--",
                BatteryPercent = -1,
                HasGpsFix = false,
                SatellitesVisible = 0
            };

            ConnectionChanged?.Invoke(false);

            LogRequested?.Invoke("MAVLink bağlantısı kapatıldı.");
        }

        private void ReadLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_serialPort == null || !_serialPort.IsOpen)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    MAVLink.MAVLinkMessage packet = _mavlinkParser.ReadPacket(_serialPort.BaseStream);

                    if (packet == null)
                        continue;

                    ProcessPacket(packet);
                }
                catch (TimeoutException)
                {
                    // Normal. Seri portta o an veri yoksa geçiyoruz.
                }
                catch (Exception ex)
                {
                    _isConnected = false;
                    LogRequested?.Invoke("MAVLink okuma hatası: " + ex.Message);
                    ConnectionChanged?.Invoke(false);
                    Thread.Sleep(500);
                }
            }
        }

        private const uint MSG_ID_HEARTBEAT = 0;
        private const uint MSG_ID_SYS_STATUS = 1;
        private const uint MSG_ID_GPS_RAW_INT = 24;
        private const uint MSG_ID_GLOBAL_POSITION_INT = 33;
        private const uint MSG_ID_VFR_HUD = 74;

        private void ProcessPacket(MAVLink.MAVLinkMessage packet)
        {
            if (packet == null)
                return;

            TelemetryData telemetryToSend = null;

            lock (_lockObject)
            {
                bool telemetryChanged = false;

                switch (packet.msgid)
                {
                    case MSG_ID_HEARTBEAT:
                        ProcessHeartbeat(packet);
                        telemetryChanged = true;
                        break;

                    case MSG_ID_GLOBAL_POSITION_INT:
                        ProcessGlobalPosition(packet);
                        telemetryChanged = true;
                        break;

                    case MSG_ID_VFR_HUD:
                        ProcessVfrHud(packet);
                        telemetryChanged = true;
                        break;

                    case MSG_ID_SYS_STATUS:
                        ProcessSystemStatus(packet);
                        telemetryChanged = true;
                        break;

                    case MSG_ID_GPS_RAW_INT:
                        ProcessGpsRaw(packet);
                        telemetryChanged = true;
                        break;

                    case MSG_ID_ATTITUDE:
                        ProcessAttitude(packet);
                        telemetryChanged = true;
                        break;

                    default:
                        return;
                }

                if (!telemetryChanged)
                    return;

                // UI'yi boğmamak için en fazla 5 Hz güncelleme
                if ((DateTime.Now - _lastTelemetryEventTime).TotalMilliseconds < 200)
                    return;

                _lastTelemetryEventTime = DateTime.Now;
                telemetryToSend = CloneTelemetry(_latestTelemetry);
            }

            if (telemetryToSend != null)
            {
                TelemetryReceived?.Invoke(telemetryToSend);
            }
        }

        private void StartConnectionWatchdog()
        {
            _connectionWatchdogTimer?.Dispose();

            _connectionWatchdogTimer = new System.Threading.Timer(_ =>
            {
                if (!_isRunning)
                    return;

                if (!_isConnected)
                    return;

                double secondsSinceHeartbeat = (DateTime.Now - _lastHeartbeatTime).TotalSeconds;

                if (secondsSinceHeartbeat > 3)
                {
                    _isConnected = false;
                    ConnectionChanged?.Invoke(false);
                    LogRequested?.Invoke("MAVLink HEARTBEAT kesildi. Bağlantı pasif.");
                }

            }, null, 1000, 1000);
        }

        private void ProcessHeartbeat(MAVLink.MAVLinkMessage packet)
        {
            MAVLink.mavlink_heartbeat_t heartbeat =
                (MAVLink.mavlink_heartbeat_t)packet.data;

            // MAV_AUTOPILOT_ARDUPILOTMEGA = 3
            // Mission Planner / MAVProxy / GCS heartbeat'lerini uçuş modu gibi okumayalım.
            if ((int)heartbeat.autopilot != 3)
                return;

            // MAV_TYPE_GCS = 6
            // Ek güvenlik: GCS tipindekileri de alma.
            if ((int)heartbeat.type == 6)
                return;

            _lastHeartbeatTime = DateTime.Now;

            if (!_isConnected)
            {
                _isConnected = true;
                ConnectionChanged?.Invoke(true);
                LogRequested?.Invoke("MAVLink HEARTBEAT alındı. Bağlantı aktif.");
            }

            _latestTelemetry.FlightMode = DecodeFlightMode(heartbeat.custom_mode);
        }

        private void ProcessGlobalPosition(MAVLink.MAVLinkMessage packet)
        {
            MAVLink.mavlink_global_position_int_t position =
                (MAVLink.mavlink_global_position_int_t)packet.data;

            _latestTelemetry.Latitude = position.lat / 10000000.0;
            _latestTelemetry.Longitude = position.lon / 10000000.0;
            _latestTelemetry.AltitudeMeters = position.alt / 1000.0;
            _latestTelemetry.RelativeAltitudeMeters = position.relative_alt / 1000.0;
            _latestTelemetry.HeadingDegree = position.hdg / 100.0;
        }

        private void ProcessVfrHud(MAVLink.MAVLinkMessage packet)
        {
            MAVLink.mavlink_vfr_hud_t vfr =
                (MAVLink.mavlink_vfr_hud_t)packet.data;

            _latestTelemetry.AirSpeed = vfr.airspeed;
            _latestTelemetry.GroundSpeed = vfr.groundspeed;

            // İrtifayı burada değiştirmiyoruz.
            // RelativeAltitudeMeters sadece GLOBAL_POSITION_INT içindeki relative_alt'tan gelsin.
        }

        private void ProcessSystemStatus(MAVLink.MAVLinkMessage packet)
        {
            MAVLink.mavlink_sys_status_t status =
                (MAVLink.mavlink_sys_status_t)packet.data;

            _latestTelemetry.BatteryPercent = status.battery_remaining;
        }

        private void ProcessGpsRaw(MAVLink.MAVLinkMessage packet)
        {
            MAVLink.mavlink_gps_raw_int_t gps =
                (MAVLink.mavlink_gps_raw_int_t)packet.data;

            _latestTelemetry.HasGpsFix = gps.fix_type >= 3;
            _latestTelemetry.SatellitesVisible = gps.satellites_visible;

            EventTimeDto gpsTime = ConvertGpsTimeUsecToEventTime(gps.time_usec);

            if (gpsTime != null)
                _latestTelemetry.GpsTime = gpsTime;
        }

        private EventTimeDto ConvertGpsTimeUsecToEventTime(ulong timeUsec)
        {
            // MAVLink GPS_RAW_INT time_usec bazen Unix epoch, bazen boot time olabilir.
            // 2000 yılından küçükse gerçek UTC saymayalım.
            if (timeUsec < 946684800000000UL)
                return null;

            DateTimeOffset gpsDateTime =
                DateTimeOffset.FromUnixTimeMilliseconds((long)(timeUsec / 1000));

            DateTime utc = gpsDateTime.UtcDateTime;

            return new EventTimeDto
            {
                saat = utc.Hour,
                dakika = utc.Minute,
                saniye = utc.Second,
                milisaniye = utc.Millisecond
            };
        }

        private string DecodeFlightMode(uint customMode)
        {
            // ArduPilot Plane için temel mod isimleri.
            // Cube/ArduPilot kullanıldığı için başlangıçta bu eşleme yeterli.
            switch (customMode)
            {
                case 0:
                    return "MANUAL";
                case 1:
                    return "CIRCLE";
                case 2:
                    return "STABILIZE";
                case 3:
                    return "TRAINING";
                case 4:
                    return "ACRO";
                case 5:
                    return "FBWA";
                case 6:
                    return "FBWB";
                case 7:
                    return "CRUISE";
                case 8:
                    return "AUTOTUNE";
                case 10:
                    return "AUTO";
                case 11:
                    return "RTL";
                case 12:
                    return "LOITER";
                case 13:
                    return "TAKEOFF";
                case 15:
                    return "GUIDED";
                case 20:
                    return "QSTABILIZE";
                case 21:
                    return "QHOVER";
                case 22:
                    return "QLOITER";
                case 23:
                    return "QLAND";
                case 24:
                    return "QRTL";
                case 25:
                    return "LOITER_TO_QLAND";
                default:
                    return "MODE " + customMode;
            }
        }

        private TelemetryData CloneTelemetry(TelemetryData data)
        {
            return new TelemetryData
            {
                TeamId = data.TeamId,
                Latitude = data.Latitude,
                Longitude = data.Longitude,
                AltitudeMeters = data.AltitudeMeters,
                RelativeAltitudeMeters = data.RelativeAltitudeMeters,
                GroundSpeed = data.GroundSpeed,
                AirSpeed = data.AirSpeed,
                BatteryPercent = data.BatteryPercent,
                FlightMode = data.FlightMode,
                HasGpsFix = data.HasGpsFix,
                SatellitesVisible = data.SatellitesVisible,
                HeadingDegree = data.HeadingDegree,
                PitchDegree = data.PitchDegree,
                RollDegree = data.RollDegree,
                GpsTime = data.GpsTime,
                IsTrackingTarget = data.IsTrackingTarget,
                TargetCenterX = data.TargetCenterX,
                TargetCenterY = data.TargetCenterY,
                TargetWidth = data.TargetWidth,
                TargetHeight = data.TargetHeight,
            };
        }

        public void Dispose()
        {
            Disconnect();
            _cancellationTokenSource?.Dispose();
        }
    }
}