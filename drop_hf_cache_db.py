import argparse
from pathlib import Path


def remove_if_exists(path: Path) -> bool:
    if not path.exists():
        return False
    path.unlink()
    return True


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Delete local SQLite cache used by hf_translate_server.py"
    )
    parser.add_argument(
        "--yes",
        action="store_true",
        help="Do not ask for confirmation",
    )
    args = parser.parse_args()

    base = Path(__file__).resolve().parent
    db = base / "hf_translate_cache.db"
    wal = base / "hf_translate_cache.db-wal"
    shm = base / "hf_translate_cache.db-shm"

    print(f"Target DB: {db}")
    if not args.yes:
        answer = input("Delete cache DB files? [y/N]: ").strip().lower()
        if answer not in ("y", "yes"):
            print("Cancelled.")
            return 1

    removed = []
    for candidate in (db, wal, shm):
        if remove_if_exists(candidate):
            removed.append(candidate.name)

    if removed:
        print("Removed:", ", ".join(removed))
    else:
        print("Nothing to remove.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
