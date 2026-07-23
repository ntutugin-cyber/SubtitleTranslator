from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
from typing import List, Optional
from contextlib import asynccontextmanager
import uvicorn
import time

from smart_translator import SmartTranslator

# Глобальный переводчик
translator: Optional[SmartTranslator] = None

def check_and_install_packages():
    packages = {
        'demucs': 'demucs',
        'pydantic': 'pydantic'
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

@app.get("/health")
async def health_check():
    """Проверка работоспособности"""
    return {
        "status": "ok",
        "model_loaded": translator is not None,
        "device": "GPU" if translator and translator.device == 0 else "CPU"
    }

@app.post("/translate", response_model=TranslateResponse)
async def translate(request: TranslateRequest):
    """Перевод одного текста"""
    if not translator:
        raise HTTPException(status_code=503, detail="Модель не загружена")
    
    if not request.text.strip():
        raise HTTPException(status_code=400, detail="Текст не может быть пустым")
    
    try:
        start_time = time.time()
        
        # Определяем язык
        detected_lang, detected_lang_name = translator.detect_language(request.text)
        
        # Переводим
        translated = translator.translate(request.text, request.src_lang)
        
        elapsed = time.time() - start_time
        print(f"✅ Перевод за {elapsed:.2f}с: [{detected_lang_name}] → Русский")
        
        return TranslateResponse(
            translated_text=translated,
            detected_lang=detected_lang,
            detected_lang_name=detected_lang_name
        )
    
    except Exception as e:
        print(f"❌ Ошибка: {e}")
        raise HTTPException(status_code=500, detail=str(e))

@app.post("/translate/batch", response_model=BatchTranslateResponse)
async def translate_batch(request: BatchTranslateRequest):
    """Пакетный перевод (для субтитров)"""
    if not translator:
        raise HTTPException(status_code=503, detail="Модель не загружена")
    
    if not request.texts:
        raise HTTPException(status_code=400, detail="Список текстов пуст")
    
    try:
        start_time = time.time()
        results = []
        
        for i, text in enumerate(request.texts, 1):
            print(f"🔄 Перевод {i}/{len(request.texts)}...")
            
            detected_lang, detected_lang_name = translator.detect_language(text)
            translated = translator.translate(text, request.src_lang)
            
            results.append(TranslateResponse(
                translated_text=translated,
                detected_lang=detected_lang,
                detected_lang_name=detected_lang_name
            ))
        
        total_time = time.time() - start_time
        print(f"✅ Пакетный перевод завершен: {len(results)} текстов за {total_time:.2f}с")
        
        return BatchTranslateResponse(
            translations=results,
            total_time=total_time
        )
    
    except Exception as e:
        print(f"❌ Ошибка пакетного перевода: {e}")
        raise HTTPException(status_code=500, detail=str(e))

@app.post("/detect", response_model=DetectLanguageResponse)
async def detect_language(request: DetectLanguageRequest):
    """Определение языка текста"""
    if not translator:
        raise HTTPException(status_code=503, detail="Модель не загружена")
    
    if not request.text.strip():
        raise HTTPException(status_code=400, detail="Текст не может быть пустым")
    
    try:
        lang_code, lang_name = translator.detect_language(request.text)
        return DetectLanguageResponse(
            lang_code=lang_code,
            lang_name=lang_name
        )
    
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))

if __name__ == "__main__":
    print("\n" + "="*60)
    print("🚀 Запуск Smart NLLB Translation Server")
    print("="*60 + "\n")
    
    uvicorn.run(
        app,
        host="0.0.0.0",
        port=8000,
        log_level="info"
    )
