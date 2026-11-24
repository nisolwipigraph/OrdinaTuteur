' Двойной щелчок в Проводнике: собирает программу, если надо, и запускает её.
' Отдельное чёрное окно командной строки не остаётся на экране.
Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")

' Папка проекта — это папка самого start.vbs, а не папка, откуда его вызвали.
root = files.GetParentFolderName(WScript.ScriptFullName)
shell.CurrentDirectory = root

' Путь совпадает с настройками OrdinaTuteur.csproj: Debug, x64, .NET 10.
app = root & "\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\OrdinaTuteur.exe"

' Если любой из этих файлов новее exe, сборку нужно повторить.
sources = Array( _
    "AIKey.py", _
    "App.xaml", _
    "App.xaml.cs", _
    "MainWindow.xaml", _
    "MainWindow.xaml.cs", _
    "OrdinaTuteur.csproj", _
    "app.manifest", _
    "brain.py")

rebuild = Not files.FileExists(app)
If Not rebuild Then
    Set executable = files.GetFile(app)
    For Each sourceName In sources
        sourcePath = root & "\" & sourceName
        If files.FileExists(sourcePath) Then
            If files.GetFile(sourcePath).DateLastModified > executable.DateLastModified Then
                rebuild = True
                Exit For
            End If
        End If
    Next
End If

If rebuild Then
    ' 0 — скрытое окно, True — ждать окончания сборки.
    result = shell.Run("cmd.exe /c dotnet build -p:Platform=x64", 0, True)
    If result <> 0 Then
        MsgBox "Не получилось собрать программу. На компьютере нужен .NET 10 SDK.", 16, "OrdinaTuteur"
        WScript.Quit result
    End If
End If

' Pillow нужен brain.py для снимка экрана. Ставим его, только если импорта нет.
' Сначала пробуем лаунчер py -3, потом команду python: на другом компьютере
' может быть установлено что-то одно.
pillow = shell.Run("cmd.exe /c py -3 -c ""import PIL""", 0, True)
If pillow <> 0 Then
    pillow = shell.Run("cmd.exe /c python -c ""import PIL""", 0, True)
End If
If pillow <> 0 Then
    installed = shell.Run("cmd.exe /c py -3 -m pip install -r requirements.txt", 0, True)
    If installed <> 0 Then
        installed = shell.Run("cmd.exe /c python -m pip install -r requirements.txt", 0, True)
    End If
    If installed <> 0 Then
        MsgBox "Не получилось установить Pillow. На компьютере нужен Python 3.", 16, "OrdinaTuteur"
        WScript.Quit installed
    End If
End If

If Not files.FileExists(app) Then
    MsgBox "Программа не собралась. Откройте OrdinaTuteur.sln в Visual Studio.", 16, "OrdinaTuteur"
    WScript.Quit 1
End If

' 1 — обычное окно приложения. False — не ждать, пока пользователь его закроет.
shell.Run """" & app & """", 1, False
