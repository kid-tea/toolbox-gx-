using System.Windows.Controls;
using Toolbox.ViewModels;

namespace Toolbox.Views;

public partial class AgentInspectorView : UserControl
{
    public AgentInspectorView(AgentInspectorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
