using System;
using System.ComponentModel;
using System.Windows;

namespace GCS_Project
{
    public partial class MainWindow
    {
        #region [창 이벤트]

        // 창 위치나 크기가 바뀌면 오버레이 위치와 박스를 다시 맞춘다.
        private void MainWindow_WindowChanged(object? sender, EventArgs e)
        {
            UpdateOverlayBounds();
            RedrawBboxes();
        }

        // 창 닫기 직전 오버레이 창을 먼저 닫는다.
        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            _overlayWindow?.Close();
            _overlayWindow = null;
        }

        #endregion


        #region [종료 처리]
        // 창이 닫힐 때 타이머, 오버레이, ViewModel, 영상 리소스를 정리한다.
        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _overlayTimer?.Stop();
                _overlayTimer = null;

                _overlayWindow?.Close();
                _overlayWindow = null;

                _viewModel?.Dispose();

                _mp1?.Stop();
                _mp1?.Dispose();

                _libVLC?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"종료 처리 중 오류: {ex.Message}");
            }

            UnsubscribeViewModelEvents();

            base.OnClosed(e);
        }

        // ViewModel의 타겟 변경 이벤트 구독을 해제한다.
        private void UnsubscribeViewModelEvents()
        {
            if (_viewModel == null) return;

            _viewModel.DetectedTargets.CollectionChanged -= DetectedTargets_CollectionChanged;

            foreach (var target in _viewModel.DetectedTargets)
            {
                target.PropertyChanged -= DetectedTarget_PropertyChanged;
            }
        }

        #endregion
    }
}