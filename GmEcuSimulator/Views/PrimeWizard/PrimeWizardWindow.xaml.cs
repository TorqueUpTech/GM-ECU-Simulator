using System.Windows;
using Core.Bus;
using Core.Dps;
using Core.Ecu;
using GmEcuSimulator.ViewModels.PrimeWizard;

namespace GmEcuSimulator.Views.PrimeWizard;

// Host window for the four-page DPS prime wizard. Two constructors:
//   - new() for a fresh prime triggered from File -> Prime from DPS archive...
//   - new(existingNode, priorContext) to re-edit an already-primed ECU. The
//     wizard opens with the prior selections pre-filled and replaces the
//     existing node on Apply.
public partial class PrimeWizardWindow : Window
{
    private readonly PrimeWizardViewModel vm;

    public PrimeWizardWindow(VirtualBus bus)
        : this(bus, existingNode: null, priorContext: null) { }

    public PrimeWizardWindow(VirtualBus bus, EcuNode? existingNode, PrimeWizardContext? priorContext)
    {
        InitializeComponent();
        vm = new PrimeWizardViewModel(bus, existingNode, priorContext);
        vm.RequestClose += () => Dispatcher.BeginInvoke(new Action(Close));
        DataContext = vm;
    }

    // Result handed back to the caller after a successful Apply. Null when
    // the user cancelled or closed without applying.
    public EcuNode? CommittedNode => vm.CommittedNode;
    public PrimedDataset? CommittedDataset => vm.CommittedDataset;
    public PrimeWizardContext Context => vm.Context;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
