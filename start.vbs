 ' Этот сценарий запускается двойным щелчком в Проводнике Windows.
Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")

' Определяем папку проекта по расположению этого сценария.
root = files.GetParentFolderName(WScript.ScriptFullName)
shell.CurrentDirectory = root

' Путь к exe соответствует настройкам сборки из OrdinaTuteur.csproj.
app = root & "\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\OrdinaTuteur.exe"

' Эти исходники влияют на собранную программу.
sources = Array( _
    "AIKey.py", _
    "App.xaml", _
    "App.xaml.cs", _
    "MainWindow.xaml", _
    "MainWindow.xaml.cs", _
    "OrdinaTuteur.csproj", _
    "app.manifest", _
    "brain.py")

' Если exe отсутствует, его нужно собрать.
rebuild = Not files.FileExists(app)
If Not rebuild Then
    ' Если exe есть, сравниваем время его изменения со временем исходников.
    Set executable = files.GetFile(app)
    For Each sourceName In sources
        sourcePath = root & "\" & sourceName
        If files.FileExists(sourcePath) Then
            If files.GetFile(sourcePath).DateLastModified > executable.DateLastModified Then
                ' Нашли новый исходник: сборку нужно выполнить снова.
                rebuild = True
                Exit For
            End If
        End If
    Next
End If

If rebuild Then
    ' cmd выполняет сборку и показывает сообщение, если dotnet завершился с ошибкой.
    buildCommand = "cmd.exe /c dotnet build -p:Platform=x64 || " & _
        "(echo. & echo Build failed. See the errors above. Press any key to close. " & _
        "& pause & exit /b 1)"
    result = shell.Run(buildCommand, 1, True)
    If result <> 0 Then
        ' Сообщаем об ошибке и завершаем сценарий с тем же кодом.
        MsgBox "Сборка не удалась. Подробности ошибки показаны в окне сборки.", 16, "OrdinaTuteur"
        WScript.Quit result
    End If
End If

' Запускаем приложение и не ждём, пока пользователь закроет его.
shell.Run """" & app & """", 1, False
