import argparse
import json
import sqlite3
import sys
import threading
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from itertools import count
from pathlib import Path
from urllib import error as urlerror
from urllib import request as urlrequest


REQUEST_IDS = count(1)
LOG_PATH = Path(__file__).with_name("gemma_translate_server.log")
DB_PATH = Path(__file__).with_name("gemma_translate_cache.db")
DB_CONN = None
BYPASS_COUNTS = {}
BYPASS_LOCK = threading.Lock()
LM_CONFIG = {}
RESPONSE_SCHEMA = {
    "type": "object",
    "properties": {
        "translation": {
            "type": "string",
            "description": "Русский перевод исходного фрагмента без дополнительных комментариев.",
        },
        "source_unchanged_tokens": {
            "type": "array",
            "items": {"type": "string"},
            "description": "Список специальных токенов, которые были сохранены без изменений.",
        },
    },
    "required": ["translation", "source_unchanged_tokens"],
    "additionalProperties": False,
}
DEFAULT_SYSTEM_PROMPT = (
    "Ты профессиональный локализатор Caves of Qud (EN -> RU). "
    "Переводи художественно и точно, сохраняя стиль оригинала. "
    "Никогда не меняй плейсхолдеры и служебные маркеры: {{...}}, $variable, <spice...>, "
    "XML/JSON синтаксис, числа, escape-последовательности. "
    "Не добавляй пояснений. Отвечай строго в JSON по заданной схеме. "
    "Критично: не сокращай и не обобщай исходный текст. "
    "Сохраняй все факты, перечисления, отношения между фракциями, предметы экипировки, "
    "физические особенности, оценки отношения и весь смысл каждого предложения."
)


try:
    sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
except Exception:
    pass


def now():
    return datetime.now().strftime("%H:%M:%S")


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def compact_text(text: str, limit: int = 240) -> str:
    if text is None:
        return ""
    one_line = text.replace("\r", " ").replace("\n", "\\n")
    if len(one_line) <= limit:
        return one_line
    return one_line[:limit] + "...(truncated)"


def log_line(message: str):
    try:
        print(message, flush=True)
    except Exception:
        safe = message.encode("utf-8", errors="backslashreplace").decode("utf-8", errors="replace")
        print(safe, flush=True)
    with LOG_PATH.open("a", encoding="utf-8") as f:
        f.write(message + "\n")


def init_db():
    global DB_CONN
    DB_CONN = sqlite3.connect(str(DB_PATH), check_same_thread=False)
    DB_CONN.execute(
        """
        CREATE TABLE IF NOT EXISTS translations (
            source_lang TEXT NOT NULL,
            target_lang TEXT NOT NULL,
            source_text TEXT NOT NULL,
            translation TEXT NOT NULL,
            hit_count INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY (source_lang, target_lang, source_text)
        )
        """
    )
    DB_CONN.commit()


def db_get_translation(source_lang: str, target_lang: str, source_text: str):
    if DB_CONN is None:
        return None
    cur = DB_CONN.execute(
        "SELECT translation, hit_count FROM translations WHERE source_lang=? AND target_lang=? AND source_text=?",
        (source_lang, target_lang, source_text),
    )
    row = cur.fetchone()
    if row is None:
        return None
    DB_CONN.execute(
        "UPDATE translations SET hit_count=?, updated_at=? WHERE source_lang=? AND target_lang=? AND source_text=?",
        (int(row[1]) + 1, utc_now_iso(), source_lang, target_lang, source_text),
    )
    DB_CONN.commit()
    return row[0]


def db_put_translation(source_lang: str, target_lang: str, source_text: str, translation: str):
    if DB_CONN is None:
        return
    now_iso = utc_now_iso()
    DB_CONN.execute(
        """
        INSERT INTO translations (source_lang, target_lang, source_text, translation, hit_count, created_at, updated_at)
        VALUES (?, ?, ?, ?, 0, ?, ?)
        ON CONFLICT(source_lang, target_lang, source_text)
        DO UPDATE SET translation=excluded.translation, updated_at=excluded.updated_at
        """,
        (source_lang, target_lang, source_text, translation, now_iso, now_iso),
    )
    DB_CONN.commit()


def should_bypass_translation(text: str) -> bool:
    if not text:
        return True

    for ch in text:
        code = ord(ch)
        if code < 32 and ch not in ("\t", "\n", "\r"):
            return True
        if 0xE000 <= code <= 0xF8FF:
            return True

    stripped = text.strip()
    if not stripped:
        return True

    lowered = stripped.lower()
    if "<color=" in lowered or "</color>" in lowered:
        return True
    if "{{hotkey|" in lowered:
        return True
    if stripped.startswith("[") and stripped.endswith("]") and len(stripped) <= 24:
        return True

    letters = sum(1 for ch in stripped if ch.isalpha())
    digits = sum(1 for ch in stripped if ch.isdigit())
    alnum = letters + digits
    symbol_ratio = 1.0 - (float(alnum) / float(max(1, len(stripped))))

    if len(stripped) <= 1 and alnum == 0:
        return True
    if alnum == 0 and len(stripped) <= 4:
        return True
    if len(stripped) >= 24 and letters <= 2 and symbol_ratio >= 0.85:
        return True
    if len(stripped) > 80 and letters < 8:
        return True
    if stripped.count(".") >= 20 and letters <= 3:
        return True
    if stripped.count("■") >= 2 and letters <= 3:
        return True
    if "{{" in stripped and "}}" not in stripped:
        return True
    if "{{" in stripped and stripped.endswith("|"):
        return True

    return False


def should_log_bypass(text: str) -> bool:
    key = text if len(text) <= 120 else text[:120]
    with BYPASS_LOCK:
        hit = BYPASS_COUNTS.get(key, 0) + 1
        BYPASS_COUNTS[key] = hit
        if len(BYPASS_COUNTS) > 5000:
            BYPASS_COUNTS.clear()
        return hit == 1 or hit % 100 == 0


def parse_model_json_content(content: str):
    if not content:
        raise RuntimeError("LM Studio returned empty content")

    text = content.strip()
    if text.startswith("```"):
        first_nl = text.find("\n")
        if first_nl >= 0:
            text = text[first_nl + 1 :]
        end_fence = text.rfind("```")
        if end_fence >= 0:
            text = text[:end_fence].strip()

    parsed = None
    try:
        parsed = json.loads(text)
    except Exception:
        start = text.find("{")
        end = text.rfind("}")
        if start >= 0 and end > start:
            parsed = json.loads(text[start : end + 1])
        else:
            raise RuntimeError(f"Model did not return JSON: {compact_text(content)}")

    if not isinstance(parsed, dict):
        raise RuntimeError("Model JSON is not an object")

    translation = parsed.get("translation")
    tokens = parsed.get("source_unchanged_tokens")
    if not isinstance(translation, str):
        raise RuntimeError("Model JSON missing string field 'translation'")
    if not isinstance(tokens, list) or any(not isinstance(t, str) for t in tokens):
        raise RuntimeError("Model JSON missing string-array field 'source_unchanged_tokens'")

    return translation.strip(), tokens


def call_lmstudio_chat(text: str, source_lang: str, target_lang: str):
    base_url = LM_CONFIG["base_url"].rstrip("/")
    url = base_url + "/chat/completions"

    payload = {
        "model": LM_CONFIG["model"],
        "messages": [
            {"role": "system", "content": LM_CONFIG["system_prompt"]},
            {"role": "user", "content": text},
        ],
        "temperature": 0,
        "max_tokens": LM_CONFIG["max_tokens"],
        "response_format": {
            "type": "json_schema",
            "json_schema": {
                "name": "coq_translation_response",
                "schema": RESPONSE_SCHEMA,
                "strict": True,
            },
        },
    }
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")

    headers = {"Content-Type": "application/json"}
    if LM_CONFIG["api_key"]:
        headers["Authorization"] = f"Bearer {LM_CONFIG['api_key']}"

    req = urlrequest.Request(url=url, data=data, headers=headers, method="POST")
    try:
        with urlrequest.urlopen(req, timeout=LM_CONFIG["timeout_sec"]) as resp:
            raw = resp.read().decode("utf-8", errors="replace")
    except urlerror.HTTPError as e:
        body = e.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"LM Studio HTTP {e.code}: {body}") from e
    except urlerror.URLError as e:
        raise RuntimeError(f"LM Studio connection error: {e}") from e

    parsed = json.loads(raw)
    choices = parsed.get("choices") or []
    if not choices:
        raise RuntimeError("LM Studio response has no choices")

    msg = choices[0].get("message") or {}
    content = (msg.get("content") or "").strip()
    translation, tokens = parse_model_json_content(content)
    return translation, tokens


class TranslateHandler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path != "/translate":
            self.send_error(404, "Not found")
            return

        content_length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(content_length)

        try:
            try:
                decoded = body.decode("utf-8")
            except UnicodeDecodeError:
                decoded = body.decode("cp1251", errors="replace")

            payload = json.loads(decoded)
            text = (payload.get("text") or "").strip()
            source_lang = (payload.get("source_lang") or "en").strip()
            target_lang = (payload.get("target_lang") or "ru").strip()
            request_id = next(REQUEST_IDS)

            if not text:
                self._send_json(400, {"error": "text is required"})
                return

            if should_bypass_translation(text):
                if should_log_bypass(text):
                    log_line(f"[{now()}] [req:{request_id}] in {source_lang}->{target_lang}: \"{compact_text(text)}\"")
                    log_line(f"[{now()}] [req:{request_id}] bypass: \"{compact_text(text)}\"")
                self._send_json(
                    200,
                    {
                        "translation": text,
                        "source_unchanged_tokens": [],
                        "cached": True,
                        "bypass": True,
                    },
                )
                return

            log_line(f"[{now()}] [req:{request_id}] in {source_lang}->{target_lang}: \"{compact_text(text)}\"")
            cached = db_get_translation(source_lang, target_lang, text)
            if cached is not None:
                log_line(f"[{now()}] [req:{request_id}] cache-hit: \"{compact_text(cached)}\"")
                self._send_json(
                    200,
                    {"translation": cached, "source_unchanged_tokens": [], "cached": True},
                )
                return

            translated, unchanged_tokens = call_lmstudio_chat(text, source_lang, target_lang)
            db_put_translation(source_lang, target_lang, text, translated)
            log_line(f"[{now()}] [req:{request_id}] out: \"{compact_text(translated)}\"")
            self._send_json(
                200,
                {
                    "translation": translated,
                    "source_unchanged_tokens": unchanged_tokens,
                    "cached": False,
                },
            )
        except Exception as exc:
            log_line(f"[{now()}] [error] {repr(exc)}")
            self._send_json(500, {"error": str(exc)})

    def do_GET(self):
        if self.path == "/health":
            self._send_json(
                200,
                {
                    "status": "ok",
                    "engine": "gemma-chat",
                    "model": LM_CONFIG.get("model"),
                    "base_url": LM_CONFIG.get("base_url"),
                },
            )
            return
        self.send_error(404, "Not found")

    def log_message(self, fmt, *args):
        return

    def _send_json(self, status: int, payload: dict):
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5007)
    parser.add_argument("--base-url", default="http://127.0.0.1:1234/v1")
    parser.add_argument("--model", default="google/gemma-4-e4b")
    parser.add_argument("--api-key", default="")
    parser.add_argument("--timeout-sec", type=int, default=60)
    parser.add_argument("--max-tokens", type=int, default=512)
    parser.add_argument("--system-prompt", default=DEFAULT_SYSTEM_PROMPT)
    args = parser.parse_args()

    init_db()
    LM_CONFIG["base_url"] = args.base_url
    LM_CONFIG["model"] = args.model
    LM_CONFIG["api_key"] = args.api_key
    LM_CONFIG["timeout_sec"] = args.timeout_sec
    LM_CONFIG["max_tokens"] = args.max_tokens
    LM_CONFIG["system_prompt"] = args.system_prompt

    log_line(f"[{now()}] [gemma-server] sqlite cache: {DB_PATH}")
    log_line(f"[{now()}] [gemma-server] backend: {args.base_url}")
    log_line(f"[{now()}] [gemma-server] model: {args.model}")
    log_line(f"[{now()}] [gemma-server] ready on http://{args.host}:{args.port}")

    server = ThreadingHTTPServer((args.host, args.port), TranslateHandler)
    server.serve_forever()


if __name__ == "__main__":
    main()
