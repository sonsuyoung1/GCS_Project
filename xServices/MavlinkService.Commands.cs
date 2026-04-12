using Asv.IO;
using Asv.Mavlink;
using Asv.Mavlink.Common;
using Asv.Mavlink.Minimal;
using Asv.Cfg;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GCS_Project.xServices
{
    public partial class MavlinkService
    {
        #region [비행 명령 (Flight Commands)]
        /// <summary>
        /// 드론의 비행 모드(GUIDED, AUTO, RTL 등)를 변경합니다.
        /// </summary>
        public async Task SetFlightMode(MavModeFlag baseMode, uint customMode)
        {
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 모드를 변경할 수 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            try
            {
                await cmd.SendCommandLong(
                    MavCmd.MavCmdDoSetMode,
                    (float)baseMode,
                    customMode,
                    0f, 0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );

                // 서비스 레벨에서는 최소 로그만
                OnLogReceived?.Invoke($"[명령] 비행 모드 변경 요청 전송 (커스텀 코드: {customMode})");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 모드 변경 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 지정된 고도로 이륙 명령(Takeoff)을 전송합니다.
        /// </summary>
        public async Task Takeoff(float alt)
        {
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 이륙할 수 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            try
            {
                await SetFlightMode(MavModeFlag.MavModeFlagCustomModeEnabled, 4);

                await Task.Delay(200, _cts?.Token ?? CancellationToken.None);

                await cmd.SendCommandLong(
                    MavCmd.MavCmdNavTakeoff,
                    float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN,
                    alt,
                    _cts?.Token ?? CancellationToken.None
                );

                OnLogReceived?.Invoke($"[명령] 이륙 명령 전송 완료 (목표 고도: {alt}m)");
            }
            catch (TaskCanceledException)
            {
                OnLogReceived?.Invoke("[경고] 이륙 명령이 취소되었습니다.");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 이륙 명령 전송 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 현재 위치에서 즉시 착륙 명령(Land)을 전송합니다.
        /// </summary>
        public async Task Landing()
        {
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 착륙 명령을 내릴 수 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            try
            {
                await cmd.SendCommandLong(
                    MavCmd.MavCmdNavLand,
                    0f, 0f, 0f, 0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );

                OnLogReceived?.Invoke("[명령] 착륙 명령 전송 완료");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 착륙 명령 전송 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 기체를 이륙했던 위치(Home)로 되돌아오게 하는 복귀 명령(RTL)을 전송합니다.
        /// </summary>
        public async Task Rtl()
        {
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 복귀(RTL) 명령을 내릴 수 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            try
            {
                await cmd.SendCommandLong(
                    MavCmd.MavCmdNavReturnToLaunch,
                    0f, 0f, 0f, 0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );

                OnLogReceived?.Invoke("[명령] 복귀(RTL) 명령 전송 완료");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 복귀(RTL) 명령 전송 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 기체의 모터를 회전시키는 시동(Arm) 명령을 전송합니다.
        /// </summary>
        public async Task SendArmAsync()
        {
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 통신이 끊겨 시동(Arm)을 걸 수 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            try
            {
                await cmd.SendCommandLong(
                    MavCmd.MavCmdComponentArmDisarm,
                    1f, 0f, 0f, 0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );

                OnLogReceived?.Invoke("[명령] 시동(Arm) 명령 전송 완료");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 시동(Arm) 명령 전송 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 기체의 모터를 정지시키는 시동 끄기(Disarm) 명령을 전송합니다.
        /// </summary>
        public async Task SendDisarmAsync()
        {
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 통신이 끊겨 시동을 끌 수(Disarm) 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            try
            {
                await cmd.SendCommandLong(
                    MavCmd.MavCmdComponentArmDisarm,
                    0f, 0f, 0f, 0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );

                OnLogReceived?.Invoke("[명령] 시동 끄기(Disarm) 명령 전송 완료");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 시동 끄기(Disarm) 명령 전송 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 비행 중 실시간으로 기체의 목표 이동 속도를 변경합니다.
        /// </summary>
        public async Task ChangeTargetSpeed(float targetSpeed)
        {
            if (_drone == null || !CurrentState.IsConnected) return;

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            try
            {
                await cmd.SendCommandLong(
                    MavCmd.MavCmdDoChangeSpeed,
                    1f, targetSpeed, -1f, 0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );

                OnLogReceived?.Invoke($"[명령] 목표 속도 변경 요청 전송: {targetSpeed}m/s");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 속도 변경 명령 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 비행 중 실시간으로 목표 고도를 변경합니다. (제자리 고도 상승/하강)
        /// </summary>
        public async Task ChangeTargetAltitude(float targetAltitude)
        {
            if (_drone == null || !CurrentState.IsConnected) return;

            try
            {
                await GoTo(CurrentState.Latitude, CurrentState.Longitude, targetAltitude);
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 고도 변경 명령 실패: {ex.Message}");
            }
        }

        public async Task Hold()
        {
            // 1. 기체 연결 상태 확인
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 정지 명령(Hold)을 수행할 수 없습니다.");
                return;
            }

            // 2. 명령 전송 클라이언트 가져오기
            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            try
            {
                // MAV_CMD_DO_PAUSE_CONTINUE (193) 명령 사용
                // 첫 번째 파라미터가 0이면 '일시 정지(Hold)', 1이면 '계속(Continue)'을 의미합니다.
                await cmd.SendCommandInt(
                    MavCmd.MavCmdDoPauseContinue,
                    MavFrame.MavFrameGlobalRelativeAltInt,
                    false,
                    false,
                    0f,    // Param 1: 0 = Pause (기체 정지)
                    0f, 0f, 0f, 0, 0, 0f,
                    _cts?.Token ?? CancellationToken.None
                );

                OnLogReceived?.Invoke("[시스템] 기체 정지(Hold) 명령을 전송했습니다.");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] Hold 명령 전송 실패: {ex.Message}");
            }
        }
        #endregion
    }
}