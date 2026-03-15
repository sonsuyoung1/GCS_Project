using Asv.Mavlink.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GCS_Project.xServices
{
    public partial class MavlinkService
    {
        #region [수동 조종 (Virtual Joystick)]

        // 조이스틱의 4가지 축 값 (기본값 세팅)
        // X, Y, R은 -1000 ~ 1000 / Z(스로틀)는 0 ~ 1000 (500이 제자리 호버링)
        private short _joyX = 0;   // 앞/뒤 (Pitch)
        private short _joyY = 0;   // 좌/우 (Roll)
        private short _joyZ = 500; // 상승/하강 (Thrust)
        private short _joyR = 0;   // 좌우 회전 (Yaw)

        private bool _isManualControlActive = false;
        private Task? _manualControlTask;

        /// <summary>
        /// 키보드나 패드에서 입력받은 조이스틱 값을 업데이트합니다.
        /// </summary>
        public void SetJoystick(short pitch, short roll, short thrust, short yaw)
        {
            _joyX = pitch;
            _joyY = roll;
            _joyZ = thrust;
            _joyR = yaw;
        }

        /// <summary>
        /// 1초에 20번씩 드론에게 가상 조이스틱 상태를 전송하는 엔진을 켭니다.
        /// </summary>
        public void StartManualControl()
        {
            if (_manualControlTask != null && !_manualControlTask.IsCompleted) return;

            _isManualControlActive = true;
            OnLogReceived?.Invoke("[시스템] 수동 조종(가상 조이스틱) 엔진 가동!");

            _manualControlTask = Task.Run(async () =>
            {
                while (_isManualControlActive && _cts != null && !_cts.IsCancellationRequested)
                {
                    if (_drone != null && CurrentState.IsConnected && _router != null)
                    {
                        try
                        {
                            // 수동 조종 패킷 조립
                            var pkt = new ManualControlPacket();
                            pkt.Payload.Target = 1; // 보통 1번 드론
                            pkt.Payload.X = _joyX;
                            pkt.Payload.Y = _joyY;
                            pkt.Payload.Z = _joyZ;
                            pkt.Payload.R = _joyR;
                            pkt.Payload.Buttons = 0;

                            // 전송! (1초에 20번 쏘기 때문에 로그는 남기지 않습니다)
                            await _router.Send(pkt, _cts.Token);
                        }
                        catch { /* 통신 튐 무시 */ }
                    }
                    // 50ms 대기 (1초에 약 20번 전송 = 20Hz)
                    await Task.Delay(50);
                }
            });
        }

        /// <summary>
        /// 수동 조종 엔진을 끕니다.
        /// </summary>
        public void StopManualControl()
        {
            _isManualControlActive = false;
            _manualControlTask = null;
            OnLogReceived?.Invoke("[시스템] 수동 조종 엔진 종료.");
        }
        #endregion
    }
}
