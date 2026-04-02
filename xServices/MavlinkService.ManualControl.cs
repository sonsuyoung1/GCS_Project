using Asv.Mavlink.Common;
using Asv.Mavlink.Minimal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace GCS_Project.xServices
{
    public partial class MavlinkService
    {
        #region [수동 조종 (Virtual Joystick)]

        private short _joyX = 0;
        private short _joyY = 0;
        private short _joyZ = 500;
        private short _joyR = 0;

        private bool _isManualControlActive = false;
        private Task? _manualControlTask;

        public void SetJoystick(short pitch, short roll, short thrust, short yaw)
        {
            _joyX = pitch;
            _joyY = roll;
            _joyZ = thrust;
            _joyR = yaw;
        }

        public async Task SetManualControlModeAsync(bool enable)
        {
            if (enable)
            {
                // 1. 드론 모드 변경 (POSCTL/MANUAL)
                await SetFlightMode(MavModeFlag.MavModeFlagCustomModeEnabled, 3);
                // 2. 가상 조이스틱 패킷 스트리밍 시작
                StartManualControl();
                OnLogReceived?.Invoke("[시스템] 수동 조종 모드 활성화");
            }
            else
            {
                // 1. 조이스틱 중단
                StopManualControl();
                // 2. 드론을 다시 AUTO 모드로 복귀
                await SetFlightMode(MavModeFlag.MavModeFlagCustomModeEnabled, 4);
                OnLogReceived?.Invoke("[시스템] 수동 조종 모드 해제 (AUTO 복귀)");
            }
        }

        /// <summary>
        /// 1초에 20번씩 드론에게 가상 조이스틱 상태를 전송하는 엔진을 켭니다.
        /// </summary>
        public void StartManualControl()
        {
            if (_manualControlTask != null && !_manualControlTask.IsCompleted) return;

            _isManualControlActive = true;
            OnLogReceived?.Invoke("[시스템] 수동 조종(가상 조이스틱) 엔진 가동");

            _manualControlTask = Task.Run(async () =>
            {
                try
                {
                    while (_isManualControlActive && _cts != null && !_cts.IsCancellationRequested)
                    {
                        if (_drone != null && CurrentState.IsConnected && _router != null)
                        {
                            var pkt = new ManualControlPacket();
                            pkt.Payload.Target = 1;
                            pkt.Payload.X = _joyX;
                            pkt.Payload.Y = _joyY;
                            pkt.Payload.Z = _joyZ;
                            pkt.Payload.R = _joyR;
                            pkt.Payload.Buttons = 0;

                            await _router.Send(pkt, _cts.Token);
                        }

                        await Task.Delay(50, _cts?.Token ?? CancellationToken.None);
                    }
                }
                catch (TaskCanceledException)
                {
                    // 정상 종료
                }
                catch (Exception ex)
                {
                    OnLogReceived?.Invoke($"[오류] 수동 조종 송신 실패: {ex.Message}");
                }
                finally
                {
                    _manualControlTask = null;
                }
            });
        }

        public void StopManualControl()
        {
            _isManualControlActive = false;
            OnLogReceived?.Invoke("[시스템] 수동 조종 엔진 종료");
        }
        #endregion
    }
}