using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MobileEssControl.Views.AutoCharge;

public enum ChargeFlowState
{
    Ready,
    Starting,
    Charging,
    Stopping,
    Stopped,
    Completed,
    Fault,
    Disconnected
}
public partial class AutoChargeView : UserControl
{
    public AutoChargeView()
    {
        InitializeComponent();
    }
}