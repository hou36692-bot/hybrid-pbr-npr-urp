// README image builder: PNG renders -> web JPGs and labelled side-by-side panels.
//   node web_images.js <jobs.json>
// jobs.json: [{ "out": "docs/images/x.jpg", "width": 1600, "quality": 86,
//               "cells": [{ "src": "a.png", "crop": [x, y, w, h] (optional, source pixels), "label": "A" }],
//               "columns": 2, "gap": 6 }]
// Each cell is box-filtered to (width - gaps) / columns wide; labels are drawn with a tiny built-in bitmap font
// (ASCII only) so no font or canvas dependency is needed.
const fs = require("fs");
const { PNG } = require("pngjs");
const jpeg = require("jpeg-js");

function load(src) { const p = PNG.sync.read(fs.readFileSync(src)); return { w: p.width, h: p.height, d: p.data }; }

function crop(img, [x, y, w, h]) {
  const d = Buffer.alloc(w * h * 4);
  for (let j = 0; j < h; j++) img.d.copy(d, j * w * 4, ((y + j) * img.w + x) * 4, ((y + j) * img.w + x + w) * 4);
  return { w, h, d };
}

function resize(img, dw) {
  const dh = Math.round(img.h * dw / img.w), d = Buffer.alloc(dw * dh * 4);
  const fx = img.w / dw, fy = img.h / dh;
  for (let y = 0; y < dh; y++) {
    const y0 = Math.floor(y * fy), y1 = Math.max(y0 + 1, Math.floor((y + 1) * fy));
    for (let x = 0; x < dw; x++) {
      const x0 = Math.floor(x * fx), x1 = Math.max(x0 + 1, Math.floor((x + 1) * fx));
      let r = 0, g = 0, b = 0, n = 0;
      for (let yy = y0; yy < y1; yy++) for (let xx = x0; xx < x1; xx++) {
        const i = (yy * img.w + xx) * 4; r += img.d[i]; g += img.d[i + 1]; b += img.d[i + 2]; n++;
      }
      const o = (y * dw + x) * 4; d[o] = r / n; d[o + 1] = g / n; d[o + 2] = b / n; d[o + 3] = 255;
    }
  }
  return { w: dw, h: dh, d };
}

// 5x7 bitmap font, uppercase/lowercase mapped to uppercase, digits and a few symbols.
const FONT = {
  A:"01110100011000111111100011000110001",B:"11110100011000111110100011000111110",C:"01110100011000010000100001000101110",
  D:"11110100011000110001100011000111110",E:"11111100001000011110100001000011111",F:"11111100001000011110100001000010000",
  G:"01110100011000010111100011000101111",H:"10001100011000111111100011000110001",I:"01110001000010000100001000010001110",
  J:"00111000100001000010000101001001100",K:"10001100101010011000101001001010001",L:"10000100001000010000100001000011111",
  M:"10001110111010110101100011000110001",N:"10001100011100110101100111000110001",O:"01110100011000110001100011000101110",
  P:"11110100011000111110100001000010000",Q:"01110100011000110001101011001001101",R:"11110100011000111110101001001010001",
  S:"01111100001000001110000010000111110",T:"11111001000010000100001000010000100",U:"10001100011000110001100011000101110",
  V:"10001100011000110001100010101000100",W:"10001100011000110101101011010101010",X:"10001100010101000100010101000110001",
  Y:"10001100010101000100001000010000100",Z:"11111000010001000100010001000011111",
  "0":"01110100011001110101110011000101110","1":"00100011000010000100001000010001110","2":"01110100010000100010001000100011111",
  "3":"11111000100010000010000011000101110","4":"00010001100101010010111110001000010","5":"11111100001111000001000011000101110",
  "6":"00110010001000011110100011000101110","7":"11111000010001000100010000100001000","8":"01110100011000101110100011000101110",
  "9":"01110100011000101111000010001001100"," ":"00000000000000000000000000000000000","-":"00000000000000011111000000000000000",
  "/":"00001000010001000100010001000010000",".":"00000000000000000000000000110001100","(":"00010001000100001000010000010000010",
  ")":"01000001000001000010000100010001000",":":"00000011000110000000011000110000000","+":"00000001000010011111001000010000000",
  "=":"00000000001111100000111110000000000",",":"00000000000000000000001100010001000","%":"11000110010001000100010001001100011",
};
function drawLabel(img, text, x0, y0, scale) {
  const chars = text.toUpperCase().split("");
  const tw = chars.length * 6 * scale + 2 * scale, th = 9 * scale;
  for (let y = y0; y < y0 + th + 4 && y < img.h; y++) for (let x = x0; x < x0 + tw + 8 && x < img.w; x++) {
    const i = (y * img.w + x) * 4; img.d[i] *= 0.3; img.d[i + 1] *= 0.3; img.d[i + 2] *= 0.3;
  }
  chars.forEach((c, n) => {
    const glyph = FONT[c] || FONT[" "];
    for (let gy = 0; gy < 7; gy++) for (let gx = 0; gx < 5; gx++) {
      if (glyph[gy * 5 + gx] !== "1") continue;
      for (let sy = 0; sy < scale; sy++) for (let sx = 0; sx < scale; sx++) {
        const x = x0 + 4 + scale + n * 6 * scale + gx * scale + sx, y = y0 + 2 + scale + gy * scale + sy;
        if (x < img.w && y < img.h) { const i = (y * img.w + x) * 4; img.d[i] = 255; img.d[i + 1] = 255; img.d[i + 2] = 255; }
      }
    }
  });
}

const jobs = JSON.parse(fs.readFileSync(process.argv[2], "utf8"));
for (const job of jobs) {
  const columns = job.columns || job.cells.length, gap = job.gap ?? 6, width = job.width || 1600;
  const cellW = Math.floor((width - gap * (columns - 1)) / columns);
  const cells = job.cells.map(c => {
    let img = load(c.src);
    if (c.crop) img = crop(img, c.crop);
    img = resize(img, cellW);
    if (c.label) drawLabel(img, c.label, 0, 0, Math.max(2, Math.round(cellW / 320)));
    return img;
  });
  const rows = Math.ceil(cells.length / columns), cellH = Math.max(...cells.map(c => c.h));
  const W = columns * cellW + gap * (columns - 1), H = rows * cellH + gap * (rows - 1);
  const out = Buffer.alloc(W * H * 4, 0);
  for (let i = 3; i < out.length; i += 4) out[i] = 255;
  for (let i = 0; i < out.length; i += 4) { out[i] = 22; out[i + 1] = 24; out[i + 2] = 28; }
  cells.forEach((c, n) => {
    const ox = (n % columns) * (cellW + gap), oy = Math.floor(n / columns) * (cellH + gap);
    for (let y = 0; y < c.h; y++) c.d.copy(out, ((oy + y) * W + ox) * 4, y * c.w * 4, (y + 1) * c.w * 4);
  });
  const enc = jpeg.encode({ data: out, width: W, height: H }, job.quality || 86);
  fs.mkdirSync(require("path").dirname(job.out), { recursive: true });
  fs.writeFileSync(job.out, enc.data);
  console.log(`${job.out} ${W}x${H} ${(enc.data.length / 1024).toFixed(0)} KB`);
}
