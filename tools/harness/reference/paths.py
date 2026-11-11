"""Data location for the regression harness. Datasets are downloaded by ../fetch_data.sh into a git-ignored cache
(default tools/harness/.cache); they are never committed. Layout: ext/ears/pXXX, ext/demand/ENV/ch01.wav,
data/libri/*.flac, ext/sig_bak_ovr.onnx, ext/rnnoise/{rnnoise_demo,rnnoise_vad}; results go to results/."""
import os
ROOT = os.environ.get("CORDMIC_HARNESS_DATA", os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".cache"))
os.makedirs(os.path.join(ROOT, "results"), exist_ok=True)
