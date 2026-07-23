from datetime import datetime
from typing import Tuple
import torch
from transformers import pipeline as hf_pipeline
from langdetect import detect, LangDetectException

# Вспомогательные функции
def print_elapsed(start_time: datetime, operation: str):
    elapsed = (datetime.now() - start_time).total_seconds()
    print(f"   ⏱️  {operation}: {elapsed:.1f}с")

def detect_repetition(text: str, threshold: float = 0.3) -> bool:
    """
    Обнаруживает патологические повторы в тексте.
    Возвращает True, если обнаружены повторяющиеся паттерны.
    """
    if len(text) < 20:
        return False
    
    words = text.lower().split()
    if len(words) < 4:
        return False
    
    # Проверяем повтор 2-грамм и 3-грамм
    for n in [2, 3]:
        ngrams = [tuple(words[i:i+n]) for i in range(len(words)-n+1)]
        if len(ngrams) < 2:
            continue
        
        unique_ngrams = set(ngrams)
        repetition_ratio = 1 - (len(unique_ngrams) / len(ngrams))
        
        if repetition_ratio > threshold:
            return True
    
    return False

TRANSLATE_MODEL = "facebook/nllb-200-distilled-1.3B"

class SmartTranslator:
    """NLLB-200 переводчик с автодетекцией языка."""

    LANG_MAP = {
        'en': 'eng_Latn', 'ru': 'rus_Cyrl', 'de': 'deu_Latn', 'fr': 'fra_Latn',
        'es': 'spa_Latn', 'it': 'ita_Latn', 'pt': 'por_Latn', 'nl': 'nld_Latn',
        'pl': 'pol_Latn', 'uk': 'ukr_Cyrl', 'be': 'bel_Cyrl', 'bg': 'bul_Cyrl',
        'sr': 'srp_Cyrl', 'mk': 'mkd_Cyrl', 'cs': 'ces_Latn', 'tr': 'tur_Latn',
        'ar': 'arb_Arab', 'zh': 'zho_Hans', 'ja': 'jpn_Jpan', 'ko': 'kor_Hang',
        'hi': 'hin_Deva', 'bn': 'ben_Beng', 'fa': 'pes_Arab', 'he': 'heb_Hebr',
        'vi': 'vie_Latn', 'th': 'tha_Thai', 'id': 'ind_Latn', 'ms': 'zsm_Latn',
        'el': 'ell_Grek', 'ro': 'ron_Latn', 'hu': 'hun_Latn', 'sv': 'swe_Latn',
        'fi': 'fin_Latn', 'da': 'dan_Latn', 'no': 'nob_Latn', 'hr': 'hrv_Latn',
        'sk': 'slk_Latn', 'sl': 'slv_Latn',
    }

    LANG_NAMES_RU = {
        'eng_Latn': 'Английский',    'rus_Cyrl': 'Русский',
        'deu_Latn': 'Немецкий',      'fra_Latn': 'Французский',
        'spa_Latn': 'Испанский',     'ita_Latn': 'Итальянский',
        'por_Latn': 'Португальский', 'nld_Latn': 'Нидерландский',
        'pol_Latn': 'Польский',      'ukr_Cyrl': 'Украинский',
        'bel_Cyrl': 'Белорусский',   'bul_Cyrl': 'Болгарский',
        'srp_Cyrl': 'Сербский',      'mkd_Cyrl': 'Македонский',
        'ces_Latn': 'Чешский',       'tur_Latn': 'Турецкий',
        'arb_Arab': 'Арабский',      'zho_Hans': 'Китайский',
        'jpn_Jpan': 'Японский',      'kor_Hang': 'Корейский',
        'hin_Deva': 'Хинди',         'ben_Beng': 'Бенгальский',
        'pes_Arab': 'Персидский',    'heb_Hebr': 'Иврит',
        'vie_Latn': 'Вьетнамский',   'tha_Thai': 'Тайский',
        'ind_Latn': 'Индонезийский', 'zsm_Latn': 'Малайский',
        'ell_Grek': 'Греческий',     'ron_Latn': 'Румынский',
        'hun_Latn': 'Венгерский',    'swe_Latn': 'Шведский',
        'fin_Latn': 'Финский',       'dan_Latn': 'Датский',
        'nob_Latn': 'Норвежский',    'hrv_Latn': 'Хорватский',
        'slk_Latn': 'Словацкий',     'slv_Latn': 'Словенский',
    }

    CYRILLIC = set(
        'абвгдеёжзийклмнопрстуфхцчшщъыьэюя'
        'АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ'
        'ґєіїҐЄІЇўЎ'
    )

    def __init__(self, model_name: str = TRANSLATE_MODEL):
        print("\n🌍 Инициализация переводчика NLLB-200...")

        self.device = 0 if torch.cuda.is_available() else -1
        dev_name = (
            f"GPU: {torch.cuda.get_device_name(0)}"
            if self.device == 0 else "CPU"
        )
        print(f"   Устройство: {dev_name}")

        if self.device == 0:
            free_gb = torch.cuda.mem_get_info(0)[0] / 1024 ** 3
            total_gb = torch.cuda.mem_get_info(0)[1] / 1024 ** 3
            print(f"   VRAM свободно: {free_gb:.2f} / {total_gb:.2f} ГБ")

        t0 = datetime.now()
        print("📥 Загружаю модель перевода...")
        self.translator = hf_pipeline(
            "translation",
            model=model_name,
            device=self.device,
            torch_dtype=torch.float16 if self.device == 0 else torch.float32,
            clean_up_tokenization_spaces=True,
        )
        print_elapsed(t0, "загрузка модели")

        self.lang_cache = {}
        print("✅ Переводчик готов\n")

    def detect_language(self, text: str) -> Tuple[str, str]:
        """
        Определяет язык текста.
        Возвращает (nllb_code, название_на_русском).
        """
        key = text[:100].strip().lower()
        cached = self.lang_cache.get(key)
        if cached:
            return cached

        stripped = text.strip()
        if not stripped or not any(c.isalpha() for c in stripped):
            result = ('eng_Latn', 'Английский')
            self.lang_cache[key] = result
            return result

        clean = ''.join(c for c in stripped[:500] if c.isprintable() or c.isspace())

        try:
            code = detect(clean)
            nllb_code = self.LANG_MAP.get(code, 'eng_Latn')
        except (LangDetectException, Exception) as e:
            low = stripped.lower()
            if any(c in low for c in 'ґєії'):
                nllb_code = 'ukr_Cyrl'
            elif 'ў' in low:
                nllb_code = 'bel_Cyrl'
            elif any(c in self.CYRILLIC for c in stripped):
                nllb_code = 'rus_Cyrl'
            else:
                nllb_code = 'eng_Latn'
            print(f"⚠️  Фолбэк детекции для «{text[:30]}»: {e}")

        lang_name = self.LANG_NAMES_RU.get(nllb_code, 'Неизвестный')
        result = (nllb_code, lang_name)
        self.lang_cache[key] = result
        return result

    def translate(self, text: str, src_lang_override: str = None) -> str:
        """
        Переводит текст на русский язык.
        """
        if not text.strip():
            return text

        src_lang, _ = self.detect_language(text)

        if src_lang_override:
            src_lang = src_lang_override

        if src_lang == 'rus_Cyrl':
            return text

        input_text = text[:800] if len(text) > 800 else text
        input_len = len(input_text.split())
        max_length = max(50, min(500, int(input_len * 2.5)))

        def _do_translate(rep_penalty: float, ngram_size: int, beams: int) -> str:
            out = self.translator(
                input_text,
                src_lang=src_lang,
                tgt_lang='rus_Cyrl',
                max_length=max_length,
                num_beams=beams,
                repetition_penalty=rep_penalty,
                no_repeat_ngram_size=ngram_size,
                length_penalty=0.8,
                early_stopping=True,
            )
            return out[0]['translation_text'].strip()

        try:
            translated = _do_translate(rep_penalty=1.4, ngram_size=4, beams=4)

            if detect_repetition(translated):
                print(f"  ⚠️  Обнаружены повторы, повторяю перевод с усиленными параметрами...")
                translated = _do_translate(rep_penalty=2.0, ngram_size=3, beams=5)

                if detect_repetition(translated):
                    print(f"  ❌ Повторная попытка не помогла, оставляю оригинал: «{text[:60]}»")
                    return text

            return translated

        except Exception as e:
            print(f"⚠️  Ошибка перевода: {e}")
            return text
