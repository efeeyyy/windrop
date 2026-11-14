using System.Windows;
using WinDrop.App.ViewModels;

namespace WinDrop.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // Pencere ekrana sığmazsa (çok cihaz, yüksek ölçek) içerik kaydırılır; Kaydet/Vazgeç altta sabit kalır.
        MaxHeight = SystemParameters.WorkArea.Height - 32;
        viewModel.CloseRequested += (_, _) => Close();
        Closed += (_, _) => viewModel.Dispose();
    }
}
