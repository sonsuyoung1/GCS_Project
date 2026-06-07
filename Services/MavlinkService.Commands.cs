using Asv.IO;
using Asv.Mavlink;
using Asv.Mavlink.Common;
using Asv.Mavlink.Minimal;
using GCS_Project.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace GCS_Project.Services
{
    public partial class MavlinkService
    {
        #region [1. 공통 명령 처리]

        // 드론 연결 상태를 확인하고 MAVLink 명령 클라이언트를 반환한다.
        private ICommandClient? GetCommandClient(string warningMessage)
        {
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke(warningMessage);
                return null;
            }

            return _drone.GetMicroservice<ICommandClient>();
        }

        #endregion


        #region [2. 비행 모드 명령]

        // 드론의 PX4 비행 모드를 변경한다.
        public async Task SetFlightMode(
            MavModeFlag baseMode,
            uint customMainMode,
            uint customSubMode = 0)
        {
            var commandClient = GetCommandClient("[경고] 기체가 연결되지 않아 모드를 변경할 수 없습니다.");
            if (commandClient == null) return;

            try
            {
                uint customMode =
                    (customMainMode << 16) |
                    (customSubMode << 24);

                await commandClient.SendCommandLong(
                    MavCmd.MavCmdDoSetMode,
                    (float)baseMode,   // param1: base mode
                    customMode,        // param2: PX4 packed custom mode
                    0f,
                    0f,
                    0f,
                    0f,
                    0f,
                    _cts?.Token ?? CancellationToken.None
                );

                OnLogReceived?.Invoke(
                    $"[명령] 비행 모드 변경 요청 전송 " +
                    $"(main={customMainMode}, sub={customSubMode}, customMode={customMode})"
                );
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 모드 변경 실패: {ex.Message}");
            }
        }

        // PX4 HOLD 모드로 전환한다.
        // PX4 HOLD / AUTO.LOITER 모드로 전환한다.
        public async Task SetHoldModeAsync()
        {
            if (_router == null)
            {
                OnLogReceived?.Invoke("[경고] MAVLink 라우터가 없어 HOLD 모드로 변경할 수 없습니다.");
                return;
            }

            try
            {
                // PX4 custom mode:
                // main mode 4 = AUTO
                // sub mode 3 = LOITER/HOLD
                uint customMode = (4u << 16) | (3u << 24);

                var pkt = new SetModePacket
                {
                    SystemId = 255,
                    ComponentId = 190
                };

                pkt.Payload.TargetSystem = 1;
                pkt.Payload.BaseMode = (Asv.Mavlink.Common.MavMode)1;
                pkt.Payload.CustomMode = customMode;

                await _router.Send(
                    pkt,
                    _cts?.Token ?? CancellationToken.None
                );

                OnLogReceived?.Invoke(
                    $"[PX4] SET_MODE HOLD/LOITER 전송 완료 (customMode={customMode})"
                );
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] HOLD/LOITER 모드 전환 실패: {ex.Message}");
            }
        }


        // 기체를 현재 위치에서 일시 정지 또는 제자리 유지 상태로 전환한다.
        public async Task Hold()
        {
            var commandClient = GetCommandClient("[경고] 기체가 연결되지 않아 정지 명령(Hold)을 수행할 수 없습니다.");
            if (commandClient == null) return;

            try
            {
                /*await commandClient.SendCommandInt(
                    MavCmd.MavCmdDoPauseContinue,
                    MavFrame.MavFrameGlobalRelativeAltInt,
                    false,
                    false,
                    0f,
                    0f, 0f, 0f,
                    0, 0,
                    0f,
                    _cts?.Token ?? CancellationToken.None
                );*/

                // 위에 거 안되면 이걸로 실행해봐야됨
                await commandClient.SendCommandLong(
                    MavCmd.MavCmdDoPauseContinue,
                    0f,     // param1: 0 = pause, 1 = continue
                    0f,
                    0f,
                    0f,
                    0f,
                    0f,
                    0f,
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


        #region [3. 기본 비행 명령]

        // 지정된 목표 고도로 이륙 명령을 전송한다.
        public async Task Takeoff(float alt)
        {
            var commandClient = GetCommandClient("[경고] 기체가 연결되지 않아 이륙할 수 없습니다.");
            if (commandClient == null) return;

            try
            {
                await SetFlightMode(MavModeFlag.MavModeFlagCustomModeEnabled, 4);
                await Task.Delay(200, _cts?.Token ?? CancellationToken.None);

                await commandClient.SendCommandLong(
                    MavCmd.MavCmdNavTakeoff,
                    float.NaN, float.NaN, float.NaN,
                    float.NaN, float.NaN, float.NaN,
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

        // 현재 위치에서 착륙 명령을 전송한다.
        public async Task Landing()
        {
            var commandClient = GetCommandClient("[경고] 기체가 연결되지 않아 착륙 명령을 내릴 수 없습니다.");
            if (commandClient == null) return;

            try
            {
                await commandClient.SendCommandLong(
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

        // 기체를 이륙 지점 또는 Home 위치로 복귀시킨다.
        public async Task Rtl()
        {
            var commandClient = GetCommandClient("[경고] 기체가 연결되지 않아 복귀(RTL) 명령을 내릴 수 없습니다.");
            if (commandClient == null) return;

            try
            {
                await commandClient.SendCommandLong(
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

        #endregion


        #region [4. Arm / Disarm 명령]

        // 기체의 모터를 활성화하는 Arm 명령을 전송한다.
        public async Task SendArmAsync()
        {
            var commandClient = GetCommandClient("[경고] 통신이 끊겨 시동(Arm)을 걸 수 없습니다.");
            if (commandClient == null) return;

            try
            {
                await commandClient.SendCommandLong(
                    MavCmd.MavCmdComponentArmDisarm,
                    1f,
                    0f, 0f, 0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );

                OnLogReceived?.Invoke("[명령] 시동(Arm) 명령 전송 완료");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 시동(Arm) 명령 전송 실패: {ex.Message}");
            }
        }

        // 기체의 모터를 비활성화하는 Disarm 명령을 전송한다.
        public async Task SendDisarmAsync()
        {
            var commandClient = GetCommandClient("[경고] 통신이 끊겨 시동을 끌 수(Disarm) 없습니다.");
            if (commandClient == null) return;

            try
            {
                await commandClient.SendCommandLong(
                    MavCmd.MavCmdComponentArmDisarm,
                    0f,
                    0f, 0f, 0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );

                OnLogReceived?.Invoke("[명령] 시동 끄기(Disarm) 명령 전송 완료");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 시동 끄기(Disarm) 명령 전송 실패: {ex.Message}");
            }
        }

        #endregion


        #region [5. 목표값 변경 명령]

        // 비행 중 기체의 목표 이동 속도를 변경한다.
        public async Task ChangeTargetSpeed(float targetSpeed)
        {
            var commandClient = GetCommandClient("[경고] 기체가 연결되지 않아 속도를 변경할 수 없습니다.");
            if (commandClient == null) return;

            try
            {
                await commandClient.SendCommandLong(
                    MavCmd.MavCmdDoChangeSpeed,
                    1f,
                    targetSpeed,
                    -1f,
                    0f, 0f, 0f, 0f,
                    _cts?.Token ?? CancellationToken.None
                );

                OnLogReceived?.Invoke($"[명령] 목표 속도 변경 요청 전송: {targetSpeed}m/s");
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 속도 변경 명령 실패: {ex.Message}");
            }
        }

        // 현재 위치를 유지한 채 목표 고도만 변경한다.
        public async Task ChangeTargetAltitude(float targetAltitude)
        {
            if (_drone == null || !CurrentState.IsConnected)
            {
                OnLogReceived?.Invoke("[경고] 기체가 연결되지 않아 고도를 변경할 수 없습니다.");
                return;
            }

            try
            {
                await GoTo(
                    CurrentState.Latitude,
                    CurrentState.Longitude,
                    targetAltitude
                );
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke($"[오류] 고도 변경 명령 실패: {ex.Message}");
            }
        }

        #endregion
    }
}