using Asv.Mavlink;
using GCS_Project.Models;
using GCS_Project.xServices;
using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace GCS_Project.ViewModels
{
    public class MainViewModel : ObservableObject // 명령 조율하는 두뇌 역할 
    {
        #region [1. 필드 (Fields)]
        private readonly MavlinkService _mavlinkService;
        private DetectedTarget? _selectedTarget;
        private System.Timers.Timer _targetLostTimer;
        private string _flightMode = "IDLE";
        private string _chatLog = "드론 연결 대기 중...";
        private int _currentWaypointCount = 0;
        private double _pitchOffset;
        #endregion

        #region [2. 속성 (Properties)]
        public MavlinkService MavlinkService => _mavlinkService;
        public DroneState Drone => _mavlinkService.CurrentState;
        public int BatteryPercent => Drone.BatteryPercent;
        public double HeadingOffset => -Drone.Heading;

        // MavlinkService의 리스트를 직접 참조하도록 변경하여 메모리 중복 방지
        // public ObservableCollection<DetectedTarget> DetectedTargets => _mavlinkService.DetectedTargets;

        public string FlightMode
        {
            get => _flightMode;
            set { if (SetProperty(ref _flightMode, value)) NotifyCommandStates(); }
        }

        public string ChatLog
        {
            get => _chatLog;
            set => SetProperty(ref _chatLog, value);
        }

        public DetectedTarget? SelectedTarget
        {
            get => _selectedTarget;
            set
            {
                if (SetProperty(ref _selectedTarget, value))
                    (FollowCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }

        public double PitchOffset
        {
            get => _pitchOffset;
            set => SetProperty(ref _pitchOffset, value);
        }
        // 연결 상태 텍스트
        public string ConnectionStatusText
        {
            get
            {
                // Drone 객체가 없거나 IsConnected가 false면 무조건 DISCONNECTED
                if (Drone == null || !Drone.IsConnected) return "DISCONNECTED";
                return "CONNECTED";
            }
        }

        // 연결 상태 색상
        public string ConnectionStatusColor => Drone.IsConnected ? "#22C55E" : "#EF4444";

        // 버튼 활성화 조건들
        public bool IsMissionStartEnabled => FlightMode == "TAKEOFF" && _currentWaypointCount > 0;
        public bool IsTakeoffEnabled => FlightMode == "READY" || FlightMode == "IDLE";
        public bool IsLandingEnabled => FlightMode == "TAKEOFF" && FlightMode != "MISSION";
        public bool IsRtlEnabled => FlightMode == "TAKEOFF" || FlightMode == "MISSION" || FlightMode == "TRACKING";
        public bool IsUndoEnabled => (FlightMode == "IDLE" || FlightMode == "TAKEOFF") && _currentWaypointCount > 0 && FlightMode != "MISSION";
        public bool IsClearEnabled => (FlightMode == "IDLE" || FlightMode == "TAKEOFF") && _currentWaypointCount > 0 && FlightMode != "MISSION";
        public bool IsFollowEnabled => SelectedTarget != null && FlightMode != "LANDING" && FlightMode != "TRACKING";
        public bool IsStopEnabled => FlightMode == "MISSION" || FlightMode == "TRACKING";
        public bool IsArmEnabled => (FlightMode == "IDLE" || FlightMode == "READY"); // && !_mavlinkService.CurrentState.IsArmed;
        public bool IsDisarmEnabled => (FlightMode == "IDLE" || FlightMode == "READY"); // && _mavlinkService.CurrentState.IsArmed;

        #endregion

        #region [3. 커맨드 (Commands)]
        public ICommand StartMissionCommand { get; }
        public ICommand StopMissionCommand { get; }
        public ICommand ArmCommand { get; }
        public ICommand DisarmCommand { get; }
        public ICommand TakeoffCommand { get; }
        public ICommand LandingCommand { get; }
        public ICommand RtlCommand { get; }
        public ICommand UndoWaypointCommand { get; }
        public ICommand ClearWaypointsCommand { get; }
        public ICommand FollowCommand { get; }
        // public ICommand StopTrackingCommand { get; }

        #endregion

        #region [4. 이벤트 (View 통신용)]
        public event Action? UndoWaypointRequested;
        public event Action? ClearWaypointsRequested;
        public event Action<double, double>? PositionUpdated;
        public event Action<double, double>? TakeoffPointSet;
        public event Action<double, double>? PersonDetectedOnMap;
        #endregion

        #region [5. 생성자 (Constructor)]
        public MainViewModel()
        {
            _mavlinkService = new MavlinkService();

            // 커맨드 초기화 (로직 분리)
            StartMissionCommand = new RelayCommand(ExecuteStartMission, () => IsMissionStartEnabled);
            ArmCommand = new RelayCommand(ExecuteArm, () => IsArmEnabled);
            DisarmCommand = new RelayCommand(ExecuteDisarm, () => IsDisarmEnabled);
            TakeoffCommand = new RelayCommand(ExecuteTakeoff, () => IsTakeoffEnabled);
            LandingCommand = new RelayCommand(ExecuteLanding, () => IsLandingEnabled);
            RtlCommand = new RelayCommand(ExecuteRtl, () => IsRtlEnabled);
            UndoWaypointCommand = new RelayCommand(ExecuteUndoWaypoint, () => IsUndoEnabled);
            ClearWaypointsCommand = new RelayCommand(ExecuteClearWaypoints, () => IsClearEnabled);
            FollowCommand = new RelayCommand(ExecuteFollow, () => IsFollowEnabled);
            StopMissionCommand = new RelayCommand(ExecuteStopMission, () => IsStopEnabled);
            // StopTrackingCommand = new RelayCommand(ExecuteStopTracking, () => IsStopEnabled);


            _targetLostTimer = new System.Timers.Timer(3000); // 3초 동안 데이터 없으면 소실 판단
            _targetLostTimer.Elapsed += (s, e) => {
                if (FlightMode == "TRACKING")
                {
                    Application.Current.Dispatcher.Invoke(() => {
                        FlightMode = "TAKEOFF";
                        ChatLog += "\n[경고] 타겟을 놓쳤습니다. 추적을 중단하고 대기합니다.";
                    });
                }
                _targetLostTimer.Stop();
            };

            // 서비스 시작 및 이벤트 구독
            InitializeService();
        }
        #endregion

        #region [6. 이벤트 핸들러 (Event Handlers)]
        private async void InitializeService()
        {
            await _mavlinkService.StartAsync();

            // 드론의 데이터 업데이트(화면의 계기판들을 최신화)
            _mavlinkService.OnDataUpdated += (state) =>
            {
                Application.Current.Dispatcher.BeginInvoke(() => HandleDroneData(state));
            };

            // 타겟 업데이트
            /*_mavlinkService.OnTargetLocationUpdated += (id, lat, lng, type, isTracked) =>
            {
                Application.Current.Dispatcher.BeginInvoke(async () =>
                {
                    // 1. 지도에 마커 표시 (기존 유지)
                    PersonDetectedOnMap?.Invoke(lat, lng);

                    if (id == SelectedTarget?.ID)
                    {
                        _targetLostTimer.Stop(); // 타이머 리셋
                        _targetLostTimer.Start();
                    }

                    // 2. 현재 추적 모드이고, 이 타겟이 내가 선택한 타겟이라면?
                    if (FlightMode == "TRACKING" && SelectedTarget != null && SelectedTarget.ID == id)
                    {
                        // 드론에게 실시간 좌표 전송!
                        await _mavlinkService.GoTo(lat, lng);
                    }
                });
            };*/

            // 웨이포인트 개수 변화
            _mavlinkService.OnWaypointCountChanged += (count) =>
            {
                _currentWaypointCount = count;
                NotifyCommandStates();
            };

            _mavlinkService.OnMissionCompleted += () =>
            {
                Application.Current.Dispatcher.BeginInvoke(() => {
                    FlightMode = "TAKEOFF";
                    ChatLog += "\n모든 목적지에 도착하여 미션을 종료합니다.";
                });
            };

            _mavlinkService.OnLogReceived += (message) =>
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    ChatLog += $"\n{message}";
                });
            };
        }

        private void HandleDroneData(DroneState state)
        {
            if (!Drone.IsConnected)
            {
                Drone.IsConnected = true;
                // ⭐ 연결 상태가 변했음을 알리는 신호들
                OnPropertyChanged(nameof(ConnectionStatusText));
                OnPropertyChanged(nameof(ConnectionStatusColor));
            }

            // 2. 기존 위치 업데이트 로직
            if (state.TakeoffLat.HasValue && state.TakeoffLng.HasValue)
                TakeoffPointSet?.Invoke(state.TakeoffLat.Value, state.TakeoffLng.Value);

            PositionUpdated?.Invoke(state.Latitude, state.Longitude);

            // 3. HUD 계산 (Pitch 오프셋)
            PitchOffset = state.Pitch * 3.55;

            // 4. 배터리 경고
            if (state.BatteryPercent > 0 && state.BatteryPercent <= 20 && !ChatLog.Contains("배터리 부족"))
            {
                ChatLog += $"\n[경고] 기체 배터리 부족: {state.BatteryPercent}%";
            }

            // 5. UI 알림 (중요: HeadingOffset 추가)
            OnPropertyChanged(nameof(BatteryPercent));
            OnPropertyChanged(nameof(PitchOffset));
            OnPropertyChanged(nameof(HeadingOffset)); // 나침반 업데이트용
            OnPropertyChanged(nameof(Drone));
            OnPropertyChanged(nameof(ConnectionStatusText));
            OnPropertyChanged(nameof(ConnectionStatusColor));

            // 6. 착륙 감지
            if (FlightMode == "LANDING" && state.Altitude <= 0.1)
            {
                FlightMode = "IDLE";
                ChatLog += "\n착륙 완료. 시스템 대기 중.";
            }
        }
        #endregion

        #region [7. 실행 메서드 (Execute Methods)]

        // 1. 지정한 웨이포인트 가기 위한 미션 시작
        private async void ExecuteStartMission()
        {
            try
            {
                // void 메서드이므로 Task.Run으로 감싸 UI 스레드 멈춤 완벽 방지!
                await Task.Run(() => _mavlinkService.StartMission());
                FlightMode = "MISSION";
                ChatLog += "\n미션을 시작합니다.";
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 미션 시작 실패: {ex.Message}";
            }
        }

        // 2. 지정한 웨이포인트 가기 위한 미션 중단
        private async void ExecuteStopMission()
        {
            try
            {
                await Task.Run(() => _mavlinkService.StopMission());
                FlightMode = "TAKEOFF";
                ChatLog += "\n미션을 중단했습니다.";
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 미션 중단 실패: {ex.Message}";
            }
        }

        // 시동걸기
        private async void ExecuteArm()
        {
            try
            {
                await _mavlinkService.SendArmAsync();
                // 드론이 실제로 시동이 걸리면 상태가 업데이트되겠지만, 일단 명령 전송 로그를 띄웁니다.
                ChatLog += "\n[명령] 시동(Arm) 명령 전송 완료";

            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 시동 명령 실패: {ex.Message}";
            }
        }

        // 시동 끄기
        private async void ExecuteDisarm()
        {
            try
            {
                await _mavlinkService.SendDisarmAsync();
                ChatLog += "\n[명령] 시동 끄기(Disarm) 명령 전송 완료";

                // (DisarmCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 시동 끄기 명령 실패: {ex.Message}";
            }
        }

        // 3. 이륙 (Takeoff는 원래 비동기(Task) 메서드이므로 바로 await)
        private async void ExecuteTakeoff()
        {
            try
            {
                await _mavlinkService.Takeoff(5.0f);
                FlightMode = "TAKEOFF";
                ChatLog += "\n[명령] 이륙 명령 전송 (목표 고도: 5m)";

                if (Drone.Latitude != 0 && Drone.Longitude != 0)
                {
                    TakeoffPointSet?.Invoke(Drone.Latitude, Drone.Longitude);
                }
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 이륙 명령 실패: {ex.Message}";
            }
        }

        // 4. 착륙 및 웨이포인트 초기화
        private async void ExecuteLanding()
        {
            try
            {
                await _mavlinkService.Landing();
                FlightMode = "LANDING";

                await Task.Run(() => _mavlinkService.ClearWaypoints());
                ClearWaypointsRequested?.Invoke();

                ChatLog += "\n[명령] 착륙 명령 전송 및 목적지 초기화";
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 착륙 명령 실패: {ex.Message}";
            }
        }

        // 5. 마지막 웨이포인트 취소
        private async void ExecuteUndoWaypoint()
        {
            try
            {
                await Task.Run(() => _mavlinkService.UndoWaypoint());
                UndoWaypointRequested?.Invoke();
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 목적지 취소 실패: {ex.Message}";
            }
        }

        // 6. 모든 웨이포인트 삭제
        private async void ExecuteClearWaypoints()
        {
            try
            {
                await Task.Run(() => _mavlinkService.ClearWaypoints());
                ClearWaypointsRequested?.Invoke();
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 목적지 삭제 실패: {ex.Message}";
            }
        }

        // 7. 객체 추적 실행
        private async void ExecuteFollow()
        {
            try
            {
                if (SelectedTarget == null) return;

                await Task.Run(() => _mavlinkService.StopMission());
                await _mavlinkService.GoTo(SelectedTarget.Lat, SelectedTarget.Lng);

                FlightMode = "TRACKING";
                ChatLog += $"\n[시스템] 타겟 추적 모드: ID {SelectedTarget.ID}";
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 타겟 추적 실패: {ex.Message}";
            }
        }

        // 8. 복귀 (RTL)
        private async void ExecuteRtl()
        {
            try
            {
                await _mavlinkService.Rtl();
                ChatLog += "\n[명령] 복귀(RTL) 명령 전송.";
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 복귀 명령 실패: {ex.Message}";
            }
        }

        // 9. 객체 추적 중단
        /*private async void ExecuteStopTracking()
        {
            try
            {
                await Task.Run(() => _mavlinkService.StopMission());
                await _mavlinkService.StopTracking();

                FlightMode = "LOITER";
                ChatLog += "\n[시스템] 추적 자율 주행 중단.";
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 추적 중단 실패: {ex.Message}";
            }
        }*/

        // 10. 지도에서 웨이포인트 추가
        public async void AddWaypointFromMap(double lat, double lng)
        {
            try
            {
                await Task.Run(() => _mavlinkService.AddWaypoint(lat, lng));
                ChatLog += $"\n[지도] 목적지 추가: {lat:F5}, {lng:F5}";
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 목적지 추가 실패: {ex.Message}";
            }
        }
        #endregion

        private void NotifyCommandStates()
        {
            OnPropertyChanged(nameof(IsMissionStartEnabled));
            OnPropertyChanged(nameof(IsTakeoffEnabled));
            OnPropertyChanged(nameof(IsLandingEnabled));
            OnPropertyChanged(nameof(IsStopEnabled));
            OnPropertyChanged(nameof(IsUndoEnabled));
            OnPropertyChanged(nameof(IsClearEnabled));
            OnPropertyChanged(nameof(IsFollowEnabled));

            Application.Current.Dispatcher.Invoke(() =>
            {
                CommandManager.InvalidateRequerySuggested();
            });
        }

        public void Dispose()
        {
            _targetLostTimer?.Stop();
            _targetLostTimer?.Dispose();
            _mavlinkService?.StopAsync();
        }
    }
}