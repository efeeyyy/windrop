using System.ComponentModel;
using System.Windows;
using WinDrop.App.ViewModels;

namespace WinDrop.App.Views;

public partial class MainWindow : Window
{
    private readonly System.Windows.Media.Brush _dropBorder;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _dropBorder = DropZone.BorderBrush;   // XAML'deki tema uyumlu kenarlık; sürüklemeden sonra bu geri gelir
    }

    /// <summary>Gerçek çıkışta true: pencere kapanır; aksi halde tepsiye küçülür.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Pencere ilk kez tepsiye gizlendiğinde.</summary>
    public event EventHandler? HiddenToTray;

    private MainViewModel Vm => (MainViewModel)DataContext;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke(this, EventArgs.Empty);
        }
        base.OnClosing(e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.BorderBrush = ok ? SystemColors.HighlightBrush : _dropBorder;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) =>
        DropZone.BorderBrush = _dropBorder;

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = _dropBorder;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Vm.StageFiles(files);
        e.Handled = true;
    }
}
