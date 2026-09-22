# PhoneSaberSender evaluation tools

These tools read saved lossless fixtures and Debug Recording metadata. They do
not alter or re-run detection for session metadata.

Run the lossless fixture suite from the repository root:

```bash
ios/PhoneSaberSender/Tools/run_lossless_regression.py
```

Save machine-readable results when needed:

```bash
ios/PhoneSaberSender/Tools/run_lossless_regression.py \
  --json /tmp/phonesaber-regression.json \
  --csv /tmp/phonesaber-regression.csv
```

Analyze one device recording:

```bash
ios/PhoneSaberSender/Tools/analyze_session_metadata.py \
  /path/to/phonesaber_session_metadata.json
```

Save JSON, or compare before/after recordings:

```bash
ios/PhoneSaberSender/Tools/analyze_session_metadata.py session.json --json summary.json
ios/PhoneSaberSender/Tools/analyze_session_metadata.py \
  --compare before_metadata.json after_metadata.json
```

The existing metadata does not identify prediction-bridge frames, so the
analyzer reports that metric as unavailable instead of inferring it.
