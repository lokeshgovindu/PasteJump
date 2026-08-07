using System.Windows.Input;

namespace PasteJump.App.Services;

/// <summary>
/// Minimal <see cref="ICommand"/> over an <see cref="Action"/>.
/// <para>
/// Hand-rolled rather than pulled from a MVVM framework: the app needs exactly this and nothing else,
/// and it ships as a self-contained folder where every dependency is weight on disk.
/// </para>
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>
    /// Routed through <see cref="CommandManager"/> so WPF re-queries on the same cues it uses for
    /// built-in commands, rather than needing manual invalidation.
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();
}
