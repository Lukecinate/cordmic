# Podcast voice chain

CordMic 1.1.0 added a podcast-style voice chain in front of noise reduction in every preset. It is on by default.
It can be turned off globally in **Settings → Noise & voice → Podcast voice chain**. Turning it off restores the
1.0.0 stage list, still with the 1.1.1 manual gain and ceilings below.

Since 1.1.1, mic gain is manual: −24 to +24 dB, default 0 dB. Existing settings migrate to 0 dB. The chain never
auto-raises gain. Peak-aware protection may only reduce it, and does so when a sample peak would meet −12 dBFS
(3 dB below the yellow meter mark at −9 dBFS sample peak on both the Windows and Android meters). The look-ahead
limiter and the true-peak guard sit at −10 dBFS, 1 dB below yellow, as a last-resort net. Compressor makeup is 0 dB.
The same range and default apply on the phone. Fine steps of 0.1 dB travel in an optional trailing `GainExt` block
(magic 0xC9, version 1, int16 little-endian tenths). Older peers ignore it. The legacy gain byte stays 0–24;
a negative value is sent as 0 so an older peer never treats a wrap as +24 dB. The wire version stays 1.

All code is original and MIT-licensed. It was written from the public RBJ *Audio EQ Cookbook* formulas and from
standard one-pole, dB-domain gain-computer and Wiener-gain math. It contains no models, no weights, and no
third-party DSP code. Commercial plugins (MJUC, TDR, LoudMax) and GPL projects (AndroidMic, QuicMic) were used only
as conceptual references for what a podcast chain does. None of their code was read or copied.

Sources: `android/app/src/main/java/dev/cordmic/PodcastChain.kt` (phone) and
`windows-app/src/CordMic.Core/Dsp/PodcastChain.cs` (PC Studio and the C# reference). The two use the same math and
the same preset numbers; a test checks the preset values against each other.

## Where it runs

The chain runs in exactly one place per stream, so nothing is processed twice.

| Noise setting | Runs on | Order |
|---|---|---|
| Off / Phone – light / Phone – strong | Phone (`VoiceChain.kt`) | [presence lift] → gate measure → mic gain → **AGC → dynamic EQ → compressor → look-ahead limiter** → STFT noise suppression with the **dereverb / dialog masks** → pause expander → **true-peak guard** |
| PC Studio – balanced / whisper / maximum | PC (`StudioChain.cs`) | **AGC → dynamic EQ → compressor → look-ahead limiter → dereverb / dialog masks** → RNNoise ∥ Strong → WhisperGuard / Max blend → comfort floor → **true-peak guard** |

- With Studio on, the PC already sends `FlagStudio`, and the phone sends raw capture. The phone's podcast chain is
  off, so only the PC runs it.
- With the toggle off, the PC sets `FlagChainOff` (PING flags bit 7) and the phone bypasses the chain. Older PCs
  never set this bit, so a 1.1.0 phone keeps the chain on. Older phones ignore the bit. The wire version stays 1.
- In Studio, the B3 auto volume is not stacked on top of the chain. The podcast peak protection may only reduce
  gain, and B3 only meters loudness for the comfort floor, at 0 dB. With the chain off, B3 works as it did in 1.0.0,
  and its saved start gain is kept.
- **Platform noise suppression.** On phones where *Phone – light* uses Android's built-in `NoiseSuppressor`, that
  suppressor runs inside the capture path, before CordMic receives any samples. It cannot be reordered. Everything
  CordMic does, including the podcast chain and its own NS, comes after it.

## Stages

1. **Peak protection (downward only).** This stage never raises the user gain. `AgcMaxDb` is 0 dB relative to the
   manual mic gain. When a sample peak would meet −12 dBFS (`MeterPeakCeilingDbfs`, 3 dB below the yellow meter mark
   at −9 dBFS), the gain drops at once, holds for about 150 ms, then recovers toward unity over about 2 s. It is
   never taken above 1.0. With Auto volume off, this stage is skipped and only the limiter and true-peak guard remain
   as a net. Quiet speech stays quiet; there is no auto-boost toward a loudness target.
2. **Dynamic EQ.**
   - High-pass: 80 Hz, 2nd-order Butterworth (RBJ).
   - Mud: a parallel band at 250 Hz, Q 1. Written as `x + (g − 1)·BP(x)` with a constant-peak RBJ band-pass, this is a
     peaking cut of `g` at the centre and needs no coefficient updates. The cut is 2:1 above −6 dB band-to-broadband
     energy, capped per preset. Detector: 10 ms attack, 100 ms release.
   - De-ess: the same structure at 6.5 kHz, Q 1.4, covering about 5–8 kHz. The cut is 4:1 above −12 dB
     band-to-broadband, capped per preset. Detector: 1 ms attack, 50 ms release. It is off below a 16 kHz sample rate
     because those bands have no sibilance. At 16 kHz the centre is clamped to 0.4 × fs.
   - The gains are computed every 16 samples in dB and smoothed per sample (2 ms).
3. **Compressor.** Feed-forward, with a linked mean-square detector (5 ms attack, 25 ms release) and a dB-domain
   soft-knee gain computer: −22 dBFS threshold, 3:1 ratio, 10 dB knee, 0 dB makeup. Release is program-dependent,
   for a variable-mu-like feel. A fast gain-reduction envelope (8 ms attack, 80 ms release) runs alongside a slow one
   (400 ms / 900 ms) that charges only under sustained compression, and the larger of the two is applied. Short peaks
   recover quickly; dense passages recover slowly. A test checks that recovery after 2 s of compression takes more than
   twice as long as after a 30 ms burst.
4. **Look-ahead brickwall limiter**, −10 dBFS (1 dB below the yellow meter mark). The required gain `min(1, ceil/|x|)` gets a one-pole release (still ≤ the
   required gain). Then come a sliding minimum over L samples (a monotonic deque in fixed arrays) and a length-L boxcar,
   with the signal delayed by L − 1 samples. The boxcar reaches the needed gain exactly when the peak leaves the delay.
   This guarantees the ceiling with a smooth attack, and a final clamp only absorbs float rounding.
5. **Dereverb.** A spectral late-reverb suppressor, inside the existing 256-point, hop-128 Hann STFT. The late-reverb
   power per bin is `P_late(t) = exp(−2δ·Td)·P(t − Td)`, with δ = 3 ln 10 / RT60, RT60 assumed to be 0.5 s, and Td about
   50 ms (rounded to hops). The gain is Wiener-style, `SRR / (1 + SRR)` with `SRR = max(P − P_late, 0) / P_late`,
   floored at `−12 dB × strength`.
6. **Dialog isolation.** A frame VAD uses voice-band (100 Hz–4 kHz) SNR against the NS noise estimate, with a
   spectral-flatness check and a 150 ms hangover. Per-bin speech presence comes from the posterior SNR; local
   voice-band spectral peaks above 2× noise count as harmonics and are kept. This is weighted by a voice-band prior
   (full from 70 Hz to 8 kHz, falling to 0.5 above 10 kHz and 0.3 below 70 Hz). Non-speech bins go down to
   `−10 dB × strength`. The 256-point frame has bins about 190 Hz wide at 48 kHz, so individual harmonics of a low voice
   are not resolved, and "harmonics" here means harmonic and formant peaks at that resolution.
7. **One mask, no double masking.** In each bin, the dereverb × dialog gain is compared with the NS Wiener gain, and
   the **deeper of the two** is kept, never their product. The existing cross-bin (¼-½-¼) and over-time (fast open,
   slow release) smoothing then runs once. This is why the extra stages do not stack into musical noise. The
   strengths are conservative: on the phone the dereverb mask never goes below −6 dB and the dialog mask never below
   −4.5 dB. Together they reach at most −10.5 dB (Strong), which is still above the Strong NS floor of −15 dB.
8. **True-peak guard** after NS. Inter-sample peaks are estimated with a 4-tap half-sample interpolator
   `(−x0 + 9x1 + 9x2 − x3)/16`. The guard reduces gain at once, releases over 40 ms, and clamps at −10 dBFS. Delay is
   2 samples. NS gain changes and OLA phase therefore cannot clip.

## Per-preset parameters

The defaults apply unless a preset changes them: AGC max 0 dB (never auto-raises), peak-protection ceiling −12 dBFS,
HP 80 Hz, mud 250 Hz / −6 dB threshold, de-ess 6.5 kHz / −12 dB threshold, compressor −22 dBFS 3:1 10 dB knee 0 dB
makeup, limiter and true-peak guard −10 dBFS. The meter turns yellow at −9 dBFS sample peak.

| Preset | Mud max cut | De-ess max cut | Comp threshold | AGC max | Look-ahead | Limiter release | Dereverb | Dialog |
|---|---|---|---|---|---|---|---|---|
| Off (lowest latency) | 3 dB | 5 dB | −22 dBFS | 0 | **1.0 ms** | 50 ms | 0 (no STFT) | 0 |
| Phone – light | 4 dB | 6 dB | −22 dBFS | 0 | 1.5 ms | 60 ms | 0.35 (floor −4.2 dB) | 0.30 (−3 dB) |
| Phone – strong | 4 dB | 6 dB | −22 dBFS | 0 | 2.0 ms | 80 ms | 0.50 (−6 dB) | 0.45 (−4.5 dB) |
| PC Studio – balanced | 4 dB | 6 dB | −22 dBFS | 0 | 2.0 ms | 60 ms | 0.35 | 0.30 |
| PC Studio – whisper | 2 dB | 3 dB | −26 dBFS | 0 | 2.0 ms | 60 ms | 0.25 | 0.15 |
| PC Studio – maximum | 4 dB | 6 dB | −22 dBFS | 0 | 2.0 ms | 80 ms | 0.50 | 0.40 |

The noise setting Off keeps no STFT, so it stays the lowest-latency choice for games. Whisper keeps the de-ess and
the masks gentle so whispered s/f/sh sounds are not thinned.

## Added latency (48 kHz)

| Stage | Samples | ms |
|---|---|---|
| AGC, dynamic EQ, compressor | 0 (minimum-phase IIR, no buffering) | 0 |
| Look-ahead limiter | 48 / 72 / 96 | 1.0 / 1.5 / 2.0 |
| Dereverb + dialog (own STFT) | 0 when the NS STFT already runs; 256 when only these masks need the STFT | 0 or 5.33 |
| True-peak guard | 2 | 0.04 |

| Preset | 1.0.0 | 1.1.0 | Added |
|---|---|---|---|
| Off | 0 | 50 samples | **+1.0 ms** |
| Phone – light, CordMic STFT (no platform NS) | 256 | 330 | **+1.5 ms** |
| Phone – light, platform NS active | 0 | 330 | **+6.9 ms** (the masks need the STFT; platform NS has none) |
| Phone – strong | 256 | 354 | **+2.0 ms** |
| PC Studio (any) | 960 (RNNoise) + worker | 960 + 356 | **+7.4 ms** |

The limiter look-ahead stays within the 3 ms budget in every preset. The STFT figure is the true block delay of the
256-point OLA (`StudioChain.StrongDelay`). The `delayFrames` property reports one hop (2.67 ms), as in earlier
releases. `VoiceChain.chainDelayFrames` / `ChainDelayFrames` report the true total, and tests pin the values above.

## CPU cost

These are measured by the benchmark tests (`PodcastChainTest.cpuBenchmarkPerStage`,
`PodcastChainBenchmarkTests.CpuBenchmark_PerStage`) on the build box, an x86-64 cloud VM, with 5 s of chaotic
48 kHz input in 240-sample blocks. They are single-thread figures and vary from run to run by about ±30 %.

| Stage | Kotlin/JVM ns/sample | .NET ns/sample |
|---|---|---|
| AGC | 47 | 47 |
| Dynamic EQ | 65 | 84 |
| Compressor | 50 | 50 |
| Look-ahead limiter | 33 | 69 |
| AGC + EQ + compressor + limiter | 68 | 143 |
| True-peak guard | 29 | 52 |
| Full phone chain, 1.0.0 Strong | 118 | 273 |
| Full phone chain, 1.1.0 Strong (NS + dereverb + dialog) | 167 | 479 |
| 1.1.0 Off preset (no STFT) | 97 | 216 |
| 1.1.0 Light with platform NS (STFT for the masks only) | 178 | 495 |

The dereverb and dialog masks cost little next to the FFT they share. Strong with and without the masks measures
within noise (170 vs 167 ns/sample). The whole 1.1.0 Strong chain is under 1 % of one core on the box, against a
25 % test gate. A low-end phone core (Cortex-A53 class) is roughly 5–10× slower than the box. That projects to about
4–8 % of one core, which is inside the budget, but it is an estimate: no low-end phone was measured.

**Auto-fallback.** `CpuGuard` times every processed block against its real-time length. After a 1 s warm-up, if the
smoothed load stays above 30 % of real time for 1.5 s, it latches for the session and dereverb and dialog isolation
are dropped. In the Off preset, or in Light with platform NS, the STFT then stops and its 5.33 ms of delay goes away.
NS, AGC, EQ, compressor, limiter and the true-peak guard keep running. A test drives a slow clock and checks the
fallback and the latency drop.

## Allocation and robustness

- No allocation happens in the audio callback. Every buffer is sized at construction; the limiter delay and deque
  are sized for 5 ms at the stream rate. The C# `VoiceChain` grows its work buffer only if a caller passes a block
  longer than 4096 samples. The phone never does.
- Non-finite input (NaN, ±Inf) is treated as silence. Input is clamped to ±4 before processing. Biquad state below
  1e-25 is flushed, because the JVM has no flush-to-zero mode.
- Parameter changes mid-stream recompute coefficients but keep the filter and envelope state. A look-ahead change
  resets only the limiter, and the ceiling still holds.
- Tests (Kotlin and C#) run every stage with silence, full-scale square, DC, NaN/Inf, denormals, over-range noise,
  bursts and dropouts. They cover 8 / 16 / 44.1 / 48 kHz, mono and stereo, with presets switched every few blocks.
  An integration test runs the whole phone chain at +24 dB mic gain in every preset and checks that no output
  sample reaches the yellow meter mark (−9 dBFS); the limiter and true-peak guard hold −10 dBFS. A Studio test does
  the same through RNNoise.
