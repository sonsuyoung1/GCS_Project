using GCS_Project.Data;
using GCS_Project.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace GCS_Project.ViewModels
{
    public partial class ViewModel
    {
        #region [1. 기본 비행 명령]

        // 드론 미션 비행을 시작한다.
        private async void ExecuteStartMission()
        {
            try
            {
                await Task.Run(() => _mavlinkService.StartMission());

                FlightMode = MODE_MISSION;
                AppendLog("[명령] 미션을 시작합니다.");
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 미션 시작 실패: {ex.Message}");
            }
        }

        // 현재 진행 중인 미션을 중단한다.
        private async void ExecuteStopMission()
        {
            try
            {
                await _mavlinkService.StopMission();

                FlightMode = MODE_TAKEOFF;
                AppendLog("[명령] 미션을 중단했습니다.");
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 미션 중단 실패: {ex.Message}");
            }
        }

        // 드론에 시동(Arm) 명령을 전송한다.
        private async void ExecuteArm()
        {
            try
            {
                await _mavlinkService.SendArmAsync();
                AppendLog("[명령] 시동(Arm) 명령 전송 완료");
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 시동 명령 실패: {ex.Message}");
            }
        }

        // 드론에 시동 끄기(Disarm) 명령을 전송한다.
        private async void ExecuteDisarm()
        {
            try
            {
                await _mavlinkService.SendDisarmAsync();
                AppendLog("[명령] 시동 끄기(Disarm) 명령 전송 완료");
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 시동 끄기 명령 실패: {ex.Message}");
            }
        }

        // 설정된 목표 고도로 이륙 명령을 전송한다.
        private async void ExecuteTakeoff()
        {
            try
            {
                await _mavlinkService.Takeoff((float)Drone.TargetAltitude);

                FlightMode = MODE_TAKEOFF;
                AppendLog($"[명령] 이륙 명령 전송 (목표 고도: {Drone.TargetAltitude}m)");

                if (Drone.Latitude != 0 && Drone.Longitude != 0)
                {
                    TakeoffPointSet?.Invoke(Drone.Latitude, Drone.Longitude);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 이륙 명령 실패: {ex.Message}");
            }
        }

        // 착륙 명령을 전송하고 지도와 웨이포인트를 초기화한다.
        private async void ExecuteLanding()
        {
            try
            {
                await _mavlinkService.Landing();

                await Task.Run(() => _mavlinkService.ClearWaypoints());
                AllClearRequested?.Invoke();

                AppendLog("[명령] 착륙 명령 전송 및 목적지 초기화");
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 착륙 명령 실패: {ex.Message}");
            }
        }

        // 복귀(RTL) 명령을 전송하고 지도와 웨이포인트를 초기화한다.
        private async void ExecuteRtl()
        {
            try
            {
                await _mavlinkService.Rtl();
                AppendLog("[명령] 복귀(RTL) 명령 전송");

                await Task.Run(() => _mavlinkService.ClearWaypoints());
                AllClearRequested?.Invoke();
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 복귀 명령 실패: {ex.Message}");
            }
        }

        #endregion


        #region [2. 웨이포인트 명령]

        // 마지막으로 추가한 웨이포인트를 취소한다.
        private async void ExecuteUndoWaypoint()
        {
            try
            {
                await Task.Run(() => _mavlinkService.UndoWaypoint());
                UndoWaypointRequested?.Invoke();
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 목적지 취소 실패: {ex.Message}");
            }
        }

        // 등록된 모든 웨이포인트를 삭제한다.
        private async void ExecuteClearWaypoints()
        {
            try
            {
                await Task.Run(() => _mavlinkService.ClearWaypoints());
                ClearWaypointsRequested?.Invoke();
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 목적지 삭제 실패: {ex.Message}");
            }
        }

        // 지도에서 클릭한 좌표를 웨이포인트로 추가한다.
        public async void AddWaypointFromMap(double lat, double lng)
        {
            try
            {
                await Task.Run(() => _mavlinkService.AddWaypoint(lat, lng));
                AppendLog($"[지도] 목적지 추가: {lat:F5}, {lng:F5}");
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 목적지 추가 실패: {ex.Message}");
            }
        }

        #endregion


        #region [3. 타격 명령]

        // 두 GPS 좌표 사이의 거리를 미터 단위로 계산한다.
        private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
        {
            const double EarthRadiusMeters = 6371000.0;

            double dLat = (lat2 - lat1) * Math.PI / 180.0;
            double dLon = (lon2 - lon1) * Math.PI / 180.0;

            double radLat1 = lat1 * Math.PI / 180.0;
            double radLat2 = lat2 * Math.PI / 180.0;

            double a =
                Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(radLat1) * Math.Cos(radLat2) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

            return EarthRadiusMeters * c;
        }

        // 선택된 타겟을 향해 반복적으로 GoTo 명령을 보내는 타격 루프를 시작한다.
        private async void ExecuteStrike()
        {
            if (SelectedTarget == null) return;
            if (IsStriking) return;

            IsStriking = true;

            var targetToStrike = SelectedTarget;

            SaveTargetLogAsync(targetToStrike, isStriked: true);

            await _guidanceCommandService.SendSelectedTargetAsync(targetToStrike.ID);
            await _guidanceCommandService.SendStartGuidanceAsync();

            AppendLog($"[작전] 타겟(ID:{targetToStrike.ID}) 선택 및 타격 가이던스 시작 명령 전송");
        }

        // 타격 루프를 중지하고 기체를 제자리 유지 상태로 전환한다.
        private async void ExecuteStopStrike()
        {
            if (!IsStriking) return;

            IsStriking = false;

            await _guidanceCommandService.SendStopGuidanceAsync();

            AppendLog("[작전] 타격 가이던스 중지 명령 전송");
        }

        #endregion


        #region [4. 비행 기록 조회]

        // 선택된 날짜 또는 현재 세션의 비행 궤적을 지도에 표시하거나 숨긴다.
        private async Task ExecuteShowHistory()
        {
            if (_isHistoryVisible)
            {
                HideHistoryPath();
                return;
            }

            try
            {
                await Task.Run(() => LoadAndShowHistoryPath());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DB 에러] {ex.Message}");
                AppendLog($"[에러] 데이터 변환 실패: {ex.Message}");
            }
        }

        // 지도에 표시 중인 비행 궤적을 숨긴다.
        private void HideHistoryPath()
        {
            _isHistoryVisible = false;
            HistoryPathRequested?.Invoke(new List<Tuple<double, double>>());
            AppendLog("[시스템] 비행 궤적을 숨겼습니다.");
        }

        // DB에서 비행 기록을 불러와 지도에 표시한다.
        private void LoadAndShowHistoryPath()
        {
            using (var db = new AppDbContext())
            {
                List<FlightLog> logs = LoadFlightLogs(db);

                if (logs.Count == 0)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        AppendLog("[알림] 표시할 비행 기록이 없습니다.");
                    });

                    return;
                }

                var path = logs
                    .Select(l => new Tuple<double, double>(l.Latitude, l.Longitude))
                    .ToList();

                _isHistoryVisible = true;
                HistoryPathRequested?.Invoke(path);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    AppendLog($"[성공] {logs.Count}개의 비행 궤적을 표시합니다.");
                });
            }
        }

        // 선택 날짜가 있으면 해당 날짜, 없으면 현재 세션의 비행 기록을 조회한다.
        private List<FlightLog> LoadFlightLogs(AppDbContext db)
        {
            if (SelectedHistoryDate.HasValue)
            {
                DateTime targetDate = SelectedHistoryDate.Value.Date;
                DateTime nextDate = targetDate.AddDays(1);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    AppendLog($"[시스템] {targetDate:yyyy-MM-dd}의 비행 기록을 분석 중입니다...");
                });

                return db.FlightLogs
                    .Where(x => x.Timestamp >= targetDate && x.Timestamp < nextDate)
                    .OrderBy(x => x.Timestamp)
                    .ToList();
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                AppendLog("[시스템] 현재 세션의 비행 기록을 분석 중입니다...");
            });

            return db.FlightLogs
                .Where(x => x.Timestamp >= _programStartTime)
                .OrderBy(x => x.Timestamp)
                .ToList();
        }
        #endregion


        #region [5. 탐색 명령]

        // 지정된 시간 동안 제자리 회전 탐색을 수행한다.
        private async Task ExecuteScan(double seconds)
        {
            try
            {
                if (_isScanning)
                {
                    AppendLog("[시스템] 이미 탐색 중입니다.");
                    return;
                }

                if (!Drone.IsArmed)
                {
                    AppendLog("[시스템] 시동 후에만 탐색할 수 있습니다.");
                    return;
                }

                if (seconds <= 0)
                {
                    seconds = 10;
                }

                _scanCts?.Cancel();
                _scanCts = new CancellationTokenSource();

                CancellationToken token = _scanCts.Token;

                _isScanning = true;
                AppendLog($"[시스템] {seconds:F0}초 동안 회전 탐색을 시작합니다.");

                _mavlinkService.StartManualControl();

                int totalMs = (int)(seconds * 1000);
                int stepMs = 100;
                int elapsed = 0;

                while (elapsed < totalMs && !token.IsCancellationRequested)
                {
                    _mavlinkService.SetJoystick(0, 0, 500, 250);

                    await Task.Delay(stepMs, token);
                    elapsed += stepMs;
                }

                if (!token.IsCancellationRequested)
                {
                    AppendLog("[시스템] 회전 탐색을 종료합니다.");
                }
            }
            catch (TaskCanceledException)
            {
                AppendLog("[시스템] 회전 탐색이 중단되었습니다.");
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 회전 탐색 실패: {ex.Message}");
            }
            finally
            {
                StopScanControl();
            }
        }

        // 진행 중인 회전 탐색을 중지한다.
        private void ExecuteStopScan()
        {
            try
            {
                if (!_isScanning)
                {
                    AppendLog("[시스템] 현재 탐색 중이 아닙니다.");
                    return;
                }

                _scanCts?.Cancel();
                StopScanControl();

                AppendLog("[시스템] 탐색을 중지합니다.");
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 탐색 중지 실패: {ex.Message}");
            }
        }

        // 탐색에 사용한 조이스틱 입력과 수동 제어 송신을 정리한다.
        private void StopScanControl()
        {
            _mavlinkService.SetJoystick(0, 0, 500, 0);
            _mavlinkService.StopManualControl();
            _isScanning = false;
        }

        #endregion


        #region [6. 버튼 상태 갱신]

        // 버튼 커맨드들의 CanExecute 상태를 다시 계산하도록 알린다.
        private void NotifyCommandStates()
        {
            (StartMissionCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (StopMissionCommand as RelayCommand)?.NotifyCanExecuteChanged();

            (ArmCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (DisarmCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (TakeoffCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (LandingCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (RtlCommand as RelayCommand)?.NotifyCanExecuteChanged();

            (UndoWaypointCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (ClearWaypointsCommand as RelayCommand)?.NotifyCanExecuteChanged();

            (StrikeCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (StopStrikeCommand as RelayCommand)?.NotifyCanExecuteChanged();

            Application.Current.Dispatcher.Invoke(() =>
            {
                CommandManager.InvalidateRequerySuggested();
            });
        }

        #endregion
    }
}