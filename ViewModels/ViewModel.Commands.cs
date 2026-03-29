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
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 시동 끄기 명령 실패: {ex.Message}";
            }
        }

        // 3. 이륙
        private async void ExecuteTakeoff()
        {
            try
            {
                await _mavlinkService.Takeoff((float)Drone.TargetAltitude);
                ChatLog += $"\n[명령] 이륙 명령 전송 (목표 고도: {Drone.TargetAltitude}m)";

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

                await Task.Run(() => _mavlinkService.ClearWaypoints());
                AllClearRequested?.Invoke();

                ChatLog += "\n[명령] 착륙 명령 전송 및 목적지 초기화";
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 착륙 명령 실패: {ex.Message}";
            }
        }

        // 5. 복귀 (RTL)
        private async void ExecuteRtl()
        {
            try
            {
                await _mavlinkService.Rtl();
                ChatLog += "\n[명령] 복귀(RTL) 명령 전송.";

                await Task.Run(() => _mavlinkService.ClearWaypoints());
                AllClearRequested?.Invoke();
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 복귀 명령 실패: {ex.Message}";
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
                ChatLog += $"\n[오류] 목적지 취소 실패: {ex.Message}";
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
                ChatLog += $"\n[오류] 목적지 삭제 실패: {ex.Message}";
            }
        }

        // 8. 지도에서 웨이포인트 추가
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

        // 9. 객체 추적 실행
        private async void ExecuteFollow()
        {
            try
            {
                if (SelectedTarget == null) return;

                await Task.Run(() => _mavlinkService.StopMission());
                await _mavlinkService.GoTo(SelectedTarget.Lat, SelectedTarget.Lng, (float)Drone.TargetAltitude);
                ChatLog += $"\n[시스템] 타겟 추적 모드: ID {SelectedTarget.ID}";
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 타겟 추적 실패: {ex.Message}";
            }
        }

        /*// 10. 객체 추적 중단
        private async void ExecuteStopTracking()
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

        private async Task ExecuteShowHistory()
        {
            // 1. 이미 궤적이 보이고 있다면? -> 지우기!
            if (_isHistoryVisible)
            {
                _isHistoryVisible = false;
                HistoryPathRequested?.Invoke(new List<Tuple<double, double>>());
                Application.Current.Dispatcher.Invoke(() => ChatLog += "\n[시스템] 비행 궤적을 숨겼습니다.");
                return;
            }

            try
            {
                await Task.Run(() =>
                {
                    using (var db = new GCS_Project.Data.AppDbContext())
                    {
                        // Var 대신 명시적으로 List 타입을 선언해 둡니다.
                        // (if 문 안에서 값을 넣기 위함)
                        List<FlightLog> logs;

                        // [핵심 조건 분기] 달력에 날짜가 선택되어 있는지 확인!
                        if (SelectedHistoryDate.HasValue)
                        {
                            // [경우 A] 달력에서 특정 날짜를 고른 경우
                            DateTime targetDate = SelectedHistoryDate.Value.Date;
                            DateTime nextDate = targetDate.AddDays(1);

                            Application.Current.Dispatcher.Invoke(() =>
                                ChatLog += $"\n[시스템] {targetDate:yyyy-MM-dd}의 비행 기록을 분석 중입니다...");

                            // 선택한 날짜 하루치 데이터만 가져오기
                            logs = db.FlightLogs
                                     .Where(x => x.Timestamp >= targetDate && x.Timestamp < nextDate)
                                     .OrderBy(x => x.Timestamp)
                                     .ToList();
                        }
                        else
                        {
                            // [경우 B] 달력을 비워둔 경우 (현재 프로그램 실행 세션)
                            Application.Current.Dispatcher.Invoke(() =>
                                ChatLog += "\n[시스템] 현재 세션의 비행 기록을 분석 중입니다...");

                            // 프로그램 켠 시간 이후의 데이터만 가져오기
                            logs = db.FlightLogs
                                     .Where(x => x.Timestamp >= _programStartTime)
                                     .OrderBy(x => x.Timestamp)
                                     .ToList();
                        }

                        // 2. 결과 처리 및 지도에 그리기
                        if (logs.Count > 0)
                        {
                            var path = logs.Select(l => new Tuple<double, double>(l.Latitude, l.Longitude)).ToList();

                            // 상태를 true로 변경
                            _isHistoryVisible = true;

                            HistoryPathRequested?.Invoke(path);

                            Application.Current.Dispatcher.Invoke(() =>
                                ChatLog += $"\n[성공] {logs.Count}개의 비행 궤적을 표시합니다.");
                        }
                        else
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                                ChatLog += "\n[알림] 표시할 비행 기록이 없습니다.");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DB 에러] {ex.Message}");
                Application.Current.Dispatcher.Invoke(() => ChatLog += $"\n[에러] 데이터 변환 실패: {ex.Message}");
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
            (FollowCommand as RelayCommand)?.NotifyCanExecuteChanged();

            Application.Current.Dispatcher.Invoke(() =>
            {
                CommandManager.InvalidateRequerySuggested();
            });
        }

        #endregion
    }
}
