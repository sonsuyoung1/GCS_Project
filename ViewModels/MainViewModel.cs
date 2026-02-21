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
        #endregion

        #region [2. 속성 (Properties)]
        public MavlinkService MavlinkService => _mavlinkService;
        public DroneState Drone => _mavlinkService.CurrentState;
        public int BatteryPercent => Drone.BatteryPercent;

        // MavlinkService의 리스트를 직접 참조하도록 변경하여 메모리 중복 방지
        public ObservableCollection<DetectedTarget> DetectedTargets => _mavlinkService.DetectedTargets;

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

        // 버튼 활성화 조건들
        public bool IsMissionStartEnabled => FlightMode == "TAKEOFF" && _currentWaypointCount > 0;
        public bool IsTakeoffEnabled => FlightMode == "READY" || FlightMode == "IDLE";
        public bool IsLandingEnabled => FlightMode == "TAKEOFF" && FlightMode != "MISSION";
        public bool IsUndoEnabled => (FlightMode == "IDLE" || FlightMode == "TAKEOFF") && _currentWaypointCount > 0 && FlightMode != "MISSION";
        public bool IsClearEnabled => (FlightMode == "IDLE" || FlightMode == "TAKEOFF") && _currentWaypointCount > 0 && FlightMode != "MISSION";
        public bool IsFollowEnabled => SelectedTarget != null && FlightMode != "LANDING" && FlightMode != "TRACKING";
        public bool IsStopEnabled => FlightMode == "MISSION" || FlightMode == "TRACKING";
        #endregion

        #region [3. 커맨드 (Commands)]
        public ICommand StartMissionCommand { get; }
        public ICommand StopMissionCommand { get; }
        public ICommand TakeoffCommand { get; }
        public ICommand LandingCommand { get; }
        public ICommand UndoWaypointCommand { get; }
        public ICommand ClearWaypointsCommand { get; }
        public ICommand FollowCommand { get; }
        public ICommand StopTrackingCommand { get; }
        public ICommand RtlCommand { get; }
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
            StartMissionCommand = new RelayCommand(ExecuteStartMission);
            StopMissionCommand = new RelayCommand(ExecuteStopMission);
            TakeoffCommand = new RelayCommand(ExecuteTakeoff);
            LandingCommand = new RelayCommand(ExecuteLanding);
            UndoWaypointCommand = new RelayCommand(ExecuteUndoWaypoint);
            ClearWaypointsCommand = new RelayCommand(ExecuteClearWaypoints);
            FollowCommand = new RelayCommand(ExecuteFollow, () => SelectedTarget != null);
            RtlCommand = new RelayCommand(() =>
            {
                ChatLog += "\n[TODO] RTL 명령은 v4 송신 붙인 뒤 구현";
            });

            StopTrackingCommand = new RelayCommand(async () =>
            {
                // 만약 StopMission이 내부적으로 전송 패킷을 기다려야 하는 Task로 바뀌었다면 await를 붙입니다.
                // 현재는 단순 플래그 변경이므로 그대로 두셔도 되지만, 흐름상 async를 유지합니다.
                ChatLog += "\n[TODO] StopMission 기능 v4에서 아직 미구현";

                FlightMode = "TAKEOFF";
                ChatLog += "\n[시스템] 추적 자율 주행을 중단합니다.";

                // UI 업데이트 알림 (필요 시)
                NotifyCommandStates();
            }, () => FlightMode == "TRACKING");

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
        }
        #endregion

        #region [6. 이벤트 핸들러 (Event Handlers)]
        private void InitializeService()
        {
            // v4: async start (일단 연결 테스트용)
            //_ = _mavlinkService.StartAsync("0.0.0.0", 14550);

            _mavlinkService.OnDataUpdated += (state) =>
            {
                Application.Current.Dispatcher.Invoke(() => HandleDroneData(state));
            };

            /*_mavlinkService.OnTargetLocationUpdated += (id, lat, lng, type, isTracked) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    PersonDetectedOnMap?.Invoke(lat, lng);

                    if (id == SelectedTarget?.ID)
                    {
                        _targetLostTimer.Stop();
                        _targetLostTimer.Start();
                    }

                    if (FlightMode == "TRACKING" && SelectedTarget != null && SelectedTarget.ID == id)
                    {
                        _mavlinkService.SendTrackCoordinate(lat, lng); // 이건 형님 서비스에 이미 있음
                    }
                });
            };*/

            /*_mavlinkService.OnWaypointCountChanged += (count) =>
            {
                _currentWaypointCount = count;
                NotifyCommandStates();
            };*/

            /*_mavlinkService.OnMissionCompleted += () =>
            {
                Application.Current.Dispatcher.Invoke(() => {
                    FlightMode = "TAKEOFF";
                    ChatLog += "\n모든 목적지에 도착하여 미션을 종료합니다.";
                });
            };*/

            _mavlinkService.OnLogReceived += (message) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ChatLog += $"\n{message}";
                });
            };
        }

        private void HandleDroneData(DroneState state)
        {
            if (state.TakeoffLat.HasValue && state.TakeoffLng.HasValue)
                TakeoffPointSet?.Invoke(state.TakeoffLat.Value, state.TakeoffLng.Value);

            PositionUpdated?.Invoke(state.Latitude, state.Longitude);

            OnPropertyChanged(nameof(BatteryPercent));
            OnPropertyChanged("Drone");

            if (FlightMode == "LANDING" && state.Altitude <= 0.1)
            {
                FlightMode = "IDLE";
                ChatLog += "\n착륙 완료. 시스템 대기 중.";
            }
        }
        #endregion

        #region [7. 실행 메서드 (Execute Methods)]
        private void ExecuteStartMission() { ChatLog += "\n[TODO] StartMission"; }
        private void ExecuteStopMission() { ChatLog += "\n[TODO] StopMission"; }
        private void ExecuteTakeoff() { ChatLog += "\n[TODO] Takeoff"; }
        private void ExecuteLanding() { ChatLog += "\n[TODO] Landing"; }
        private void ExecuteUndoWaypoint() { ChatLog += "\n[TODO] UndoWaypoint"; }
        private void ExecuteClearWaypoints() { ChatLog += "\n[TODO] ClearWaypoints"; }
        public void AddWaypointFromMap(double lat, double lng) { ChatLog += $"\n[TODO] AddWaypoint {lat},{lng}"; }
        private void ExecuteFollow()
        {
            if (SelectedTarget != null)
            {
                if (SelectedTarget != null)
                {
                    // 1. 미션 실행 중이었다면 내부적으로 중단
                    ChatLog += "\n[TODO] StopMission";

                    // 2. 비행 모드를 TRACKING(GCS 내부 상태)으로 변경
                    FlightMode = "TRACKING";

                    // 3. 드론에게 첫 번째 추적 패킷 전송
                    ChatLog += $"\n[TODO] FollowCommand(MAVLink 송신) 구현 전, 선택 ID: {SelectedTarget.ID}";

                    ChatLog += $"\n[시스템] 자율 추적 모드 전환: ID {SelectedTarget.ID}";
                }
            }
        }

        public void StopTracking()
        {
            if (FlightMode == "TRACKING")
            {
                FlightMode = "TAKEOFF";
                ChatLog += "\n[시스템] 추적을 종료하고 대기 모드로 전환합니다.";
                // 추적 중 마커 표시 해제 등을 위해 서비스에 알림
                ChatLog += "\n[TODO] 추적 해제 송신 구현 전";
            }
        }

        private void NotifyCommandStates()
        {
            OnPropertyChanged(nameof(IsMissionStartEnabled));
            OnPropertyChanged(nameof(IsTakeoffEnabled));
            OnPropertyChanged(nameof(IsLandingEnabled));
            OnPropertyChanged(nameof(IsStopEnabled));
            OnPropertyChanged(nameof(IsUndoEnabled));
            OnPropertyChanged(nameof(IsClearEnabled));
            OnPropertyChanged(nameof(IsFollowEnabled));
        }
    }
}
#endregion