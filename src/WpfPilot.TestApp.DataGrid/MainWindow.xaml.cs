using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace WpfPilot.TestApp.DataGrid;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var viewModel = new DataGridViewModel();
        DataContext = viewModel;

        Loaded += (_, _) =>
        {
            PeopleGrid.SelectedIndex = -1;
            viewModel.SelectedPerson = null;
        };
    }
}

internal sealed class DataGridViewModel : INotifyPropertyChanged
{
    private PersonRow? _selectedPerson;
    private string _selectedStatus = "Selected: (none)";

    public DataGridViewModel()
    {
        People =
        [
            new PersonRow("DataGrid_Row_0", "Alice", 30),
            new PersonRow("DataGrid_Row_1", "Bob", 41),
            new PersonRow("DataGrid_Row_2", "Charlie", 27),
            new PersonRow("DataGrid_Row_3", "Dana", 22),
        ];
    }

    public ObservableCollection<PersonRow> People { get; }

    public PersonRow? SelectedPerson
    {
        get => _selectedPerson;
        set
        {
            if (ReferenceEquals(_selectedPerson, value))
            {
                return;
            }

            if (_selectedPerson is not null)
            {
                _selectedPerson.PropertyChanged -= SelectedPerson_PropertyChanged;
            }

            _selectedPerson = value;

            if (_selectedPerson is not null)
            {
                _selectedPerson.PropertyChanged += SelectedPerson_PropertyChanged;
            }

            UpdateSelectedStatus();
            OnPropertyChanged();
        }
    }

    public string SelectedStatus
    {
        get => _selectedStatus;
        private set
        {
            if (string.Equals(_selectedStatus, value, StringComparison.Ordinal))
            {
                return;
            }

            _selectedStatus = value;
            OnPropertyChanged();
        }
    }

    private void SelectedPerson_PropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateSelectedStatus();

    private void UpdateSelectedStatus()
    {
        var selected = SelectedPerson;
        SelectedStatus = selected is null
            ? "Selected: (none)"
            : $"Selected: {selected.Name} (Age={selected.Age})";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class PersonRow : INotifyPropertyChanged
{
    private string _name;
    private int _age;

    public PersonRow(string automationId, string name, int age)
    {
        AutomationId = automationId;
        _name = name;
        _age = age;
    }

    public string AutomationId { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (string.Equals(_name, value, StringComparison.Ordinal))
            {
                return;
            }

            _name = value;
            OnPropertyChanged();
        }
    }

    public int Age
    {
        get => _age;
        set
        {
            if (_age == value)
            {
                return;
            }

            _age = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

