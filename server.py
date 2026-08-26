from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
from typing import List, Optional
from contextlib import asynccontextmanager
import uvicorn
import time
import asyncio
import subprocess
import sys

from pathlib import Path
from threading import Lock

from smart_translator import SmartTranslator

# Глобальный переводчик
translator: Optional[SmartTranslator] = None
translate_lock = Lock()

def check_and_install_packages():
    packages = {
        "demucs": "demucs",
        "pydantic": "pydantic",
    }
    
    missing_packages = []
    for package, import_name in packages.items():
        try:
            __import__(import_name)
        except ImportError:
            missing_packages.append(package)
    
    if missing_packages:
        print("📦 Устанавливаю необходимые библиотеки...")
        for pkg in missing_packages:
            print(f"   → {pkg}")
            subprocess.check_call([sys.executable, "-m", "pip", "install", "-q", pkg])
        print("✅ Все библиотеки установлены!\n")

@asynccontextmanager
async def lifespan(app: FastAPI):
    """Загрузка модели при старте"""
    global translator
    translator = SmartTranslator()
    yield
    # Очистка при завершении (если нужно)

app = FastAPI(
    title="Smart NLLB Translation Server",
    description="Локальный сервер перевода с автодетекцией языка",
    version="1.0.0",
    lifespan=lifespan
)

# CORS для WPF приложения
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


# ==========================================================
# Модели для перевода
# ==========================================================

class TranslateRequest(BaseModel):
    text: str
    src_lang: Optional[str] = None  # Если None - автодетекция

class TranslateResponse(BaseModel):
    translated_text: str
    detected_lang: str
    detected_lang_name: str

class BatchTranslateRequest(BaseModel):
    texts: List[str]
    src_lang: Optional[str] = None

class BatchTranslateResponse(BaseModel):
    translations: List[TranslateResponse]
    total_time: float

class DetectLanguageRequest(BaseModel):
    text: str

class DetectLanguageResponse(BaseModel):
    lang_code: str
    lang_name: str


# ==========================================================
# Модели для удаления вокала
# ==========================================================

class RemoveVocalRequest(BaseModel):
    video_path: str
    output_dir: Optional[str] = "outputs"

    # Удалить промежуточный WAV после создания MP3
    delete_wav_after_mp3: bool = True

    # Если True, сервер вернет ошибку, если CUDA недоступна
    require_gpu: bool = True


class RemoveVocalResponse(BaseModel):
    success: bool
    message: Optional[str] = None

    # Основной путь к готовому MP3
    no_vocal_mp3_path: Optional[str] = None

    # Оставил для совместимости, сюда тоже кладется путь к MP3
    instrumental_path: Optional[str] = None

    vocals_path: Optional[str] = None


# ==========================================================
# Эндпоинты перевода
# ==========================================================

@app.get("/health")
async def health_check():
    """Проверка работоспособности"""
    return {
        "status": "ok",
        "model_loaded": translator is not None,
        "device": "GPU" if translator and getattr(translator, "device", None) == 0 else "CPU",
    }

def _translate_one(text: str, src_lang: Optional[str]) -> TranslateResponse:
    detected_lang, detected_lang_name = translator.detect_language(text)
    translated = translator.translate(text, src_lang)

    return TranslateResponse(
        translated_text=translated,
        detected_lang=detected_lang,
        detected_lang_name=detected_lang_name
    )


@app.post("/translate", response_model=TranslateResponse)
def translate(request: TranslateRequest):
    """Перевод одного текста"""
    if not translator:
        raise HTTPException(status_code=503, detail="Модель не загружена")

    if not request.text.strip():
        raise HTTPException(status_code=400, detail="Текст не может быть пустым")

    try:
        start_time = time.time()

        with translate_lock:
            result = _translate_one(request.text, request.src_lang)

        elapsed = time.time() - start_time
        print(
            f"✅ Перевод за {elapsed:.2f}с: "
            f"[{result.detected_lang_name}] → Русский"
        )

        return result

    except HTTPException:
        raise

    except Exception as e:
        print(f"❌ Ошибка перевода: {e}")
        raise HTTPException(status_code=500, detail=str(e))


@app.post("/translate/batch", response_model=BatchTranslateResponse)
def translate_batch(request: BatchTranslateRequest):
    """Пакетный перевод (для субтитров)"""
    if not translator:
        raise HTTPException(status_code=503, detail="Модель не загружена")

    if not request.texts:
        raise HTTPException(status_code=400, detail="Список текстов пуст")

    try:
        start_time = time.time()
        results = []

        with translate_lock:
            for i, text in enumerate(request.texts, 1):
                print(f"🔄 Перевод {i}/{len(request.texts)}...")

                # Чтобы сервер не падал на null/пустых элементах
                if text is None:
                    text = ""

                result = _translate_one(text, request.src_lang)
                results.append(result)

        total_time = time.time() - start_time

        print(
            f"✅ Пакетный перевод завершен: "
            f"{len(results)} текстов за {total_time:.2f}с"
        )

        return BatchTranslateResponse(
            translations=results,
            total_time=total_time
        )

    except HTTPException:
        raise

    except Exception as e:
        print(f"❌ Ошибка пакетного перевода: {e}")
        raise HTTPException(status_code=500, detail=str(e))


@app.post("/detect", response_model=DetectLanguageResponse)
def detect_language(request: DetectLanguageRequest):
    """Определение языка текста"""
    if not translator:
        raise HTTPException(status_code=503, detail="Модель не загружена")

    if not request.text.strip():
        raise HTTPException(status_code=400, detail="Текст не может быть пустым")

    try:
        with translate_lock:
            lang_code, lang_name = translator.detect_language(request.text)

        return DetectLanguageResponse(
            lang_code=lang_code,
            lang_name=lang_name
        )

    except HTTPException:
        raise

    except Exception as e:
        print(f"❌ Ошибка определения языка: {e}")
        raise HTTPException(status_code=500, detail=str(e))

# ==========================================================
# Удаление вокала через demucs + конвертация в MP3
# ==========================================================

vocal_lock = Lock()


def _ensure_gpu_available() -> None:
    """
    Проверяет, что доступен GPU.
    Если GPU недоступен, бросаем ошибку, чтобы демикс не уходил на CPU.
    """
    try:
        import torch

        if not torch.cuda.is_available():
            raise RuntimeError(
                "CUDA недоступна. "
                "Проверь установку PyTorch с поддержкой CUDA. "
                "Команда для проверки: python -c \"import torch; print(torch.cuda.is_available())\""
            )

        print(f"✅ GPU доступен: {torch.cuda.get_device_name(0)}")

    except ImportError:
        raise RuntimeError(
            "PyTorch не установлен. "
            "Сначала установи torch с поддержкой CUDA."
        )


def _run_vocal_remover(video_path: str, output_dir: str) -> dict:
    """
    Запускает vocal_remover.remove_vocal в отдельном потоке.

    Важно:
    vocal_remover.remove_vocal у тебя объявлен как async,
    но внутри выполняет блокирующие вызовы demucs.
    """
    from vocal_remover import vocal_remover

    return asyncio.run(vocal_remover.remove_vocal(video_path, output_dir))


def _convert_wav_to_mp3(wav_path: Path, delete_wav: bool = True) -> Path:
    """
    Конвертирует WAV в MP3 через ffmpeg.
    Возвращает полный путь к MP3.
    """
    mp3_path = wav_path.with_suffix(".mp3")

    cmd = [
        "ffmpeg",
        "-y",
        "-hide_banner",
        "-loglevel", "error",
        "-i", str(wav_path),
        "-codec:a", "libmp3lame",
        "-qscale:a", "2",
        str(mp3_path),
    ]

    process = subprocess.run(
        cmd,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="ignore",
    )

    if process.returncode != 0:
        raise RuntimeError(f"ffmpeg conversion failed: {process.stderr}")

    if not mp3_path.exists():
        raise RuntimeError("После конвертации не был создан MP3 файл")

    if delete_wav:
        try:
            wav_path.unlink(missing_ok=True)
        except Exception as e:
            print(f"⚠️ Не удалось удалить WAV файл: {e}")

    return mp3_path.resolve()


@app.post("/remove-vocal", response_model=RemoveVocalResponse, tags=["Vocal Remover"])
def remove_vocal_endpoint(request: RemoveVocalRequest):
    """
    Удаляет вокал из аудиодорожки видео через demucs.

    Возвращает полный путь к готовому файлу:
    *_no_vocal.mp3
    """

    if not request.video_path.strip():
        raise HTTPException(status_code=400, detail="video_path не может быть пустым")

    video_path = Path(request.video_path).expanduser()

    if not video_path.exists():
        raise HTTPException(
            status_code=404,
            detail=f"Видео файл не найден: {video_path}",
        )

    output_dir = Path(request.output_dir or "outputs").expanduser()
    output_dir.mkdir(parents=True, exist_ok=True)

    # Если пользователь требует только GPU, проверяем CUDA заранее
    if request.require_gpu:
        try:
            _ensure_gpu_available()
        except Exception as e:
            raise HTTPException(status_code=500, detail=str(e))

    try:
        # Lock защищает от одновременного запуска нескольких demucs
        with vocal_lock:
            result = _run_vocal_remover(str(video_path), str(output_dir))

    except Exception as e:
        raise HTTPException(
            status_code=500,
            detail=f"Ошибка при удалении вокала: {e}",
        )

    if not result.get("success"):
        raise HTTPException(
            status_code=500,
            detail=result.get("error") or result.get("message") or "Неизвестная ошибка demucs",
        )

    instrumental_wav = result.get("instrumental_path")

    if not instrumental_wav:
        raise HTTPException(
            status_code=500,
            detail="Demucs завершился, но не вернул путь к инструменталу",
        )

    wav_path = Path(instrumental_wav)

    if not wav_path.exists():
        raise HTTPException(
            status_code=500,
            detail=f"WAV файл с инструменталом не найден: {wav_path}",
        )

    try:
        mp3_path = _convert_wav_to_mp3(
            wav_path=wav_path,
            delete_wav=request.delete_wav_after_mp3,
        )
    except Exception as e:
        raise HTTPException(
            status_code=500,
            detail=f"Не удалось конвертировать WAV в MP3: {e}",
        )

    return RemoveVocalResponse(
        success=True,
        message=result.get("message"),
        no_vocal_mp3_path=str(mp3_path),
        instrumental_path=str(mp3_path),
        vocals_path=result.get("vocals_path"),
    )


# ==========================================================
# Запуск сервера
# ==========================================================

if __name__ == "__main__":
    check_and_install_packages()
    print("\n" + "=" * 60)
    print("🚀 Запуск Smart NLLB Translation Server")
    print("=" * 60 + "\n")

    uvicorn.run(
        app,
        host="0.0.0.0",
        port=8000,
        log_level="info"
    )
