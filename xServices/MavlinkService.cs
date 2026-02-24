using Asv.Cfg;
using Asv.Common;
using Asv.IO;
using Asv.Mavlink;
using Asv.Mavlink.Common;
using Asv.Mavlink.Minimal;
using GCS_Project.Models;
using ObservableCollections;
using R3;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace GCS_Project.xServices
{
    public class MavlinkService : IAsyncDisposable
    {
        private readonly List<GCS_Project.Models.GeoPoint> _waypoints = new();
        private bool _isMissionRunning;

        private CancellationTokenSource? _cts;

        private CoreServices? _gcsCore;
        private HeartbeatServer? _gcsHbServer;
        // v4 core
        private IProtocolFactory? _protocol;
        private IProtocolRouter? _router;
        private IDeviceExplorer? _explorer;
        private IClientDevice? _drone;

        private readonly CompositeDisposable _subs = new();
        private DateTime _lastHeartbeatUtc = DateTime.MinValue;
        private Task? _linkWatchdogTask;

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

            //StartGcsHeartbeat_ByDoc() //잠시 주석;

            _router = _protocol.CreateRouter("GCS_ROUTER");
            OnLogReceived?.Invoke("[MAV] Router created");


            // 1) PX4 → GCS (Telemetry 수신)
            AddUdpListenPort(_router, listenHost, listenPort);
            OnLogReceived?.Invoke($"[MAV] UDP listen {listenHost}:{listenPort}");

            // 2) GCS → PX4 (Command 송신)
            //AddUdpOutPort(_router, "192.168.0.6", 14551);

            // GCS -> PX4 송신 소켓 (local ephemeral port)
/*            _px4Ep = new IPEndPoint(IPAddress.Parse("192.168.0.6"), 14551);
            _gcsTx = new UdpClient();                 // OS가 임의 local port 할당
            _gcsTx.Connect(_px4Ep);*/


            // 2) GCS → PX4 (송신)  ★ 이 줄 추가
            AddUdpOutPort(_router, "192.168.0.6", 14551);
            OnLogReceived?.Invoke("[MAV] UDP out 192.168.0.6:14551 added");

            // 추가 02/23
            //await UdpOutSmokeTestAsync("192.168.0.6", 14551);

            OnLogReceived?.Invoke($"[MAV] RouterType={_router.GetType().FullName}"); //지워라

            var seq = new PacketSequenceCalculator();
            var identity = new MavlinkIdentity(255, 190);



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

            _subs.Add(_explorer.Devices.ObserveAdd().Take(1).Subscribe(e =>
            {
                _drone = e.Value.Value; 

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

            HookCommandAck(_drone);

            StartLinkWatchdog();

            OnLogReceived?.Invoke("[MAV] 장치 연결됨 (device ready)");
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
                p.Port = port;     // PX4가 듣는 포트 
                p.IsEnabled = true;
                p.Name = "udp_out";
            });
        }

        /*private void StartGcsHeartbeat_ByDoc()
        {
            // 문서에 나온 정석 예시: CreateVirtualConnection 사용
            var link = _protocol!.CreateVirtualConnection();

            var serverSeq = new PacketSequenceCalculator();

            _gcsCore = new CoreServices(
                link.Server,
                serverSeq,
                logFactory: null,
                timeProvider: TimeProvider.System,
                new DefaultMeterFactory()
            );

            // PX4 target (보통 1/1)
            var target = new MavlinkIdentity(1, 1);

            _gcsHbServer = new HeartbeatServer(
                target,
                new MavlinkHeartbeatServerConfig(),
                _gcsCore
            );

            _gcsHbServer.Start();

            _gcsHbServer.Set(p =>
            {
                p.Type = MavType.MavTypeGcs;
                p.Autopilot = MavAutopilot.MavAutopilotInvalid;
                p.BaseMode = 0;
                p.SystemStatus = MavState.MavStateActive;
            });

            OnLogReceived?.Invoke("[MAV] GCS HeartbeatServer started (doc virtual)");
        }*/

        private void HookTelemetry(IClientDevice drone)
        {
            OnLogReceived?.Invoke("[MAV] HookTelemetry entered");

            // 1) Heartbeat
            var hb = drone.GetMicroservice<IHeartbeatClient>();
            if (hb != null)
            {
                _subs.Add(hb.RawHeartbeat.Subscribe(h =>
                {
                    _lastHeartbeatUtc = DateTime.UtcNow;

                    if (!CurrentState.IsConnected)
                    {
                        CurrentState.IsConnected = true;
                        OnDataUpdated?.Invoke(CurrentState);
                        OnLogReceived?.Invoke("[MAV] 통신 연결됨 (heartbeat)");
                    }
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

        private void HookCommandAck(IClientDevice drone)
        {
            var cmd = drone.GetMicroservice<ICommandClient>();
            if (cmd == null)
            {
                OnLogReceived?.Invoke("[MAV] ICommandClient NOT found");
                return;
            }

            _subs.Add(cmd.OnCommandAck.Subscribe(ack =>
            {
                OnLogReceived?.Invoke($"[MAV] COMMAND_ACK: cmd={ack.Command} result={ack.Result}");
            }));

            OnLogReceived?.Invoke("[MAV] COMMAND_ACK hooked");
        }

        private void StartLinkWatchdog()
        {
            // 중복 실행 방지
            if (_linkWatchdogTask != null) return;

            _lastHeartbeatUtc = DateTime.UtcNow;

            _linkWatchdogTask = Task.Run(async () =>
            {
                OnLogReceived?.Invoke("[MAV] Link watchdog started");

                while (_cts != null && !_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var ageMs = (DateTime.UtcNow - _lastHeartbeatUtc).TotalMilliseconds;

                        // 3초 이상 heartbeat 없음 => 링크 다운 처리
                        if (ageMs > 3000 && CurrentState.IsConnected)
                        {
                            CurrentState.IsConnected = false;
                            OnDataUpdated?.Invoke(CurrentState);
                            OnLogReceived?.Invoke("[MAV] 통신 끊김 (heartbeat timeout)");
                        }

                        await Task.Delay(200, _cts.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        OnLogReceived?.Invoke($"[MAV] Watchdog error: {ex.Message}");
                        await Task.Delay(500);
                    }
                }

                OnLogReceived?.Invoke("[MAV] Link watchdog stopped");
            });
        }

        public async Task SendDisarmAsync()
        {
            if (_drone == null)
            {
                OnLogReceived?.Invoke("[MAV] SendDisarmAsync failed: _drone null");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null)
            {
                OnLogReceived?.Invoke("[MAV] SendDisarmAsync failed: ICommandClient null");
                return;
            }

            OnLogReceived?.Invoke("[MAV] Sending DISARM (MAV_CMD_COMPONENT_ARM_DISARM, p1=0) ...");

            // SendCommandLong(MavCmd, p1..p7, CancellationToken)
            await cmd.SendCommandLong(
                MavCmd.MavCmdComponentArmDisarm, // = MAV_CMD_COMPONENT_ARM_DISARM
                0f,  // param1: 0 = DISARM
                0f,  // param2
                0f,  // param3
                0f,  // param4
                0f,  // param5
                0f,  // param6
                0f,  // param7
                _cts?.Token ?? CancellationToken.None
            );
        }

        public async Task SendArmAsync()
        {
            if (_drone == null)
            {
                OnLogReceived?.Invoke("[MAV] ARM 실패: 드론 객체 없음");
                return;
            }

            if (!CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[MAV] ARM 실패: 통신 끊김 상태");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null)
            {
                OnLogReceived?.Invoke("[MAV] ARM 실패: ICommandClient 없음");
                return;
            }

            OnLogReceived?.Invoke("[MAV] ARM 명령 전송");

            await cmd.SendCommandLong(
                MavCmd.MavCmdComponentArmDisarm,
                1f, // ARM
                0f, 0f, 0f, 0f, 0f, 0f,
                _cts?.Token ?? CancellationToken.None
            );
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