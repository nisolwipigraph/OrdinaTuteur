// Подключаем типы WinUI, которые нужны для создания окна приложения.
using Microsoft.UI.Xaml;

namespace OrdinaTuteur;

// Точка входа WinUI-приложения: создаёт и показывает главное окно.
public sealed partial class App : Application
{
    // Ссылка не даёт сборщику мусора удалить окно во время работы программы.
    private Window? _mainWindow;

    // Загружает ресурсы приложения, описанные в App.xaml.
    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Windows вызывает этот метод, когда приложение запускается.
        _mainWindow = new MainWindow();
        // Делаем созданное окно видимым.
        _mainWindow.Activate();
    }
}
