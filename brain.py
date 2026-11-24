# Этот файл не рисует окно. Его запускает C#-программа.
# Здесь две работы: снять экран и спросить ИИ, какой шаг сделать дальше.
# C# присылает одну JSON-команду в стандартный ввод и читает JSON-ответ
# из стандартного вывода. Одна напечатанная строка — одно событие.

# base64 превращает байты картинки в обычный текст для JSON.
import base64
# json читает команду от C# и печатает ответ.
import json
# platform сообщает версию Windows. ИИ учитывает её в подсказке.
import platform
# sys — это стандартный ввод, вывод и ошибки.
import sys
# uuid делает уникальный номер запроса, чтобы сервис не склеил повторы.
import uuid
# winreg читает выбранные в Windows приложения по умолчанию.
import winreg
# BytesIO — картинка в памяти. Временный файл на диске не нужен.
from io import BytesIO
# Request и urlopen отправляют запрос в интернет без дополнительных библиотек.
from urllib.request import Request, urlopen

# ImageGrab из Pillow умеет снять экран Windows.
from PIL import ImageGrab

# Ключ доступа лежит отдельно, чтобы его было легко заменить и не публиковать.
from AIKey import API_KEY

# Windows часто открывает консоль не в UTF-8. Тогда русский текст портится.
# reconfigure есть у обычной консоли; в редких средах его может не быть.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")

# Имя модели и адрес сервиса LimitDeck. Менять их нужно сразу в двух местах:
# здесь и в описании README, чтобы документация не разошлась с кодом.
MODEL = "gpt-5.6-sol"
API_URL = "https://limitdeckai.ru/v1/chat/completions"

# Это постоянная инструкция для модели. Её пользователь не видит.
# STEP: значит «вот следующий шаг». DONE: значит «задача уже выполнена».
# C# потом стирает эти служебные слова и показывает только сам текст.
SYSTEM_PROMPT = """Ты помогаешь выполнить задачу в Windows.
Определи по снимку активное приложение и текущий этап задачи. Учитывай историю и подтверждённые шаги, чтобы не просить повторять сделанное. Учитывай сведения о Windows и приложениях по умолчанию ниже. Если снимок не совпадает с историей, доверяй снимку. Если задача зависит от открытого окна, ориентируйся на снимок, а не на список программ.
Не выдумывай настройки и не утверждай, что окно открыто, если этого не видно.
Давай только одно ближайшее действие за раз. Если нужно открыть контекстное окно, сразу скажи, что в нём сделать. Иначе оно закроется. К таким окнам относятся: Пуск, меню Wi-Fi, Bluetooth, громкость, меню правой кнопки и другие окна, которые закрываются при нажатии снаружи. Назови видимый элемент или попроси открыть нужное окно.
Не перечисляй будущие действия и варианты. Не добавляй вступление и нумерацию. Если безопасно выбрать действие нельзя, задай один короткий вопрос. Если нужно открыть окно и сделать действие, сразу напиши, что сделать. Не открывай файлы внутри папки OrdinaTuteur.
После подтверждения шага проверь новый снимок экрана.
Если на снимке цель уже достигнута, не предлагай следующий шаг. Например, если просили открыть Spotify и его окно видно, ответь только: DONE: Spotify открыт.
Если цель ещё не достигнута, продолжай одним ближайшим действием.
Если задача выполнена, ответь только DONE: и коротким подтверждением.
Иначе ответь только STEP: и одной инструкцией."""


def read_registry(path, name):
    """Читает одну настройку текущего пользователя. Если её нет — «не задано»."""
    try:
        # OpenKey открывает раздел реестра. with сам закрывает его после чтения.
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as key:
            # QueryValueEx возвращает пару: значение и его тип. Берём значение.
            value = winreg.QueryValueEx(key, name)[0]
            # Пустая строка для ИИ бесполезна, поэтому заменяем её текстом.
            return value or "не задано"
    except OSError:
        # Раздела может не быть, если пользователь ещё не выбирал программу.
        return "не задано"


def windows_info():
    """Коротко описывает Windows и три программы по умолчанию.

    Полный список установленных программ сознательно не собирается:
    ИИ выбирает действие по снимку, а эти три пункта только подсказывают,
    чем пользователь обычно открывает сайты, почту и PDF.
    """
    return {
        "windows": platform.system() + " " + platform.release() + " (" + platform.machine() + ")",
        "default_apps": {
            # ProgId — внутреннее имя программы, которую Windows выбрала сама.
            "браузер": read_registry(
                r"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice",
                "ProgId",
            ),
            "почта": read_registry(
                r"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\mailto\UserChoice",
                "ProgId",
            ),
            "PDF": read_registry(
                r"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice",
                "ProgId",
            ),
        },
    }


def send(event):
    """Печатает одну строку JSON. C# читает ответ построчно.

    ensure_ascii=False оставляет русские буквы читаемыми.
    flush=True отправляет строку сразу, не держа её в буфере.
    """
    print(json.dumps(event, ensure_ascii=False), flush=True)


def screenshot():
    """Снимает основной экран и возвращает уменьшенную картинку текстом."""
    # convert("RGB") нужен потому, что JPEG не хранит прозрачность.
    image = ImageGrab.grab().convert("RGB")
    # Меньшая картинка быстрее уходит в интернет и дешевле для запроса.
    image.thumbnail((800, 500))

    # Сохраняем JPEG в память, а не в файл рядом с программой.
    buffer = BytesIO()
    image.save(buffer, format="JPEG", quality=70)
    # Base64 — это текст из букв и цифр. Так картинку можно положить в JSON.
    text = base64.b64encode(buffer.getvalue()).decode("ascii")
    # Префикс говорит сервису ИИ, что это JPEG, а не просто набор букв.
    return "data:image/jpeg;base64," + text


def ask_ai(messages):
    """Отправляет переписку и снимок в LimitDeck и возвращает текст ответа.

    Ответ приходит целиком. Окно показывает его сразу, без печати по буквам.
    """
    # Сведения о компьютере дописываем к инструкции, а не к каждому шагу.
    prompt = SYSTEM_PROMPT + "\n\nКомпьютер пользователя:\n"
    prompt += json.dumps(windows_info(), ensure_ascii=False)

    body = {
        "model": MODEL,
        # Первое сообщение — правила. Дальше идёт история и новый снимок.
        "messages": [{"role": "system", "content": prompt}] + messages,
        # Короткий предел: модели нужно назвать одно действие, а не план.
        "max_tokens": 96,
    }
    request = Request(
        API_URL,
        data=json.dumps(body).encode("utf-8"),
        headers={
            # Bearer — обычный способ передать ключ доступа в HTTP-запросе.
            "Authorization": "Bearer " + API_KEY,
            "Content-Type": "application/json",
            # Новый ключ на каждый запрос, чтобы повтор не считался тем же самым.
            "Idempotency-Key": str(uuid.uuid4()),
        },
    )

    # Если сервис молчит дольше 90 секунд, urlopen сам бросит ошибку.
    # C# увидит ненулевой код выхода и предложит повторить запрос.
    with urlopen(request, timeout=90) as response:
        result = json.loads(response.read().decode("utf-8"))

    # Берём текст первого варианта ответа. Другие поля сервиса окну не нужны.
    return read_text(result["choices"][0]["message"])


def read_text(message):
    """Достаёт текст из ответа сервиса.

    Обычно content — готовая строка. Иногда сервис присылает список частей,
    и текст лежит в поле text у каждой части.
    """
    content = message.get("content")
    if isinstance(content, str):
        return content.strip()
    if isinstance(content, list):
        parts = []
        for part in content:
            if isinstance(part, dict) and isinstance(part.get("text"), str):
                parts.append(part["text"])
        return "".join(parts).strip()
    # Пустой или неожиданный ответ лучше отдать как пустую строку.
    # C# сочтёт такой результат ошибкой и не покажет его пользователю.
    return ""


def clean_answer(answer):
    """Убирает STEP: или DONE: и сообщает, закончена ли задача.

    Сравнение идёт по верхнему регистру, чтобы «done:» тоже подошёл.
    Срез [5:] выкидывает само слово и двоеточие: у обоих маркеров длина 5.
    Если маркера нет, считаем ответ обычным шагом и показываем как есть.
    """
    text = answer.strip()
    upper = text.upper()
    if upper.startswith("DONE:"):
        return True, text[5:].strip()
    if upper.startswith("STEP:"):
        return False, text[5:].strip()
    return False, text


def next_step(data):
    """Собирает задачу, историю и снимок, затем спрашивает ИИ."""
    # История уже хранит role и content. Просто перекладываем их в запрос.
    messages = []
    for turn in data["history"]:
        messages.append({"role": turn["role"], "content": turn["content"]})

    # Задачу и сделанные шаги кладём в последнее сообщение вместе со снимком.
    # Так модель видит и текст, и картинку в одном месте.
    task = json.dumps({
        "task": data["question"],
        "completed_steps": data["completed_steps"],
    }, ensure_ascii=False)

    messages.append({
        "role": "user",
        "content": [
            {"type": "text", "text": task},
            {"type": "image_url", "image_url": {"url": data["image"]}},
        ],
    })

    # previous_answer есть только у кнопки «Переделать ответ».
    # Снимок при этом тот же: C# не делает новый capture.
    previous = data.get("previous_answer")
    if previous:
        messages.append({"role": "assistant", "content": previous})
        messages.append({
            "role": "user",
            "content": (
                "Пользователя что-то не устроило - проверь внимательнее. "
                "Повторно сверь предыдущую рекомендацию с тем же снимком экрана. "
                "Исправь ответ, если нужного элемента на экране нет."
            ),
        })

    done, instruction = clean_answer(ask_ai(messages))
    # type=result — сигнал C#, что это итог, а не снимок экрана.
    send({"type": "result", "done": done, "instruction": instruction})


def main():
    """Читает одну команду от C# и выполняет её.

    action=capture только снимает экран. Любая другая команда — запрос шага.
    json.load ждёт, пока C# закроет ввод, и затем разбирает весь JSON.
    """
    data = json.load(sys.stdin)
    if data.get("action") == "capture":
        send({"type": "capture", "image": screenshot()})
    else:
        next_step(data)


# Этот блок выполняется, когда файл запустили напрямую, а не импортировали.
if __name__ == "__main__":
    main()
