using GCS_Project.ViewModels;
using System.Windows;

namespace GCS_Project
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            
            var window = new MainWindow();
            /*var viewModel = new MainViewModel();
            // View와 ViewModel을 연결합니다.
            window.DataContext = viewModel;*/

            window.Show();
        }
    }
}