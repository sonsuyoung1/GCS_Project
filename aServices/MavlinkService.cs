using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GCS_Project.Models;

namespace GCS_Project.Services
{
    public class MavlinkService
    {
        #region [1. 상수 및 필드 (Constants & Fields)]
        private const int Port = 14550;
        private const byte MSG_ID_HEARTBEAT = 0;
        private const byte MSG_ID_GLOBAL_POSITION_INT = 33;
        private const byte MSG_ID_YOLO_TARGET = 250; // ROS2 커스텀 메시지 ID

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
            if (packet.Length < 10) return;
            int msgId = packet[7]; // MAVLink v2 기준

            switch (msgId)
            {
                case MSG_ID_HEARTBEAT:
                    CurrentState.IsConnected = true;
                    break;
                case MSG_ID_GLOBAL_POSITION_INT:
                    UpdateDroneLocation(packet);
                    break;
                case MSG_ID_YOLO_TARGET:
                    UpdateYoloTargets(packet);
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
        public void SendTrackCoordinate(double lat, double lng, float alt = 5.0f)
        {
            // MAVLink Message ID: 113 (SET_POSITION_TARGET_GLOBAL_INT)
            // 이 명령은 드론이 GUIDED 또는 OFFBOARD 모드일 때 실시간으로 반응하게 합니다.

            byte[] packet = new byte[50]; // 실제 구조에 맞춰 패킷 조립 필요
            packet[0] = 0xFD; // STX
            packet[7] = 113;  // MSG ID

            // lat/lng를 degE7 형식으로 변환하여 패킷에 삽입
            int latE7 = (int)(lat * 1E7);
            int lngE7 = (int)(lng * 1E7);

            // [바이너리 조립 로직 생략 - 실제 전송 시 BitConverter 사용]

            System.Diagnostics.Debug.WriteLine($"[TRACKING TX] Target Loc: {lat:F6}, {lng:F6}");
            // _udpClient?.SendAsync(packet, packet.Length, ...);
        }

        public void Takeoff(string altitude)
        {
            if (float.TryParse(altitude, out float altValue))
            {
                SendMavlinkCommand(22, altValue); // MAV_CMD_NAV_TAKEOFF
            }
        }

        public void Landing() => SendMavlinkCommand(21, 0); // MAV_CMD_NAV_LAND

        public void SendFollowCommand(int targetId)
        {
            // 1. UI 리스트 상태 동기화
            foreach (var t in DetectedTargets) t.IsBeingTracked = (t.ID == targetId);

            // 2. MAVLink 커스텀 패킷 생성 (예시 ID 251)
            byte[] followPacket = new byte[12];
            followPacket[0] = 0xFD;
            followPacket[7] = 251;
            followPacket[10] = (byte)targetId;

            // _udpClient?.SendAsync(followPacket, followPacket.Length, ...);
            System.Diagnostics.Debug.WriteLine($"[TX] ID {targetId} 추적 명령 전송");
        }

        private void SendMavlinkCommand(int commandId, float param1)
        {
            // 실제 MAVLink 규격에 맞춰 UDP 전송 로직 구현
            System.Diagnostics.Debug.WriteLine($"[TX] CMD ID: {commandId}, Param: {param1}");
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

        public void StartMission() => _isMissionRunning = true;
        public void StopMission() => _isMissionRunning = false;
        #endregion
    }
}