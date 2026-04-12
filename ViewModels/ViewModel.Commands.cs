using Asv.Mavlink.Minimal;
using GCS_Project.Data;
using GCS_Project.Models;
using GCS_Project.xServices;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace GCS_Project.ViewModels
{
    public partial class ViewModel
    {
        #region [ 버튼 실행 메서드 (Execute Methods)]
        // 1. 지정한 웨이포인트 가기 위한 미션 시작
        private async void ExecuteStartMission()
        {
            try
            {
                // void 메서드이므로 Task.Run으로 감싸 UI 스레드 멈춤 완벽 방지
                await Task.Run(() => _mavlinkService.StartMission());
                FlightMode = MODE_MISSION;
                AppendLog("미션을 시작합니다.");
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 미션 시작 실패: {ex.Message}");
            }
        }

        // 2. 지정한 웨이포인트 가기 위한 미션 중단
        private async void ExecuteStopMission()
        {
            try
            {
                await Task.Run(() => _mavlinkService.StopMission());
                FlightMode = MODE_TAKEOFF;
                AppendLog("미션을 중단했습니다.");
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 미션 중단 실패: {ex.Message}");
            }
        }

        // 시동걸기
        private async void ExecuteArm()
        {
            try
            {
                await _mavlinkService.SendArmAsync();
                // 드론이 실제로 시동이 걸리면 상태가 업데이트되겠지만, 일단 명령 전송 로그를 띄웁니다.
                AppendLog("[명령] 시동(Arm) 명령 전송 완료");

            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 시동 명령 실패: {ex.Message}");
            }
        }

        // 시동 끄기
        private async void ExecuteDisarm()
        {
            try
            {
                await _mavlinkService.SendDisarmAsync();
                AppendLog("[명령] 시동 끄기(Disarm) 명령 전송 완료");

                // (DisarmCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 시동 끄기 명령 실패: {ex.Message}");
            }
        }

        // 3. 이륙
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

        // 4. 착륙 및 웨이포인트 초기화
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

        // 5. 복귀 (RTL)
        private async void ExecuteRtl()
        {
            try
            {
                await _mavlinkService.Rtl();
                AppendLog("[명령] 복귀(RTL) 명령 전송.");

                await Task.Run(() => _mavlinkService.ClearWaypoints());
                AllClearRequested?.Invoke();
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 복귀 명령 실패: {ex.Message}");
            }
        }

        // 6. 마지막 웨이포인트 취소
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

        // 7. 모든 웨이포인트 삭제
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

        // 8. 지도에서 웨이포인트 추가
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

        // 드론과 물체의 거리 계산
        private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000.0; // 지구의 반지름 (미터)

            double dLat = (lat2 - lat1) * Math.PI / 180.0;
            double dLon = (lon2 - lon1) * Math.PI / 180.0;

            double radLat1 = lat1 * Math.PI / 180.0;
            double radLat2 = lat2 * Math.PI / 180.0;

            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(radLat1) * Math.Cos(radLat2) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

            return R * c;
        }

        // 9. 타격 명령 실행 메서드
        private async void ExecuteStrike()
        {
            if (SelectedTarget == null) return;

            IsStriking = true; // 타격 상태 ON
            var targetToStrike = SelectedTarget;

            // 로그 도배를 막기 위한 플래그
            bool impactImminentLogged = false;

            AppendLog($"[작전] 타겟(ID:{targetToStrike.ID})으로 종말 유도(자폭) 돌격을 개시합니다!");

            // 타격 시 돌진 속도를 최대로 올림 (20m/s)
            await _mavlinkService.ChangeTargetSpeed(20.0f);

            // 실시간 추적 루프 (Background Task)
            _ = Task.Run(async () =>
            {
                try
                {
                    while (IsStriking)
                    {
                        // 타겟 최신화
                        if (SelectedTarget != null)
                        {
                            targetToStrike = SelectedTarget;
                        }

                        // 1. 현재 드론 위치와 타겟 위치의 거리(m) 계산
                        double distance = CalculateDistance(Drone.Latitude, Drone.Longitude, targetToStrike.Lat, targetToStrike.Lng);

                        // 2. 거리가 3m 이하로 들어왔고, 아직 알림을 안 띄웠다면?
                        if (distance <= 3.0 && !impactImminentLogged)
                        {
                            impactImminentLogged = true; // 알림 잠금 (한 번만 실행되도록)

                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                AppendLog("🚨 [경고] 타겟 명중 임박 (Impact Imminent)! 충돌 대비!");

                                // (추가 팁) 만약 화면을 빨갛게 번쩍이게 하거나 경고음을 넣고 싶다면
                                // 이 위치에서 이벤트를 발생시키거나 변수를 조작하면 됩니다!
                            });
                        }

                        // 고도를 0(지면)으로 설정하여 돌진
                        await _mavlinkService.GoTo(targetToStrike.Lat, targetToStrike.Lng, 0f);

                        // 0.5초 대기 (2Hz 갱신)
                        await Task.Delay(500);
                    }
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                        AppendLog($"[오류] 타격 유도 루프 중단: {ex.Message}"));
                }
            });
        }

        // 10. 타격 명령 취소 메서드
        private async void ExecuteStopStrike()
        {
            IsStriking = false; // 타격 중 상태 스위치 OFF
            RefreshCommandStatesIfNeeded();
            AppendLog("[작전] 타격 중지. 제자리 비행으로 전환합니다.");

            await _mavlinkService.Hold();
        }

        //   비행 경로 찍어주는 명령
        private async Task ExecuteShowHistory()
        {
            if (_isHistoryVisible)
            {
                _isHistoryVisible = false;
                HistoryPathRequested?.Invoke(new List<Tuple<double, double>>());
                AppendLog("[시스템] 비행 궤적을 숨겼습니다.");
                return;
            }

            try
            {
                await Task.Run(() =>
                {
                    using (var db = new AppDbContext())
                    {
                        List<FlightLog> logs;

                        if (SelectedHistoryDate.HasValue)
                        {
                            DateTime targetDate = SelectedHistoryDate.Value.Date;
                            DateTime nextDate = targetDate.AddDays(1);

                            Application.Current.Dispatcher.Invoke(() =>
                                AppendLog($"[시스템] {targetDate:yyyy-MM-dd}의 비행 기록을 분석 중입니다..."));

                            logs = db.FlightLogs
                                     .Where(x => x.Timestamp >= targetDate && x.Timestamp < nextDate)
                                     .OrderBy(x => x.Timestamp)
                                     .ToList();
                        }
                        else
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                                AppendLog("[시스템] 현재 세션의 비행 기록을 분석 중입니다..."));

                            logs = db.FlightLogs
                                     .Where(x => x.Timestamp >= _programStartTime)
                                     .OrderBy(x => x.Timestamp)
                                     .ToList();
                        }

                        if (logs.Count > 0)
                        {
                            var path = logs.Select(l => new Tuple<double, double>(l.Latitude, l.Longitude)).ToList();

                            _isHistoryVisible = true;
                            HistoryPathRequested?.Invoke(path);

                            Application.Current.Dispatcher.Invoke(() =>
                                AppendLog($"[성공] {logs.Count}개의 비행 궤적을 표시합니다."));
                        }
                        else
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                                AppendLog("[알림] 표시할 비행 기록이 없습니다."));
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DB 에러] {ex.Message}");
                AppendLog($"[에러] 데이터 변환 실패: {ex.Message}");
            }
        }
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
                    seconds = 10;

                _scanCts?.Cancel();
                _scanCts = new CancellationTokenSource();
                var token = _scanCts.Token;

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

                _mavlinkService.SetJoystick(0, 0, 500, 0);
                _mavlinkService.StopManualControl();

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
                _mavlinkService.SetJoystick(0, 0, 500, 0);
                _mavlinkService.StopManualControl();
                _isScanning = false;
            }
        }

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
                _mavlinkService.SetJoystick(0, 0, 500, 0);

                // 절대 호출 금지
                // ToggleManualControl(false);

                _isScanning = false;
                AppendLog("[시스템] 탐색을 중지합니다.");
            }
            catch (Exception ex)
            {
                AppendLog($"[오류] 탐색 중지 실패: {ex.Message}");
            }
        }
        #endregion
    }
}
