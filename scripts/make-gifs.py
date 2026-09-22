#!/usr/bin/env python3
"""Turns the frames the readme_gifs autotest recorded into the README's GIFs.

    ./scripts/autotest.sh readme_gifs       record the frames into .devtest/gifs/<name>/ (a few minutes)
    python3 scripts/make-gifs.py            make docs/media/editor.gif, build.gif and capture.gif
    python3 scripts/make-gifs.py build      make only that one

A take the recorder refused (a piece missing in the editor, a building already on the set) leaves
bad.txt next to its frames, with the reason; this script then stops instead of making the GIF.

Needs Pillow, numpy and an ffmpeg: on PATH, in $FFMPEG, or from the imageio-ffmpeg package.
One way to get all three:
    python3 -m venv /tmp/gif-venv && /tmp/gif-venv/bin/pip install pillow numpy imageio-ffmpeg
    /tmp/gif-venv/bin/python scripts/make-gifs.py

Each frame is scaled down, then every 8 x 8 block that barely changed since the last frame keeps
the last frame's pixels. The game's picture shimmers a little from frame to frame (light, leaves,
the grass), and without this every frame of the GIF would store the whole picture again. Then
ffmpeg makes one palette for the whole clip and writes the GIF with an ordered dither, which
leaves pixels that did not change alone.
"""
import glob
import os
import shutil
import subprocess
import sys
import tempfile

import numpy as np
from PIL import Image

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FRAMES = os.path.join(REPO, ".devtest", "gifs")
OUT = os.path.join(REPO, "docs", "media")

# name: width in pixels, frames a second (the recording's), colours, the change that counts:
# a block is drawn again when one pixel changed by more than `threshold`, or the block on average
# by more than `mean` (a dark panel fading off dark ground changes every pixel a little; without
# `mean` its blocks would stay on the ground for good). `stats` is how the palette is picked:
# "full" from every pixel, "diff" mostly from what moves, so the editor's short red ghost stays red.
CLIPS = {
    "editor": dict(width=960, fps=15, colors=256, threshold=10, mean=None, stats="diff"),
    "build": dict(width=880, fps=15, colors=256, threshold=32, mean=10, stats="full"),
    "capture": dict(width=960, fps=15, colors=256, threshold=14, mean=None, stats="full"),
}

BLOCK = 8


def ffmpeg():
    found = os.environ.get("FFMPEG") or shutil.which("ffmpeg")
    if found:
        return found
    try:
        import imageio_ffmpeg

        return imageio_ffmpeg.get_ffmpeg_exe()
    except ImportError:
        sys.exit("!! no ffmpeg: put it on PATH, set FFMPEG, or pip install imageio-ffmpeg")


def crop_box(folder):
    """The part of the frames to keep, from crop.txt (x,y,w,h), or all of it."""
    path = os.path.join(folder, "crop.txt")
    if not os.path.exists(path):
        return None
    x, y, w, h = [int(v) for v in open(path).read().strip().split(",")]
    return (x, y, x + w, y + h)


def steady_frames(folder, work, width, threshold, mean=None):
    """Scaled frames where a block that changed by at most `threshold` (and on average by at most
    `mean`) keeps the last frame's pixels."""
    bad = os.path.join(folder, "bad.txt")
    if os.path.exists(bad):
        sys.exit(f"!! {folder}: the recording was refused, record it again: {open(bad).read().strip()}")
    files = sorted(glob.glob(os.path.join(folder, "*.jpg")))
    if not files:
        sys.exit(f"!! no frames in {folder}: run ./scripts/autotest.sh readme_gifs first")
    box = crop_box(folder)
    last = None
    for index, path in enumerate(files, 1):
        image = Image.open(path).convert("RGB")
        if box:
            image = image.crop(box)
        height = round(image.height * width / image.width / 2) * 2
        frame = np.asarray(image.resize((width, height), Image.LANCZOS)).astype(np.int16)
        if last is not None:
            rows = (height + BLOCK - 1) // BLOCK
            cols = (width + BLOCK - 1) // BLOCK
            change = np.zeros((rows * BLOCK, cols * BLOCK), np.int16)
            change[:height, :width] = np.abs(frame - last).max(axis=2)
            blocks = change.reshape(rows, BLOCK, cols, BLOCK)
            moved = blocks.max(axis=(1, 3)) > threshold
            if mean is not None:
                moved |= blocks.mean(axis=(1, 3)) > mean
            # A block next to one that moved goes along, so a moving edge takes its soft border.
            grown = moved.copy()
            grown[1:, :] |= moved[:-1, :]
            grown[:-1, :] |= moved[1:, :]
            grown[:, 1:] |= moved[:, :-1]
            grown[:, :-1] |= moved[:, 1:]
            mask = np.repeat(np.repeat(grown, BLOCK, 0), BLOCK, 1)[:height, :width]
            frame = np.where(mask[..., None], frame, last)
        last = frame
        Image.fromarray(frame.astype(np.uint8)).save(os.path.join(work, f"{index:04d}.png"))
    return len(files)


def make(name, settings, tool):
    folder = os.path.join(FRAMES, name)
    target = os.path.join(OUT, name + ".gif")
    work = tempfile.mkdtemp(prefix=f"gif-{name}-")
    try:
        count = steady_frames(folder, work, settings["width"], settings["threshold"], settings.get("mean"))
        graph = (
            f"split[a][b];[a]palettegen=max_colors={settings['colors']}:stats_mode={settings.get('stats', 'full')}[p];"
            "[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle"
        )
        subprocess.run(
            [tool, "-v", "error", "-y", "-framerate", str(settings["fps"]),
             "-i", os.path.join(work, "%04d.png"), "-vf", graph, "-loop", "0", target],
            check=True,
        )
    finally:
        shutil.rmtree(work, ignore_errors=True)
    size = os.path.getsize(target) / 1048576
    with Image.open(target) as gif:
        shape = gif.size
    print(f"{target}: {shape[0]}x{shape[1]}, {count} frames at {settings['fps']} fps "
          f"({count / settings['fps']:.1f} s), {size:.2f} MB")


def main():
    names = sys.argv[1:] or list(CLIPS)
    unknown = [n for n in names if n not in CLIPS]
    if unknown:
        sys.exit(f"!! unknown clip {', '.join(unknown)}; known: {', '.join(CLIPS)}")
    os.makedirs(OUT, exist_ok=True)
    tool = ffmpeg()
    for name in names:
        make(name, CLIPS[name], tool)


if __name__ == "__main__":
    main()
