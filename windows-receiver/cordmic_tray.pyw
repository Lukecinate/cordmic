"""Double-click launcher (no console window): tray icon + log file cordmic.log.
Edit DEFAULT_ARGS to pick your virtual cable device."""
import os
import sys

os.chdir(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.getcwd())
from cordmic_receiver.__main__ import main  # noqa: E402

DEFAULT_ARGS = ["--device", "CABLE Input", "--jitter-ms", "10", "--tray", "--log", "cordmic.log"]
sys.exit(main(sys.argv[1:] or DEFAULT_ARGS))
