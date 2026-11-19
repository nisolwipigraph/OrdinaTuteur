// Подключаем работу с процессами, текстом, JSON и элементами WinUI.
using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace OrdinaTuteur;

// Это код главного окна: он реагирует на кнопки и связывает экран с Python.
public sealed partial class MainWindow : Window
{
    // Python и C# обмениваются UTF-8 текстом; false не добавляет BOM в начало.
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    // В этих настройках JSON использует привычные для API имена свойств.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Пока выполняется запрос, блокируем отправку других сообщений.
    private bool _busy;

    public MainWindow()
    {
        // Создаёт элементы окна, описанные в MainWindow.xaml.
        InitializeComponent();
    }

    // Хранит части одной карточки, чтобы позже менять её текст и статус.
    private sealed class ChatMessage(TextBlock text, StackPanel body, Border bubble)
    {
        // Сохраняем ссылки на элементы, созданные при показе сообщения.
        public TextBlock Text { get; } = text;
        public StackPanel Body { get; } = body;
        public Border Bubble { get; } = bubble;
        public TextBlock? Status { get; set; }
    }

    // Одно сообщение в истории, которую Python отправляет ИИ.
    private sealed class GuideMemoryMessage(string role, string content)
    {
        // role обычно равен user или assistant, content содержит текст.
        public string Role { get; } = role;
        public string Content { get; } = content;
    }

    // Один ответ ИИ: инструкция и признак завершённой задачи.
    private sealed class GuideStepResponse(bool done, string instruction)
    {
        public bool Done { get; } = done;
        public string Instruction { get; } = instruction;
    }

    // Описывает одно JSON-событие, которое Python выводит в консоль.
    private sealed class BrainEvent
    {
        // Type отличает текстовый фрагмент, готовый ответ и снимок экрана.
        public string Type { get; set; } = "";
        public string? Text { get; set; }
        public string? Image { get; set; }
        public bool Done { get; set; }
        public string? Instruction { get; set; }
    }

    // Память одной задачи. Новое сообщение создаёт новую такую сессию.
    private sealed class GuideSession(string question)
    {
        // Сохраняем исходную задачу, чтобы передавать её ИИ на каждом шаге.
        public string Question { get; } = question;
        // Сюда попадают шаги, которые пользователь подтвердил кнопкой «Дальше».
        public List<string> CompletedSteps { get; } = [];
        // Здесь хранится короткая переписка внутри текущей задачи.
        public List<GuideMemoryMessage> History { get; } = [];
    }

    // Данные одного запроса: нужны для показа и для повторной проверки ответа.
    private sealed class StepRequest(
        IReadOnlyList<string> completedSteps,
        IReadOnlyList<GuideMemoryMessage> history,
        string image,
        int number)
    {
        // Запоминаем состояние на момент запроса, чтобы повтор использовал тот же снимок.
        public IReadOnlyList<string> CompletedSteps { get; } = completedSteps;
        public IReadOnlyList<GuideMemoryMessage> History { get; } = history;
        public string Image { get; } = image;
        public int Number { get; } = number;
    }

    // Создаёт карточку чата и добавляет её в историю на экране.
    private ChatMessage AddMessage(string text, bool user)
    {
        // StackPanel размещает подпись, текст сообщения и кнопки сверху вниз.
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock
        {
            Text = user ? "Ты" : "OrdinaTuteur",
            FontSize = 12,
            Opacity = 0.75
        });

        // TextBlock показывает содержимое сообщения и позволяет его выделить.
        var content = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16,
            IsTextSelectionEnabled = true
        };
        body.Children.Add(content);

        // Border рисует фон и скруглённые края карточки.
        var bubble = new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                user ? "AccentFillColorDefaultBrush" : "CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 10, 14, 10),
            MaxWidth = 680,
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = body
        };
        ChatMessages.Children.Add(bubble);
        // После добавления прокручиваем список, чтобы было видно новое сообщение.
        ScrollToLatest();
        return new ChatMessage(content, body, bubble);
    }

    // Меняет текст сообщения; при необходимости прокручивает чат вниз.
    private void UpdateText(TextBlock text, string value, bool scrollToLatest = true)
    {
        text.Text = value;
        if (scrollToLatest)
            ScrollToLatest();
    }

    // Добавляет подпись с ошибкой или убирает её, если ошибки больше нет.
    private void SetStatus(ChatMessage message, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            // Удаляем старую подпись из окна и очищаем сохранённую ссылку.
            if (message.Status is not null)
                message.Body.Children.Remove(message.Status);
            message.Status = null;
            return;
        }

        // Создаём подпись только один раз; далее меняем её текст.
        message.Status ??= new TextBlock
        {
            FontSize = 13,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap
        };
        if (!message.Body.Children.Contains(message.Status))
            message.Body.Children.Add(message.Status);
        message.Status.Text = value;
    }

    // Пересчитывает размер содержимого и прокручивает чат к последней карточке.
    private void ScrollToLatest()
    {
        ChatMessages.UpdateLayout();
        ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null);
    }

    // Обрабатывает Enter так же, как нажатие кнопки «Отправить».
    private void PromptInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        // Не даём Enter перейти на новую строку в поле ввода.
        e.Handled = true;
        SendButton_Click(sender, new RoutedEventArgs());
    }

    // Проверяет введённый текст и начинает новую сессию задачи.
    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        // Trim убирает пробелы по краям сообщения.
        string question = PromptInput.Text.Trim();
        if (_busy || question.Length == 0)
            return;

        // Показываем отправленный текст и просим ИИ подготовить первый шаг.
        AddMessage(question, true);
        if (await RequestNextStepAsync(new GuideSession(question)))
            PromptInput.Text = "";
    }

    // Добавляет к ответу кнопки «Дальше» и «Переделать ответ».
    private void AddStepButtons(
        ChatMessage message,
        GuideSession session,
        StepRequest request,
        string instruction,
        bool done)
    {
        // Если задача уже выполнена, продолжать её не нужно.
        var next = new Button { Content = "Дальше", IsEnabled = !done };
        var revise = new Button { Content = new FontIcon { Glyph = "\uE72C" } };
        // Подсказка и доступное имя объясняют кнопку со значком.
        ToolTipService.SetToolTip(revise, "Переделать ответ");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(revise, "Переделать ответ");

        // Кнопки стоят рядом друг с другом.
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(next);
        buttons.Children.Add(revise);
        message.Body.Children.Add(buttons);

        // Локальные значения обновляются, когда пользователь переделывает ответ.
        string current = instruction;
        bool complete = done;
        bool recorded = false;

        next.Click += async (_, _) =>
        {
            // Не запускаем два действия одновременно.
            if (_busy)
                return;

            next.IsEnabled = false;
            revise.IsEnabled = false;
            if (!recorded)
            {
                // Нажатие «Дальше» подтверждает текущий шаг и добавляет его в историю.
                session.CompletedSteps.Add(current);
                session.History.Add(
                    new GuideMemoryMessage("user", $"Пользователь подтвердил шаг: {current}"));
                recorded = true;
            }

            // Запрашиваем следующий шаг; если запрос не удался, разрешаем повторную проверку.
            if (!await RequestNextStepAsync(session, next))
                revise.IsEnabled = true;
        };

        revise.Click += async (_, _) =>
        {
            // Пока работает запрос, повторное нажатие ничего не делает.
            if (_busy)
                return;

            next.IsEnabled = false;
            revise.IsEnabled = false;
            GuideStepResponse? result = await ReviseStepAsync(
                session, request, message, current, recorded);
            if (result is null)
            {
                // При ошибке возвращаем кнопкам прежнюю доступность.
                next.IsEnabled = !complete;
                revise.IsEnabled = true;
                return;
            }

            // После успешного повтора кнопки учитывают новый ответ.
            current = result.Instruction;
            complete = result.Done;
            next.IsEnabled = !complete;
            revise.IsEnabled = true;
        };
    }

    // Просит ИИ перепроверить ответ с тем же снимком и прежней историей.
    private async Task<GuideStepResponse?> ReviseStepAsync(
        GuideSession session,
        StepRequest request,
        ChatMessage message,
        string previous,
        bool recorded)
    {
        SetBusy(true);
        // Сохраняем текст, чтобы восстановить его, если повторный запрос не удастся.
        string oldText = message.Text.Text;

        try
        {
            SetStatus(message, null);
            GuideStepResponse result = await GetAnswerFromPythonAsync(
                session, request, previous, message);
            string instruction = result.Instruction.Trim();
            ShowStep(message, request.Number, instruction, result.Done);
            // Запоминаем просьбу перепроверить и исправленный ответ.
            session.History.Add(new GuideMemoryMessage(
                "user", "Пользователя что-то не устроило - проверь внимательнее."));
            session.History.Add(new GuideMemoryMessage("assistant", instruction));
            // Если шаг уже подтверждали, исправляем его текст в списке выполненных.
            if (recorded && request.Number <= session.CompletedSteps.Count)
                session.CompletedSteps[request.Number - 1] = instruction;
            return new GuideStepResponse(result.Done, instruction);
        }
        catch (Exception)
        {
            // Не теряем прежний ответ, если связь с Python или ИИ не сработала.
            UpdateText(message.Text, oldText);
            SetStatus(message, "Не получилось переделать ответ. Попробуй ещё раз.");
            return null;
        }
        finally
        {
            // Этот блок выполняется и после успеха, и после ошибки.
            SetBusy(false);
        }
    }

    // Добавляет кнопку для повторного запроса после ошибки.
    private void AddRetryButton(ChatMessage message, GuideSession session)
    {
        var retry = new Button { Content = "Повторить" };
        retry.Click += async (_, _) =>
        {
            // Оставляем сообщение об ошибке, если повтор снова не удался.
            if (!await RequestNextStepAsync(session, retry))
                return;

            // После успешного повтора убираем старое сообщение об ошибке.
            ChatMessages.Children.Remove(message.Bubble);
            PromptInput.Text = "";
        };
        message.Body.Children.Add(retry);
    }

    // Разрешает или блокирует отправку текста на время запроса.
    private void SetBusy(bool busy)
    {
        _busy = busy;
        SendButton.IsEnabled = !busy;
        PromptInput.IsEnabled = !busy;
    }

    // Полный порядок запроса: сделать снимок, спросить ИИ, показать ответ.
    private async Task<bool> RequestNextStepAsync(GuideSession session, Button? source = null)
    {
        if (_busy)
            return false;

        SetBusy(true);
        if (source is not null)
            // На время запроса блокируем кнопку, которая его запустила.
            source.IsEnabled = false;

        // Сначала показываем, что ответ готовится.
        ChatMessage message = AddMessage("Смотрю на экран...", false);
        // Следующий номер равен числу подтверждённых шагов плюс один.
        int number = session.CompletedSteps.Count + 1;
        bool delivered = false;

        try
        {
            // Окно скрывается на время снимка, чтобы не попасть в изображение.
            string image = await CaptureScreenAsync();
            // Снимок и историю копируем в неизменяемый набор для этого запроса.
            var request = new StepRequest(
                session.CompletedSteps.ToArray(),
                session.History.ToArray(),
                image,
                number);

            GuideStepResponse result = await GetAnswerFromPythonAsync(
                session, request, null, message);
            string instruction = result.Instruction.Trim();
            // Показываем инструкцию и запоминаем её для следующего шага.
            ShowStep(message, number, instruction, result.Done);
            session.History.Add(new GuideMemoryMessage("assistant", instruction));
            AddStepButtons(message, session, request, instruction, result.Done);
            delivered = true;
        }
        catch (Exception)
        {
            // Первую ошибку показываем с кнопкой повтора; ошибки продолжения скрываем.
            if (source is null)
            {
                UpdateText(message.Text, "Не получилось получить шаг. Попробуй ещё раз.");
                AddRetryButton(message, session);
            }
            else
            {
                ChatMessages.Children.Remove(message.Bubble);
            }
        }
        finally
        {
            // В любом случае разрешаем ввод и правильно включаем кнопку-источник.
            SetBusy(false);
            if (source is not null)
                source.IsEnabled = !delivered;
        }

        return delivered;
    }

    // Передаёт запрос Python и обновляет карточку по мере прихода текста.
    private async Task<GuideStepResponse> GetAnswerFromPythonAsync(
        GuideSession session,
        StepRequest request,
        string? previous,
        ChatMessage message)
    {
        // StringBuilder удобно добавляет много небольших частей одной строки.
        var text = new StringBuilder($"Шаг {request.Number}\n");
        // callback и очередь окна могут обращаться к этим значениям в разное время.
        var updateLock = new object();
        bool updateQueued = false;
        bool finished = false;

        GuideStepResponse result = await GetNextStepAsync(
            session.Question,
            request.CompletedSteps,
            request.History,
            request.Image,
            previous,
            fragment =>
            {
                // Фрагмент дописываем под блокировкой, чтобы текст не повредился.
                bool enqueueUpdate = false;
                lock (updateLock)
                {
                    text.Append(fragment);
                    // Пока обновление уже стоит в очереди, второе не добавляем.
                    if (!updateQueued)
                    {
                        updateQueued = true;
                        enqueueUpdate = true;
                    }
                }

                if (!enqueueUpdate)
                    return;

                // Изменять элементы интерфейса нужно в очереди главного окна.
                DispatcherQueue.TryEnqueue(() =>
                {
                    string? currentText;
                    lock (updateLock)
                    {
                        updateQueued = false;
                        // После конца запроса не ставим устаревшее обновление на экран.
                        currentText = finished ? null : text.ToString();
                    }

                    if (currentText is not null)
                        UpdateText(message.Text, currentText, scrollToLatest: false);
                });
            });

        // Помечаем ответ завершённым, прежде чем вернуть его вызывающему коду.
        lock (updateLock)
            finished = true;

        return result;
    }

    // Формирует JSON-команду для Python и получает итоговое событие.
    private async Task<GuideStepResponse> GetNextStepAsync(
        string question,
        IReadOnlyList<string> completedSteps,
        IReadOnlyList<GuideMemoryMessage> history,
        string image,
        string? previousAnswer,
        Action<string> onText)
    {
        // Имена с подчёркиванием совпадают с теми, которые ожидает brain.py.
        var request = new
        {
            action = "answer",
            question,
            completed_steps = completedSteps,
            history,
            image,
            previous_answer = previousAnswer
        };

        // Python присылает текст отдельными delta-событиями и затем итог.
        BrainEvent result = await RunBrainAsync(request, item =>
        {
            if (item.Type == "delta")
                onText(item.Text ?? "");
        });
        return new GuideStepResponse(result.Done, result.Instruction ?? "");
    }

    // Отдельная команда Python делает снимок экрана без запроса к ИИ.
    private async Task<string> CaptureScreenFromPythonAsync()
    {
        BrainEvent result = await RunBrainAsync(new { action = "capture" }, _ => { });
        return result.Image
            ?? throw new InvalidOperationException("Python не вернул снимок экрана.");
    }

    // Запускает brain.py, отправляет ему JSON и читает строки JSON-событий.
    private static async Task<BrainEvent> RunBrainAsync(object input, Action<BrainEvent> onEvent)
    {
        // Можно задать полный путь к Python; иначе используем стандартный Windows Launcher.
        string? configuredPython = Environment.GetEnvironmentVariable("ORDINATUTEUR_PYTHON");
        var start = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(configuredPython) ? "py" : configuredPython,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8
        };
        if (string.IsNullOrWhiteSpace(configuredPython))
            // Launcher py получает параметр -3, чтобы выбрать Python 3.
            start.ArgumentList.Add("-3");

        // Python-скрипт лежит рядом с собранным приложением.
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "brain.py"));
        // Настраиваем Python на UTF-8, чтобы русский текст не повредился.
        start.Environment["PYTHONIOENCODING"] = "utf-8";

        // Запускаем процесс и сразу начинаем читать ошибки, чтобы не заполнить буфер.
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Не удалось запустить Python.");
        Task<string> errors = process.StandardError.ReadToEndAsync();

        try
        {
            // Передаём одну JSON-команду и закрываем ввод, сообщая Python, что данных больше нет.
            await process.StandardInput.WriteAsync(
                JsonSerializer.Serialize(input, JsonOptions));
            process.StandardInput.Close();

            BrainEvent? result = null;
            string? line;
            // Python выдаёт по одному JSON-событию на каждой строке.
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                BrainEvent? item = JsonSerializer.Deserialize<BrainEvent>(line, JsonOptions);
                if (item is null)
                    continue;

                onEvent(item);
                // Последнее событие содержит результат снимка или полный ответ ИИ.
                if (item.Type == "result" || item.Type == "capture")
                    result = item;
            }

            // Дожидаемся завершения Python и чтения его потока ошибок.
            await process.WaitForExitAsync();
            _ = await errors;

            // Ошибкой считается сбой Python или отсутствие обязательного результата.
            if (process.ExitCode != 0
                || result is null
                || (result.Type == "result" && string.IsNullOrWhiteSpace(result.Instruction))
                || (result.Type == "capture" && string.IsNullOrWhiteSpace(result.Image)))
                throw new InvalidOperationException("Не удалось получить ответ.");

            return result;
        }
        catch
        {
            // Если запрос прервался, закрываем Python и его дочерние процессы.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            throw;
        }
    }

    // Показывает номер шага, но убирает его, когда задача уже завершена.
    private void ShowStep(ChatMessage message, int number, string text, bool done)
    {
        if (done)
            UpdateText(message.Text, text);
        else
            UpdateText(message.Text, $"Шаг {number}\n{text}");
    }

    // Временно прячет окно, чтобы оно не попало в снимок экрана.
    private async Task<string> CaptureScreenAsync()
    {
        AppWindow.Hide();
        try
        {
            // Короткая пауза даёт Windows время убрать окно перед снимком.
            await Task.Delay(150);
            return await CaptureScreenFromPythonAsync();
        }
        finally
        {
            // Показываем окно и возвращаем ему фокус даже после ошибки снимка.
            AppWindow.Show();
            Activate();
        }
    }
}
