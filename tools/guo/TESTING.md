# Configuration resolution checks

Run `python tools/guo/test_config.py` with the standard library only.
The suite creates temporary launcher fixtures and isolates environment
variables, so it never reads private `config.local.bat` values or requires
a client install, engine, network connection or account.

Coverage: environment/local/default precedence, blank overrides, UTF-8 BOM
and CRLF, ignored comments/commands, paths with spaces, cross-file variable
expansion, guarded assignments, case-insensitive batch variable names,
unresolved references, root discovery, derived paths, numeric fallbacks,
account-list normalization and absence of environment side effects.
Store coverage includes checkout-relative defaults, `%UO_ROOT%` expansion,
paths containing spaces, URL precedence and absolute-path preservation.

Regression cases exposed three differences from the shared batch pattern:
local values were unavailable while defaults expanded references; guarded
assignments overwrote earlier values; lowercase names did not resolve.
The resolver now reads the local layer first, keeps the guard and normalizes
batch names. It remains a narrow assignment parser: it does not execute
batch commands, follow `call` statements or evaluate arbitrary batch code.
