using Asv.Mavlink.Minimal;
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
                // void 메서드이므로 Task.Run으로 감싸 UI 스레드 멈춤 완벽 방지!
                await Task.Run(() => _mavlinkService.StartMission());
                FlightMode = MODE_MISSION;
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
                FlightMode = MODE_TAKEOFF;
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

        // 3. 이륙
        private async void ExecuteTakeoff()
        {
            try
            {
                await _mavlinkService.Takeoff((float)Drone.TargetAltitude);
                FlightMode = MODE_TAKEOFF;
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
                FlightMode = MODE_IDLE;

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

                FlightMode = MODE_TRACKING;
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
        }

        private async Task ExecuteScan(double seconds)
        {
            try
            {
                if (_isScanning)
                {
                    ChatLog += "\n[시스템] 이미 탐색 중입니다.";
                    return;
                }

                if (!Drone.IsArmed)
                {
                    ChatLog += "\n[시스템] 시동 후에만 탐색할 수 있습니다.";
                    return;
                }

                if (seconds <= 0)
                    seconds = 10;

                _scanCts?.Cancel();
                _scanCts = new CancellationTokenSource();
                var token = _scanCts.Token;

                _isScanning = true;
                ChatLog += $"\n[시스템] {seconds:F0}초 동안 회전 탐색을 시작합니다.";

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
                    ChatLog += "\n[시스템] 회전 탐색을 종료합니다.";
                }
            }
            catch (TaskCanceledException)
            {
                ChatLog += "\n[시스템] 회전 탐색이 중단되었습니다.";
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 회전 탐색 실패: {ex.Message}";
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
                    ChatLog += "\n[시스템] 현재 탐색 중이 아닙니다.";
                    return;
                }

                _scanCts?.Cancel();
                _mavlinkService.SetJoystick(0, 0, 500, 0);

                // 절대 호출 금지
                // ToggleManualControl(false);

                _isScanning = false;
                ChatLog += "\n[시스템] 탐색을 중지합니다.";
            }
            catch (Exception ex)
            {
                ChatLog += $"\n[오류] 탐색 중지 실패: {ex.Message}";
            }
        }
        #endregion
    }
}
