using GCS_Project.Models;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GCS_Project.xServices
{
    public class YoloMetaUdpService
    {
        private readonly int _listenPort;
        private readonly string _rosCommandIp;
        private readonly int _rosCommandPort;

        private UdpClient? _udpClient;
        private UdpClient? _commandUdpClient;
        private IPEndPoint? _rosCommandEndPoint;
        private CancellationTokenSource? _cts;

        public event Action<List<DetectedTarget>>? OnTargetsReceived;
        public event Action<string>? OnLogReceived;

        public YoloMetaUdpService(
        int listenPort = 5001,
        // 우분투 ip로 바꿔야됨
        string rosCommandIp = "192.168.0.6",
        int rosCommandPort = 5006)
            {
                _listenPort = listenPort;
                _rosCommandIp = rosCommandIp;
                _rosCommandPort = rosCommandPort;
            }
        public Task StartAsync()
        {
            if (_cts != null || _udpClient != null)
                return Task.CompletedTask;

            _cts = new CancellationTokenSource();
            _udpClient = new UdpClient(_listenPort);

            _commandUdpClient = new UdpClient();
            _rosCommandEndPoint = new IPEndPoint(
                IPAddress.Parse(_rosCommandIp),
                _rosCommandPort
            );

            OnLogReceived?.Invoke($"[YOLO-UDP] 메타 수신 시작: 0.0.0.0:{_listenPort}");
            OnLogReceived?.Invoke($"[GCS-CMD] ROS2 명령 송신 대상: {_rosCommandIp}:{_rosCommandPort}");

            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            if (_cts == null && _udpClient == null)
                return Task.CompletedTask;

            try
            {
                _cts?.Cancel();
                _udpClient?.Close();
                _udpClient?.Dispose();

                _commandUdpClient?.Close();        // ✅ 추가
                _commandUdpClient?.Dispose();      // ✅ 추가
            }
            catch
            {
                // 종료 중 예외 무시
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _udpClient = null;

                _commandUdpClient = null;          // ✅ 추가
                _rosCommandEndPoint = null;        // ✅ 추가
            }

            OnLogReceived?.Invoke("[YOLO-UDP] 메타 수신 중지");
            return Task.CompletedTask;
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            if (_udpClient == null) return;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var result = await _udpClient.ReceiveAsync();
                    string json = Encoding.UTF8.GetString(result.Buffer);

                    var targets = ParseTargets(json);
                    OnTargetsReceived?.Invoke(targets);
                }
            }
            catch (ObjectDisposedException)
            {
                // 정상 종료
            }
            catch (SocketException)
            {
                if (!token.IsCancellationRequested)
                {
                    OnLogReceived?.Invoke("[YOLO-UDP][오류] 소켓 수신 중 오류가 발생했습니다.");
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    OnLogReceived?.Invoke($"[YOLO-UDP][오류] {ex.Message}");
                }
            }
        }

        private List<DetectedTarget> ParseTargets(string json)
        {
            var list = new List<DetectedTarget>();

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("targets", out JsonElement targetsElement) ||
                    targetsElement.ValueKind != JsonValueKind.Array)
                {
                    return list;
                }

                foreach (var item in targetsElement.EnumerateArray())
                {
                    var target = new DetectedTarget
                    {
                        ID = item.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0,
                        TargetType = item.TryGetProperty("class", out var classEl) ? classEl.GetString() ?? "Unknown" : "Unknown",
                        Lat = item.TryGetProperty("lat", out var latEl) ? latEl.GetDouble() : 0.0,
                        Lng = item.TryGetProperty("lng", out var lngEl) ? lngEl.GetDouble() : 0.0,
                        Bx = item.TryGetProperty("cx", out var cxEl) ? cxEl.GetDouble() : 0.0,
                        By = item.TryGetProperty("cy", out var cyEl) ? cyEl.GetDouble() : 0.0,
                        Bw = item.TryGetProperty("w", out var wEl) ? wEl.GetDouble() : 0.0,
                        Bh = item.TryGetProperty("h", out var hEl) ? hEl.GetDouble() : 0.0,
                        IsBeingTracked = item.TryGetProperty("tracked", out var trackedEl) && trackedEl.GetBoolean()
                    };

                    list.Add(target);
                }
            }
            catch (JsonException)
            {
                // 잘못된 JSON은 조용히 무시하거나 필요 시 로그 1줄만
            }

            return list;
        }

        public void SendTargetId(int targetId)
        {
            var msg = new
            {
                type = "select_target",
                id = targetId
            };

            SendCommandJson(msg);
        }

        public void SendStartGuidance()
        {
            var msg = new
            {
                type = "start_guidance",
                value = true
            };

            SendCommandJson(msg);
        }
        public void SendStopGuidance()
        {
            var msg = new
            {
                type = "start_guidance",
                value = false
            };

            SendCommandJson(msg);
        }
        private void SendCommandJson(object msg)
        {
            try
            {
                if (_commandUdpClient == null || _rosCommandEndPoint == null)
                {
                    _commandUdpClient = new UdpClient();
                    _rosCommandEndPoint = new IPEndPoint(
                        IPAddress.Parse(_rosCommandIp),
                        _rosCommandPort
                    );
                }

                string json = JsonSerializer.Serialize(msg);
                byte[] data = Encoding.UTF8.GetBytes(json);

                _commandUdpClient.Send(data, data.Length, _rosCommandEndPoint);

                OnLogReceived?.Invoke($"[GCS-CMD] 송신: {json}");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[GCS-CMD][오류] 송신 실패: {ex.Message}");
            }
        }
    }


}