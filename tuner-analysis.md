# Tuner.js — Complete Technical Analysis

> A browser-based chromatic tuner (Chrome Extension) that detects musical pitch from a microphone in real-time and visualizes it on two HTML5 canvases.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Startup Flow](#2-startup-flow)
3. [Audio Pipeline](#3-audio-pipeline)
4. [Pitch Detection Algorithm (McLeod Pitch Method)](#4-pitch-detection-algorithm-mcleod-pitch-method)
5. [Frequency → MIDI Conversion](#5-frequency--midi-conversion)
6. [Pitch Smoothing](#6-pitch-smoothing)
7. [Visualization — Note Display (Right Side)](#7-visualization--note-display-right-side)
8. [Visualization — Chromatic Grid (Left Side)](#8-visualization--chromatic-grid-left-side)
9. [Visualization — Pitch History Line](#9-visualization--pitch-history-line)
10. [Notation Modes](#10-notation-modes)
11. [Canvas Coordinate System](#11-canvas-coordinate-system)
12. [Color Scheme & Rendering Details](#12-color-scheme--rendering-details)
13. [Frame Loop](#13-frame-loop)
14. [Data Flow Diagram](#14-data-flow-diagram)
15. [Key Constants & Magic Numbers](#15-key-constants--magic-numbers)

---

## 1. Architecture Overview

```
┌──────────────┐     ┌─────────────┐     ┌──────────────┐     ┌─────────────────┐
│  Microphone  │────▶│ AudioContext │────▶│ AnalyserNode │────▶│ PitchDetector   │
│  (getUserMedia)    │              │     │              │     │ (pitchy / MPM)  │
└──────────────┘     └─────────────┘     └──────────────┘     └────────┬────────┘
                                                                      │
                                                                      ▼
                                                              [pitch, clarity]
                                                                      │
                                                                      ▼
                                                             ┌─────────────────┐
                                                             │  updatePitch()  │
                                                             │  (main render)  │
                                                             └────────┬────────┘
                                                                      │
                                                    ┌─────────────────┼─────────────────┐
                                                    ▼                 ▼                 ▼
                                           Canvas (overlay)   historyData[]      Canvas (history)
                                           - Note labels      - Note values      - Pitch line
                                           - Cents display    - Rolling buffer   - Scrolling trace
```

**Key files:**

| File | Purpose |
|------|---------|
| `tuner.html` | UI shell — two stacked `<canvas>` elements, settings overlay, mic permission prompt |
| `tuner.js` | All logic — audio setup, pitch detection loop, rendering |
| `vendor/pitchy@2.0.3.js` | Third-party library implementing McLeod Pitch Method (MPM) |

---

## 2. Startup Flow

```
start()                                              [line 158]
  │
  ├─ getStream()                                     [line 142]
  │    └─ navigator.mediaDevices.getUserMedia({ audio: { autoGainControl: false } })
  │         ├─ Success → returns MediaStream
  │         └─ Failure → shows #micNeeded UI, opens chrome.runtime grant.html
  │
  ├─ new AudioContext()
  ├─ audioContext.resume()           // required by autoplay policy
  ├─ audioContext.createAnalyser()
  ├─ audioContext.createMediaStreamSource(stream)
  ├─ sourceNode.connect(analyserNode)
  │
  ├─ pitchy.PitchDetector.forFloat32Array(analyserNode.fftSize)
  │     // fftSize is typically 2048 (default), so input buffer = 2048 samples
  │
  ├─ new Float32Array(detector.inputLength)
  │     // reusable buffer, avoids GC pressure
  │
  └─ updatePitch(analyserNode, detector, input, audioContext.sampleRate)
       // enters the recursive render loop
```

**Important detail:** `autoGainControl: false` ensures the audio signal isn't normalized by the browser, giving the tuner raw amplitude data for more accurate pitch detection.

---

## 3. Audio Pipeline

### Components

1. **MediaStreamSource** — wraps the microphone stream as a Web Audio node.
2. **AnalyserNode** — provides time-domain audio samples via `getFloatTimeDomainData()`. The FFT size (default 2048) determines:
   - **Buffer length:** 2048 samples per frame.
   - **Frequency resolution:** `sampleRate / fftSize` ≈ 21.5 Hz at 44100 Hz.
   - **Time resolution:** `fftSize / sampleRate` ≈ 46 ms per frame.

### Data Path Per Frame

```
analyserNode.getFloatTimeDomainData(input)
  → fills Float32Array with 2048 samples of raw PCM audio (range: -1.0 to +1.0)
  → handed to detector.findPitch(input, sampleRate)
  → returns [pitch_in_Hz, clarity_0_to_1]
```

---

## 4. Pitch Detection Algorithm (McLeod Pitch Method)

The `pitchy` library implements the **McLeod Pitch Method (MPM)** from the paper *"A Smarter Way to Find Pitch"* by Philip McLeod and Geoff Wyvill (University of Otago).

### Step-by-step:

#### 4.1 Autocorrelation via FFT

```
Input signal x[n], length W (2048 samples)
  │
  ├─ Zero-pad to next power of 2 × W (4096)
  ├─ Forward FFT → X[k]
  ├─ Complete spectrum (mirror for real input)
  ├─ Multiply each bin by its conjugate:  X[k]² + X[k+1]²  (power spectrum)
  ├─ Inverse FFT → autocorrelation r'[τ]
  └─ Extract real part (first W values)
```

This computes the autocorrelation `r'(τ)` efficiently in O(N log N) rather than O(N²).

#### 4.2 Normalization → NSDF

The raw autocorrelation is converted to the **Normalized Square Difference Function (NSDF)**:

```
NSDF(τ) = 2·r'(τ) / m'(τ)
```

Where `m'(τ)` is a triangular windowing normalization factor:
- `m'(0) = 2·r'(0)`
- Each subsequent `m'(τ)` is computed incrementally by subtracting edge samples: `m'(τ) = m'(τ-1) - x[τ-1]² - x[W-τ]²`
- The loop terminates early when `m'(τ) ≤ 0` (remaining values set to 0).

The NSDF normalizes the autocorrelation to the range **[-1, 1]**, making it invariant to signal amplitude.

#### 4.3 Key Maximum Detection

**Key maxima** are the highest peaks between a positively-sloped zero crossing and the next negatively-sloped zero crossing:

```
For each sample in NSDF:
  ├─ If zero crossing from negative → positive: start tracking a new peak
  ├─ While tracking: remember the highest value and its index
  └─ If zero crossing from positive → negative: record that index as a key maximum
```

This filters out spurious local maxima and keeps only the most prominent peaks.

#### 4.4 Pitch & Clarity Extraction

```
nMax = max(all key maximum NSDF values)

resultIndex = first key maximum where NSDF[i] ≥ 0.9 × nMax
clarity = clamp(NSDF[resultIndex], 0, 1)
pitch = sampleRate / resultIndex
```

- The **0.9 threshold** (called `clarityThreshold` in the library) picks the first strong peak — the fundamental frequency — rather than a harmonic.
- **Clarity** ranges from 0 to 1: how confident the detection is. A clarity of 1.0 means a perfectly clear pitch; below ~0.5 the pitch is unreliable.
- The tuner app uses a **clarity threshold of 0.9** (`clarity >= 0.9`) to decide whether to display a pitch or push `null` to the history.

### Why MPM?

| Advantage | Explanation |
|-----------|-------------|
| **Noise-robust** | NSDF normalization removes amplitude dependence |
| **Harmonically-aware** | Key maxima + threshold picks the fundamental, not harmonics |
| **Fast** | FFT-based autocorrelation runs in real-time on modest hardware |
| **Stable** | Returns both pitch and a confidence metric (clarity) |

---

## 5. Frequency → MIDI Conversion

The `ftom()` function converts a frequency in Hz to a **floating-point MIDI note number**:

```
ftom(f) = 69 + 12 × log₂(f / 440)
```

This is the standard **12-TET (equal temperament)** formula:

- **A4 = 440 Hz → MIDI 69** (by definition)
- Each semitone is a factor of `2^(1/12) ≈ 1.05946`
- The result is a **float**, not an integer — fractional values represent cents deviation from the nearest semitone.

**Examples:**

| Frequency | MIDI Number | Nearest Note | Cents Deviation |
|-----------|-------------|--------------|-----------------|
| 440.0 Hz | 69.0 | A4 | +0 |
| 466.2 Hz | 70.0 | Bb4 | +0 |
| 452.0 Hz | 69.38 | A4 | +38 |
| 261.6 Hz | 60.0 | C4 | +0 |

---

## 6. Pitch Smoothing

The tuner applies **exponential moving average (EMA)** smoothing to prevent jittery note display:

```javascript
if (Math.abs(fnote - note) < 1) {
    note += (fnote - note) / 5    // smooth: move 20% toward new value
} else {
    note = fnote                   // snap: large change → immediate update
}
```

- **Small changes (< 1 semitone):** The displayed note moves smoothly — each frame it shifts by 1/5 of the difference. This prevents the display from flickering between adjacent notes.
- **Large changes (≥ 1 semitone):** The note snaps immediately. This ensures the tuner responds quickly to real pitch changes (e.g., moving to a new string).

The **1/5 factor** means convergence to the true value takes ~5 frames (~80ms at 16ms/frame), giving a balance between smoothness and responsiveness.

---

## 7. Visualization — Note Display (Right Side)

This is the large text showing the detected note name, cents deviation, and frequency.

### What's drawn (when `clarity >= 0.9`):

For each octave from -1 to +1 (to show the note in neighboring octaves too):

```
1. Blue filled rectangle ("smear") — shows the gap between actual and rounded pitch
   ┌─────────────────────────────────────────────────────────────┐
   │  Color: #bef (light blue)                                  │
   │  Width: from x=280 to canvas right edge                    │
   │  Height: absolute difference between actual and rounded Y  │
   │  Alpha: proportional to how close to a note (0.5 semitone) │
   └─────────────────────────────────────────────────────────────┘

2. Blue horizontal line — exact pitch position
   ┌─────────────────────────────────────────────────────────────┐
   │  Color: #bef (light blue)                                  │
   │  Width: from x=280 to canvas right edge                    │
   │  Alpha: full clarity-based opacity                         │
   └─────────────────────────────────────────────────────────────┘

3. Note name text (32px bold)
   ┌─────────────────────────────────────────────────────────────┐
   │  Format: "A4" or "Bb3" etc.                                │
   │  Position: (290, y-8) — just above the line                │
   └─────────────────────────────────────────────────────────────┘

4. Cents + Hz text (12px)
   ┌─────────────────────────────────────────────────────────────┐
   │  Format: "+38 (440 Hz)" or "-12 (262 Hz)"                  │
   │  Position: immediately right of the note name              │
   │  Cents: deviation from nearest semitone × 100              │
   └─────────────────────────────────────────────────────────────┘
```

### Cents Calculation

```javascript
const deviation = Math.round((note - Math.round(note)) * 100)
const cents = `${deviation < 0 ? '' : '+'}${deviation}`
```

- `note - Math.round(note)` gives the fractional semitone deviation (e.g., +0.38)
- Multiply by 100 → cents (38 cents sharp)
- Displayed as "+38" or "-12" (negative sign is implicit)

---

## 8. Visualization — Chromatic Grid (Left Side)

The left portion of the canvas shows a **vertical chromatic grid** — horizontal lines for each of the 12 pitch classes.

### Grid Lines

```javascript
for (let i = 0; i <= 12; i++) {
    ctx.fillRect(0, getY(i) - 1, width, 2)       // faint horizontal line
    ctx.fillText(`${pitchClasses[i % 12]}`, 32, getY(i) - 4)  // note label
}
```

- **13 lines** (0 through 12) spanning one octave
- **Color:** `#fff2` (white at ~12% opacity) — very subtle background
- **Labels:** C, Db, D, Eb, E, F, F#, G, Ab, A, Bb, B (left-aligned at x=32)

### Highlight on Detection

When a pitch is detected with high clarity:

```javascript
for (let i = 0; i < 12; i++) {
    let dist = p - i              // fractional distance from pitch class
    // wrap to [-6, 6]
    dist = Math.abs(dist)
    if (dist < 0.5) {
        ctx.globalAlpha = (1 - dist / 0.5) * o   // fade as pitch moves away
        ctx.fillRect(0, getY(i) - 1, width, 2)   // green highlight line
    }
}
```

- **Color:** `#d7fc70` (yellow-green)
- **Effect:** The grid line closest to the detected pitch lights up. The further the pitch is from a semitone center, the dimmer the highlight.
- **Opacity factor `o`:** Derived from clarity: `o = (clarity - 0.9) / 0.1`. Since clarity must be ≥ 0.9, this maps [0.9, 1.0] → [0.0, 1.0]. So a clarity of 0.95 gives half-brightness, while 1.0 is full brightness.

---

## 9. Visualization — Pitch History Line

A scrolling line chart showing the pitch over time, drawn on the **left side** of the canvas (x < 280).

### Data Buffer

```javascript
const historyData = []
// Each frame pushes: note (float MIDI) if clarity >= 0.9, else null
// Maximum length: 560 / 3 ≈ 186 samples (~3 seconds at 16ms/frame)
if (historyData.length > 560 / 3) {
    historyData.shift()
}
```

### Multi-Octave Line Drawing

The history doesn't just draw one line — it draws **parallel lines for octaves -1, 0, and +1** to show the pitch's relationship across octaves:

```javascript
for (let i = 0; i < historyData.length; i++) {
    let x = 280 - (historyData.length - i - 1) * 3   // each sample = 3px wide
    for (let octave = -1; octave <= 1; octave++) {
        const octaveNumber = Math.floor(historyData[i] / 12) + octave
        let y = getY(historyData[i] - 12 * octaveNumber)
        activeStroke.line.push({ x, y })               // add to active stroke
    }
}
```

**Key rendering detail:**

- The `active` object tracks **continuous strokes per octave**. When a `null` entry appears (no detection), the stroke for that octave is broken (deleted from `active`).
- This creates **disconnected line segments** for periods of silence, rather than a continuous line jumping across the canvas.
- Each stroke is rendered with `ctx.stroke()` — a simple polyline.

### Spacing

- **3 pixels per sample** → 186 samples × 3px = 558px, which fits the 560px canvas width (280px left side + ~280px right side).

---

## 10. Notation Modes

Three different octave numbering conventions, all starting from the same MIDI number:

| Notation | Octave Formula | Middle C (MIDI 60) | Used By |
|----------|---------------|---------------------|---------|
| **Roland** | `floor(midi / 12) - 1` | C4 | Most DAWs, scientific pitch notation |
| **Yamaha** | `floor(midi / 12) - 2` | C3 | Yamaha keyboards, many hardware synths |
| **Cakewalk** | `floor(midi / 12)` | C5 | Cakewalk/SONAR, some vintage gear |

**Persistence:** The notation mode is saved in `chrome.storage.sync` (Chrome Extension) or `localStorage` (standalone), loaded on startup via `loadNotationMode()`.

The `pitchClasses` array always uses flats (`Db`, `Eb`, `Ab`, `Bb`) and sharps (`F#`), not a full set of enharmonic equivalents — a simplification for display purposes.

---

## 11. Canvas Coordinate System

```
Canvas size: 480 × 320 pixels (set in HTML)

Two layered canvases:
  ┌──────────────────────────────────────────────┐
  │ historyCanvas (z-index: behind)              │  ← pitch history lines
  ├──────────────────────────────────────────────┤
  │ canvas (z-index: front)                      │  ← grid, note display, labels
  └──────────────────────────────────────────────┘
```

### Y-Axis Mapping

```javascript
const getY = (note) => 300 - (note * 45) / 2
```

| MIDI Note | Y Position | Note |
|-----------|-----------|------|
| 0 (C-1) | 300 | Bottom |
| 12 (C0) | 270 | |
| 24 (C1) | 240 | |
| 36 (C2) | 210 | |
| 48 (C3) | 180 | |
| 60 (C4) | 150 | Middle C |
| 72 (C5) | 120 | |
| 84 (C6) | 90 | |
| 96 (C7) | 60 | |
| 108 (C8) | 30 | |
| 120 (C9) | 0 | Top |

- **45/2 = 22.5 pixels per semitone**
- **270 pixels total range** (MIDI 0 to 120)
- Higher MIDI numbers → lower Y values (higher on screen = higher pitch)

### X-Axis Split

- **x = 0 to 280:** History line + chromatic grid labels
- **x = 280 to 480:** Note name display, cents, frequency

---

## 12. Color Scheme & Rendering Details

| Element | Color | Hex | Opacity Logic |
|---------|-------|-----|---------------|
| Background | Black | `#000` | Fixed (CSS) |
| Grid lines | White | `#fff` | `#fff2` → ~12.5% fixed |
| Pitch class labels | White | `#fff` | ~12.5% fixed |
| Detected pitch highlight | Yellow-green | `#d7fc70` | `(1 - dist/0.5) × o` — fades with distance from center |
| Note display line | Light blue | `#bef` | Full `o` (clarity-based) |
| Note name text | Light blue | `#bef` | Full `o` |
| Cents/Hz text | Light blue | `#bef` | Full `o` |
| History line | Light blue | `#bef` | Fixed `#bef` (lineWidth: 2) |
| Smear rectangle | Light blue | `#bef` | `o × (1 - |p - round(p)| / 0.5)` — fades with detuning |

**Alpha variable `o`:**

```
o = (clarity - 0.9) / 0.1     // maps clarity [0.9, 1.0] → alpha [0, 1]
```

This is the master opacity multiplier for all detection-dependent rendering.

---

## 13. Frame Loop

```javascript
function updatePitch(analyserNode, detector, input, sampleRate) {
    // ... all detection + rendering ...

    setTimeout(() => updatePitch(analyserNode, detector, input, sampleRate), 16)
}
```

- Uses `setTimeout(fn, 16)` — targeting ~60 FPS (16ms intervals)
- **Not** `requestAnimationFrame` — this means the tuner keeps running even when the tab is not focused (though Chrome throttles `setTimeout` in background tabs to 1 Hz)
- Each frame:
  1. Read audio samples from AnalyserNode
  2. Run MPM pitch detection
  3. Convert to MIDI, smooth
  4. Clear canvas
  5. Draw grid
  6. Draw note display (if clarity ≥ 0.9)
  7. Append to history buffer
  8. Draw history line
  9. Schedule next frame

---

## 14. Data Flow Diagram

```
         ┌──────────── Audio Samples (Float32Array, 2048 samples)
         │
         ▼
   ┌─────────────┐
   │  findPitch() │──── [pitch_Hz, clarity]
   └──────┬──────┘
          │
          ▼
   ┌─────────────┐
   │   ftom()    │──── fnote (float MIDI number)
   └──────┬──────┘
          │
          ▼
   ┌─────────────┐
   │  Smoothing  │──── note (smoothed MIDI number)
   │  (EMA, 1/5) │
   └──────┬──────┘
          │
    ┌─────┴─────┐
    │           │
    ▼           ▼
┌────────┐  ┌──────────────────────────────────┐
│ Canvas │  │ historyData.push(note or null)    │
│  draw  │  │ (capped at ~186 entries)         │
└───┬────┘  └───────────┬──────────────────────┘
    │                   │
    ▼                   ▼
┌────────┐  ┌──────────────────┐
│ Grid + │  │ History line     │
│ Note   │  │ rendering        │
│ display│  │ (multi-octave)   │
└────────┘  └──────────────────┘
```

---

## 15. Key Constants & Magic Numbers

| Value | Location | Meaning |
|-------|----------|---------|
| `2048` | `analyserNode.fftSize` (default) | Sample buffer length |
| `0.9` | `clarity >= 0.9` in tuner.js | Display threshold — only show pitch above this clarity |
| `0.9` | `_clarityThreshold` in pitchy | MPM key maximum threshold |
| `0.1` | `(clarity - 0.9) / 0.1` | Opacity scaling range |
| `1/5` | `(fnote - note) / 5` | EMA smoothing factor |
| `1` | `Math.abs(fnote - note) < 1` | Smoothing/snap threshold (1 semitone) |
| `560 / 3 ≈ 186` | `historyData.length > 560 / 3` | Max history samples (3 seconds of data at 3px/sample) |
| `3` | `(historyData.length - i - 1) * 3` | Pixels per history sample |
| `280` | `ctx.fillRect(280, ...)` | X-axis split point (left=history, right=note) |
| `45 / 2 = 22.5` | `(note * 45) / 2` | Pixels per semitone |
| `300` | `300 - (note * 45) / 2` | Y-axis offset (bottom of canvas) |
| `16` | `setTimeout(..., 16)` | Target frame interval (ms) → ~60 FPS |
| `440` | `f / 440` in `ftom()` | A4 reference frequency |
| `69` | `69 + 12 × log₂(...)` | MIDI number of A4 |
| `12` | `pitchClasses.length` | Notes per octave |
| `-1, 0, 1` | octave loop in history | Number of octaves rendered in history |

---

*Analysis generated from `tuner.js` v0.0.1 (tuner-chrome) and `pitchy@2.0.3`.*
