using CommunityToolkit.Mvvm.ComponentModel;

namespace Pipework.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _greeting = "Pipework";
}
