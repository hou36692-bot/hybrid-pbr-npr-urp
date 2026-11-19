// PNG frame sequence -> looping GIF (gifenc + pngjs, resolved from NODE_PATH; install them outside the project).
//   node make_gif.js <outGif> <frameDir> <width> <delayMs> [pingpong=1] [holdFirstLastMs=0] [colors=255]
// One palette is quantised from a sample of all frames so colours do not shift between frames. At most 255
// colours: the 256th palette slot is the transparent index used for unchanged pixels (see below).
const fs = require("fs");
const path = require("path");
const { PNG } = require("pngjs");
const { GIFEncoder, quantize, applyPalette } = require("gifenc");

const [outGif, frameDir, widthArg, delayArg, pingArg = "1", holdArg = "0", colorsArg = "255"] = process.argv.slice(2);
const width = +widthArg, delay = +delayArg, pingpong = pingArg === "1", hold = +holdArg, colors = +colorsArg;

function resize(src, sw, sh, dw, dh) {
  // Box-filter downscale (area average) to keep thin hair strands and outlines from aliasing.
  const out = new Uint8Array(dw * dh * 4);
  const fx = sw / dw, fy = sh / dh;
  for (let y = 0; y < dh; y++) {
    const y0 = Math.floor(y * fy), y1 = Math.max(y0 + 1, Math.floor((y + 1) * fy));
    for (let x = 0; x < dw; x++) {
      const x0 = Math.floor(x * fx), x1 = Math.max(x0 + 1, Math.floor((x + 1) * fx));
      let r = 0, g = 0, b = 0, n = 0;
      for (let yy = y0; yy < y1; yy++) for (let xx = x0; xx < x1; xx++) {
        const i = (yy * sw + xx) * 4; r += src[i]; g += src[i + 1]; b += src[i + 2]; n++;
      }
      const o = (y * dw + x) * 4;
      out[o] = r / n; out[o + 1] = g / n; out[o + 2] = b / n; out[o + 3] = 255;
    }
  }
  return out;
}

const files = fs.readdirSync(frameDir).filter(f => /^f\d+\.png$/.test(f)).sort();
if (files.length === 0) throw new Error("no frames in " + frameDir);
const frames = files.map(f => {
  const png = PNG.sync.read(fs.readFileSync(path.join(frameDir, f)));
  const h = Math.round(png.height * width / png.width);
  return { data: resize(png.data, png.width, png.height, width, h), w: width, h };
});
const { w, h } = frames[0];

// Palette from every frame, subsampled (every 3rd pixel) so one palette covers the whole sequence.
const step = 3, per = Math.ceil(w * h / step);
const sample = new Uint8Array(per * 4 * frames.length);
let k = 0;
for (const f of frames) for (let i = 0; i < w * h; i += step) { sample.set(f.data.subarray(i * 4, i * 4 + 4), k); k += 4; }
const palette = quantize(sample.subarray(0, k), Math.min(colors, 255), { format: "rgb565" });

const order = frames.map((_, i) => i);
if (pingpong && frames.length > 2) for (let i = frames.length - 2; i > 0; i--) order.push(i);
// Inter-frame transparency: a pixel whose palette index is unchanged from the previous frame is written as the
// transparent index, so LZW stores long runs of it instead of the busy background again. That index is the slot
// after the quantised colours; the palette is padded to a power of two as GIF requires. Frame 0 is complete.
const indexed = frames.map(f => applyPalette(f.data, palette, "rgb565"));
const opaque = palette.length;
const fullPalette = palette.concat([[0, 0, 0]]);
while (fullPalette.length & (fullPalette.length - 1)) fullPalette.push([0, 0, 0]);
const gif = GIFEncoder();
let previous = null;
order.forEach((idx, n) => {
  const cur = indexed[idx];
  let out = cur;
  if (previous) {
    out = new Uint8Array(cur.length);
    for (let i = 0; i < cur.length; i++) out[i] = cur[i] === previous[i] ? opaque : cur[i];
  }
  const isEnd = idx === 0 || idx === frames.length - 1;
  gif.writeFrame(out, w, h, {
    palette: n === 0 ? fullPalette : undefined,
    delay: isEnd && hold > 0 ? hold : delay,
    repeat: 0,
    transparent: previous !== null,
    transparentIndex: opaque,
    dispose: 1,
  });
  previous = cur;
});
gif.finish();
fs.writeFileSync(outGif, gif.bytes());
console.log(`${outGif}: ${order.length} frames ${w}x${h}, ${(fs.statSync(outGif).size / 1024 / 1024).toFixed(2)} MB`);
