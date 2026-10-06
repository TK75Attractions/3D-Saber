# PhoneSaber metadata compatibility fixtures

These JSON-only fixtures were derived from existing device recordings. They do
not include video or images.

- `legacy-pre-diagnostics-session.json` is the complete 294-frame session
  `phonesaber_20260921_184852_675`; it predates prediction flags, fresh-success
  flags, candidate diagnostics, and forensic/manual capture flags.
- `unversioned-red-dropout-excerpt.json` contains zero-based source frame-array
  indices 27–29 from `phonesaber_20260923_143446_247` and includes RED dropout
  annotations and camera samples.
- `unversioned-blue-dropout-excerpt.json` contains zero-based source
  frame-array indices 305–307 from the same session and includes BLUE dropout
  annotations.

The excerpts retain the original frame objects. Their top-level session ID,
dimensions, and sampled camera fields are from the same recording. They remain
unversioned to exercise the compatibility path used by existing sessions.
