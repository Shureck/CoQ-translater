import argparse
import json
import sqlite3
import sys
import threading
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from itertools import count
from pathlib import Path

import argostranslate.package as argos_package
import argostranslate.translate as argos_translate


REQUEST_IDS = count(1)
LOG_PATH = Path(__file__).with_name("argos_translate_server.log")
DB_PATH = Path(__file__).with_name("argos_translate_cache.db")
DB_CONN = None
BYPASS_COUNTS = {}
BYPASS_LOCK = threading.Lock()


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


def resolve_translation(source_lang: str, target_lang: str):
    installed_languages = argos_translate.get_installed_languages()
    from_lang = next((lang for lang in installed_languages if lang.code == source_lang), None)
    to_lang = next((lang for lang in installed_languages if lang.code == target_lang), None)
    if from_lang is None or to_lang is None:
        return None
    return from_lang.get_translation(to_lang)


def install_package_if_missing(source_lang: str, target_lang: str):
    if resolve_translation(source_lang, target_lang) is not None:
        return

    log_line(f"[{now()}] [argos] {source_lang}->{target_lang} package not found, trying auto-install")
    argos_package.update_package_index()
    available = argos_package.get_available_packages()
    pkg = next(
        (p for p in available if p.from_code == source_lang and p.to_code == target_lang),
        None,
    )
    if pkg is None:
        raise RuntimeError(f"Argos package {source_lang}->{target_lang} not found in package index")

    download_path = pkg.download()
    argos_package.install_from_path(download_path)
    if resolve_translation(source_lang, target_lang) is None:
        raise RuntimeError(f"Failed to install Argos package {source_lang}->{target_lang}")
    log_line(f"[{now()}] [argos] package {source_lang}->{target_lang} installed")


class TranslateHandler(BaseHTTPRequestHandler):
    source_lang = "en"
    target_lang = "ru"
    translation = None

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
            source_lang = (payload.get("source_lang") or self.source_lang).strip()
            target_lang = (payload.get("target_lang") or self.target_lang).strip()
            request_id = next(REQUEST_IDS)

            if not text:
                self._send_json(400, {"error": "text is required"})
                return

            if source_lang != self.source_lang or target_lang != self.target_lang:
                self._send_json(
                    400,
                    {
                        "error": (
                            "This server is initialized for "
                            f"{self.source_lang}->{self.target_lang} only"
                        )
                    },
                )
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

            translated = (self.translation.translate(text) or "").strip()
            if not translated:
                translated = text
            db_put_translation(source_lang, target_lang, text, translated)
            log_line(f"[{now()}] [req:{request_id}] out: \"{compact_text(translated)}\"")
            self._send_json(200, {"translation": translated, "cached": False})
        except Exception as exc:
            log_line(f"[{now()}] [error] {repr(exc)}")
            self._send_json(500, {"error": str(exc)})

    def do_GET(self):
        if self.path == "/health":
            self._send_json(
                200,
                {
                    "status": "ok",
                    "engine": "argos",
                    "source_lang": self.source_lang,
                    "target_lang": self.target_lang,
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
    parser.add_argument("--port", type=int, default=5006)
    parser.add_argument("--source-lang", default="en")
    parser.add_argument("--target-lang", default="ru")
    parser.add_argument("--auto-install-en-ru", action="store_true")
    args = parser.parse_args()

    init_db()
    log_line(f"[{now()}] [argos-server] sqlite cache: {DB_PATH}")

    if args.auto_install_en_ru and args.source_lang == "en" and args.target_lang == "ru":
        install_package_if_missing("en", "ru")

    translation = resolve_translation(args.source_lang, args.target_lang)
    if translation is None:
        raise RuntimeError(
            f"Argos translation pair {args.source_lang}->{args.target_lang} is not installed. "
            "Install via Argos GUI or run with --auto-install-en-ru for en->ru."
        )

    TranslateHandler.source_lang = args.source_lang
    TranslateHandler.target_lang = args.target_lang
    TranslateHandler.translation = translation
    log_line(f"[{now()}] [argos-server] ready on http://{args.host}:{args.port}")

    server = ThreadingHTTPServer((args.host, args.port), TranslateHandler)
    server.serve_forever()


if __name__ == "__main__":
    main()
