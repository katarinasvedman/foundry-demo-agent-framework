"""
Small helper to compress/resample an image to keep size small for JSON base64 attachments.
Usage:
  python compress_image.py <input_path> <output_path> [--max-width 1200] [--max-height 800] [--jpeg-quality 85]
Returns exit code 0 on success and prints the output path and size in bytes.
"""
import sys
from PIL import Image
import os

def compress_image(in_path, out_path, max_w=1200, max_h=800, jpeg_q=85):
    img = Image.open(in_path)
    img.thumbnail((max_w, max_h), Image.LANCZOS)
    # If PNG and small after thumbnail, save as PNG; otherwise convert to JPEG
    # Estimate size by saving to bytes
    _, ext = os.path.splitext(out_path)
    ext = ext.lower()
    if ext in ['.png']:
        img.save(out_path, format='PNG', optimize=True)
    else:
        # Convert RGBA to RGB if needed
        if img.mode in ('RGBA', 'LA'):
            background = Image.new('RGB', img.size, (255,255,255))
            background.paste(img, mask=img.split()[3])
            img = background
        img.save(out_path, format='JPEG', quality=jpeg_q, optimize=True)

    size = os.path.getsize(out_path)
    print(out_path)
    print(size)
    return size

if __name__ == '__main__':
    import argparse
    p = argparse.ArgumentParser()
    p.add_argument('input')
    p.add_argument('output')
    p.add_argument('--max-width', type=int, default=1200)
    p.add_argument('--max-height', type=int, default=800)
    p.add_argument('--jpeg-quality', type=int, default=85)
    args = p.parse_args()
    try:
        size = compress_image(args.input, args.output, args.max_width, args.max_height, args.jpeg_quality)
        sys.exit(0)
    except Exception as e:
        print('ERROR', e, file=sys.stderr)
        sys.exit(2)
