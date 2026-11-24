using Microsoft.UI.Xaml;

namespace OrdinaTuteur;

// С этого класса Windows запускает приложение.
public sealed partial class App : Application
{
    // Поле держит окно живым. Без ссылки сборщик мусора может закрыть его
    // сразу после OnLaunched, потому что локальная переменная уже исчезла.
    private Window? _mainWindow;

    // Загружает ресурсы из App.xaml: стили и цвета WinUI.
    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        // Activate показывает окно и передаёт ему фокус клавиатуры.
        _mainWindow.Activate();
    }
}
