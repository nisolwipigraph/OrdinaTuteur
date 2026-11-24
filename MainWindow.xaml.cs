// Подключаем запуск процессов, текст и разбор JSON.
using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace OrdinaTuteur;

// Код главного окна. Разметка лежит в MainWindow.xaml.
// Здесь кнопки, память текущей задачи и связь с brain.py.
public sealed partial class MainWindow : Window
{
    // false означает: не добавлять скрытый символ BOM в начало текста.
    // Иначе Python может не разобрать первую JSON-команду.
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    // Web-настройки пишут имена полей маленькими буквами: role, а не Role.
    // Так же их ждёт brain.py.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Пока идёт запрос, второе сообщение отправить нельзя.
    private bool _busy;

    public MainWindow()
    {
        // Создаёт элементы, описанные в MainWindow.xaml: чат, поле и кнопку.
        InitializeComponent();
    }

    // Карточка на экране. Храним ссылки, чтобы потом менять текст и кнопки.
    private sealed class ChatMessage(TextBlock text, StackPanel body, Border bubble)
    {
        public TextBlock Text { get; } = text;
        public StackPanel Body { get; } = body;
        public Border Bubble { get; } = bubble;
    }

    // Одна реплика для ИИ. role — это "user" или "assistant".
    private sealed class MemoryMessage(string role, string content)
    {
        public string Role { get; } = role;
        public string Content { get; } = content;
    }

    // Готовый ответ Python: задача закончена или нет, и что показать.
    private sealed class StepAnswer(bool done, string instruction)
    {
        public bool Done { get; } = done;
        public string Instruction { get; } = instruction;
    }

    // Одна строка, которую печатает brain.py.
    // Для снимка заполнено Image, для шага — Done и Instruction.
    private sealed class BrainEvent
    {
        public string Type { get; set; } = "";
        public string? Image { get; set; }
        public bool Done { get; set; }
        public string? Instruction { get; set; }
    }

    // Память одной задачи. Новая фраза в поле ввода создаёт новую сессию.
    // Списки живут только в памяти и пропадают вместе с окном.
    private sealed class TaskSession(string question)
    {
        public string Question { get; } = question;
        public List<string> CompletedSteps { get; } = [];
        public List<MemoryMessage> History { get; } = [];
    }

    // Копия состояния на момент шага.
    // Она нужна кнопке «Переделать»: снимок и история не должны уехать вперёд,
    // даже если пользователь уже нажал «Дальше» у более позднего шага.
    private sealed class StepRequest(
        IReadOnlyList<string> completedSteps,
        IReadOnlyList<MemoryMessage> history,
        string image,
        int number)
    {
        public IReadOnlyList<string> CompletedSteps { get; } = completedSteps;
        public IReadOnlyList<MemoryMessage> History { get; } = history;
        public string Image { get; } = image;
        public int Number { get; } = number;
    }

    // Рисует одну карточку и добавляет её вниз чата.
    private ChatMessage AddMessage(string text, bool user)
    {
        // Сверху подпись «Ты» или «OrdinaTuteur», под ней текст, потом кнопки.
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock
        {
            Text = user ? "Ты" : "OrdinaTuteur",
            FontSize = 12,
            Opacity = 0.75
        });

        // Текст можно выделить мышкой. Wrap переносит длинные строки.
        var content = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16,
            IsTextSelectionEnabled = true
        };
        body.Children.Add(content);

        // Сообщение пользователя рисуем акцентным цветом и прижимаем вправо.
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
        ScrollToLatest();
        return new ChatMessage(content, body, bubble);
    }

    // Меняет текст карточки и прокручивает чат, чтобы его было видно.
    private void UpdateText(TextBlock text, string value)
    {
        text.Text = value;
        ScrollToLatest();
    }

    // UpdateLayout нужен, чтобы ScrollableHeight уже знала новую высоту.
    private void ScrollToLatest()
    {
        ChatMessages.UpdateLayout();
        ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null);
    }

    // Enter в поле ввода работает так же, как кнопка «Отправить».
    private void PromptInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        // Handled не даёт полю ввода обработать Enter ещё раз.
        e.Handled = true;
        SendButton_Click(sender, new RoutedEventArgs());
    }

    // Начинает новую задачу. Старые карточки на экране остаются.
    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        // Trim убирает пробелы и пустые строки по краям.
        string question = PromptInput.Text.Trim();
        if (_busy || question.Length == 0)
            return;

        AddMessage(question, true);
        // Поле очищаем только после удачного ответа. Иначе текст не пропадёт,
        // и его можно отправить ещё раз кнопкой «Повторить».
        if (await AskForNextStep(new TaskSession(question)))
            PromptInput.Text = "";
    }

    // Под ответом ИИ появляются «Дальше» и кнопка переделки.
    private void AddStepButtons(
        ChatMessage message,
        TaskSession session,
        StepRequest request,
        string instruction,
        bool done)
    {
        // У готовой задачи продолжать некуда, поэтому «Дальше» выключена.
        var next = new Button { Content = "Дальше", IsEnabled = !done };
        // E72C — значок обновления из шрифта Windows. Подпись нужна для
        // подсказки мыши и для программ чтения с экрана.
        var revise = new Button { Content = new FontIcon { Glyph = "\uE72C" } };
        ToolTipService.SetToolTip(revise, "Переделать ответ");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(revise, "Переделать ответ");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(next);
        buttons.Children.Add(revise);
        message.Body.Children.Add(buttons);

        // current и complete меняются, если пользователь переделал ответ.
        // saved не даёт записать один и тот же шаг в историю дважды.
        string current = instruction;
        bool complete = done;
        bool saved = false;

        next.Click += async (_, _) =>
        {
            if (_busy)
                return;

            next.IsEnabled = false;
            revise.IsEnabled = false;
            if (!saved)
            {
                session.CompletedSteps.Add(current);
                session.History.Add(new MemoryMessage("user", "Пользователь подтвердил шаг: " + current));
                saved = true;
            }

            // При ошибке «Дальше» снова включит AskForNextStep.
            // Здесь возвращаем только кнопку переделки.
            if (!await AskForNextStep(session, next))
                revise.IsEnabled = true;
        };

        revise.Click += async (_, _) =>
        {
            if (_busy)
                return;

            next.IsEnabled = false;
            revise.IsEnabled = false;
            // В Python уходит current — текст совета без строки «Шаг N».
            StepAnswer? result = await ReviseStep(session, request, message, current, saved);
            if (result is null)
            {
                // Ошибка: оставляем прежний текст и прежние кнопки.
                next.IsEnabled = !complete;
                revise.IsEnabled = true;
                return;
            }

            current = result.Instruction;
            complete = result.Done;
            next.IsEnabled = !complete;
            revise.IsEnabled = true;
        };
    }

    // Просит ИИ ещё раз посмотреть тот же снимок и тот же прошлый совет.
    private async Task<StepAnswer?> ReviseStep(
        TaskSession session,
        StepRequest request,
        ChatMessage message,
        string previous,
        bool saved)
    {
        SetBusy(true);
        // Если запрос сорвётся, вернём на экран то, что уже было показано.
        string oldText = message.Text.Text;

        try
        {
            StepAnswer result = await AskPython(session, request, previous);
            string instruction = result.Instruction.Trim();
            ShowStep(message, request.Number, instruction, result.Done);
            session.History.Add(new MemoryMessage(
                "user", "Пользователя что-то не устроило - проверь внимательнее."));
            session.History.Add(new MemoryMessage("assistant", instruction));
            // Если шаг уже подтвердили, исправляем его текст, а не добавляем новый.
            if (saved && request.Number <= session.CompletedSteps.Count)
                session.CompletedSteps[request.Number - 1] = instruction;
            return new StepAnswer(result.Done, instruction);
        }
        catch (Exception)
        {
            UpdateText(message.Text, oldText);
            return null;
        }
        finally
        {
            // finally выполняется и после успеха, и после ошибки.
            SetBusy(false);
        }
    }

    // Кнопка на случай, когда самый первый запрос задачи не удался.
    private void AddRetryButton(ChatMessage message, TaskSession session)
    {
        var retry = new Button { Content = "Повторить" };
        retry.Click += async (_, _) =>
        {
            if (!await AskForNextStep(session, retry))
                return;

            // Успех уже показал новую карточку. Старую карточку с «Повторить» убираем.
            ChatMessages.Children.Remove(message.Bubble);
            PromptInput.Text = "";
        };
        message.Body.Children.Add(retry);
    }

    // Блокирует ввод на время снимка и запроса к ИИ.
    private void SetBusy(bool busy)
    {
        _busy = busy;
        SendButton.IsEnabled = !busy;
        PromptInput.IsEnabled = !busy;
    }

    // Полный цикл нового шага: снимок, вопрос к ИИ, карточка с кнопками.
    // source — кнопка, которая запустила шаг. У первой отправки её нет.
    private async Task<bool> AskForNextStep(TaskSession session, Button? source = null)
    {
        if (_busy)
            return false;

        SetBusy(true);
        if (source is not null)
            source.IsEnabled = false;

        ChatMessage message = AddMessage("Смотрю на экран...", false);
        // Номер шага на экране = сколько шагов уже подтверждено + 1.
        int number = session.CompletedSteps.Count + 1;
        bool ok = false;

        try
        {
            string image = await TakeScreenshot();
            // ToArray копирует списки. Дальнейшие «Дальше» не изменят этот запрос.
            var request = new StepRequest(
                session.CompletedSteps.ToArray(),
                session.History.ToArray(),
                image,
                number);

            StepAnswer result = await AskPython(session, request, null);
            string instruction = result.Instruction.Trim();
            ShowStep(message, number, instruction, result.Done);
            session.History.Add(new MemoryMessage("assistant", instruction));
            AddStepButtons(message, session, request, instruction, result.Done);
            ok = true;
        }
        catch (Exception)
        {
            // Текст ошибки не показываем.
            // Первая отправка получает кнопку «Повторить».
            // Повтор с «Дальше» просто убирает пустую карточку.
            if (source is null)
            {
                message.Body.Children.Remove(message.Text);
                AddRetryButton(message, session);
            }
            else
            {
                ChatMessages.Children.Remove(message.Bubble);
            }
        }
        finally
        {
            SetBusy(false);
            // После неудачи кнопку-источник можно нажать снова.
            if (source is not null)
                source.IsEnabled = !ok;
        }

        return ok;
    }

    // Собирает JSON-команду answer и ждёт итоговую строку от Python.
    // previous равен null для нового шага и заполнен при переделке.
    private async Task<StepAnswer> AskPython(TaskSession session, StepRequest request, string? previous)
    {
        var command = new
        {
            action = "answer",
            question = session.Question,
            completed_steps = request.CompletedSteps,
            history = request.History,
            image = request.Image,
            previous_answer = previous
        };

        BrainEvent result = await RunPython(command);
        return new StepAnswer(result.Done, result.Instruction ?? "");
    }

    // Прячет окно, просит Python снять экран и снова показывает окно.
    private async Task<string> TakeScreenshot()
    {
        // Иначе на снимке будет сам OrdinaTuteur, а не программа пользователя.
        AppWindow.Hide();
        try
        {
            // Короткая пауза даёт Windows время убрать окно с экрана.
            await Task.Delay(150);
            BrainEvent result = await RunPython(new { action = "capture" });
            if (string.IsNullOrWhiteSpace(result.Image))
                throw new InvalidOperationException("Python не вернул снимок экрана.");
            return result.Image;
        }
        finally
        {
            // Окно возвращаем даже если снимок не удался.
            AppWindow.Show();
            Activate();
        }
    }

    // Запускает brain.py, отдаёт ему одну команду и читает строки ответа.
    private static async Task<BrainEvent> RunPython(object command)
    {
        using Process process = StartPython();
        // Ошибки читаем сразу и параллельно. Иначе заполненный буфер
        // может навсегда остановить Python, а окно будет ждать вывод.
        Task<string> errors = process.StandardError.ReadToEndAsync();

        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(command, JsonOptions));
            // Закрытый ввод говорит Python, что команда закончилась.
            process.StandardInput.Close();

            BrainEvent? result = null;
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                BrainEvent? item = JsonSerializer.Deserialize<BrainEvent>(line, JsonOptions);
                if (item is null)
                    continue;

                // capture — это снимок, result — готовый шаг.
                if (item.Type == "result" || item.Type == "capture")
                    result = item;
            }

            await process.WaitForExitAsync();
            // Сам текст ошибки пользователю не нужен, но дочитать поток надо.
            _ = await errors;

            if (process.ExitCode != 0 || result is null)
                throw new InvalidOperationException("Не удалось получить ответ.");
            if (result.Type == "result" && string.IsNullOrWhiteSpace(result.Instruction))
                throw new InvalidOperationException("Не удалось получить ответ.");
            if (result.Type == "capture" && string.IsNullOrWhiteSpace(result.Image))
                throw new InvalidOperationException("Не удалось получить ответ.");

            return result;
        }
        catch
        {
            // Если окно уже не ждёт ответ, Python тоже нужно остановить.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            throw;
        }
    }

    // Ищет Python на этом компьютере и запускает brain.py без окна консоли.
    // Свой путь можно задать переменной среды ORDINATUTEUR_PYTHON.
    private static Process StartPython()
    {
        string? custom = Environment.GetEnvironmentVariable("ORDINATUTEUR_PYTHON");
        // BaseDirectory — папка exe. Туда сборка копирует brain.py и AIKey.py.
        string brain = Path.Combine(AppContext.BaseDirectory, "brain.py");
        // py -3 — обычный запуск Python 3 в Windows. Если его нет, пробуем python.
        string[] programs = string.IsNullOrWhiteSpace(custom) ? ["py", "python"] : [custom];

        foreach (string program in programs)
        {
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = program,
                    WorkingDirectory = AppContext.BaseDirectory,
                    // false: запускаем программу напрямую и можем читать её текст.
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = Utf8,
                    StandardOutputEncoding = Utf8,
                    StandardErrorEncoding = Utf8
                };
                // Дублируем UTF-8 для Python, даже если консоль Windows другая.
                start.Environment["PYTHONIOENCODING"] = "utf-8";
                if (program == "py")
                    start.ArgumentList.Add("-3");
                start.ArgumentList.Add(brain);

                Process? process = Process.Start(start);
                if (process is not null)
                    return process;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Этой команды на компьютере нет. Пробуем следующее имя.
            }
        }

        throw new InvalidOperationException("Не удалось запустить Python.");
    }

    // У готовой задачи номер шага не показываем: остаётся короткое подтверждение.
    private void ShowStep(ChatMessage message, int number, string text, bool done)
    {
        if (done)
            UpdateText(message.Text, text);
        else
            UpdateText(message.Text, "Шаг " + number + "\n" + text);
    }
}
