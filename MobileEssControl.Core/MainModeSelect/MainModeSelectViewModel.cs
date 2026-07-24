using System;
using CommunityToolkit.Mvvm.Input;

namespace MobileEssControl.ViewModels.MainModeSelect;

public partial class MainModeSelectViewModel : ViewModelBase
{
    private readonly Action _showAutoCharge;
    private readonly Action _showExternalOutput;
    private readonly Action _showGridDischarge;


    public MainModeSelectViewModel(
        Action showAutoCharge,
        Action showExternalOutput,
        Action showGridDischarge)
    {
        _showAutoCharge = showAutoCharge;
        _showExternalOutput = showExternalOutput;
        _showGridDischarge = showGridDischarge;
    }

    [RelayCommand]
    private void ShowAutoCharge()
    {
        _showAutoCharge();
    }

    [RelayCommand]
    private void ShowExternalOutput()
    {
        _showExternalOutput();
    }

    [RelayCommand]
    private void ShowGridDischarge()
    {
        _showGridDischarge();
    }


}