import os
import torch
import demucs.separate
from pathlib import Path
import logging
import shutil
import subprocess
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

class VocalRemover:
    def __init__(self):
        if torch.cuda.is_available():
            self.device = 'cuda'
            logger.info(f"✅ Using GPU: {torch.cuda.get_device_name(0)}")
            logger.info(f"   GPU Memory: {torch.cuda.get_device_properties(0).total_memory / 1e9:.2f} GB")
        else:
            self.device = 'cpu'
            logger.warning("⚠️ CUDA not available! Using CPU")
        
        logger.info(f"Device: {self.device}")
    
    async def remove_vocal(self, input_video: str, output_dir: str) -> dict:
        """
        Удаляет вокал из видеофайла
        """
        try:
            input_path = Path(input_video)
            output_path = Path(output_dir)
            output_path.mkdir(parents=True, exist_ok=True)
            
            logger.info(f"Processing video: {input_video}")
            logger.info(f"Using device: {self.device}")
            
            # Создаем временную директорию для demucs
            temp_output = output_path / "temp_demucs"
            temp_output.mkdir(parents=True, exist_ok=True)
            
            try:
                # Используем demucs для разделения аудио
                demucs.separate.main([
                    '--two-stems', 'vocals',
                    '-n', 'htdemucs',
                    '--device', self.device,
                    '-o', str(temp_output),
                    str(input_video)
                ])
                
                # Ищем результат
                stem_dir = temp_output / 'htdemucs' / input_path.stem
                if not stem_dir.exists():
                    raise FileNotFoundError(f"Папка с результатом не найдена: {stem_dir}")
                
                logger.info(f"🔍 Ищем файлы в: {stem_dir}")
                
                # Собираем все WAV файлы
                wav_files = list(stem_dir.glob("*.wav"))
                logger.info(f" Найдено файлов: {len(wav_files)}")
                for file in wav_files:
                    logger.info(f"   - {file.name}")
                
                # Ищем инструментал и вокал
                instrumental_path = None
                vocals_path = None
                
                for file in wav_files:
                    file_name = file.name.lower()
                    # Файл с вокалом - тот, где есть ТОЛЬКО "vocals" (не "no_vocals")
                    if file_name == 'vocals.wav':
                        vocals_path = file
                        logger.info(f"🎤 Вокал: {file.name}")
                    # Инструментал - no_vocals или любой другой файл
                    elif file_name == 'no_vocals.wav':
                        instrumental_path = file
                        logger.info(f" Инструментал: {file.name}")
                
                # Если не нашли no_vocals, берем любой файл кроме vocals.wav
                if not instrumental_path:
                    for file in wav_files:
                        if file.name.lower() != 'vocals.wav':
                            instrumental_path = file
                            logger.info(f"🎵 Инструментал (альтернатива): {file.name}")
                            break
                
                if not instrumental_path:
                    raise FileNotFoundError(
                        f"Instrumental track not found! "
                        f"Available files: {[f.name for f in wav_files]}"
                    )
                
                # Копируем результаты в основную директорию
                result = {
                    'success': True,
                    'message': 'Vocal removed successfully'
                }
                
                # Копируем инструментал
                if instrumental_path:
                    final_instrumental = output_path / f"{input_path.stem}_no_vocal.wav"
                    # Проверяем, что это не один и тот же файл
                    if instrumental_path.resolve() != final_instrumental.resolve():
                        logger.info(f" Копирование {instrumental_path.name} -> {final_instrumental.name}")
                        shutil.copy2(instrumental_path, final_instrumental)
                        if not final_instrumental.exists():
                            raise RuntimeError("Не удалось скопировать инструментал")
                        file_size_mb = final_instrumental.stat().st_size / 1e6
                        logger.info(f"✅ Successfully created instrumental: {final_instrumental}")
                        logger.info(f" Размер: {file_size_mb:.2f} MB")
                        result['instrumental_path'] = str(final_instrumental)
                    else:
                        # Файл уже на месте
                        result['instrumental_path'] = str(instrumental_path.resolve())
                        logger.info(f"✅ Инструментал уже находится в нужной директории")
                
                # Копируем вокал (НОВОЕ!)
                if vocals_path:
                    final_vocals = output_path / f"{input_path.stem}_vocal.wav"
                    # Проверяем, что это не один и тот же файл
                    if vocals_path.resolve() != final_vocals.resolve():
                        logger.info(f" Копирование {vocals_path.name} -> {final_vocals.name}")
                        shutil.copy2(vocals_path, final_vocals)
                        if not final_vocals.exists():
                            raise RuntimeError("Не удалось скопировать вокал")
                        file_size_mb = final_vocals.stat().st_size / 1e6
                        logger.info(f"✅ Successfully created vocal track: {final_vocals}")
                        logger.info(f" Размер: {file_size_mb:.2f} MB")
                        result['vocals_path'] = str(final_vocals)
                    else:
                        # Файл уже на месте
                        result['vocals_path'] = str(vocals_path.resolve())
                        logger.info(f"✅ Вокал уже находится в нужной директории")
                else:
                    logger.warning("⚠️ Вокал не найден в результате разделения")
                    result['vocals_path'] = None
                
                # Очищаем временные файлы
                logger.info("️ Очистка временных файлов...")
                shutil.rmtree(temp_output, ignore_errors=True)
                
                return result
                
            except RuntimeError as e:
                error_msg = str(e).lower()
                if "backend" in error_msg or "torchaudio" in error_msg:
                    logger.error("❌ Torchaudio backend error!")
                    shutil.rmtree(temp_output, ignore_errors=True)
                    raise RuntimeError(
                        "Torchaudio не может сохранить WAV файл. "
                        "Попробуйте: pip install soundfile==0.12.1"
                    )
                else:
                    raise
                    
        except Exception as e:
            logger.error(f"Error removing vocal: {str(e)}", exc_info=True)
            return {
                'success': False,
                'error': str(e),
                'message': 'Failed to remove vocal'
            }

vocal_remover = VocalRemover()