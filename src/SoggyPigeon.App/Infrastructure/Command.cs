using System.Windows.Input;

namespace SoggyPigeon.App.Infrastructure;

public sealed class Command : ICommand
{
    private readonly Action _execute;
    private readonly Action<object?>? _executeWithParam;
    private readonly Func<bool>? _canExecute;

    public Command(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>Parameterised form — for commands invoked from item templates
    /// (e.g. Join on a lobby room row), where the parameter is the row.</summary>
    public Command(Action<object?> execute, Func<bool>? canExecute = null)
    {
        _execute = () => { };
        _executeWithParam = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter)
    {
        if (_executeWithParam is { } withParam) withParam(parameter);
        else _execute();
    }
    public void RaiseCanExecute() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    public event EventHandler? CanExecuteChanged;
}
