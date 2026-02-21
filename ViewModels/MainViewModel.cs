using GCS_Project.Models;
using GCS_Project.Services;
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
            RtlCommand = new RelayCommand(ExecuteRtl);
            StopTrackingCommand = new RelayCommand(ExecuteStopTracking, () => FlightMode == "TRACKING");

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
        private void InitializeService()
        {
            _mavlinkService.Start();

            // 드론의 데이터 업데이트(화면의 계기판들을 최신화)
            _mavlinkService.OnDataUpdated += (state) =>
            {
                Application.Current.Dispatcher.Invoke(() => HandleDroneData(state));
            };

            // 타겟 업데이트
            _mavlinkService.OnTargetLocationUpdated += (id, lat, lng, type, isTracked) =>
            {
                Application.Current.Dispatcher.Invoke(async () =>
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
                        await _mavlinkService.SendTrackCoordinate(lat, lng);
                    }
                });
            };

            // 웨이포인트 개수 변화
            _mavlinkService.OnWaypointCountChanged += (count) =>
            {
                _currentWaypointCount = count;
                NotifyCommandStates();
            };

            _mavlinkService.OnMissionCompleted += () =>
            {
                Application.Current.Dispatcher.Invoke(() => {
                    FlightMode = "TAKEOFF";
                    ChatLog += "\n모든 목적지에 도착하여 미션을 종료합니다.";
                });
            };

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
        // 1. 미션 시작
        private async void ExecuteStartMission()
        {
            // void 메서드를 Task.Run으로 감싸서 비동기로 대기 가능하게 만듭니다.
            await Task.Run(() => _mavlinkService.StartMission());
            FlightMode = "MISSION";
            ChatLog += "\n미션을 시작합니다.";
        }

        // 2. 미션 중단
        private async void ExecuteStopMission()
        {
            await Task.Run(() => _mavlinkService.StopMission());
            FlightMode = "TAKEOFF";
            ChatLog += "\n미션을 중단했습니다.";
        }

        // 3. 이륙
        private async void ExecuteTakeoff()
        {
            await Task.Run(() => _mavlinkService.Takeoff("5.0"));
            FlightMode = "TAKEOFF";
            ChatLog += "\n이륙 명령 전송 (목표 고도: 5m)";
        }

        // 4. 착륙 및 웨이포인트 초기화
        private async void ExecuteLanding()
        {
            await Task.Run(() => _mavlinkService.Landing());
            FlightMode = "LANDING";
            await Task.Run(() => _mavlinkService.ClearWaypoints());
            ClearWaypointsRequested?.Invoke();
        }

        // 5. 웨이포인트 취소 (간단한 작업은 동기 유지 가능하지만, 통일성을 위해 Task.Run 권장)
        private async void ExecuteUndoWaypoint()
        {
            await Task.Run(() => _mavlinkService.UndoWaypoint());
            UndoWaypointRequested?.Invoke();
        }

        // 6. 모든 웨이포인트 삭제
        private async void ExecuteClearWaypoints()
        {
            await Task.Run(() => _mavlinkService.ClearWaypoints());
            ClearWaypointsRequested?.Invoke();
        }

        private async void ExecuteFollow()
        {
            if (SelectedTarget == null) return;
            await Task.Run(() => {
                _mavlinkService.StopMission();
                _mavlinkService.SendFollowCommand(SelectedTarget.ID);
            });
            FlightMode = "TRACKING";
            ChatLog += $"\n[시스템] 타겟 추적 모드: ID {SelectedTarget.ID}";
        }

        private async void ExecuteRtl()
        {
            await Task.Run(() => _mavlinkService.SendMavlinkCommand(20, 0f));
            ChatLog += "\n[명령] 복귀(RTL) 명령 전송.";
        }

        private async void ExecuteStopTracking()
        {
            await Task.Run(() => {
                _mavlinkService.StopMission();
                _mavlinkService.SendFollowCommand(-1);
            });
            FlightMode = "TAKEOFF";
            ChatLog += "\n[시스템] 추적 자율 주행 중단.";
        }

        public async void AddWaypointFromMap(double lat, double lng)
        {
            await Task.Run(() => _mavlinkService.AddWaypoint(lat, lng));
            ChatLog += $"\n[지도] 목적지 추가: {lat:F5}, {lng:F5}";
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
        }
    }
}