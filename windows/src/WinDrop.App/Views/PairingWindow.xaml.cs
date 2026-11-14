using System.Windows;
using WinDrop.App.Services;
using WinDrop.App.ViewModels;

namespace WinDrop.App.Views;

public partial class PairingWindow : Window
{
    private readonly PairingViewModel _vm;

    public PairingWindow(IDeviceService devices)
    {
        InitializeComponent();
        _vm = new PairingViewModel(devices);
        DataContext = _vm;
        _vm.CloseRequested += (_, _) => Close();
        Loaded += async (_, _) =>
        {
            try { await _vm.StartAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Infrastructure.AppLog.Write($"eşleştirme penceresi hatası: {ex}"); }
        };
        Closed += (_, _) => _vm.Dispose();
    }
}
