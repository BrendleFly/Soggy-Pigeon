using System.Windows.Input;

namespace SoggyPigeon.App.Infrastructure;

public sealed class Command : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public Command(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public void RaiseCanExecute() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    public event EventHandler? CanExecuteChanged;
}
