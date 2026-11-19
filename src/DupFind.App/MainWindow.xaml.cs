using System.Windows;
using DupFind.App.ViewModels;

namespace DupFind.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
