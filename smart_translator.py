from datetime import datetime
from typing import Tuple, List, Optional
import re

import torch
from transformers import pipeline as hf_pipeline, AutoTokenizer
from langdetect import detect, DetectorFactory, LangDetectException

# Фиксируем seed, чтобы langdetect был более стабильным
DetectorFactory.seed = 0

TRANSLATE_MODEL = "facebook/nllb-200-distilled-1.3B"


def print_elapsed(start_time: datetime, operation: str):
    elapsed = (datetime.now() - start_time).total_seconds()
    print(f"   ⏱️  {operation}: {elapsed:.1f}с")


def detect_repetition(text: str, threshold: float = 0.70) -> bool:
    """
    Обнаруживает патологические повторы в тексте.

    Порог специально поднят, чтобы не было ложных срабатываний
    на нормальных переводах.
    """
    text = text.strip()

    if len(text) < 30:
        return False

    words = text.lower().split()
    if len(words) < 12:
        return False

    # 1) Явные циклические повторы подряд:
    # например: "a b c a b c a b c a b c"
    for n in (3, 4, 5):
        if len(words) < n * 4:
            continue

        i = 0
        while i + n <= len(words):
            pattern = tuple(words[i:i + n])
            j = i + n
            repeats = 1

            while j + n <= len(words) and tuple(words[j:j + n]) == pattern:
                repeats += 1
                j += n

            if repeats >= 4:
                return True

            i = j if repeats > 1 else i + 1

    # 2) Слишком мало уникальных биграмм/триграмм.
    for n in (2, 3):
        ngrams = [tuple(words[i:i + n]) for i in range(len(words) - n + 1)]
        if len(ngrams) < 12:
            continue

        repetition_ratio = 1.0 - (len(set(ngrams)) / len(ngrams))
        if repetition_ratio > threshold:
            return True

    return False


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

    UKRAINIAN_CHARS = set('ґєії')
    BELARUSIAN_CHARS = set('ў')

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

        self.tokenizer = AutoTokenizer.from_pretrained(model_name)

        self.translator = hf_pipeline(
            "translation",
            model=model_name,
            tokenizer=self.tokenizer,
            device=self.device,
            torch_dtype=torch.float16 if self.device == 0 else torch.float32,
            clean_up_tokenization_spaces=True,
        )

        print_elapsed(t0, "загрузка модели")

        # Аккуратно определяем максимальную длину последовательности.
        # Для NLLB чаще всего безопасно ориентироваться на 512 токенов.
        cfg = self.translator.model.config
        config_max = (
            getattr(cfg, "max_length", None)
            or getattr(cfg, "max_position_embeddings", None)
            or 512
        )

        try:
            config_max = int(config_max)
        except Exception:
            config_max = 512

        self.max_seq_len = max(128, min(512, config_max))

        # Резервируем токены под ответ, чтобы не обрезать перевод.
        self.reserved_output_tokens = min(192, max(32, self.max_seq_len // 3))
        self.max_input_tokens = max(32, self.max_seq_len - self.reserved_output_tokens)

        self.lang_cache = {}

        print(
            "✅ Переводчик готов "
            f"(max_seq_len={self.max_seq_len}, "
            f"max_input_tokens={self.max_input_tokens})\n"
        )

    def _token_len(self, text: str) -> int:
        """Возвращает приблизительное количество токенов."""
        if not text:
            return 0

        try:
            return len(self.tokenizer(text, add_special_tokens=True).input_ids)
        except Exception:
            # Грубый fallback на случай проблем с токенизатором.
            return max(1, len(text) // 3)

    def detect_language(self, text: str) -> Tuple[str, str]:
        """
        Определяет язык текста.
        Возвращает (nllb_code, название_на_русском).
        """
        key = text[:200].strip().lower()
        cached = self.lang_cache.get(key)
        if cached:
            return cached

        stripped = text.strip()
        if not stripped or not any(c.isalpha() for c in stripped):
            result = ('eng_Latn', 'Английский')
            self.lang_cache[key] = result
            return result

        sample = stripped[:1000]
        low = sample.lower()
        clean = ''.join(c for c in sample if c.isprintable() or c.isspace())

        alpha_count = sum(1 for c in sample if c.isalpha())
        cyr_count = sum(1 for c in sample if c in self.CYRILLIC)

        detected_iso: Optional[str] = None
        nllb_code: Optional[str] = None

        if len(clean.strip()) >= 12:
            try:
                detected_iso = detect(clean)
                if detected_iso:
                    # langdetect может вернуть что-то вроде zh-cn
                    detected_iso = detected_iso.split('-')[0].lower()
                    nllb_code = self.LANG_MAP.get(detected_iso)
            except (LangDetectException, Exception) as e:
                print(f"⚠️  Фолбэк детекции для «{stripped[:30]}»: {e}")
                detected_iso = None
                nllb_code = None

        # Дополнительные признаки для кириллицы.
        ukr_chars = sum(1 for c in low if c in self.UKRAINIAN_CHARS)

        if ukr_chars >= 2 or (ukr_chars > 0 and detected_iso in {'uk', 'ru'}):
            nllb_code = 'ukr_Cyrl'
        elif any(c in low for c in self.BELARUSIAN_CHARS):
            nllb_code = 'bel_Cyrl'
        elif not nllb_code:
            # Если langdetect не справился, а текст похож на кириллицу.
            if cyr_count > 0 and cyr_count / max(1, alpha_count) >= 0.35:
                nllb_code = 'rus_Cyrl'
            else:
                nllb_code = 'eng_Latn'

        lang_name = self.LANG_NAMES_RU.get(nllb_code, 'Неизвестный')
        result = (nllb_code, lang_name)
        self.lang_cache[key] = result
        return result

    def _normalize_lang(self, lang: Optional[str], default: str) -> str:
        """
        Приводит язык из запроса к NLLB-коду.
        Поддерживает варианты:
        - en
        - eng_Latn
        - EN
        - en-US
        """
        if not lang:
            return default

        lang = lang.strip()

        if lang in self.LANG_NAMES_RU:
            return lang

        key = lang.lower()

        if key in self.LANG_MAP:
            return self.LANG_MAP[key]

        key = key.replace('-', '_')

        if key in self.LANG_MAP:
            return self.LANG_MAP[key]

        short = key.split('_')[0].split('-')[0]
        return self.LANG_MAP.get(short, default)

    def _split_line(self, line: str) -> List[str]:
        """
        Разбивает длинную строку на части, чтобы каждая часть
        помещалась в лимит токенов модели.
        """
        line = line.strip()
        if not line:
            return []

        if self._token_len(line) <= self.max_input_tokens:
            return [line]

        # Сначала пробуем резать по предложениям.
        sentences = re.split(r'(?<=[.!?…])\s+', line)

        chunks: List[str] = []
        cur = ""

        for sent in sentences:
            sent = sent.strip()
            if not sent:
                continue

            candidate = f"{cur} {sent}".strip() if cur else sent

            if self._token_len(candidate) <= self.max_input_tokens:
                cur = candidate
                continue

            if cur:
                chunks.append(cur)
                cur = ""

            if self._token_len(sent) <= self.max_input_tokens:
                cur = sent
            else:
                # Если одно предложение слишком длинное, режем по словам.
                words = sent.split()
                cur = ""

                for word in words:
                    candidate = f"{cur} {word}".strip() if cur else word

                    if self._token_len(candidate) <= self.max_input_tokens:
                        cur = candidate
                    else:
                        if cur:
                            chunks.append(cur)
                            cur = ""

                        # Если одно слово слишком длинное, режем его посимвольно.
                        if self._token_len(word) > self.max_input_tokens:
                            step = 150
                            for i in range(0, len(word), step):
                                chunks.append(word[i:i + step])
                        else:
                            cur = word

                if cur:
                    chunks.append(cur)
                    cur = ""

        if cur:
            chunks.append(cur)

        return chunks or [line]

    def _run_translation(
        self,
        text: str,
        src_lang: str,
        max_new_tokens: int,
        **overrides
    ) -> str:
        """
        Вызывает pipeline перевода с безопасными параметрами.
        """
        kwargs = dict(
            src_lang=src_lang,
            tgt_lang="rus_Cyrl",
            max_new_tokens=max_new_tokens,
            num_beams=4,
            early_stopping=False,
            do_sample=False,
            length_penalty=1.0,
            repetition_penalty=1.0,
            no_repeat_ngram_size=0,
        )

        forced_bos = None
        lang_code_to_id = getattr(self.tokenizer, "lang_code_to_id", None)
        if isinstance(lang_code_to_id, dict):
            forced_bos = lang_code_to_id.get("rus_Cyrl")

        if forced_bos is not None:
            kwargs["forced_bos_token_id"] = forced_bos

        kwargs.update(overrides)

        # Дополнительная страховка для некоторых версий tokenizer/pipeline.
        try:
            self.translator.tokenizer.src_lang = src_lang
        except Exception:
            pass

        out = self.translator(text, **kwargs)
        return out[0]["translation_text"].strip()

    def _looks_too_short(self, src: str, dst: str, src_lang: str) -> bool:
        """
        Эвристика: если вход длинный, а выход подозрительно короткий,
        возможно, перевод обрезался.
        Для CJK/тайского сравнение по словам некорректно.
        """
        if src_lang in {"zho_Hans", "jpn_Jpan", "kor_Hang", "tha_Thai"}:
            return False

        src_words = len(src.split())
        dst_words = len(dst.split())

        if src_words < 8:
            return False

        return dst_words < max(2, int(src_words * 0.35))

    def _translate_chunk(self, chunk: str, src_lang: str) -> str:
        """Переводит один небольшой фрагмент текста."""
        if not chunk.strip():
            return chunk

        input_len = self._token_len(chunk)

        # Если вход слишком длинный, аккуратно обрезаем его токенизатором.
        if input_len >= self.max_seq_len - 24:
            encoded = self.tokenizer(
                chunk,
                truncation=True,
                max_length=self.max_seq_len - 24
            )
            chunk = self.tokenizer.decode(
                encoded.input_ids,
                skip_special_tokens=True
            )
            input_len = len(encoded.input_ids)

        max_allowed_output = self.max_seq_len - input_len - 2

        if max_allowed_output < 24:
            encoded = self.tokenizer(
                chunk,
                truncation=True,
                max_length=self.max_seq_len - 26
            )
            chunk = self.tokenizer.decode(
                encoded.input_ids,
                skip_special_tokens=True
            )
            input_len = len(encoded.input_ids)
            max_allowed_output = self.max_seq_len - input_len - 2

        desired_tokens = int(input_len * 2.0) + 32
        max_new_tokens = max(24, min(max_allowed_output, desired_tokens))

        try:
            result = self._run_translation(chunk, src_lang, max_new_tokens)

            # Если получилось пусто, пробуем более простой режим.
            if not result.strip():
                result = self._run_translation(
                    chunk,
                    src_lang,
                    max_new_tokens,
                    num_beams=2,
                    repetition_penalty=1.0,
                    no_repeat_ngram_size=0
                )

            # Если есть патологические повторы, пробуем мягко наказать повторы.
            if detect_repetition(result):
                retry = self._run_translation(
                    chunk,
                    src_lang,
                    max_new_tokens,
                    num_beams=5,
                    repetition_penalty=1.25,
                    no_repeat_ngram_size=3
                )

                if retry.strip() and not detect_repetition(retry):
                    result = retry

            # Если перевод подозрительно короткий, пробуем чуть более длинный вывод.
            if result.strip() and self._looks_too_short(chunk, result, src_lang):
                retry = self._run_translation(
                    chunk,
                    src_lang,
                    max_new_tokens,
                    length_penalty=1.2,
                    num_beams=5
                )

                if retry.strip() and not self._looks_too_short(chunk, retry, src_lang):
                    result = retry

            return result.strip() or chunk

        except Exception as e:
            print(f"⚠️  Ошибка перевода чанка: {e}")
            return chunk

    def translate(self, text: str, src_lang_override: str = None) -> str:
        """
        Переводит текст на русский язык.
        """
        if text is None:
            return ""

        if not text.strip():
            return text

        # Если букв нет, переводить обычно нечего.
        if not any(c.isalpha() for c in text.strip()):
            return text

        detected_src, _ = self.detect_language(text)
        src_lang = self._normalize_lang(src_lang_override, detected_src)

        if src_lang == 'rus_Cyrl':
            return text

        lines = text.split('\n')
        translated_lines = []

        for line in lines:
            if not line.strip():
                translated_lines.append(line)
                continue

            chunks = self._split_line(line)
            translated_parts = []

            for chunk in chunks:
                translated_parts.append(self._translate_chunk(chunk, src_lang))

            translated_lines.append(
                " ".join(p.strip() for p in translated_parts if p and p.strip())
            )

        result = "\n".join(translated_lines)

        return result if result.strip() else text