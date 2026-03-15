using Asv.IO;
using Asv.Mavlink;
using Asv.Mavlink.Minimal;
using Asv.Mavlink.Common;
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
                // 최신 PX4가 좋아하는 CommandLong 방식으로 모드 변경!
                await cmd.SendCommandLong(
                    MavCmd.MavCmdDoSetMode,
                    (float)baseMode,       // Param 1: 기본 모드 (보통 CustomModeEnabled = 1)
                    customMode,            // Param 2: 커스텀 모드 번호 (PX4의 AUTO = 4)
                    0f, 0f, 0f, 0f, 0f,    // 나머지 파라미터는 0
                    _cts?.Token ?? CancellationToken.None
                );

                OnLogReceived?.Invoke($"[명령] 비행 모드 변경 요청 전송 (커스텀 코드: {customMode})");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 모드 변경 중 예외 발생: {ex.Message}");
            }
        }

        /// <summary>
        /// 지정된 고도로 이륙 명령(Takeoff)을 전송합니다.
        /// </summary>
        public async Task Takeoff(float alt)
        {
            // 1. 안전장치: 드론 객체와 통신 상태(IsConnected) 꼼꼼하게 확인
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 이륙할 수 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            // 2. 명령 전송 및 에러 방어막
            try
            {
                OnLogReceived?.Invoke($"[명령] 이륙 시도 (목표 고도: {alt}m)...");

                await SetFlightMode(MavModeFlag.MavModeFlagCustomModeEnabled, 4);

                // 드론이 모드를 바꿀 수 있게 잠깐 숨 돌릴 시간(0.2초)을 줍니다.
                await Task.Delay(200);

                // 실제 이륙 명령 전송 (빈칸은 반드시 NaN으로 채움!)
                // param7 = altitude, 나머지 1~6은 NaN(미사용) 처리
                await cmd.SendCommandLong(
                    MavCmd.MavCmdNavTakeoff,
                    float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN,
                    alt,
                    _cts?.Token ?? CancellationToken.None
                );

                OnLogReceived?.Invoke($"[확인] 이륙(Takeoff) 명령이 기체로 전송되었습니다.");
            }
            catch (Exception ex)
            {
                // 통신 지연이나 에러 발생 시 프로그램이 안 튕기도록 조용히 로그만 출력
                OnLogReceived?.Invoke($"[오류] 이륙 명령 전송 실패: {ex.Message}");
            }
        }


        /// <summary>
        /// 현재 위치에서 즉시 착륙 명령(Land)을 전송합니다.
        /// </summary>
        public async Task Landing()
        {
            // 1. 안전장치
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 착륙 명령을 내릴 수 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            // 2. 명령 전송 및 에러 방어막
            try
            {
                OnLogReceived?.Invoke(" [명령] 착륙 시도...");

                await cmd.SendCommandLong(
                    MavCmd.MavCmdNavLand,
                    0f, 0f, 0f, 0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );
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
            // 1. 안전장치
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 복귀(RTL) 명령을 내릴 수 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            // 2. 명령 전송 및 에러 방어막
            try
            {
                OnLogReceived?.Invoke("[명령] 드론 복귀(RTL) 시도 중...");

                await cmd.SendCommandLong(
                    MavCmd.MavCmdNavReturnToLaunch,
                    0f, 0f, 0f, 0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );
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
            // 1. 방탄 안전장치 (통일)
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 통신이 끊겨 시동(Arm)을 걸 수 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            // 2. 에러 방어막 및 전송
            try
            {
                OnLogReceived?.Invoke("[명령] 기체 시동(Arm) 전송 중...");

                // 파라미터 1번 = 1 (Arm)
                await cmd.SendCommandLong(
                    MavCmd.MavCmdComponentArmDisarm,
                    1f, 0f, 0f, 0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );
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
            // 1. Disarm에도 IsConnected 안전장치 추가!
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 통신이 끊겨 시동을 끌 수(Disarm) 없습니다.");
                return;
            }

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            // 2. 에러 방어막 및 전송
            try
            {
                OnLogReceived?.Invoke("[명령] 기체 시동 끄기(Disarm) 전송 중...");

                // 파라미터 1번 = 0 (Disarm)
                await cmd.SendCommandLong(
                    MavCmd.MavCmdComponentArmDisarm,
                    0f, 0f, 0f, 0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );
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
            // 안전장치 확인
            if (_drone == null || !CurrentState.IsConnected) return;

            var cmd = _drone.GetMicroservice<ICommandClient>();
            if (cmd == null) return;

            try
            {
                // MavCmdDoChangeSpeed 전송
                // Param1: 1 (Ground Speed, 멀티로터용)
                // Param2: 목표 속도 (m/s)
                // Param3: Throttle (-1은 변경 안 함 유지)
                await cmd.SendCommandLong(
                    MavCmd.MavCmdDoChangeSpeed,
                    1f, targetSpeed, -1f, 0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );

                OnLogReceived?.Invoke($"[명령] 목표 속도를 {targetSpeed}m/s로 변경했습니다.");
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
            // 안전장치 확인
            if (_drone == null || !CurrentState.IsConnected) return;

            try
            {
                await GoTo(CurrentState.Latitude, CurrentState.Longitude, targetAltitude);

                // (GoTo 내부에 이미 로그가 출력되므로, 여기서는 별도의 로그를 찍지 않아도 깔끔합니다)
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 고도 변경 명령 실패: {ex.Message}");
            }
        }
        #endregion
    }
}
