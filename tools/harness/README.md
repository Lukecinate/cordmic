# CordMic regression harness (developer tool)

Measures the 0.6.0 release gates on the shipping C# DSP code. It is not part of the Windows installer, the
portable zip or the APK.

- `cs_gates.py` runs the C# Studio chain (`CordMic.Harness studio`) and the C# Auto volume on the held-out
  noise-reduction set and the AGC rooms. It reports NR-1…8, AGC-1…6 and a C#-vs-Python port check, writing
  `results/cs_nr.json`, `results/cs_agc.json` and `results/cs_gates.md` in the data directory.
- `reference/` holds the Python reference models used during research: the 0.5.7 Strong suppressor, WhisperGuard,
  B3 LUFS volume, pitch PLC, the cubic resampler and the vector generator for `tests/CordMic.Tests/TestData`.
- `fetch_data.sh` downloads the evaluation data into `.cache/` (git-ignored) at test time.

## Data and licences

| Data | Licence | Use |
|---|---|---|
| EARS talkers | CC-BY-NC 4.0 | Local evaluation only. Never commit or redistribute clips or processed audio. |
| DEMAND noise | CC-BY 4.0 | Noise beds |
| LibriSpeech dev-clean (8 clips) | CC-BY 4.0 | PLC and AGC speech |
| DNSMOS P.835 model | MIT | OVRL / SIG / BAK scores |
| PESQ (`pesq` package) | ITU-T P.862, research use only | Scores only; not shipped |
| RNNoise | BSD-3-Clause | Reference `rnnoise_demo` / `rnnoise_vad`, built from the pinned commit |

No recording, clip or processed audio from this harness belongs in the repository.

## Run

```bash
cd tools/harness
python -m venv .venv && .venv/bin/pip install -r requirements.txt
./fetch_data.sh                              # or CORDMIC_HARNESS_DATA=/path ./fetch_data.sh
dotnet build ../../windows-app/src/CordMic.Harness -c Release -o /tmp/cmhost
PYTHONHASHSEED=0 .venv/bin/python cs_gates.py --host /tmp/cmhost/CordMic.Harness --ref
```

Options: `--quick` (2 talkers × 2 noises), `--skip-nr`, `--skip-agc`, `--ref` (also run the Python port check).
`CORDMIC_HARNESS_DATA` points the scripts at another data directory. A full run takes about 15–25 minutes on
8 cores. The C# unit tests (`dotnet test`) cover the PLC, resampler, delay, CPU and allocation gates without any
downloaded data.
