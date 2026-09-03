import os, warnings, logging

warnings.filterwarnings("ignore", category=FutureWarning)
warnings.filterwarnings("ignore", category=UserWarning)
os.environ["TRANSFORMERS_VERBOSITY"] = "error"
os.environ["TOKENIZERS_PARALLELISM"] = "false"
logging.getLogger("transformers").setLevel(logging.ERROR)


def translate_to_ru(text: str, verbose: bool = True):
    from smart_translator import SmartTranslator

    if not hasattr(translate_to_ru, "translator"):
        # Подавляем stdout тоже, чтобы "🌍 Инициализация..." не мешало
        import sys, io
        _stdout = sys.stdout
        sys.stdout = io.StringIO()
        try:
            translate_to_ru.translator = SmartTranslator()
        finally:
            sys.stdout = _stdout

    tr = translate_to_ru.translator
    lang_code, lang_name = tr.detect_language(text)
    translated_text = tr.translate(text)

    if verbose:
        print(f"🌐 Язык: {lang_name} ({lang_code})")
        print(f"🇷🇺 Перевод: {translated_text}")

    return {
        "lang_code": lang_code,
        "lang_name": lang_name,
        "translated_text": translated_text,
    }

def divide_time(time_str: str, divisor: int) -> str:
    # Разбираем строку на часы, минуты и секунды
    hours, minutes, seconds = map(int, time_str.split(':'))
    
    # Переводим всё время в общие секунды
    total_seconds = hours * 3600 + minutes * 60 + seconds
    
    # Делим секунды на число (используем целочисленное деление //)
    result_seconds = total_seconds // divisor
    
    # Считаем новые часы, минуты и секунды
    new_hours = result_seconds // 3600
    new_minutes = (result_seconds % 3600) // 60
    new_seconds = result_seconds % 60
    
    # Возвращаем строку с ведущими нулями
    return f"{new_hours:02d}:{new_minutes:02d}:{new_seconds:02d}"

# Пример использования:
print(divide_time("00:24:14", 3))  # Выведет: 00:05:25
