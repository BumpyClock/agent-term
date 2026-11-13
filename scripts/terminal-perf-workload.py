"""Emit bounded agent-like ANSI output through a real shell/ConPTY.

Each trial waits for run-N.go and records run-N.done.json after writing.
Patterns:
  agent  counters and a spinner on a mostly stable 36-row screen.
  dense  every payload character changes on every update.
  cjk    26 wide CJK glyphs per row; each update slides every row by 13 code points,
         so a 12 s run at 60 Hz draws about 10,300 distinct glyphs, more than one screen.
  held   one synchronized update (ESC[?2026h) that is never released, then silence.
"""

import argparse
import json
import pathlib
import sys
import time

CJK_FIRST = 0x4E00
CJK_COUNT = 20000
HELD_MARKER = "Held synchronized update"


def payload(pattern, row, tick):
    if pattern == "dense":
        return "".join(chr(33 + (column + row + tick) % 90) for column in range(52))
    if pattern == "cjk":
        return "".join(chr(CJK_FIRST + (row * 26 + column + tick * 13) % CJK_COUNT) for column in range(26))
    return "reading source, running tools, streaming response... "


def screen(pattern, tick):
    lines = ["\x1b[?2026h\x1b[H\x1b[?25l"]
    for row in range(36):
        color = (32, 36, 37)[row % 3]
        spinner = ("|", "/", "-", chr(92))[tick % 4]
        lines.append(
            f"\x1b[{row + 1};1H\x1b[{color}m[{row:02}] Agent task {tick:05} | "
            f"{payload(pattern, row, tick)}{spinner}\x1b[0m\x1b[K"
        )
    lines.append("\x1b[?2026l")
    return "".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("output", type=pathlib.Path)
    parser.add_argument("--trials", type=int, default=5)
    parser.add_argument("--seconds", type=float, default=12)
    parser.add_argument("--hz", type=int, default=60)
    parser.add_argument("--pattern", choices=("agent", "dense", "cjk", "held"), default="agent")
    args = parser.parse_args()
    if args.trials < 1 or args.seconds <= 0 or args.hz < 1:
        parser.error("trials, seconds, and hz must be positive")
    args.output.mkdir(parents=True, exist_ok=True)
    if list(args.output.glob("run-*.go")) or list(args.output.glob("run-*.done.json")):
        raise FileExistsError("Use a fresh workload directory without previous signals")
    sys.stdout.write("\x1b[?1049h\x1b[2J" + (screen("agent", 0) if args.pattern == "held" else ""))
    sys.stdout.flush()
    (args.output / "ready.json").write_text(json.dumps({"hz": args.hz, "pattern": args.pattern}))
    try:
        for trial in range(1, args.trials + 1):
            gate = args.output / f"run-{trial}.go"
            done = args.output / f"run-{trial}.done.json"
            if gate.exists() or done.exists():
                raise FileExistsError("Use a fresh workload directory without previous signals")
            deadline = time.monotonic() + 300
            while not gate.exists():
                if time.monotonic() > deadline:
                    raise TimeoutError(f"No start signal for trial {trial}")
                time.sleep(0.01)
            started = time.monotonic()
            ticks = 0
            byte_count = 0
            expected_ticks = 1 if args.pattern == "held" else round(args.seconds * args.hz)
            while ticks < expected_ticks:
                if args.pattern == "held":
                    frame = f"\x1b[?2026h\x1b[1;1H{HELD_MARKER}\x1b[K"
                else:
                    frame = screen(args.pattern, ticks)
                sys.stdout.write(frame)
                sys.stdout.flush()
                byte_count += len(frame.encode("utf-8"))
                ticks += 1
                if args.pattern != "held":
                    time.sleep(max(0, started + ticks / args.hz - time.monotonic()))
            pending = done.with_suffix(".tmp")
            pending.write_text(
                json.dumps({"ticks": ticks, "expectedTicks": expected_ticks, "bytes": byte_count,
                            "elapsed": time.monotonic() - started, "hz": args.hz, "pattern": args.pattern,
                            "idleUntil": f"run-{trial + 1}.go" if trial < args.trials else None})
            )
            pending.replace(done)
    finally:
        sys.stdout.write("\x1b[?2026l\x1b[?25h\x1b[?1049l\r\nWorkload complete.\r\n")
        sys.stdout.flush()


if __name__ == "__main__":
    main()
