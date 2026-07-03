using System.Windows.Input;

namespace CopilotChatApp.ViewModels;

/// <summary>
/// Minimal ICommand implementation supporting async execution and re-entrancy guarding
/// (CanExecute returns false while the command is already running). Kept hand-rolled rather than
/// pulling in CommunityToolkit.Mvvm to avoid adding a new dependency for a single small ViewModel.
/// </summary>
public class RelayCommand : ICommand
{
    readonly Func<Task> _executeAsync;
    readonly Func<bool>? _canExecute;
    bool _isExecuting;

    public RelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _executeAsync();
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
