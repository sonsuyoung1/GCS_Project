using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GCS_Project.Models;
using GCS_Project.xServices;

namespace GCS_Project.Services
{
    public class MavlinkService
    {
        #region [1. 상수 및 필드 (Constants & Fields)]
        private const int Port = 14550;
        private const string DroneIp = "192.168.1.136";

        private byte _sequence = 0; // 패킷 순서 번호 (보낼 때마다 1씩 증가)
        private const byte MSG_ID_HEARTBEAT = 0;
        private const byte MSG_ID_GLOBAL_POSITION_INT = 33;
        private const byte MSG_ID_YOLO_TARGET = 250; // ROS2 커스텀 메시지 ID
        private const byte MSG_ID_SYS_STATUS = 1;
        private const byte MSG_ID_VFR_HUD = 74;

        private UdpClient? _udpClient;
        private CancellationTokenSource? _cts;
        private bool _isMissionRunning = false;
        private readonly List<GeoPoint> _waypoints = new List<GeoPoint>();
        #endregion

        #region [2. 속성 및 상태 (Properties & State)]
        public DroneState CurrentState { get; } = new DroneState();
        public ObservableCollection<DetectedTarget> DetectedTargets { get; } = new ObservableCollection<DetectedTarget>();
        #endregion

        #region [3. 이벤트 (Events)]
        public event Action<int, double, double, string, bool>? OnTargetLocationUpdated;
        public event Action<DroneState>? OnDataUpdated;
        public event Action<int>? OnWaypointCountChanged;
        public event Action? OnMissionCompleted;
        public event Action<string>? OnLogReceived;
        #endregion

        #region [4. 생성자 및 초기화 (Constructor & Lifecycle)]
        public MavlinkService()
        {
            // 초기 위치 설정 (한서대학교)
            CurrentState.Latitude = 36.6906;
            CurrentState.Longitude = 126.5820;
            CurrentState.Altitude = 0;
            CurrentState.IsConnected = false;
        }

        public void Start()
        {
            if (_udpClient != null) return;
            _cts = new CancellationTokenSource();

            try
            {
                _udpClient = new UdpClient();
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, Port));

                CurrentState.IsConnected = false;
                OnDataUpdated?.Invoke(CurrentState);

                // 수신 루프 시작
                Task.Run(() => ReceiveData(_cts.Token), _cts.Token);
                StartHeartbeatLoop(_cts.Token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MAVLINK] 초기화 실패: {ex.Message}");
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            _udpClient?.Close();
            _udpClient = null;
            CurrentState.IsConnected = false;
        }

        // 1초 간격으로 생존 신호를 송신.
        private void StartHeartbeatLoop(CancellationToken token)
        {
            Task.Run(async () => {
                while (!token.IsCancellationRequested)
                {
                    // 1. 전체 패킷 배열 준비 (헤더 10 + 페이로드 9 + CRC 2 = 21바이트)
                    byte[] packet = new byte[21];

                    // [헤더 조립]
                    packet[0] = 0xFD;           // STX (v2 시작 마커)
                    packet[1] = 9;              // Payload Length (하트비트는 9바이트)
                    packet[2] = 0x00;           // Incompatibility Flags
                    packet[3] = 0x00;           // Compatibility Flags
                    packet[4] = _sequence++;    // Sequence Number
                    packet[5] = 255;            // System ID (GCS는 보통 255)
                    packet[6] = 1;              // Component ID
                    packet[7] = 0;              // Message ID (Heartbeat = 0)
                    packet[8] = 0x00;           // Message ID 확장
                    packet[9] = 0x00;

                    // [페이로드 조립 - 하트비트 전용 데이터 9바이트]
                    // Offset 10: Custom Mode (4 bytes, GCS는 0으로 설정)
                    BitConverter.GetBytes(0).CopyTo(packet, 10);

                    packet[14] = 6;    // Type: MAV_TYPE_GCS (6)
                    packet[15] = 8;    // Autopilot: MAV_AUTOPILOT_INVALID (8)
                    packet[16] = 0;    // Base Mode
                    packet[17] = 4;    // System Status: MAV_STATE_ACTIVE (4)
                    packet[18] = 3;    // MAVLink Version (v2는 3으로 표기)

                    // [3. 체크섬(CRC) 계산]
                    // 하트비트(MSG ID 0)의 매직 넘버(CRC_EXTRA)는 50입니다.
                    ushort crc = MavlinkCRC.CalculateCRC(packet, 1, packet.Length - 3);
                    crc = MavlinkCRC.AccumulateCRC((byte)50, crc);

                    BitConverter.GetBytes(crc).CopyTo(packet, packet.Length - 2);

                    // [4. 실제 송신]
                    await SendPacket(packet);

                    await Task.Delay(1000); // 1초 주기로 반복
                }
            }, token);
        }
        #endregion

        #region [5. 데이터 수신 및 파싱 (Rx Logic)]
        private async Task ReceiveData(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpClient!.ReceiveAsync();
                    byte[] buffer = result.Buffer;

                    // MAVLink 프로토콜 버전 체크 (0xFD: v2, 0xFE: v1)
                    if (buffer.Length > 0 && (buffer[0] == 0xFD || buffer[0] == 0xFE))
                    {
                        if (!CurrentState.IsConnected)
                        {
                            CurrentState.IsConnected = true;
                            OnDataUpdated?.Invoke(CurrentState);
                        }
                        ParseMavlinkBinary(buffer);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RX Error] {ex.Message}");
                }
            }
        }

        private void ParseMavlinkBinary(byte[] packet)
        {
            if (packet.Length < 10) 
                return;
            int msgId = packet[7]; // MAVLink v2 기준에서 메세지의 정체(ID)를 알려주는 숫자는 8번째 칸(index 7)에 들어있다.

            switch (msgId)
            {
                case MSG_ID_SYS_STATUS: // 배터리 잔량 업데이트
                                                               // v2 기준 페이로드 시작(10) + 30바이트 부근에 battery_remaining(%) 위치
                                                               // 정확한 오프셋은 MAVLink 라이브러리 규격을 따르지만, 여기선 예시로 처리
                    CurrentState.BatteryPercent = packet[10 + 30];
                    break;

                case MSG_ID_VFR_HUD: // 속도 데이터 업데이트
                                                          // groundspeed는 페이로드 시작(10) 후 4바이트(float) 지점에 위치
                    float groundSpeed = BitConverter.ToSingle(packet, 10 + 4);
                    CurrentState.Speed = Math.Round(groundSpeed, 2);
                    break;
                case MSG_ID_HEARTBEAT:
                    CurrentState.IsConnected = true;
                    break;
                case MSG_ID_GLOBAL_POSITION_INT:
                    UpdateDroneLocation(packet);
                    break;
                case MSG_ID_YOLO_TARGET:
                    UpdateYoloTargets(packet);
                    break;
                case 77: // MSG_ID_COMMAND_ACK
                    int result = packet[15]; // 0이면 성공, 나머지는 에러 코드
                    string msg = (result == 0) ? "[시스템] 명령 수행 성공" : $"[경고] 명령 거부됨 (코드: {result})";

                    // ChatLog 대신 이벤트를 발생시킵니다!
                    OnLogReceived?.Invoke(msg);
                    break;
            }
            OnDataUpdated?.Invoke(CurrentState);
        }

        private void UpdateDroneLocation(byte[] packet)
        {
            // degE7 단위를 소수점 좌표로 변환
            CurrentState.Latitude = BitConverter.ToInt32(packet, 10) / 1E7;
            CurrentState.Longitude = BitConverter.ToInt32(packet, 14) / 1E7;
            CurrentState.Altitude = BitConverter.ToInt32(packet, 22) / 1000.0;
        }

        private void UpdateYoloTargets(byte[] packet)
        {
            int id = packet[10];
            double lat = BitConverter.ToInt32(packet, 11) / 1E7;
            double lng = BitConverter.ToInt32(packet, 15) / 1E7;
            string targetType = "Person";

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var target = DetectedTargets.FirstOrDefault(t => t.ID == id);
                bool isTracked = false;

                if (target != null)
                {
                    target.Lat = lat;
                    target.Lng = lng;
                    isTracked = target.IsBeingTracked;
                }
                else
                {
                    DetectedTargets.Add(new DetectedTarget { ID = id, Lat = lat, Lng = lng, TargetType = targetType });
                    System.Diagnostics.Debug.WriteLine($"[YOLO] 새 객체 탐지: ID {id}");
                }
                OnTargetLocationUpdated?.Invoke(id, lat, lng, targetType, isTracked);
            });
        }
        #endregion

        #region [6. 명령 송신 로직 (Tx Logic)]

        // 조립이 완료된 바이너리 데이터(byte[])를 UDP 프로토콜을 이용해 실제 우분투 IP 주소로 보내줌
        private async Task SendPacket(byte[] packet)
        {
            if (_udpClient != null)
            {
                try { await _udpClient.SendAsync(packet, packet.Length, DroneIp, Port); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[TX Error] {ex.Message}"); }
            }
        }

        // 명령어를 MAVLink 규격 봉투에 담는 공장
        public async Task SendMavlinkCommand(int commandId, float param1)
        {
            // MAVLink v2 COMMAND_LONG (#76) 구조 조립
            // 헤더(9) + 페이로드(33) + 체크섬(2) = 총 44바이트
            byte payloadSize = 33;
            byte[] packet = new byte[payloadSize + 12]; // v2 헤더 10바이트 + 페이로드 + CRC 2바이트

            // 1. 헤더 조립 (MAVLink v2)
            packet[0] = 0xFD;           // STX (v2 시작 마커)
            packet[1] = payloadSize;    // 페이로드 길이
            packet[2] = 0x00;           // 호환성 플래그
            packet[3] = 0x00;           // 비호환성 플래그
            packet[4] = _sequence++;    // 시퀀스 번호
            packet[5] = 255;            // GCS 시스템 ID (보통 255)
            packet[6] = 1;              // GCS 컴포넌트 ID
            packet[7] = 76;             // 메시지 ID (COMMAND_LONG = 76)
            packet[8] = 0x00;           // 메시지 ID 확장
            packet[9] = 0x00;           // 메시지 ID 확장

            // 2. 페이로드 조립 (COMMAND_LONG)
            // param1 (4), param2~7 (24), command(2), target_sys(1), target_comp(1), confirmation(1)
            Array.Copy(BitConverter.GetBytes(param1), 0, packet, 10, 4);    // Param1
                                                                                                                     // param2 ~ param7 은 0으로 채움 (대부분의 기본 명령에서 0 사용)
            BitConverter.GetBytes((ushort)commandId).CopyTo(packet, 38);   // Command ID (예: 22=Takeoff)
            packet[40] = 1;             // Target System ID (드론 ID)
            packet[41] = 1;             // Target Component ID
            packet[42] = 0;             // Confirmation (0: 첫 전송)

            // 3. 체크섬(CRC) 계산
            // MAVLink는 메시지마다 고유의 CRC_EXTRA 값을 더해야 합니다. (COMMAND_LONG은 152)
            ushort crc = MavlinkCRC.CalculateCRC(packet, 1, packet.Length - 3);
            crc = MavlinkCRC.AccumulateCRC((byte)152, crc); // COMMAND_LONG 전용 매직 넘버

            BitConverter.GetBytes(crc).CopyTo(packet, packet.Length - 2);

            // 4. 진짜 전송!
            await SendPacket(packet);
            System.Diagnostics.Debug.WriteLine($"[TX] 진짜 패킷 전송됨: CMD {commandId}, Val {param1}");
        }

        // 이륙 명령 보냄
        public async Task Takeoff(string altitude)
        {
            if (float.TryParse(altitude, out float altValue))
            {
                await SetFlightMode("GUIDED");
                await SendMavlinkCommand(22, altValue); // MAV_CMD_NAV_TAKEOFF
            }
        }

        // 착륙 명령 보냄
        public async Task Landing()
        {
            // SendMavlinkCommand 호출 시 await를 사용합니다.
            await SendMavlinkCommand(21, 0);
        }

        // 드론의 비행 모드 변경
        public async Task SetFlightMode(string modeName)
        {
            // MAV_CMD_DO_SET_MODE (176) 명령을 사용하여 모드 변경
            System.Diagnostics.Debug.WriteLine($"[TX] 비행 모드 변경 요청: {modeName}");

            // SendMavlinkCommand 호출 시 await를 사용합니다.
            await SendMavlinkCommand(176, 1.0f);
        }

        // 실시간으로 추적할 드론이 움직이는 좌표를 보내줌
        public async Task SendTrackCoordinate(double lat, double lng, float alt = 5.0f)
        {
            // MAVLink v2 SET_POSITION_TARGET_GLOBAL_INT (#113)
            // 헤더(10) + 페이로드(53) + 체크섬(2) = 총 65바이트
            byte payloadSize = 53;
            byte[] packet = new byte[payloadSize + 12];

            // 1. 헤더 조립
            packet[0] = 0xFD;           // STX
            packet[1] = payloadSize;    // Payload Length
            packet[2] = 0x00;           // Incompatibility Flags
            packet[3] = 0x00;           // Compatibility Flags
            packet[4] = _sequence++;    // Packet Sequence
            packet[5] = 255;            // System ID (GCS)
            packet[6] = 1;              // Component ID
            packet[7] = 113;            // Message ID (113)
            packet[8] = 0x00;
            packet[9] = 0x00;

            // 2. 페이로드 조립 (Byte Offset 기준)
            // [0-3] time_boot_ms (보통 0)
            // [4] coordinate_frame (5 = MAV_FRAME_GLOBAL_INT)
            // [5-6] type_mask (어떤 데이터를 무시할지 결정. 위치만 사용시 0x0DF8)
            // [7-10] lat_int (degE7)
            // [11-14] lon_int (degE7)
            // [15-18] alt (float, meters)

            packet[14] = 5; // MAV_FRAME_GLOBAL_INT (Offset 10+4)

            // type_mask: 0x0DF8 (위치 정보만 사용하고 속도/가속도/요 비트는 무시하겠다는 설정)
            ushort type_mask = 3576;
            BitConverter.GetBytes(type_mask).CopyTo(packet, 15);

            // 좌표 변환 (수영님의 핵심 로직)
            int latE7 = (int)(lat * 1E7);
            int lngE7 = (int)(lng * 1E7);

            BitConverter.GetBytes(latE7).CopyTo(packet, 17); // Lat (Offset 10+7)
            BitConverter.GetBytes(lngE7).CopyTo(packet, 21); // Lng (Offset 10+11)
            BitConverter.GetBytes(alt).CopyTo(packet, 25);   // Alt (Offset 10+15)

            // 3. 체크섬(CRC) 계산
            // 113번 메시지의 CRC_EXTRA 값은 33입니다.
            ushort crc = MavlinkCRC.CalculateCRC(packet, 1, packet.Length - 3);
            crc = MavlinkCRC.AccumulateCRC((byte)33, crc);

            BitConverter.GetBytes(crc).CopyTo(packet, packet.Length - 2);

            // 4. 전송
            await SendPacket(packet);
            System.Diagnostics.Debug.WriteLine($"[TRACK TX] 실시간 좌표 송신: {lat:F6}, {lng:F6}");
        }

        // GCS에서 어떤 타켓을 추적할지 결정하고 우분투한테 명령 보냄
        public async void SendFollowCommand(int targetId)
        {
            // 추적 상태 UI 업데이트
            foreach (var t in DetectedTargets) t.IsBeingTracked = (t.ID == targetId);

            // 커스텀 메시지 ID 251 조립 (간단하게 1바이트 ID만 전송)
            byte payloadSize = 1;
            byte[] packet = new byte[payloadSize + 12];

            packet[0] = 0xFD;
            packet[1] = payloadSize;
            packet[4] = _sequence++;
            packet[5] = 255;
            packet[6] = 1;
            packet[7] = 251; // Custom ID

            packet[10] = (byte)targetId; // 페이로드에 타겟 ID 삽입

            // 임의의 커스텀 메시지이므로 CRC_EXTRA는 보통 0이나 약속된 값을 사용 (여기선 0 예시)
            ushort crc = MavlinkCRC.CalculateCRC(packet, 1, packet.Length - 3);
            BitConverter.GetBytes(crc).CopyTo(packet, packet.Length - 2);

            await SendPacket(packet);
            OnLogReceived?.Invoke($"[명령] ID {targetId} 추적 시작 패킷 송신");
        }
        #endregion


        #region [7. 웨이포인트 관리 (Waypoint Management)]
        public void AddWaypoint(double lat, double lng)
        {
            _waypoints.Add(new GeoPoint { Lat = lat, Lng = lng });
            OnWaypointCountChanged?.Invoke(_waypoints.Count);
        }

        public void UndoWaypoint()
        {
            if (_waypoints.Count > 0)
            {
                _waypoints.RemoveAt(_waypoints.Count - 1);
                OnWaypointCountChanged?.Invoke(_waypoints.Count);
            }
        }

        public void ClearWaypoints()
        {
            _waypoints.Clear();
            OnWaypointCountChanged?.Invoke(0);
        }

        public void StartMission()
        {
            if (_isMissionRunning) 
                return;
            _isMissionRunning = true;
            

            // 미션 수행 루프 시작
            Task.Run(async () => {

                await SetFlightMode("GUIDED");

                while (_isMissionRunning && _waypoints.Count > 0)
                {
                    var targetWP = _waypoints[0]; // 맨 앞의 목표 지점

                    // 드론에게 이동 명령 전송
                    await SendTrackCoordinate(targetWP.Lat, targetWP.Lng);

                    // 드론이 목표 지점 근처에 도착했는지 체크하는 로직 (단순 거리 계산)
                    if (IsReached(targetWP))
                    {
                        _waypoints.RemoveAt(0); // 도착했으면 리스트에서 제거
                        OnWaypointCountChanged?.Invoke(_waypoints.Count);
                    }

                    await Task.Delay(1000); // 1초마다 체크
                }
                _isMissionRunning = false;
                OnMissionCompleted?.Invoke();
            });
        }

        private bool IsReached(GeoPoint target)
        {
            // 현재 드론 위치와 목표 지점 사이의 거리를 계산 (간단한 예시)
            double dist = Math.Sqrt(Math.Pow(CurrentState.Latitude - target.Lat, 2) +
                                    Math.Pow(CurrentState.Longitude - target.Lng, 2));
            return dist < 0.0001; // 약 10m 이내면 도착으로 판정
        }
        public void StopMission() => _isMissionRunning = false;
        #endregion
    }
}