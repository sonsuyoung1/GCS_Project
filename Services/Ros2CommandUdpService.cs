using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GCS_Project.Services
{
    // Ubuntu의 ROS2 노드로 GCS 제어 명령을 UDP 전송한다.
    public class Ros2CommandUdpService : IDisposable
    {
        private readonly string _commandIp;
        private readonly int _commandPort;
        private readonly object _sendLock = new object();

        private UdpClient? _udpClient;
        private IPEndPoint? _commandEndPoint;

        public event Action<string>? OnLogReceived;

        public Ros2CommandUdpService(string commandIp, int commandPort)
        {
            _commandIp = commandIp;
            _commandPort = commandPort;
        }

        // 선택 취소 명령을 ROS2 쪽으로 전송한다.
        public void SendClearTargetId()
        {
            var msg = new
            {
                type = "clear_target"
            };

            SendCommandJson(msg);
        }

        // 선택된 타겟 ID를 ROS2 쪽으로 전송한다.
        public void SendTargetId(int targetId)
        {
            var msg = new
            {
                type = "select_target",
                id = targetId
            };

            SendCommandJson(msg);
        }

        // 360도 스캔 시작 명령을 ROS2 쪽으로 전송한다.
        public void SendStartScan()
        {
            var msg = new
            {
                type = "start_scan",
                value = true
            };

            SendCommandJson(msg);
        }

        // 선택 객체를 화면 중앙에 유지하기 위한 헤딩 추적을 시작한다.
        public void SendStartTracking()
        {
            // 추적 시작 전에 360도 스캔을 정지한다.
            SendStopScan();

            var msg = new
            {
                type = "start_tracking",
                value = true
            };

            SendCommandJson(msg);
        }

        // 실제 타격 유도를 시작한다.
        public void SendStartStrikeGuidance()
        {
            // 타격 시작 전에 스캔과 추적을 정지한다.
            SendStopScan();
            SendStopTracking();

            var msg = new
            {
                type = "start_strike_guidance",
                value = true
            };

            SendCommandJson(msg);
        }

        public void SendStopScan()
        {
            var msg = new
            {
                type = "start_scan",
                value = false
            };

            SendCommandJson(msg);
        }

        public void SendStopTracking()
        {
            var msg = new
            {
                type = "start_tracking",
                value = false
            };

            SendCommandJson(msg);
        }

        public void SendStopStrikeGuidance()
        {
            var msg = new
            {
                type = "start_strike_guidance",
                value = false
            };

            SendCommandJson(msg);
        }

        private void SendCommandJson(object msg)
        {
            try
            {
                lock (_sendLock)
                {
                    EnsureClientCreated();

                    string json = JsonSerializer.Serialize(msg);
                    byte[] data = Encoding.UTF8.GetBytes(json);

                    _udpClient!.Send(data, data.Length, _commandEndPoint!);
                    OnLogReceived?.Invoke($"[GCS-CMD] 송신: {json}");
                }
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[GCS-CMD][오류] 송신 실패: {ex.Message}");
            }
        }

        private void EnsureClientCreated()
        {
            if (_udpClient != null && _commandEndPoint != null)
                return;

            _udpClient = new UdpClient();
            _commandEndPoint = new IPEndPoint(
                IPAddress.Parse(_commandIp),
                _commandPort
            );

            OnLogReceived?.Invoke($"[GCS-CMD] ROS2 명령 송신 대상: {_commandIp}:{_commandPort}");
        }

        public void Dispose()
        {
            lock (_sendLock)
            {
                _udpClient?.Close();
                _udpClient?.Dispose();
                _udpClient = null;
                _commandEndPoint = null;
            }
        }
    }
}
