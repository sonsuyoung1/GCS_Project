using System.Windows;
using System.Windows.Input;

namespace GCS_Project
{
    public partial class MainWindow
    {
        #region [수동 조종 입력]

        // 수동 조종 모드를 켠다.
        private void ManualModeToggle_Checked(object sender, RoutedEventArgs e)
        {
            _viewModel?.ToggleManualControl(true);
        }

        // 수동 조종 모드를 끈다.
        private void ManualModeToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            _viewModel?.ToggleManualControl(false);
        }

        // 키가 눌렸을 때 조이스틱 값을 변경한다.
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.IsRepeat) return;

            bool isChanged = ApplyJoystickKeyDown(e.Key);

            if (isChanged)
            {
                SendJoystickState();
            }
        }

        // 키에서 손을 뗐을 때 해당 조이스틱 값을 중립으로 되돌린다.
        private void Window_KeyUp(object sender, KeyEventArgs e)
        {
            bool isChanged = ApplyJoystickKeyUp(e.Key);

            if (isChanged)
            {
                SendJoystickState();
            }
        }

        // 눌린 키에 따라 조이스틱 값을 변경한다.
        private bool ApplyJoystickKeyDown(Key key)
        {
            switch (key)
            {
                case Key.W:
                    _joyPitch = 1000;
                    return true;

                case Key.S:
                    _joyPitch = -1000;
                    return true;

                case Key.A:
                    _joyRoll = -1000;
                    return true;

                case Key.D:
                    _joyRoll = 1000;
                    return true;

                case Key.Up:
                    _joyThrust = 1000;
                    return true;

                case Key.Down:
                    _joyThrust = 0;
                    return true;

                case Key.Left:
                    _joyYaw = -1000;
                    return true;

                case Key.Right:
                    _joyYaw = 1000;
                    return true;

                default:
                    return false;
            }
        }

        // 떼어진 키에 해당하는 조이스틱 값을 중립 상태로 되돌린다.
        private bool ApplyJoystickKeyUp(Key key)
        {
            switch (key)
            {
                case Key.W:
                case Key.S:
                    _joyPitch = 0;
                    return true;

                case Key.A:
                case Key.D:
                    _joyRoll = 0;
                    return true;

                case Key.Up:
                case Key.Down:
                    _joyThrust = 500;
                    return true;

                case Key.Left:
                case Key.Right:
                    _joyYaw = 0;
                    return true;

                default:
                    return false;
            }
        }

        // 현재 조이스틱 값을 ViewModel로 전달한다.
        private void SendJoystickState()
        {
            _viewModel?.UpdateJoystick(_joyPitch, _joyRoll, _joyThrust, _joyYaw);
        }

        #endregion
    }
}