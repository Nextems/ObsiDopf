# OCR worker origin

The model mapping, OCR output structures and bounding-box helpers in src/main.rs were adapted from [Nextems/OCR_Translator](https://github.com/Nextems/OCR_Translator), `OCR-deepl/src-tauri/src/lib.rs`.

This worker contains no Tauri shell, translator, API-key storage or translation endpoints. The original repository was read only.

Protocol: one JSON request per stdin line: {"path":"absolute/path/image.png","lang":"en"}. Each completed request emits one stdout line prefixed OCR_RESULT: with {"ok":true,"result":{"text":"...","bbox":null,"regions":[...]}} or {"ok":false,"error":"..."}. Ignore other diagnostic stdout lines. Models stay loaded between requests. Closing stdin exits the worker.
