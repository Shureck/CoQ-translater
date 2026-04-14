import argparse
import json
import sqlite3
import sys
import threading
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from itertools import count
from pathlib import Path

from transformers import pipeline


def build_translator(model_name: str):
    return pipeline("text2text-generation", model=model_name, tokenizer=model_name)


REQUEST_IDS = count(1)
LOG_PATH = Path(__file__).with_name("hf_translate_server.log")
DB_PATH = Path(__file__).with_name("hf_translate_cache.db")
DB_CONN = None
BYPASS_COUNTS = {}
BYPASS_LOCK = threading.Lock()

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
except Exception:
    pass


def now():
    return datetime.now().strftime("%H:%M:%S")


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
        # Fallback for terminals with non-UTF-8 codepages.
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
        (int(row[1]) + 1, datetime.utcnow().isoformat(), source_lang, target_lang, source_text),
    )
    DB_CONN.commit()
    return row[0]


def db_put_translation(source_lang: str, target_lang: str, source_text: str, translation: str):
    if DB_CONN is None:
        return
    now_iso = datetime.utcnow().isoformat()
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
    # Skip control and private-use glyphs (UI icons / key symbols).
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
    if "{{hotkey|" in stripped.lower():
        return True
    if len(stripped) <= 1:
        if not any(ch.isalpha() or ch.isdigit() for ch in stripped):
            return True
    if all((not ch.isalpha() and not ch.isdigit()) for ch in stripped) and len(stripped) <= 4:
        return True
    letters = sum(1 for ch in stripped if ch.isalpha())
    digits = sum(1 for ch in stripped if ch.isdigit())
    alnum = letters + digits
    symbol_ratio = 1.0 - (float(alnum) / float(max(1, len(stripped))))

    # Drop decorative/ASCII-art lines and fragment markup.
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
    # Preserve key hints like [Esc], [Space] as-is.
    if stripped.startswith("[") and stripped.endswith("]") and len(stripped) <= 24:
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


class TranslateHandler(BaseHTTPRequestHandler):
    translator = None

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
                # Some clients on Windows may send ANSI payloads.
                decoded = body.decode("cp1251", errors="replace")
            payload = json.loads(decoded)
            text = (payload.get("text") or "").strip()
            source_lang = (payload.get("source_lang") or "en").strip()
            target_lang = (payload.get("target_lang") or "ru").strip()
            request_id = next(REQUEST_IDS)

            if not text:
                log_line(f"[{now()}] [req:{request_id}] empty text, rejected")
                self._send_json(400, {"error": "text is required"})
                return

            if should_bypass_translation(text):
                if should_log_bypass(text):
                    log_line(f"[{now()}] [req:{request_id}] in {source_lang}->{target_lang}: \"{compact_text(text)}\"")
                    log_line(f"[{now()}] [req:{request_id}] bypass: \"{compact_text(text)}\"")
                self._send_json(200, {"translation": text, "cached": True, "bypass": True})
                return

            log_line(f"[{now()}] [req:{request_id}] in {source_lang}->{target_lang}: \"{compact_text(text)}\"")
            cached = db_get_translation(source_lang, target_lang, text)
            if cached is not None:
                log_line(f"[{now()}] [req:{request_id}] cache-hit: \"{compact_text(cached)}\"")
                self._send_json(200, {"translation": cached, "cached": True})
                return

            result = self.translator(text, max_new_tokens=512, do_sample=False)
            first = result[0] if result else {}
            translation = (first.get("generated_text") or first.get("translation_text") or "").strip()
            db_put_translation(source_lang, target_lang, text, translation)
            log_line(f"[{now()}] [req:{request_id}] out: \"{compact_text(translation)}\"")
            self._send_json(200, {"translation": translation, "cached": False})
        except Exception as exc:
            log_line(f"[{now()}] [error] {repr(exc)}")
            self._send_json(500, {"error": str(exc)})

    def do_GET(self):
        if self.path == "/health":
            self._send_json(200, {"status": "ok"})
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
    parser.add_argument("--port", type=int, default=5005)
    parser.add_argument("--model", default="Helsinki-NLP/opus-mt-en-ru")
    args = parser.parse_args()

    init_db()
    log_line(f"[{now()}] [hf-server] sqlite cache: {DB_PATH}")
    log_line(f"[{now()}] [hf-server] loading model: {args.model}")
    TranslateHandler.translator = build_translator(args.model)
    log_line(f"[{now()}] [hf-server] ready on http://{args.host}:{args.port}")

    server = ThreadingHTTPServer((args.host, args.port), TranslateHandler)
    server.serve_forever()


if __name__ == "__main__":
    main()
