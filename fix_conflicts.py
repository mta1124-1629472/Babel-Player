from pathlib import Path
import sys


def main() -> int:
    if len(sys.argv) != 2:
        print("Usage: python fix_conflicts.py <path-to-file>")
        return 1

    target_path = Path(sys.argv[1])
    if not target_path.is_file():
        print(f"File not found: {target_path}")
        return 1

    content = target_path.read_text(encoding="utf-8")

    if not all(marker in content for marker in ("<<<<<<<", "=======", ">>>>>>>")):
        print(f"No merge-conflict markers found in {target_path}")
        return 0

    normalized_content = content.replace("\r\n", "\n")
    target_path.write_text(normalized_content, encoding="utf-8")
    print(f"Conflict markers still require manual resolution in: {target_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
