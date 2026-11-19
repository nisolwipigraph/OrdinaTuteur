# Разрешает указывать типы, которые объявлены ниже в файле.
from __future__ import annotations

# Нужен для превращения снимка экрана в строку для передачи через JSON.
import base64
# Нужен, чтобы читать и создавать сообщения в формате JSON.
import json
# Нужен для сведений о версии Windows.
import platform
# Нужен для чтения команды от C#-приложения.
import sys
# Нужен для уникального номера запроса к сервису ИИ.
import uuid
# Нужен для чтения выбранных в Windows приложений по умолчанию.
import winreg
# Буфер в памяти позволяет сохранить картинку, не создавая временный файл.
from io import BytesIO
# Помогает читать ответ ИИ по частям, не теряя первую уже прочитанную строку.
from itertools import chain
# Any означает «значение любого типа», Iterator — «последовательность значений».
from typing import Any, Iterator
# Эти классы отправляют запрос в интернет и читают ответ сервера.
from urllib.request import Request, urlopen

# ImageGrab делает снимок экрана с помощью библиотеки Pillow.
from PIL import ImageGrab

# Ключ
from AIKey import API_KEY

# Имя модели, адрес сервиса и ограничения одного запроса.
MODEL = "gpt-5.6-sol"
API_URL = "https://limitdeckai.ru/v1/chat/completions"
API_TIMEOUT = 90
MAX_TOKENS = 96

# Модель начинает ответ с одного из этих слов, чтобы программа поняла его вид.
STEP_MARKER = "STEP:"
DONE_MARKER = "DONE:"

# Главная инструкция модели
SYSTEM_PROMPT = """Ты помогаешь выполнить задачу в Windows. 
Определи по снимку активное приложение и текущий этап задачи. Учитывай историю и подтверждённые шаги, чтобы не просить повторять сделанное. Учитывай сведения о Windows и установленных программах ниже. Если снимок не совпадает с историей, доверяй снимку. Если задача зависит от открытого окна, ориентируйся на снимок, а не на список программ.
Не выдумывай настройки и не утверждай, что окно открыто, если этого не видно. Также у тебя есть список приложений по умолчанию и приложений на компьютере вообще.
Давай только одно ближайшее действие за раз (если нужно открыть контекстное окно, то скажи, что в нём нужно сделать пользователю. Иначе контекстное окно закроется. Под контекстные окна включай: окно пуск, меню wifi bluetooth громкость, контекстное меню правой конпки и прочие окна, которые закрываются при нажатии вне этого окна). Назови видимый элемент или попроси открыть нужное окно.
Не перечисляй будущие действия и варианты. Не добавляй вступление и нумерацию. Если безопасно выбрать действие нельзя, задай один короткий
вопрос. Если нужно открыть окно и сделать действие, сразу напиши, что сделать. Не открывай файлы внутри папки OrdinaTuteur.
После подтверждения пользователем шага обязательно проверь новый снимок экрана.
Если на снимке видно, что цель достигнута, не предлагай следующий шаг и не проси
подтверждение ещё раз. Заверши ответ коротко: например, если пользователь просил
открыть Spotify и его окно уже видно, ответь только DONE: Spotify открыт.
Если на снимке цель ещё не достигнута, продолжай с одним ближайшим действием.
Если задача выполнена, ответь только DONE: и коротким подтверждением.
Иначе ответь только STEP: и одной инструкцией."""


# Контекст Windows для выбора подходящих приложений.
def registry_value(root: Any, path: str, name: str, default: Any = "") -> Any:
    """Читает одно значение реестра или возвращает запасной текст."""
    try:
        # OpenKey открывает раздел реестра, а with закрывает его после чтения.
        with winreg.OpenKey(root, path) as key:
            # QueryValueEx возвращает пару: значение и его тип. Нужна первая часть.
            return winreg.QueryValueEx(key, name)[0]
    except FileNotFoundError:
        # Например, приложение по умолчанию может быть ещё не выбрано.
        return default


def default_apps() -> dict[str, str]:
    """Возвращает только три выбранных приложения, а не весь список программ."""
    # Для каждого типа файлов задаём раздел реестра и имя нужного значения.
    associations = {
        "браузер": (
            r"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice",
            "ProgId",
        ),
        "почта": (
            r"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\mailto\UserChoice",
            "ProgId",
        ),
        "PDF": (
            r"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice",
            "ProgId",
        ),
    }

    # По очереди читаем настройки и собираем их в словарь «название: приложение».
    return {
        name: registry_value(winreg.HKEY_CURRENT_USER, path, value_name) or "не задано"
        for name, (path, value_name) in associations.items()
    }


def computer_context() -> dict[str, Any]:
    """Собирает короткие сведения о Windows для подсказки модели."""
    return {
        # Версию показываем, чтобы ИИ мог учитывать особенности этой Windows.
        "windows": f"{platform.system()} {platform.release()} "
        f"({platform.machine()}, build {platform.version()})",
        # Названия приложений помогают выбрать подходящую инструкцию.
        "default_apps": default_apps(),
    }


def message_text(message: Any) -> str:
    """Достаёт текст из сообщения сервиса, если он там есть."""
    # Ответ API может содержать не словарь, а ошибочное или пустое значение.
    if not isinstance(message, dict):
        return ""

    content = message.get("content")
    # В обычном ответе content — это готовая строка.
    if isinstance(content, str):
        return content
    # В некоторых ответах content — список частей с отдельными фрагментами текста.
    if isinstance(content, list):
        return "".join(
            part["text"]
            for part in content
            if isinstance(part, dict) and isinstance(part.get("text"), str)
        )
    return ""


# Разбор ответов API в потоковом и обычном форматах.
def response_chunks(response: Any) -> Iterator[str]:
    """Возвращает текст ответа по частям, как для потока, так и для целого ответа."""
    # Читаем первую строку, чтобы определить формат ответа.
    first_line = response.readline()
    content_type = response.headers.get("Content-Type", "").lower()
    first_content = first_line.lstrip()
    # Поток обычно помечен типом text/event-stream или начинается с data:/event:.
    is_stream = "text/event-stream" in content_type or first_content.startswith(
        (b"data:", b"event:", b":")
    )

    if not is_stream:
        # Обычный ответ целиком состоит из JSON; присоединяем оставшуюся часть.
        body = first_line + response.read()
        result = json.loads(body)
        for choice in result.get("choices", []):
            text = message_text(choice.get("message"))
            if text:
                yield text
        return

    # В первой строке уже могли быть данные, поэтому добавляем её обратно в поток.
    for line in chain((first_line,), response):
        # Каждая строка события приходит в байтах; переводим её в обычный текст.
        line = line.decode("utf-8").strip()
        # В потоке нас интересуют только строки с содержимым события.
        if not line.startswith("data:"):
            continue
        payload = line[5:].strip()
        # Это специальное сообщение сервера о конце потока.
        if payload == "[DONE]":
            break
        # Пустые строки разделяют события и не содержат ответа.
        if not payload:
            continue

        # У потокового ответа текст обычно лежит в delta; иногда — в message.
        for choice in json.loads(payload).get("choices", []):
            text = message_text(choice.get("delta"))
            if not text:
                text = message_text(choice.get("message"))
            if text:
                yield text


def emit_event(event: dict[str, Any]) -> None:
    """Передаёт одно JSON-событие приложению через стандартный вывод."""
    # Одна строка — одно событие. flush сразу отправляет его C#-программе.
    print(json.dumps(event, ensure_ascii=False), flush=True)


def capture_screen() -> str:
    """Снимает основной экран и возвращает уменьшенную картинку для API."""
    # Снимаем экран и уменьшаем изображение, чтобы отправлять меньше данных.
    image = ImageGrab.grab()
    image.thumbnail((800, 500))

    # Сохраняем картинку в памяти в JPEG, не создавая файл на диске.
    buffer = BytesIO()
    image.save(buffer, format="JPEG")

    # Кодируем байты в текст Base64 и добавляем префикс формата картинки.
    encoded_image = base64.b64encode(buffer.getvalue()).decode("ascii")
    return "data:image/jpeg;base64," + encoded_image


# Запрос к модели и передача ответа в окно.
def ask_model(messages: list[dict[str, Any]], system_prompt: str) -> None:
    """Отправляет сообщения ИИ и постепенно передаёт ответ приложению."""
    # Готовим данные запроса: модель, историю, ограничение и режим потока.
    body = {
        "model": MODEL,
        "messages": [{"role": "system", "content": system_prompt}, *messages],
        "max_tokens": MAX_TOKENS,
        "stream": True,
    }
    # Превращаем данные в HTTP-запрос с JSON-телом и нужными заголовками.
    request = Request(
        API_URL,
        data=json.dumps(body).encode("utf-8"),
        headers={
            "Authorization": f"Bearer {API_KEY}",
            "Content-Type": "application/json",
            "Accept": "text/event-stream",
            "Idempotency-Key": str(uuid.uuid4()),
        },
    )
    # answer хранит полный ответ; pending — начало ответа до маркера STEP/DONE.
    answer = []
    pending = ""
    stream_started = False

    # urlopen ждёт ответ сервера не дольше API_TIMEOUT секунд.
    with urlopen(request, timeout=API_TIMEOUT) as response:
        for chunk in response_chunks(response):
            # Запоминаем фрагмент, чтобы после потока определить итоговый ответ.
            answer.append(chunk)
            if stream_started:
                # После маркера дальнейшие фрагменты можно сразу показывать окну.
                emit_event({"type": "delta", "text": chunk})
                continue

            # Маркер может прийти несколькими кусочками, поэтому собираем начало.
            pending += chunk
            candidate = pending.lstrip()
            upper = candidate.upper()
            if upper.startswith(STEP_MARKER):
                # STEP означает, что модель предлагает следующий шаг.
                stream_started = True
                text = candidate[len(STEP_MARKER):].lstrip()
            elif upper.startswith(DONE_MARKER):
                # DONE означает, что модель считает задачу завершённой.
                stream_started = True
                text = candidate[len(DONE_MARKER):].lstrip()
            elif STEP_MARKER.startswith(upper) or DONE_MARKER.startswith(upper):
                # Пока пришла только часть маркера, ждём следующий кусочек.
                continue
            else:
                # Если маркера нет, считаем обычный текст началом ответа.
                stream_started = True
                text = pending
            pending = ""
            if text:
                # Отправляем первый текстовый фрагмент без служебного маркера.
                emit_event({"type": "delta", "text": text})

    # Собираем полный ответ и определяем, сообщил ли ИИ о завершении задачи.
    answer_text = "".join(answer).strip()
    upper_answer = answer_text.upper()
    done = upper_answer.startswith(DONE_MARKER)
    instruction = answer_text[len(DONE_MARKER):].strip() if done else answer_text
    if not done and upper_answer.startswith(STEP_MARKER):
        instruction = answer_text[len(STEP_MARKER):].strip()

    # Последнее событие содержит полный текст и признак завершённой задачи.
    emit_event({
        "type": "result",
        "done": done,
        "instruction": instruction,
    })


# Подготовка контекста задачи и изображения для API.
def image_content(text: str, image: str) -> dict[str, Any]:
    """Создаёт сообщение пользователя, которое содержит текст и снимок экрана."""
    return {
        "role": "user",
        "content": [
            {"type": "text", "text": text},
            {"type": "image_url", "image_url": {"url": image}},
        ],
    }


def computer_prompt() -> str:
    """Добавляет сведения о компьютере к основной инструкции для ИИ."""
    # ensure_ascii=False оставляет русские буквы читаемыми в JSON-строке.
    context = json.dumps(computer_context(), ensure_ascii=False)
    return (
        f"{SYSTEM_PROMPT}\n\n"
        f"Версия Windows и приложения по умолчанию пользователя:\n{context}"
    )


# Сборка и запуск запроса для одного шага.
def get_next_step(data: dict[str, Any]) -> None:
    """Собирает историю задачи, добавляет снимок и запрашивает следующий шаг."""
    # Готовим данные задачи для ИИ.
    details = {
        "task": data["question"],
        "completed_steps": data["completed_steps"],
    }
    # Копируем только роль и текст каждого сообщения из истории.
    messages = [
        {"role": turn["role"], "content": turn["content"]}
        for turn in data["history"]
    ]

    # Последнее сообщение содержит новую задачу и актуальный снимок экрана.
    messages.append(
        image_content(json.dumps(details, ensure_ascii=False), data["image"])
    )

    # Если ответ переделывают, добавляем прошлый ответ и причину повтора.
    previous_answer = data.get("previous_answer")
    if previous_answer:
        messages.extend([
            # Так ИИ видит свой прежний ответ и может его исправить.
            {"role": "assistant", "content": previous_answer},
            {
                "role": "user",
                "content": (
                    "Пользователя что-то не устроило - проверь внимательнее. "
                    "Повторно сверь предыдущую рекомендацию с тем же снимком экрана. "
                    "Исправь ответ, если нужного элемента на экране нет."
                ),
            },
        ])

    # Передаём всю переписку и инструкцию модели.
    ask_model(messages, computer_prompt())


def main() -> None:
    """Читает одну команду от C# и выполняет нужное действие."""
    # C# отправляет JSON в стандартный ввод, а затем закрывает его.
    request_data = json.load(sys.stdin)
    if request_data.get("action") == "capture":
        # Команда capture просит сделать снимок, не обращаясь к ИИ.
        emit_event({"type": "capture", "image": capture_screen()})
    else:
        # Любая другая команда обрабатывает запрос на следующий шаг.
        get_next_step(request_data)


if __name__ == "__main__":
    # Запускаем main только при прямом запуске этого файла, а не при импорте.
    main()
