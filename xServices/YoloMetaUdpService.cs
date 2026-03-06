using GCS_Project.Models;
using System;
using System.Collections.Generic;
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
        private UdpClient? _udpClient;
        private CancellationTokenSource? _cts;

        public event Action<List<DetectedTarget>>? OnTargetsReceived;
        public event Action<string>? OnLogReceived;

        public YoloMetaUdpService(int listenPort = 5001)
        {
            _listenPort = listenPort;
        }

        public Task StartAsync()
        {
            if (_cts != null) return Task.CompletedTask;

            _cts = new CancellationTokenSource();
            _udpClient = new UdpClient(_listenPort);

            OnLogReceived?.Invoke($"[YOLO-UDP] 메타 수신 시작: 0.0.0.0:{_listenPort}");

            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            try
            {
                _cts?.Cancel();
                _udpClient?.Close();
                _udpClient?.Dispose();
            }
            catch { }

            _cts = null;
            _udpClient = null;

            OnLogReceived?.Invoke("[YOLO-UDP] 메타 수신 중지");
            return Task.CompletedTask;
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            if (_udpClient == null) return;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync();
                    string json = Encoding.UTF8.GetString(result.Buffer);

                    var targets = ParseTargets(json);
                    OnTargetsReceived?.Invoke(targets);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnLogReceived?.Invoke($"[YOLO-UDP][오류] {ex.Message}");
                }
            }
        }

        private List<DetectedTarget> ParseTargets(string json)
        {
            var list = new List<DetectedTarget>();

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

            return list;
        }
    }
}