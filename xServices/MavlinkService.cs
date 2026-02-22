using Asv.Cfg;
using Asv.IO;
using Asv.Mavlink;
using Asv.Mavlink.Common;
using GCS_Project.Models;
using ObservableCollections;
using R3;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace GCS_Project.xServices
{
    public class MavlinkService : IAsyncDisposable
    {
        private readonly List<GeoPoint> _waypoints = new();
        private bool _isMissionRunning;

        private CancellationTokenSource? _cts;

        // v4 core
        private IProtocolFactory? _protocol;
        private IProtocolRouter? _router;
        private IDeviceExplorer? _explorer;
        private IClientDevice? _drone;

        private readonly CompositeDisposable _subs = new();

        public DroneState CurrentState { get; } = new DroneState();
        public ObservableCollection<DetectedTarget> DetectedTargets { get; } = new ObservableCollection<DetectedTarget>();

        public event Action<DroneState>? OnDataUpdated;
        public event Action<string>? OnLogReceived;

        public async Task StartAsync(string listenHost = "0.0.0.0", int listenPort = 14550)
        {
            OnLogReceived?.Invoke("[MAV] StartAsync entered");
            if (_router != null) return;

            OnLogReceived?.Invoke("[MAV] StartAsync called");

            _cts = new CancellationTokenSource();

            _protocol = Protocol.Create(builder =>
            {
                builder.RegisterMavlinkV2Protocol();
                builder.Features.RegisterBroadcastFeature<MavlinkMessage>();
                builder.Formatters.RegisterSimpleFormatter();
            });
            OnLogReceived?.Invoke("[MAV] Protocol created");

            _router = _protocol.CreateRouter("GCS_ROUTER");
            OnLogReceived?.Invoke("[MAV] Router created");

            // 1) PX4 → GCS (수신)
            AddUdpListenPort(_router, listenHost, listenPort);
            OnLogReceived?.Invoke($"[MAV] UDP listen {listenHost}:{listenPort}");

            // 2) GCS → PX4 (송신)  ★ 이 줄 추가
            AddUdpOutPort(_router, "192.168.0.6", 14551);
            OnLogReceived?.Invoke("[MAV] UDP out 192.168.0.6:18570 added");

            var seq = new PacketSequenceCalculator();
            var identity = new MavlinkIdentity(255, 255);

            _explorer = DeviceExplorer.Create(_router, b =>
            {
                b.SetConfig(new ClientDeviceBrowserConfig
                {
                    DeviceTimeoutMs = 1000,
                    DeviceCheckIntervalMs = 30_000
                });

                b.Factories.RegisterDefaultDevices(
                    new MavlinkIdentity(identity.SystemId, identity.ComponentId),
                    seq,
                    new InMemoryConfiguration()
                );
            });
            OnLogReceived?.Invoke("[MAV] DeviceExplorer created");

            var found = new TaskCompletionSource();
            _subs.Add(_explorer.Devices.ObserveAdd().Take(1).Subscribe(kvp =>
            {
                _drone = kvp.Value.Value;
                OnLogReceived?.Invoke("[MAV] Device found");
                found.TrySetResult();
            }));
            OnLogReceived?.Invoke("[MAV] Waiting device(found)...");
            await found.Task;

            var ready = new TaskCompletionSource();
            _subs.Add(_drone!.State.Subscribe(st =>
            {
                OnLogReceived?.Invoke($"[MAV] Drone state: {st}");
                if (st == ClientDeviceState.Complete) ready.TrySetResult();
            }));
            OnLogReceived?.Invoke("[MAV] Waiting device(ready)...");
            await ready.Task;

            HookTelemetry(_drone);
            OnLogReceived?.Invoke("[MAV] Telemetry hooked");

            CurrentState.IsConnected = true;
            OnDataUpdated?.Invoke(CurrentState);
            OnLogReceived?.Invoke("[MAV] Connected");
        }

        private static void AddUdpListenPort(IProtocolRouter router, string host, int port)
        {
            router.AddUdpPort(p =>
            {
                p.Scheme = "udp";      // 있으면 넣는 게 확실
                p.Host = host;         // "0.0.0.0"
                p.Port = port;         // 14550
                p.IsEnabled = true;    // ★ 이거 중요

                // 있으면 디버깅에 도움
                p.Name = "udp_in";


            });
        }
        private static void AddUdpOutPort(IProtocolRouter router, string host, int port)
        {
            router.AddUdpPort(p =>
            {
                p.Scheme = "udp";
                p.Host = host;     // 우분투 IP
                p.Port = port;     // PX4가 듣는 포트 (18570)
                p.IsEnabled = true;
                p.Name = "udp_out";
            });
        }



        private void HookTelemetry(IClientDevice drone)
        {
            OnLogReceived?.Invoke("[MAV] HookTelemetry entered");

            // 1) Heartbeat
            var hb = drone.GetMicroservice<IHeartbeatClient>();
            if (hb != null)
            {
                _subs.Add(hb.RawHeartbeat.Subscribe(h =>
                {
                    CurrentState.IsConnected = true;
                    OnDataUpdated?.Invoke(CurrentState);
                }));
                OnLogReceived?.Invoke("[MAV] Heartbeat hooked");
            }
            else
            {
                OnLogReceived?.Invoke("[MAV] IHeartbeatClient NOT found");
            }

            // 2) PositionClient (여기서 위치/고도/속도까지)
            var pos = drone.GetMicroservice<IPositionClient>();
            if (pos == null)
            {
                OnLogReceived?.Invoke("[MAV] IPositionClient NOT found");
                return;
            }
            OnLogReceived?.Invoke("[MAV] IPositionClient found");

            // 2-1) GlobalPosition 구독
            try
            {
                _subs.Add(pos.GlobalPosition.Subscribe(gp =>
                {
                    
                    // ✅ 1e7 스케일 → degree 변환, Akt -> meter 로 변환
                    var latDeg = gp.Lat / 1e7;
                    var lonDeg = gp.Lon / 1e7;
                    var AltMeter = gp.Alt / 1000.0;
                    CurrentState.Latitude = latDeg;
                    CurrentState.Longitude = lonDeg;
                    CurrentState.Altitude = AltMeter;
                    OnDataUpdated?.Invoke(CurrentState);
                }));

                OnLogReceived?.Invoke("[MAV] GlobalPosition hooked");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[MAV] GlobalPosition subscribe failed: {ex.Message}");
            }

            // 2-2) VfrHud 구독 (속도)
            try
            {
                _subs.Add(pos.VfrHud.Subscribe(vfr =>
                {
                    CurrentState.Speed = Math.Round(vfr.Groundspeed, 2);
                    OnDataUpdated?.Invoke(CurrentState);
                }));

                OnLogReceived?.Invoke("[MAV] VfrHud hooked");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[MAV] VfrHud subscribe failed: {ex.Message}");
            }
            // 3) TelemetryClient (배터리/상태)
            var tel = drone.GetMicroservice<ITelemetryClient>();
            if (tel == null)
            {
                OnLogReceived?.Invoke("[MAV] ITelemetryClient NOT found");
                return;
            }
            OnLogReceived?.Invoke("[MAV] ITelemetryClient found");

            // 3-1) Battery
            try
            {
                _subs.Add(tel.Battery.Subscribe(b =>
                {
                    CurrentState.BatteryPercent = b.BatteryRemaining; 
                    OnDataUpdated?.Invoke(CurrentState);
                }));
                OnLogReceived?.Invoke("[MAV] Battery hooked");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[MAV] Battery subscribe failed: {ex.Message}");
            }

            // 3-2) SystemStatus (선택: 로그용)
            /*try
            {
                _subs.Add(tel.SystemStatus.Subscribe(s =>
                {
                    // 일단 들어오는지 확인용 로그
                    OnLogReceived?.Invoke("[MAV] SystemStatus tick");
                }));
                OnLogReceived?.Invoke("[MAV] SystemStatus hooked");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[MAV] SystemStatus subscribe failed: {ex.Message}");
            }*/
        }

        public async Task StopAsync()
        {
            _isMissionRunning = false;

            _subs.Dispose();

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            if (_explorer is IAsyncDisposable ax1) await ax1.DisposeAsync();
            _explorer = null;

            if (_router is IAsyncDisposable ax2) await ax2.DisposeAsync();
            _router = null;

            _protocol = null;
            _drone = null;

            CurrentState.IsConnected = false;
            OnDataUpdated?.Invoke(CurrentState);
            OnLogReceived?.Invoke("[MAV] Disconnected");
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }

    }
}