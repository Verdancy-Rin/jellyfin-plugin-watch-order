"""Prints the changelog from build.yaml (the notes for the release being made)."""
import sys

lines = open(sys.argv[1] if len(sys.argv) > 1 else "build.yaml", encoding="utf-8").read().splitlines()
start = next(i for i, line in enumerate(lines) if line.startswith("changelog:"))
notes = []
for line in lines[start + 1:]:
    if line and not line.startswith(" "):
        break  # the next top-level key
    notes.append(line[2:] if line.startswith("  ") else line.strip())
print("\n".join(notes).strip())
